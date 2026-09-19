using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Binding = System.Windows.Data.Binding;

namespace CodexUsageAssistant.Views;

public sealed class SessionBrowserWindow : Window
{
    private readonly SessionBrowserStore _store;
    private SessionBrowserSettings _settings;
    private AppLanguage _language;
    private readonly List<Action> _translations = [];
    private readonly TextBox _search = new() { Width = 300, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox _filter = new() { Width = 155, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _hideInternal = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserAddRows = false,
        CanUserDeleteRows = false, RowHeight = 30, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(12) };
    private readonly TextBlock _previewTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Foreground = System.Windows.Media.Brushes.SteelBlue, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _refresh = new();
    private readonly Button _backup = new();
    private readonly Button _delete = new();
    private readonly Button _restore = new();
    private readonly Button _cancel = new();
    private readonly Dictionary<string, DataGridColumn> _columns = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private IReadOnlyList<BrowserSession> _sessions = [];
    private ICollectionView? _view;
    private CancellationTokenSource? _scanCts, _previewCts, _operationCts;
    private bool _closed, _changingView, _exiting, _restoring = true;
    private int _skipped;
    public bool IsFileOperationRunning { get; private set; }
    internal DataGrid SessionsGrid => _grid;
    internal string PreviewText => _preview.Text;

    public SessionBrowserWindow(SessionBrowserStore? store = null)
    {
        _store = store ?? new(); _settings = SessionBrowserSettings.Load(_store.SettingsPath);
        _language = LocalizationService.CurrentLanguage;
        Width = 1280; Height = 820; MinWidth = 920; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 240, 243));
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 59, 70)); FontSize = 13;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/CodexEzMate;component/Assets/CodexUsageAssistant.ico"));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexEzMate;component/Resources/SettingsStyles.xaml", UriKind.Relative) });
        var panel = new DockPanel { Margin = new Thickness(20), Background = Background };
        var footer = new StackPanel(); footer.Children.Add(_status); footer.Children.Add(_summary); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var title = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold };
        Translate(() => title.Text = L("Codex 會話", "Codex Sessions")); top.Children.Add(title);
        var help = new TextBlock { Margin = new Thickness(0, 8, 0, 16) };
        Translate(() => help.Text = L("瀏覽、備份及管理本機對話", "Browse, back up and manage your local conversations")); top.Children.Add(help);
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var searchLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Translate(() => searchLabel.Text = L("搜尋", "Search")); filters.Children.Add(searchLabel);
        filters.Children.Add(_search); filters.Children.Add(_filter); filters.Children.Add(_refresh); filters.Children.Add(_hideInternal); top.Children.Add(filters);
        Translate(() => { _search.ToolTip = L("搜尋名稱、訊息、ID、專案或路徑", "Search names, messages, IDs, projects or paths"); _hideInternal.Content = L("隱藏內部會話", "Hide internal sessions"); });
        _refresh.Click += async (_, _) => await RefreshAsync(); Label(_refresh, "重新整理", "Refresh");
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; top.Children.Add(actions);
        Button Action(string chinese, string english, Action run)
        {
            var button = new Button(); Label(button, chinese, english); button.Click += (_, _) => run(); actions.Children.Add(button); return button;
        }
        Action("全選可見項目", "Select visible", () => _grid.SelectAll());
        Action("複製 ID", "Copy ID", CopyIds);
        Action("開啟 JSONL", "Open JSONL", () => OpenSelected(false));
        Action("開啟資料夾", "Open folder", () => OpenSelected(true));
        Label(_delete, "移至回收區", "Move to Trash"); Label(_backup, "備份", "Backup"); Label(_restore, "還原", "Restore"); Label(_cancel, "取消操作", "Cancel operation");
        foreach (var button in new[] { _delete, _backup, _restore, _cancel }) actions.Children.Add(button);
        _delete.Click += async (_, _) => await OperateAsync(BrowserFileAction.Trash);
        _backup.Click += async (_, _) => await OperateAsync(BrowserFileAction.Backup);
        _restore.Click += async (_, _) => await OperateAsync(BrowserFileAction.Restore);
        _cancel.Click += (_, _) => _operationCts?.Cancel();
        var split = new Grid(); split.RowDefinitions.Add(new() { Height = new GridLength(3, GridUnitType.Star) }); split.RowDefinitions.Add(new() { Height = new GridLength(6) }); split.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star) });
        split.Children.Add(_grid);
        var divider = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows }; Grid.SetRow(divider, 1); split.Children.Add(divider);
        var previewPanel = new DockPanel(); DockPanel.SetDock(_previewTitle, Dock.Top); previewPanel.Children.Add(_previewTitle); previewPanel.Children.Add(_preview); Grid.SetRow(previewPanel, 2); split.Children.Add(previewPanel); panel.Children.Add(split);
        Content = panel;
        Column("updated", "更新時間", "Updated", nameof(BrowserSession.Updated), 150, "yyyy-MM-dd HH:mm:ss");
        Column("status", "狀態", "Status", nameof(BrowserSession.Status), 115);
        Column("name", "會話名稱", "Session name", nameof(BrowserSession.DisplayName), 260);
        Column("preview", "首則訊息", "First message", nameof(BrowserSession.Preview), 320);
        Column("provider", "供應商", "Provider", nameof(BrowserSession.Provider), 90);
        Column("cwd", "專案／工作目錄", "Project / cwd", nameof(BrowserSession.Project), 230);
        Column("id", "會話 ID", "Session ID", nameof(BrowserSession.Id), 290);
        Column("size", "大小", "Size", nameof(BrowserSession.SizeText), 90, sort: nameof(BrowserSession.Size));
        RestoreGeometry(); _search.Text = _settings.Search; _hideInternal.IsChecked = _settings.HideInternal;
        SetLanguage(_language); _restoring = false;
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); SaveSettings(); };
        _filter.SelectionChanged += (_, _) => { if (!_restoring) { ApplyFilter(); SaveSettings(); } };
        _hideInternal.Click += (_, _) => { ApplyFilter(); SaveSettings(); };
        _grid.SelectionChanged += async (_, _) => { if (_changingView) return; UpdateButtons(); await PreviewAsync(); };
        _grid.Sorting += (_, e) =>
        {
            e.Handled = true; var key = _columns.First(p => p.Value == e.Column).Key;
            _settings.SortDescending = key == _settings.SortColumn ? !_settings.SortDescending : false;
            _settings.SortColumn = key; Sort(); SaveSettings();
        };
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) =>
        {
            if (IsFileOperationRunning) { e.Cancel = true; ShowBusy(); return; }
            SaveSettings(); _closed = true; _searchTimer.Stop(); _scanCts?.Cancel(); _previewCts?.Cancel();
            LocalizationService.LanguageChanged -= SetLanguage;
        };
        LocalizationService.LanguageChanged += SetLanguage;
        UpdateButtons();
    }

    private string L(string chinese, string english) => LocalizationService.Translate(chinese, english, _language);
    private void Translate(Action action) { _translations.Add(action); action(); }
    private void Label(Button button, string chinese, string english)
    { button.Padding = new Thickness(10, 6, 10, 6); button.Margin = new Thickness(0, 0, 7, 0); Translate(() => button.Content = L(chinese, english)); }
    private void Column(string key, string chinese, string english, string property, double width, string? format = null, string? sort = null)
    {
        var column = new DataGridTextColumn { Binding = new Binding(property) { StringFormat = format }, SortMemberPath = sort ?? property,
            Width = _settings.Columns.TryGetValue(key, out var saved) && saved is >= 60 and <= 2000 ? saved : width };
        Translate(() => column.Header = L(chinese, english)); _columns.Add(key, column); _grid.Columns.Add(column);
    }
    internal void SetLanguage(AppLanguage language)
    {
        _language = language; Title = "Codex EzMate v1.21.3 — " + L("會話瀏覽器", "Session Browser");
        foreach (var session in _sessions) session.SetLanguage(language);
        foreach (var translate in _translations) translate();
        var restoring = _restoring; _restoring = true;
        var selected = _filter.SelectedIndex >= 0 ? _filter.SelectedIndex : Array.IndexOf(SessionBrowserSettings.Filters, _settings.Filter);
        _filter.ItemsSource = new[] { L("所有會話", "All sessions"), L("使用中", "Active only"), L("已封存", "Archived only"), L("已刪除", "Deleted only"), L("備份", "Backups only") };
        _filter.SelectedIndex = Math.Max(selected, 0); _restoring = restoring;
        ApplyFilter(); UpdatePreviewTitle();
    }
    internal void SetSessions(BrowserScan scan)
    {
        var selected = Selected().Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sessions = scan.Sessions; _skipped = scan.Skipped;
        foreach (var session in _sessions) session.SetLanguage(_language);
        _view = CollectionViewSource.GetDefaultView(_sessions); _grid.ItemsSource = _view;
        ApplyFilter(); Sort();
        foreach (var session in _grid.Items.OfType<BrowserSession>().Where(s => selected.Contains(s.Path))) _grid.SelectedItems.Add(session);
    }
    private BrowserSession[] Selected() => _grid.SelectedItems.Cast<BrowserSession>().ToArray();
    private void ApplyFilter()
    {
        if (_view is null) return;
        var selected = Selected();
        _changingView = true;
        var filter = SessionBrowserSettings.Filters[Math.Max(_filter.SelectedIndex, 0)];
        try
        {
            _view.Filter = item => item is BrowserSession session && SessionBrowserSettings.Matches(session, _search.Text, filter, _hideInternal.IsChecked == true);
            _view.Refresh();
            foreach (var session in selected.Where(_view.Contains)) if (!_grid.SelectedItems.Contains(session)) _grid.SelectedItems.Add(session);
        }
        finally { _changingView = false; }
        UpdateButtons();
        if (_grid.SelectedItem is null) { _previewCts?.Cancel(); _preview.Clear(); UpdatePreviewTitle(); }
        _summary.Text = $"{_grid.Items.Count}/{_sessions.Count}  ·  " + L("使用中", "Active") + $" {_sessions.Count(s => !s.Archived && !s.Deleted && !s.Backup)}  ·  "
            + L("已封存", "Archived") + $" {_sessions.Count(s => s.Archived && !s.Deleted && !s.Backup)}  ·  " + L("已刪除", "Deleted") + $" {_sessions.Count(s => s.Deleted)}  ·  "
            + L("備份", "Backups") + $" {_sessions.Count(s => s.Backup)}  ·  " + L("略過無法讀取", "Unreadable skipped") + $" {_skipped}\n{_store.CodexHome}";
    }
    private void Sort()
    {
        if (_view is null) return;
        var column = _columns.GetValueOrDefault(_settings.SortColumn) ?? _columns["updated"];
        var direction = _settings.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _view.SortDescriptions.Clear(); _view.SortDescriptions.Add(new(column.SortMemberPath, direction));
        foreach (var other in _columns.Values) other.SortDirection = null; column.SortDirection = direction;
    }
    private async Task RefreshAsync()
    {
        if (_closed || IsFileOperationRunning) return;
        _scanCts?.Cancel(); var cts = new CancellationTokenSource(); _scanCts = cts;
        _status.Text = L("正在掃描本機對話…", "Scanning local sessions…");
        try
        {
            var scan = await Task.Run(() => _store.Scan(cts.Token), cts.Token);
            if (_closed || cts.IsCancellationRequested) return;
            SetSessions(scan); _status.Text = L("掃描完成", "Scan complete");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { if (!_closed && !cts.IsCancellationRequested) _status.Text = L("掃描失敗，保留原清單：", "Scan failed; previous list retained: ") + ex.Message; }
        finally { if (ReferenceEquals(_scanCts, cts)) _scanCts = null; cts.Dispose(); }
    }
    private void UpdatePreviewTitle() => _previewTitle.Text = L("對話預覽", "Conversation preview") + (_grid.SelectedItem is BrowserSession session ? $" — {session.DisplayName}   [{session.Id}]" : "");
    internal async Task PreviewAsync()
    {
        _previewCts?.Cancel(); UpdatePreviewTitle();
        if (_grid.SelectedItem is not BrowserSession session) { _preview.Clear(); return; }
        var cts = new CancellationTokenSource(); _previewCts = cts;
        _preview.Text = L("讀取中…", "Loading…");
        try
        {
            var result = await Task.Run(() => _store.ReadPreview(session, cts.Token), cts.Token);
            if (_closed || cts.IsCancellationRequested) return;
            var text = new StringBuilder();
            foreach (var message in result.Messages) text.AppendLine($"{message.Role.ToUpperInvariant()}  {message.Timestamp}").AppendLine(message.Text).AppendLine();
            if (result.Truncated) text.AppendLine(L("預覽已截斷；可開啟 JSONL 查看完整記錄。", "Preview truncated. Open JSONL for the full record."));
            _preview.Text = text.Length == 0 ? L("沒有可預覽的對話訊息", "No conversation messages to preview") : text.ToString(); _preview.ScrollToHome();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!_closed && !cts.IsCancellationRequested) _preview.Text = ex.Message; }
        finally { if (ReferenceEquals(_previewCts, cts)) _previewCts = null; cts.Dispose(); }
    }
    private void UpdateButtons()
    {
        var selected = Selected(); var original = selected.Length > 0 && selected.All(s => !s.Deleted && !s.Backup);
        _backup.IsEnabled = _delete.IsEnabled = !_exiting && !IsFileOperationRunning && original;
        _restore.IsEnabled = !_exiting && !IsFileOperationRunning && selected.Length > 0 && selected.All(s => s.Deleted || s.Backup);
        _cancel.IsEnabled = IsFileOperationRunning; _refresh.IsEnabled = !IsFileOperationRunning;
        _grid.IsEnabled = _search.IsEnabled = _filter.IsEnabled = _hideInternal.IsEnabled = !IsFileOperationRunning;
    }
    internal void ShowBusy()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); _status.Text = L("檔案操作尚未完成，請稍候或先按「取消操作」再退出。", "A file operation is running. Wait or cancel the operation before exiting.");
    }
    internal void PrepareForExit(bool exiting) { _exiting = exiting; UpdateButtons(); }
    private async Task OperateAsync(BrowserFileAction action)
    {
        if (IsFileOperationRunning || _exiting || _closed) return;
        var selected = Selected(); if (selected.Length == 0) return;
        if (action != BrowserFileAction.Backup && System.Windows.MessageBox.Show(this,
            L($"將處理 {selected.Length} 個會話。請先停止所選對話的工作。還原不會覆蓋現有檔案。是否繼續？", $"Process {selected.Length} sessions? Stop work in the selected conversations first. Restore never overwrites existing files."),
            action == BrowserFileAction.Trash ? L("確認移至回收區", "Confirm move to Trash") : L("確認還原", "Confirm restore"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _scanCts?.Cancel(); _previewCts?.Cancel(); IsFileOperationRunning = true; UpdateButtons();
        using var cts = new CancellationTokenSource(); _operationCts = cts;
        var errors = new List<string>(); var completed = 0; var cancelled = false;
        _status.Text = L("正在處理檔案…", "Processing files…");
        try
        {
            await Task.Run(() =>
            {
                foreach (var session in selected)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try { _store.Apply(session, action, cts.Token); completed++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{session.DisplayName}: {ex.Message}"); }
                }
            });
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally { _operationCts = null; IsFileOperationRunning = false; UpdateButtons(); }
        await RefreshAsync();
        if (_closed) return;
        _status.Text = L($"完成 {completed}／{selected.Length}；失敗 {errors.Count}", $"Completed {completed}/{selected.Length}; failed {errors.Count}") + (cancelled ? L("；已取消剩餘操作", "; remaining operations cancelled") : "");
        if (errors.Count > 0) System.Windows.MessageBox.Show(this, string.Join("\n", errors.Take(12)), L("部分操作未完成", "Some operations failed"));
    }
    private void CopyIds()
    {
        try { var value = string.Join(Environment.NewLine, Selected().Select(s => s.Id).Where(id => id.Length > 0).Distinct()); if (value.Length > 0) System.Windows.Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException ex) { _status.Text = ex.Message; }
    }
    private void OpenSelected(bool folder)
    {
        if (_grid.SelectedItem is not BrowserSession session) return;
        try { _store.ValidateSource(session); Process.Start(new ProcessStartInfo(folder ? Path.GetDirectoryName(session.Path)! : session.Path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { _status.Text = ex.Message; }
    }
    private void RestoreGeometry()
    {
        var match = Regex.Match(_settings.Geometry ?? "", @"^(\d+)x(\d+)([+-]\d+)([+-]\d+)$");
        if (!match.Success) return;
        var numbers = match.Groups.Cast<Group>().Skip(1).Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        Width = Math.Clamp(numbers[0], MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(numbers[1], MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        var bounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (bounds.Contains(new System.Windows.Point(numbers[2] + 100, numbers[3] + 20))) { Left = numbers[2]; Top = numbers[3]; WindowStartupLocation = WindowStartupLocation.Manual; }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }
    private void SaveSettings()
    {
        if (_restoring || _closed) return;
        _settings.Filter = SessionBrowserSettings.Filters[Math.Max(_filter.SelectedIndex, 0)]; _settings.HideInternal = _hideInternal.IsChecked == true; _settings.Search = _search.Text;
        _settings.Columns = _columns.ToDictionary(p => p.Key, p => Math.Clamp(p.Value.ActualWidth, 60, 2000));
        _settings.Maximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)) _settings.Geometry = FormattableString.Invariant($"{bounds.Width:0}x{bounds.Height:0}{bounds.X:+0;-0;+0}{bounds.Y:+0;-0;+0}");
        try { _settings.Save(_store.SettingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = L("設定未能儲存：", "Could not save settings: ") + ex.Message; }
    }
}
