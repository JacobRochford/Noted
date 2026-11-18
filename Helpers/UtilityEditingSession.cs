using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Noted.Models;
using Noted.Services;

namespace Noted.Helpers;

internal sealed class UtilityEditingSession<TItem> : IDisposable where TItem : class, INotifyPropertyChanged
{
    private readonly Window _window;
    private readonly ObservableCollection<TItem> _items;
    private readonly HashSet<TItem> _observedItems = [];
    private readonly JsonCollectionFileStore<UtilityItemSession> _store;
    private readonly SaveScheduler<object> _scheduler;
    private readonly Func<IReadOnlyList<UtilityItemSession>> _capture;
    private readonly Func<bool> _flushContent;
    private readonly RoutedEventHandler _selectionChanged;
    private readonly ScrollChangedEventHandler _scrollChanged;
    private readonly HashSet<string> _properties;
    private IReadOnlyList<UtilityItemSession> _saved;
    private static readonly object s_saveRequest = new();

    internal UtilityEditingSession(Window window, string directory, string file, ObservableCollection<TItem> items,
        Func<IReadOnlyList<UtilityItemSession>> capture, Func<bool> flushContent,
        Func<TextBox, UtilityEditorField> editorField, params string[] properties)
    {
        _window = window;
        _items = items;
        _capture = capture;
        _flushContent = flushContent;
        _properties = properties.ToHashSet(StringComparer.Ordinal);
        var path = Path.Combine(directory, PersistencePaths.SessionDirectory, file);
        _store = new(path, "Utility editing session", Validate);
        var loaded = _store.Load();
        _saved = loaded.Items ?? [];
        foreach (var issue in loaded.Issues)
            ExceptionDiagnostics.Record(new IOException(issue.Message));
        _scheduler = new(window.Dispatcher, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(2), _ => Save());
        _items.CollectionChanged += ItemsChanged;
        ObserveItems();
        _selectionChanged = (_, e) =>
        {
            if (e.OriginalSource is TextBox editor && editor.IsKeyboardFocused && editorField(editor) != UtilityEditorField.None)
                Schedule();
        };
        _scrollChanged = (_, e) =>
        {
            if ((e.HorizontalChange != 0 || e.VerticalChange != 0) &&
                VisualTreeHelpers.FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is { IsKeyboardFocused: true } editor &&
                editorField(editor) != UtilityEditorField.None)
                Schedule();
        };
        window.AddHandler(TextBox.SelectionChangedEvent, _selectionChanged);
        window.AddHandler(ScrollViewer.ScrollChangedEvent, _scrollChanged);
    }

    internal IReadOnlyList<UtilityItemSession> Loaded => _saved;

    internal void Schedule() => _scheduler.Schedule(s_saveRequest);

    internal void Checkpoint()
    {
        Schedule();
        _scheduler.TryFlush(out _);
    }

    internal bool TryRetireChecklistCreation(Guid itemId, out string? error)
    {
        var item = _saved.FirstOrDefault(item => item.ItemId == itemId);
        if (item?.ChecklistEdit?.Creation is null)
        {
            error = null;
            return true;
        }
        // retire the creation marker before accepting a blank item that Escape must no longer delete.
        var snapshot = _saved.Select(saved => saved == item
            ? saved with { ChecklistEdit = item.ChecklistEdit with { Creation = null } }
            : saved).ToArray();
        var result = Write(snapshot);
        error = result.Error;
        return result.Success;
    }

    private PersistenceSaveResult Save()
    {
        if (!_flushContent()) return PersistenceSaveResult.Failed("Save the content before saving its editing session.");
        return Write(_capture());
    }

    private PersistenceSaveResult Write(IReadOnlyList<UtilityItemSession> snapshot)
    {
        if (_saved.SequenceEqual(snapshot)) return PersistenceSaveResult.Succeeded();
        try
        {
            // an older snapshot can reopen a completed edit, so keep only the atomic current snapshot.
            var warning = _store.Write(snapshot, updateBackup: false);
            _saved = snapshot;
            return PersistenceSaveResult.Succeeded(warning);
        }
        catch (Exception ex) when (FileSystemErrors.IsExpected(ex))
        {
            ExceptionDiagnostics.Record(ex);
            return PersistenceSaveResult.Failed("The editing session could not be saved.");
        }
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) ObserveItems();
        else if (e.Action != NotifyCollectionChangedAction.Move)
        {
            foreach (var item in e.OldItems?.OfType<TItem>() ?? [])
                if (_observedItems.Remove(item)) item.PropertyChanged -= ItemChanged;
            foreach (var item in e.NewItems?.OfType<TItem>() ?? [])
                if (_observedItems.Add(item)) item.PropertyChanged += ItemChanged;
        }
        Schedule();
    }

    private void ObserveItems()
    {
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        _observedItems.Clear();
        foreach (var item in _items)
            if (_observedItems.Add(item)) item.PropertyChanged += ItemChanged;
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _properties.Contains(name)) Schedule();
    }

    private static void Validate(IReadOnlyList<UtilityItemSession> items)
    {
        var ids = new HashSet<Guid>();
        var hasActive = false;
        foreach (var item in items)
        {
            if (item.ItemId == Guid.Empty || !ids.Add(item.ItemId) || !Enum.IsDefined(item.ActiveEditor) ||
                item.IsActive && hasActive || !IsValid(item.Primary) || !IsValid(item.Details))
                throw new JsonException("The editing session contains invalid item or editor state.");
            hasActive |= item.IsActive;
        }
    }

    private static bool IsValid(UtilityEditorPosition? position) => position is null ||
        position.TextHash is { Length: 64 } && position.CaretIndex >= 0 &&
        position.SelectionStart >= 0 && position.SelectionLength >= 0 &&
        double.IsFinite(position.HorizontalOffset) && position.HorizontalOffset >= 0 &&
        double.IsFinite(position.VerticalOffset) && position.VerticalOffset >= 0;

    public void Dispose()
    {
        _scheduler.Dispose();
        _items.CollectionChanged -= ItemsChanged;
        foreach (var item in _observedItems) item.PropertyChanged -= ItemChanged;
        _window.RemoveHandler(TextBox.SelectionChangedEvent, _selectionChanged);
        _window.RemoveHandler(ScrollViewer.ScrollChangedEvent, _scrollChanged);
    }
}
