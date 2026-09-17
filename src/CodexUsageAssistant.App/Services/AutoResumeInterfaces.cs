using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface IConversationDiscoveryService
{
    Task<ConversationDiscoveryResult> ScanAsync(CancellationToken cancellationToken);
}

public interface IGoalResumeService
{
    Task<UsageData> ReadUsageAsync(CancellationToken cancellationToken);
    Task<ConversationResumeResult> ResumeAsync(ConversationTarget target, CancellationToken cancellationToken);
    Task<ConversationResumeResult> ContinuePausedAsync(ConversationTarget target, CancellationToken cancellationToken);
    bool HasActiveWork { get; }
    GoalExecutionState State { get; }
    IReadOnlyList<GoalPendingRequest> PendingRequests { get; }
    event Action? Changed;
    event Action? UsageChanged;
    Task<bool> PauseAsync(CancellationToken cancellationToken);
    Task RespondAsync(string key, string decision, IReadOnlyDictionary<string, string[]>? answers, CancellationToken cancellationToken);
}

public interface IAutoResumeScheduler
{
    DateTimeOffset? NextCheckAt { get; }
    string Status { get; }
    event EventHandler? StatusChanged;
    event Action<UsageData>? UsageUpdated;
    Task InitializeAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<AutoResumeRunResult> RunNowAsync(CancellationToken cancellationToken);
    void RequestCheck();
}

public interface ITrayNotificationService
{
    void ShowNotification(string title, string message);
}
