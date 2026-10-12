using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Views;

namespace CodexUsageAssistant.Services;

public sealed class CodexToolbarIconService : IDisposable
{
    private readonly ToolbarIconWindow _window = new();
    private readonly DispatcherTimer _timer;
    private readonly string _settingsPath;
    private readonly Func<IntPtr, bool>? _windowMatcher;
    private readonly Action<string> _report;
    private readonly WinEventCallback _callback;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _locationHook;
    private readonly IntPtr _handle;
    private readonly ToolbarIconSettings _settings;
    private IntPtr _target;
    private IntPtr _checkedWindow;
    private uint _checkedProcess;
    private bool _checkedResult;
    private DateTime _nextIdentityCheck;
    private bool _disposed;
    private bool _refreshQueued;
    private double _scale = 1;
    public bool Enabled => _settings.Enabled;
    public bool FixedMode => _settings.FixedMode;
    public event Action<bool>? EnabledChanged;
    public event Action<bool>? FixedModeChanged;

    public CodexToolbarIconService(Action open, Action<string> report)
        : this(open, report, Path.Combine(InstallationPaths.Root, "settings", "toolbar-icon.json"), null) { }

    internal CodexToolbarIconService(Action open, Action<string> report, string settingsPath, Func<IntPtr, bool>? windowMatcher)
    {
        _settingsPath = settingsPath;
        _windowMatcher = windowMatcher;
        _report = report;
        _settings = ToolbarIconSettings.Load(_settingsPath);
        _window.SetFixedMode(_settings.FixedMode);
        _window.FixedModeRequested += SetFixedMode;
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _window.OpenRequested += () =>
        {
            // Activation is intentional only after the user clicks the icon.
            _window.Activate();
            open();
            _window.Hide();
        };
        _window.Dragged += (dx, dy) =>
        {
            if (_settings.FixedMode) return;
            _settings.Right -= dx / _scale;
            _settings.Top += dy / _scale;
            Refresh();
        };
        _window.DragCompleted += Save;
        _window.ResetRequested += () => { _settings.ResetPosition(); Save(); Refresh(); };
        _window.HideRequested += () => SetEnabled(false);
        _callback = (_, eventType, hwnd, _, _, _, _) =>
        {
            if (_disposed || _refreshQueued || (eventType != 3 && hwnd != _target)) return;
            _refreshQueued = true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                _refreshQueued = false;
                if (!_disposed) Refresh();
            }));
        };
        // Out-of-context hooks observe window state without injecting into Codex.
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
        _locationHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _callback, 0, 0, 0);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += Tick;
        _timer.Start();
        Refresh();
    }

    public void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled; Save(); Refresh(); EnabledChanged?.Invoke(enabled);
    }

    public void SetFixedMode(bool enabled)
    {
        _settings.FixedMode = enabled; _window.SetFixedMode(enabled);
        Save(); Refresh(); FixedModeChanged?.Invoke(enabled);
    }

    private void Save()
    {
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _report(LocalizationService.Pick("無法儲存浮動圖示設定：", "Could not save toolbar icon settings: ") + ex.Message); }
    }

    private void Tick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_disposed) return;
        if (!Enabled) { _window.Hide(); return; }
        var foreground = GetForegroundWindow();
        var root = GetAncestor(foreground, 2);
        if (IsCodexWindow(root)) _target = root;
        else if (!_window.IsInteracting && foreground != _handle) { _window.Hide(); return; }
        if (_target == IntPtr.Zero || !IsWindowVisible(_target) || IsIconic(_target)
            || (DwmGetWindowAttribute(_target, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            ) { _window.Hide(); return; }
        // Visible frame bounds include the title bar and exclude invisible resize borders.
        if (DwmGetFrameBounds(_target, 9, out var frame, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(_target, out frame))
        { _window.Hide(); return; }
        var width = frame.Right - frame.Left; var height = frame.Bottom - frame.Top;
        var dpi = GetDpiForWindow(_target);
        _scale = dpi == 0 ? 1 : dpi / 96.0;
        if (width < 64 * _scale || height < 64 * _scale) { _window.Hide(); return; }
        ToolbarIconBounds bounds;
        if (_settings.FixedMode)
        {
            // Electron's maximized frame includes hidden resize borders (for example -8,-8).
            // Its sidebar is laid out from the client origin, which remains the visual origin.
            var fixedBounds = GetFixedPlacement(_target, _scale);
            if (fixedBounds is null)
            { _window.Hide(); return; }
            bounds = fixedBounds.Value;
        }
        else bounds = ToolbarIconPlacement.Calculate(frame.Left, frame.Top, width, height, _scale, _settings);
        if (!_settings.FixedMode && _window.IsMouseCaptured)
        {
            // Persist the clamped position so dragging never creates an invisible offset.
            _settings.Right = (frame.Right - bounds.X - bounds.Size) / _scale;
            _settings.Top = (bounds.Y - frame.Top) / _scale;
        }
        if (!_window.IsVisible)
        {
            _window.Opacity = 0;
            _window.Show();
        }
        SetWindowPos(_handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Size, bounds.Size, 0x10 | 0x40);
        _window.Opacity = 1;
    }

    internal static ToolbarIconBounds? GetFixedPlacement(IntPtr hwnd, double scale)
    {
        var origin = new NativePoint();
        if (!GetClientRect(hwnd, out var client) || !ClientToScreen(hwnd, ref origin)) return null;
        return ToolbarIconPlacement.CalculateFixed(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top, scale);
    }

    private bool IsCodexWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _handle) return false;
        if (_windowMatcher is not null) return _windowMatcher(hwnd);
        GetWindowThreadProcessId(hwnd, out var pid);
        if (hwnd == _checkedWindow && pid == _checkedProcess && DateTime.UtcNow < _nextIdentityCheck) return _checkedResult;
        _checkedWindow = hwnd; _checkedProcess = pid; _checkedResult = false;
        _nextIdentityCheck = DateTime.UtcNow.AddSeconds(3);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            _checkedResult = CodexDesktopIdentity.Matches(process.ProcessName, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
        return _checkedResult;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= Tick;
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) UnhookWinEvent(_locationHook);
        _window.Close();
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetFrameBounds(IntPtr hwnd, int attribute, out NativeRect value, int size);
}
