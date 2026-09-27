using System.Text.Json;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Tests.Regression;

internal static class ResetWatchTestData
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-06-01T12:00:00Z");
    internal static ResetWatchSettings Settings => new() { Enabled = true, FeedUrl = "https://example.com/feed.json" };
    internal static ResetAnnouncement Event(string id = "event-1", int revision = 1) => new()
    {
        Id = id, Revision = revision, Title = "Test Reset announcement", Status = ResetAnnouncementStatus.Announced,
        Evidence = ResetEvidence.Official, Impacts = [ResetImpact.Banked], PublishedAt = Now.AddMinutes(-10), UpdatedAt = Now,
        Links = [new() { SourceId = "official", Label = "Original source", Url = "https://example.com/announcement" }]
    };
    internal static ResetFeed Feed(params ResetAnnouncement[] events) => new()
    {
        SchemaVersion = 1, FeedId = "test-feed", GeneratedAt = Now, Events = events,
        Sources = [new() { Id = "official", Name = "Test source", Health = ResetSourceHealth.Healthy, LastSuccessfulCheckAt = Now }]
    };
    internal static byte[] Bytes(ResetFeed feed) => JsonSerializer.SerializeToUtf8Bytes(feed, ResetFeedClient.JsonOptions);
    internal static ResetWatchState Baseline(ResetFeed feed) => new()
    {
        FeedUrl = Settings.FeedUrl, Feed = feed,
        Seen = feed.Events.ToDictionary(x => x.Id, x => new ResetSeenRevision(x.Revision, Now))
    };
    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = ResetWatchTestData.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Store : IResetWatchStore
    {
        internal ResetWatchSettings? Settings { get; set; } = ResetWatchTestData.Settings;
        internal ResetWatchState? State { get; set; }
        internal Exception? ReadError { get; set; }
        internal bool FailWrites { get; set; }
        internal int Writes { get; private set; }
        public Task<ResetWatchSettings?> LoadSettingsAsync(CancellationToken token) => Task.FromResult(Settings);
        public Task<ResetWatchState?> LoadStateAsync(CancellationToken token) => ReadError is null
            ? Task.FromResult(State) : Task.FromException<ResetWatchState?>(ReadError);
        public Task SaveSettingsAsync(ResetWatchSettings settings, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailWrites) throw new System.IO.IOException("test write failure"); Settings = settings; return Task.CompletedTask; }
        public Task SaveStateAsync(ResetWatchState state, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailWrites) throw new System.IO.IOException("test write failure");
            // Round-trip to exercise the persisted contract, not shared object references.
            State = JsonSerializer.Deserialize<ResetWatchState>(JsonSerializer.SerializeToUtf8Bytes(state, ResetFeedClient.JsonOptions), ResetFeedClient.JsonOptions);
            Writes++;
            return Task.CompletedTask;
        }
    }
    internal sealed class Client : IResetFeedClient
    {
        internal ResetFeedResponse Response { get; set; } = new(Feed());
        internal Func<CancellationToken, Task<ResetFeedResponse>>? Fetch { get; set; }
        internal int Calls { get; private set; }
        internal string? LastETag { get; private set; }
        public Task<ResetFeedResponse> FetchAsync(Uri address, string? etag, DateTimeOffset? modified, CancellationToken token)
        { Calls++; LastETag = etag; return Fetch?.Invoke(token) ?? Task.FromResult(Response); }
    }
}
