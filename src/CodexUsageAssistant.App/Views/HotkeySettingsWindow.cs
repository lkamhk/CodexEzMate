using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace CodexUsageAssistant.Views;

public sealed class HotkeySettingsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly CheckBox _enabled = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 12, 0, 0) };
    private readonly List<(HotkeyAction Action, CheckBox Ctrl, CheckBox Alt, CheckBox Shift, CheckBox Win, ComboBox Key)> _rows = [];
    private readonly Button _save = new() { MinWidth = 90, Padding = new Thickness(12, 6, 12, 6) };
    private bool _saving;

    public HotkeySettingsWindow(ISettingsService settings, GlobalHotkeyService hotkeys)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        Title = "Codex EzMate v1.21.3 — " + L("快捷鍵設定", "Keyboard shortcuts");
        Width = 720; Height = 590; MinWidth = 680; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 14;
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 59, 73));
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 241, 245));
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L("快捷鍵設定", "Keyboard shortcuts"), FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L("全域快捷鍵：程式在背景時亦可開啟視窗。選擇修飾鍵及按鍵；選「未設定」可清除。", "Global shortcuts open windows while the app runs in the background. Select modifiers and a key; choose Unassigned to clear."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        _enabled.Content = L("啟用全域快捷鍵", "Enable global shortcuts");
        _enabled.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(_enabled);
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            row.Children.Add(new TextBlock { Text = GlobalHotkeyService.ActionLabel(action), Width = 165, VerticalAlignment = VerticalAlignment.Center });
            CheckBox Modifier(string name)
            {
                var check = new CheckBox { Content = name, Width = 65, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(check); return check;
            }
            var ctrl = Modifier("Ctrl"); var alt = Modifier("Alt"); var shift = Modifier("Shift"); var win = Modifier("Win");
            var key = new ComboBox { Width = 160, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
            key.Items.Add(new KeyChoice(0, L("未設定", "Unassigned")));
            foreach (var number in Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10))) key.Items.Add(new KeyChoice((uint)number, ((char)number).ToString()));
            foreach (var number in Enumerable.Range(1, 11)) key.Items.Add(new KeyChoice((uint)(0x6F + number), "F" + number));
            key.SelectedIndex = 0;
            row.Children.Add(key); panel.Children.Add(row);
            _rows.Add((action, ctrl, alt, shift, win, key));
        }
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = L("取消", "Cancel"), MinWidth = 90, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => Close();
        _save.Content = L("儲存", "Save"); _save.IsEnabled = false;
        _save.Click += async (_, _) => await SaveAsync();
        buttons.Children.Add(cancel); buttons.Children.Add(_save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private async Task LoadAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            _enabled.IsChecked = settings.HotkeysEnabled;
            foreach (var row in _rows)
            {
                var binding = settings.Hotkeys?.FirstOrDefault(b => b.Action == row.Action);
                if (binding is null) continue;
                row.Ctrl.IsChecked = (binding.Modifiers & 2) != 0; row.Alt.IsChecked = (binding.Modifiers & 1) != 0;
                row.Shift.IsChecked = (binding.Modifiers & 4) != 0; row.Win.IsChecked = (binding.Modifiers & 8) != 0;
                row.Key.SelectedValue = binding.VirtualKey;
            }
            _save.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
    }

    private async Task SaveAsync()
    {
        _saving = true; _save.IsEnabled = false;
        try
        {
            var bindings = _rows.Where(r => r.Key.SelectedValue is uint key && key != 0)
                .Select(r => new HotkeyBinding(r.Action,
                    (r.Ctrl.IsChecked == true ? 2u : 0) | (r.Alt.IsChecked == true ? 1u : 0) |
                    (r.Shift.IsChecked == true ? 4u : 0) | (r.Win.IsChecked == true ? 8u : 0), (uint)r.Key.SelectedValue)).ToList();
            var settings = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            var oldBindings = settings.Hotkeys ?? [];
            var oldEnabled = settings.HotkeysEnabled;
            if (!_hotkeys.TryApply(_enabled.IsChecked == true, bindings, out var error)) { _status.Text = error; return; }
            settings.HotkeysEnabled = _enabled.IsChecked == true; settings.Hotkeys = bindings;
            try { await _settings.SaveAsync(settings, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _hotkeys.TryApply(oldEnabled, oldBindings, out var rollbackError);
                _status.Text = L("儲存失敗：", "Save failed: ") + ex.Message + "\n" + rollbackError;
                return;
            }
            _saving = false; Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
        finally { _saving = false; _save.IsEnabled = true; }
    }

    private static string L(string chinese, string english) => LocalizationService.Pick(chinese, english);
    private sealed record KeyChoice(uint Value, string Label);
}
