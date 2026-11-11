using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Helpers;
using Noted.Tests.Services;
using static Noted.Tests.Services.ClipboardTestSupport;

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
    [TestCategory("DesktopInteraction")]
    [DoNotParallelize]
    public void NativePasteReplacesSelectionSupportsUndoAndReadsCurrentSetting()
    {
        RunSta(() =>
        {
            IDataObject? previousClipboard = null;
            RetryClipboard(() => previousClipboard = Clipboard.GetDataObject());
            Exception? testFailure = null;
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, "\u201Cpaste\u201D\u00A0\u00AD");
                data.SetData(DataFormats.Rtf, "{\\rtf1 rich text must not be pasted}");
                RetryClipboard(() => Clipboard.SetDataObject(data));
                var enabled = true;
                var editor = new TextBox { Text = "\u2018before REMOVE after\u2019", AcceptsReturn = true };
                using var surface = new WpfEditorTestSurface(editor);
                PlainTextPaste.Attach(editor, () => enabled);
                editor.Select(8, 6);
                editor.Paste();
                Assert.AreEqual("\u2018before \"paste\"  after\u2019", editor.Text);
                Assert.AreEqual(16, editor.CaretIndex);
                Assert.AreEqual(0, editor.SelectionLength);
                Assert.IsTrue(editor.CanUndo, "Native paste must create a WPF undo unit.");
                Assert.IsTrue(editor.Undo());
                Assert.AreEqual("\u2018before REMOVE after\u2019", editor.Text);
                Assert.IsTrue(editor.CanRedo);
                Assert.IsTrue(editor.Redo());
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
            catch (Exception ex)
            {
                testFailure = ex;
                throw;
            }
            finally
            {
                try
                {
                    RetryClipboard(() =>
                    {
                        if (previousClipboard is null) Clipboard.Clear();
                        else Clipboard.SetDataObject(previousClipboard, true);
                    });
                }
                catch (Exception restoreFailure) when (testFailure is not null)
                {
                    throw new AggregateException("Paste test and clipboard restoration both failed.",
                        testFailure, restoreFailure);
                }
            }
        });
    }

    [TestMethod]
    public void ClipboardRetryAllowsSuccessOnLastAttempt()
    {
        var attempts = 0;
        RetryClipboard(() =>
        {
            if (++attempts < 5)
                throw new COMException("Clipboard is busy.", unchecked((int)0x800401D0));
        });
        Assert.AreEqual(5, attempts);
    }

    [TestMethod]
    [DataRow(unchecked((int)0x800401D0), 5)]
    [DataRow(unchecked((int)0x80004005), 1)]
    public void ClipboardRetryPropagatesComFailures(int hresult, int expectedAttempts)
    {
        var failure = new COMException("Clipboard operation failed.", hresult);
        var attempts = 0;
        var actual = Assert.ThrowsExactly<COMException>(() => RetryClipboard(() =>
        {
            attempts++;
            throw failure;
        }));
        Assert.AreSame(failure, actual);
        Assert.AreEqual(expectedAttempts, attempts);
    }

    [TestMethod]
    public void ClipboardRetryPropagatesOtherExceptionsWithoutRetrying()
    {
        var failure = new InvalidOperationException("Clipboard operation failed.");
        var attempts = 0;
        var actual = Assert.ThrowsExactly<InvalidOperationException>(() => RetryClipboard(() =>
        {
            attempts++;
            throw failure;
        }));
        Assert.AreSame(failure, actual);
        Assert.AreEqual(1, attempts);
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
