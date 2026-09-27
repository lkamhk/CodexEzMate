namespace CodexUsageAssistant.Models;

public sealed record ResetWatchSettings
{
    // Opt in only. A collector has not been deployed and no endpoint is invented here.
    public bool Enabled { get; init; }
    public string FeedUrl { get; init; } = "";
    public int PollIntervalMinutes { get; init; } = 5;
    public bool NotifyPreviews { get; init; } = true;
    public bool NotifyAnnouncements { get; init; } = true;
    public bool NotifyCommunity { get; init; }
    // Independent of the public feed: existing local-account notices stay enabled by default.
    public bool NotifyAccountChanges { get; init; } = true;
}
