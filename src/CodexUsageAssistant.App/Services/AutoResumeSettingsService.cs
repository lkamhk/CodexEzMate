using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface IAutoResumeSettingsService
{
    Task<AutoResumeSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AutoResumeSettings settings, CancellationToken cancellationToken);
    async Task UpdateAsync(Action<AutoResumeSettings> update, CancellationToken token)
    {
        var settings = await LoadAsync(token); update(settings); await SaveAsync(settings, token);
    }
}

public sealed class AutoResumeSettingsService : IAutoResumeSettingsService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexUsageAssistant", "automation", "auto-resume.json");

    public async Task<AutoResumeSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new AutoResumeSettings();
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<AutoResumeSettings>(stream, Options, cancellationToken)
                   ?? new AutoResumeSettings();
        }
        catch (JsonException) { return new AutoResumeSettings(); }
        catch (IOException) { return new AutoResumeSettings(); }
        catch (UnauthorizedAccessException) { return new AutoResumeSettings(); }
    }

    public async Task SaveAsync(AutoResumeSettings settings, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try { await WriteAsync(settings, cancellationToken); }
        finally { Gate.Release(); }
    }

    public async Task UpdateAsync(Action<AutoResumeSettings> update, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try { var value = await LoadAsync(token); update(value); await WriteAsync(value, token); }
        finally { Gate.Release(); }
    }

    private async Task WriteAsync(AutoResumeSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        File.Move(temporary, _path, true);
    }
}
