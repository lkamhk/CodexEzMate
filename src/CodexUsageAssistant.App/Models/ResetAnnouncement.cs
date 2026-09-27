using System.Text.Json.Serialization;

namespace CodexUsageAssistant.Models;

public enum ResetAnnouncementStatus { Preview, Announced, Corrected, Retracted }
public enum ResetEvidence { Official, Corroborated, Community }
public enum ResetImpact { Global, Banked, PolicyChange }
public enum ResetSourceHealth { Healthy, Degraded, Unavailable }

// Public information only: never include account IDs, credentials or local usage in the feed.
public sealed record ResetAnnouncement
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public int Revision { get; init; }
    [JsonRequired] public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    [JsonRequired] public ResetAnnouncementStatus Status { get; init; }
    [JsonRequired] public ResetEvidence Evidence { get; init; }
    [JsonRequired] public ResetImpact[] Impacts { get; init; } = [];
    [JsonRequired] public DateTimeOffset PublishedAt { get; init; }
    [JsonRequired] public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ExpectedStartAt { get; init; }
    public DateTimeOffset? ExpectedEndAt { get; init; }
    public string TimeDescription { get; init; } = "";
    public string Scope { get; init; } = "";
    public string? RelatedEventId { get; init; }
    [JsonRequired] public ResetAnnouncementLink[] Links { get; init; } = [];
}

public sealed record ResetAnnouncementLink
{
    [JsonRequired] public string SourceId { get; init; } = "";
    [JsonRequired] public string Label { get; init; } = "";
    [JsonRequired] public string Url { get; init; } = "";
}

public sealed record ResetFeedSource
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public ResetSourceHealth Health { get; init; }
    public DateTimeOffset? LastSuccessfulCheckAt { get; init; }
    public string Message { get; init; } = "";
}

public sealed record ResetFeed
{
    [JsonRequired] public int SchemaVersion { get; init; }
    [JsonRequired] public string FeedId { get; init; } = "";
    [JsonRequired] public DateTimeOffset GeneratedAt { get; init; }
    [JsonRequired] public ResetFeedSource[] Sources { get; init; } = [];
    [JsonRequired] public ResetAnnouncement[] Events { get; init; } = [];
}

public sealed record ResetSeenRevision(int Revision, DateTimeOffset ObservedAt);

public sealed record ResetWatchState
{
    public string FeedUrl { get; init; } = "";
    public ResetFeed? Feed { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public DateTimeOffset? LastSuccessfulFetchAt { get; init; }
    public Dictionary<string, ResetSeenRevision> Seen { get; init; } = new(StringComparer.Ordinal);
}
