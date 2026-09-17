using CodexUsageAssistant.Models;
using CodexUsageAssistant.Views;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class ResetRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(9));

    [Theory]
    [InlineData("Sep 21, 8:42 AM", 9, 21, 8, 42)]
    [InlineData("Oct 4, 10:26 AM", 10, 4, 10, 26)]
    [InlineData("Oct 5, 8:18 AM", 10, 5, 8, 18)]
    public void ScreenshotExpiration_PreservesMonthDayAndTime(string text, int month, int day, int hour, int minute)
    {
        var parsed = UsageResetTimeParser.ParseExpiration(text, Now);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(2026, month, day, hour, minute, 0), parsed.Value.DateTime);
    }

    [Fact]
    public void ScreenshotCards_PreserveAllThreeDates()
    {
        const string text = "Usage limit resets\nFull reset (Weekly + 5 hr)\nExpires Sep 21, 8:42 AM\nUse reset\nFull reset (Weekly + 5 hr)\nExpires Oct 4, 10:26 AM\nUse reset\nFull reset (Weekly + 5 hr)\nExpires Oct 5, 8:18 AM\nUse reset";
        var info = LoginWindow.FindUsageLimitResetInfo(text, Now);
        Assert.Equal(3, info.Count);
        Assert.Equal(new[] { "2026-09-21", "2026-10-04", "2026-10-05" },
            info.Entries.Select(entry => entry.ExpiresAt?.ToString("yyyy-MM-dd")));
    }

    [Fact]
    public void CachedIncorrectDates_AreCorrectedBeforeDisplay()
    {
        var usage = new UsageData
        {
            LastUpdatedAt = Now,
            UsageLimitResets =
            [
                new() { Type = "Full reset", ExpiresText = "Sep 21, 8:42 AM" },
                new() { Type = "Full reset", ExpiresText = "Oct 4, 10:26 AM", ExpiresAt = Now },
                new() { Type = "Full reset", ExpiresText = "Oct 5, 8:18 AM", ExpiresAt = Now }
            ]
        };
        Assert.Equal(string.Join(Environment.NewLine,
            "Full reset: 2026-09-21", "Full reset: 2026-10-04", "Full reset: 2026-10-05"),
            FloatingBallViewModel.FormatUsageResetEntries(usage));
    }

    [Theory]
    [InlineData("Oct 40, 10:26 AM")]
    [InlineData("Oct 4, 25:26 AM")]
    [InlineData("10:26 AM")]
    public void InvalidExpiration_DoesNotInventTodaysDate(string text)
    {
        Assert.Null(UsageResetTimeParser.ParseExpiration(text, Now));
    }

    [Theory]
    [InlineData("Jan 4, 10:26 AM", 2027)]
    [InlineData("Jan 4, 2026, 10:26 AM", 2026)]
    public void ExpirationYear_UsesReferenceYearUnlessExplicit(string text, int year)
    {
        Assert.Equal(new DateTime(year, 1, 4, 10, 26, 0), UsageResetTimeParser.ParseExpiration(text, Now)!.Value.DateTime);
    }

    [Fact]
    public void IdenticalResetCards_CountAsSeparateEntitlements()
    {
        var info = LoginWindow.FindUsageLimitResetInfo(
            "Usage limit resets\nFull reset\nExpires September 21\nUse reset\nFull reset\nExpires September 21\nUse reset", Now);
        Assert.Equal(2, info.Count);
        Assert.Equal(2, info.Entries.Count);
    }

    [Fact]
    public void MixedCardLabels_PreserveEveryExpiration()
    {
        var info = LoginWindow.FindUsageLimitResetInfo(
            "Usage resets\nFull reset\nExpires September 21\nSpecial reset\nExpires October 18\nWeekly reset\nExpires November 2", Now);
        Assert.Equal(3, info.Count);
        Assert.Equal(new[] { 9, 10, 11 }, info.Entries.Select(entry => entry.ExpiresAt!.Value.Month));
        Assert.Equal(new[] { 21, 18, 2 }, info.Entries.Select(entry => entry.ExpiresAt!.Value.Day));
    }

    [Fact]
    public void LongSection_DoesNotTruncateLaterCards()
    {
        var info = LoginWindow.FindUsageLimitResetInfo("Usage resets\nFull reset\nExpires September 21\n" +
            new string(' ', 2200) + "\nWeekly reset\nExpires October 18", Now);
        Assert.Equal(2, info.Count);
        Assert.Equal(18, info.Entries[1].ExpiresAt!.Value.Day);
    }

    [Fact]
    public void TenResets_AreNotMistakenForZero()
    {
        var info = LoginWindow.FindUsageLimitResetInfo("Usage resets\n10 resets available", Now);
        Assert.Equal(10, info.Count);
    }

    [Fact]
    public void ChineseCards_PreserveCountAndDates()
    {
        var info = LoginWindow.FindUsageLimitResetInfo(
            "使用量限制重設\n可用 2 次\n完整重設\n有效期至 2026年9月21日\n每週重設\n到期日 2026年10月18日", Now);
        Assert.Equal(2, info.Count);
        Assert.Equal(18, info.Entries[1].ExpiresAt!.Value.Day);
    }

    [Fact]
    public void Readiness_WaitsForResetCountAndAllCards()
    {
        var usage = new UsageData { Status = UsageStatus.Available };
        Assert.False(LoginWindow.HasCompleteResetInfo(usage));
        usage.AvailableUsageResetCount = 2;
        usage.UsageLimitResets.Add(new UsageLimitReset { ExpiresText = "September 21" });
        Assert.False(LoginWindow.HasCompleteResetInfo(usage));
        usage.UsageLimitResets.Add(new UsageLimitReset { ExpiresText = "October 18" });
        Assert.True(LoginWindow.HasCompleteResetInfo(usage));
        usage.AvailableUsageResetCount = 0;
        usage.UsageLimitResets.Clear();
        Assert.True(LoginWindow.HasCompleteResetInfo(usage));
    }
}
