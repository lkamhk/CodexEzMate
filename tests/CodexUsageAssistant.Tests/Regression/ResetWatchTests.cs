using System.IO;
using System.Net.Http;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;
using static CodexUsageAssistant.Tests.Regression.ResetWatchTestData;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class ResetWatchTests
{
    [Fact]
    public void FirstFeedAndChangedFeedEstablishSilentBaselines()
    {
        var first = ResetNotificationCoordinator.Evaluate(new(), Feed(Event()), Settings, Now);
        Assert.Empty(first.Announcements); Assert.False(first.RefreshUsage); Assert.Single(first.Seen);
        var changed = ResetNotificationCoordinator.Evaluate(Baseline(Feed()), Feed(Event()) with { FeedId = "other-feed" }, Settings, Now);
        Assert.Empty(changed.Announcements); Assert.False(changed.RefreshUsage);
    }

    [Theory]
    [InlineData(ResetAnnouncementStatus.Preview, false)]
    [InlineData(ResetAnnouncementStatus.Announced, true)]
    [InlineData(ResetAnnouncementStatus.Corrected, true)]
    [InlineData(ResetAnnouncementStatus.Retracted, false)]
    public void NotificationAndAccountRefreshAreSeparate(ResetAnnouncementStatus status, bool refresh)
    {
        var result = ResetNotificationCoordinator.Evaluate(Baseline(Feed()), Feed(Event() with { Status = status }), Settings, Now);
        Assert.Single(result.Announcements); Assert.Equal(refresh, result.RefreshUsage);
    }

    [Fact]
    public void CommunityIsOptInAndNeverTriggersAccountRefresh()
    {
        var feed = Feed(Event() with { Evidence = ResetEvidence.Community });
        var hidden = ResetNotificationCoordinator.Evaluate(Baseline(Feed()), feed, Settings, Now);
        Assert.Empty(hidden.Announcements); Assert.False(hidden.RefreshUsage);
        var visible = ResetNotificationCoordinator.Evaluate(Baseline(Feed()), feed, Settings with { NotifyCommunity = true }, Now);
        Assert.Single(visible.Announcements); Assert.False(visible.RefreshUsage);
    }

    [Fact]
    public void RepeatedOrOlderRevisionsDoNotNotify()
    {
        var previous = Baseline(Feed(Event(revision: 2)));
        Assert.Empty(ResetNotificationCoordinator.Evaluate(previous, Feed(Event()), Settings, Now).Announcements);
        Assert.Empty(ResetNotificationCoordinator.Evaluate(previous, Feed(Event(revision: 2)), Settings, Now).Announcements);
        Assert.Single(ResetNotificationCoordinator.Evaluate(previous, Feed(Event(revision: 3)), Settings, Now).Announcements);
    }

    [Fact]
    public void SourceHealthAndFeedAgeDeferRecentEvents()
    {
        var previous = Baseline(Feed());
        var fresh = Feed(Event());
        var stale = fresh with { GeneratedAt = Now.AddHours(-2) };
        var failed = fresh with { Sources = [fresh.Sources[0] with { Health = ResetSourceHealth.Unavailable }] };
        foreach (var feed in new[] { stale, failed })
        {
            var result = ResetNotificationCoordinator.Evaluate(previous, feed, Settings, Now);
            Assert.Empty(result.Announcements); Assert.Empty(result.Seen); Assert.False(result.RefreshUsage);
        }
        Assert.Single(ResetNotificationCoordinator.Evaluate(previous, fresh, Settings, Now).Announcements);
    }

    [Fact]
    public void OldAndExpiredPreviewsNeverPopup()
    {
        var old = Event("old") with { PublishedAt = Now.AddDays(-2), UpdatedAt = Now.AddDays(-2) };
        var expired = Event("expired") with { Status = ResetAnnouncementStatus.Preview, ExpectedEndAt = Now.AddMinutes(-1) };
        var result = ResetNotificationCoordinator.Evaluate(Baseline(Feed()), Feed(old, expired), Settings, Now);
        Assert.Empty(result.Announcements); Assert.False(result.RefreshUsage); Assert.Equal(2, result.Seen.Count);
    }

    [Fact]
    public void DisabledNoticeIsNotReplayedWhenLaterEnabled()
    {
        var feed = Feed(Event());
        var muted = ResetNotificationCoordinator.Evaluate(Baseline(Feed()), feed, Settings with { NotifyAnnouncements = false }, Now);
        Assert.Empty(muted.Announcements);
        var state = Baseline(feed) with { Seen = muted.Seen };
        Assert.Empty(ResetNotificationCoordinator.Evaluate(state, feed, Settings, Now).Announcements);
    }

    [Fact]
    public void LedgerIsBounded()
    {
        var prior = Baseline(Feed()) with { Seen = Enumerable.Range(0, 2100).ToDictionary(x => "old-" + x, x => new ResetSeenRevision(1, Now.AddDays(-1))) };
        var result = ResetNotificationCoordinator.Evaluate(prior, Feed(Event()), Settings, Now);
        Assert.Equal(2048, result.Seen.Count); Assert.Contains("event-1", result.Seen.Keys);
    }

    [Fact]
    public async Task RestartAndHttp304DoNotRepeatNotifications()
    {
        var store = new Store(); var client = new Client();
        using (var watch = new ResetWatchService(client, store, new Clock()))
        {
            var notices = 0; var refreshes = 0;
            watch.AnnouncementsAvailable += items => notices += items.Count;
            watch.UsageRefreshRequested += () => refreshes++;
            await watch.RefreshAsync();
            Assert.Equal(0, notices);
            client.Response = new(Feed(Event()), "\"v1\"");
            await watch.RefreshAsync(); await watch.RefreshAsync();
            Assert.Equal(1, notices); Assert.Equal(1, refreshes);
        }
        client.Response = new(null, "\"v1\"", NotModified: true);
        using var restarted = new ResetWatchService(client, store, new Clock());
        var replayed = 0;
        restarted.AnnouncementsAvailable += items => replayed += items.Count;
        await restarted.RefreshAsync();
        Assert.Equal(0, replayed); Assert.Null(restarted.Error);
        Assert.Equal("\"v1\"", client.LastETag);
        Assert.Single(restarted.State.Feed!.Events);
    }

    [Theory]
    [InlineData("network")][InlineData("invalid")][InlineData("rollback")]
    [InlineData("changedWithoutRevision")][InlineData("revisionRollback")][InlineData("disk")]
    public async Task FailuresRetainLastValidDataAndDoNotNotify(string kind)
    {
        var store = new Store(); var client = new Client { Response = new(Feed(Event())) };
        using var watch = new ResetWatchService(client, store, new Clock());
        await watch.RefreshAsync();
        var previous = watch.State; var notices = 0;
        watch.AnnouncementsAvailable += items => notices += items.Count;
        client.Response = new(Feed(Event(revision: 2)));
        switch (kind)
        {
            case "network": client.Fetch = _ => throw new HttpRequestException("offline"); break;
            case "invalid": client.Response = new(Feed(Event()) with { SchemaVersion = 2 }); break;
            case "rollback": client.Response = new(Feed(Event()) with { GeneratedAt = Now.AddMinutes(-1) }); break;
            case "changedWithoutRevision": client.Response = new(Feed(Event() with { Title = "Edited without revision" })); break;
            case "revisionRollback": client.Response = new(Feed(Event(revision: 0))); break;
            case "disk": store.FailWrites = true; break;
        }
        await watch.RefreshAsync();
        Assert.Same(previous, watch.State); Assert.NotNull(watch.Error); Assert.Equal(0, notices);
    }

    [Fact]
    public async Task CorruptCacheCreatesSilentBaselineAndDisabledClientDoesNotConnect()
    {
        var store = new Store { ReadError = new InvalidDataException("bad cache") };
        var client = new Client { Response = new(Feed(Event())) };
        using var watch = new ResetWatchService(client, store, new Clock());
        await watch.InitializeAsync(); Assert.Equal("cache", watch.Error);
        var notices = 0; watch.AnnouncementsAvailable += items => notices += items.Count;
        await watch.RefreshAsync(); Assert.Equal(0, notices); Assert.Null(watch.Error);
        await watch.SaveSettingsAsync(Settings with { Enabled = false });
        await watch.RefreshAsync(); Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Http304WithoutCacheFailsSafely()
    {
        using var watch = new ResetWatchService(new Client { Response = new(null, NotModified: true) }, new Store(), new Clock());
        await watch.RefreshAsync(); Assert.Equal("invalid", watch.Error); Assert.Null(watch.State.Feed);
    }

    [Fact]
    public async Task ConcurrentChecksAndShutdownDoNotDuplicateWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Fetch = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(Feed()); } };
        var store = new Store();
        using var watch = new ResetWatchService(client, store, new Clock());
        var first = watch.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await watch.RefreshAsync(); Assert.Equal(1, client.Calls);
        watch.Dispose(); await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, store.Writes); Assert.False(watch.IsRefreshing); Assert.Null(watch.Error);
    }

    [Fact]
    public async Task SettingsFailureKeepsPreviousConfiguration()
    {
        var store = new Store();
        using var watch = new ResetWatchService(new Client(), store, new Clock());
        await watch.InitializeAsync(); store.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => watch.SaveSettingsAsync(Settings with { Enabled = false }));
        Assert.True(watch.Settings.Enabled);
    }

    [Theory]
    [InlineData(0)][InlineData(4)][InlineData(61)]
    public void InvalidPollingIntervalIsRejected(int minutes) =>
        Assert.Throws<InvalidDataException>(() => ResetWatchService.ValidateSettings(Settings with { PollIntervalMinutes = minutes }));

    [Fact]
    public void PopupStaysOpenByDefaultAndOptionalTimeoutIsValidated()
    {
        Assert.Null(new ResetWatchSettings().PopupAutoCloseSeconds);
        ResetWatchService.ValidateSettings(Settings with { PopupAutoCloseSeconds = null });
        ResetWatchService.ValidateSettings(Settings with { PopupAutoCloseSeconds = 1 });
        ResetWatchService.ValidateSettings(Settings with { PopupAutoCloseSeconds = 3600 });
        Assert.Throws<InvalidDataException>(() => ResetWatchService.ValidateSettings(Settings with { PopupAutoCloseSeconds = 0 }));
        Assert.Throws<InvalidDataException>(() => ResetWatchService.ValidateSettings(Settings with { PopupAutoCloseSeconds = 3601 }));
    }

    [Fact]
    public async Task FileStoreRoundTripsAndCancelledWritePreservesPreviousFile()
    {
        // Keep test-owned files under ignored build output; never touch the real user's settings.
        var directory = Path.Combine(AppContext.BaseDirectory, "reset-test-data", Guid.NewGuid().ToString("N"));
        var store = new ResetWatchStore(directory);
        await store.SaveSettingsAsync(Settings, CancellationToken.None);
        await store.SaveStateAsync(Baseline(Feed(Event())), CancellationToken.None);
        Assert.Equal(Settings, await store.LoadSettingsAsync(CancellationToken.None));
        Assert.Single((await store.LoadStateAsync(CancellationToken.None))!.Seen);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveSettingsAsync(Settings with { Enabled = false }, cancel.Token));
        Assert.Equal(Settings, await store.LoadSettingsAsync(CancellationToken.None));
    }
}
