using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CodexUsageAssistant.Services;

public sealed class SingleInstanceActivation : IDisposable
{
    public const string SessionBrowserArgument = "--session-browser";
    private const string OpenCommand = "session-browser";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;

    public bool IsPrimary { get; }

    public SingleInstanceActivation() : this(DefaultScope()) { }

    internal SingleInstanceActivation(string scope)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..24];
        _pipeName = "CodexEzMate.Activation." + suffix;
        // Keeping the handle alive elects one primary without thread-affine ownership.
        _mutex = new Mutex(false, @"Local\" + _pipeName, out var created);
        IsPrimary = created;
    }

    private static string DefaultScope()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"{identity.User?.Value ?? Environment.UserName}:{process.SessionId}";
    }

    public void Listen(Func<Task<bool>> open)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException("Only the primary instance can listen once.");
        _listener = ListenAsync(open);
    }

    public async Task<bool> ForwardAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            var greeting = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (!uint.TryParse(greeting, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return false;
            // The process launched by the user's click delegates foreground permission.
            AllowSetForegroundWindow(pid);
            await writer.WriteLineAsync(OpenCommand.AsMemory(), timeout.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) == "ok";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Func<Task<bool>> open)
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                await writer.WriteLineAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture).AsMemory(), timeout.Token).ConfigureAwait(false);
                // Read a fixed command length so malformed input cannot grow memory indefinitely.
                var command = new char[OpenCommand.Length + 1];
                var length = 0;
                var terminated = false;
                while (length < command.Length)
                {
                    var read = await reader.ReadAsync(command.AsMemory(length, 1), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (command[length] == '\n') { terminated = true; break; }
                    length++;
                }
                var accepted = terminated && new string(command, 0, length) == OpenCommand
                    && await open().WaitAsync(timeout.Token).ConfigureAwait(false);
                await writer.WriteLineAsync((accepted ? "ok" : "busy").AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                try { await Task.Delay(100, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The listener observes cancellation before a replacement instance starts.
        try { _listener?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
