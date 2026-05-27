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

    [Fact]
    public void OrdersNumericPathSegmentsBeyondIntRange()
    {
        string[] paths =
        [
            "0/99999999999999999999999999999999999999",
            "0/10",
            "0/2",
            "0/100000000000000000000000000000000000000",
        ];

        Array.Sort(paths, AndroidPathComparer.Instance);

        Assert.Equal(
            [
                "0/2",
                "0/10",
                "0/99999999999999999999999999999999999999",
                "0/100000000000000000000000000000000000000",
            ],
            paths);
    }

    [Fact]
    public void OrdersEqualNumericPathSegmentsByShortestRepresentation()
    {
        string[] paths = ["0/0002", "0/02", "0/2", "0/0010", "0/10"];

        Array.Sort(paths, AndroidPathComparer.Instance);

        Assert.Equal(["0/2", "0/02", "0/0002", "0/10", "0/0010"], paths);
    }
}
