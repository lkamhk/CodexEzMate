using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CodexUsageAssistant.Services;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;

namespace CodexUsageAssistant.Views;

public sealed class ToolbarIconWindow : Window
{
    private readonly Border _surface;
    private readonly ContextMenu _menu = new();
    private readonly MenuItem _open = new();
    private readonly MenuItem _reset = new();
    private readonly MenuItem _hide = new();
    private Point _down;
    private bool _dragged;
    public bool IsInteracting => IsMouseCaptured || _menu.IsOpen;
    public event Action? OpenRequested;
    public event Action<double, double>? Dragged;
    public event Action? DragCompleted;
    public event Action? ResetRequested;
    public event Action? HideRequested;

    public ToolbarIconWindow()
    {
        Title = "Codex EzMate v1.21.9 — Session Browser icon";
        Width = Height = 32;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        var artwork = (ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/CodexEzMate;component/Assets/EzMate-toolbar-mono.xaml", UriKind.Relative));
        var logo = (DrawingImage)artwork["EzMateToolbarLogo"];
        logo.Freeze();
        _surface = new Border
        {
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ContextMenu = _menu,
            Opacity = 0.94,
            Child = new Image
            {
                Source = logo,
                Stretch = Stretch.Uniform, IsHitTestVisible = false
            }
        };
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        Content = _surface;
        _menu.Items.Add(_open); _menu.Items.Add(_reset); _menu.Items.Add(new Separator()); _menu.Items.Add(_hide);
        _open.Click += (_, _) => OpenRequested?.Invoke();
        _reset.Click += (_, _) => ResetRequested?.Invoke();
        _hide.Click += (_, _) => HideRequested?.Invoke();
        MouseEnter += (_, _) => _surface.Opacity = 1;
        MouseLeave += (_, _) => _surface.Opacity = 0.94;
        MouseLeftButtonDown += (_, e) =>
        {
            _down = PointToScreen(e.GetPosition(this)); _dragged = false;
            CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            var point = PointToScreen(e.GetPosition(this));
            var delta = point - _down;
            if (!_dragged && Math.Abs(delta.X) + Math.Abs(delta.Y) < 5) return;
            _dragged = true; _down = point;
            Dragged?.Invoke(delta.X, delta.Y);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture(); e.Handled = true;
            if (_dragged) DragCompleted?.Invoke();
            else OpenRequested?.Invoke();
        };
        SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowMessage);
        };
        LocalizationService.LanguageChanged += LanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= LanguageChanged;
        UpdateLabels();
    }

    private static IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Hovering and dragging must not steal focus from the Codex composer.
        if (message == 0x21) { handled = true; return new IntPtr(3); }
        return IntPtr.Zero;
    }

    private void LanguageChanged(Models.AppLanguage language) => UpdateLabels();
    private void UpdateLabels()
    {
        _surface.ToolTip = LocalizationService.Pick("開啟會話瀏覽器 · 拖曳調位 · 右鍵設定", "Open Session Browser · Drag to reposition · Right-click for options");
        _open.Header = LocalizationService.Pick("開啟會話瀏覽器", "Open Session Browser");
        _reset.Header = LocalizationService.Pick("重設圖示位置", "Reset icon position");
        _hide.Header = LocalizationService.Pick("隱藏圖示（可從通知區重新開啟）", "Hide icon (enable again from the tray)");
        System.Windows.Automation.AutomationProperties.SetName(_surface, LocalizationService.Pick("會話瀏覽器", "Session Browser"));
    }
}
