using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;
using Xunit.Abstractions;

namespace CodexUsageAssistant.Tests;

public sealed class AppServerUsageTests(ITestOutputHelper output)
{
    private sealed class FakeSignIn(bool result) : IAppServerSignInService
    {
        public int Calls;
        public Task<bool> SignInAsync(string? executable, CancellationToken token) { Calls++; return Task.FromResult(result); }
    }

    [Theory]
    [InlineData(UsageReadMode.AppServerOnly, true, 1, 0)]
    [InlineData(UsageReadMode.AppServerWithDomFallback, true, 1, 0)]
    [InlineData(UsageReadMode.AppServerWithDomFallback, false, 0, 0)]
    [InlineData(UsageReadMode.DomOnly, true, 0, 1)]
    public async Task ExplicitSignIn_UsesSelectedAuthenticationAndDoesNotFallBack(UsageReadMode mode, bool success, int appCalls, int domCalls)
    {
        var app = new FakeApp { Status = UsageStatus.NetworkError };
        var dom = new FakeDom(); var signIn = new FakeSignIn(success);
        var router = new UsageSourceService(new MemorySettings { Value = new() { UsageReadMode = mode } }, app, dom, signIn);
        await router.ShowLoginAndReadUsageAsync(CancellationToken.None);
        Assert.Equal(appCalls, app.Calls); Assert.Equal(domCalls, dom.Calls);
        Assert.Equal(mode == UsageReadMode.DomOnly ? 0 : 1, signIn.Calls);
    }

