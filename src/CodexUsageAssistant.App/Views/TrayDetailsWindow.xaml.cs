using System.Windows;
using System.Windows.Input;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CodexUsageAssistant.ViewModels;
using Forms = System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;
using Point = System.Windows.Point;

namespace CodexUsageAssistant.Views;

public partial class TrayDetailsWindow : Window
{
    private bool _dragging;
    private bool _confirmingReset;
    private readonly System.Diagnostics.Stopwatch _planWatch = new();
    private readonly System.Windows.Threading.DispatcherTimer _planTimer = new(System.Windows.Threading.DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(33) };
    internal bool IsPlanAnimationRunning => _planTimer.IsEnabled;

    private void OnPlanVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdatePlanAnimation(e.NewValue is true);

    private void OnPlanLoaded(object sender, RoutedEventArgs e) => UpdatePlanAnimation(PlanBadge.IsVisible);
    private void OnPlanUnloaded(object sender, RoutedEventArgs e) => UpdatePlanAnimation(false);
    private void OnPlanSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlanAnimation(PlanBadge.IsVisible);

    internal void UpdatePlanAnimation(bool visible)
    {
        if (PlanSweep is null) return;
        if (visible)
        {
            _planWatch.Restart();
            PlanSweep.X = 0;
            _planTimer.Start();
        }
        else { _planTimer.Stop(); _planWatch.Stop(); PlanSweep.X = -80; }
    }

    private async void OnUseResetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FloatingBallViewModel vm || !vm.CanUseReset || _confirmingReset) return;
        _confirmingReset = true;
        try
        {
            var answer = System.Windows.MessageBox.Show(this,
                Services.LocalizationService.Pick("使用一次 reset？會優先選擇明細中最快到期的一次；若沒有可辨識明細，則由 App Server 選擇。此操作會消耗一次可用 reset。", "Use one reset? The earliest-expiring identifiable credit in the returned details is preferred; otherwise App Server selects one. This consumes one available reset."),
                Services.LocalizationService.Pick("確認使用 reset", "Confirm reset"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
            {
                await vm.UseResetAsync();
                System.Windows.MessageBox.Show(this, vm.StatusMessage,
                    Services.LocalizationService.Pick("Reset 結果", "Reset result"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally { _confirmingReset = false; }
    }
    private bool _hasUserPosition;
    private static readonly string PositionPath = Path.Combine(Services.InstallationPaths.Root, "settings", "tray-details-position.json");
    public TrayDetailsWindow(FloatingBallViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _planTimer.Tick += (_, _) =>
        {
            var phase = _planWatch.Elapsed.TotalSeconds % 2.4;
            PlanSweep.X = (Math.Max(PlanBadge.ActualWidth, 80) + 65) * Math.Min(phase / 1.8, 1);
        };
        Closed += (_, _) => _planTimer.Stop();
    }

    public void ToggleNearTray(DrawingPoint? trayAnchor = null)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        var screen = Forms.Screen.FromPoint(trayAnchor ?? Forms.Cursor.Position);
        MaxHeight = screen.WorkingArea.Height / VisualTreeHelper.GetDpi(this).DpiScaleY;
        UpdateLayout();
        if (!_hasUserPosition && !TryRestorePosition()) PositionNearTaskbar(trayAnchor ?? Forms.Cursor.Position);
        Activate();
    }

    public void HideDetails() => Hide();

    private void OnCarouselMouseChanged(object sender, System.Windows.Input.MouseEventArgs e) => UpdateCarouselArrows();

    private void OnCarouselFocusChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateCarouselArrows();

    private void UpdateCarouselArrows()
    {
        if (PreviousDetailsButton is null || NextDetailsButton is null) return;
        var visible = CarouselArea.IsMouseOver || CarouselArea.IsKeyboardFocusWithin;
        AnimateCarouselArrows(visible);
    }

    internal void AnimateCarouselArrows(bool visible)
    {
        foreach (var button in new[] { PreviousDetailsButton, NextDetailsButton })
        {
            button.IsHitTestVisible = visible;
            button.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
                visible ? 1 : 0, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void PositionNearTaskbar(DrawingPoint trayAnchor)
    {
        var screen = Forms.Screen.FromPoint(trayAnchor);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : Width) * dpi.DpiScaleX);
        var height = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : Height) * dpi.DpiScaleY);
        var target = CalculatePopupPosition(screen.Bounds, screen.WorkingArea, trayAnchor, width, height);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(handle, IntPtr.Zero, target.X, target.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    internal static DrawingPoint CalculatePopupPosition(System.Drawing.Rectangle bounds,
        System.Drawing.Rectangle workingArea, DrawingPoint anchor, int width, int height)
    {
        const int gap = 8;
        var taskbarAtBottom = workingArea.Bottom < bounds.Bottom;
        var taskbarAtTop = workingArea.Top > bounds.Top;
        var taskbarAtLeft = workingArea.Left > bounds.Left;
        var taskbarAtRight = workingArea.Right < bounds.Right;
        if (!taskbarAtBottom && !taskbarAtTop && !taskbarAtLeft && !taskbarAtRight)
        {
            var distances = new[]
            {
                (Edge: 0, Distance: Math.Abs(anchor.Y - bounds.Bottom)),
                (Edge: 1, Distance: Math.Abs(anchor.Y - bounds.Top)),
                (Edge: 2, Distance: Math.Abs(anchor.X - bounds.Left)),
                (Edge: 3, Distance: Math.Abs(anchor.X - bounds.Right))
            };
            switch (distances.MinBy(item => item.Distance).Edge)
            {
                case 0: taskbarAtBottom = true; break;
                case 1: taskbarAtTop = true; break;
                case 2: taskbarAtLeft = true; break;
                default: taskbarAtRight = true; break;
            }
        }

        var x = anchor.X - width / 2;
        var y = anchor.Y - height - gap;
        if (taskbarAtTop) y = anchor.Y + gap;
        else if (taskbarAtLeft) { x = anchor.X + gap; y = anchor.Y - height / 2; }
        else if (taskbarAtRight) { x = anchor.X - width - gap; y = anchor.Y - height / 2; }
        x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - width));
        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - height));
        return new DrawingPoint(x, y);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _dragging = true;
        try
        {
            DragMove();
            _hasUserPosition = true;
            SavePosition();
            e.Handled = true;
        }
        finally { _dragging = false; }
    }

    private bool TryRestorePosition()
    {
        try
        {
            if (!File.Exists(PositionPath)) return false;
            var coordinates = JsonSerializer.Deserialize<int[]>(File.ReadAllText(PositionPath, System.Text.Encoding.UTF8));
            if (coordinates is not { Length: 2 }) return false;
            var point = new DrawingPoint(coordinates[0], coordinates[1]);
            if (!Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(point))) return false;
            var area = Forms.Screen.FromPoint(point).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var x = Math.Clamp(point.X, area.Left, Math.Max(area.Left, area.Right - (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)));
            var y = Math.Clamp(point.Y, area.Top, Math.Max(area.Top, area.Bottom - (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _hasUserPosition = SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            return _hasUserPosition;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void SavePosition()
    {
        try
        {
            var point = PointToScreen(new Point(0, 0));
            Directory.CreateDirectory(Path.GetDirectoryName(PositionPath)!);
            File.WriteAllText(PositionPath, JsonSerializer.Serialize(new[] { (int)point.X, (int)point.Y }), System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to save details window position: {ex.Message}");
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_dragging && !_confirmingReset) Hide();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (System.Windows.Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
