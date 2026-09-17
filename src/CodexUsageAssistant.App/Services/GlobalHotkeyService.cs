using System.Runtime.InteropServices;
using System.Windows.Interop;
using CodexUsageAssistant.Models;

namespace CodexUsageAssistant.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource? _source;
    private readonly Func<int, uint, uint, bool> _register;
    private readonly Action<int> _unregister;
    private readonly Dictionary<(uint Modifiers, uint Key), int> _registered = [];
    private Dictionary<int, HotkeyAction> _actions = [];
    private int _nextId = 1;
    public event Action<HotkeyAction>? Invoked;

    public GlobalHotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("Codex EzMate hotkeys")
        { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WindowProc);
        _register = (id, modifiers, key) => RegisterHotKey(_source.Handle, id, modifiers | 0x4000, key);
        _unregister = id => UnregisterHotKey(_source.Handle, id);
    }

    internal GlobalHotkeyService(Func<int, uint, uint, bool> register, Action<int> unregister)
    { _register = register; _unregister = unregister; }

    public static string ActionLabel(HotkeyAction action) => action switch
    {
        HotkeyAction.SessionBrowser => LocalizationService.Pick("會話瀏覽器", "Session Browser"),
        HotkeyAction.GoalMonitor => LocalizationService.Pick("Goal 監控", "Goal monitoring"),
        HotkeyAction.ServerHost => LocalizationService.Pick("Server 代管", "Server host"),
        HotkeyAction.UsageDetails => LocalizationService.Pick("詳細用量", "Usage details"),
        HotkeyAction.Settings => LocalizationService.Pick("設定", "Settings"),
        HotkeyAction.Updates => LocalizationService.Pick("檢查更新", "Check for updates"),
        _ => LocalizationService.Pick("快捷鍵設定", "Keyboard shortcuts")
    };

    internal static bool IsValid(HotkeyBinding binding) => Enum.IsDefined(binding.Action)
        && (binding.Modifiers & ~15u) == 0 && (binding.Modifiers & 11) != 0
        && (binding.VirtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A);

    public bool TryApply(bool enabled, IEnumerable<HotkeyBinding> bindings, out string error)
    {
        error = "";
        var all = bindings.ToList();
        if (all.Any(b => !IsValid(b)) || all.Select(b => b.Action).Distinct().Count() != all.Count)
        {
            error = LocalizationService.Pick("請選擇 Ctrl、Alt 或 Win，配合字母、數字或 F1–F11。", "Choose Ctrl, Alt or Win with a letter, digit or F1–F11.");
            return false;
        }
        if (all.Select(b => (b.Modifiers, b.VirtualKey)).Distinct().Count() != all.Count)
        {
            error = LocalizationService.Pick("快捷鍵重複，請為每個功能選擇不同組合。", "Duplicate shortcut: choose a different combination for each action.");
            return false;
        }
        var desired = enabled ? all : [];
        var added = new Dictionary<(uint, uint), int>();
        foreach (var binding in desired)
        {
            var chord = (binding.Modifiers, binding.VirtualKey);
            if (_registered.ContainsKey(chord)) continue;
            var id = _nextId++;
            if (!_register(id, chord.Modifiers, chord.VirtualKey))
            {
                foreach (var registeredId in added.Values) _unregister(registeredId);
                error = ActionLabel(binding.Action) + ": " + LocalizationService.Pick("快捷鍵已被佔用或系統不允許，請選擇其他組合。", "Shortcut is in use or reserved by Windows. Choose another combination.");
                return false;
            }
            added[chord] = id;
        }
        // Keep existing registrations until every new chord has succeeded.
        foreach (var pair in added) _registered.Add(pair.Key, pair.Value);
        var keep = desired.Select(b => (b.Modifiers, b.VirtualKey)).ToHashSet();
        foreach (var chord in _registered.Keys.Where(c => !keep.Contains(c)).ToList())
        { _unregister(_registered[chord]); _registered.Remove(chord); }
        _actions = desired.ToDictionary(b => _registered[(b.Modifiers, b.VirtualKey)], b => b.Action);
        return true;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            _source!.Dispatcher.BeginInvoke(new Action(() => Invoked?.Invoke(action)));
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Values) _unregister(id);
        _registered.Clear();
        _actions.Clear();
        _source?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
