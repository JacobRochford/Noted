using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Models;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class JsonFormattingTests
{
    [TestMethod]
    public void FormatsNestedObjectsAndArraysWithConfiguredSpaces()
    {
        Assert.AreEqual("{\r\n  \"a\": [\r\n    1,\r\n    {\r\n      \"b\": true\r\n    },\r\n    []\r\n  ],\r\n  \"c\": {}\r\n}",
            Format("{\"a\":[1,{\"b\":true},[]],\"c\":{}}", settings: new() { TabSize = 2 }));
    }

    [TestMethod]
    public void MinifiesWithoutChangingStringsNumbersEscapesOrDuplicateProperties()
    {
        const string input = " { \"x\" : \" spaces  \\t \\n \\u0061 \\\" \\\\ \" , \"n\" : 1.00e+02, \"n\" : -0 } \r\n";
        const string expected = "{\"x\":\" spaces  \\t \\n \\u0061 \\\" \\\\ \",\"n\":1.00e+02,\"n\":-0}";
        Assert.AreEqual(expected, Format(input, minify: true));
        Assert.AreEqual(expected, Format(Format(input), minify: true));
    }

    [TestMethod]
    public void UsesTabsAndExistingLfNewlinesIncludingTerminalNewline()
    {
        Assert.AreEqual("{\n\t\"a\": [\n\t\t1,\n\t\t2\n\t]\n}\n",
            Format("{\n\"a\":[1,2]}\n", settings: new() { Mode = IndentationMode.Tabs, TabSize = 8 }));
    }

    [TestMethod]
    public void UsesFallbackNewlineForSingleLineInputAndPreservesCrConvention()
    {
        Assert.AreEqual("[\n    1\n]", Format("[1]", fallback: "\n"));
        Assert.AreEqual("[\r    1\r]", Format("[\r1]"));
    }

    [TestMethod]
    public void InvalidJsonReturnsNoEditsAndAnErrorPosition()
    {
        foreach (var input in new[] { "", "{\"a\":}", "[1,]", "// comment\n{}", "{} {}", "{\n bad}" })
        {
            Assert.IsFalse(JsonFormatting.TryGetEdits(input, false, new(), "\r\n", out var edits, out var error));
            Assert.AreEqual(0, edits.Count);
            StringAssert.Contains(error!, "line");
            StringAssert.Contains(error!, "Text was not changed");
        }
    }

    [TestMethod]
    public void HandlesScalarRootsAndIsIdempotent()
    {
        foreach (var input in new[] { "true", "null", "1.00e+02", "\" [ { , : } ] \"", "{}", "[]" })
            Assert.AreEqual(input, Format("  " + input + "  "));
        var formatted = Format("{\"x\":[1,2]}");
        Assert.AreEqual(formatted, Format(formatted));
    }

    private static string Format(string input, bool minify = false, IndentationSettings? settings = null,
        string fallback = "\r\n")
    {
        Assert.IsTrue(JsonFormatting.TryGetEdits(input, minify, settings ?? new(), fallback, out var edits, out var error), error);
        foreach (var edit in edits.Reverse())
            input = input.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        return input;
    }
}
