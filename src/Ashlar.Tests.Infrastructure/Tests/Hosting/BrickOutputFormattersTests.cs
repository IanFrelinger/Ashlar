using Ashlar.Core.Domain.Execution;
using Ashlar.Hosting;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

public class BrickOutputFormattersTests
{
    [Fact]
    public void ToJson_EmptyOutput_ReturnsEmptyJsonObject()
    {
        var output = new BrickOutput();

        var result = output.ToJson();

        Assert.NotNull(result);
        Assert.Contains("{", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void ToJson_WithSummaryAndData_ReturnsFormattedJson()
    {
        var output = new BrickOutput
        {
            Summary = "Test summary"
        };
        output.Set("key1", "value1");
        output.Set("key2", 42);
        output.Set("key3", true);

        var result = output.ToJson(indented: true);

        Assert.Contains("\"summary\"", result);
        Assert.Contains("Test summary", result);
        Assert.Contains("\"key1\"", result);
        Assert.Contains("\"value1\"", result);
        Assert.Contains("\"key2\"", result);
        Assert.Contains("42", result);
        Assert.Contains("\"key3\"", result);
        Assert.Contains("true", result);
    }

    [Fact]
    public void ToJson_Unindented_ReturnsCompactJson()
    {
        var output = new BrickOutput { Summary = "Compact" };
        output.Set("key", "value");

        var result = output.ToJson(indented: false);

        Assert.DoesNotContain("\n", result);
        Assert.Contains("\"summary\"", result);
    }

    [Fact]
    public void ToMarkdown_EmptyOutput_ReturnsEmptyString()
    {
        var output = new BrickOutput();

        var result = output.ToMarkdown();

        Assert.Equal("", result);
    }

    [Fact]
    public void ToMarkdown_WithTitle_IncludesTitle()
    {
        var output = new BrickOutput { Summary = "Test" };

        var result = output.ToMarkdown(title: "Results");

        Assert.Contains("## Results", result);
    }

    [Fact]
    public void ToMarkdown_WithSummaryAndData_FormatsCorrectly()
    {
        var output = new BrickOutput
        {
            Summary = "Test summary"
        };
        output.Set("status", "completed");
        output.Set("count", 10);
        output.Set("success", true);

        var result = output.ToMarkdown();

        Assert.Contains("### Summary", result);
        Assert.Contains("Test summary", result);
        Assert.Contains("### Outputs", result);
        Assert.Contains("**status**:", result);
        Assert.Contains("completed", result);
        Assert.Contains("**count**:", result);
        Assert.Contains("10", result);
        Assert.Contains("**success**:", result);
        Assert.Contains("✅ true", result);
    }

    [Fact]
    public void ToMarkdown_WithBooleanFalse_ShowsXEmoji()
    {
        var output = new BrickOutput();
        output.Set("passed", false);

        var result = output.ToMarkdown();

        Assert.Contains("❌ false", result);
    }

    [Fact]
    public void ToMarkdown_WithList_FormatsAsList()
    {
        var output = new BrickOutput();
        output.Set("items", new List<string> { "item1", "item2", "item3" });

        var result = output.ToMarkdown();

        Assert.Contains("[item1, item2, item3]", result);
    }

    [Fact]
    public void ToPlainText_EmptyOutput_ReturnsEmptyString()
    {
        var output = new BrickOutput();

        var result = output.ToPlainText();

        Assert.Equal("", result);
    }

    [Fact]
    public void ToPlainText_WithSummaryAndData_FormatsCorrectly()
    {
        var output = new BrickOutput
        {
            Summary = "Operation completed"
        };
        output.Set("status", "success");
        output.Set("duration", 5.2);

        var result = output.ToPlainText();

        Assert.Contains("Summary:", result);
        Assert.Contains("Operation completed", result);
        Assert.Contains("Outputs:", result);
        Assert.Contains("status: success", result);
        Assert.Contains("duration: 5.2", result);
    }

    [Fact]
    public void ToPlainText_IncludeEmpty_ShowsEmptyValues()
    {
        var output = new BrickOutput();
        output.Set("key1", "value");
        output.Set("key2", "");

        var resultExcludeEmpty = output.ToPlainText(includeEmpty: false);
        var resultIncludeEmpty = output.ToPlainText(includeEmpty: true);

        Assert.Contains("key1:", resultExcludeEmpty);
        Assert.DoesNotContain("key2:", resultExcludeEmpty);
        
        Assert.Contains("key1:", resultIncludeEmpty);
        Assert.Contains("key2:", resultIncludeEmpty);
    }

    [Fact]
    public void ToPlainText_WithBooleans_ShowsLowercaseValues()
    {
        var output = new BrickOutput();
        output.Set("isTrue", true);
        output.Set("isFalse", false);

        var result = output.ToPlainText();

        Assert.Contains("isTrue: true", result);
        Assert.Contains("isFalse: false", result);
    }

    [Fact]
    public void ToTable_EmptyOutput_ReturnsEmptyString()
    {
        var output = new BrickOutput();

        var result = output.ToTable();

        Assert.Equal("", result);
    }

    [Fact]
    public void ToTable_WithData_FormatsAsTable()
    {
        var output = new BrickOutput
        {
            Summary = "Results"
        };
        output.Set("status", "ok");
        output.Set("count", 5);

        var result = output.ToTable();

        Assert.Contains("Summary: Results", result);
        Assert.Contains("┌", result);
        Assert.Contains("│", result);
        Assert.Contains("└", result);
        Assert.Contains("Key", result);
        Assert.Contains("Value", result);
        Assert.Contains("status", result);
        Assert.Contains("ok", result);
        Assert.Contains("count", result);
        Assert.Contains("5", result);
    }

    [Fact]
    public void ToTable_WithBooleans_ShowsCheckmarks()
    {
        var output = new BrickOutput();
        output.Set("enabled", true);
        output.Set("disabled", false);

        var result = output.ToTable();

        Assert.Contains("✓", result);
        Assert.Contains("✗", result);
    }

    [Fact]
    public void ToTable_WithList_ShowsItemCount()
    {
        var output = new BrickOutput();
        output.Set("items", new List<int> { 1, 2, 3, 4, 5 });

        var result = output.ToTable();

        Assert.Contains("[5 items]", result);
    }

    [Fact]
    public void ToTable_WithLongValue_TruncatesValue()
    {
        var output = new BrickOutput();
        var longValue = new string('x', 60);
        output.Set("long", longValue);

        var result = output.ToTable();

        Assert.Contains("...", result);
    }

    [Fact]
    public void ToTable_WithNullValue_ShowsNull()
    {
        var output = new BrickOutput();
        output.Set("nullable", null!);

        var result = output.ToTable();

        Assert.Contains("(null)", result);
    }

    [Fact]
    public void AllFormatters_WithMixedTypes_HandleCorrectly()
    {
        var output = new BrickOutput
        {
            Summary = "Complex output"
        };
        output.Set("string", "text");
        output.Set("number", 123);
        output.Set("boolean", true);
        output.Set("list", new List<string> { "a", "b", "c" });
        output.Set("nullable", null!);

        var json = output.ToJson();
        var markdown = output.ToMarkdown();
        var plainText = output.ToPlainText();
        var table = output.ToTable();

        Assert.NotNull(json);
        Assert.NotEmpty(json);
        
        Assert.NotNull(markdown);
        Assert.NotEmpty(markdown);
        
        Assert.NotNull(plainText);
        Assert.NotEmpty(plainText);
        
        Assert.NotNull(table);
        Assert.NotEmpty(table);
    }

    [Fact]
    public void AllFormatters_OutputKeysInAlphabeticalOrder()
    {
        var output = new BrickOutput();
        output.Set("zebra", "last");
        output.Set("alpha", "first");
        output.Set("middle", "mid");

        var markdown = output.ToMarkdown();
        var plainText = output.ToPlainText();
        var table = output.ToTable();

        var alphaIndexMd = markdown.IndexOf("alpha", StringComparison.Ordinal);
        var middleIndexMd = markdown.IndexOf("middle", StringComparison.Ordinal);
        var zebraIndexMd = markdown.IndexOf("zebra", StringComparison.Ordinal);
        Assert.True(alphaIndexMd < middleIndexMd);
        Assert.True(middleIndexMd < zebraIndexMd);

        var alphaIndexPt = plainText.IndexOf("alpha", StringComparison.Ordinal);
        var middleIndexPt = plainText.IndexOf("middle", StringComparison.Ordinal);
        var zebraIndexPt = plainText.IndexOf("zebra", StringComparison.Ordinal);
        Assert.True(alphaIndexPt < middleIndexPt);
        Assert.True(middleIndexPt < zebraIndexPt);

        var alphaIndexTb = table.IndexOf("alpha", StringComparison.Ordinal);
        var middleIndexTb = table.IndexOf("middle", StringComparison.Ordinal);
        var zebraIndexTb = table.IndexOf("zebra", StringComparison.Ordinal);
        Assert.True(alphaIndexTb < middleIndexTb);
        Assert.True(middleIndexTb < zebraIndexTb);
    }
}
