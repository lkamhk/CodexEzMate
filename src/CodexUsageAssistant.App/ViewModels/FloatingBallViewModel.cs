using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.Views;
using CodexUsageAssistant.Models;
using System.Globalization;
using System.IO;

namespace CodexUsageAssistant.ViewModels;

public partial class FloatingBallViewModel : ObservableObject
{
    public event Action<UsageDisplayMode>? DisplayModeChanged;
    public event Action<AppLanguage>? LanguageChanged;
    public event Action<bool>? TrayUsageIconChanged;
    public event Action? HotkeySettingsRequested;

    [RelayCommand]
    private void OpenHotkeySettings() => HotkeySettingsRequested?.Invoke();
    private GoalMonitorWindow? _goalWindow;
    private ServerHostWindow? _hostWindow;
    private UpdateWindow? _updateWindow;
    private readonly AppUpdateService? _updates;
    private readonly IUsageLoginService _usageLogin;
    private readonly ISettingsService _settings;
    private readonly IProxyTestService _proxyTester;
    private readonly IUsageCacheService _cache;
    private readonly IAutoResumeScheduler _autoResumeScheduler;
    private readonly IAutoResumeSettingsService _autoResumeSettings;
    private readonly IConversationDiscoveryService _conversationDiscovery;
    private readonly IGoalResumeService _goalResume;
    private readonly CodexAppServerHost? _host;
    private UsageData? _lastUsage;
    private readonly CancellationTokenSource _backgroundRefreshCts = new();
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _showDomLoginMenu;
    [ObservableProperty] private string _fiveHourPercent = "—";
    [ObservableProperty] private string _weeklyPercent = "—";
    [ObservableProperty] private string _primaryLimitLabel = "5h";
    [ObservableProperty] private string _primaryLimitPercent = "—";
    [ObservableProperty] private double _fiveHourValue;
    [ObservableProperty] private double _weeklyValue;
    [ObservableProperty] private string _lastUpdated = DateTime.Now.ToString("HH:mm");
    [ObservableProperty] private string _fiveHourResetDisplay = "—";
    [ObservableProperty] private string _weeklyResetDisplay = "—";
    [ObservableProperty] private string _availableUsageResetDisplay = LocalizationService.Pick("未知", "Unknown");
    [ObservableProperty] private string _usageResetExpiresDisplay = "—";
    [ObservableProperty] private string _statusMessage = LocalizationService.Pick("等待資料", "Waiting for data");
    [ObservableProperty] private string _dataSourceDisplay = "—";
    [ObservableProperty] private string _planDisplay = "—";

    internal static string FormatPlan(string? plan) => plan?.ToLowerInvariant() switch
    {
        "plus" => "Plus", "pro" => "Pro", "prolite" => "Pro Lite", "free" => "Free",
        "go" => "Go", "team" => "Team", "business" => "Business",
        "enterprise" or "ent26" or "enterprise_cbp_automation" or "enterprise_cbp_usage_based" => "Enterprise",
        "self_serve_business_prolite" or "self_serve_business_usage_based" => "Business",
        "edu" => "Edu", "edu_plus" => "Edu Plus", "edu_pro" => "Edu Pro",
        "pro_5x" => "Pro", "pro_20x" => "Pro", _ => "—"
    };
    [ObservableProperty] private string _todayTokensDisplay = "—";
    [ObservableProperty] private string _lifetimeTokensDisplay = "—";
    [ObservableProperty] private string _streakDisplay = "—";
    [ObservableProperty] private int _selectedDetailsTab;
    [ObservableProperty] private List<DailyTokenRow> _dailyTokenRows = [];
    [ObservableProperty] private bool _hasLocalTokenEstimate;
    [ObservableProperty] private bool _canUseReset;
    [ObservableProperty] private string _resetAvailabilityHint = LocalizationService.Pick("等待 App Server 資料。", "Waiting for App Server data.");

