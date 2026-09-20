using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

// One shared transport for usage reads and usage notifications, independent of Goal execution.
public sealed class UsageAppServerSession : IDisposable, IAsyncDisposable
{
    internal static UsageAppServerSession Shared { get; } = new(new JsonSettingsService());
    private readonly ISettingsService _settings;
    private readonly CodexAppServerHost? _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<WindowPosition, int?, CancellationToken, Task<IGoalRpcConnection>> _factory;
    private IGoalRpcConnection? _connection;
    private string? _key;
    private Task? _maintenance;
    private DateTimeOffset _lastUsed;
    private int _active;
    private Action? _changed;
    private readonly ConcurrentDictionary<string, string> _notificationHashes = new();
    public UsageAppServerSession(ISettingsService settings, CodexAppServerHost? host = null) : this(settings, host,
        (options, port, token) => port is int value ? GoalRpcConnection.ConnectAsync(value, token) : StdioUsageConnection.ConnectAsync(options, token)) { }
    internal UsageAppServerSession(ISettingsService settings, CodexAppServerHost? host,
        Func<WindowPosition, int?, CancellationToken, Task<IGoalRpcConnection>> factory)
    { _settings = settings; _host = host; _factory = factory; }

    internal void Subscribe(Action changed) { _changed += changed; StartMaintenance(); }
    internal void Unsubscribe(Action changed) => _changed -= changed;
    internal UsageAppServerSession CreateFresh() => new(_settings, null, _factory);
    internal async Task InvalidateAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Release(); }
    }
    private void StartMaintenance()
    {
        lock (_lifetime)
        {
            if (_maintenance is not null) return;
            _maintenance = Task.Run(async () =>
            {
                try
                {
                    while (!_lifetime.IsCancellationRequested)
                    {
                        if (_changed is not null)
                            try { await RequestAsync(null, "", null, _lifetime.Token).ConfigureAwait(false); }
                            catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
                        else
                        {
                            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                            try
                            {
                                if (_active == 0 && DateTimeOffset.UtcNow - _lastUsed > TimeSpan.FromSeconds(30)) await CloseAsync().ConfigureAwait(false);
                            }
                            finally { _gate.Release(); }
                        }
                        await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    internal async Task<JsonElement> RequestAsync(string? executable, string method, object? parameters, CancellationToken token)
    {
        StartMaintenance();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        IGoalRpcConnection connection;
        try
        {
            var settings = await _settings.LoadAsync(linked.Token).ConfigureAwait(false) ?? new();
            settings.CodexExecutablePath = executable ?? settings.CodexExecutablePath;
            var port = _host?.CompatibleUsagePort(settings);
            var key = JsonSerializer.Serialize(new { settings.CodexExecutablePath, port, settings.ProxyEnabled, settings.ProxyServer,
                settings.ProxyBypassList, settings.ProxyUsername, settings.EncryptedProxyPassword });
            if (_connection?.IsConnected != true || _key != key)
            {
                await CloseAsync().ConfigureAwait(false);
                _connection = await _factory(settings, port, linked.Token).ConfigureAwait(false); _key = key;
                var current = _connection;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var notification in current.Events(_lifetime.Token).ConfigureAwait(false))
                        {
                            if (ReferenceEquals(current, _connection) && UsageChangeMonitor.IsUsageNotification(notification))
                            {
                                var name = GoalProtocol.Text(notification, "method")!;
                                var payload = GoalProtocol.Property(notification, "params");
                                var hash = payload.ValueKind == JsonValueKind.Undefined ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
                                if (!_notificationHashes.TryGetValue(name, out var previous) || previous != hash)
                                { _notificationHashes[name] = hash; _changed?.Invoke(); }
                            }
                            if (notification.TryGetProperty("id", out var id)) await current.ReplyAsync(id, null, true, _lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
                });
            }
            connection = _connection; _active++; _lastUsed = DateTimeOffset.UtcNow;
        }
        finally { _gate.Release(); }
        try { return method.Length == 0 ? default : await connection.RequestAsync(method, parameters, linked.Token).ConfigureAwait(false); }
        catch (HostRpcException ex) { throw new InvalidOperationException($"App Server rejected {method} ({ex.Code}).", ex); }
        finally { Interlocked.Decrement(ref _active); _lastUsed = DateTimeOffset.UtcNow; }
    }
    private async Task CloseAsync()
    { var old = _connection; _connection = null; _key = null; _notificationHashes.Clear(); if (old is not null) await old.DisposeAsync().ConfigureAwait(false); }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _changed = null;
        if (_maintenance is not null) await _maintenance.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Release(); }
    }
}

internal sealed class StdioUsageConnection : IGoalRpcConnection
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly Channel<JsonElement> _events = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private Task? _reader;
    private int _id, _disposed;
    public bool IsConnected => !_lifetime.IsCancellationRequested && !_process.HasExited;
    private StdioUsageConnection(Process process) => _process = process;
    internal static async Task<IGoalRpcConnection> ConnectAsync(WindowPosition settings, CancellationToken token)
    {
        var start = new ProcessStartInfo(AppServerUsageService.FindExecutable(settings.CodexExecutablePath))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageAssistant", "runtime", "usage-server-work") };
        Directory.CreateDirectory(start.WorkingDirectory);
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        AppServerClient.ApplyProxyEnvironment(start, settings);
        var process = new Process { StartInfo = start };
        try { process.Start(); } catch { process.Dispose(); throw; }
        var client = new StdioUsageConnection(process);
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        client._reader = client.ReadAsync();
        try
        {
            await client.RequestAsync("initialize", new { clientInfo = new { name = "codex_ezmate_usage", version = "1.21.4" }, capabilities = new { experimentalApi = true } }, token).ConfigureAwait(false);
            await client.SendAsync(new { method = "initialized" }, token).ConfigureAwait(false); return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _id); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await SendAsync(new { id, method, @params = parameters }, timeout.Token).ConfigureAwait(false); return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (HostRpcException ex) { throw ex.ForMethod(method); }
        finally { _requests.TryRemove(id, out _); }
    }
    public Task ReplyAsync(JsonElement id, object? result, bool error, CancellationToken token) =>
        SendAsync(new { id, error = new { code = -32601, message = "Usage client has no execution capabilities" } }, token);
    public IAsyncEnumerable<JsonElement> Events(CancellationToken token) => _events.Reader.ReadAllAsync(token);
    private async Task SendAsync(object value, CancellationToken token)
    {
        await _write.WaitAsync(token).ConfigureAwait(false);
        try { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token).ConfigureAwait(false); await _process.StandardInput.FlushAsync(token).ConfigureAwait(false); }
        finally { _write.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("Usage response too large.");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.TryGetProperty("method", out _))
                {
                    if (root.TryGetProperty("id", out var requestId)) await ReplyAsync(requestId, null, true, _lifetime.Token).ConfigureAwait(false);
                    else if (UsageChangeMonitor.IsUsageNotification(root)) _events.Writer.TryWrite(root.Clone());
                }
                else if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && _requests.TryGetValue(value, out var completion))
                {
                    if (root.TryGetProperty("error", out var error)) completion.TrySetException(HostRpcException.FromError(error));
                    else completion.TrySetResult(root.GetProperty("result").Clone());
                }
            }
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
        finally
        {
            _lifetime.Cancel(); _events.Writer.TryComplete();
            foreach (var completion in _requests.Values) completion.TrySetException(new IOException("Usage connection closed."));
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        }
        finally { if (_reader is not null) await _reader.ConfigureAwait(false); _process.Dispose(); }
    }
}
