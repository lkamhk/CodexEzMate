using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.ViewModels;

public partial class ResetWatchViewModel : ObservableObject, IDisposable
{
    private readonly ResetWatchService _watch;
    private readonly Dispatcher _dispatcher;
    private readonly AppLanguage? _language;
    private readonly CancellationTokenSource _closed = new();
    private readonly DispatcherTimer _clock;
    private bool _disposed;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _feedUrl = "";
    [ObservableProperty] private string _pollMinutes = "5";
    [ObservableProperty] private bool _notifyPreviews;
    [ObservableProperty] private bool _notifyAnnouncements;
    [ObservableProperty] private bool _notifyCommunity;
    [ObservableProperty] private bool _showPopup;
    [ObservableProperty] private bool _popupAutoClose;
    [ObservableProperty] private string _popupAutoCloseSeconds = "30";
    [ObservableProperty] private bool _notifyAccountChanges;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _statusTone = "neutral";
    [ObservableProperty] private string _sourceDetails = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private IReadOnlyList<ResetEventRow> _events = [];
    [ObservableProperty] private bool _busy;
    public bool CanEdit => !Busy && !_watch.IsRefreshing;
    public bool CanCheck => CanEdit && _watch.Settings.Enabled;
    public Visibility EmptyVisibility => Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TrackerAttributionVisibility => _watch.State.Feed?.FeedId == CodexResetsApiAdapter.FeedId ? Visibility.Visible : Visibility.Collapsed;

    public ResetWatchViewModel(ResetWatchService watch, AppLanguage? language = null)
    {
        _watch = watch;
        _language = language;
        _dispatcher = Dispatcher.CurrentDispatcher;
        LoadSettings();
        _watch.Changed += OnChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _clock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += OnClock;
        _clock.Start();
        UpdateDisplay();
    }

    private void LoadSettings()
    {
        var settings = _watch.Settings;
        Enabled = settings.Enabled; FeedUrl = settings.FeedUrl;
        PollMinutes = settings.PollIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        NotifyPreviews = settings.NotifyPreviews; NotifyAnnouncements = settings.NotifyAnnouncements;
        NotifyCommunity = settings.NotifyCommunity; NotifyAccountChanges = settings.NotifyAccountChanges;
        ShowPopup = settings.ShowPopup;
        PopupAutoClose = settings.PopupAutoCloseSeconds is > 0;
        PopupAutoCloseSeconds = (settings.PopupAutoCloseSeconds ?? 30).ToString(CultureInfo.InvariantCulture);
    }

