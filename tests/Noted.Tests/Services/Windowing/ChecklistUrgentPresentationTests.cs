using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Noted.Models;
using Noted.Services;
using Noted.ViewModels;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ChecklistUrgentPresentationTests
{
    private static readonly DateTimeOffset s_now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void GroupingUsesExistingItemsAndAcknowledgesEachOccurrenceSeparately() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var owner = Owner(store);
        var first = TaskItem(); var second = TaskItem(); owner.Items.Add(first); owner.Items.Add(second);
        using var presentation = Presentation(owner);
        Assert.IsTrue(presentation.TryAccept([Request(first), Request(second)]));
        var retained = presentation.Entries[0];
        Assert.AreSame(first, retained.Item);
        Assert.IsTrue(presentation.TryAccept([Request(first)]));
        Assert.AreEqual(2, presentation.Entries.Count); Assert.AreSame(retained, presentation.Entries[0]);
        Assert.IsTrue(presentation.TryAcknowledge(retained));
        Assert.AreEqual(1, presentation.Entries.Count);
        Assert.IsFalse(first.IsChecked); Assert.IsFalse(second.IsChecked);
        Assert.IsNotNull(first.ReminderOccurrence!.AcknowledgedAtUtc);
        Assert.IsNull(second.ReminderOccurrence!.AcknowledgedAtUtc);
    });

    [TestMethod]
    public void FailedActionsKeepVisibleEntriesEvenWithReentrantRefresh() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var owner = Owner(store);
        var item = TaskItem(); owner.Items.Add(item);
        using var presentation = Presentation(owner);
        Assert.IsTrue(presentation.TryAccept([Request(item)]));
        var row = presentation.Entries.Single();
        store.Fail = true; store.DuringSave = presentation.Refresh;
        Assert.IsFalse(presentation.TryAcknowledge(row));
        Assert.AreSame(row, presentation.Entries.Single());
        Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc);
        Assert.IsNotNull(presentation.StatusMessage);
        Assert.IsFalse(presentation.TrySilence(row));
        Assert.IsNull(item.ReminderOccurrence.SilencedAtUtc);
        store.Fail = false; store.DuringSave = null;
        Assert.IsTrue(presentation.TrySilence(row));
        Assert.AreSame(row, presentation.Entries.Single());
        Assert.IsTrue(row.IsSilenced); Assert.IsNull(item.ReminderOccurrence.AcknowledgedAtUtc);
        Assert.IsTrue(presentation.TryAcknowledge(row)); Assert.AreEqual(0, presentation.Entries.Count);
    });

    [TestMethod]
    public void OpenTaskDoesNotAcknowledgeAndKeepsTheSameEntry() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); owner.Items.Add(item);
        using var presentation = Presentation(owner);
        presentation.TryAccept([Request(item)]); Guid? opened = null;
        presentation.OpenTaskRequested += id => opened = id;
        var row = presentation.Entries.Single(); presentation.OpenTask(row);
        Assert.AreEqual(item.Id, opened); Assert.AreSame(row, presentation.Entries.Single());
        Assert.IsFalse(item.IsChecked); Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc);
    });

    [TestMethod]
    public void CancellationReschedulingAndDeletionRemoveOldRowsAndRejectStaleActions() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); owner.Items.Add(item);
        using var presentation = Presentation(owner);
        var oldRequest = Request(item); presentation.TryAccept([oldRequest]); var oldRow = presentation.Entries.Single();
        item.ReminderAtUtc = s_now.AddHours(-2); presentation.Refresh();
        Assert.AreEqual(0, presentation.Entries.Count);
        Assert.IsFalse(presentation.TryAcknowledge(oldRow));
        Assert.IsFalse(presentation.TryAccept([oldRequest]));
        Assert.IsTrue(presentation.TryAccept([Request(item)]));
        item.IsChecked = true; presentation.Refresh(); Assert.AreEqual(0, presentation.Entries.Count);
        item.IsChecked = false; item.ReminderAtUtc = s_now.AddMinutes(-1);
        presentation.TryAccept([Request(item)]); owner.RemoveItem(item); presentation.Refresh();
        Assert.AreEqual(0, presentation.Entries.Count);
    });

    [TestMethod]
    public void TextNotesOriginalTimeAndOverdueDetailsRemainCurrent() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); item.ReminderAtUtc = s_now.AddHours(-2);
        owner.Items.Add(item); var code = item.ReminderOccurrence!.ConfirmationCode;
        using var presentation = Presentation(owner); presentation.TryAccept([Request(item)]);
        var row = presentation.Entries.Single();
        Assert.AreEqual(s_now.AddHours(-2), row.ScheduledAtUtc);
        Assert.AreEqual("Overdue by 2h 0m", row.OverdueText);
        item.Text = "edited title"; item.Notes = "long details"; presentation.Refresh();
        Assert.AreEqual("edited title", row.Title); Assert.AreEqual("long details", row.Item.Notes);
        Assert.AreEqual(code, item.ReminderOccurrence.ConfirmationCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(row.ScheduledText));
    });

    [TestMethod]
    public void AcceptedUrgentStateRecoversAfterPresenterDisposalWithoutAcknowledgment() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); Guid occurrence;
        using (var owner = Owner(store))
        {
            var item = TaskItem(); owner.Items.Add(item); occurrence = item.ReminderOccurrence!.Id;
            using var presentation = Presentation(owner);
            using var scheduler = new ChecklistReminderScheduler(owner, Dispatcher.CurrentDispatcher, _ => false,
                new Clock(), presentation.TryAccept);
            Assert.AreEqual(1, presentation.Entries.Count);
            Assert.IsNotNull(store.Items.Single().ReminderHandledAtUtc);
        }
        using var recovered = Owner(store); using var next = Presentation(recovered);
        using var restarted = new ChecklistReminderScheduler(recovered, Dispatcher.CurrentDispatcher, _ => false,
            new Clock(), next.TryAccept);
        Assert.AreEqual(occurrence, next.Entries.Single().OccurrenceId);
        Assert.IsNull(next.Entries.Single().Item.ReminderOccurrence!.AcknowledgedAtUtc);
    });

    [TestMethod]
    public void UnavailableDesktopDefersDeliveryAndDisposalDetachesPresentation() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); owner.Items.Add(item);
        using var presenter = new ChecklistUrgentReminderPresenter(owner, Dispatcher.CurrentDispatcher, _ => { }, () => false);
        Assert.IsFalse(presenter.TrySubmit([Request(item)]));
        Assert.AreEqual(0, presenter.ViewModel.Entries.Count);
        Assert.IsNull(item.ReminderHandledAtUtc);
        presenter.Dispose(); Assert.IsFalse(presenter.TrySubmit([Request(item)]));
    });

    [TestMethod]
    public void WindowHasNoDefaultAcknowledgmentAndClosingDoesNotDiscardEntries() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); owner.Items.Add(item);
        using var presentation = Presentation(owner); presentation.TryAccept([Request(item)]);
        var window = new ChecklistUrgentReminderWindow(presentation);
        try
        {
            Assert.IsTrue(window.Topmost); Assert.AreEqual(1d, window.Opacity);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1000, 700)); content.Arrange(new Rect(0, 0, 1000, 700)); content.UpdateLayout();
            var buttons = Descendants(content).OfType<Button>().ToArray();
            Assert.IsTrue(buttons.Any(button => Equals(button.Content, "Acknowledge")));
            Assert.IsTrue(buttons.All(button => !button.IsDefault && !button.IsCancel));
            var acknowledge = buttons.Single(button => Equals(button.Content, "Acknowledge"));
            acknowledge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc, "Unarmed input must not acknowledge.");
            var closing = new CancelEventArgs();
            typeof(ChecklistUrgentReminderWindow).GetMethod("OnClosing", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [closing]);
            Assert.IsTrue(closing.Cancel);
            Assert.AreEqual(1, presentation.Entries.Count);
            Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc);
        }
        finally { window.CloseForShutdown(); }
        Assert.AreEqual(1, presentation.Entries.Count);
    });

    [TestMethod]
    public void FullScreenScalesDetailsAndActionsWhileCenteredStaysCompact() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store());
        var item = TaskItem();
        item.Text = "Read this reminder";
        item.Notes = "Reminder details";
        owner.Items.Add(item);
        using var presentation = Presentation(owner);
        presentation.TryAccept([Request(item)]);

        var small = MeasureAlert(ChecklistAlertSize.FullScreen, 840, 600);
        var large = MeasureAlert(ChecklistAlertSize.FullScreen, 1600, 950);
        var centered = MeasureAlert(ChecklistAlertSize.Large, 1100, 800);
        var measurements = $"Small full-screen: {small.Summary}{Environment.NewLine}" +
                           $"Large full-screen: {large.Summary}{Environment.NewLine}" +
                           $"Centered: {centered.Summary}";
        Assert.IsTrue(large.Width > centered.Width && large.Width > small.Width, $"Expected the large full-screen details panel to be widest.{Environment.NewLine}{measurements}");
        Assert.IsTrue(large.Title > centered.Title && large.Title > small.Title, $"Expected the large full-screen title font to be largest.{Environment.NewLine}{measurements}");
        Assert.IsTrue(large.Details > centered.Details && large.Details > small.Details, $"Expected the large full-screen detail font to be largest.{Environment.NewLine}{measurements}");
        Assert.IsTrue(large.ActionHeight > centered.ActionHeight && large.ActionHeight > small.ActionHeight, $"Expected the large full-screen action button to be tallest.{Environment.NewLine}{measurements}");

        (double Width, double Title, double Details, double ActionHeight, string Summary)
            MeasureAlert(ChecklistAlertSize size, double width, double height)
        {
            var window = new ChecklistUrgentReminderWindow(presentation, options: new() { Size = size }) { Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                var panel = (FrameworkElement)window.FindName("DetailsPanel");
                var text = Descendants(panel).OfType<TextBlock>().ToArray();
                var title = text.Single(block => block.Text == item.Text);
                var details = text.Single(block => block.Text == item.Notes);
                var action = Descendants(panel).OfType<Button>().Single(button => Equals(button.Content, "Acknowledge"));
                var dpi = VisualTreeHelper.GetDpi(window);
                var summary = $"requested window {width}x{height}, actual window {window.ActualWidth}x{window.ActualHeight}, " +
                              $"DPI scale {dpi.DpiScaleX}x{dpi.DpiScaleY}, panel width {panel.ActualWidth} (max {panel.MaxWidth}), " +
                              $"title font {title.FontSize}, detail font {details.FontSize}, action button height {action.ActualHeight}";
                return (panel.ActualWidth, title.FontSize, details.FontSize, action.ActualHeight, summary);
            }
            finally { window.CloseForShutdown(); }
        }
    });

    [TestMethod]
    public void DeliveredUrgentRemainsActionableAfterClockMovesBeforeScheduledTime() => WpfTestHost.Invoke(() =>
    {
        var store = new Store(); using var owner = Owner(store); var item = TaskItem();
        item.ReminderAtUtc = s_now.AddHours(1);
        item.ReminderHandledAtUtc = s_now.AddHours(1);
        owner.Items.Add(item);
        using var presentation = Presentation(owner);
        using var scheduler = new ChecklistReminderScheduler(owner, Dispatcher.CurrentDispatcher, _ => false,
            new Clock(), presentation.TryAccept);
        Assert.AreEqual(1, presentation.Entries.Count, "An already delivered urgent occurrence must survive a clock rollback.");
        presentation.Entries.Single().ConfirmationInput = item.ReminderOccurrence!.ConfirmationCode;
        Assert.IsTrue(presentation.TryAcknowledge(presentation.Entries.Single()));
        Assert.IsFalse(item.IsChecked);
    });

    [TestMethod]
    public void FutureUndeliveredUrgentCannotBePresentedOrAcknowledgedEarly() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); item.ReminderAtUtc = s_now.AddHours(1);
        owner.Items.Add(item); using var presentation = Presentation(owner);
        Assert.IsFalse(presentation.TryAccept([Request(item)]));
        Assert.IsFalse(owner.TryAcknowledgeUrgentReminder(item.Id, item.ReminderOccurrence!.Id, s_now, out _));
        Assert.AreEqual(0, presentation.Entries.Count);
    });

    [TestMethod]
    [TestCategory("DesktopInteraction")]
    public void MinimizedInspectionWindowRestoresWithoutLeavingCompactMode() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store()); var item = TaskItem(); owner.Items.Add(item);
        using var presentation = Presentation(owner); presentation.TryAccept([Request(item)]);
        var window = new ChecklistUrgentReminderWindow(presentation);
        try
        {
            window.ShowCompact(); window.ShowAlert();
            window.WindowState = WindowState.Minimized;
            window.ShowAlert();
            Assert.AreEqual(WindowState.Normal, window.WindowState);
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)window.FindName("CompactPanel")).Visibility);
            Assert.IsNull(item.ReminderOccurrence!.AcknowledgedAtUtc);
        }
        finally { window.CloseForShutdown(); }
    });

    [TestMethod]
    [TestCategory("DesktopInteraction")]
    public void SwitchingHoldControlsRetainsNewHoldAndRejectsMismatchedRelease() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store());
        foreach (var title in new[] { "first", "second" })
        {
            var item = TaskItem(); item.Text = title;
            item.ReminderOccurrence = item.ReminderOccurrence! with
            { Options = new() { Acknowledgment = ChecklistAcknowledgment.Hold, Size = ChecklistAlertSize.Percentage, ScreenPercent = 30, UrgentSound = ChecklistUrgentSound.Silent } };
            owner.Items.Add(item);
        }
        using var model = new ChecklistUrgentRemindersViewModel(owner, Dispatcher.CurrentDispatcher, new Clock(), isPreview: true);
        model.TryAccept(owner.Items.Select(Request).ToArray());
        var window = new ChecklistUrgentReminderWindow(model);
        try
        {
            window.ShowAlert(); window.UpdateLayout();
            var buttons = Descendants(window).OfType<Button>()
                .Where(button => button.Content is string text && text.StartsWith("Hold for", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(2, buttons.Length);
            var begin = typeof(ChecklistUrgentReminderWindow).GetMethod("BeginHold", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var holding = typeof(ChecklistUrgentReminderWindow).GetField("_holding", BindingFlags.Instance | BindingFlags.NonPublic)!;
            begin.Invoke(window, [buttons[0], false]);
            begin.Invoke(window, [buttons[1], false]);
            Assert.AreSame(buttons[1], Keyboard.FocusedElement);
            Assert.AreSame(model.Entries[1], holding.GetValue(window));
            Assert.IsTrue(model.Entries[1].HoldStarted.HasValue);
            begin.Invoke(window, [buttons[1], true]);
            model.Entries[1].HoldStarted = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 3;
            var keyUp = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
            typeof(ChecklistUrgentReminderWindow).GetMethod("HoldKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [buttons[1], keyUp]);
            Assert.IsNull(owner.Items[1].ReminderOccurrence!.AcknowledgedAtUtc);
            Assert.AreSame(model.Entries[1], holding.GetValue(window));
            begin.Invoke(window, [buttons[1], false]);
            var mouseUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
            typeof(ChecklistUrgentReminderWindow).GetMethod("HoldMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [buttons[1], mouseUp]);
            Assert.AreSame(model.Entries[1], holding.GetValue(window));
        }
        finally { window.CloseForShutdown(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    [TestMethod]
    public void EnterInCodeFieldConfirmsOnlyThatReminderAndRejectsWrongCodeModifiersAndRepeats() => WpfTestHost.Invoke(() =>
    {
        using var owner = Owner(new Store());
        var first = TaskItem(); var second = TaskItem();
        foreach (var item in new[] { first, second })
        {
            item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new() { Acknowledgment = ChecklistAcknowledgment.Code } };
            owner.Items.Add(item);
        }
        using var presentation = new ChecklistUrgentRemindersViewModel(owner, Dispatcher.CurrentDispatcher, new Clock(), isPreview: true);
        presentation.TryAccept([Request(first), Request(second)]);
        var window = new ChecklistUrgentReminderWindow(presentation);
        try
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1000, 700)); content.Arrange(new Rect(0, 0, 1000, 700)); content.UpdateLayout();
            var input = Descendants(content).OfType<TextBox>().Single(control => control.DataContext is ChecklistUrgentReminderEntry entry && entry.Item.Id == first.Id);
            input.Text = "bad";
            KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None);
            Assert.IsNull(first.ReminderOccurrence!.AcknowledgedAtUtc);
            input.Text = first.ReminderOccurrence.ConfirmationCode;
            KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.Control);
            Assert.IsNull(first.ReminderOccurrence.AcknowledgedAtUtc);
            var repeated = KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None, previewOnly: true);
            typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repeated, [true]);
            repeated.RoutedEvent = Keyboard.KeyDownEvent;
            input.RaiseEvent(repeated);
            Assert.IsNull(first.ReminderOccurrence.AcknowledgedAtUtc);
            Assert.IsTrue(KeyboardTestInput.Press(input, Key.Enter, ModifierKeys.None).Handled);
            Assert.IsNotNull(first.ReminderOccurrence.AcknowledgedAtUtc);
            Assert.IsNull(second.ReminderOccurrence!.AcknowledgedAtUtc);
            Assert.IsFalse(first.IsChecked);
        }
        finally { window.CloseForShutdown(); }
    });

    private static ChecklistItem TaskItem()
    {
        var item = new ChecklistItem { Text = "task", NotificationStyle = ChecklistNotificationStyle.Urgent, ReminderAtUtc = s_now.AddMinutes(-1) };
        item.ReminderOccurrence = item.ReminderOccurrence! with { Options = new() { Acknowledgment = ChecklistAcknowledgment.Button } };
        return item;
    }
    private static ChecklistReminder Request(ChecklistItem item) => new(item.Id, item.ReminderAtUtc!.Value, item.Text,
        Style: ChecklistNotificationStyle.Urgent, Occurrence: item.ReminderOccurrence, Notes: item.Notes);
    private static ChecklistWindowViewModel Owner(Store store) => new(store, Dispatcher.CurrentDispatcher, action => action(), false);
    private static ChecklistUrgentRemindersViewModel Presentation(ChecklistWindowViewModel owner) => new(owner, Dispatcher.CurrentDispatcher, new Clock());
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => s_now; }
    private sealed class Store : IChecklistContentService
    {
        internal IReadOnlyList<ChecklistItemState> Items { get; set; } = [];
        internal bool Fail { get; set; }
        internal Action? DuringSave { get; set; }
        public ChecklistContentLoadResult LoadItems() => new(Items, [], []);
        public ChecklistContentSaveResult SaveItems(IReadOnlyList<ChecklistItemState> items)
        {
            DuringSave?.Invoke();
            if (Fail) throw new IOException("test disk failure");
            Items = items.ToArray(); return new(null);
        }
        public ChecklistContentSaveResult SaveTabs(IReadOnlyList<ChecklistTabState> tabs) => new(null);
    }
}
