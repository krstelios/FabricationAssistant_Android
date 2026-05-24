using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Vector3d_Add_returns_componentwise_sum()
    {
        var a = new Vector3d(1.0, 2.0, 3.0);
        var b = new Vector3d(10.0, 20.0, 30.0);
        var c = a + b;
        Assert.Equal(11.0, c.X);
        Assert.Equal(22.0, c.Y);
        Assert.Equal(33.0, c.Z);
    }
}
