namespace CodexUsageAssistant.Models;

public enum UsageStatus { Unknown, Loading, Available, LoginRequired, ParseFailed, NetworkError }

public sealed class UsageLimitReset
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? ExpiresText { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class UsageData
{
    public string? PlanType { get; set; }
    public List<DailyTokenUsage> DailyTokenUsage { get; set; } = [];
    public string? CreditBalance { get; set; }
    public bool? HasCredits { get; set; }
    public bool? UnlimitedCredits { get; set; }
    public long? TodayTokens { get; set; }
    public string? TodayTokensDate { get; set; }
    public long? LifetimeTokens { get; set; }
    public long? CurrentStreakDays { get; set; }
    public string? DataSource { get; set; }
    public bool? OrdinaryUsageAllowed { get; set; }
    public double? FiveHourRemainingPercent { get; set; }
    public double? WeeklyRemainingPercent { get; set; }
    public string? FiveHourResetText { get; set; }
    public string? WeeklyResetText { get; set; }
    public DateTimeOffset? FiveHourResetAt { get; set; }
    public DateTimeOffset? WeeklyResetAt { get; set; }
    public int? AvailableUsageResetCount { get; set; }
    public string? UsageResetType { get; set; }
    public string? UsageResetExpiresText { get; set; }
    public DateTimeOffset? UsageResetExpiresAt { get; set; }
    public List<UsageLimitReset> UsageLimitResets { get; set; } = [];
    public DateTimeOffset LastUpdatedAt { get; set; }
    public UsageStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class DailyTokenUsage
{
    public bool IsLocalEstimate { get; set; }
    public DateOnly Date { get; set; }
    public long Tokens { get; set; }
}
