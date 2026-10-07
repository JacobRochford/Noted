using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Tests.Services;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class WindowEdgeResizerTests
{
    [TestMethod]
    public void ButtonSpecificPreviewRunsBeforeTheResizerEvenWhenRegisteredLater()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window();
            var calls = new List<string>();
            var completed = 0;
            using var resizer = new WindowEdgeResizer(window, () => completed++, () =>
            {
                calls.Add("resizer");
                return true;
            });
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    calls.Add("button-specific");
                    e.Handled = true;
                };
                window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
                {
                    calls.Add("general");
                    Assert.IsTrue(e.Handled);
                }), true);

                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });

                CollectionAssert.AreEqual(new[] { "button-specific", "resizer", "general" }, calls);
                Assert.AreEqual(ResizeEdge.None, ActiveEdge(resizer));
                Assert.AreEqual(0, completed, "A handled press must not attempt capture or complete a resize.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReleasedButtonRejectsDelayedMouseDownAndClearsStaleCapture(bool handled)
    {
        VerifyReleasedInput(Mouse.PreviewMouseDownEvent, handled);
    }

    [TestMethod]
    public void ReleasedButtonEndsResizeOnMovementWithoutWaitingForAnotherClick()
    {
        VerifyReleasedInput(Mouse.PreviewMouseMoveEvent, true);
    }

    [TestMethod]
    public void ReleasedButtonEndsResizeOnOtherButtonRelease()
    {
        VerifyReleasedInput(Mouse.PreviewMouseUpEvent, true);
    }

    private static void VerifyReleasedInput(RoutedEvent inputEvent, bool handled)
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window
            {
                Width = 200, Height = 150, Left = -10000, Top = -10000,
                Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Cursor = Cursors.Arrow
            };
            var pressed = true;
            var completed = 0;
            using var resizer = new WindowEdgeResizer(window, () => completed++, () => pressed);
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                typeof(WindowEdgeResizer).GetField("_activeEdge", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(resizer, ResizeEdge.Right);
                window.Cursor = Cursors.SizeWE;
                Assert.IsTrue(Mouse.Capture(window, CaptureMode.SubTree));
                Assert.AreSame(window, Mouse.Captured);
                var width = window.Width;
                var height = window.Height;
                pressed = false;
                MouseEventArgs input = inputEvent == Mouse.PreviewMouseMoveEvent
                    ? new MouseEventArgs(Mouse.PrimaryDevice, 0)
                    : new MouseButtonEventArgs(Mouse.PrimaryDevice, 0,
                        inputEvent == Mouse.PreviewMouseUpEvent ? MouseButton.Right : MouseButton.Left);
                input.RoutedEvent = inputEvent;
                input.Handled = handled;
                window.RaiseEvent(input);

                Assert.AreEqual(ResizeEdge.None, ActiveEdge(resizer));
                Assert.AreNotSame(window, Mouse.Captured);
                Assert.AreEqual(Cursors.Arrow, window.Cursor);
                Assert.AreEqual(width, window.Width);
                Assert.AreEqual(height, window.Height);
                Assert.AreEqual(1, completed);

                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent
                });
                Assert.AreEqual(1, completed, "Cleanup must run only once.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ResizeEdge ActiveEdge(WindowEdgeResizer resizer) =>
        (ResizeEdge)typeof(WindowEdgeResizer).GetField("_activeEdge", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resizer)!;
}
