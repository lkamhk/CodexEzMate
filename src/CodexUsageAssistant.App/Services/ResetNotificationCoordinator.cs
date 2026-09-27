using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed record ResetNotificationBatch(Dictionary<string, ResetSeenRevision> Seen,
    IReadOnlyList<ResetAnnouncement> Announcements, bool RefreshUsage);

// Pure notification policy. This class has no account credentials or execution capability.
public static class ResetNotificationCoordinator
{
    public static bool IsFresh(ResetFeed feed, DateTimeOffset now) => now - feed.GeneratedAt <= ResetFeedClient.MaximumAge;

    public static bool HasHealthySource(ResetAnnouncement item, ResetFeed feed, DateTimeOffset now) =>
        item.Links.Any(link => feed.Sources.Any(source => source.Id == link.SourceId
            && source.Health == ResetSourceHealth.Healthy && source.LastSuccessfulCheckAt is { } checkedAt
            && now - checkedAt <= ResetFeedClient.MaximumAge));

    public static ResetNotificationBatch Evaluate(ResetWatchState previous, ResetFeed feed,
        ResetWatchSettings settings, DateTimeOffset now)
    {
        var baseline = previous.Feed is null || previous.Feed.FeedId != feed.FeedId || previous.FeedUrl != settings.FeedUrl;
        var seen = baseline ? new Dictionary<string, ResetSeenRevision>(StringComparer.Ordinal)
            : new Dictionary<string, ResetSeenRevision>(previous.Seen, StringComparer.Ordinal);
        var announcements = new List<ResetAnnouncement>();
        var refresh = false;
        foreach (var item in feed.Events.OrderBy(x => x.UpdatedAt))
        {
            if (seen.TryGetValue(item.Id, out var known) && known.Revision >= item.Revision) continue;
            var recent = now - item.UpdatedAt <= TimeSpan.FromHours(24);
            // Defer new, recent events when a source/feed is stale instead of losing the notice.
            if (!baseline && recent && (!IsFresh(feed, now) || !HasHealthySource(item, feed, now))) continue;
            seen[item.Id] = new(item.Revision, now);
            if (baseline || !settings.Enabled || !recent) continue;
            if (item.Status == ResetAnnouncementStatus.Preview && item.ExpectedEndAt < now) continue;
            var trusted = item.Evidence != ResetEvidence.Community;
            if (trusted && item.Status is ResetAnnouncementStatus.Announced or ResetAnnouncementStatus.Corrected
                && item.Impacts.Any(x => x is ResetImpact.Global or ResetImpact.Banked)) refresh = true;
            var permitted = item.Status == ResetAnnouncementStatus.Preview ? settings.NotifyPreviews : settings.NotifyAnnouncements;
            if (permitted && (trusted || settings.NotifyCommunity)) announcements.Add(item);
        }
        var currentIds = feed.Events.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        seen = seen.OrderByDescending(x => currentIds.Contains(x.Key)).ThenByDescending(x => x.Value.ObservedAt).Take(2048)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return new(seen, announcements, refresh);
    }
}
