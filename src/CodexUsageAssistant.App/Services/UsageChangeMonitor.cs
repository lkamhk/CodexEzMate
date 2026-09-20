using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal sealed class UsageChangeMonitor : IAsyncDisposable
{
    private readonly UsageAppServerSession _session;
    internal UsageChangeMonitor(UsageAppServerSession? session = null) => _session = session ?? UsageAppServerSession.Shared;
    private CancellationTokenSource? _cts;
    private Task? _listener;
    private FileSystemWatcher? _watcher;
    private string? _configuration;
    private int _pending;
    private DateTimeOffset _lastRefresh;

    internal static bool IsUsageNotification(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("id", out _) &&
        root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String &&
        method.GetString() is "account/rateLimits/updated" or "account/updated" or "thread/tokenUsage/updated";

    internal void Signal() => Interlocked.Exchange(ref _pending, 1);

    internal bool TakeChange(DateTimeOffset now)
    {
        if (now - _lastRefresh < TimeSpan.FromSeconds(10)) return false;
        if (Interlocked.Exchange(ref _pending, 0) == 0) return false;
        _lastRefresh = now;
        return true;
    }

    internal async Task ConfigureAsync(WindowPosition settings, CancellationToken token)
    {
        var configuration = settings.RefreshOnServerNotification && settings.UsageReadMode != UsageReadMode.DomOnly
            ? JsonSerializer.Serialize(new { settings.CodexExecutablePath, settings.ProxyEnabled, settings.ProxyServer,
                settings.ProxyBypassList, settings.ProxyUsername, settings.EncryptedProxyPassword }) : null;
        if (_configuration == configuration) return;
        await StopAsync();
        _configuration = configuration;
        if (configuration is null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Other Codex processes may not broadcast their events to this App Server instance.
        // Local rollout changes provide a complementary activity signal, without reading chat text here.
        try
        {
            if (Directory.Exists(LocalTokenUsageService.CodexHome))
            {
                _watcher = new FileSystemWatcher(LocalTokenUsageService.CodexHome, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                _watcher.Changed += OnLocalActivity;
                _watcher.Created += OnLocalActivity;
                _watcher.Renamed += OnLocalActivity;
                _watcher.Error += (_, _) => Signal();
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        _listener = ListenAsync(settings.CodexExecutablePath, _cts.Token);
        Signal();
    }

    private void OnLocalActivity(object sender, FileSystemEventArgs e) => Signal();

    private async Task ListenAsync(string? executable, CancellationToken token)
    {
        _session.Subscribe(Signal);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { _session.Unsubscribe(Signal); }
    }

    private async Task StopAsync()
    {
        _watcher?.Dispose();
        _watcher = null;
        _cts?.Cancel();
        if (_listener is not null) await _listener;
        _cts?.Dispose();
        _cts = null;
        _listener = null;
        Interlocked.Exchange(ref _pending, 0);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
