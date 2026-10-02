using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Noted.Models;
using Noted.Search;
using Noted.Services;

namespace Noted.Helpers;

internal static class EditorIndentation
{
    internal static void Attach(TextBoxBase editor, Func<IndentationSettings?> getSettings)
    {
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None ||
                editor.IsReadOnly || !editor.IsEnabled || getSettings() is not { } settings)
                return;

            Insert(editor, settings);
            e.Handled = true;
        };
    }

    internal static void Insert(TextBoxBase editor, IndentationSettings settings)
    {
        if (editor.IsReadOnly || !editor.IsEnabled)
            return;

        editor.BeginChange();
        try
        {
            switch (editor)
            {
                case TextBox textBox:
                    var start = textBox.SelectionStart;
                    var insertion = Indentation.GetInsertion(textBox.Text.AsSpan(0, start), settings);
                    textBox.SelectedText = insertion;
                    textBox.Select(start + insertion.Length, 0);
                    break;
                case RichTextBox richTextBox:
                    var selection = richTextBox.Selection;
                    var paragraphStart = selection.Start.Paragraph?.ContentStart ?? richTextBox.Document.ContentStart;
                    var prefix = new TextRange(paragraphStart, selection.Start).Text;
                    selection.Text = Indentation.GetInsertion(prefix, settings);
                    richTextBox.CaretPosition = selection.End;
                    break;
            }
        }
        finally
        {
            editor.EndChange();
        }
    }

    internal static string GetLabel(IndentationSettings settings) => $"{settings.Mode}: {settings.TabSize}";

    internal static IndentationSettings Detect(TextBoxBase editor, IndentationSettings current) =>
        Indentation.Detect(editor switch
        {
            TextBox textBox => textBox.Text,
            RichTextBox richTextBox => new RichTextSearchSnapshot(richTextBox.Document, markStructuralWhitespace: true).Text,
            _ => throw new ArgumentException("Unsupported editor control.", nameof(editor))
        }, current);

    internal static bool ConvertIndentation(TextBoxBase editor, IndentationSettings settings, IndentationMode target)
    {
        if (editor.IsReadOnly || !editor.IsEnabled)
            return false;

        var snapshot = editor is RichTextBox rich
            ? new RichTextSearchSnapshot(rich.Document, markStructuralWhitespace: true) : null;
        var text = editor is TextBox plain ? plain.Text : snapshot!.Text;
        var edits = Indentation.GetConversionEdits(text, settings.TabSize, target);
        if (edits.Count == 0)
            return true;

        var verticalOffset = editor.VerticalOffset;
        var horizontalOffset = editor.HorizontalOffset;
        editor.BeginChange();
        try
        {
            switch (editor)
            {
                case TextBox textBox:
                    var start = Indentation.MapOffset(text, edits, textBox.SelectionStart, settings.TabSize);
                    var end = Indentation.MapOffset(text, edits,
                        textBox.SelectionStart + textBox.SelectionLength, settings.TabSize);
                    foreach (var edit in edits.Reverse())
                    {
                        textBox.Select(edit.Start, edit.Length);
                        textBox.SelectedText = edit.Replacement;
                    }
                    textBox.Select(start, end - start);
                    break;
                case RichTextBox richTextBox:
                    var selection = richTextBox.Selection;
                    var backward = !selection.IsEmpty && richTextBox.CaretPosition.CompareTo(selection.Start) == 0;
                    var richStart = Indentation.MapOffset(text, edits, snapshot!.GetOffset(selection.Start), settings.TabSize);
                    var richEnd = Indentation.MapOffset(text, edits, snapshot.GetOffset(selection.End), settings.TabSize);
                    foreach (var edit in edits.Reverse())
                        snapshot.CreateRange(new TextSearchMatch(edit.Start, edit.Length)).Text = edit.Replacement;

                    var converted = new RichTextSearchSnapshot(richTextBox.Document, markStructuralWhitespace: true);
                    TextPointer Position(int offset) => offset < converted.Text.Length
                        ? converted.GetStart(offset)
                        : richTextBox.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                    selection.Select(Position(backward ? richEnd : richStart), Position(backward ? richStart : richEnd));
                    break;
            }
        }
        finally
        {
            editor.EndChange();
        }
        editor.ScrollToVerticalOffset(verticalOffset);
        editor.ScrollToHorizontalOffset(horizontalOffset);
        return true;
    }

    internal static void AttachIndicator(
        Button button,
        Func<IndentationSettings?> getSettings,
        Action<IndentationSettings> setSettings,
        Func<TextBoxBase?> getEditor)
    {
        button.Click += (_, _) =>
        {
            if (getSettings() is not { } settings)
                return;

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
            if (button.TryFindResource("NotedContextMenu") is Style menuStyle)
                menu.Style = menuStyle;
            var itemStyle = button.TryFindResource("NotedContextMenuItem") as Style;
            void ApplySettings(IndentationSettings value)
            {
                setSettings(value);
                button.Content = GetLabel(getSettings() ?? settings);
            }
            void AddChoice(ItemsControl parent, string label, IndentationSettings value, bool selected)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected, Style = itemStyle };
                item.Click += (_, _) =>
                {
                    try
                    {
                        ApplySettings((getSettings() ?? settings) with { Mode = value.Mode });
                    }
                    catch (SettingsPersistenceException ex)
                    {
                        ExceptionDiagnostics.Record(ex);
                        AppDialog.Show(ex.Message, "Indentation Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                parent.Items.Add(item);
            }

            AddChoice(menu, "Indent Using Spaces", settings with { Mode = IndentationMode.Spaces }, settings.Mode == IndentationMode.Spaces);
            AddChoice(menu, "Indent Using Tabs", settings with { Mode = IndentationMode.Tabs }, settings.Mode == IndentationMode.Tabs);
            var sizePanel = new StackPanel { Orientation = Orientation.Horizontal };
            sizePanel.Children.Add(new TextBlock
            {
                Text = "Tab size",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });
            var sizeButtonStyle = button.TryFindResource("NotedSecondaryButton") as Style;
            Button CreateSizeButton(string label) => new()
            {
                Content = label,
                Width = 24,
                Height = 20,
                Padding = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Style = sizeButtonStyle
            };
            var decrease = CreateSizeButton("−");
            var sizeLabel = new TextBlock
            {
                Width = 24,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var increase = CreateSizeButton("+");
            System.Windows.Automation.AutomationProperties.SetName(decrease, "Decrease tab size");
            System.Windows.Automation.AutomationProperties.SetName(increase, "Increase tab size");
            void RefreshSize()
            {
                var size = (getSettings() ?? settings).TabSize;
                sizeLabel.Text = size.ToString();
                decrease.IsEnabled = size > 1;
                increase.IsEnabled = size < IndentationSettings.MaximumTabSize;
            }
            void ChangeSize(int delta)
            {
                if (getSettings() is not { } current)
                    return;
                try
                {
                    ApplySettings(current with
                    {
                        TabSize = Math.Clamp(current.TabSize + delta, 1, IndentationSettings.MaximumTabSize)
                    });
                }
                catch (SettingsPersistenceException ex)
                {
                    ExceptionDiagnostics.Record(ex);
                    AppDialog.Show(ex.Message, "Indentation Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RefreshSize();
            }
            decrease.Click += (_, e) => { e.Handled = true; ChangeSize(-1); };
            increase.Click += (_, e) => { e.Handled = true; ChangeSize(1); };
            sizePanel.Children.Add(decrease);
            sizePanel.Children.Add(sizeLabel);
            sizePanel.Children.Add(increase);
            RefreshSize();
            menu.Items.Add(new MenuItem { Header = sizePanel, StaysOpenOnClick = true, Style = itemStyle });
            void AddAction(string label, bool changesText, Action<TextBoxBase, IndentationSettings> action)
            {
                var editor = getEditor();
                var item = new MenuItem
                {
                    Header = label,
                    Style = itemStyle,
                    IsEnabled = editor is { IsEnabled: true } && (!changesText || !editor.IsReadOnly)
                };
                item.Click += (_, _) =>
                {
                    if (getEditor() is not { IsEnabled: true } currentEditor || getSettings() is not { } current)
                        return;
                    try { action(currentEditor, current); }
                    catch (SettingsPersistenceException ex)
                    {
                        ExceptionDiagnostics.Record(ex);
                        AppDialog.Show(ex.Message, "Indentation Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };
                menu.Items.Add(item);
            }
            AddAction("Detect Indentation from Content", false, (editor, current) => ApplySettings(Detect(editor, current)));
            void AddConversion(string label, IndentationMode target) => AddAction(label, true, (editor, current) =>
            {
                if (ConvertIndentation(editor, current, target))
                    ApplySettings(current with { Mode = target });
            });
            AddConversion("Convert Indentation to Spaces", IndentationMode.Spaces);
            AddConversion("Convert Indentation to Tabs", IndentationMode.Tabs);
            button.ContextMenu = menu;
            menu.IsOpen = true;
        };
    }
}
