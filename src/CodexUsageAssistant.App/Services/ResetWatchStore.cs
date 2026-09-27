using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public interface IResetWatchStore
{
    Task<ResetWatchSettings?> LoadSettingsAsync(CancellationToken token);
    Task<ResetWatchState?> LoadStateAsync(CancellationToken token);
    Task SaveSettingsAsync(ResetWatchSettings settings, CancellationToken token);
    Task SaveStateAsync(ResetWatchState state, CancellationToken token);
}

public sealed class ResetWatchStore : IResetWatchStore
{
    private readonly string _directory;
    public ResetWatchStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CodexUsageAssistant", "ResetWatch")) { }
    public ResetWatchStore(string directory) => _directory = directory;

    public Task<ResetWatchSettings?> LoadSettingsAsync(CancellationToken token) => ReadAsync<ResetWatchSettings>("settings.json", 16384, token);
    public Task<ResetWatchState?> LoadStateAsync(CancellationToken token) => ReadAsync<ResetWatchState>("state.json", 2 * 1024 * 1024, token);
    public Task SaveSettingsAsync(ResetWatchSettings settings, CancellationToken token) => WriteAsync("settings.json", settings, token);
    public Task SaveStateAsync(ResetWatchState state, CancellationToken token) => WriteAsync("state.json", state, token);

    private async Task<T?> ReadAsync<T>(string name, int limit, CancellationToken token)
    {
        var path = Path.Combine(_directory, name);
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        var data = await ResetFeedClient.ReadBoundedAsync(stream, limit, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data, ResetFeedClient.JsonOptions) ?? throw new InvalidDataException("Invalid local Reset state.");
    }

    private async Task WriteAsync<T>(string name, T value, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        var temporary = path + ".tmp";
        // Serialized by ResetWatchService; an incomplete write never replaces the last good file.
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, value, ResetFeedClient.JsonOptions, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        File.Move(temporary, path, true);
    }
}
