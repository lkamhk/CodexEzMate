using System.Globalization;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.ViewModels;

public sealed record ResetLinkRow(string Label, string Url);

// Presentation only. Never rewrite the persisted feed or its revision ledger to shorten the UI.
public sealed record ResetEventRow
{
    public string Id { get; init; } = "";
    public ResetAnnouncementStatus StatusCode { get; init; }
    public string Status { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Published { get; init; } = "";
    public string PublishedDetails { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Timing { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Warning { get; init; } = "";
    public string Details { get; init; } = "";
    public IReadOnlyList<ResetLinkRow> Links { get; init; } = [];
    public bool HasDetails => Details.Length > 0;
    public bool DetailsExpanded { get; set; }
}

internal static class ResetActivityPresentation
{
    internal static ResetEventRow Create(ResetAnnouncement item, ResetFeed feed, DateTimeOffset now, AppLanguage language)
    {
        string L(string zh, string en) => LocalizationService.Translate(zh, en, language);
        var canonicalTracker = item.Id.StartsWith("codex-resets-", StringComparison.Ordinal)
            && item.Links.Any(x => x.SourceId == "codex-resets-com"
                && Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && uri.Host == "codex-resets.com")
            && item.Status == ResetAnnouncementStatus.Announced && item.Evidence == ResetEvidence.Community && item.Impacts.Length == 1;
        var type = item.Impacts.Length == 1 ? item.Impacts[0] switch
        { ResetImpact.Banked => "Banked reset", ResetImpact.Global => "Regular reset", _ => "" } : "";
        var knownTitle = canonicalTracker && type.Length > 0 && item.Title == "Codex " + type;
        var category = string.Join(" · ", item.Impacts.Select(x => x switch
        { ResetImpact.Banked => "Banked Reset", ResetImpact.Global => L("額度重置", "Quota reset"), _ => L("額度政策", "Quota policy") }));
        var title = knownTitle ? category : item.Title;

        // Suppress only the collector's exact redundant template, not arbitrary source text or corrections.
        var summary = item.Summary;
        if (knownTitle && (summary == $"codex-resets.com reports a {type}. This third-party report does not confirm receipt by your account."
            || summary == $"codex-resets.com reports the latest Codex reset as {type}.")) summary = "";
        var description = item.TimeDescription.Trim();
        const string trackerTimePrefix = "Reset time reported by the tracker: ";
        if (knownTitle && description.StartsWith(trackerTimePrefix, StringComparison.Ordinal)
            && DateTimeOffset.TryParse(description[trackerTimePrefix.Length..], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var reported) && reported == item.PublishedAt) description = "";

        var timingParts = new List<string>();
        if (description.Length > 0) timingParts.Add(description);
        if (item.ExpectedStartAt is { } start)
            timingParts.Add(LocalTime(start) + (item.ExpectedEndAt is { } end ? " – " + LocalTime(end) : ""));
        else if (item.ExpectedEndAt is { } end) timingParts.Add(L("截至 ", "By ") + LocalTime(end));
        var timing = string.Join(" · ", timingParts);
        if (item.Status == ResetAnnouncementStatus.Preview)
            timing = timing.Length > 0 ? L("預計：", "Expected: ") + timing : L("時間待定", "Time not announced");
        else if (timing.Length > 0) timing = L("時間說明：", "Timing: ") + timing;

        var shortenedSummary = Shorten(summary, 180);
        var shortenedTiming = Shorten(timing, 160);
        var scope = item.Scope.Trim();
        var shortenedScope = Shorten(scope, 140);
        var revised = item.Status is ResetAnnouncementStatus.Corrected or ResetAnnouncementStatus.Retracted;
        var published = (revised ? L("更新 ", "Updated ") : "") + LocalTime(revised ? item.UpdatedAt : item.PublishedAt);
        var metadata = L("公告：", "Published: ") + FullTime(item.PublishedAt);
        if (item.UpdatedAt != item.PublishedAt) metadata += L("\n更新：", "\nUpdated: ") + FullTime(item.UpdatedAt);
        if (item.Revision > 1) metadata += L("\n修訂：", "\nRevision: ") + item.Revision;
        var details = new List<string>();
        if (summary != shortenedSummary) details.Add(summary);
        if (timing != shortenedTiming) details.Add(timing);
        if (scope != shortenedScope) details.Add(L("適用範圍：", "Scope: ") + scope);
        if (details.Count > 0 || item.Revision > 1 || revised) details.Add(metadata);
        return new()
        {
            Id = item.Id, StatusCode = item.Status, Status = ResetWatchViewModel.StatusLabel(item.Status, language),
            Title = title, Category = title.Equals(category, StringComparison.OrdinalIgnoreCase) ? "" : category,
            Evidence = ResetWatchViewModel.EvidenceLabel(item.Evidence, language),
            Published = published, PublishedDetails = metadata,
            Summary = shortenedSummary, Timing = shortenedTiming,
            Scope = scope.Length == 0 ? "" : L("適用：", "Scope: ") + shortenedScope,
            Warning = ResetNotificationCoordinator.IsFresh(feed, now) && ResetNotificationCoordinator.HasHealthySource(item, feed, now)
                ? "" : L("來源未更新，暫停新通知", "Source needs updating · new notices paused"),
            Details = string.Join("\n\n", details),
            // Keep the source label, with the full URL in its tooltip; never repeat the same domain twice.
            Links = item.Links.Select(x => new ResetLinkRow(x.Label.Trim(), x.Url)).Distinct().ToArray()
        };
    }

    internal static string Shorten(string text, int maximum)
    {
        var offsets = StringInfo.ParseCombiningCharacters(text);
        return offsets.Length <= maximum ? text : text[..offsets[maximum]].TrimEnd() + "…";
    }

    private static string LocalTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd · HH:mm", CultureInfo.InvariantCulture);
    private static string FullTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