    [Fact]
    public void Plan_UsesSelectedCodexBucket()
    {
        var usage = Parse("""{"rateLimits":{"planType":"free"},"rateLimitsByLimitId":{"codex":{"planType":"plus","secondary":{"usedPercent":4,"windowDurationMins":10080}}}}""");
        Assert.Equal("plus", usage.PlanType);
        Assert.Equal("Plus", CodexUsageAssistant.ViewModels.FloatingBallViewModel.FormatPlan(usage.PlanType));
    }
    [Fact]
    public void ResetSelection_PrefersEarliestUnexpiredIdentifiableCredit()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var usage = new UsageData { UsageLimitResets = [
            new() { Id = "later", ExpiresAt = now.AddDays(20) },
            new() { Id = "unknown" },
            new() { Id = "expired", ExpiresAt = now.AddDays(-1) },
            new() { ExpiresAt = now.AddDays(1) },
            new() { Id = "earliest", ExpiresAt = now.AddDays(6) }
        ] };
        Assert.Equal("earliest", ResetRedemptionService.SelectEarliestCredit(usage, now));
        Assert.Null(ResetRedemptionService.SelectEarliestCredit(new UsageData(), now));
    }
    [Fact]
    public async Task ResetRetry_PersistsSameKeyAcrossServiceInstances()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reset-test-" + Guid.NewGuid());
        var path = Path.Combine(folder, "pending.json");
        string? first = null;
        try
        {
            var failing = new ResetRedemptionService(path, (_, key, creditId, _) => { first = key; Assert.Equal("earliest", creditId); throw new IOException("Simulated lost response"); });
            await Assert.ThrowsAsync<IOException>(() => failing.RedeemAsync(null, CancellationToken.None, "earliest"));
            Assert.True(File.Exists(path));
            var retry = new ResetRedemptionService(path, (_, key, creditId, _) => { Assert.Equal(first, key); Assert.Equal("earliest", creditId); return Task.FromResult("alreadyRedeemed"); });
            Assert.Equal("alreadyRedeemed", await retry.RedeemAsync(null, CancellationToken.None, "different"));
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("nothingToReset")]
    [InlineData("noCredit")]
    public async Task ResetKnownOutcome_CompletesAttempt(string outcome)
    {
        var folder = Path.Combine(Path.GetTempPath(), "reset-test-" + Guid.NewGuid());
        var path = Path.Combine(folder, "pending.json");
        try
        {
            var service = new ResetRedemptionService(path, (_, key, _, _) => { Assert.True(Guid.TryParse(key, out _)); return Task.FromResult(outcome); });
            Assert.Equal(outcome, await service.RedeemAsync(null, CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ResetUnknownOutcome_PreservesAttemptAndBlocksChangedExecutable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reset-test-" + Guid.NewGuid());
        var path = Path.Combine(folder, "pending.json");
        try
        {
            var service = new ResetRedemptionService(path, (_, _, _, _) => Task.FromResult("unknown"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RedeemAsync(null, CancellationToken.None));
            Assert.True(File.Exists(path));
            var calls = 0;
            var retry = new ResetRedemptionService(path, (_, _, _, _) => { calls++; return Task.FromResult("reset"); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => retry.RedeemAsync("different.exe", CancellationToken.None));
            Assert.Equal(0, calls);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Theory]
    [InlineData("{\"balance\":\"12.3456\",\"hasCredits\":true,\"unlimited\":false}", "12.3456", true, false)]
    [InlineData("{\"balance\":\"0\",\"hasCredits\":false,\"unlimited\":false}", "0", false, false)]
    [InlineData("{\"balance\":null,\"hasCredits\":true,\"unlimited\":true}", null, true, true)]
    [InlineData("null", null, null, null)]
    [InlineData("{}", null, null, null)]
    public void Credits_ParsesCodexBucketWithoutGuessing(string credits, string? balance, bool? available, bool? unlimited)
    {
        var usage = Parse("{\"rateLimits\":{\"credits\":{\"balance\":\"999\"}},\"rateLimitsByLimitId\":{\"codex\":{\"credits\":" + credits + "}}}");
        Assert.Equal(balance, usage.CreditBalance);
        Assert.Equal(available, usage.HasCredits);
        Assert.Equal(unlimited, usage.UnlimitedCredits);
    }
    [Fact]
    public void Tokens_UsesTodayBucketAndAccountSummary()
    {
        using var json = JsonDocument.Parse("""{"summary":{"lifetimeTokens":18400000,"currentStreakDays":12},"dailyUsageBuckets":[{"startDate":"2026-09-14","tokens":999},{"startDate":"2026-09-15","tokens":482000}]}""");
        var usage = new UsageData();
        AppServerUsageService.ApplyTokenUsage(usage, json.RootElement, new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal(482000L, usage.TodayTokens);
        Assert.Equal("2026-09-15", usage.TodayTokensDate);
        Assert.Equal(18400000L, usage.LifetimeTokens);
        Assert.Equal(12L, usage.CurrentStreakDays);
        Assert.Equal(2, usage.DailyTokenUsage.Count);
        Assert.Equal(482000, usage.DailyTokenUsage[1].Tokens);
    }

    [Fact]
    public void DailyChart_UsesLatestFiveDatesAndScalesToMaximum()
    {
        var data = Enumerable.Range(1, 6).Reverse().Select(day => new DailyTokenUsage { Date = new DateOnly(2026, 9, day), Tokens = day * 1000 });
        var rows = CodexUsageAssistant.ViewModels.FloatingBallViewModel.CreateDailyTokenRows(data);
        Assert.Equal(5, rows.Count);
        Assert.Equal("Sep 2", rows[0].DateLabel);
        Assert.Equal("6k", rows[4].CountLabel);
        Assert.Equal(100, rows[4].Percent);
        Assert.Equal(100.0 / 3, rows[0].Percent, 8);
    }

    [Fact]
    public void DailyChart_EmptyAndZeroHistoryAreSafe()
    {
        Assert.Empty(CodexUsageAssistant.ViewModels.FloatingBallViewModel.CreateDailyTokenRows(null));
        var rows = CodexUsageAssistant.ViewModels.FloatingBallViewModel.CreateDailyTokenRows([new DailyTokenUsage { Date = new DateOnly(2026, 9, 15), Tokens = 0 }]);
        Assert.Equal(0, rows[0].Percent);
        Assert.Equal("0", rows[0].CountLabel);
    }

    [Fact]
    public void DailyHistory_IgnoresMalformedDatesAndCounts()
    {
        using var json = JsonDocument.Parse("""{"dailyUsageBuckets":[{"startDate":"bad","tokens":15},{"startDate":"2026-09-15","tokens":-2},{"startDate":"2026-09-14","tokens":0}]}""");
        var usage = new UsageData();
        AppServerUsageService.ApplyTokenUsage(usage, json.RootElement, DateTimeOffset.Now);
        Assert.Equal(new DateOnly(2026, 9, 14), Assert.Single(usage.DailyTokenUsage).Date);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"summary\":{\"lifetimeTokens\":null,\"currentStreakDays\":-1},\"dailyUsageBuckets\":[]}")]
    public void Tokens_MissingValuesStayUnknown(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var usage = new UsageData();
        AppServerUsageService.ApplyTokenUsage(usage, json.RootElement, DateTimeOffset.Now);
        Assert.Null(usage.TodayTokens);
        Assert.Null(usage.LifetimeTokens);
        Assert.Null(usage.CurrentStreakDays);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0 tokens")]
    [InlineData(482000L, "482k tokens")]
    [InlineData(18400000L, "18.4M tokens")]
    [InlineData(999999L, "1M tokens")]
    public void Tokens_FormatCompactCounts(long? count, string expected) =>
        Assert.Equal(expected, CodexUsageAssistant.ViewModels.FloatingBallViewModel.FormatTokenCount(count));

    private static UsageData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AppServerUsageService.ParseRateLimits(document.RootElement, DateTimeOffset.Now);
    }

    [Fact]
    public void Parser_MapsDurationsNotPrimaryOrderAndPreservesAllCredits()
    {
        var usage = Parse("""
            {"rateLimits":{"primary":{"usedPercent":35,"windowDurationMins":10080,"resetsAt":1789988400},
             "secondary":{"usedPercent":23,"windowDurationMins":300,"resetsAt":1789446060}},
             "rateLimitResetCredits":{"availableCount":3,"credits":[
              {"status":"available","title":"Full reset","expiresAt":1789976520},
              {"status":"available","title":"Full reset","expiresAt":1791109560},
              {"status":"available","title":"Full reset","expiresAt":1791188280}]}}
            """);
        Assert.Equal(UsageStatus.Available, usage.Status);
        Assert.Equal(65, usage.WeeklyRemainingPercent);
        Assert.Equal(77, usage.FiveHourRemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789446060), usage.FiveHourResetAt);
        Assert.Equal(3, usage.AvailableUsageResetCount);
        Assert.Equal(new long[] { 1789976520, 1791109560, 1791188280 }, usage.UsageLimitResets.Select(x => x.ExpiresAt!.Value.ToUnixTimeSeconds()));
    }

    [Fact]
    public void Parser_UsesCodexBucketAndKeepsMissingFieldsUnknown()
    {
        var usage = Parse("""
            {"rateLimits":{"limitId":"other","primary":{"usedPercent":99,"windowDurationMins":300}},
             "rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":12,"windowDurationMins":10080}}},
             "rateLimitResetCredits":{"availableCount":5,"credits":null}}
            """);
        Assert.Equal(88, usage.WeeklyRemainingPercent);
        Assert.Null(usage.FiveHourRemainingPercent);
        Assert.Null(usage.WeeklyResetAt);
        Assert.Equal(5, usage.AvailableUsageResetCount);
        Assert.Empty(usage.UsageLimitResets);
    }

    [Fact]
    public void Parser_UnknownWindowDoesNotMasqueradeAsFiveHours()
    {
        var usage = Parse("""{"rateLimits":{"primary":{"usedPercent":15,"windowDurationMins":15}}}""");
        Assert.Equal(UsageStatus.ParseFailed, usage.Status);
        Assert.Null(usage.FiveHourRemainingPercent);
        Assert.Null(usage.AvailableUsageResetCount);
    }

    [Fact]
    public void Parser_BackendBlockPreventsAutomaticResume()
    {
        var usage = Parse("""{"ordinaryUsageAllowed":false,"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":10,"windowDurationMins":10080}}}""");
        Assert.True(AutoResumeScheduler.IsUsageLimited(usage));
    }

    [Theory]
    [InlineData(UsageReadMode.AppServerWithDomFallback, UsageStatus.Available, 1, 0)]
    [InlineData(UsageReadMode.AppServerWithDomFallback, UsageStatus.NetworkError, 1, 1)]
    [InlineData(UsageReadMode.AppServerOnly, UsageStatus.NetworkError, 1, 0)]
    [InlineData(UsageReadMode.DomOnly, UsageStatus.Available, 0, 1)]
    public async Task Router_RespectsSelection(UsageReadMode mode, UsageStatus status, int appCalls, int domCalls)
    {
        var app = new FakeApp { Status = status };
        var dom = new FakeDom();
        var service = new UsageSourceService(new MemorySettings { Value = new() { UsageReadMode = mode } }, app, dom);
        var usage = await service.RefreshUsageAsync(CancellationToken.None);
        Assert.Equal(appCalls, app.Calls);
        Assert.Equal(domCalls, dom.Calls);
        if (domCalls > 0) Assert.Contains("DOM", usage!.DataSource);
    }

    [Fact]
    public async Task Router_CancellationDoesNotOpenDom()
    {
        var dom = new FakeDom();
        var service = new UsageSourceService(new MemorySettings(), new FakeApp { Cancel = true }, dom);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshUsageAsync(CancellationToken.None));
        Assert.Equal(0, dom.Calls);
    }

    [Fact]
    public async Task Settings_LegacyDefaultsAndModePathRoundTrip()
    {
        Assert.Equal(UsageReadMode.AppServerWithDomFallback, JsonSerializer.Deserialize<WindowPosition>("{}")!.UsageReadMode);
        var path = Path.Combine(Path.GetTempPath(), "usage-source-" + Guid.NewGuid() + ".json");
        try
        {
            var service = new JsonSettingsService(path);
            await service.SaveAsync(new() { DetailsContentTab = 2, UsageReadMode = UsageReadMode.DomOnly, CodexExecutablePath = @"C:\Codex App\codex.exe" }, CancellationToken.None);
            var actual = await service.LoadAsync(CancellationToken.None);
            Assert.Equal(UsageReadMode.DomOnly, actual!.UsageReadMode);
            Assert.Equal(2, actual.DetailsContentTab);
            Assert.Equal(@"C:\Codex App\codex.exe", actual.CodexExecutablePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LiveRead_WhenExplicitlyEnabled_OnlyReadsUsage()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_LIVE_USAGE") != "1") return;
        var result = await new AppServerUsageService().ReadAsync(null, CancellationToken.None);
        output.WriteLine($"Status={result.Status}; 5h={result.FiveHourRemainingPercent}; weekly={result.WeeklyRemainingPercent}; resets={result.AvailableUsageResetCount}; entries={result.UsageLimitResets.Count}");
        Assert.True(result.Status == UsageStatus.Available, result.ErrorMessage);
        output.WriteLine($"Today={result.TodayTokens}; date={result.TodayTokensDate}; lifetime={result.LifetimeTokens}; streak={result.CurrentStreakDays}");
        output.WriteLine($"Credit balance={result.CreditBalance ?? "unavailable"}; available={result.HasCredits}; unlimited={result.UnlimitedCredits}");
        output.WriteLine($"Plan={result.PlanType ?? "unavailable"}");
        output.WriteLine("Recent daily tokens: " + string.Join("; ", result.DailyTokenUsage.OrderBy(x => x.Date).TakeLast(5).Select(x => $"{x.Date:yyyy-MM-dd}={x.Tokens} local={x.IsLocalEstimate}")));
    }

    private sealed class MemorySettings : ISettingsService
    {
        public WindowPosition Value { get; set; } = new();
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(Value);
        public Task SaveAsync(WindowPosition value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
    private sealed class FakeApp : IAppServerUsageReader
    {
        public UsageStatus Status = UsageStatus.Available;
        public bool Cancel;
        public int Calls;
        public Task<UsageData> ReadAsync(string? path, CancellationToken token)
        {
            Calls++;
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(new UsageData { Status = Status, DataSource = "App Server" });
        }
    }
    private sealed class FakeDom : IUsageLoginService
    {
        public int Calls;
        public Task<UsageData?> RefreshUsageAsync(CancellationToken token) { Calls++; return Task.FromResult<UsageData?>(new() { Status = UsageStatus.Available }); }
        public Task<UsageData?> ShowLoginAndReadUsageAsync(CancellationToken token) => RefreshUsageAsync(token);
    }
}
