using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Reflection;
using System.IO;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class JotWindowTests
{
    [TestMethod]
    public void EnterWaitsForCompositionAndEscapeFlushesDraftBeforeHiding() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var content = new JotsContentService(directory.Path);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        var model = new JotCaptureViewModel(workspace, new JotCaptureDraftService(directory.Path), Dispatcher.CurrentDispatcher);
        var popup = new JotCaptureWindow(model);
        try
        {
            popup.ShowCapture();
            model.Text = "Composed thought";
            var textBox = (TextBox)popup.FindName("CaptureTextBox");
            var composition = new TextComposition(InputManager.Current, textBox, "thought");
            textBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent
            });
            PressKey(popup, Key.Enter);
            Assert.IsTrue(popup.IsVisible);
            Assert.IsFalse(workspace.IsLoaded);
            Assert.AreEqual("Composed thought", model.Text);
            textBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputEvent
            });
            PressKey(popup, Key.Enter);
            Assert.IsFalse(popup.IsVisible);
            Assert.AreEqual("Composed thought", content.LoadItems().Items.Single().Details);
            popup.ShowCapture();
            model.Text = "Draft for next time";
            PressKey(popup, Key.Escape);
            Assert.IsFalse(popup.IsVisible);
            Assert.AreEqual("Draft for next time", new JotCaptureDraftService(directory.Path).Load(out _).Text);
        }
        finally
        {
            popup.PrepareForShutdown();
            popup.Close();
        }
    });

    private static void PressKey(JotCaptureWindow popup, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        typeof(JotCaptureWindow).GetMethod("OnPreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(popup, [args]);
    }

    [TestMethod]
    public void DeletedJotWindowClosesEvenWhenDeletionCannotYetBePersisted() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Title = "Delete me" }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        WindowManager.ClearAll();
        WindowManager.JotProvider = id => new JotsWindow(settings, workspace, workspace.Items.Single(item => item.Id == id));
        try
        {
            WindowManager.ShowJot(workspace.Items.Single().Id);
            using (File.Open(directory.File("jots.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                workspace.Items.RemoveAt(0);
                Assert.AreEqual(0, WindowManager.JotWindows.Count);
                Assert.IsFalse(workspace.TryFlush(out _));
                Assert.AreEqual(0, workspace.Items.Count);
            }
            Assert.IsTrue(workspace.TryFlush(out var error), error);
            Assert.AreEqual(0, content.LoadItems().Items.Count);
        }
        finally
        {
            foreach (var window in WindowManager.JotWindows) window.Close();
            WindowManager.ClearAll();
        }
    });

    [TestMethod]
    public void ReopeningSavedJotReusesWindowAndClosingOneLeavesOthersOpen() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var settings = new AppSettingsService(directory.Path);
        var content = new JotsContentService(directory.Path);
        content.SaveItems([new JotItemState { Title = "First" }, new JotItemState { Title = "Second" }]);
        using var workspace = new JotsWorkspace(content, Dispatcher.CurrentDispatcher);
        WindowManager.ClearAll();
        var collection = new JotsWindow(settings, workspace);
        WindowManager.Jots = collection;
        var creates = 0;
        WindowManager.JotProvider = id =>
        {
            creates++;
            return workspace.Items.FirstOrDefault(item => item.Id == id) is { } item
                ? new JotsWindow(settings, workspace, item) : null;
        };
        try
        {
            WindowManager.ShowJot(workspace.Items[0].Id);
            var first = WindowManager.JotWindows.Single();
            WindowManager.ShowJot(workspace.Items[0].Id);
            Assert.AreEqual(1, creates);
            Assert.AreSame(first, WindowManager.JotWindows.Single());
            WindowManager.ShowJot(workspace.Items[1].Id);
            var second = WindowManager.JotWindows.Single(window => window != first);
            first.Left = 330;
            first.Top = 220;
            first.Close();
            Assert.AreEqual(1, WindowManager.JotWindows.Count);
            Assert.IsTrue(second.IsVisible);
            Assert.AreSame(collection, WindowManager.Jots);
            Assert.AreEqual(330, settings.LoadJotWindowState(workspace.Items[0].Id).Left);
            workspace.Items.Remove(workspace.Items[1]);
            Assert.AreEqual(0, WindowManager.JotWindows.Count);
            Assert.AreSame(collection, WindowManager.Jots);
        }
        finally
        {
            foreach (var window in WindowManager.JotWindows) window.Close();
            collection.Close();
            WindowManager.ClearAll();
        }
    });

    [TestMethod]
    public void CaptureShortcutReusesOnePopupAndRetainsHiddenTextWithoutLoadingJots() => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        using var workspace = new JotsWorkspace(new JotsContentService(directory.Path), Dispatcher.CurrentDispatcher);
        WindowManager.ClearAll();
        var creates = 0;
        WindowManager.JotCaptureProvider = () =>
        {
            creates++;
            return new JotCaptureWindow(new JotCaptureViewModel(workspace,
                new JotCaptureDraftService(directory.Path), Dispatcher.CurrentDispatcher));
        };
        try
        {
            WindowManager.ShowJotCapture();
            var popup = WindowManager.JotCapture!;
            var textBox = (TextBox)popup.FindName("CaptureTextBox");
            textBox.Text = "Unfinished thought\r\nAnother line";
            Assert.IsTrue(popup.TryFlushDraft(out var error), error);
            popup.Hide();
            WindowManager.ShowJotCapture();
            WindowManager.ShowJotCapture();
            Assert.AreEqual(1, creates);
            Assert.AreSame(popup, WindowManager.JotCapture);
            Assert.AreEqual("Unfinished thought\r\nAnother line", textBox.Text);
            Assert.IsFalse(workspace.IsLoaded);
            Assert.IsNull(WindowManager.Jots);
        }
        finally
        {
            if (WindowManager.JotCapture is { } popup)
            {
                popup.PrepareForShutdown();
                popup.Close();
            }
            WindowManager.ClearAll();
        }
    });
}