    partial void OnBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanCheck)); }
    private void OnLanguageChanged(AppLanguage _) => OnChanged();
    private void OnClock(object? sender, EventArgs e) => UpdateDisplay();
    private void OnChanged()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) UpdateDisplay();
        else _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) UpdateDisplay(); }));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanEdit) return;
        if (!int.TryParse(PollMinutes, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        { Message = L("請輸入 5–60 分鐘的整數。", "Enter a whole number from 5 to 60 minutes."); return; }
        int? popupAutoCloseSeconds = null;
        if (PopupAutoClose)
        {
            if (!int.TryParse(PopupAutoCloseSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds is < 1 or > 3600)
            {
                Message = L("小視窗自動關閉時間必須是 1–3600 秒的整數。",
                    "Popup auto-close must be a whole number from 1 to 3600 seconds.");
                return;
            }
            popupAutoCloseSeconds = seconds;
        }
        Busy = true;
        try
        {
            await _watch.SaveSettingsAsync(new ResetWatchSettings { Enabled = Enabled, FeedUrl = FeedUrl,
                PollIntervalMinutes = minutes, NotifyPreviews = NotifyPreviews, NotifyAnnouncements = NotifyAnnouncements,
                NotifyCommunity = NotifyCommunity, NotifyAccountChanges = NotifyAccountChanges, ShowPopup = ShowPopup,
                PopupAutoCloseSeconds = popupAutoCloseSeconds }, _closed.Token);
            LoadSettings();
            Message = L("設定已儲存", "Settings saved");
        }
        catch (InvalidDataException) { Message = L("請檢查 HTTPS 網址、檢查間隔及小視窗自動關閉時間。",
            "Check the HTTPS URL, polling interval, and popup auto-close time."); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ResetWatchService.IsExpected(ex)) { Message = L("儲存失敗，已保留原設定。", "Save failed. Previous settings retained."); }
        finally { Busy = false; UpdateDisplay(); }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (!CanCheck) return;
        Busy = true;
        Message = "";
        try { await _watch.RefreshAsync(_closed.Token); }
        catch (OperationCanceledException) { }
        finally { Busy = false; UpdateDisplay(); }
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!ResetFeedClient.TryGetHttpsUri(url, out var uri)) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { Message = L("無法開啟瀏覽器。", "Could not open the browser."); }
    }

    private void UpdateDisplay()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        var state = _watch.State;
        var feed = state.Feed;
        OnPropertyChanged(nameof(TrackerAttributionVisibility));
        bool Healthy(ResetFeedSource source) => source.Health == ResetSourceHealth.Healthy
            && source.LastSuccessfulCheckAt is { } at && now - at <= ResetFeedClient.MaximumAge;
        var healthyCount = feed?.Sources.Count(Healthy) ?? 0;
        var sourceName = feed is null ? "" : feed.Sources.Length == 1 ? feed.Sources[0].Name
            : L($"{feed.Sources.Length} 個來源", $"{feed.Sources.Length} sources");
        StatusTone = "neutral";
        StatusDetail = sourceName;
        if (_watch.Error is { } error)
        {
            StatusTone = "error";
            Status = error switch
            { "settings" => L("設定無法讀取", "Settings unavailable"), "cache" => L("快取無法讀取", "Cache unavailable"),
                "invalid" => L("Feed 格式不正確", "Invalid feed"), _ => L("暫時無法更新", "Update unavailable") };
            StatusDetail = error == "invalid" && Uri.TryCreate(_watch.Settings.FeedUrl, UriKind.Absolute, out var address)
                && address.AbsolutePath.TrimEnd('/') == "/healthz"
                ? L("請使用公告 JSON 網址，不是 /healthz。", "Use the announcement JSON URL, not /healthz.")
                : error == "settings" ? L("請重新儲存 Feed 設定。", "Save your feed settings again.")
                : feed is not null ? L("保留上次資料，稍後重試。", "Previous data retained. Retrying later.")
                : L("請檢查 Feed 網址及連線。", "Check the feed URL and connection.");
        }
        else if (!_watch.Settings.Enabled)
        { Status = L("未啟用", "Disabled"); StatusDetail = L("在 Feed 設定中啟用", "Enable in Feed settings"); }
        else if (_watch.IsRefreshing) Status = L("正在更新…", "Updating…");
        else if (feed is null) Status = L("等待公告資料", "Waiting for feed");
        else if (!ResetNotificationCoordinator.IsFresh(feed, now))
        { Status = L("資料已過期", "Feed is stale"); StatusTone = "warning"; }
        else if (healthyCount == 0)
        { Status = L("來源暫不可用", "Source unavailable"); StatusTone = "warning"; }
        else if (healthyCount < feed.Sources.Length)
        { Status = L("部分來源未更新", "Some sources unavailable"); StatusTone = "warning"; }
        else { Status = L("來源正常", "Source healthy"); StatusTone = "healthy"; }
        SourceDetails = feed is null ? "" : string.Join("\n", feed.Sources.Select(source => source.Name + " · "
            + (Healthy(source) ? L("正常", "Healthy") : L("未更新／不可用", "Stale / unavailable"))
            + " · " + Time(source.LastSuccessfulCheckAt) + (string.IsNullOrWhiteSpace(source.Message) ? "" : "\n" + source.Message)));
        var expanded = Events.Where(x => x.DetailsExpanded).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        Events = feed?.Events.OrderByDescending(x => x.UpdatedAt).Select(item =>
        {
            var row = ResetActivityPresentation.Create(item, feed, now, _language ?? LocalizationService.CurrentLanguage);
            row.DetailsExpanded = expanded.Contains(item.Id);
            return row;
        }).ToArray() ?? [];
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    internal static string StatusLabel(ResetAnnouncementStatus status, AppLanguage? language = null)
    {
        var (chinese, english) = status switch
        { ResetAnnouncementStatus.Preview => ("預告", "Preview"), ResetAnnouncementStatus.Announced => ("發放消息", "Announced"),
            ResetAnnouncementStatus.Corrected => ("更正", "Corrected"), _ => ("已撤回", "Retracted") };
        return LocalizationService.Translate(chinese, english, language ?? LocalizationService.CurrentLanguage);
    }
    internal static string EvidenceLabel(ResetEvidence evidence, AppLanguage? language = null)
    {
        var (chinese, english) = evidence switch
        { ResetEvidence.Official => ("Feed：官方來源", "Feed: official source"),
              ResetEvidence.Tracker => ("網站彙整 · 非官方 API", "Tracker · unofficial API"),
            ResetEvidence.Corroborated => ("Feed：交叉核實", "Feed: corroborated"), _ => ("社群 · 未核實", "Community · unverified") };
        return LocalizationService.Translate(chinese, english, language ?? LocalizationService.CurrentLanguage);
    }
    private static string Time(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") ?? "—";
    private string L(string chinese, string english) => LocalizationService.Translate(chinese, english, _language ?? LocalizationService.CurrentLanguage);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closed.Cancel();
        _clock.Stop(); _clock.Tick -= OnClock;
        _watch.Changed -= OnChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
    }
}
