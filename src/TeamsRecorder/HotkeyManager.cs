using System.Runtime.InteropServices;

namespace TeamsRecorder;

/// <summary>
/// Registers a global hotkey (default Ctrl+Alt+R) on a hidden NativeWindow and
/// raises <see cref="HotkeyPressed"/> when it fires (WM_HOTKEY).
/// </summary>
public partial class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    // MOD_* flags for RegisterHotKey
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private static readonly int HotkeyId = 0xA11CE;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HiddenWindow : NativeWindow
    {
        private readonly HotkeyManager _owner;

        public HiddenWindow(HotkeyManager owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                _owner.OnHotkey();
            base.WndProc(ref m);
        }
    }

    private readonly HiddenWindow _window;
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    public HotkeyManager()
    {
        _window = new HiddenWindow(this);
    }

    /// <summary>
    /// Registers the hotkey described by <paramref name="hotkey"/> (e.g. "Ctrl+Alt+R").
    /// Returns false (and reports the reason via <paramref name="error"/> if the key
    /// is already taken by another app).
    /// </summary>
    public bool TryRegister(string hotkey, out string? error)
    {
        error = null;
        Unregister();

        var (modifiers, vk) = ParseHotkey(hotkey);
        if (vk == 0)
        {
            error = $"Unrecognized hotkey: '{hotkey}'";
            return false;
        }

        if (!RegisterHotKey(_window.Handle, HotkeyId, modifiers | MOD_NOREPEAT, vk))
        {
            var winError = Marshal.GetLastWin32Error();
            error = winError == 1409
                ? $"Hotkey '{hotkey}' is already in use by another application."
                : $"RegisterHotKey failed for '{hotkey}' (Win32 error {winError}).";
            return false;
        }

        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }
    }

    private void OnHotkey() => HotkeyPressed?.Invoke();

    /// <summary>Parses "Ctrl+Alt+Shift+Win+Key" into MOD flags and a virtual key code.</summary>
    public static (uint modifiers, uint vk) ParseHotkey(string hotkey)
    {
        var modifiers = 0u;
        string? keyPart = null;

        foreach (var rawPart in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    keyPart = rawPart;
                    break;
            }
        }

        var vk = 0u;
        if (keyPart is not null)
        {
            if (keyPart.Length == 1)
            {
                var c = char.ToUpperInvariant(keyPart[0]);
                vk = c is >= 'A' and <= 'Z'
                    ? (uint)(c - 'A' + 0x41)
                    : c is >= '0' and <= '9'
                        ? (uint)(c - '0')
                        : 0u;
            }
            else
            {
                var named = Enum.TryParse<VirtualKey>(keyPart, ignoreCase: true, out var vkEnum);
                if (named)
                    vk = (uint)vkEnum;
            }
        }

        return (modifiers, vk);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unregister();
        _window.DestroyHandle();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Subset of Win32 virtual key codes (user32 VK_*).</summary>
public enum VirtualKey : uint
{
    VK_BACK = 0x08,
    VK_TAB = 0x09,
    VK_RETURN = 0x0D,
    VK_SHIFT = 0x10,
    VK_CONTROL = 0x11,
    VK_MENU = 0x12,
    VK_PAUSE = 0x13,
    VK_CAPITAL = 0x14,
    VK_SPACE = 0x20,
    VK_PRIOR = 0x21,
    VK_NEXT = 0x22,
    VK_END = 0x23,
    VK_HOME = 0x24,
    VK_LEFT = 0x25,
    VK_UP = 0x26,
    VK_RIGHT = 0x27,
    VK_DOWN = 0x28,
    VK_INSERT = 0x2D,
    VK_DELETE = 0x2E,
    VK_LWIN = 0x5B,
    VK_RWIN = 0x5C,
    VK_NUMPAD0 = 0x60,
    VK_NUMPAD9 = 0x69,
    VK_APP = 0x5D,
    VK_SLEEP = 0x5F,
}
