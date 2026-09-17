namespace CodexUsageAssistant.Services;

public interface IAutomationService
{
    Task<string> ExecuteDefaultProfileAsync(CancellationToken cancellationToken);
    Task<string> ResumeCodexAsync(CancellationToken cancellationToken);
}
