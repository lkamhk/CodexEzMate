using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;

namespace CodexUsageAssistant.Views;

public sealed class ResetNoticeWindow : Window
{
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Brushes.White };
    private readonly Button _credit = new() { Content = "Data from Codex Resets ↗", HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<string, ResetAnnouncement> _items = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new();
    private int? _autoCloseSeconds;
    public ResetNoticeWindow(Action openDetails, bool isPreview = false)
    {
        Title = "Codex EzMate v1.22.7 — Reset notification";
        Width = 420; SizeToContent = SizeToContent.Height; MaxHeight = 440;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        var panel = new StackPanel { Margin = new Thickness(18) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = new Button { Content = "×", Width = 28, Height = 28, ToolTip = L("關閉通知", "Dismiss notification") };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "EzMate · " + (isPreview ? L("測試通知", "Test notification") : L("Reset 新動態", "Reset update")), Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { Content = _message, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _credit.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://codex-resets.com") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        };
        panel.Children.Add(_credit);
        panel.Children.Add(new TextBlock { Text = L("公告不代表個別帳戶已到帳。", "Announcements do not confirm account receipt."), Foreground = new SolidColorBrush(Color.FromRgb(207, 219, 228)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10), FontSize = 12 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var view = new Button { Content = L("查看 Reset 動態", "View reset activity"), Padding = new Thickness(12, 6, 12, 6) };
        view.Click += (_, _) => { openDetails(); Close(); }; actions.Children.Add(view); panel.Children.Add(actions);
        Content = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromRgb(32, 45, 58)), BorderBrush = new SolidColorBrush(Color.FromRgb(117, 170, 194)), BorderThickness = new Thickness(1), Child = panel };
        _timer.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        MouseEnter += (_, _) => _timer.Stop(); MouseLeave += (_, _) => StartAutoCloseTimer();
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => PlaceNearTray(); SizeChanged += (_, _) => { if (IsVisible) PlaceNearTray(); };
    }

    public void Present(IReadOnlyList<ResetAnnouncement> items, int? autoCloseSeconds = null)
    {
        foreach (var item in items) _items[item.Id] = item;
        var newest = _items.Values.OrderByDescending(i => i.UpdatedAt).Take(20).ToArray();
        _items.Clear(); foreach (var item in newest) _items[item.Id] = item;
        _message.Text = string.Join("\n\n", newest.Take(3).Select(i => ResetWatchViewModel.StatusLabel(i.Status) + " · " + ResetWatchViewModel.EvidenceLabel(i.Evidence) + "\n" + i.Title));
        if (newest.Length > 3) _message.Text += L($"\n\n另有 {newest.Length - 3} 項更新。", $"\n\n{newest.Length - 3} more updates.");
        _credit.Visibility = newest.Any(i => i.Evidence == ResetEvidence.Tracker) ? Visibility.Visible : Visibility.Collapsed;
        _timer.Stop();
        _autoCloseSeconds = autoCloseSeconds is > 0 ? autoCloseSeconds : null;
        if (_autoCloseSeconds is { } seconds) _timer.Interval = TimeSpan.FromSeconds(seconds);
        if (!IsVisible) Show(); PlaceNearTray(); StartAutoCloseTimer();
    }

    private void StartAutoCloseTimer()
    {
        if (_autoCloseSeconds is > 0 && !IsMouseOver) _timer.Start();
    }

    private void PlaceNearTray()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * scale.DpiScaleX); var height = (int)Math.Ceiling(ActualHeight * scale.DpiScaleY);
        SetWindowPos(handle, new IntPtr(-1), Math.Max(area.Left, area.Right - width - 16), Math.Max(area.Top, area.Bottom - height - 16), 0, 0, 0x1 | 0x10);
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}
