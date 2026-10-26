using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Search;

namespace Noted.Tests.Services;

[TestClass]
public sealed class NoteEditorScrollingTests
{
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    public void WheelEventMagnitude_DoesNotAmplifyConfiguredScrollStep(int speed, bool findOpen)
        => RunWheelTest(speed, findOpen, 0);

    [TestMethod]
    [DataRow(0x10, false)]
    [DataRow(0x11, false)]
    [DataRow(0x12, false)]
    [DataRow(0x5B, false)]
    [DataRow(0x10, true)]
    [DataRow(0x11, true)]
    [DataRow(0x12, true)]
    [DataRow(0x5B, true)]
    public void SyntheticWheelDispatchIsIndependentOfModifiers(int modifier, bool findOpen)
        => RunWheelTest(3, findOpen, modifier);

    private static void RunWheelTest(int speed, bool findOpen, int modifier)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyWheelScrolling(speed, findOpen, modifier); }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Wheel test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyWheelScrolling(int speed, bool findOpen, int modifier)
    {
        using var source = new HwndSource(new HwndSourceParameters("Wheel test")
        {
            Width = 400,
            Height = 340,
            WindowStyle = unchecked((int)0x80000000)
        });
        var editor = new TextBox
        {
            Text = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}")),
            FontSize = 14,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var root = new AdornerDecorator { Width = 400, Height = 340, Child = editor };
        source.RootVisual = root;
        void Pump()
        {
            root.Measure(new Size(400, 340));
            root.Arrange(new Rect(0, 0, 400, 340));
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        Pump();

        using var presenter = new TextBoxSearchPresenter(editor);
        var session = new TextSearchSession();
        if (findOpen)
        {
            session.Refresh(editor.Text, editor, "line", default, 0);
            presenter.BeginFind();
            presenter.Present(session, bringCurrentIntoView: false);
        }
        var current = session.CurrentMatch;

        // Exercise the handler shared by both panes without constructing application services.
        var window = RuntimeHelpers.GetUninitializedObject(typeof(NoteEditorWindow));
        typeof(NoteEditorWindow).GetField("_scrollSpeed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, speed);
        var handler = typeof(NoteEditorWindow).GetMethod("Editor_PreviewMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var calls = 0;
        editor.PreviewMouseWheel += (sender, args) =>
        {
            calls++;
            handler.Invoke(window, [sender, args]);
        };
        var host = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);

        double Scroll(int delta)
        {
            editor.ScrollToVerticalOffset(1000);
            Pump();
            var before = editor.VerticalOffset;
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent
            };
            var original = GetKeyboardState();
            try
            {
                if (modifier != 0)
                {
                    var controlled = (byte[])original.Clone();
                    controlled[modifier] = 0x80;
                    Assert.IsTrue(SetKeyboardState(controlled));
                }
                DispatchWithoutModifiers(() =>
                {
                    host.RaiseEvent(args);
                    if (!args.Handled)
                    {
                        args.RoutedEvent = Mouse.MouseWheelEvent;
                        host.RaiseEvent(args);
                    }
                });
                if (modifier != 0)
                    Assert.AreEqual((byte)0x80, GetKeyboardState()[modifier], "Restore the controlled modifier state.");
            }
            finally { Assert.IsTrue(SetKeyboardState(original)); }
            Pump();
            Assert.AreEqual(current, session.CurrentMatch, "Scrolling must not navigate Find.");
            return editor.VerticalOffset - before;
        }

        foreach (var direction in new[] { -1, 1 })
        {
            var ordinaryStep = Scroll(direction * 120);
            var amplifiedStep = Scroll(direction * 1200);
            Assert.AreEqual(ordinaryStep, amplifiedStep, 0.01,
                "A wheel event must use the configured step regardless of amplified delta magnitude.");
            if (speed > 1 && SystemParameters.WheelScrollLines > 0)
                Assert.AreEqual(-direction * SystemParameters.WheelScrollLines * editor.FontSize * 1.2 * speed,
                    ordinaryStep, 0.01, "Keep the existing configured scroll distance.");
        }
        Assert.AreEqual(4, calls, "Each routed wheel event must reach the handler once.");
    }

    private static void DispatchWithoutModifiers(Action dispatch)
    {
        var original = GetKeyboardState();
        var normalized = (byte[])original.Clone();
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            normalized[key] = 0;
        try
        {
            Assert.IsTrue(SetKeyboardState(normalized));
            Assert.AreEqual(ModifierKeys.None, Keyboard.Modifiers);
            dispatch();
        }
        finally { Assert.IsTrue(SetKeyboardState(original)); }
    }

    private static byte[] GetKeyboardState()
    {
        var state = new byte[256];
        Assert.IsTrue(GetKeyboardState(state));
        return state;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);
}
