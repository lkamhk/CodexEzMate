using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Microsoft.Web.WebView2.Core;

namespace CodexUsageAssistant.Views;

public partial class LoginWindow : Window
{
    private const string UsageUrl = "https://chatgpt.com/codex/cloud/settings/analytics";
    private TaskCompletionSource<UsageData?>? _completion;
    private readonly string? _proxyServer;
    private readonly string? _proxyBypassList;
    private readonly string? _proxyUsername;
    private readonly string? _proxyPassword;
    private bool _autoRead;
    private int _autoReadStarted;
    private bool _closed;
    private bool _reading;

    public LoginWindow(string? proxyServer = null, string? proxyBypassList = null,
        string? proxyUsername = null, string? proxyPassword = null)
    {
        _proxyServer = proxyServer;
        _proxyBypassList = proxyBypassList;
        _proxyUsername = proxyUsername;
        _proxyPassword = proxyPassword;
        InitializeComponent();
    }

    public async Task<UsageData?> ShowAndReadAsync(CancellationToken cancellationToken, bool autoRead)
    {
        _autoRead = autoRead;
        if (_autoRead)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Width = 1100;
            Height = 850;
            Left = -32000;
            Top = -32000;
            Opacity = 0;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
        }
        _completion = new TaskCompletionSource<UsageData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => Dispatcher.Invoke(Close));
        Loaded += OnLoaded;
        Closed += (_, _) => { _closed = true; _completion.TrySetResult(null); };
        Show();
        return await _completion.Task;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageAssistant", "WebView2");
            var options = CreateEnvironmentOptions(_proxyServer, _proxyBypassList);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile, options: options);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Browser.CoreWebView2.BasicAuthenticationRequested += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(_proxyUsername) || string.IsNullOrEmpty(_proxyPassword) ||
                    !IsProxyAuthenticationUri(args.Uri, _proxyServer)) return;
                args.Response.UserName = _proxyUsername;
                args.Response.Password = _proxyPassword;
            };
            Browser.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                StatusText.Text = args.IsSuccess
                    ? L("頁面已載入；確認看到 Weekly usage limit 後，按『讀取當前頁用量』。",
                        "Page loaded. When Weekly usage limit is visible, select Read current page usage.")
                    : L("頁面載入失敗。", "Page failed to load.");
                if (_autoRead && args.IsSuccess && Browser.Source?.AbsolutePath.Contains("analytics", StringComparison.OrdinalIgnoreCase) == true &&
                    Interlocked.Exchange(ref _autoReadStarted, 1) == 0)
                    await ReadUsageWithRetriesAsync();
            };
            NavigateToUsagePage();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{L("WebView2 初始化失敗", "WebView2 initialization failed")}：{ex.Message}";
        }
    }

    private void OnGoClick(object sender, RoutedEventArgs e) => NavigateFromAddressBar();

    private void OnAddressKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        NavigateFromAddressBar();
        e.Handled = true;
    }

    private void NavigateToUsagePage()
    {
        AddressTextBox.Text = UsageUrl;
        Browser.Source = new Uri(UsageUrl);
    }

    private void NavigateFromAddressBar()
    {
        var candidate = AddressTextBox.Text.Trim();
        if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) candidate = $"https://{candidate}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsAllowedOpenAiUri(uri))
        {
            StatusText.Text = L("只可前往 chatgpt.com 或 openai.com 的 HTTPS 頁面。",
                "Only HTTPS pages on chatgpt.com or openai.com are allowed.");
            return;
        }
        AddressTextBox.Text = uri.AbsoluteUri;
        Browser.Source = uri;
    }

    internal static CoreWebView2EnvironmentOptions CreateEnvironmentOptions(string? proxyServer, string? bypassList)
    {
        var arguments = ProxyConfiguration.BuildBrowserArguments(proxyServer, bypassList);
        return new CoreWebView2EnvironmentOptions(arguments);
    }

    internal static bool IsProxyAuthenticationUri(string? requestUri, string? proxyServer)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var request) ||
            !Uri.TryCreate(proxyServer, UriKind.Absolute, out var proxy)) return false;
        return request.Host.Equals(proxy.Host, StringComparison.OrdinalIgnoreCase) && request.Port == proxy.Port;
    }

    private async void OnReadUsageClick(object sender, RoutedEventArgs e) => await ReadUsageWithRetriesAsync();

    private async Task ReadUsageWithRetriesAsync()
    {
        if (_reading || _closed) return;
        _reading = true;
        try
        {
        UsageData? lastResult = null;
        UsageData? availableResult = null;
        string? previousSnapshot = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (_closed) return;
            lastResult = await ReadUsageOnceAsync();
            if (lastResult?.Status != UsageStatus.Available) continue;
            availableResult = lastResult;
            var snapshot = JsonSerializer.Serialize(new { lastResult.AvailableUsageResetCount, lastResult.UsageLimitResets });
            if (HasCompleteResetInfo(lastResult) && snapshot == previousSnapshot)
            {
                _completion?.TrySetResult(lastResult);
                Close();
                return;
            }
            previousSnapshot = snapshot;
        }
        if (!_autoRead && availableResult is null) return;
        _completion?.TrySetResult(availableResult ?? lastResult ?? new UsageData { Status = UsageStatus.NetworkError,
            ErrorMessage = L("背景更新逾時。", "Background update timed out.") });
        Close();
        }
        finally { _reading = false; }
    }

    internal static bool HasCompleteResetInfo(UsageData usage) =>
        usage.AvailableUsageResetCount is int count && count >= 0 &&
        usage.UsageLimitResets.Count == count &&
        usage.UsageLimitResets.All(entry => !string.IsNullOrWhiteSpace(entry.ExpiresText));

    private async Task<UsageData?> ReadUsageOnceAsync()
    {
        if (Browser.CoreWebView2 is null) return null;
        const string script = """
            (() => {
              const text = document.body?.innerText || '';
              return JSON.stringify({ text, url: location.href, ready: document.readyState });
            })();
            """;
        try
        {
            var encoded = await Browser.CoreWebView2.ExecuteScriptAsync(script);
            var json = DecodeScriptResult(encoded);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlProperty) ? urlProperty.GetString() ?? string.Empty : string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedOpenAiUri(uri))
            {
                StatusText.Text = string.IsNullOrWhiteSpace(url)
                    ? L("頁面讀取結果不完整，請等待頁面載入後重試。",
                        "The page result is incomplete. Wait for it to load and try again.")
                    : L("只會從 ChatGPT/OpenAI 頁面讀取用量。",
                        "Usage is read only from ChatGPT/OpenAI pages.");
                return new UsageData { Status = UsageStatus.NetworkError, ErrorMessage = StatusText.Text };
            }
            var text = root.TryGetProperty("text", out var textProperty) ? textProperty.GetString() ?? string.Empty : string.Empty;
            var usage = ParseVisibleText(text);
            if (usage.Status != UsageStatus.Available)
            {
                var ready = root.TryGetProperty("ready", out var readyProperty)
                    ? readyProperty.GetString() ?? L("未知", "Unknown") : L("未知", "Unknown");
                StatusText.Text = $"{usage.ErrorMessage} {L("頁面狀態", "Page state")}：{ready}。";
                return usage;
            }
            var fiveDisplay = usage.FiveHourRemainingPercent is double five ? $"{five:0.#}%" : "—";
            var weeklyDisplay = usage.WeeklyRemainingPercent is double weekly ? $"{weekly:0.#}%" : "—";
            StatusText.Text = L($"已讀取：5 小時 {fiveDisplay}；每週 {weeklyDisplay}。正在更新顯示…",
                $"Read: 5-hour {fiveDisplay}; weekly {weeklyDisplay}. Updating display…");
            return usage;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{L("解析失敗", "Parse failed")}：{ex.Message}";
            return new UsageData { Status = UsageStatus.ParseFailed, ErrorMessage = StatusText.Text };
        }
    }

    internal static string DecodeScriptResult(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return "{}";
        using var result = JsonDocument.Parse(encoded);
        return result.RootElement.ValueKind == JsonValueKind.String
            ? result.RootElement.GetString() ?? "{}"
            : result.RootElement.GetRawText();
    }

    private static bool IsAllowedOpenAiUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        (uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("openai.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase));

    public static UsageData ParseVisibleText(string text)
    {
        var five = FindUsagePercent(text, @"(?:5\s*(?:hour|hours)|5\s*(?:小時|小时))");
        var week = FindUsagePercent(text, @"(?:weekly|week(?:ly)?|每週|每周)");
        if (!five.Success && !week.Success)
            return new UsageData { Status = text.Contains("Log in", StringComparison.OrdinalIgnoreCase) ? UsageStatus.LoginRequired : UsageStatus.ParseFailed,
                ErrorMessage = L("找不到可用的額度資料。請等待 Analytics 頁完整載入，再重試。",
                    "No usage data was found. Wait for the Analytics page to finish loading, then try again.") };
        var fiveResetText = FindResetText(text, five);
        var weeklyResetText = FindResetText(text, week);
        var now = DateTimeOffset.Now;
        var usage = new UsageData
        {
            FiveHourRemainingPercent = ParsePercent(five),
            WeeklyRemainingPercent = ParsePercent(week),
            FiveHourResetText = fiveResetText,
            WeeklyResetText = weeklyResetText,
            FiveHourResetAt = UsageResetTimeParser.Parse(fiveResetText, now),
            WeeklyResetAt = UsageResetTimeParser.Parse(weeklyResetText, now),
            LastUpdatedAt = now, Status = UsageStatus.Available
        };
        ApplyUsageLimitResetInfo(usage, text, now);
        return usage;
    }

    private static Match FindUsagePercent(string text, string label) => Regex.Match(text,
        $@"{label}[\s\S]{{0,180}}?(?<percent>\d+(?:[\.,]\d+)?)\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static double? ParsePercent(Match match) => match.Success
        ? double.Parse(match.Groups["percent"].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)
        : null;

    private static string? FindResetText(string text, Match usageMatch)
    {
        if (!usageMatch.Success) return null;
        var segment = text.Substring(usageMatch.Index, Math.Min(220, text.Length - usageMatch.Index));
        var reset = Regex.Match(segment, @"(?:resets?|重置)\s*[:：]?\s*(?<value>[^\r\n]+)", RegexOptions.IgnoreCase);
        return reset.Success ? reset.Groups["value"].Value.Trim() : null;
    }

    internal static (int? Count, string? Type, string? ExpiresText, DateTimeOffset? ExpiresAt,
        IReadOnlyList<UsageLimitReset> Entries)
        FindUsageLimitResetInfo(string text, DateTimeOffset now)
    {
        var heading = Regex.Match(text,
            @"(?:usage\s+(?:limit\s+)?resets?|limit\s+resets?|(?:用量|使用量|額度)(?:限制)?(?:重設|重置))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!heading.Success) return (null, null, null, null, []);

        var section = text[heading.Index..];
        if (Regex.IsMatch(section,
                @"\b(?:no|0)\s+(?:(?:usage\s+limit\s+)?resets?)(?:\s+(?:available|remaining|left))?|沒有可用.*(?:重設|重置)|無可用.*(?:重設|重置)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return (0, null, null, null, []);

        var explicitCount = Regex.Match(section,
            @"(?:(?<count>\d+)\s*(?:usage\s+limit\s+)?resets?\s*(?:available|remaining|left)?|(?:available|remaining)\s*(?<count>\d+)\s*resets?|(?:可用|剩餘)\s*[:：]?\s*(?<count>\d+)\s*(?:次|個)?|(?<count>\d+)\s*(?:次|個)\s*(?:可用)?(?:重設|重置))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var expirations = Regex.Matches(section,
            @"(?:expires?(?:\s+on)?|valid\s+until|available\s+until|有效期(?:至)?|到期(?:日)?)\s*[:：]?\s*(?<expires>[^\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var useResetCount = Regex.Matches(section, @"(?:use\s+(?:this\s+)?reset|使用(?:此)?(?:重設|重置))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;

        // Parse each expiration independently; identical cards are separate reset entitlements.
        var entries = new List<UsageLimitReset>();
        var previousEnd = 0;
        foreach (Match expiration in expirations)
        {
            var precedingText = section[previousEnd..expiration.Index];
            var types = Regex.Matches(precedingText,
                @"(?:full(?:\s+limit)?|5\s*[- ]?\s*hour(?:\s+limit)?|weekly(?:\s+limit)?|both\s+limits?)\s+reset|(?:完整|完全|全部|每週|每周|5\s*小時)(?:額度|用量)?(?:重設|重置)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var expiresText = expiration.Groups["expires"].Value.Trim();
            entries.Add(new UsageLimitReset
            {
                Type = types.Count > 0 ? types[^1].Value.Trim() : "Reset",
                ExpiresText = expiresText,
                ExpiresAt = UsageResetTimeParser.ParseExpiration(expiresText, now)
            });
            previousEnd = expiration.Index + expiration.Length;
        }

        int? count = explicitCount.Success
            ? int.Parse(explicitCount.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : entries.Count > 0 ? entries.Count
            : useResetCount > 0 ? useResetCount
            : null;
        var first = entries.FirstOrDefault();
        return (count, first?.Type, first?.ExpiresText, first?.ExpiresAt, entries);
    }

    private static void ApplyUsageLimitResetInfo(UsageData usage, string text, DateTimeOffset now)
    {
        var resetInfo = FindUsageLimitResetInfo(text, now);
        if (resetInfo.Count is null && resetInfo.Type is null && resetInfo.ExpiresText is null) return;
        usage.AvailableUsageResetCount = resetInfo.Count;
        usage.UsageResetType = resetInfo.Type;
        usage.UsageResetExpiresText = resetInfo.ExpiresText;
        usage.UsageResetExpiresAt = resetInfo.ExpiresAt;
        usage.UsageLimitResets = resetInfo.Entries.ToList();
    }

    private static string L(string traditionalChinese, string english) =>
        LocalizationService.Pick(traditionalChinese, english);
}
