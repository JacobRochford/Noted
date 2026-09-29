using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;

namespace Noted.Tests.Helpers;

[TestClass]
public sealed class PlainTextPasteTests
{
    [TestMethod]
    [DataRow("\u2018\u2019", "''")]
    [DataRow("\u201C\u201D", "\"\"")]
    [DataRow("\u00A0\u202F\u2007", "   ")]
    [DataRow("soft\u00ADhyphen", "softhyphen")]
    public void NormalizesOnlyExplicitMappings(string input, string expected) =>
        Assert.AreEqual(expected, PlainTextPaste.Normalize(input, true));

    [TestMethod]
    public void PreservesUnrelatedUnicodeAndWhitespace()
    {
        const string input = " \t\r\n\n\r\v\f\u0085\u2028\u2029\u2000\u2009\u3000\u2014\u2013\u2026é e\u0301\u200D\u200C😀  ";
        Assert.AreEqual(input, PlainTextPaste.Normalize(input, true));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void DisabledOrBypassedNormalizationPreservesExactText(bool enabled, bool bypass)
    {
        const string input = "\t\u2018\u2019\u201C\u201D\u00A0\u202F\u2007\u00AD\r\n\n\r ";
        Assert.AreEqual(input, PlainTextPaste.Normalize(input, enabled, bypass));
    }

    [TestMethod]
    [DoNotParallelize]
    public void NativePasteReplacesSelectionSupportsUndoAndReadsCurrentSetting()
    {
        RunSta(() =>
        {
            var previousClipboard = Clipboard.GetDataObject();
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, "\u201Cpaste\u201D\u00A0\u00AD");
                data.SetData(DataFormats.Rtf, "{\\rtf1 rich text must not be pasted}");
                Clipboard.SetDataObject(data);
                var enabled = true;
                var editor = new TextBox { Text = "\u2018before REMOVE after\u2019", AcceptsReturn = true };
                PlainTextPaste.Attach(editor, () => enabled);
                editor.Select(8, 6);
                editor.Paste();
                Assert.AreEqual("\u2018before \"paste\"  after\u2019", editor.Text);
                Assert.AreEqual(16, editor.CaretIndex);
                Assert.AreEqual(0, editor.SelectionLength);
                editor.Undo();
                Assert.AreEqual("\u2018before REMOVE after\u2019", editor.Text);
                editor.Redo();
                Assert.AreEqual("\u2018before \"paste\"  after\u2019", editor.Text);

                enabled = false;
                editor.SelectAll();
                editor.Paste();
                Assert.AreEqual("\u201Cpaste\u201D\u00A0\u00AD", editor.Text);

                enabled = true;
                editor.SelectAll();
                PlainTextPaste.PasteWithoutNormalization.Execute(null, editor);
                Assert.AreEqual("\u201Cpaste\u201D\u00A0\u00AD", editor.Text);
                editor.SelectAll();
                editor.Paste();
                Assert.AreEqual("\"paste\" ", editor.Text);

                var binding = editor.InputBindings.OfType<KeyBinding>().Single();
                Assert.AreEqual(Key.V, binding.Key);
                Assert.AreEqual(ModifierKeys.Control | ModifierKeys.Alt, binding.Modifiers);
            }
            finally
            {
                if (previousClipboard is null) Clipboard.Clear();
                else Clipboard.SetDataObject(previousClipboard, true);
            }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Paste test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
