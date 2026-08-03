using System.Text.Json;
using Xunit;

public sealed class PlaneHtmlTextTests
{
    [Theory]
    [InlineData("first</br>second", "first\nsecond")]
    [InlineData("first<br>second", "first\nsecond")]
    [InlineData("first<br/>second", "first\nsecond")]
    [InlineData("first<br />second", "first\nsecond")]
    public void ToPlainText_PreservesEveryBreakVariant(
        string html,
        string expected)
    {
        Assert.Equal(expected, PlaneHtmlText.ToPlainText(html));
    }

    [Fact]
    public void DescriptionText_PrefersStructuredHtmlOverFlattenedText()
    {
        using var document = JsonDocument.Parse("""
        {
          "description_stripped": "first second",
          "description_html": "first</br>second"
        }
        """);

        var description = PlaneWebhookEndpoints.DescriptionText(
            document.RootElement,
            default);

        Assert.Equal("first\nsecond", description);
    }

    [Fact]
    public void DescriptionText_PrefersChangedHtmlOverFlattenedText()
    {
        using var data = JsonDocument.Parse("""
        { "description_stripped": "first second" }
        """);
        using var newValue = JsonDocument.Parse("""
        "first</br>second"
        """);

        var description = PlaneWebhookEndpoints.DescriptionText(
            data.RootElement,
            newValue.RootElement);

        Assert.Equal("first\nsecond", description);
    }
}
