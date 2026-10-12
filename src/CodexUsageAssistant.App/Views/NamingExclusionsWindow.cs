using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CodexUsageAssistant.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;

namespace CodexUsageAssistant.Views;

public sealed class NamingExclusionsWindow : Window
{
    private sealed record Entry(string Id, string Name);
    private readonly SessionAutoNamingService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGrid _available = CreateGrid(), _excluded = CreateGrid();
    private readonly TextBox _search = new(), _id = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private List<Entry> _sessions = [];
    private bool _closed;
    private string? _viewKey;

    public NamingExclusionsWindow(SessionAutoNamingService service, SessionBrowserStore? store = null)
    {
        _service = service;
        Title = "Codex EzMate v1.22.7 — " + L("自動命名排除名單", "Auto-naming exclusions");
        Width = 930; Height = 570; MinWidth = 760; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 241, 245));
        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 59, 73));
        var panel = new DockPanel { Margin = new Thickness(18) }; Content = panel;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = L("加入排除後，EzMate 不會自動改動該會話名稱。移除排除只影響之後完成的回覆。", "Excluded sessions keep their names. Removing an exclusion applies only to future completed replies."), TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = L("搜尋名稱或 Session ID", "Search name or Session ID"), Margin = new Thickness(0, 10, 0, 4) });
        _search.Margin = new Thickness(0, 0, 0, 10); heading.Children.Add(_search);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        _id.Width = 310; _id.MaxLength = 64; _id.ToolTip = "Session ID"; _actions.Children.Add(_id);
        AddButton(L("按 ID 加入", "Add ID"), async () =>
        {
            if (!Guid.TryParse(_id.Text.Trim(), out var id)) throw new InvalidDataException(L("請輸入有效 Session ID（UUID）。", "Enter a valid Session ID (UUID)."));
            await _service.SetExcludedAsync([id.ToString("D")], true, _lifetime.Token); _id.Clear();
        });
        AddButton(L("加入所選 →", "Exclude selected →"), () => ChangeSelected(_available, true));
        AddButton(L("← 移除排除", "← Remove exclusion"), () => ChangeSelected(_excluded, false));
        footer.Children.Add(_actions); _status.Margin = new Thickness(0, 8, 0, 0); footer.Children.Add(_status);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new()); panel.Children.Add(columns);
        AddList(columns, _available, L("可加入的會話", "Available sessions"), 0);
        AddList(columns, _excluded, L("已排除（可多選）", "Excluded (multiple selection)"), 1);
        _search.TextChanged += async (_, _) => await RefreshSafely();
        _service.Changed += ServiceChanged;
        Loaded += async (_, _) =>
        {
            await RefreshSafely();
            try
            {
                var scan = await Task.Run(() => (store ?? new SessionBrowserStore()).Scan(_lifetime.Token), _lifetime.Token);
                _sessions = scan.Sessions.Where(s => !s.Internal && !s.Backup && !s.Deleted && Guid.TryParse(s.Id, out _))
                    .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Select(g => new Entry(g.Key, g.First().DisplayName)).ToList();
                await RefreshSafely();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!_closed) _status.Text = ex.Message; }
        };
        Closed += (_, _) => { _closed = true; _service.Changed -= ServiceChanged; _lifetime.Cancel(); };
    }

    private void ServiceChanged()
    {
        if (!_closed) Dispatcher.BeginInvoke(new Action(async () => await RefreshSafely()));
    }

    private async Task ChangeSelected(DataGrid grid, bool excluded)
    {
        var ids = grid.SelectedItems.Cast<Entry>().Select(e => e.Id).ToArray();
        if (ids.Length > 0) await _service.SetExcludedAsync(ids, excluded, _lifetime.Token);
    }

    private void AddButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(6, 0, 0, 0) };
        button.Click += async (_, _) =>
        {
            _actions.IsEnabled = false;
            try { await action(); await RefreshSafely(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { if (!_closed) _status.Text = ex.Message; }
            finally { if (!_closed) _actions.IsEnabled = true; }
        };
        _actions.Children.Add(button);
    }

    private async Task RefreshSafely()
    {
        if (_closed) return;
        try
        {
            var config = await _service.GetConfigurationAsync(_lifetime.Token);
            if (_closed) return;
            var ids = config.ExcludedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var query = _search.Text.Trim();
            var key = string.Join(",", config.ExcludedIds) + "\n" + query + "\n" + _sessions.Count;
            if (key == _viewKey) return;
            _viewKey = key;
            bool Matches(Entry e) => query.Length == 0 || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            _available.ItemsSource = _sessions.Where(e => !ids.Contains(e.Id) && Matches(e)).ToArray();
            var names = _sessions.ToDictionary(e => e.Id, e => e.Name, StringComparer.OrdinalIgnoreCase);
            _excluded.ItemsSource = ids.Select(id => new Entry(id, names.GetValueOrDefault(id, L("未載入／未找到會話", "Session not loaded / not found")))).Where(Matches).ToArray();
            _status.Text = L($"已排除 {ids.Count} 個會話。變更即時儲存，不受基本設定的取消按鈕影響。", $"{ids.Count} excluded sessions. Changes save immediately, independently of the settings Cancel button.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { if (!_closed) _status.Text = ex.Message; }
    }

    private static DataGrid CreateGrid()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow };
        grid.Columns.Add(new DataGridTextColumn { Header = L("會話名稱", "Name"), Binding = new Binding(nameof(Entry.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Session ID", Binding = new Binding(nameof(Entry.Id)), Width = 260 });
        return grid;
    }

    private static void AddList(Grid parent, DataGrid grid, string title, int column)
    {
        var panel = new DockPanel { Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 0 ? 6 : 0, 0) };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(grid);
        Grid.SetColumn(panel, column); parent.Children.Add(panel);
    }
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}
