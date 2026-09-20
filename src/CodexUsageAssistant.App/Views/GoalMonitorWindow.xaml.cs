using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Views;

public partial class GoalMonitorWindow : Window
{
    private readonly IAutoResumeSettingsService _autoResumeSettings;
    private readonly IConversationDiscoveryService _conversationDiscovery;
    private readonly IAutoResumeScheduler _autoResumeScheduler;
    private readonly ObservableCollection<ConversationTarget> _conversationTargets = [];
    private AutoResumeSettings _autoModel = new();
    private readonly IGoalResumeService? _goals;
    private bool _closed;
    private int _refreshSequence;
    public GoalMonitorWindow(IAutoResumeSettingsService settings, IConversationDiscoveryService discovery, IAutoResumeScheduler scheduler, IGoalResumeService? goals = null)
    {
        _autoResumeSettings = settings; _conversationDiscovery = discovery; _autoResumeScheduler = scheduler;
        _goals = goals;
        InitializeComponent();
        ConversationsGrid.ItemsSource = _conversationTargets;
        _autoResumeScheduler.StatusChanged += OnSchedulerStatusChanged;
        Closed += (_, _) => { _closed = true; _autoResumeScheduler.StatusChanged -= OnSchedulerStatusChanged;
            ConversationsGrid.ItemsSource = null; _conversationTargets.Clear(); _autoModel = new(); SelectedGoalResultText.Clear(); };
        if (_goals is not null) { _goals.Changed += OnGoalChanged; Closed += (_, _) => _goals.Changed -= OnGoalChanged; }
        Loaded += OnLoaded;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _autoModel = await _autoResumeSettings.LoadAsync(CancellationToken.None);
        if (_closed) { _autoModel = new(); return; }
        AutoResumeEnabledCheckBox.IsChecked = _autoModel.MonitorEnabled;
        _conversationTargets.Clear();
        foreach (var target in _autoModel.Conversations) _conversationTargets.Add(target);
        UpdateAutoResumeStatus(_autoResumeScheduler.Status);
        OnGoalChanged();
    }
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        await SaveAutoResumeSettingsAsync(AutoResumeEnabledCheckBox.IsChecked == true);
        if (AutoResumeEnabledCheckBox.IsChecked == true) await _autoResumeScheduler.StartAsync(CancellationToken.None);
        else await _autoResumeScheduler.StopAsync(CancellationToken.None);
        UpdateAutoResumeStatus(_autoResumeScheduler.Status);
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnGoalChanged()
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed) return;
            PauseGoalButton.IsEnabled = _goals?.HasActiveWork == true;
            if (_goals is null) return;
            var target = _conversationTargets.FirstOrDefault(x => x.ThreadId == _goals.State.ThreadId);
            if (target is not null)
            {
                target.LastStatus = _goals.State.Status; target.LastMessage = _goals.State.Message; target.LastTurnId = _goals.State.TurnId;
                target.GoalStatus = _goals.State.GoalStatus;
                if (!ConversationsGrid.IsKeyboardFocusWithin) ConversationsGrid.Items.Refresh();
            }
            if (!string.IsNullOrWhiteSpace(_goals.State.Message)) UpdateAutoResumeStatus(_goals.State.Message);
        }));
    }
    private async void OnPauseGoal(object sender, RoutedEventArgs e)
    {
        if (_goals is null) return;
        PauseGoalButton.IsEnabled = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _goals.PauseAsync(timeout.Token);
        OnGoalChanged();
    }
    private async void OnContinueGoal(object sender, RoutedEventArgs e)
    {
        if (_goals is null || ConversationsGrid.SelectedItem is not ConversationTarget target) return;
        var result = await _goals.ContinuePausedAsync(target, CancellationToken.None);
        target.LastStatus = result.Status; target.LastMessage = result.Message; target.LastAttemptAt = result.AttemptedAt;
        UpdateAutoResumeStatus(result.Message);
    }
    private async void OnScanConversationsClick(object sender, RoutedEventArgs e)
    {
        ScanConversationsButton.IsEnabled = false;
        AutoResumeStatusText.Text = L("正在透過 App Server 掃描本機對話…", "Scanning local conversations through App Server…");
        try
        {
            var old = _conversationTargets.GroupBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var result = await _conversationDiscovery.ScanAsync(CancellationToken.None);
            if (!result.Success)
            {
                AutoResumeStatusText.Text = result.Message;
                return;
            }
            _conversationTargets.Clear();
            foreach (var target in result.Conversations)
            {
                var previous = old.GetValueOrDefault(target.Key);
                if (previous is not null)
                {
                    target.Enabled = previous.Enabled;
                    target.LastStatus = previous.LastStatus;
                    target.LastMessage = previous.LastMessage;
                    target.LastAttemptAt = previous.LastAttemptAt;
                }
                if (target.IdentityStatus != ConversationIdentityStatus.Available) target.Enabled = false;
                _conversationTargets.Add(target);
            }
            foreach (var previous in old.Values.Where(value => !_conversationTargets.Any(target => target.Key == value.Key ||
                string.IsNullOrEmpty(value.ThreadId) && target.ProjectName == value.ProjectName && target.Title == value.Title)))
            {
                previous.Enabled = false;
                previous.IdentityStatus = ConversationIdentityStatus.Missing;
                _conversationTargets.Add(previous);
            }
            await SaveAutoResumeSettingsAsync(null);
            AutoResumeStatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            AutoResumeStatusText.Text = $"{L("掃描失敗", "Scan failed")}：{ex.Message}";
            AutoResumeStatusText.Foreground = System.Windows.Media.Brushes.Salmon;
        }
        finally { ScanConversationsButton.IsEnabled = true; }
    }

    private async void OnStartMonitoringClick(object sender, RoutedEventArgs e)
    {
        AutoResumeEnabledCheckBox.IsChecked = true;
        await SaveAutoResumeSettingsAsync(true);
        await _autoResumeScheduler.StartAsync(CancellationToken.None);
        UpdateAutoResumeStatus(_autoResumeScheduler.Status);
    }

    private async void OnStopMonitoringClick(object sender, RoutedEventArgs e)
    {
        AutoResumeEnabledCheckBox.IsChecked = false;
        await SaveAutoResumeSettingsAsync(false);
        await _autoResumeScheduler.StopAsync(CancellationToken.None);
        UpdateAutoResumeStatus(_autoResumeScheduler.Status);
    }

    private async void OnRunAutoResumeNowClick(object sender, RoutedEventArgs e)
    {
        await SaveAutoResumeSettingsAsync(null);
        AutoResumeStatusText.Text = L("正在更新用量及檢查白名單…", "Updating usage and checking the allowlist…");
        var result = await _autoResumeScheduler.RunNowAsync(CancellationToken.None);
        _autoModel = await _autoResumeSettings.LoadAsync(CancellationToken.None);
        _conversationTargets.Clear();
        foreach (var target in _autoModel.Conversations) _conversationTargets.Add(target);
        UpdateAutoResumeStatus(result.Message);
    }

    private async Task SaveAutoResumeSettingsAsync(bool? enabled)
    {
        ConversationsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        ConversationsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        await _autoResumeSettings.UpdateAsync(value =>
        {
            if (enabled.HasValue) value.MonitorEnabled = enabled.Value;
            foreach (var target in _conversationTargets)
            {
                var previous = value.Conversations.FirstOrDefault(x => x.Key == target.Key);
                if (previous is not null) { target.LastStatus = previous.LastStatus; target.LastMessage = previous.LastMessage; target.LastTurnId = previous.LastTurnId; target.NextResumeAt = previous.NextResumeAt; }
            }
            value.Conversations = _conversationTargets.ToList();
        }, CancellationToken.None);
    }

    private void OnSchedulerStatusChanged(object? sender, EventArgs e)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            var sequence = ++_refreshSequence;
            try
            {
                var current = await _autoResumeSettings.LoadAsync(CancellationToken.None);
                if (_closed || sequence != _refreshSequence) return;
                MergeRuntimeResults(_conversationTargets, current.Conversations);
                UpdateAutoResumeStatus(_autoResumeScheduler.Status);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { if (!_closed) UpdateAutoResumeStatus(_autoResumeScheduler.Status); }
        }));
    }

    internal static void MergeRuntimeResults(IEnumerable<ConversationTarget> displayed, IEnumerable<ConversationTarget> saved)
    {
        var results = saved.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.Last());
        foreach (var row in displayed)
        {
            if (!results.TryGetValue(row.Key, out var current)) continue;
            // Preserve unsaved checkbox edits, row identity and the user's selection.
            row.LastStatus = current.LastStatus; row.LastMessage = current.LastMessage;
            row.LastAttemptAt = current.LastAttemptAt; row.NextResumeAt = current.NextResumeAt;
            row.LastTurnId = current.LastTurnId; row.GoalStatus = current.GoalStatus;
            row.Compatibility = current.Compatibility; row.Cwd = current.Cwd;
        }
    }

    private void UpdateAutoResumeStatus(string message)
    {
        var next = _autoResumeScheduler.NextCheckAt is { } at
            ? $"｜{L("下次檢查", "Next check")}：{at:yyyy-MM-dd HH:mm:ss}" : string.Empty;
        AutoResumeStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(83, 104, 114));
        AutoResumeStatusText.Text = message + next;
    }

    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}
