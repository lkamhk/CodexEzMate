using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;

namespace CodexUsageAssistant.Views;

public sealed class SessionNamingEditor : UserControl
{
    private sealed record Choice(NamingSegmentSource Source, string Label);
    private sealed record Row(Grid Panel, ComboBox Source, TextBox Length, TextBox Text);
    private readonly CheckBox _enabled = new();
    private readonly TextBox _separator = new() { Width = 110, MaxLength = 16 };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _exclusions = new();
    private readonly Button _add = new();
    private readonly List<Row> _entries = [];
    private SessionAutoNamingService? _service;
    private bool _loading, _rulesDirty, _legacy;

    public SessionNamingEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        panel.Children.Add(new TextBlock { Text = L("自動命名規則", "Automatic naming rules"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        _enabled.Content = L("啟用自動命名", "Enable automatic naming"); _enabled.Margin = new Thickness(0, 12, 0, 8);
        panel.Children.Add(_enabled);
        panel.Children.Add(new TextBlock { Text = L("全部在本機處理，不呼叫 AI、不消耗 token。新規則只套用於之後完成回覆的會話。", "Local rules only: no AI requests or token usage. Changes apply after future completed replies."), TextWrapping = TextWrapping.Wrap });
        var separatorLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        separatorLine.Children.Add(new TextBlock { Text = L("分隔符（可包含空格）", "Separator (spaces allowed)"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        separatorLine.Children.Add(_separator); panel.Children.Add(separatorLine);
        panel.Children.Add(new TextBlock { Text = L("由上至下組合。每段上限 1–100 字（包含省略號），總上限 240 字；空白內容會略過。", "Top-to-bottom order. 1–100 characters per segment (including ellipsis), 240 total; empty values are skipped."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(_rows);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        _add.Content = L("＋新增命名段", "+ Add segment"); _add.Padding = new Thickness(10, 5, 10, 5);
        _add.Click += (_, _) => { AddRow(new()); MarkRulesChanged(); };
        _exclusions.Content = L("管理排除名單…", "Manage exclusions…"); _exclusions.Margin = new Thickness(8, 0, 0, 0); _exclusions.Padding = new Thickness(10, 5, 10, 5);
        _exclusions.Click += (_, _) =>
        {
            if (_service is null) return;
            new NamingExclusionsWindow(_service) { Owner = Window.GetWindow(this) }.ShowDialog();
        };
        actions.Children.Add(_add); actions.Children.Add(_exclusions); panel.Children.Add(actions);
        panel.Children.Add(new TextBlock { Text = L("格式預覽（示例資料）", "Format preview (sample data)") });
        panel.Children.Add(_preview); panel.Children.Add(_hint);
        _separator.TextChanged += (_, _) => MarkRulesChanged();
        Content = panel; IsEnabled = false;
    }

    public async Task InitializeAsync(SessionAutoNamingService? service)
    {
        _service = service;
        if (service is null) { _hint.Text = L("命名服務未啟動。", "Naming service is unavailable."); return; }
        var configuration = await service.GetConfigurationAsync();
        _loading = true;
        try
        {
            _legacy = configuration.UsesLegacyRules;
            _enabled.IsChecked = configuration.Enabled; _separator.Text = configuration.Rules.Separator;
            _rows.Children.Clear(); _entries.Clear();
            foreach (var segment in configuration.Rules.Segments) AddRow(segment);
            _rulesDirty = false; IsEnabled = true;
        }
        finally { _loading = false; }
        RefreshPreview();
    }

    public void ValidateRules() { if (_service is not null && _rulesDirty) ReadRules().Validate(); }

    public async Task SaveAsync()
    {
        if (_service is null || !IsEnabled) return;
        if (_rulesDirty) await _service.ConfigureAsync(_enabled.IsChecked == true, ReadRules());
        else if ((_enabled.IsChecked == true) != _service.Enabled) await _service.SetEnabledAsync(_enabled.IsChecked == true);
    }

    private void AddRow(NamingSegment segment)
    {
        if (_entries.Count >= 8) return;
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), new GridLength(66), new GridLength(34), new GridLength(34), new GridLength(52) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var source = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0), DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Source), ItemsSource = new[]
        {
            new Choice(NamingSegmentSource.ProjectName, L("Project 名（工作目錄名稱）", "Project name (working folder)")),
            new Choice(NamingSegmentSource.FirstPrompt, L("第一條 prompt", "First prompt")),
            new Choice(NamingSegmentSource.LatestPrompt, L("最新 prompt", "Latest prompt")),
            new Choice(NamingSegmentSource.CustomText, L("固定文字", "Custom text")),
            new Choice(NamingSegmentSource.SessionId, "Session ID")
        }, SelectedValue = segment.Source };
        var length = new TextBox { Text = segment.MaxCharacters.ToString(), Margin = new Thickness(0, 0, 6, 0), ToolTip = L("字數上限（包含省略號）", "Character limit (includes ellipsis)") };
        var custom = new TextBox { Text = segment.Text, MaxLength = 1000, Margin = new Thickness(0, 4, 8, 0), ToolTip = L("固定文字", "Custom text"), Visibility = segment.Source == NamingSegmentSource.CustomText ? Visibility.Visible : Visibility.Collapsed };
        var row = new Row(grid, source, length, custom);
        grid.Children.Add(source); Grid.SetColumn(length, 1); grid.Children.Add(length);
        Grid.SetRow(custom, 1); Grid.SetColumnSpan(custom, 2); grid.Children.Add(custom);
        void AddButton(string text, int column, Action click)
        {
            var button = new Button { Content = text, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(3) };
            button.Click += (_, _) => click(); Grid.SetColumn(button, column); grid.Children.Add(button);
        }
        AddButton("↑", 2, () => Move(row, -1)); AddButton("↓", 3, () => Move(row, 1));
        AddButton(L("移除", "Remove"), 4, () =>
        {
            if (_entries.Count <= 1) return;
            _entries.Remove(row); _rows.Children.Remove(grid); MarkRulesChanged();
        });
        source.SelectionChanged += (_, _) => { custom.Visibility = (NamingSegmentSource?)source.SelectedValue == NamingSegmentSource.CustomText ? Visibility.Visible : Visibility.Collapsed; MarkRulesChanged(); };
        source.PreviewMouseWheel += (_, e) =>
        {
            // Wheel input scrolls the settings page, never changes the selected rule.
            e.Handled = true;
            RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
        };
        length.TextChanged += (_, _) => MarkRulesChanged(); custom.TextChanged += (_, _) => MarkRulesChanged();
        _entries.Add(row); _rows.Children.Add(grid);
    }

    private void Move(Row row, int direction)
    {
        var index = _entries.IndexOf(row); var target = index + direction;
        if (target < 0 || target >= _entries.Count) return;
        _entries.RemoveAt(index); _entries.Insert(target, row);
        _rows.Children.Remove(row.Panel); _rows.Children.Insert(target, row.Panel); MarkRulesChanged();
    }

    private SessionNamingRules ReadRules()
    {
        var rules = new SessionNamingRules { Separator = _separator.Text, Segments = [] };
        foreach (var row in _entries)
        {
            if (!int.TryParse(row.Length.Text, out var length) || row.Source.SelectedValue is not NamingSegmentSource source)
                throw new InvalidDataException(L("請為每段選擇內容，字數須為整數。", "Choose a source and a whole-number length for every segment."));
            rules.Segments.Add(new() { Source = source, MaxCharacters = length, Text = row.Text.Text });
        }
        rules.Validate(); return rules;
    }

    private void MarkRulesChanged() { if (_loading) return; _rulesDirty = true; RefreshPreview(); }
    private void RefreshPreview()
    {
        _add.IsEnabled = _entries.Count < 8;
        try
        {
            var rules = ReadRules();
            _preview.Text = _legacy && !_rulesDirty ? "myProject | " + SessionNameFormatter.Shorten(L("更新設定頁及命名規則", "Update settings and naming rules"))
                : rules.Format("myProject", L("建立會話瀏覽器", "Create a session browser"), L("更新設定頁及命名規則", "Update settings and naming rules"), "01900000-0000-7000-8000-000000000099");
            _hint.Text = _legacy && !_rulesDirty ? L("目前保留舊命名規則；修改規則並按「儲存」後才切換。", "Legacy naming is preserved until you edit the rules and save.")
                : L("儲存設定後生效。排除名單的變更會獨立即時儲存。", "Rules take effect after saving. Exclusion changes are saved immediately and independently.");
            _hint.Foreground = new SolidColorBrush(Color.FromRgb(54, 79, 96));
        }
        catch (InvalidDataException ex) { _preview.Text = "—"; _hint.Text = ex.Message; _hint.Foreground = System.Windows.Media.Brushes.DarkRed; }
    }

    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}
