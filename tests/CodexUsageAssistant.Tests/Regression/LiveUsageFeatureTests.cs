using System.Text.Json;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class LiveUsageFeatureTests
{
    [Theory]
    [InlineData("—", "96%", "96")]
    [InlineData("0%", "96%", "0")]
    [InlineData("64%", "96%", "64")]
    [InlineData(null, null, "—")]
    public void TrayIcon_FallsBackToWeeklyOnlyWhenFiveHourIsUnknown(string? five, string? weekly, string expected) =>
        Assert.Equal(expected, TrayUsageIconRenderer.SelectLabel(five, weekly));

    [Fact]
    public async Task SignInFlow_OpensBrowserWaitsAndVerifiesAccount()
    {
        var calls = new List<string>(); bool opened = false;
        await AppServerSignInService.RunFlowAsync((method, parameters, token) =>
        {
            calls.Add(method);
            return Task.FromResult(method == "account/login/start"
                ? JsonSerializer.SerializeToElement(new { loginId = "test-login", authUrl = "https://auth.openai.com/test" })
                : JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", planType = "pro" } }));
        }, (id, _) => { Assert.Equal("test-login", id); return Task.FromResult(true); },
        _ => opened = true, _ => { }, CancellationToken.None);
        Assert.True(opened);
        Assert.Equal(new[] { "account/login/start", "account/read" }, calls);
    }

    [Fact]
    public async Task SignInFlow_CancellationCancelsServerLogin()
    {
        var calls = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppServerSignInService.RunFlowAsync((method, parameters, token) =>
        {
            calls.Add(method);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "test-login", authUrl = "https://auth.openai.com/test" }));
        }, (_, _) => throw new OperationCanceledException(), _ => { }, _ => { }, CancellationToken.None));
        Assert.Equal(new[] { "account/login/start", "account/login/cancel" }, calls);
    }

    [Theory]
    [InlineData("https://example.com/signin")]
    [InlineData("file:///C:/test")]
    public void SignIn_RejectsUnexpectedAuthenticationDestination(string uri) =>
        Assert.Throws<System.IO.InvalidDataException>(() => AppServerSignInService.ValidateAuthUri(uri));

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("prolite", "Pro Lite")]
    [InlineData("plus", "Plus")]
    [InlineData(null, "—")]
    public void PlanLabel_DoesNotInventMissingSubscription(string? plan, string expected) =>
        Assert.Equal(expected, FloatingBallViewModel.FormatPlan(plan));
    [Theory]
    [InlineData("64%", "64")]
    [InlineData("100%", "100")]
    [InlineData("0%", "0")]
    [InlineData("—", "—")]
    [InlineData("NaN", "—")]
    public void TrayIcon_FormatsRemainingQuotaAndReleasesNativeHandle(string input, string expected)
    {
        var label = TrayUsageIconRenderer.Label(input);
        Assert.Equal(expected, label);
        using var icon = TrayUsageIconRenderer.Create(label);
        Assert.Equal(32, icon.Width);
        Assert.Equal(32, icon.Height);
        var output = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            System.IO.Directory.CreateDirectory(output);
            using var bitmap = TrayUsageIconRenderer.Render(label);
            bitmap.Save(System.IO.Path.Combine(output, $"tray-usage-{(label == "—" ? "unknown" : label)}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    [Fact]
    public void TrayUsageSetting_RoundTrips()
    {
        Assert.False(JsonSerializer.Deserialize<WindowPosition>("{}")!.TrayShowRemainingUsage);
        var loaded = JsonSerializer.Deserialize<WindowPosition>(JsonSerializer.Serialize(new WindowPosition { TrayShowRemainingUsage = true }))!;
        Assert.True(loaded.TrayShowRemainingUsage);
    }
    [Fact]
    public void AppServerProxy_UsesSavedSettingsWithoutInheritedProxy()
    {
        var start = new System.Diagnostics.ProcessStartInfo();
        start.Environment.Clear();
        AppServerClient.ApplyProxyEnvironment(start, new WindowPosition { ProxyEnabled = true,
            ProxyServer = "http://127.0.0.1:3128", ProxyBypassList = "localhost;127.0.0.1" });
        Assert.Equal("http://127.0.0.1:3128", start.Environment["HTTPS_PROXY"]);
        Assert.Equal(start.Environment["HTTPS_PROXY"], start.Environment["https_proxy"]);
        Assert.Equal("localhost,127.0.0.1", start.Environment["NO_PROXY"]);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void AppServerProxy_DisabledPreservesInheritedEnvironment()
    {
        var start = new System.Diagnostics.ProcessStartInfo();
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:9999";
        AppServerClient.ApplyProxyEnvironment(start, new WindowPosition { ProxyEnabled = false });
        Assert.Equal("http://127.0.0.1:9999", start.Environment["HTTPS_PROXY"]);
    }

    [Fact]
    public void ResetHint_ExplainsDomFallbackInsteadOfImplyingZeroResets()
    {
        var hint = FloatingBallViewModel.GetResetAvailabilityHint(new UsageData { DataSource = "DOM (App Server fallback)", AvailableUsageResetCount = 3 }, false);
        Assert.Contains("App Server", hint);
        Assert.Contains("Proxy", hint, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task LiveNotificationConnection_WhenEnabled_CancelsCleanly()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_LIVE_USAGE") != "1") return;
        using var connection = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await AppServerClient.ConnectAsync(null, connection.Token);
        await client.RequestAsync("account/rateLimits/read", null, connection.Token);
        using var listen = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListenForUsageChangesAsync(() => { }, listen.Token));
    }
    private static string Event(string timestamp, long total, long last) => JsonSerializer.Serialize(new
    {
        timestamp, type = "event_msg", payload = new { type = "token_count", info = new
        {
            total_token_usage = new { total_tokens = total }, last_token_usage = new { total_tokens = last }
        } }
    });

    [Fact]
    public void LocalToday_UsesLocalMidnightAndCumulativeDeltas()
    {
        var now = new DateTimeOffset(2026, 9, 16, 1, 0, 0, TimeSpan.FromHours(9));
        var lines = new[] { Event("2026-09-15T14:59:00Z", 1000, 1000),
            Event("2026-09-15T15:01:00Z", 1200, 200), Event("2026-09-15T15:02:00Z", 1500, 300),
            Event("2026-09-15T15:03:00Z", 1500, 300), "{incomplete" };
        Assert.Equal(500, LocalTokenUsageService.CountToday(lines, now, [], CancellationToken.None));
    }

    [Fact]
    public void LocalToday_DeduplicatesCopiedHistoryAndDoesNotCountInheritedTotal()
    {
        var now = new DateTimeOffset(2026, 9, 16, 1, 0, 0, TimeSpan.FromHours(9));
        var lines = new[] { Event("2026-09-15T15:01:00Z", 1000000, 500) };
        var seen = new HashSet<string>();
        Assert.Equal(500, LocalTokenUsageService.CountToday(lines, now, seen, CancellationToken.None));
        Assert.Equal(0, LocalTokenUsageService.CountToday(lines, now, seen, CancellationToken.None));
        Assert.Null(LocalTokenUsageService.CountToday([], now, [], CancellationToken.None));
    }

    [Fact]
    public void LocalToday_CounterRestartUsesLastRequestOnly()
    {
        var now = DateTimeOffset.Parse("2026-09-16T12:00:00Z");
        var lines = new[] { Event("2026-09-16T01:00:00Z", 1000, 200), Event("2026-09-16T01:01:00Z", 100, 100) };
        Assert.Equal(300, LocalTokenUsageService.CountToday(lines, now, [], CancellationToken.None));
    }

    [Fact]
    public async Task OfficialToday_TakesPriorityWithoutReadingLocalFiles()
    {
        var usage = new UsageData { DailyTokenUsage = [new() { Date = DateOnly.FromDateTime(DateTime.Now), Tokens = 123 }] };
        await LocalTokenUsageService.EnrichTodayAsync(usage, CancellationToken.None);
        Assert.False(Assert.Single(usage.DailyTokenUsage).IsLocalEstimate);
        Assert.Equal(123, usage.DailyTokenUsage[0].Tokens);
        var row = Assert.Single(FloatingBallViewModel.CreateDailyTokenRows([new() { Date = DateOnly.FromDateTime(DateTime.Now), Tokens = 123, IsLocalEstimate = true }]));
        Assert.Equal("~123", row.CountLabel);
    }

    [Theory]
    [InlineData("account/rateLimits/updated", true)]
    [InlineData("account/updated", true)]
    [InlineData("thread/tokenUsage/updated", true)]
    [InlineData("item/agentMessage/delta", false)]
    public void Notifications_OnlyUsageSignalsRefresh(string method, bool expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { method }));
        Assert.Equal(expected, UsageChangeMonitor.IsUsageNotification(json.RootElement));
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { id = 1, method }));
        Assert.False(UsageChangeMonitor.IsUsageNotification(request.RootElement));
    }

    [Fact]
    public async Task Notifications_CoalesceBurstsAndPreservePendingChange()
    {
        await using var monitor = new UsageChangeMonitor();
        var now = DateTimeOffset.UtcNow;
        monitor.Signal();
        Assert.True(monitor.TakeChange(now));
        monitor.Signal(); monitor.Signal();
        Assert.False(monitor.TakeChange(now.AddSeconds(5)));
        Assert.True(monitor.TakeChange(now.AddSeconds(10)));
        Assert.False(monitor.TakeChange(now.AddSeconds(20)));
        await monitor.ConfigureAsync(new WindowPosition { RefreshOnServerNotification = true, UsageReadMode = UsageReadMode.DomOnly }, CancellationToken.None);
        Assert.False(monitor.TakeChange(now.AddSeconds(30)));
    }

    [Fact]
    public void NotificationSetting_RoundTripsAndDefaultsOff()
    {
        Assert.False(JsonSerializer.Deserialize<WindowPosition>("{}")!.RefreshOnServerNotification);
        var settings = JsonSerializer.Deserialize<WindowPosition>(JsonSerializer.Serialize(new WindowPosition { RefreshOnServerNotification = true, AutoRefreshIntervalMinutes = 0 }))!;
        Assert.True(settings.RefreshOnServerNotification);
        Assert.Null(FloatingBallViewModel.GetAutoRefreshInterval(settings));
    }
}
