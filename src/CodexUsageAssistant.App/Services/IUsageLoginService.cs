using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface IUsageLoginService
{
    Task<UsageData?> ShowLoginAndReadUsageAsync(CancellationToken cancellationToken);
    Task<UsageData?> RefreshUsageAsync(CancellationToken cancellationToken);
}
