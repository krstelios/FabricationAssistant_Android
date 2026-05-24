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
    [InlineData(7, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    [InlineData(32, 16)]
    public void ClampSamples_snaps_to_nearest_bucket(int desired, int expected)
    {
        Assert.Equal(expected, MsaaSceneFramebuffer.ClampSamples(desired));
    }

    [Fact]
    public void ClampSamples_handles_all_legal_buckets()
    {
        Assert.Equal(1, MsaaSceneFramebuffer.ClampSamples(1));
        Assert.Equal(2, MsaaSceneFramebuffer.ClampSamples(2));
        Assert.Equal(4, MsaaSceneFramebuffer.ClampSamples(4));
        Assert.Equal(8, MsaaSceneFramebuffer.ClampSamples(8));
        Assert.Equal(16, MsaaSceneFramebuffer.ClampSamples(16));
    }
}
