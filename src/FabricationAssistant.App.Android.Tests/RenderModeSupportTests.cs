using FabricationAssistant.Rendering.Gles;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class RenderModeSupportTests
{
    [Theory]
    [InlineData(RenderMode.ShadedWithEdges, RenderMode.ShadedWithEdges)]
    [InlineData(RenderMode.Shaded, RenderMode.Shaded)]
    [InlineData(RenderMode.Wireframe, RenderMode.Wireframe)]
    [InlineData(RenderMode.Clay, RenderMode.Clay)]
    [InlineData(RenderMode.Realistic, RenderMode.Shaded)]
    public void EffectiveAndroidMode_maps_only_realistic_to_shaded(
        RenderMode requested,
        RenderMode expected)
    {
        Assert.Equal(expected, RenderModeSupport.EffectiveAndroidMode(requested));
    }
}
