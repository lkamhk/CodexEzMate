using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace CodexUsageAssistant.Services;

internal enum HostRpcFailure { Unknown, ActiveWriter }

internal sealed class HostRpcException(int code, HostRpcFailure failure = HostRpcFailure.Unknown, string? method = null) : Exception("App Server RPC failed.")
{
    internal int Code { get; } = code;
    internal HostRpcFailure Failure { get; } = failure;
    internal string? Method { get; } = method;
    internal HostRpcException ForMethod(string value) => new(Code, Failure, value);

    internal static HostRpcException FromError(JsonElement error, string? method = null)
    {
        var code = error.TryGetProperty("code", out var item) && item.TryGetInt32(out var number) ? number : -1;
        var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        // Classify a known server condition without retaining arbitrary error text or credentials.
        var failure = message.StartsWith("thread ", StringComparison.OrdinalIgnoreCase) && message.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase)
            ? HostRpcFailure.ActiveWriter : HostRpcFailure.Unknown;
        return new(code, failure, method);
    }
}

internal sealed class HostWebSocketClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private int _id;
    private CancellationToken _connectionBudget;
    private readonly Queue<JsonElement> _notifications = new();

    internal static async Task<HostWebSocketClient> ConnectAsync(int port, CancellationToken token)
    {
        var client = new HostWebSocketClient();
        client._connectionBudget = token;
        try
        {
            client._socket.Options.Proxy = null;
            await client._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), token);
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex_usage_assistant_host", title = "Codex EzMate", version = "1.21.3" }
            }, token);
            await client.SendAsync(new { method = "initialized", @params = new { } }, token);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    internal async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = ++_id;
        await SendAsync(new { id, method, @params = parameters }, token);
        while (true)
        {
            var root = await ReceiveAsync(token);
            if (root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var requestId))
            {
                await SendAsync(new { id = requestId, error = new { code = -32601, message = "Unsupported client method" } }, token);
                continue;
            }
            if (!root.TryGetProperty("id", out var responseId))
            {
                if (_notifications.Count < 32) _notifications.Enqueue(root);
                continue;
            }
            if (!responseId.TryGetInt32(out var value) || value != id) continue;
            if (root.TryGetProperty("error", out var error))
                throw HostRpcException.FromError(error, method);
            return root.GetProperty("result").Clone();
        }
    }

    internal async Task<bool> WaitForLoginAsync(string loginId, CancellationToken token)
    {
        while (true)
        {
            var root = _notifications.TryDequeue(out var pending) ? pending : await ReceiveAsync(token);
            if (!root.TryGetProperty("method", out var method) || method.GetString() != "account/login/completed" ||
                !root.TryGetProperty("params", out var parameters)) continue;
            if (parameters.TryGetProperty("loginId", out var id) && id.GetString() == loginId)
                return parameters.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Unexpected WebSocket message.");
            if (data.Length + result.Count > 4 * 1024 * 1024) throw new IOException("WebSocket message too large.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(data.ToArray());
        return json.RootElement.Clone();
    }

    private Task SendAsync(object message, CancellationToken token) =>
        _socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, token);

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_connectionBudget);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            if (_socket.State == WebSocketState.Open) await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally { _socket.Dispose(); }
    }
}
