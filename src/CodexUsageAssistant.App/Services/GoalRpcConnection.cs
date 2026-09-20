using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexUsageAssistant.Services;

internal interface IGoalRpcConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token);
    Task ReplyAsync(JsonElement id, object? result, bool error, CancellationToken token);
    IAsyncEnumerable<JsonElement> Events(CancellationToken token);
}

internal sealed class GoalRpcConnection : IGoalRpcConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly Channel<JsonElement> _events = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    private Task? _reader;
    private int _nextId;
    public bool IsConnected => !_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open;

    internal static async Task<IGoalRpcConnection> ConnectAsync(int port, CancellationToken token)
    {
        var connection = new GoalRpcConnection();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            connection._socket.Options.Proxy = null;
            await connection._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token).ConfigureAwait(false);
            connection._reader = connection.ReadAsync();
            await connection.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex_ezmate_goals", title = "Codex EzMate", version = "1.21.4" },
                capabilities = new { experimentalApi = true }
            }, timeout.Token).ConfigureAwait(false);
            await connection.SendAsync(new { method = "initialized", @params = new { } }, timeout.Token).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (HostRpcException ex) { throw ex.ForMethod(method); }
        finally { _requests.TryRemove(id, out _); }
    }

    public Task ReplyAsync(JsonElement id, object? result, bool error, CancellationToken token) =>
        error ? SendAsync(new { id, error = new { code = -32601, message = "Client capability unavailable" } }, token)
            : SendAsync(new { id, result }, token);

    public IAsyncEnumerable<JsonElement> Events(CancellationToken token) => _events.Reader.ReadAllAsync(token);

    private async Task SendAsync(object value, CancellationToken token)
    {
        await _send.WaitAsync(token).ConfigureAwait(false);
        try { await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[8192];
            while (!_lifetime.IsCancellationRequested)
            {
                using var data = new MemoryStream(); ValueWebSocketReceiveResult part;
                do
                {
                    part = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Goal connection closed.");
                    if (data.Length + part.Count > 8 * 1024 * 1024) throw new IOException("Goal message is too large.");
                    data.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(data.ToArray()); var root = json.RootElement;
                if (!root.TryGetProperty("method", out var method))
                {
                    if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) && _requests.TryGetValue(number, out var request))
                    {
                        if (root.TryGetProperty("error", out var error))
                            request.TrySetException(HostRpcException.FromError(error));
                        else if (root.TryGetProperty("result", out var result)) request.TrySetResult(result.Clone());
                    }
                }
                else if (root.TryGetProperty("id", out _) || method.GetString() is
                    "turn/started" or "turn/completed" or "thread/goal/updated" or "thread/status/changed" or
                    "serverRequest/resolved" or "account/rateLimits/updated" or "account/updated" or "thread/tokenUsage/updated" ||
                    (method.GetString() is "item/started" or "item/completed") &&
                    GoalProtocol.Text(GoalProtocol.Property(GoalProtocol.Property(root, "params"), "item"), "type") == "fileChange")
                    await _events.Writer.WriteAsync(root.Clone(), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or JsonException or OperationCanceledException or InvalidOperationException) { failure = ex; }
        finally
        {
            _lifetime.Cancel();
            foreach (var request in _requests.Values) request.TrySetException(new IOException("Goal connection lost."));
            _events.Writer.TryComplete(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _socket.Abort();
        if (_reader is not null) await _reader.ConfigureAwait(false);
        _socket.Dispose();
    }
}
