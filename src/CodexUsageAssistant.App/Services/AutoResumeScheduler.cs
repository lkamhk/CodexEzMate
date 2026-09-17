using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class AutoResumeScheduler : IAutoResumeScheduler, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResetSafetyDelay = TimeSpan.FromMinutes(1);
    private readonly IUsageLoginService _usage;
    private readonly IUsageCacheService _cache;
    private readonly IAutoResumeSettingsService _settings;
    private readonly IGoalResumeService _goalResume;
    private readonly ITrayNotificationService _notifications;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private readonly SemaphoreSlim _evaluation = new(1, 1);
    private int _requested;
    private DateTimeOffset _lastEvaluation;
    public void RequestCheck() => Interlocked.Exchange(ref _requested, 1);

    public DateTimeOffset? NextCheckAt { get; private set; }
    public string Status { get; private set; } = LocalizationService.Pick("監控未啟用", "Monitoring is disabled");
    public event EventHandler? StatusChanged;
    public event Action<UsageData>? UsageUpdated;

    public AutoResumeScheduler(IUsageLoginService usage, IUsageCacheService cache,
        IAutoResumeSettingsService settings, IGoalResumeService goalResume,
        ITrayNotificationService notifications)
    {
        _usage = usage;
        _cache = cache;
        _settings = settings;
        _goalResume = goalResume;
        _notifications = notifications;
        if (goalResume is not null) { goalResume.Changed += OnExecutionChanged; goalResume.UsageChanged += RequestCheck; }
        _goalResume = goalResume!;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        NextCheckAt = settings.NextCheckAt;
        if (settings.MonitorEnabled) StartLoop();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _settings.UpdateAsync(settings => settings.MonitorEnabled = true, cancellationToken);
        SetStatus(L("Goal 自動恢復監控已啟用", "Goal auto-resume monitoring enabled"));
        StartLoop();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        await _settings.UpdateAsync(settings => { settings.MonitorEnabled = false; settings.NextCheckAt = null; }, cancellationToken);
        NextCheckAt = null;
        SetStatus(L("Goal 自動恢復監控已停止", "Goal auto-resume monitoring stopped"));
    }

    public Task<AutoResumeRunResult> RunNowAsync(CancellationToken cancellationToken) => EvaluateAsync(forceResumeCheck: true, cancellationToken);

    private void StartLoop()
    {
        if (_loopTask is { IsCompleted: false } && _loopCts?.IsCancellationRequested == false) return;
        _loopCts?.Dispose();
        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_loopCts.Token);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        var forceFirstCheck = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await EvaluateAsync(forceFirstCheck, token);
                forceFirstCheck = false;
                while (DateTimeOffset.Now < (NextCheckAt ?? DateTimeOffset.Now.Add(PollInterval)))
                {
                    await Task.Delay(2000, token);
                    if (Volatile.Read(ref _requested) != 0 && DateTimeOffset.UtcNow - _lastEvaluation >= TimeSpan.FromSeconds(10))
                    { Interlocked.Exchange(ref _requested, 0); break; }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<AutoResumeRunResult> EvaluateAsync(bool forceResumeCheck, CancellationToken token)
    {
        await _evaluation.WaitAsync(token);
        try { return await EvaluateCoreAsync(token); }
        finally { _evaluation.Release(); }
    }

    private async Task<AutoResumeRunResult> EvaluateCoreAsync(CancellationToken token)
    {
        _lastEvaluation = DateTimeOffset.UtcNow;
        var results = new List<ConversationResumeResult>();
        try
        {
            var settings = await _settings.LoadAsync(token);
            var usage = await _goalResume.ReadUsageAsync(token);
            // This is the same App Server account used for background execution.
            if (usage.Status == UsageStatus.Available) UsageUpdated?.Invoke(usage);
            if (usage.Status == UsageStatus.Available && HasDisplayableUsage(usage)) await _cache.SaveAsync(usage, token);
            if (!GoalProtocol.UsageAllowsResume(usage))
            {
                NextCheckAt = IsUsageLimited(usage) ? CalculateNextCheck(usage, DateTimeOffset.Now) : DateTimeOffset.Now.Add(PollInterval);
                await _settings.UpdateAsync(value => { value.NextCheckAt = NextCheckAt; value.AwaitingReset = true; }, token);
                SetStatus(L("等待 App Server 確認額度可用。", "Waiting for App Server to confirm available quota."));
                return new(Status, NextCheckAt, results);
            }
            if (!_goalResume.HasActiveWork)
                foreach (var target in settings.Conversations.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ThreadId) &&
                    (x.NextResumeAt is null || x.NextResumeAt <= DateTimeOffset.Now)))
                {
                    token.ThrowIfCancellationRequested();
                    var result = await _goalResume.ResumeAsync(target, token);
                    results.Add(result);
                    if (settings.NotifyOnFailure && target.LastStatus != result.Status && result.Status is ConversationResumeStatus.Failed or ConversationResumeStatus.Unsupported or ConversationResumeStatus.NotFound)
                        _notifications.ShowNotification(L("Goal 背景接手需要處理", "Goal takeover needs attention"), target.Title + ": " + result.Message);
                    await _settings.UpdateAsync(value =>
                    {
                        var saved = value.Conversations.FirstOrDefault(x => x.ThreadId == target.ThreadId);
                        if (saved is null) return;
                        saved.LastStatus = result.Status; saved.LastMessage = result.Message; saved.LastAttemptAt = result.AttemptedAt;
                        saved.GoalStatus = target.GoalStatus; saved.Cwd = target.Cwd;
                        var current = _goalResume.State;
                        if (current.ThreadId == target.ThreadId && result.Status is ConversationResumeStatus.Resumed or ConversationResumeStatus.Resuming or ConversationResumeStatus.PendingConfirmation or ConversationResumeStatus.AwaitingApproval)
                        { saved.GoalStatus = current.GoalStatus; saved.LastStatus = current.Status; saved.LastMessage = current.Message; }
                    }, token);
                    if (_goalResume.HasActiveWork) break;
                }
            NextCheckAt = DateTimeOffset.Now.Add(PollInterval);
            await _settings.UpdateAsync(value => { value.NextCheckAt = NextCheckAt; value.AwaitingReset = false; }, token);
            SetStatus(_goalResume.HasActiveWork ? _goalResume.State.Message : L("檢查完成；只自動恢復已勾選且因額度停止的 Goal。", "Check complete; only selected usage-limited Goals resume automatically."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        {
            NextCheckAt = DateTimeOffset.Now.Add(PollInterval);
            SetStatus(L("背景檢查失敗，5 分鐘後重試；沒有前台操作。", "Background check failed; retrying in 5 minutes without foreground interaction."));
        }
        return new(Status, NextCheckAt, results);
    }

    private async void OnExecutionChanged()
    {
        var state = _goalResume.State;
        SetStatus(state.Message);
        if (state.ThreadId is null) return;
        try
        {
            await _settings.UpdateAsync(value =>
            {
                var target = value.Conversations.FirstOrDefault(x => x.ThreadId == state.ThreadId);
                if (target is null) return;
                target.LastStatus = state.Status; target.LastMessage = state.Message;
                if (state.TurnId is not null) target.LastTurnId = state.TurnId;
                target.GoalStatus = state.GoalStatus;
                target.LastAttemptAt = DateTimeOffset.Now;
                if (state.Status == ConversationResumeStatus.WaitingForReset) target.NextResumeAt = DateTimeOffset.Now.Add(PollInterval);
            }, CancellationToken.None);
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
        if (!_goalResume.HasActiveWork) RequestCheck();
    }

    internal static DateTimeOffset CalculateNextCheck(UsageData usage, DateTimeOffset now)
    {
        var resets = new List<DateTimeOffset>();
        if (usage.FiveHourRemainingPercent is <= 0 && usage.FiveHourResetAt is { } five) resets.Add(five);
        if (usage.WeeklyRemainingPercent is <= 0 && usage.WeeklyResetAt is { } weekly) resets.Add(weekly);
        if (resets.Count == 0) return now.Add(PollInterval);
        var next = resets.Max().Add(ResetSafetyDelay);
        return next <= now ? now.Add(PollInterval) : next;
    }

    internal static TimeSpan CalculateResumeRetryDelay(int retryAttempt) => retryAttempt switch
    {
        <= 0 => TimeSpan.FromMinutes(2),
        1 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10)
    };

    internal static bool HasCompleteUsage(UsageData usage) =>
        usage.FiveHourRemainingPercent.HasValue && usage.WeeklyRemainingPercent.HasValue;

    internal static bool HasDisplayableUsage(UsageData usage) =>
        usage.FiveHourRemainingPercent.HasValue || usage.WeeklyRemainingPercent.HasValue;

    internal static bool IsUsageLimited(UsageData usage) =>
        usage.OrdinaryUsageAllowed == false || usage.FiveHourRemainingPercent is <= 0 || usage.WeeklyRemainingPercent is <= 0;

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string L(string traditionalChinese, string english) =>
        LocalizationService.Pick(traditionalChinese, english);

    public void Dispose()
    {
        if (_goalResume is not null) { _goalResume.Changed -= OnExecutionChanged; _goalResume.UsageChanged -= RequestCheck; }
        _loopCts?.Cancel();
        _loopCts?.Dispose();
    }
}
