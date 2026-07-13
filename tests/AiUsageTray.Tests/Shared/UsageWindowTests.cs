using AiUsageTray.Models;
using Xunit;

namespace AiUsageTray.Tests.Shared;

public class UsageWindowTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(37, 37)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    [InlineData(-10, 0)]
    public void ClampPercent_ClampsToZeroToHundred(decimal input, decimal expected)
    {
        Assert.Equal(expected, UsageWindow.ClampPercent(input));
    }

    [Fact]
    public void ClampPercent_Null_StaysNull()
    {
        Assert.Null(UsageWindow.ClampPercent(null));
    }

    [Fact]
    public void MissingUsedPercent_IsNotTreatedAsZero()
    {
        var window = new UsageWindow("w", "Window", null, null, null, null, null, null, null);

        Assert.Null(window.UsedPercent);
        Assert.Null(window.RemainingPercent);
    }
}
