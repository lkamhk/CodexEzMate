namespace CodexUsageAssistant.Models;

public sealed record ResetWatchSettings
{
    public const string LegacyFeedUrl = "https://129.146.170.250/ezmate/reset-feed.json";
    public const string DefaultFeedUrl = "https://codex-resets.com/api/v1/status";
    // Prefill new installations; fetching remains opt-in and saved URLs are preserved.
    public bool Enabled { get; init; }
    public string FeedUrl { get; init; } = DefaultFeedUrl;
    public int PollIntervalMinutes { get; init; } = 5;
    public bool NotifyPreviews { get; init; } = true;
    public bool NotifyAnnouncements { get; init; } = true;
    public bool NotifyCommunity { get; init; }
    public bool ShowPopup { get; init; } = true;
    // Null keeps the popup open until the user closes it. A positive value auto-closes after that many seconds.
    public int? PopupAutoCloseSeconds { get; init; }
    // Independent of the public feed: existing local-account notices stay enabled by default.
    public bool NotifyAccountChanges { get; init; } = true;
}
