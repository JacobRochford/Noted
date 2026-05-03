using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyNotes.Services;

/// <summary>Manages global system-wide hotkeys for the application.</summary>
public sealed class GlobalHotkeysService : IDisposable {
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9001;

    // Modifier key flags for RegisterHotKey
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private readonly Window _window;
    private HwndSource? _hwndSource;
    private bool _isRegistered = false;
    private Action? _onHotkeyPressed;

    public GlobalHotkeysService(Window window) {
        _window = window;
    }

    /// <summary>Registers a global hotkey. Hotkey format: "Ctrl+Shift" + "Space" etc.</summary>
    public bool Register(string modifiersString, string keyString, Action onHotkeyPressed) {
        try {
            _onHotkeyPressed = onHotkeyPressed;

            // Parse modifier keys
            uint modifiers = ParseModifiers(modifiersString);

            // Parse the virtual key
            uint virtualKey = ParseVirtualKey(keyString);

            if (virtualKey == 0) {
                System.Diagnostics.Debug.WriteLine($"Failed to parse virtual key: {keyString}");
                return false;
            }

            // Get window handle
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero) {
                System.Diagnostics.Debug.WriteLine("Window handle not available yet");
                return false;
            }

            // Register with Windows
            if (!RegisterHotKey(hwnd, HOTKEY_ID, modifiers, virtualKey)) {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"RegisterHotKey failed with error: {error}");
                return false;
            }

            // Hook into window messages
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(HwndHook);

            _isRegistered = true;
            System.Diagnostics.Debug.WriteLine($"Global hotkey registered: {modifiersString}+{keyString}");
            return true;
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"Error registering hotkey: {ex.Message}");
            return false;
        }
    }

    /// <summary>Unregisters the global hotkey.</summary>
    public void Unregister() {
        if (!_isRegistered)
            return;

        try {
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero) {
                UnregisterHotKey(hwnd, HOTKEY_ID);
            }

            _hwndSource?.RemoveHook(HwndHook);
            _hwndSource = null;
            _isRegistered = false;
            System.Diagnostics.Debug.WriteLine("Global hotkey unregistered");
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"Error unregistering hotkey: {ex.Message}");
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID) {
            _onHotkeyPressed?.Invoke();
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private uint ParseModifiers(string modifiersString) {
        uint modifiers = 0;
        var parts = modifiersString.Split('+');

        foreach (var part in parts) {
            string trimmed = part.Trim().ToLower();
            modifiers |= trimmed switch {
                "alt" => MOD_ALT,
                "ctrl" or "control" => MOD_CONTROL,
                "shift" => MOD_SHIFT,
                "win" or "windows" => MOD_WIN,
                _ => 0
            };
        }

        return modifiers;
    }

    private uint ParseVirtualKey(string keyString) {
        string key = keyString.Trim().ToUpper();

        // Common keys
        return key switch {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PRIOR" => 0x21,
            "PAGEDOWN" or "NEXT" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "A" => 0x41,
            "B" => 0x42,
            "C" => 0x43,
            "D" => 0x44,
            "E" => 0x45,
            "F" => 0x46,
            "G" => 0x47,
            "H" => 0x48,
            "I" => 0x49,
            "J" => 0x4A,
            "K" => 0x4B,
            "L" => 0x4C,
            "M" => 0x4D,
            "N" => 0x4E,
            "O" => 0x4F,
            "P" => 0x50,
            "Q" => 0x51,
            "R" => 0x52,
            "S" => 0x53,
            "T" => 0x54,
            "U" => 0x55,
            "V" => 0x56,
            "W" => 0x57,
            "X" => 0x58,
            "Y" => 0x59,
            "Z" => 0x5A,
            "0" or "ZERO" => 0x30,
            "1" or "ONE" => 0x31,
            "2" or "TWO" => 0x32,
            "3" or "THREE" => 0x33,
            "4" or "FOUR" => 0x34,
            "5" or "FIVE" => 0x35,
            "6" or "SIX" => 0x36,
            "7" or "SEVEN" => 0x37,
            "8" or "EIGHT" => 0x38,
            "9" or "NINE" => 0x39,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => 0
        };
    }

    public void Dispose() {
        Unregister();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
