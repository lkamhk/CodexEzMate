using System.Globalization;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.ViewModels;
using Xunit;
using static CodexUsageAssistant.Tests.Regression.ResetWatchTestData;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class ResetActivityPresentationTests
{
    [Fact]
    public void KnownCollectorCardIsCompactWithoutChangingOriginalFeed()
    {
        var feed = ResetWatchUiTests.PresentationFeed(Now);
        var before = Bytes(feed);
        var row = ResetActivityPresentation.Create(feed.Events[0], feed, Now, AppLanguage.English);
        Assert.Equal("Banked Reset", row.Title);
        Assert.Empty(row.Category); Assert.Empty(row.Summary); Assert.Empty(row.Timing); Assert.Empty(row.Scope);
        Assert.Empty(row.Warning); Assert.False(row.HasDetails);
        Assert.Equal("Community · unverified", row.Evidence);
        Assert.Equal("codex-resets.com", Assert.Single(row.Links).Label);
        Assert.Equal(before, Bytes(feed));
    }

    [Theory]
    [InlineData(ResetAnnouncementStatus.Preview)]
    [InlineData(ResetAnnouncementStatus.Corrected)]
    [InlineData(ResetAnnouncementStatus.Retracted)]
    public void NonstandardMessagesKeepTheirTitleAndMeaning(ResetAnnouncementStatus status)
    {
        var item = Event() with { Title = "Plans changed; check eligibility", Status = status,
            Summary = "Only eligible accounts qualify. Earlier details may be incorrect.", Scope = "Selected plans only." };
        var row = ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English);
        Assert.Equal(item.Title, row.Title); Assert.Equal(item.Summary, row.Summary);
        Assert.Contains(item.Scope, row.Scope);
        Assert.NotEmpty(row.Evidence);
        if (status is ResetAnnouncementStatus.Corrected or ResetAnnouncementStatus.Retracted)
        { Assert.True(row.HasDetails); Assert.StartsWith("Updated ", row.Published); }
    }

    [Fact]
    public void OnlyExactKnownBoilerplateIsHidden()
    {
        var feed = ResetWatchUiTests.PresentationFeed(Now);
        var item = feed.Events[0] with { Summary = feed.Events[0].Summary + " Not available for every plan." };
        var row = ResetActivityPresentation.Create(item, feed, Now, AppLanguage.English);
        Assert.Contains("Not available for every plan", row.Summary);
        item = item with { Id = "another-producer", Summary = feed.Events[0].Summary };
        row = ResetActivityPresentation.Create(item, feed, Now, AppLanguage.English);
        Assert.Equal(item.Title, row.Title); Assert.Equal(item.Summary, row.Summary);
    }

    [Fact]
    public void LongTextsHaveGraphemeSafePreviewsAndFullDetails()
    {
        var item = Event() with { Summary = string.Concat(Enumerable.Repeat("🙂e\u0301", 160)), Scope = new string('s', 160) };
        var row = ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English);
        Assert.EndsWith("…", row.Summary);
        Assert.Equal(181, new StringInfo(row.Summary).LengthInTextElements);
        Assert.Contains(item.Summary, row.Details); Assert.Contains(item.Scope, row.Details);
        Assert.True(row.HasDetails);
    }

    [Fact]
    public void PreviewKeepsTimeRangeAndVagueTimeDoesNotBecomeCountdown()
    {
        var item = Event() with { Status = ResetAnnouncementStatus.Preview, TimeDescription = "Later today" };
        var row = ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English);
        Assert.Equal("Expected: Later today", row.Timing);
        item = item with { ExpectedStartAt = Now.AddHours(1), ExpectedEndAt = Now.AddHours(2) };
        row = ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English);
        Assert.Contains("Later today", row.Timing); Assert.Contains(" – ", row.Timing);
        item = item with { TimeDescription = "", ExpectedStartAt = null, ExpectedEndAt = null };
        row = ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English);
        Assert.Equal("Time not announced", row.Timing);
    }

    [Fact]
    public void AnnouncedEventIsNotLabeledExpectedAndUnknownScopeIsAbsent()
    {
        var item = Event() with { TimeDescription = "Distribution is still in progress" };
        var row = ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English);
        Assert.StartsWith("Timing:", row.Timing); Assert.DoesNotContain("Expected:", row.Timing);
        Assert.Empty(row.Scope);
    }

    [Theory]
    [InlineData(ResetEvidence.Official, "Feed: official source")]
    [InlineData(ResetEvidence.Corroborated, "Feed: corroborated")]
    [InlineData(ResetEvidence.Community, "Community · unverified")]
    public void EvidenceNeverBecomesIndependentVerification(ResetEvidence evidence, string label)
    {
        var item = Event() with { Evidence = evidence };
        Assert.Equal(label, ResetActivityPresentation.Create(item, Feed(item), Now, AppLanguage.English).Evidence);
    }

    [Theory]
    [InlineData(AppLanguage.TraditionalChinese, "發放消息", "社群 · 未核實")]
    [InlineData(AppLanguage.SimplifiedChinese, "发放消息", "社群 · 未核实")]
    [InlineData(AppLanguage.English, "Announced", "Community · unverified")]
    public void LabelsFollowSelectedLanguage(AppLanguage language, string status, string evidence)
    {
        var feed = ResetWatchUiTests.PresentationFeed(Now);
        var row = ResetActivityPresentation.Create(feed.Events[0], feed, Now, language);
        Assert.Equal(status, row.Status); Assert.Equal(evidence, row.Evidence);
    }

    [Fact]
    public void StaleSourcesKeepWarningsVisible()
    {
        var item = Event(); var feed = Feed(item);
        Assert.Empty(ResetActivityPresentation.Create(item, feed, Now, AppLanguage.English).Warning);
        Assert.NotEmpty(ResetActivityPresentation.Create(item, feed, Now.AddHours(2), AppLanguage.English).Warning);
        var unavailable = feed with { Sources = [feed.Sources[0] with { Health = ResetSourceHealth.Unavailable }] };
        Assert.NotEmpty(ResetActivityPresentation.Create(item, unavailable, Now, AppLanguage.English).Warning);
    }
}