    internal static string GetResetAvailabilityHint(UsageData? usage, bool busy) => busy
        ? LocalizationService.Pick("正在使用 reset，請稍候。", "A reset is in progress.")
        : usage?.DataSource != "App Server"
            ? LocalizationService.Pick("使用 reset 需要 App Server；目前為 DOM／快取資料，請檢查資料來源及 Proxy 後重新整理。", "Using reset requires App Server. Current data is DOM/cached; check data source and proxy, then refresh.")
            : usage.AvailableUsageResetCount > 0
                ? LocalizationService.Pick("使用明細中最快到期的可用 reset。", "Use the earliest-expiring available reset in the returned details.")
                : LocalizationService.Pick("App Server 未提供可用 reset。", "App Server has not provided any available resets.");
    private bool _usingReset;

    internal async Task UseResetAsync()
    {
        if (_usingReset || !CanUseReset) return;
        _usingReset = true;
        CanUseReset = false;
        ResetAvailabilityHint = GetResetAvailabilityHint(_lastUsage, true);
        try
        {
            var settings = await _settings.LoadAsync(CancellationToken.None);
            if (settings?.UsageReadMode == UsageReadMode.DomOnly) return;
            StatusMessage = L("正在使用 reset…", "Using reset…");
            var fresh = await new AppServerUsageService().ReadAsync(settings?.CodexExecutablePath, CancellationToken.None);
            if (fresh.Status != UsageStatus.Available) throw new InvalidOperationException("Cannot read current reset credits.");
            var creditId = ResetRedemptionService.SelectEarliestCredit(fresh, DateTimeOffset.Now);
            var outcome = await new ResetRedemptionService().RedeemAsync(settings?.CodexExecutablePath, CancellationToken.None, creditId);
            if (outcome is "reset" or "alreadyRedeemed") _autoResumeScheduler?.RequestCheck();
            var resultMessage = outcome switch
            {
                "reset" => L("已使用一次 reset。", "One reset was used."),
                "alreadyRedeemed" => L("此 reset 已成功使用，未重複扣除。", "This reset already succeeded; no duplicate redemption."),
                "nothingToReset" => L("目前沒有符合重置條件的額度。", "No usage window is eligible for reset."),
                _ => L("目前沒有可用 reset。", "No reset credits are available.")
            };
            var updated = await new AppServerUsageService().ReadAsync(settings?.CodexExecutablePath, CancellationToken.None);
            if (updated.Status == UsageStatus.Available)
            {
                ApplyFreshUsage(updated);
                await _cache.SaveAsync(updated, CancellationToken.None);
            }
            else resultMessage += L(" 請重新整理以確認最新額度。", " Refresh to check the latest limits.");
            StatusMessage = resultMessage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or KeyNotFoundException)
        {
            StatusMessage = L("未能確認 reset 結果；重試會沿用原請求，請保持 Codex 帳戶及路徑不變。", "Reset result is unconfirmed; retry reuses the original request. Keep the same Codex account and path.");
        }
        finally
        {
            _usingReset = false;
            CanUseReset = _lastUsage?.DataSource == "App Server" && _lastUsage.AvailableUsageResetCount > 0;
            ResetAvailabilityHint = GetResetAvailabilityHint(_lastUsage, false);
        }
    }

    [RelayCommand]
    private void PreviousDetails() => SelectedDetailsTab = (SelectedDetailsTab + 2) % 3;

    [RelayCommand]
    private void NextDetails() => SelectedDetailsTab = (SelectedDetailsTab + 1) % 3;

    public sealed record DailyTokenRow(string DateLabel, string CountLabel, double Percent, string Tooltip);

    internal static List<DailyTokenRow> CreateDailyTokenRows(IEnumerable<DailyTokenUsage>? entries)
    {
        var recent = (entries ?? []).Where(x => x.Tokens >= 0).GroupBy(x => x.Date)
            .Select(group => group.Last()).OrderBy(x => x.Date).TakeLast(5).ToList();
        var maximum = recent.Count == 0 ? 0 : recent.Max(x => x.Tokens);
        return recent.Select(x => new DailyTokenRow(
            x.Date.ToString("MMM d", CultureInfo.InvariantCulture),
            (x.IsLocalEstimate ? "~" : "") + FormatTokenCount(x.Tokens).Replace(" tokens", ""),
            maximum == 0 ? 0 : 100.0 * x.Tokens / maximum,
            $"{x.Date:yyyy-MM-dd}: {x.Tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens" +
                (x.IsLocalEstimate ? LocalizationService.Pick("（本機統計，僅包含此電腦記錄）", " (local estimate; this computer only)") : ""))).ToList();
    }
    [ObservableProperty] private string _creditBalanceDisplay = "—";
    [ObservableProperty] private string _creditAvailableDisplay = "—";
    [ObservableProperty] private string _creditUnlimitedDisplay = "—";
    private bool _detailsSelectionLoaded;
    private readonly SemaphoreSlim _detailsSelectionLock = new(1, 1);

