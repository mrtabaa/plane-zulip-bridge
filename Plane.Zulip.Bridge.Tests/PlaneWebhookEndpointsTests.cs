using Xunit;

public sealed class PlaneWebhookEndpointsTests
{
    [Fact]
    public void FormatDate_FormatsTimestampInTehranTime()
    {
        var result = PlaneWebhookEndpoints.FormatDate("2026-08-04T17:45:09+03:00");

        Assert.Equal("2026-08-04 18:15:09", result);
    }

    [Fact]
    public void FormatDate_UsesCurrentTehranTimeWhenPlaneSuppliesDateOnly()
    {
        var vpsTime = new DateTimeOffset(2026, 8, 4, 14, 45, 9, TimeSpan.Zero);

        var result = PlaneWebhookEndpoints.FormatDate("2026-08-04", vpsTime);

        Assert.Equal("2026-08-04 18:15:09", result);
    }

    [Fact]
    public void FormatDate_ReturnsNotSetForMissingValue()
    {
        Assert.Equal("Not set", PlaneWebhookEndpoints.FormatDate(null));
    }
}
