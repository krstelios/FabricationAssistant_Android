using FabricationAssistant.Rendering.Gles;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class MsaaSceneFramebufferTests
{
    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 4)]
    [InlineData(32, 4)]
    public void ClampSamples_snaps_to_nearest_bucket(int desired, int expected)
    {
        Assert.Equal(expected, MsaaSceneFramebuffer.ClampSamples(desired));
    }

    [Fact]
    public void ClampSamples_handles_all_android_buckets()
    {
        Assert.Equal(1, MsaaSceneFramebuffer.ClampSamples(1));
        Assert.Equal(2, MsaaSceneFramebuffer.ClampSamples(2));
        Assert.Equal(4, MsaaSceneFramebuffer.ClampSamples(4));
        Assert.Equal(4, MsaaSceneFramebuffer.ClampSamples(8));
        Assert.Equal(4, MsaaSceneFramebuffer.ClampSamples(16));
    }

    [Theory]
    [InlineData(4, 3, 2)]
    [InlineData(8, 6, 4)]
    [InlineData(16, 12, 4)]
    [InlineData(4, 0, 1)]
    [InlineData(4, 1, 1)]
    [InlineData(4, 4, 4)]
    public void ClampSamplesToHardwareLimit_stays_on_supported_buckets(
        int desired,
        int maxSamples,
        int expected)
    {
        Assert.Equal(expected, MsaaSceneFramebuffer.ClampSamplesToHardwareLimit(desired, maxSamples));
    }
}
