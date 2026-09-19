using System.IO;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using CodexUsageAssistant.Views;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CodexUsageAssistantTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoad_RoundTripsPosition()
    {
        var service = new JsonSettingsService(Path.Combine(_directory, "settings.json"));
        var expected = new WindowPosition
        {
            MonitorDeviceName = "DISPLAY2", Left = -200, Top = 80, DpiScale = 1.5,
            UseSavedResumeCoordinates = true, ResumeClickX = -420, ResumeClickY = 862,
            ProxyUsername = "proxy-user", EncryptedProxyPassword = "encrypted-value",
            AutoRefreshIntervalMinutes = 12,
            DisplayMode = UsageDisplayMode.SystemTray,
            Language = AppLanguage.English
        };
        await service.SaveAsync(expected, CancellationToken.None);
        var actual = await service.LoadAsync(CancellationToken.None);
        Assert.NotNull(actual);
        Assert.Equal(expected.MonitorDeviceName, actual.MonitorDeviceName);
        Assert.Equal(expected.Left, actual.Left);
        Assert.Equal(expected.Top, actual.Top);
        Assert.Equal(expected.DpiScale, actual.DpiScale);
        Assert.True(actual.UseSavedResumeCoordinates);
        Assert.Equal(expected.ResumeClickX, actual.ResumeClickX);
        Assert.Equal(expected.ResumeClickY, actual.ResumeClickY);
        Assert.Equal(expected.ProxyUsername, actual.ProxyUsername);
        Assert.Equal(expected.EncryptedProxyPassword, actual.EncryptedProxyPassword);
        Assert.Equal(expected.AutoRefreshIntervalMinutes, actual.AutoRefreshIntervalMinutes);
        Assert.Equal(expected.DisplayMode, actual.DisplayMode);
        Assert.Equal(expected.Language, actual.Language);
    }

    [Theory]
    [InlineData(-1920, 0, true)]
    [InlineData(-1, 1079, true)]
    [InlineData(0, 500, false)]
    [InlineData(-1921, 500, false)]
    public void ScreenCoordinateValidator_HandlesNegativeMonitorCoordinates(int x, int y, bool expected)
    {
        var bounds = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
        Assert.Equal(expected, ScreenCoordinateValidator.IsInside(x, y, bounds));
    }

    [Theory]
    [InlineData("ChatGPT", true)]
    [InlineData("chatgpt", true)]
    [InlineData("Codex", true)]
    [InlineData("CodexUsageAssistant", false)]
    [InlineData("chrome", false)]
    [InlineData("codex-command-runner-0.144.0-alpha.4", false)]
    public void IsCodexDesktopProcessName_RejectsHelpersAndOwnApp(string processName, bool expected)
    {
        Assert.Equal(expected, FlaUiAutomationService.IsCodexDesktopProcessName(processName));
    }

    [Theory]
    [InlineData("Resume goal", null, true)]
    [InlineData("", "resume-goal", true)]
    [InlineData("Continue", null, true)]
    [InlineData("Delete goal", "delete-goal", false)]
    [InlineData("Edit goal", null, false)]
    public void IsResumeButtonIdentity_OnlyAcceptsExplicitResumeLabels(string? name, string? automationId, bool expected)
    {
        Assert.Equal(expected, FlaUiAutomationService.IsResumeButtonIdentity(name, automationId));
    }

    [Fact]
    public async Task Load_InvalidJson_ReturnsNull()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ invalid", System.Text.Encoding.UTF8);
        var service = new JsonSettingsService(path);
        Assert.Null(await service.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void ParseVisibleText_ReferenceFormat_ReturnsBothLimits()
    {
        const string text = "5 hour usage limit\n91% remaining\nResets 6:19 AM\nWeekly usage limit\n66% remaining\nResets Jul 18, 2026 3:00 PM";
        var usage = LoginWindow.ParseVisibleText(text);
        Assert.Equal(UsageStatus.Available, usage.Status);
        Assert.Equal(91, usage.FiveHourRemainingPercent);
        Assert.Equal(66, usage.WeeklyRemainingPercent);
        Assert.Equal("6:19 AM", usage.FiveHourResetText);
        Assert.Equal("Jul 18, 2026 3:00 PM", usage.WeeklyResetText);
        Assert.NotNull(usage.FiveHourResetAt);
        Assert.NotNull(usage.WeeklyResetAt);
    }

    [Fact]
    public void ParseVisibleText_WeeklyOnly_IsAvailableWithoutFiveHourLimit()
    {
        const string text = "Weekly usage limit\n74% remaining\nResets Jul 20, 2026 11:34 AM";
        var usage = LoginWindow.ParseVisibleText(text);
        Assert.Equal(UsageStatus.Available, usage.Status);
        Assert.Null(usage.FiveHourRemainingPercent);
        Assert.Equal(74, usage.WeeklyRemainingPercent);
        Assert.Equal("Jul 20, 2026 11:34 AM", usage.WeeklyResetText);
    }

    [Fact]
    public void ParseVisibleText_UsageLimitReset_ReturnsCountTypeAndExpiration()
    {
        const string text = "5 hour usage limit\n91% remaining\nResets 6:19 AM\nWeekly usage limit\n66% remaining\nResets Jul 18, 2026 3:00 PM\nUsage limit resets\nUse a reset to restore your 5-hour limit, weekly limit, or both.\nFull reset\nExpires September 21";
        var usage = LoginWindow.ParseVisibleText(text);
        Assert.Equal(1, usage.AvailableUsageResetCount);
        Assert.Equal("Full reset", usage.UsageResetType);
        Assert.Equal("September 21", usage.UsageResetExpiresText);
        Assert.NotNull(usage.UsageResetExpiresAt);
    }

    [Fact]
    public void FindUsageLimitResetInfo_NoAvailableReset_ReturnsZero()
    {
        const string text = "Usage limit resets\nNo resets available";
        var info = LoginWindow.FindUsageLimitResetInfo(text,
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal(0, info.Count);
    }

    [Fact]
    public void FindUsageLimitResetInfo_UpdatedLabels_ReturnsExplicitCountAndExpiry()
    {
        const string text = "Usage resets\n2 resets available\n5-hour reset\nValid until October 1, 2026\nWeekly reset\nExpires on October 2, 2026";
        var info = LoginWindow.FindUsageLimitResetInfo(text,
            new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal(2, info.Count);
        Assert.Equal("5-hour reset", info.Type);
        Assert.Equal("October 1, 2026", info.ExpiresText);
        Assert.NotNull(info.ExpiresAt);
        Assert.Equal(2, info.Entries.Count);
        Assert.Equal("Weekly reset", info.Entries[1].Type);
        Assert.Equal("October 2, 2026", info.Entries[1].ExpiresText);
    }

    [Fact]
    public void ParseVisibleText_MultipleResetCards_PreservesEveryExpirationDate()
    {
        const string text = "5 hour usage limit\n80% remaining\nResets 6:00 PM\nWeekly usage limit\n50% remaining\nResets September 12, 2026\nUsage limit resets\nFull reset\nExpires September 21\nUse reset\nFull reset\nExpires October 18\nUse reset";
        var usage = LoginWindow.ParseVisibleText(text);
        Assert.Equal(2, usage.AvailableUsageResetCount);
        Assert.Equal(2, usage.UsageLimitResets.Count);
        Assert.Equal("September 21", usage.UsageLimitResets[0].ExpiresText);
        Assert.Equal("October 18", usage.UsageLimitResets[1].ExpiresText);
    }

    [Fact]
    public void TrayDetailsPosition_BottomTaskbar_StaysAboveAnchorAndInsideWorkingArea()
    {
        var bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var working = new System.Drawing.Rectangle(0, 0, 1920, 1040);
        var anchor = new System.Drawing.Point(1800, 1060);
        var result = TrayDetailsWindow.CalculatePopupPosition(bounds, working, anchor, 320, 370);
        Assert.True(result.Y + 370 <= anchor.Y);
        Assert.InRange(result.X, working.Left, working.Right - 320);
        Assert.InRange(result.Y, working.Top, working.Bottom - 370);
    }

    [Fact]
    public void TrayDetailsPosition_LeftTaskbar_OpensToTheRight()
    {
        var bounds = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
        var working = new System.Drawing.Rectangle(-1870, 0, 1870, 1080);
        var anchor = new System.Drawing.Point(-1900, 700);
        var result = TrayDetailsWindow.CalculatePopupPosition(bounds, working, anchor, 320, 370);
        Assert.True(result.X > anchor.X);
        Assert.InRange(result.Y, working.Top, working.Bottom - 370);
    }

    [Fact]
    public void TrayTooltip_IsLimitedToNotifyIconMaximum()
    {
        var tooltip = TrayIconService.TruncateTooltip(new string('x', 100));
        Assert.Equal(63, tooltip.Length);
        Assert.EndsWith("...", tooltip);
    }

    [Theory]
    [InlineData("{\"text\":\"usage\"}", "{\"text\":\"usage\"}")]
    [InlineData("\"{\\u0022text\\u0022:\\u0022usage\\u0022}\"", "{\"text\":\"usage\"}")]
    public void DecodeScriptResult_AcceptsObjectAndJsonString(string encoded, string expected)
    {
        Assert.Equal(expected, LoginWindow.DecodeScriptResult(encoded));
    }

    [Fact]
    public void UsageResetTimeParser_TimeAlreadyPassed_UsesNextDay()
    {
        var now = new DateTimeOffset(2026, 7, 12, 7, 0, 0, TimeSpan.FromHours(8));
        var parsed = UsageResetTimeParser.Parse("6:19 AM", now);
        Assert.Equal(new DateTimeOffset(2026, 7, 13, 6, 19, 0, TimeSpan.FromHours(8)), parsed);
    }

    [Fact]
    public void UsageResetTimeParser_EnglishFullDate_ParsesDateAndTime()
    {
        var now = new DateTimeOffset(2026, 7, 12, 7, 0, 0, TimeSpan.FromHours(8));
        var parsed = UsageResetTimeParser.Parse("Jul 18, 2026 3:00 PM", now);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 7, 18, 15, 0, 0), parsed.Value.DateTime);
    }

    [Fact]
    public void UsageResetTimeParser_ChineseFullDate_ParsesDateAndTime()
    {
        var now = new DateTimeOffset(2026, 7, 12, 7, 0, 0, TimeSpan.FromHours(8));
        var parsed = UsageResetTimeParser.Parse("2026年7月18日 下午3:00", now);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, 7, 18, 15, 0, 0), parsed.Value.DateTime);
    }

    [Fact]
    public void AutoResumeScheduler_BothLimitsExhausted_StillPollsBeforeScheduledReset()
    {
        var now = new DateTimeOffset(2026, 7, 12, 7, 0, 0, TimeSpan.FromHours(8));
        var usage = new UsageData
        {
            FiveHourRemainingPercent = 0,
            WeeklyRemainingPercent = 0,
            FiveHourResetAt = now.AddHours(2),
            WeeklyResetAt = now.AddHours(5)
        };
        Assert.Equal(now.AddMinutes(5), AutoResumeScheduler.CalculateNextCheck(usage, now));
    }

    [Fact]
    public void ConversationDiscovery_RepeatedPagesDoNotCreateFalseAmbiguity()
    {
        IReadOnlyList<ConversationTarget> page =
        [
            new ConversationTarget { ProjectName = "Project", Title = "Goal A" }
        ];
        var merged = ConversationDiscoveryService.MergeDiscoveryPages([page, page]);
        Assert.Single(merged);
        Assert.Equal(ConversationIdentityStatus.Available, merged[0].IdentityStatus);
    }

    [Fact]
    public void ConversationDiscovery_DuplicateTitleOnSamePage_IsAmbiguous()
    {
        IReadOnlyList<ConversationTarget> page =
        [
            new ConversationTarget { ProjectName = "Project", Title = "Same title" },
            new ConversationTarget { ProjectName = "Project", Title = "Same title" }
        ];
        var merged = ConversationDiscoveryService.MergeDiscoveryPages([page]);
        Assert.Single(merged);
        Assert.Equal(ConversationIdentityStatus.Ambiguous, merged[0].IdentityStatus);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(9, 10)]
    public void AutoResumeScheduler_RetryDelay_UsesTwoFiveTenMinutes(int attempt, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), AutoResumeScheduler.CalculateResumeRetryDelay(attempt));
    }

    [Fact]
    public void AutoResumeScheduler_KnownWeeklyZeroIsLimitedEvenWithoutFiveHourWindow()
    {
        var usage = new UsageData { FiveHourRemainingPercent = null, WeeklyRemainingPercent = 0 };
        Assert.False(AutoResumeScheduler.HasCompleteUsage(usage));
        Assert.True(AutoResumeScheduler.HasDisplayableUsage(usage));
        Assert.True(AutoResumeScheduler.IsUsageLimited(usage));
    }

    [Fact]
    public void AutoResumeScheduler_RealZero_IsCompleteAndLimited()
    {
        var usage = new UsageData { FiveHourRemainingPercent = 0, WeeklyRemainingPercent = 50 };
        Assert.True(AutoResumeScheduler.HasCompleteUsage(usage));
        Assert.True(AutoResumeScheduler.IsUsageLimited(usage));
    }

    [Theory]
    [InlineData("usageLimited", true)]
    [InlineData("paused", false)]
    [InlineData("active", false)]
    [InlineData("budgetLimited", false)]
    [InlineData("complete", false)]
    public void GoalResumeService_AutoResumeOnlyAcceptsUsageLimitedGoals(string text, bool expected)
    {
        Assert.Equal(expected, GoalProtocol.CanResumeGoal(text, false));
    }

    [Fact]
    public void CreateEnvironmentOptions_ValidProxy_AddsChromiumArguments()
    {
        var options = LoginWindow.CreateEnvironmentOptions("socks5://127.0.0.1:7890", "localhost;<local>");
        Assert.Contains("--proxy-server=socks5://127.0.0.1:7890", options.AdditionalBrowserArguments);
        Assert.Contains("--proxy-bypass-list=localhost;<local>", options.AdditionalBrowserArguments);
    }

    [Fact]
    public void CreateEnvironmentOptions_CredentialsInUrl_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            LoginWindow.CreateEnvironmentOptions("http://user:password@127.0.0.1:8080", null));
    }

    [Fact]
    public void CredentialProtector_RoundTripsForCurrentWindowsUser()
    {
        var encrypted = CredentialProtector.Protect("proxy-secret");
        Assert.NotEqual("proxy-secret", encrypted);
        Assert.Equal("proxy-secret", CredentialProtector.Unprotect(encrypted));
        Assert.Null(CredentialProtector.Unprotect("not-base64"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AutoRefreshInterval_ZeroBlankOrNegative_DisablesRefresh(int? minutes)
    {
        var settings = new WindowPosition { AutoRefreshIntervalMinutes = minutes };
        Assert.Null(FloatingBallViewModel.GetAutoRefreshInterval(settings));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void AutoRefreshInterval_PositiveMinutes_EnablesRefresh(int minutes)
    {
        var settings = new WindowPosition { AutoRefreshIntervalMinutes = minutes };
        Assert.Equal(TimeSpan.FromMinutes(minutes), FloatingBallViewModel.GetAutoRefreshInterval(settings));
    }

    [Theory]
    [InlineData("http://proxy.example:8080/", "http://proxy.example:8080", true)]
    [InlineData("http://PROXY.example:8080/", "http://proxy.example:8080", true)]
    [InlineData("https://chatgpt.com/", "http://proxy.example:8080", false)]
    [InlineData("http://proxy.example:8081/", "http://proxy.example:8080", false)]
    public void ProxyAuthenticationUri_OnlyMatchesConfiguredProxy(string request, string proxy, bool expected)
    {
        Assert.Equal(expected, LoginWindow.IsProxyAuthenticationUri(request, proxy));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
