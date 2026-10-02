using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class IndentationTests
{
    [TestMethod]
    [DataRow("\tfirst\n\t\tsecond", IndentationMode.Tabs, 6)]
    [DataRow("\t  first\n\t second", IndentationMode.Tabs, 6)]
    [DataRow("root\n  first\n    second\n  third", IndentationMode.Spaces, 2)]
    [DataRow("    first\n        second\n    third", IndentationMode.Spaces, 4)]
    [DataRow("  first\n  second", IndentationMode.Spaces, 2)]
    [DataRow("    first\n    second", IndentationMode.Spaces, 4)]
    [DataRow("   first\n      second", IndentationMode.Spaces, 3)]
    [DataRow("\t\n \t \n\n  first\r\n    second\r  third", IndentationMode.Spaces, 2)]
    [DataRow("    first\n    second\n        third\n\tfourth", IndentationMode.Spaces, 4)]
    [DataRow("\tfirst\n\tsecond\n\tthird\n    fourth", IndentationMode.Tabs, 6)]
    public void DetectionUsesLeadingIndentationAndKeepsTabWidthForTabs(string text, IndentationMode mode, int size)
    {
        var current = new IndentationSettings { TabSize = 6 };
        Assert.AreEqual(new IndentationSettings { Mode = mode, TabSize = size }, Indentation.Detect(text, current));
        Assert.AreEqual(new IndentationSettings { TabSize = 6 }, current);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\t  \r\n    \n\t\n")]
    [DataRow("normal\ttext  here\nmore    normal\ttext")]
    [DataRow("    only one indented line")]
    [DataRow("\tfirst\n\tsecond\n    third\n    fourth")]
    [DataRow(" \tfirst\n  \tsecond")]
    [DataRow("\tfirst\n    second\n    third")]
    public void AmbiguousModePreservesCurrentEditorSettings(string text)
    {
        var current = new IndentationSettings { Mode = IndentationMode.Tabs, TabSize = 6 };
        Assert.AreEqual(current, Indentation.Detect(text, current));
    }

    [TestMethod]
    [DataRow("   first\n     second")]
    [DataRow("    first\n      second")]
    [DataRow(" first\n second")]
    [DataRow("            first\n                        second")]
    public void ClearSpacesButAmbiguousSizePreservesCurrentSize(string text)
    {
        var current = new IndentationSettings { Mode = IndentationMode.Tabs, TabSize = 6 };
        Assert.AreEqual(current with { Mode = IndentationMode.Spaces }, Indentation.Detect(text, current));
    }

    [TestMethod]
    [DataRow("\talpha\tbeta  gamma\r\n \t  x\n   y\r\t \tZ\n\t  \n",
        "    alpha\tbeta  gamma\r\n      x\n   y\r        Z\n      \n", 4, IndentationMode.Spaces)]
    [DataRow("    alpha\tbeta  gamma\r\n      x\n   y\r \t \tZ\n  \t  \n",
        "\talpha\tbeta  gamma\r\n\t  x\n   y\r\t\tZ\n\t  \n", 4, IndentationMode.Tabs)]
    [DataRow(" \t x\n\t\ty", "   x\n    y", 2, IndentationMode.Spaces)]
    [DataRow("   x\n    y", "\t x\n\t\ty", 2, IndentationMode.Tabs)]
    [DataRow("\tlast", "        last", 8, IndentationMode.Spaces)]
    [DataRow("\n\r\n\r", "\n\r\n\r", 4, IndentationMode.Tabs)]
    [DataRow("normal\ttext    here", "normal\ttext    here", 4, IndentationMode.Spaces)]
    [DataRow("  partial", "  partial", 4, IndentationMode.Tabs)]
    public void ConversionChangesOnlyLeadingWhitespaceAndPreservesColumns(
        string text, string expected, int tabSize, IndentationMode mode)
    {
        var edits = Indentation.GetConversionEdits(text, tabSize, mode);
        var converted = text;
        foreach (var edit in edits.Reverse())
            converted = converted.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        Assert.AreEqual(expected, converted);
        Assert.AreEqual(0, Indentation.GetConversionEdits(converted, tabSize, mode).Count, "Conversion must be idempotent.");
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 0)]
    [DataRow(4, 1)]
    [DataRow(5, 2)]
    [DataRow(6, 3)]
    [DataRow(11, 8)]
    public void MappingPreservesColumnsOrThePrecedingTabBoundary(int before, int after)
    {
        const string text = "      alpha";
        var edits = Indentation.GetConversionEdits(text, 4, IndentationMode.Tabs);
        Assert.AreEqual(after, Indentation.MapOffset(text, edits, before, 4));
    }

    [TestMethod]
    public void MappingAccountsForAllEarlierEdits()
    {
        const string text = "\talpha\n  \tbeta";
        var edits = Indentation.GetConversionEdits(text, 4, IndentationMode.Spaces);
        Assert.AreEqual(4, Indentation.MapOffset(text, edits, 1, 4));
        Assert.AreEqual(14, Indentation.MapOffset(text, edits, text.IndexOf("beta", StringComparison.Ordinal), 4));
        Assert.AreEqual(18, Indentation.MapOffset(text, edits, text.Length, 4));
    }

    [TestMethod]
    [DataRow("", 4, 4)]
    [DataRow("a", 4, 3)]
    [DataRow("ab", 4, 2)]
    [DataRow("abc", 4, 1)]
    [DataRow("abcd", 4, 4)]
    [DataRow("a\tb", 4, 3)]
    [DataRow("previous\r\na", 4, 3)]
    [DataRow("previous\ra", 4, 3)]
    [DataRow("previous\na", 4, 3)]
    [DataRow("abc", 2, 1)]
    [DataRow("abc", 8, 5)]
    [DataRow("abc", 1, 1)]
    public void SpacesAdvanceToTheNextLogicalTabStop(string prefix, int tabSize, int count)
    {
        Assert.AreEqual(new string(' ', count), Indentation.GetInsertion(prefix, new() { TabSize = tabSize }));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(8)]
    public void TabsInsertOneLiteralTab(int tabSize)
    {
        Assert.AreEqual("\t", Indentation.GetInsertion("a\tb", new() { Mode = IndentationMode.Tabs, TabSize = tabSize }));
    }

    [TestMethod]
    public void MissingAndInvalidValuesUseSpacesAndFour()
    {
        var defaults = new IndentationSettings();
        Assert.AreEqual(IndentationMode.Spaces, defaults.Mode);
        Assert.AreEqual(4, defaults.TabSize);
        Assert.AreEqual(defaults, JsonSerializer.Deserialize<IndentationSettings>("{}"));
        var legacy = JsonSerializer.Deserialize<AppSettingsDocument>("{}");
        Assert.IsNotNull(legacy);
        Assert.AreEqual(defaults, legacy.DefaultIndentation);
        Assert.IsNull(JsonSerializer.Deserialize<MiniPadWindowState>("{}")!.Indentation);
        Assert.IsNull(JsonSerializer.Deserialize<ScratchpadWindowState>("{}")!.Indentation);
        Assert.AreEqual(defaults, JsonSerializer.Deserialize<IndentationSettings>("{\"Mode\":99,\"TabSize\":0}"));
        Assert.AreEqual(defaults, new IndentationSettings { TabSize = -1 });
        Assert.AreEqual(defaults, new IndentationSettings { TabSize = int.MaxValue });
    }
}
