using CodexUsageAssistant.Views;

namespace CodexUsageAssistant.Services;

public sealed class SessionBrowserLauncher : IDisposable
{
    private readonly SessionAutoNamingService? _naming;
    public SessionBrowserLauncher(SessionAutoNamingService? naming = null) => _naming = naming;
    private SessionBrowserWindow? _window;
    private bool _exiting;

    public void Open()
    {
        if (_exiting) return;
        if (_window is null)
        {
            _window = new SessionBrowserWindow(naming: _naming);
            _window.Closed += (_, _) => _window = null;
        }
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    public bool CanClose()
    {
        if (_window?.IsFileOperationRunning != true) return true;
        _window.ShowBusy(); return false;
    }

    public void Dispose() { if (CanClose()) _window?.Close(); }
    public void PrepareForExit(bool exiting) { _exiting = exiting; _window?.PrepareForExit(exiting); }
}
