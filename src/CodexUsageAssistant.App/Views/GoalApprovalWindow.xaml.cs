using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;

namespace CodexUsageAssistant.Views;

public partial class GoalApprovalWindow : Window
{
    private readonly IGoalResumeService _goals;
    private readonly GoalPendingRequest _request;
    private readonly Dictionary<string, Func<string>> _answers = [];
    public GoalApprovalWindow(IGoalResumeService goals, GoalPendingRequest request)
    {
        _goals = goals; _request = request; InitializeComponent();
        Title = "Codex EzMate v1.21.3 — " + L("Goal 待處理請求", "Goal request");
        ContextText.Text = L("請確認本次操作；關閉此視窗會保留等待狀態。", "Review this operation. Closing this window leaves the request pending.") + "\n" + request.ThreadId;
        DetailsText.Text = request.Summary;
        if (request.Method == "item/tool/requestUserInput")
            foreach (var question in GoalProtocol.Property(request.Parameters, "questions").EnumerateArray())
            {
                var id = GoalProtocol.Text(question, "id")!;
                QuestionsPanel.Children.Add(new TextBlock { Text = GoalProtocol.Text(question, "question"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
                if (GoalProtocol.Property(question, "isSecret").ValueKind == JsonValueKind.True)
                {
                    var input = new PasswordBox(); QuestionsPanel.Children.Add(input); _answers[id] = () => input.Password;
                }
                else if (GoalProtocol.Property(question, "options").ValueKind == JsonValueKind.Array)
                {
                    var input = new System.Windows.Controls.ComboBox
                    {
                        IsEditable = GoalProtocol.Property(question, "isOther").ValueKind == JsonValueKind.True,
                        ItemsSource = GoalProtocol.Property(question, "options").EnumerateArray().Select(x => GoalProtocol.Text(x, "label")).ToArray()
                    };
                    QuestionsPanel.Children.Add(input); _answers[id] = () => input.Text;
                }
                else
                {
                    var input = new System.Windows.Controls.TextBox(); QuestionsPanel.Children.Add(input); _answers[id] = () => input.Text;
                }
            }
        foreach (var decision in request.Decisions)
        {
            var button = new System.Windows.Controls.Button { Content = decision switch
            {
                "accept" => L("允許本次", "Allow this time"), "decline" => L("拒絕", "Decline"),
                "submit" => L("送出回覆", "Submit answers"), _ => L("取消並暫停", "Cancel and pause")
            }, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) };
            button.Click += async (_, _) => await RespondAsync(decision);
            DecisionsPanel.Children.Add(button);
        }
        _goals.Changed += OnChanged;
        Closed += (_, _) => _goals.Changed -= OnChanged;
    }
    private void OnChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_goals.PendingRequests.Any(x => x.Key == _request.Key)) Close(); }));
    }
    private async Task RespondAsync(string decision)
    {
        DecisionsPanel.IsEnabled = false;
        try
        {
            var answers = _answers.ToDictionary(x => x.Key, x => new[] { x.Value() });
            await _goals.RespondAsync(_request.Key, decision, answers, CancellationToken.None);
            Close();
        }
        catch (Exception ex) when (CodexAppServerHost.IsExpected(ex))
        { ContextText.Text = L("回覆未能送出。請確認已填寫答案，或返回 Goal 視窗檢查請求是否仍有效。", "Response was not sent. Check your answers or whether the request is still active."); }
        finally { DecisionsPanel.IsEnabled = true; }
    }
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}
