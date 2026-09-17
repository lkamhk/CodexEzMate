namespace CodexUsageAssistant.Models;

public enum ConversationIdentityStatus
{
    Available,
    Ambiguous,
    Missing
}

public enum ConversationResumeStatus
{
    Unknown,
    WaitingForReset,
    Ready,
    Resuming,
    Resumed,
    NoActionNeeded,
    NotFound,
    Ambiguous,
    Failed,
    PendingConfirmation,
    AwaitingApproval,
    Paused,
    Completed,
    Unsupported,
    Busy
}

public sealed class ConversationTarget
{
    public string Source { get; set; } = "unknown";
    public string? Cwd { get; set; }
    public string? GoalStatus { get; set; }
    public string? Compatibility { get; set; }
    public string? LastTurnId { get; set; }
    public DateTimeOffset? NextResumeAt { get; set; }
    public string? ThreadId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public ConversationIdentityStatus IdentityStatus { get; set; } = ConversationIdentityStatus.Available;
    public ConversationResumeStatus LastStatus { get; set; }
    public string? LastMessage { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    public string Key => string.IsNullOrWhiteSpace(ThreadId) ? $"{ProjectName}\u001f{Title}" : $"thread:{ThreadId}";
}

public sealed class AutoResumeSettings
{
    public bool MonitorEnabled { get; set; }
    public bool AwaitingReset { get; set; }
    public bool NotifyOnFailure { get; set; } = true;
    public int ResumeRetryAttempt { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public List<ConversationTarget> Conversations { get; set; } = [];
}

public sealed record ConversationResumeResult(
    string ProjectName,
    string Title,
    ConversationResumeStatus Status,
    string Message,
    DateTimeOffset AttemptedAt);

public sealed record ConversationDiscoveryResult(
    IReadOnlyList<ConversationTarget> Conversations,
    string Message,
    bool Success = true);

public sealed record AutoResumeRunResult(
    string Message,
    DateTimeOffset? NextCheckAt,
    IReadOnlyList<ConversationResumeResult> Results);

public sealed record GoalExecutionState(string? ThreadId, string? TurnId, ConversationResumeStatus Status, string Message, string? GoalStatus = null);

public sealed record GoalPendingRequest(string Key, string Method, string ThreadId, string? TurnId,
    string Summary, System.Text.Json.JsonElement Parameters, IReadOnlyList<string> Decisions)
{
    public string DisplayName => Method switch
    {
        "item/commandExecution/requestApproval" => Services.LocalizationService.Pick("指令／網絡操作批准", "Command / network approval"),
        "item/fileChange/requestApproval" => Services.LocalizationService.Pick("檔案修改批准", "File change approval"),
        "item/permissions/requestApproval" => Services.LocalizationService.Pick("額外權限請求", "Additional permissions"),
        _ => Services.LocalizationService.Pick("等待你的輸入", "Waiting for your input")
    };
}
