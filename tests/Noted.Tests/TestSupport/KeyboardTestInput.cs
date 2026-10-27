using System.Windows;
using System.Windows.Input;
using System.Reflection;
using System.Windows.Interop;

namespace Noted.Tests.Services;

internal static class KeyboardTestInput
{
    [ThreadStatic]
    private static TestKeyboardDevice? s_keyboard;

    internal static KeyEventArgs Press(UIElement target, Key key, ModifierKeys modifiers)
    {
        // this exercises routed handlers; native WPF command bindings are checked separately.
        var keyboard = s_keyboard ??= new TestKeyboardDevice();
        var source = PresentationSource.FromVisual(target);
        using var fallback = source is null ? new HwndSource(new HwndSourceParameters("Noted keyboard routing test")
        {
            Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000)
        }) : null;
        keyboard.SetInput(key, modifiers, source ?? fallback!);
        var args = new KeyEventArgs(keyboard, keyboard.ActiveSource, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        if (modifiers.HasFlag(ModifierKeys.Alt))
            typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(args, null);
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(args);
        }
        return args;
    }

    private sealed class TestKeyboardDevice : KeyboardDevice
    {
        private Key _key;
        private ModifierKeys _modifiers;
        private PresentationSource _source = null!;
        public override PresentationSource ActiveSource => _source;

        internal TestKeyboardDevice() : base(InputManager.Current) { }

        internal void SetInput(Key key, ModifierKeys modifiers, PresentationSource source)
        {
            _key = key;
            _modifiers = modifiers;
            _source = source;
        }

        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            var modifier = key switch
            {
                Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
                Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
                Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
                Key.LWin or Key.RWin => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
            return key == _key || modifier != ModifierKeys.None && (_modifiers & modifier) != 0
                ? KeyStates.Down : KeyStates.None;
        }
    }
}
