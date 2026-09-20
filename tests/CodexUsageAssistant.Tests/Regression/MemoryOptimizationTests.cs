using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class MemoryOptimizationTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "ezmate-memory-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(Root, "sessions", "rollout-01900000-0000-7000-8000-000000000003.jsonl");
        public Fixture() => Directory.CreateDirectory(Path.Combine(Root, "sessions"));
        public void Write(params object[] records) => File.WriteAllText(FilePath, string.Join("\n", records.Select(x => JsonSerializer.Serialize(x))) + "\n", new UTF8Encoding(false));
        public void Dispose() { GoalLocalObservation.Forget(FilePath); Directory.Delete(Root, true); }
    }
    private static object Message(string text, string role = "user") => new { type = "response_item", timestamp = "2026-09-20", payload = new { type = "message", role, content = new[] { new { text } } } };
    private static object Usage(DateTimeOffset time, long current, long last) => new { type = "event_msg", timestamp = time.ToString("O"), payload = new { type = "token_count", info = new { total_token_usage = new { total_tokens = current }, last_token_usage = new { total_tokens = last } } } };

    [Fact]
    public void PagedPreviewReassemblesLongUnicodeMessagesWithoutLoss()
    {
        using var f = new Fixture(); var content = string.Concat(Enumerable.Repeat("繁體😀\n", 50000)); f.Write(Message(content), Message("second", "assistant"));
        var store = new SessionBrowserStore(f.Root, f.Root); var session = store.Scan(default).Sessions.Single();
        var expected = string.Concat(store.ReadPreview(session, default).Messages.Select(m => $"{m.Role.ToUpperInvariant()}  {m.Timestamp}\n{m.Text}\n\n"));
        var builder = new StringBuilder(); BrowserPreviewCursor? cursor = null; var pages = 0;
        do
        {
            var page = store.ReadPreviewPage(session, cursor, default); Assert.InRange(page.Text.Length, 1, SessionBrowserStore.PreviewPageCharacters);
            Assert.False(char.IsHighSurrogate(page.Text[^1])); builder.Append(page.Text); cursor = page.Next; pages++;
        } while (cursor is not null);
        Assert.True(pages > 1); Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void PreviewReloadsWhenFileChangesAndClearsCachedBody()
    {
        using var f = new Fixture(); f.Write(Message(new string('x', 90000)));
        var store = new SessionBrowserStore(f.Root, f.Root); var session = store.Scan(default).Sessions.Single();
        var first = store.ReadPreviewPage(session, null, default); Assert.NotNull(first.Next);
        f.Write(Message("changed")); var changed = store.ReadPreviewPage(session, first.Next, default);
        Assert.True(changed.Reloaded); Assert.Contains("changed", changed.Text); Assert.Null(changed.Next);
        store.ClearCaches(); Assert.Contains("changed", store.ReadPreviewPage(session, null, default).Text);
    }

    [Fact]
    public void PreviewFallbackMatchesOldReaderAndIgnoresPartialJson()
    {
        using var f = new Fixture(); f.Write(new { type = "event_msg", payload = new { type = "user_message", message = "fallback" } }, Message("  "));
        File.AppendAllText(f.FilePath, "{unfinished", Encoding.UTF8);
        var store = new SessionBrowserStore(f.Root, f.Root); var session = store.Scan(default).Sessions.Single();
        Assert.Contains("fallback", store.ReadPreviewPage(session, null, default).Text);
    }

    [Fact]
    public void IncrementalReaderSkipsUnchangedBytesAndRetriesPartialUtf8()
    {
        using var f = new Fixture(); var reader = new IncrementalJsonlReader(); var values = new List<string>(); var resets = 0;
        File.WriteAllText(f.FilePath, "{\"value\":\"one\"}\n", new UTF8Encoding(false));
        void Read() => reader.Read(f.FilePath, (json, _, _) => values.Add(json.GetProperty("value").GetString()!), () => { values.Clear(); resets++; }, default);
        Read(); var bytes = reader.BytesRead; Read(); Assert.Equal(bytes, reader.BytesRead); Assert.Equal(1, resets);
        var partial = Encoding.UTF8.GetBytes("{\"value\":\"漢😀\"}\n");
        using (var file = new FileStream(f.FilePath, FileMode.Append)) file.Write(partial.AsSpan(0, partial.Length - 4));
        Read(); Assert.Single(values);
        using (var file = new FileStream(f.FilePath, FileMode.Append)) file.Write(partial.AsSpan(partial.Length - 4));
        Read(); Assert.Equal(new[] { "one", "漢😀" }, values);
        File.WriteAllText(f.FilePath, "{\"value\":\"replacement\"}\n", new UTF8Encoding(false)); Read(); Assert.Equal(new[] { "replacement" }, values);
    }

    [Fact]
    public void IncrementalTokenTotalsMatchReferenceAcrossAppendForkTruncateAndDayChange()
    {
        using var f = new Fixture(); var now = DateTimeOffset.Now;
        f.Write(Usage(now.AddMinutes(-2), 200, 20), Usage(now.AddMinutes(-1), 240, 40));
        void Match()
        {
            var seen = new HashSet<string>(); long sum = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(f.Root, "sessions"))) sum += LocalTokenUsageService.CountToday(File.ReadLines(file), now, seen, default) ?? 0;
            Assert.Equal(sum, LocalTokenUsageService.ReadToday(f.Root, now, default));
        }
        Match(); Match();
        File.AppendAllText(f.FilePath, JsonSerializer.Serialize(Usage(now, 270, 30)) + "\n"); Match();
        File.Copy(f.FilePath, Path.Combine(f.Root, "sessions", "fork.jsonl")); Match();
        f.Write(Usage(now, 15, 15)); Match();
        File.Delete(Path.Combine(f.Root, "sessions", "fork.jsonl")); Match();
        now = now.AddDays(1); f.Write(Usage(now, 30, 30)); File.SetLastWriteTimeUtc(f.FilePath, now.UtcDateTime); Match();
    }

    [Fact]
    public async Task GoalCursorPreservesEvidenceWhenOriginalMessageFallsOutsideTail()
    {
        using var f = new Fixture(); const string id = "01900000-0000-7000-8000-000000000003";
        f.Write(new { type = "event_msg", payload = new { type = "task_started", turn_id = "turn" } }, Message("[Codex EzMate marker]\nContinue"));
        var before = await GoalLocalObservation.ReadAsync(f.FilePath, id, "marker", "fingerprint", default); Assert.True(before.RequestSeen); Assert.True(before.IsBusy);
        File.AppendAllText(f.FilePath, JsonSerializer.Serialize(Message(new string('x', 3 * 1024 * 1024))) + "\n");
        var after = await GoalLocalObservation.ReadAsync(f.FilePath, id, "marker", "fingerprint", default); Assert.True(after.RequestSeen); Assert.True(after.IsBusy);
        f.Write(new { type = "event_msg", payload = new { type = "task_complete", turn_id = "turn" } });
        after = await GoalLocalObservation.ReadAsync(f.FilePath, id, "marker", "fingerprint", default); Assert.False(after.RequestSeen); Assert.False(after.IsBusy);
    }

    private sealed class Options : ISettingsService
    {
        internal WindowPosition Value = new();
        public Task<WindowPosition?> LoadAsync(CancellationToken token) => Task.FromResult<WindowPosition?>(JsonSerializer.Deserialize<WindowPosition>(JsonSerializer.Serialize(Value)));
        public Task SaveAsync(WindowPosition value, CancellationToken token) { Value = value; return Task.CompletedTask; }
    }
    private sealed class Connection : IGoalRpcConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public bool IsConnected { get; private set; } = true;
        public int Reads;
        public void Emit(object value) => _events.Writer.TryWrite(JsonSerializer.SerializeToElement(value));
        public async Task<JsonElement> RequestAsync(string method, object? p, CancellationToken token)
        { Interlocked.Increment(ref Reads); await Task.Delay(10, token); return JsonSerializer.SerializeToElement(new { method, value = p }); }
        public Task ReplyAsync(JsonElement id, object? result, bool error, CancellationToken token) => Task.CompletedTask;
        public IAsyncEnumerable<JsonElement> Events(CancellationToken token) => _events.Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() { IsConnected = false; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task SharedUsageConnectionReusesTransportAndReplacesItWhenSettingsChange()
    {
        var options = new Options(); var connections = new List<Connection>();
        await using var session = new UsageAppServerSession(options, null, (_, _, _) => { var connection = new Connection(); connections.Add(connection); return Task.FromResult<IGoalRpcConnection>(connection); });
        var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => session.RequestAsync(null, "read", i, default)));
        Assert.Single(connections); Assert.Equal(20, connections[0].Reads);
        Assert.Equal(Enumerable.Range(0, 20), replies.Select(x => x.GetProperty("value").GetInt32()));
        options.Value.ProxyServer = "http://127.0.0.1:9999"; options.Value.ProxyEnabled = true;
        await session.RequestAsync(null, "read", 20, default); Assert.Equal(2, connections.Count); Assert.False(connections[0].IsConnected);
    }

    [Fact]
    public async Task SharedUsageCoalescesDuplicateNotificationsAndUnsubscribes()
    {
        var connection = new Connection(); var changes = 0;
        await using var session = new UsageAppServerSession(new Options(), null, (_, _, _) => Task.FromResult<IGoalRpcConnection>(connection));
        void Changed() => Interlocked.Increment(ref changes);
        session.Subscribe(Changed); await session.RequestAsync(null, "read", null, default);
        connection.Emit(new { method = "account/rateLimits/updated", @params = new { value = 1 } });
        connection.Emit(new { method = "account/rateLimits/updated", @params = new { value = 1 } });
        await Task.Delay(60); Assert.Equal(1, changes);
        connection.Emit(new { method = "account/updated" }); await Task.Delay(60); Assert.Equal(2, changes);
        session.Unsubscribe(Changed);
        connection.Emit(new { method = "account/rateLimits/updated", @params = new { value = 2 } });
        await Task.Delay(60); Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ExplicitSignInCanUseFreshTransportWithoutRetainingPreviousAccountConnection()
    {
        var connections = new List<Connection>();
        await using var shared = new UsageAppServerSession(new Options(), null, (_, port, _) =>
        { Assert.Null(port); var connection = new Connection(); connections.Add(connection); return Task.FromResult<IGoalRpcConnection>(connection); });
        await shared.RequestAsync(null, "read", null, default);
        await shared.InvalidateAsync(default);
        await using var fresh = shared.CreateFresh(); await fresh.RequestAsync(null, "read", null, default);
        Assert.Equal(2, connections.Count); Assert.False(connections[0].IsConnected); Assert.True(connections[1].IsConnected);
    }

    private sealed class HostProcess : IHostProcess
    {
        public bool HasExited { get; private set; }
        public int Id => 1;
        public void Stop() => HasExited = true;
        public void Dispose() { }
    }
    private sealed class HostRuntime(bool external = false) : IAppServerHostRuntime
    {
        internal HostProcess? Process;
        public Task<HostProbe> ProbeAsync(int port, CancellationToken token) => Task.FromResult(external || Process is { HasExited: false } ? HostProbe.Ready : HostProbe.Missing);
        public IHostProcess Start(WindowPosition settings) => Process = new();
    }

    [Fact]
    public async Task SharedUsageBorrowsOnlyCompatibleOwnedHostAndNeverStopsIt()
    {
        using var f = new Fixture(); var executable = Path.Combine(f.Root, "codex.exe"); File.WriteAllText(executable, "fixture");
        var options = new Options { Value = new() { HostExecutablePath = executable, CodexExecutablePath = executable, HostPort = 4521 } };
        var runtime = new HostRuntime();
        await using var host = new CodexAppServerHost(options, runtime, Path.Combine(f.Root, "locks"), Path.Combine(f.Root, "logs", "host.log"));
        await host.ApplySettingsAsync(options.Value);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (host.State != AppServerHostState.Ready) await Task.Delay(20, timeout.Token);
        int? selectedPort = null;
        await using (var usage = new UsageAppServerSession(options, host, (_, port, _) => { selectedPort = port; return Task.FromResult<IGoalRpcConnection>(new Connection()); }))
            await usage.RequestAsync(null, "read", null, default);
        Assert.Equal(4521, selectedPort); Assert.False(runtime.Process!.HasExited);
        options.Value.ProxyEnabled = true; options.Value.ProxyServer = "http://127.0.0.1:9999";
        Assert.Null(host.CompatibleUsagePort(options.Value));
    }

    [Fact]
    public async Task LiveReadOnlyTransport_WhenEnabled_MultiplexesTwentyRequestsOnOneProcess()
    {
        if (Environment.GetEnvironmentVariable("CODEX_TEST_SHARED_USAGE_TRANSPORT") != "1") return;
        var settings = new Options { Value = new() { CodexExecutablePath = AppServerUsageService.FindExecutable(null) } };
        var count = 0;
        await using var usage = new UsageAppServerSession(settings, null, (options, _, token) => { count++; return StdioUsageConnection.ConnectAsync(options, token); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => usage.RequestAsync(null,
            i % 2 == 0 ? "thread/loaded/list" : "account/read", i % 2 == 0 ? (object)new { } : new { refreshToken = false }, timeout.Token)));
        Assert.Equal(1, count);
        for (var i = 0; i < replies.Length; i++) Assert.True(replies[i].TryGetProperty(i % 2 == 0 ? "data" : "account", out _));
    }
}
