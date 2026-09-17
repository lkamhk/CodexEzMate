using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface ISettingsService
{
    Task<WindowPosition?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(WindowPosition position, CancellationToken cancellationToken);
}