    partial void OnSelectedDetailsTabChanged(int value)
    {
        if (_detailsSelectionLoaded) _ = SaveDetailsSelectionAsync();
    }

    private async Task SaveDetailsSelectionAsync()
    {
        await _detailsSelectionLock.WaitAsync();
        try
        {
            var settings = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            settings.DetailsContentTab = SelectedDetailsTab is >= 0 and <= 2 ? SelectedDetailsTab : 0;
            await _settings.SaveAsync(settings, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = L("無法保存詳細分頁選擇。", "Could not save the details tab selection.");
        }
        finally { _detailsSelectionLock.Release(); }
    }

    public FloatingBallViewModel(IUsageLoginService usageLogin,
        ISettingsService settings, IProxyTestService proxyTester, IUsageCacheService cache,
        IAutoResumeScheduler autoResumeScheduler, IAutoResumeSettingsService autoResumeSettings,
        IConversationDiscoveryService conversationDiscovery, IGoalResumeService goalResume, CodexAppServerHost? host = null, AppUpdateService? updates = null)
    {
        _usageLogin = usageLogin;
        _settings = settings;
        _proxyTester = proxyTester;
        _cache = cache;
        _autoResumeScheduler = autoResumeScheduler;
        _autoResumeSettings = autoResumeSettings;
        _conversationDiscovery = conversationDiscovery;
        _goalResume = goalResume;
        _host = host;
        _updates = updates;
    }

    public async Task InitializeAsync()
    {
        var displaySettings = await _settings.LoadAsync(CancellationToken.None);
        ShowDomLoginMenu = displaySettings?.UsageReadMode == UsageReadMode.DomOnly;
        SelectedDetailsTab = displaySettings?.DetailsContentTab is >= 0 and <= 2 ? displaySettings.DetailsContentTab : 0;
        _detailsSelectionLoaded = true;
        var cached = await _cache.LoadAsync(CancellationToken.None);
        if (cached?.Status == UsageStatus.Available)
        {
            ApplyUsage(cached);
            StatusMessage = $"{L("快取資料", "Cached data")}：5h {FiveHourPercent} · W {WeeklyPercent}";
        }
        _autoResumeScheduler.UsageUpdated += usage => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => ApplyUsage(usage)));
        _autoResumeScheduler.StatusChanged += (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => StatusMessage = _autoResumeScheduler.Status));
        await _autoResumeScheduler.InitializeAsync(CancellationToken.None);
        _ = RunBackgroundRefreshAsync(_backgroundRefreshCts.Token);
    }

    public void StopBackgroundRefresh() => _backgroundRefreshCts.Cancel();

    public void SetLanguage(AppLanguage language)
    {
        if (_lastUsage is not null) ApplyUsage(_lastUsage);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) =>
        RefreshInBackgroundAsync(cancellationToken, L("正在重新整理用量…", "Refreshing usage…"));

    [RelayCommand]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        try { await ReadAndApplyUsageAsync(cancellationToken, L("等待登入／讀取…", "Waiting for sign-in / usage…")); }
        catch (OperationCanceledException) { StatusMessage = L("登入已取消。", "Sign-in cancelled."); }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            StatusMessage = L("登入失敗，請檢查 Codex 路徑及 Proxy 設定。", "Sign-in failed; check the Codex path and proxy settings.");
            System.Windows.MessageBox.Show(StatusMessage, L("登入 Codex", "Sign in to Codex"));
        }
    }

    private async Task ReadAndApplyUsageAsync(CancellationToken cancellationToken, string loadingMessage)
    {
        StatusMessage = loadingMessage;
        var usage = await _usageLogin.ShowLoginAndReadUsageAsync(cancellationToken);
        if (usage is null) { StatusMessage = L("未更新：登入視窗已關閉", "Not updated: sign-in window was closed"); return; }
        if (usage.Status != UsageStatus.Available)
        {
            StatusMessage = $"{L("未更新", "Not updated")}：{usage.ErrorMessage ?? L("讀取失敗", "Read failed")}";
            System.Windows.MessageBox.Show(StatusMessage, L("登入／取得額度", "Sign in / Get usage"));
            return;
        }
        ApplyFreshUsage(usage);
        if (!AutoResumeScheduler.HasDisplayableUsage(usage))
        {
            StatusMessage = L("未更新：找不到可用額度資料", "Not updated: no available usage data found");
            return;
        }
        await _cache.SaveAsync(usage, cancellationToken);
        StatusMessage = FormatUpdateStatus(L("已讀取", "Read"), usage);
    }

    private async Task RefreshInBackgroundAsync(CancellationToken cancellationToken, string loadingMessage)
    {
        StatusMessage = loadingMessage;
        var usage = await _usageLogin.RefreshUsageAsync(cancellationToken);
        if (usage?.Status != UsageStatus.Available)
        {
            StatusMessage = usage?.Status == UsageStatus.LoginRequired
                ? L("登入已失效，請按「登入／取得額度」重新登入", "Sign-in expired. Select Sign in / Get usage")
                : $"{L("未更新", "Not updated")}：{usage?.ErrorMessage ?? L("背景讀取失敗", "Background read failed")}";
            return;
        }
        ApplyFreshUsage(usage);
        if (!AutoResumeScheduler.HasDisplayableUsage(usage))
        {
            StatusMessage = L("未更新：找不到可用額度資料", "Not updated: no available usage data found");
            return;
        }
        await _cache.SaveAsync(usage, cancellationToken);
        StatusMessage = FormatUpdateStatus(L("已更新", "Updated"), usage);
    }

    private async Task RunBackgroundRefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        await using var notifications = new UsageChangeMonitor();
        var observedDay = DateOnly.FromDateTime(DateTime.Now);
        var firstCheck = true;
        TimeSpan? observedInterval = null;
        DateTimeOffset? nextRefreshAt = null;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var settings = await _settings.LoadAsync(cancellationToken) ?? new WindowPosition();
                ShowDomLoginMenu = settings.UsageReadMode == UsageReadMode.DomOnly;
                await notifications.ConfigureAsync(settings, cancellationToken);
                var startupRefresh = firstCheck && settings.UsageReadMode != UsageReadMode.DomOnly;
                firstCheck = false;
                var notified = notifications.TakeChange(DateTimeOffset.Now);
                if (notified) _autoResumeScheduler.RequestCheck();
                var day = DateOnly.FromDateTime(DateTime.Now);
                var dayChanged = day != observedDay;
                observedDay = day;
                var interval = GetAutoRefreshInterval(settings);
                if (interval != observedInterval)
                {
                    observedInterval = interval;
                    nextRefreshAt = interval is { } changed ? DateTimeOffset.Now.Add(changed) : null;
                }
                var timed = interval is not null && nextRefreshAt is not null && DateTimeOffset.Now >= nextRefreshAt;
                if (!timed && !notified && !startupRefresh && !(dayChanged && settings.RefreshOnServerNotification)) continue;

                var autoSettings = await _autoResumeSettings.LoadAsync(cancellationToken);
                if (!autoSettings.MonitorEnabled || notified || dayChanged || startupRefresh)
                {
                    try { await RefreshInBackgroundAsync(cancellationToken, L("背景更新中…", "Updating in background…")); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { StatusMessage = L("背景更新失敗，會在下次事件或定時更新重試。", "Background refresh failed; retrying on the next event or timed update."); }
                }
                nextRefreshAt = interval is { } value ? DateTimeOffset.Now.Add(value) : null;
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static TimeSpan? GetAutoRefreshInterval(WindowPosition settings) =>
        settings.AutoRefreshIntervalMinutes is int minutes && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : null;

    internal void ApplyFreshUsage(UsageData usage)
    {
        var recovered = GoalProtocol.UsageAllowsResume(usage) && (_lastUsage is null || !GoalProtocol.UsageAllowsResume(_lastUsage));
        ApplyUsage(usage);
        // Wake the monitor, which independently rechecks its own App Server account.
        if (recovered) _autoResumeScheduler?.RequestCheck();
    }

    private void ApplyUsage(UsageData usage)
    {
        PlanDisplay = FormatPlan(usage.PlanType);
        CanUseReset = !_usingReset && usage.DataSource == "App Server" && usage.AvailableUsageResetCount > 0;
        ResetAvailabilityHint = GetResetAvailabilityHint(usage, _usingReset);
        DailyTokenRows = CreateDailyTokenRows(usage.DailyTokenUsage);
        HasLocalTokenEstimate = usage.DailyTokenUsage.OrderBy(x => x.Date).TakeLast(5).Any(x => x.IsLocalEstimate);
        TodayTokensDisplay = FormatTokenCount(usage.TodayTokensDate == DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ? usage.TodayTokens : null);
        LifetimeTokensDisplay = FormatTokenCount(usage.LifetimeTokens);
        CreditBalanceDisplay = usage.UnlimitedCredits == true ? L("無限額", "Unlimited") : usage.CreditBalance ?? "—";
        CreditAvailableDisplay = usage.HasCredits is bool available ? (available ? L("有", "Yes") : L("沒有", "No")) : "—";
        CreditUnlimitedDisplay = usage.UnlimitedCredits is bool unlimited ? (unlimited ? L("是", "Yes") : L("否", "No")) : "—";
        StreakDisplay = usage.CurrentStreakDays is long days ? L($"{days} 天", $"{days} {(days == 1 ? "day" : "days")}") : "—";
        DataSourceDisplay = usage.DataSource ?? "DOM";
        _lastUsage = usage;
        if (usage.FiveHourRemainingPercent is double five) { FiveHourValue = five; FiveHourPercent = $"{five:0.#}%"; }
        else { FiveHourValue = 0; FiveHourPercent = "—"; }
        if (usage.WeeklyRemainingPercent is double weekly) { WeeklyValue = weekly; WeeklyPercent = $"{weekly:0.#}%"; }
        else { WeeklyValue = 0; WeeklyPercent = "—"; }
        if (usage.FiveHourRemainingPercent.HasValue)
        {
            PrimaryLimitLabel = "5h";
            PrimaryLimitPercent = FiveHourPercent;
        }
        else if (usage.WeeklyRemainingPercent.HasValue)
        {
            PrimaryLimitLabel = "W";
            PrimaryLimitPercent = WeeklyPercent;
        }
        else
        {
            PrimaryLimitLabel = "—";
            PrimaryLimitPercent = "—";
        }
        FiveHourResetDisplay = FormatReset(usage.FiveHourResetText, usage.FiveHourResetAt);
        WeeklyResetDisplay = FormatReset(usage.WeeklyResetText, usage.WeeklyResetAt);
        AvailableUsageResetDisplay = usage.AvailableUsageResetCount is int count
            ? LocalizationService.Pick($"{count} 次", $"{count} reset{(count == 1 ? string.Empty : "s")}")
            : LocalizationService.Pick("未提供", "Not provided");
        UsageResetExpiresDisplay = FormatUsageResetEntries(usage);
        LastUpdated = usage.LastUpdatedAt.ToString("HH:mm");
    }

    private static string FormatReset(string? raw, DateTimeOffset? parsed) =>
        parsed is { } value ? value.ToLocalTime().ToString("MM-dd HH:mm") : string.IsNullOrWhiteSpace(raw) ? "—" : raw;

    internal static string FormatTokenCount(long? count)
    {
        if (count is null or < 0) return "—";
        decimal value = count.Value;
        string[] units = ["", "k", "M", "B", "T", "Q"];
        var unit = 0;
        while (unit < units.Length - 1 && Math.Round(value, 1) >= 1000)
        {
            value /= 1000;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)}{units[unit]} tokens";
    }

    private static string FormatExpiration(string? raw, DateTimeOffset? parsed, DateTimeOffset reference)
    {
        // Reparse the source text so older cached parsing mistakes do not survive an upgrade.
        if (!string.IsNullOrWhiteSpace(raw)) parsed = UsageResetTimeParser.ParseExpiration(raw, reference);
        return parsed is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd") : string.IsNullOrWhiteSpace(raw) ? "—" : raw;
    }

    internal static string FormatUsageResetEntries(UsageData usage)
    {
        var entries = usage.UsageLimitResets ?? [];
        if (entries.Count == 0 && (!string.IsNullOrWhiteSpace(usage.UsageResetExpiresText) || usage.UsageResetExpiresAt.HasValue))
            entries = [new UsageLimitReset
            {
                Type = usage.UsageResetType,
                ExpiresText = usage.UsageResetExpiresText,
                ExpiresAt = usage.UsageResetExpiresAt
            }];
        if (entries.Count == 0) return "—";
        var reference = usage.LastUpdatedAt == default ? DateTimeOffset.Now : usage.LastUpdatedAt;
        return string.Join(Environment.NewLine, entries.Select(entry =>
            $"{(string.IsNullOrWhiteSpace(entry.Type) ? "Reset" : entry.Type)}: {FormatExpiration(entry.ExpiresText, entry.ExpiresAt, reference)}"));
    }

    private string FormatUpdateStatus(string prefix, UsageData usage) =>
        usage.WeeklyRemainingPercent.HasValue && !usage.FiveHourRemainingPercent.HasValue
            ? $"{prefix}：W {WeeklyPercent} {L("（5h 未提供；不執行 Resume）", "(5h unavailable; Resume disabled)")}"
            : $"{prefix}：5h {FiveHourPercent} · W {WeeklyPercent}";

    [RelayCommand]
    private async Task RunAutomationAsync(CancellationToken cancellationToken)
    {
        StatusMessage = L("正在執行 Goal 白名單檢查…", "Checking the Goal allowlist…");
        var result = await _autoResumeScheduler.RunNowAsync(cancellationToken);
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private Task OpenSettingsAsync() => ShowSettingsAsync();

    [RelayCommand]
    private void CheckUpdates() => ShowUpdateWindow();

    public void ShowUpdateWindow(CodexEzMate.UpdateCore.UpdateManifest? available = null)
    {
        if (_updates is null) return;
        if (_updateWindow is null)
        {
            _updateWindow = new UpdateWindow(_updates, available);
            _updateWindow.Closed += (_, _) => _updateWindow = null;
        }
        ShowIndependentWindow(_updateWindow);
    }

    [RelayCommand]
    private void OpenGoalMonitor()
    {
        if (_goalWindow is null)
        {
            _goalWindow = new GoalMonitorWindow(_autoResumeSettings, _conversationDiscovery, _autoResumeScheduler, _goalResume);
            _goalWindow.Closed += (_, _) => _goalWindow = null;
        }
        ShowIndependentWindow(_goalWindow);
    }

    [RelayCommand]
    private void OpenServerHost()
    {
        if (_hostWindow is null)
        {
            _hostWindow = new ServerHostWindow(_settings, _host);
            _hostWindow.Closed += (_, _) => _hostWindow = null;
        }
        ShowIndependentWindow(_hostWindow);
    }

    private static void ShowIndependentWindow(System.Windows.Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = System.Windows.WindowState.Normal;
        window.Activate();
    }

    private async Task ShowSettingsAsync()
    {
        var existing = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null) { ShowIndependentWindow(existing); return; }
        var owner = System.Windows.Application.Current.Windows.OfType<FloatingBallWindow>().FirstOrDefault();
        var window = new SettingsWindow(_settings, _proxyTester, _host) { Owner = owner };
        if (window.ShowDialog() != true) return;
        var settings = await _settings.LoadAsync(CancellationToken.None);
        if (settings is null) return;
        ShowDomLoginMenu = settings.UsageReadMode == UsageReadMode.DomOnly;
        DisplayModeChanged?.Invoke(settings.DisplayMode);
        LanguageChanged?.Invoke(settings.Language);
        TrayUsageIconChanged?.Invoke(settings.TrayShowRemainingUsage);
    }

    [RelayCommand]
    private static Task ExitAsync() => ((App)System.Windows.Application.Current).RequestShutdownAsync();

    private static string L(string traditionalChinese, string english) =>
        LocalizationService.Pick(traditionalChinese, english);
}
