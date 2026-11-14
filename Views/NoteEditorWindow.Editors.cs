using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Noted.Helpers;
using Noted.Models;
using Noted.Search;

namespace Noted;

public partial class NoteEditorWindow
{
    private readonly Dictionary<OpenNoteDocument, DocumentEditorState> _documentEditors = [];
    private DocumentEditorState _emptyPrimaryEditor = null!;
    private DocumentEditorState _emptySecondaryEditor = null!;
    private DocumentEditorState _primaryEditorState = null!;
    private DocumentEditorState _secondaryEditorState = null!;
    private bool _isSwitchingEditors;

    private TextBox EditorTextBox => _primaryEditorState.Editor;
    private TextBox SecondaryEditorTextBox => _secondaryEditorState.Editor;

    private void InitializeDocumentEditors()
    {
        _primaryEditorState = _emptyPrimaryEditor = CreateDocumentEditor(null);
        _secondaryEditorState = _emptySecondaryEditor = CreateDocumentEditor(null);
        PrimaryEditorHost.Content = EditorTextBox;
        SecondaryEditorHost.Content = SecondaryEditorTextBox;
        RegisterName(nameof(EditorTextBox), EditorTextBox);
        RegisterName(nameof(SecondaryEditorTextBox), SecondaryEditorTextBox);
        PrimaryLineNumbers.Editor = EditorTextBox;
        SecondaryLineNumbers.Editor = SecondaryEditorTextBox;
    }

    private DocumentEditorState CreateDocumentEditor(OpenNoteDocument? document)
    {
        var editor = (TextBox)((DataTemplate)FindResource("DocumentEditorTemplate")).LoadContent();
        var menus = editor.ContextMenu.Items.OfType<MenuItem>();
        var state = new DocumentEditorState(editor,
            menus.Single(item => item.Name == "ContextWordWrapMenuItem"),
            menus.Single(item => item.Name == "ContextMarkdownMenuItem"));
        editor.IsEnabled = document is not null;
        if (document is not null)
        {
            editor.Text = document.Content;
            editor.CaretIndex = Math.Clamp(document.CaretIndex, 0, document.Content.Length);
            editor.IsUndoEnabled = false;
            editor.IsUndoEnabled = true;
            state.VerticalOffset = Math.Max(0, document.VerticalOffset);
        }
        editor.Loaded += (_, _) => state.RestoreScroll();
        EditorIndentation.Attach(editor, () => document?.Indentation);
        EditorTextWrapping.Attach(editor);
        EditorJsonFormatting.Attach(editor, () => editor.IsVisible ? document?.Indentation : null);
        EditorTextTransformations.Attach(editor);
        EditorNavigation.Attach(editor, () => SetWordWrapEnabled(editor.TextWrapping != TextWrapping.Wrap));
        PlainTextPaste.Attach(editor, _settingsService.LoadNormalizePastedText);
        EditorLineCommands.Attach(editor);
        AddMarkdownPreviewMenu(state.PreviewMenu, () => document);
        return state;
    }

    private IEnumerable<DocumentEditorState> AllDocumentEditors() =>
        _documentEditors.Values.Concat([_emptyPrimaryEditor, _emptySecondaryEditor]);

    private void ShowDocumentEditor(OpenNoteDocument document, bool secondary)
    {
        if (!_documentEditors.TryGetValue(document, out var state))
        {
            state = CreateDocumentEditor(document);
            _documentEditors.Add(document, state);
        }
        SetPaneEditor(state, secondary);
    }

