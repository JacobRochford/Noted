using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyWheelScrolling(speed, findOpen); }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Wheel test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyWheelScrolling(int speed, bool findOpen)
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
            host.RaiseEvent(args);
            if (!args.Handled)
            {
                args.RoutedEvent = Mouse.MouseWheelEvent;
                host.RaiseEvent(args);
            }
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
}
