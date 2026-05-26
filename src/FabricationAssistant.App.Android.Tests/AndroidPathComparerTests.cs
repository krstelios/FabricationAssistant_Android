using FabricationAssistant.App.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidPathComparerTests
{
    [Fact]
    public void OrdersNumericPathSegmentsByValue()
    {
        string[] paths = ["0/10", "0/2", "0/1", "0/0/11", "0/0/2"];

        Array.Sort(paths, AndroidPathComparer.Instance);

        Assert.Equal(["0/0/2", "0/0/11", "0/1", "0/2", "0/10"], paths);
    }
}
