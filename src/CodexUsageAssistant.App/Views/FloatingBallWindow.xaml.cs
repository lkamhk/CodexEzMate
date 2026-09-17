using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace CodexUsageAssistant.Views;

public partial class FloatingBallWindow : Window
{
    private const double DragThreshold = 4;
    private readonly ISettingsService _settings;
    private readonly FloatingBallViewModel _viewModel;
    private Point _mouseDownScreen;
    private Point _windowDown;
    private bool _tracking;
    private bool _dragged;
    private bool _doubleClick;

    public FloatingBallWindow(FloatingBallViewModel viewModel, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _settings = settings;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FloatingBallViewModel.IsExpanded))
            {
                Width = _viewModel.IsExpanded ? 360 : 72;
                Height = _viewModel.IsExpanded ? 310 : 72;
            }
        };
    }

    public void RestorePosition(WindowPosition? position)
    {
        var screens = Forms.Screen.AllScreens;
        var screen = position is null ? Forms.Screen.PrimaryScreen :
            screens.FirstOrDefault(s => s.DeviceName == position.MonitorDeviceName);
        screen ??= Forms.Screen.PrimaryScreen ?? screens[0];

        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var areaTopLeft = scale.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var areaBottomRight = scale.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        Left = position is null ? areaBottomRight.X - 92 : Math.Clamp(position.Left, areaTopLeft.X, areaBottomRight.X - 72);
        Top = position is null ? areaBottomRight.Y - 92 : Math.Clamp(position.Top, areaTopLeft.Y, areaBottomRight.Y - 72);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tracking = true;
        _dragged = false;
        _doubleClick = e.ClickCount >= 2;
        _mouseDownScreen = PointToScreen(e.GetPosition(this));
        _windowDown = new Point(Left, Top);
        Mouse.Capture((IInputElement)sender);
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_tracking || e.LeftButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this));
        var dx = current.X - _mouseDownScreen.X;
        var dy = current.Y - _mouseDownScreen.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < DragThreshold) return;
        _dragged = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _windowDown.X + dx / dpi.DpiScaleX;
        Top = _windowDown.Y + dy / dpi.DpiScaleY;
    }

    private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tracking) return;
        _tracking = false;
        Mouse.Capture(null);
        if (!_dragged)
        {
            if (_doubleClick) _viewModel.IsExpanded = true;
            else _viewModel.ToggleExpandedCommand.Execute(null);
            return;
        }

        var point = PointToScreen(new Point(0, 0));
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
        var dpi = VisualTreeHelper.GetDpi(this);
        try
        {
            var settings = await _settings.LoadAsync(CancellationToken.None) ?? new WindowPosition();
            settings.MonitorDeviceName = screen.DeviceName;
            settings.Left = Left;
            settings.Top = Top;
            settings.DpiScale = dpi.DpiScaleX;
            await _settings.SaveAsync(settings, CancellationToken.None);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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