    private void SetPaneEditor(DocumentEditorState state, bool secondary)
    {
        var previous = secondary ? _secondaryEditorState : _primaryEditorState;
        if (ReferenceEquals(previous, state))
            return;

        var switching = _isSwitchingEditors;
        _isSwitchingEditors = true;
        try
        {
            state.Editor.FontSize = previous.Editor.FontSize;
            state.Editor.TextWrapping = previous.Editor.TextWrapping;
            state.Editor.HorizontalScrollBarVisibility = previous.Editor.HorizontalScrollBarVisibility;
            state.WordWrapMenu.IsChecked = HeaderWordWrapMenuItem.IsChecked;

            // a document can occupy only one pane; detach it before moving it.
            var other = secondary ? _primaryEditorState : _secondaryEditorState;
            if (ReferenceEquals(other, state))
                SetPaneEditor(secondary ? _emptyPrimaryEditor : _emptySecondaryEditor, !secondary);

            previous.CaptureScroll();
            var presenter = secondary ? _secondaryFindPresenter : _primaryFindPresenter;
            if (ReferenceEquals(_activeFindPresenter, presenter))
            {
                _activeFindPresenter = null;
                _activeFindEditor = null;
                _activeFindDocument = null;
            }
            _findSession.QueryOrOptionsChanged -= presenter.ClearPendingManualNavigationAnchor;
            presenter.Dispose();

            var preview = secondary ? _secondaryPreview : _primaryPreview;
            preview.SetMode(MarkdownPreviewMode.Off);
            var host = secondary ? SecondaryEditorHost : PrimaryEditorHost;
            host.Content = null;
            if (secondary) _secondaryEditorState = state;
            else _primaryEditorState = state;

            var name = secondary ? nameof(SecondaryEditorTextBox) : nameof(EditorTextBox);
            UnregisterName(name);
            RegisterName(name, state.Editor);
            host.Content = state.Editor;
            (secondary ? SecondaryLineNumbers : PrimaryLineNumbers).Editor = state.Editor;
            preview.SetEditor(state.Editor);

            presenter = new TextBoxSearchPresenter(state.Editor);
            if (secondary) _secondaryFindPresenter = presenter;
            else _primaryFindPresenter = presenter;
            _findSession.QueryOrOptionsChanged += presenter.ClearPendingManualNavigationAnchor;
            host.UpdateLayout();
            state.RestoreScroll();
        }
        finally
        {
            _isSwitchingEditors = switching;
        }
    }

    private void ReleaseDocumentEditor(OpenNoteDocument document)
    {
        if (_documentEditors.Remove(document, out var state))
            _previewMenus.RemoveAll(entry => ReferenceEquals(entry.Menu, state.PreviewMenu));
    }

    private void DocumentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSwitchingEditors) return;
        if (ReferenceEquals(sender, _primaryEditorState?.Editor)) EditorTextBox_TextChanged(sender, e);
        else if (ReferenceEquals(sender, _secondaryEditorState?.Editor)) SecondaryEditorTextBox_TextChanged(sender, e);
    }

    private void DocumentEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isSwitchingEditors) return;
        if (ReferenceEquals(sender, _primaryEditorState?.Editor)) EditorTextBox_SelectionChanged(sender, e);
        else if (ReferenceEquals(sender, _secondaryEditorState?.Editor)) SecondaryEditorTextBox_SelectionChanged(sender, e);
    }

    private void DocumentEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isSwitchingEditors) return;
        if (ReferenceEquals(sender, _primaryEditorState?.Editor)) EditorTextBox_GotKeyboardFocus(sender, e);
        else if (ReferenceEquals(sender, _secondaryEditorState?.Editor)) SecondaryEditorTextBox_GotKeyboardFocus(sender, e);
    }

    private sealed class DocumentEditorState(TextBox editor, MenuItem wordWrapMenu, MenuItem previewMenu)
    {
        public TextBox Editor { get; } = editor;
        public MenuItem WordWrapMenu { get; } = wordWrapMenu;
        public MenuItem PreviewMenu { get; } = previewMenu;
        public double VerticalOffset { get; set; }
        private double _horizontalOffset;
        private bool _restoreScroll = true;

        public void CaptureScroll()
        {
            if (_restoreScroll) return;
            VerticalOffset = Editor.VerticalOffset;
            _horizontalOffset = Editor.HorizontalOffset;
            _restoreScroll = true;
        }

        public void RestoreScroll()
        {
            if (!_restoreScroll || !Editor.IsLoaded) return;
            Editor.ScrollToVerticalOffset(VerticalOffset);
            Editor.ScrollToHorizontalOffset(_horizontalOffset);
            _restoreScroll = false;
        }
    }
}
