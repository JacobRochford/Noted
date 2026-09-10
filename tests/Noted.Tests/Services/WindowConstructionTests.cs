using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Noted.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.ViewModels;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class WindowConstructionTests
{
    [TestMethod]
    public void SharedResourcesAndChangedWindowsLoadWithoutStartingTheApplication()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var directory = new TemporaryTestDirectory();
                VerifyRenderingDuringDocumentChanges();
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var settings = new AppSettingsService(directory.Path);
                var editor = new NoteEditorWindow(new NoteContentService(directory.Path), settings,
                    new NoteRecoveryService(directory.Path), new NoteEditorSessionService(directory.Path));
                var miniPad = new MiniPadWindow(new MiniPadRecoveryService(directory.Path), settings);
                var dictionary = new DictionaryWindow(settings, new DictionaryContentService(directory.Path, settings));
                var checklist = new ChecklistWindow(settings, new ChecklistContentService(directory.Path, settings));
                var scratchpad = new ScratchpadWindow(new ScratchpadWindowViewModel(settings, new ScratchpadContentService(directory.Path)));
                foreach (var window in new Window[] { editor, miniPad, dictionary, checklist, scratchpad })
                {
                    // Build templates and bindings without displaying a window or loading user data.
                    window.Measure(new Size(window.Width, window.Height));
                    window.Arrange(new Rect(0, 0, window.Width, window.Height));
                    window.UpdateLayout();
                }
                miniPad.KeepDraftOnClose();
                editor.Close();
                miniPad.Close();
                dictionary.Close();
                checklist.Close();
                scratchpad.Close();
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Window construction did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyRenderingDuringDocumentChanges()
    {
        // A hidden native surface exercises WPF layout and drawing without opening a visible window.
        using var source = new HwndSource(new HwndSourceParameters("Noted render regression")
        {
            Width = 420, Height = 160, WindowStyle = unchecked((int)0x80000000)
        });
        var textBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"),
            FontSize = 14, Padding = new Thickness(12), BorderThickness = new Thickness(0),
            Background = Brushes.White, Foreground = Brushes.Black,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var gutter = new LineNumberGutter { Editor = textBox };
        var panel = new DockPanel { Width = 420, Height = 160 };
        DockPanel.SetDock(gutter, Dock.Left);
        panel.Children.Add(gutter);
        panel.Children.Add(textBox);
        source.RootVisual = panel;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        panel.Measure(new Size(420, 160));
        panel.Arrange(new Rect(0, 0, 420, 160));
        Assert.IsTrue(textBox.IsLoaded);

        // This layout request from SelectionChanged is the nested call in the reported crash stack.
        textBox.SelectionChanged += (_, _) => { _ = textBox.LineCount; };
        var longText = string.Join("\n", Enumerable.Repeat("a long document line that wraps when the editor is narrow", 1000));
        for (var i = 0; i < 12; i++)
        {
            textBox.Text = longText;
            panel.UpdateLayout();
            textBox.ScrollToEnd();
            panel.UpdateLayout();
            textBox.Text = i % 2 == 0 ? "one\ntwo\nthree\nfour" : "";
            panel.UpdateLayout();
            Render(panel);
        }

    }

    private static byte[] Render(FrameworkElement element)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
