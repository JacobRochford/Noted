using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class DictionaryDefinitionResizeTests
{
    [TestMethod]
    public void ResizingClampsHeightAndAccumulatesDragChangesWithoutWaitingForLayout() => WithWindow((window, model, _) =>
    {
        window.Height = 500;
        window.UpdateLayout();

        var item = model.Items.First();
        var content = Content(window, item);
        var scroll = (ScrollViewer)window.FindName("ItemsScrollViewer");
        Assert.AreEqual(200, content.MaxHeight);
        Assert.IsTrue(double.IsNaN(content.Height));
        var initialWindowHeight = window.Height;
        var initialContentHeight = content.ActualHeight;

        Drag(window, item, -10000);
        Assert.AreEqual(initialWindowHeight - Math.Max(0, initialContentHeight - 48), window.Height, 0.1);
        Assert.AreEqual(48, content.Height);
        Drag(window, item, 20);
        Drag(window, item, 20);
        Assert.AreEqual(88, content.Height);
        Drag(window, item, 100);
        Assert.AreEqual(initialWindowHeight - Math.Max(0, initialContentHeight - 48) + 140, window.Height, 0.1);
        Assert.AreEqual(188, content.Height);
        window.UpdateLayout();
        Assert.IsTrue(content.Height < scroll.ViewportHeight);
        Assert.AreEqual(content.Height, content.ActualHeight, 0.1);
    });

    [TestMethod]
    public void FractionalWindowRoundingDoesNotChangeRequestedDefinitionHeight() => WithWindow((window, model, _) =>
    {
        var item = model.Items.First();
        Drag(window, item, -10000);
        var content = Content(window, item);
        for (var index = 1; index <= 4; index++)
        {
            Drag(window, item, 0.25);
            Assert.AreEqual(48 + index * 0.25, content.Height, 0.0001);
        }
    });

    [TestMethod]
    public void UpwardDragKeepsBottomItemGripVisible() => WithWindow((window, model, _) =>
    {
        for (var index = 0; index < 20; index++)
            model.AddItem().Word = $"entry {index}";

        var item = model.Items.Last();
        item.IsExpanded = true;
        window.UpdateLayout();
        var scroll = (ScrollViewer)window.FindName("ItemsScrollViewer");
        scroll.ScrollToEnd();
        window.UpdateLayout();
        var grip = VisualTreeHelpers.FindDescendant<Thumb>(Container(window, item), "DefinitionResizeGrip")!;

        Drag(window, item, -1000);
        window.UpdateLayout();
        var gripTop = grip.TranslatePoint(new Point(), scroll).Y;
        Assert.IsTrue(gripTop >= 0);
        Assert.IsTrue(gripTop + grip.ActualHeight <= scroll.ViewportHeight);
    });

    [TestMethod]
    public void HorizontalDragResizesWindowAndDefinitionWithoutChangingItemHeight() => WithWindow((window, model, _) =>
    {
        var item = model.Items.First();
        var content = Content(window, item);
        var initialWidth = window.Width;
        var initialContentWidth = content.ActualWidth;

        Drag(window, item, 0, 70);
        window.UpdateLayout();
        Assert.AreEqual(initialWidth + 70, window.Width);
        Assert.AreEqual(initialContentWidth + 70, content.ActualWidth, 0.1);
        Assert.IsTrue(double.IsNaN(content.Height));

        Drag(window, item, 0, -10000);
        Assert.AreEqual(window.MinWidth, window.Width);
    });

    [TestMethod]
    public void ResizedHeightBelongsToEachItemAndSurvivesCollapseEditingAndFiltering() => WithWindow((window, model, _) =>
    {
        var first = model.Items.First();
        var second = model.Items.Last();
        Drag(window, first, -10000);
        Drag(window, first, 40);
        first.IsExpanded = false;
        window.UpdateLayout();
        first.IsExpanded = true;
        first.BeginEdit();
        window.UpdateLayout();
        Assert.AreEqual(88, Content(window, first).ActualHeight, 0.1);
        Assert.IsTrue(double.IsPositiveInfinity(Content(window, first).MaxHeight));
        Assert.IsTrue(double.IsNaN(Content(window, second).Height));
        second.BeginEdit();
        window.UpdateLayout();
        Assert.AreEqual(130, Content(window, second).MaxHeight);

        model.SearchText = "second";
        window.UpdateLayout();
        model.SearchText = "";
        window.UpdateLayout();
        var restored = Content(window, first);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
        Assert.AreEqual(88, restored.ActualHeight, 0.1);
        Assert.IsTrue(double.IsNaN(Content(window, second).Height));
    });

    [TestMethod]
    public void ResizingDoesNotPublishDraftsAndSaveAndCancelKeepTheirBehavior() => WithWindow((window, model, service) =>
    {
        var item = model.Items.First();
        item.BeginEdit();
        item.DraftDefinition = "unsaved draft";
        window.UpdateLayout();
        Drag(window, item, -10000);
        Assert.AreEqual("original definition", item.Definition);
        Assert.AreEqual("unsaved draft", item.DraftDefinition);
        Assert.IsTrue(item.HasPendingEdit);
        Assert.AreEqual(0, service.SaveCount);

        var actions = VisualTreeHelpers.FindDescendant<StackPanel>(Container(window, item), "EditActionsPanel")!;
        Assert.IsTrue(actions.IsVisible);
        ((Button)actions.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(item.HasPendingEdit);
        Assert.AreEqual("original definition", item.DraftDefinition);
        Assert.AreEqual(48, Content(window, item).Height);

        item.BeginEdit();
        item.DraftDefinition = "saved edit";
        window.UpdateLayout();
        Drag(window, item, 20);
        ((Button)actions.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(item.HasPendingEdit);
        Assert.IsTrue(window.TryFlushPendingContent(out var error), error);
        Assert.AreEqual("saved edit", service.Saved.First().Description);
        Assert.AreEqual(68, Content(window, item).Height);
    });

    private static void Drag(DictionaryWindow window, DictionaryItem item, double change, double horizontalChange = 0)
    {
        var grip = VisualTreeHelpers.FindDescendant<Thumb>(Container(window, item), "DefinitionResizeGrip")!;
        var args = new DragDeltaEventArgs(horizontalChange, change) { RoutedEvent = Thumb.DragDeltaEvent };
        grip.RaiseEvent(args);
        Assert.IsTrue(args.Handled);
    }

    private static DependencyObject Container(DictionaryWindow window, DictionaryItem item) =>
        (DependencyObject)((ItemsControl)window.FindName("ItemsList")).ItemContainerGenerator.ContainerFromItem(item);

    private static Grid Content(DictionaryWindow window, DictionaryItem item) =>
        VisualTreeHelpers.FindDescendant<Grid>(Container(window, item), "DefinitionContent")!;

    private static void WithWindow(Action<DictionaryWindow, DictionaryWindowViewModel, RecordingContentService> action) => WpfTestHost.Invoke(() =>
    {
        using var directory = new TemporaryTestDirectory();
        var service = new RecordingContentService();
        var window = new DictionaryWindow(new AppSettingsService(directory.Path), service) { Height = 700 };
        var model = (DictionaryWindowViewModel)window.DataContext;
        try
        {
            foreach (var item in model.Items) item.IsExpanded = true;
            window.Show();
            window.UpdateLayout();
            action(window, model, service);
        }
        finally
        {
            foreach (var item in model.Items.Where(item => item.IsEditing)) item.CancelEdit();
            window.Close();
        }
    });

    private sealed class RecordingContentService : IDictionaryContentService
    {
        public int SaveCount { get; private set; }
        public IReadOnlyList<DictionaryItemState> Saved { get; private set; } = [];

        public DictionaryContentLoadResult LoadItems() => new(
            [new DictionaryItemState { Word = "first", Description = "original definition" },
             new DictionaryItemState { Word = "second", Description = "another definition" }], []);

        public DictionaryContentSaveResult SaveItems(IReadOnlyList<DictionaryItemState> items)
        {
            SaveCount++;
            Saved = items.ToList();
            return new DictionaryContentSaveResult(null);
        }
    }
}
