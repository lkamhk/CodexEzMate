using System.IO;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

internal interface ISessionNameWriter
{
    Task<string?> ReadAsync(string id, CancellationToken token);
    Task WriteAsync(string id, string name, CancellationToken token);
}

internal sealed class AppServerSessionNameWriter(UsageAppServerSession session) : ISessionNameWriter
{
    public async Task<string?> ReadAsync(string id, CancellationToken token)
    {
        var result = await session.RequestAsync(null, "thread/read", new { threadId = id, includeTurns = false }, token).ConfigureAwait(false);
        return SessionBrowserStore.Text(SessionBrowserStore.Property(SessionBrowserStore.Property(result, "thread"), "name"));
    }
    public async Task WriteAsync(string id, string name, CancellationToken token) =>
        _ = await session.RequestAsync(null, "thread/name/set", new { threadId = id, name }, token).ConfigureAwait(false);
}

public sealed class SessionAutoNamingService : IDisposable
{
    private sealed class Options
    {
        public bool Enabled { get; set; }
        public SessionNamingRules? Rules { get; set; }
        public HashSet<string> ExcludedIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PendingNameHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class WatchedFile(long cutoff)
    {
        internal long Cutoff = cutoff;
        internal SessionAutoNameObserver? Observer;
        internal long Length = -1;
        internal DateTime Written;
        internal DateTime Created;
        internal long Used;
    }
    internal sealed record Work(string Path, AutoNameCandidate Candidate, long Generation, long Revision);
    private readonly string _home, _settingsPath;
    private readonly ISessionNameWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, WatchedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private Options _options = new();
    private Task? _loop;
    private long _generation, _used;
    private bool _loaded;
    private DateTimeOffset _lastVerification;
    public bool Enabled => _options.Enabled;
    public string Status { get; private set; } = "off";
    public event Action? Changed;
    public event Action<string, string>? NameUpdated;
    public SessionAutoNamingService(UsageAppServerSession session) : this(new SessionBrowserStore(), new AppServerSessionNameWriter(session)) { }
    internal SessionAutoNamingService(SessionBrowserStore store, ISessionNameWriter writer)
    {
        _home = store.CodexHome; _writer = writer;
        _settingsPath = Path.Combine(Path.GetDirectoryName(store.SettingsPath)!, "session-auto-naming.json");
    }
    public bool IsExcluded(string id) => _options.ExcludedIds.Contains(id);

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try { await TickAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception ex) when (Expected(ex)) { Status = "failed"; Changed?.Invoke(); }
                try { await Task.Delay(2000, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private void Load()
    {
        if (_loaded) return;
        if (File.Exists(_settingsPath))
        {
            var saved = JsonSerializer.Deserialize<Options>(File.ReadAllText(_settingsPath)) ?? throw new InvalidDataException("Invalid naming settings.");
            saved.Rules?.Validate();
            saved.ExcludedIds = new((saved.ExcludedIds ?? []).Where(id => Guid.TryParse(id, out _)).Select(id => Guid.Parse(id).ToString("D")), StringComparer.OrdinalIgnoreCase);
            saved.PendingNameHashes = new(saved.PendingNameHashes ?? [], StringComparer.OrdinalIgnoreCase); _options = saved;
        }
        Baseline(); _loaded = true; Status = IdleStatus;
    }
    private IEnumerable<string> ActiveFiles()
    {
        var root = Path.Combine(_home, "sessions");
        return !Directory.Exists(root) ? [] : Directory.EnumerateFiles(root, "rollout-*.jsonl", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
    }
    private void Baseline()
    {
        _files.Clear();
        foreach (var path in ActiveFiles())
        {
            var info = new FileInfo(path);
            _files[path] = new(info.Length) { Length = info.Length, Written = info.LastWriteTimeUtc, Created = info.CreationTimeUtc };
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Load();
            var next = CopyOptions(); next.Enabled = enabled;
            await SaveAsync(next, token).ConfigureAwait(false);
            _options = next; _generation++; Baseline(); Status = IdleStatus;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task<SessionNamingConfiguration> GetConfigurationAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Load();
            return new(Enabled, _options.Rules?.Copy() ?? new(), _options.ExcludedIds.OrderBy(id => id).ToArray(), _options.Rules is null);
        }
        finally { _gate.Release(); }
    }

    public async Task ConfigureAsync(bool enabled, SessionNamingRules rules, CancellationToken token = default)
    {
        rules.Validate(); var copy = rules.Copy();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Load();
            var next = CopyOptions(); next.Enabled = enabled; next.Rules = copy;
            await SaveAsync(next, token).ConfigureAwait(false);
            _options = next; _generation++; Baseline(); Status = IdleStatus;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task SetExcludedAsync(IEnumerable<string> ids, bool excluded, CancellationToken token = default)
    {
        var targets = ids.Where(id => Guid.TryParse(id, out _)).Select(id => Guid.Parse(id).ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Load();
            var next = CopyOptions();
            foreach (var id in targets) { if (excluded) next.ExcludedIds.Add(id); else next.ExcludedIds.Remove(id); }
            await SaveAsync(next, token).ConfigureAwait(false);
            _options = next;
            foreach (var id in targets) _revisions[id] = _revisions.GetValueOrDefault(id) + 1;
            // Rebase pending bytes as well as queued work; restoring never replays an old completion.
            foreach (var path in ActiveFiles())
                if (targets.Any(id => Path.GetFileName(path).Contains(id, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!_files.TryGetValue(path, out var state)) _files[path] = state = new(0);
                    state.Cutoff = new FileInfo(path).Length;
                }
            Status = IdleStatus;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    private async Task SaveAsync(Options value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
            File.Move(temporary, _settingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Options CopyOptions() => new() { Enabled = Enabled, Rules = _options.Rules?.Copy(), ExcludedIds = new(_options.ExcludedIds, StringComparer.OrdinalIgnoreCase),
        PendingNameHashes = new(_options.PendingNameHashes, StringComparer.OrdinalIgnoreCase) };
    private string IdleStatus => _options.PendingNameHashes.Count > 0 ? "uncertain" : Enabled ? "watching" : "off";
    private static string NameHash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    private async Task RecordPendingAsync(string id, string? name, CancellationToken token)
    {
        var next = CopyOptions();
        if (name is null) next.PendingNameHashes.Remove(id); else next.PendingNameHashes[id] = NameHash(name);
        await SaveAsync(next, token).ConfigureAwait(false); _options = next;
    }

    internal async Task<IReadOnlyList<Work>> ObserveAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Load();
            if (!Enabled) return [];
            var work = new List<Work>();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in ActiveFiles())
            {
                token.ThrowIfCancellationRequested(); existing.Add(path);
                try
                {
                    var info = new FileInfo(path);
                    if (!_files.TryGetValue(path, out var state)) _files[path] = state = new(0);
                    if (state.Length == info.Length && state.Written == info.LastWriteTimeUtc) continue;
                    if (state.Observer is null && state.Length >= 0 && (state.Created != info.CreationTimeUtc || info.Length < state.Length)) state.Cutoff = info.Length;
                    if (state.Observer is null && _files.Values.Count(x => x.Observer is not null) >= 256)
                    {
                        var oldest = _files.Values.Where(x => x.Observer is not null).MinBy(x => x.Used)!;
                        oldest.Observer = null;
                    }
                    state.Observer ??= new(); state.Used = ++_used;
                    var candidate = state.Observer.Read(path, state.Cutoff, token);
                    state.Length = info.Length; state.Written = info.LastWriteTimeUtc; state.Created = info.CreationTimeUtc;
                    state.Cutoff = state.Observer.Offset;
                    if (candidate is not null && Path.GetFileName(path).Contains(candidate.ThreadId, StringComparison.OrdinalIgnoreCase) && !IsExcluded(candidate.ThreadId))
                        work.Add(new(path, candidate, _generation, _revisions.GetValueOrDefault(candidate.ThreadId)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "failed"; }
            }
            foreach (var path in _files.Keys.Where(p => !existing.Contains(p)).ToArray()) _files.Remove(path);
            return work;
        }
        finally { _gate.Release(); }
    }

    internal async Task ApplyAsync(Work work, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Enabled || IsExcluded(work.Candidate.ThreadId) || _options.PendingNameHashes.ContainsKey(work.Candidate.ThreadId) || work.Generation != _generation ||
                work.Revision != _revisions.GetValueOrDefault(work.Candidate.ThreadId) || !File.Exists(work.Path)) return;
            // New input may have arrived while this item waited behind another rename.
            var info = new FileInfo(work.Path);
            if (!_files.TryGetValue(work.Path, out var state) || state.Length != info.Length || state.Written != info.LastWriteTimeUtc) return;
            var id = work.Candidate.ThreadId;
            var name = _options.Rules?.Format(work.Candidate.Project, work.Candidate.FirstPrompt, work.Candidate.LatestPrompt, id) ?? work.Candidate.Name;
            if (string.IsNullOrWhiteSpace(name)) { Status = "empty"; return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var current = await _writer.ReadAsync(id, timeout.Token).ConfigureAwait(false);
                if (current != name)
                {
                    await RecordPendingAsync(id, name, token).ConfigureAwait(false);
                    try { await _writer.WriteAsync(id, name, timeout.Token).ConfigureAwait(false); }
                    catch (InvalidOperationException)
                    {
                        // Explicit RPC rejection is a known outcome, unlike a transport timeout.
                        await RecordPendingAsync(id, null, token).ConfigureAwait(false); throw;
                    }
                }
                if (await _writer.ReadAsync(id, timeout.Token).ConfigureAwait(false) != name) throw new InvalidDataException("Name was not confirmed.");
                await RecordPendingAsync(id, null, token).ConfigureAwait(false);
                Status = "updated"; NameUpdated?.Invoke(id, name);
            }
            catch (Exception ex) when (Expected(ex))
            {
                // Never resend an uncertain mutation. A fresh read may confirm its outcome.
                Status = "failed";
                if (!token.IsCancellationRequested)
                {
                    using var verify = CancellationTokenSource.CreateLinkedTokenSource(token); verify.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        if (await _writer.ReadAsync(id, verify.Token).ConfigureAwait(false) == name)
                        {
                            await RecordPendingAsync(id, null, token).ConfigureAwait(false);
                            Status = "updated"; NameUpdated?.Invoke(id, name);
                        }
                    }
                    catch (Exception check) when (Expected(check)) { }
                }
                if (_options.PendingNameHashes.ContainsKey(id)) Status = "uncertain";
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    internal async Task TickAsync(CancellationToken token)
    {
        foreach (var item in await ObserveAsync(token).ConfigureAwait(false)) await ApplyAsync(item, token).ConfigureAwait(false);
        await VerifyPendingAsync(token).ConfigureAwait(false);
        Changed?.Invoke();
    }
    private async Task VerifyPendingAsync(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow - _lastVerification < TimeSpan.FromSeconds(30)) return;
        _lastVerification = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var pair in _options.PendingNameHashes.Take(8).ToArray())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var name = await _writer.ReadAsync(pair.Key, timeout.Token).ConfigureAwait(false);
                    if (name is not null && NameHash(name) == pair.Value)
                    {
                        await RecordPendingAsync(pair.Key, null, token).ConfigureAwait(false);
                        NameUpdated?.Invoke(pair.Key, name); Status = IdleStatus;
                    }
                }
                catch (Exception ex) when (Expected(ex)) { }
            }
            if (_options.PendingNameHashes.Count > 0) Status = "uncertain";
        }
        finally { _gate.Release(); }
    }
    private static bool Expected(Exception ex) => CodexAppServerHost.IsExpected(ex) || ex is JsonException;
    public void Dispose()
    {
        _lifetime.Cancel();
        _loop?.GetAwaiter().GetResult();
        Changed = null; NameUpdated = null; _files.Clear();
    }
}
