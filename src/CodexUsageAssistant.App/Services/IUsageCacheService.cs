using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface IUsageCacheService
{
    Task<UsageData?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(UsageData usage, CancellationToken cancellationToken);
}
