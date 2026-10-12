using System.IO;
using System.Net.Http;
using System.Text.Json;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class ResetWatchService : IDisposable
{
    private readonly IResetFeedClient _client;
    private readonly IResetWatchStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private int _started;
    private bool _initialized;
    private bool _disposed;
    private int _failures;
    private DateTimeOffset? _retryNotBefore;
    public ResetWatchSettings Settings { get; private set; } = new();
    public ResetWatchState State { get; private set; } = new();
    public string? Error { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public bool IsRefreshing { get; private set; }
    public event Action? Changed;
    public event Action<IReadOnlyList<ResetAnnouncement>>? AnnouncementsAvailable;
    public event Action? UsageRefreshRequested;

    public ResetWatchService(IResetFeedClient client, IResetWatchStore store, TimeProvider? timeProvider = null)
    { _client = client; _store = store; _time = timeProvider ?? TimeProvider.System; }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            try
            {
                var settings = await _store.LoadSettingsAsync(token).ConfigureAwait(false) ?? new();
                if (string.Equals(settings.FeedUrl?.TrimEnd('/'), ResetWatchSettings.LegacyFeedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    settings = settings with { FeedUrl = ResetWatchSettings.DefaultFeedUrl };
                    await _store.SaveSettingsAsync(settings, token).ConfigureAwait(false);
                }
                ValidateSettings(settings);
                Settings = settings;
            }
            catch (Exception ex) when (IsExpected(ex) && !token.IsCancellationRequested) { Settings = new(); Error = "settings"; }
            try
            {
                var state = await _store.LoadStateAsync(token).ConfigureAwait(false) ?? new();
                if (state.Seen is null || state.Seen.Count > 2048 || state.Seen.Any(x => !ResetFeedClient.ValidId(x.Key)
                    || x.Value is null || x.Value.Revision <= 0 || x.Value.ObservedAt > _time.GetUtcNow().AddMinutes(5))
                    || state.ETag?.Length > 1024) throw new InvalidDataException("Invalid local revision ledger.");
                if (state.Feed is not null) ResetFeedClient.Validate(state.Feed, _time.GetUtcNow());
                State = state.FeedUrl == Settings.FeedUrl ? state : new();
            }
            catch (Exception ex) when (IsExpected(ex) && !token.IsCancellationRequested) { State = new(); Error = "cache"; }
            _initialized = true;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task StartAsync()
    {
        await InitializeAsync(_lifetime.Token).ConfigureAwait(false);
        if (!_disposed && Interlocked.Exchange(ref _started, 1) == 0) _loop = Task.Run(RunAsync);
    }

    public static void ValidateSettings(ResetWatchSettings settings)
    {
        if (settings.FeedUrl is null || settings.PollIntervalMinutes is < 5 or > 60
            || settings.PopupAutoCloseSeconds is < 1 or > 3600
            || (!string.IsNullOrEmpty(settings.FeedUrl) && !ResetFeedClient.TryGetHttpsUri(settings.FeedUrl, out _))
            || (settings.Enabled && string.IsNullOrWhiteSpace(settings.FeedUrl)))
            throw new InvalidDataException("Use an HTTPS feed URL, a polling interval of 5–60 minutes, and a popup timeout of 1–3600 seconds when enabled.");
    }

    public async Task SaveSettingsAsync(ResetWatchSettings settings, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        await InitializeAsync(token).ConfigureAwait(false);
        settings = settings with { FeedUrl = settings.FeedUrl?.Trim() ?? "" };
        ValidateSettings(settings);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _store.SaveSettingsAsync(settings, token).ConfigureAwait(false);
            if (Settings.FeedUrl != settings.FeedUrl) { State = new(); LastAttemptAt = null; }
            Settings = settings;
            Error = null;
            _failures = 0;
            _retryNotBefore = null;
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = linked.Token;
        if (!_initialized) await InitializeAsync(token).ConfigureAwait(false);
        // The poller and manual checks must not overlap or emit duplicate notices.
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        ResetNotificationBatch? batch = null;
        try
        {
            if (_disposed || !Settings.Enabled || !ResetFeedClient.TryGetHttpsUri(Settings.FeedUrl, out var address)) return;
            if (_retryNotBefore > _time.GetUtcNow()) return;
            IsRefreshing = true;
            LastAttemptAt = _time.GetUtcNow();
            Changed?.Invoke();
            var response = await _client.FetchAsync(address, State.ETag, State.LastModified, token).ConfigureAwait(false);
            var feed = response.NotModified ? State.Feed : response.Feed;
            if (feed is null) throw new InvalidDataException("HTTP 304 requires a valid cached feed.");
            var now = _time.GetUtcNow();
            if (CodexResetsApiAdapter.Matches(address) && feed.FeedId == CodexResetsApiAdapter.FeedId)
            {
                if (response.NotModified) feed = CodexResetsApiAdapter.Revalidated(feed, now);
                feed = CodexResetsApiAdapter.Reconcile(feed, State);
            }
            ResetFeedClient.Validate(feed, now);
            if (State.Feed is { } old && old.FeedId == feed.FeedId)
            {
                if (feed.GeneratedAt < old.GeneratedAt) throw new InvalidDataException("Feed generation time moved backwards.");
                foreach (var item in feed.Events)
                {
                    if (State.Seen.TryGetValue(item.Id, out var known) && known.Revision > item.Revision)
                        throw new InvalidDataException("Event revision moved backwards.");
                    var prior = old.Events.FirstOrDefault(x => x.Id == item.Id);
                    if (prior?.Revision == item.Revision && JsonSerializer.Serialize(prior, ResetFeedClient.JsonOptions)
                        != JsonSerializer.Serialize(item, ResetFeedClient.JsonOptions))
                        throw new InvalidDataException("Changed events must increase their revision.");
                }
            }
            batch = ResetNotificationCoordinator.Evaluate(State, feed, Settings, now);
            var next = new ResetWatchState { FeedUrl = Settings.FeedUrl, Feed = feed, Seen = batch.Seen,
                ETag = response.ETag, LastModified = response.LastModified, LastSuccessfulFetchAt = now };
            // Persist acknowledgement before delivery. Crash recovery favors no duplicate popups.
            await _store.SaveStateAsync(next, token).ConfigureAwait(false);
            State = next;
            Error = null;
            _failures = 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { batch = null; }
        catch (ResetFeedBackoffException ex)
        { _retryNotBefore = _time.GetUtcNow() + ex.Delay; Error = "fetch"; _failures = Math.Min(5, _failures + 1); batch = null; }
        catch (Exception ex) when (IsExpected(ex))
        { Error = ex is JsonException or InvalidDataException ? "invalid" : "fetch"; _failures = Math.Min(5, _failures + 1); batch = null; }
        finally { IsRefreshing = false; _gate.Release(); Changed?.Invoke(); }
        if (_disposed || token.IsCancellationRequested) return;
        if (batch?.Announcements.Count > 0) AnnouncementsAvailable?.Invoke(batch.Announcements);
        if (batch?.RefreshUsage == true) UsageRefreshRequested?.Invoke();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
                if (!Settings.Enabled) await _wake.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                else
                {
                    var minutes = Math.Min(60, Settings.PollIntervalMinutes * Math.Pow(2, _failures));
                    var delay = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Random.Shared.Next(0, 31));
                    if (_retryNotBefore - _time.GetUtcNow() is { } retry && retry > delay) delay = retry;
                    await _wake.WaitAsync(delay, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or HttpRequestException or OperationCanceledException or InvalidOperationException or NotSupportedException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
    }
}
