using FabricationAssistant.Rendering.Gles;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AppSettingsValueGuardsTests
{
    // S9-F10: EffectiveRenderMode had no direct test. Only the two shaded
    // variants react to the edges toggle; every other mode passes through.
    [Theory]
    [InlineData((int)RenderMode.ShadedWithEdges, true, (int)RenderMode.ShadedWithEdges)]
    [InlineData((int)RenderMode.ShadedWithEdges, false, (int)RenderMode.Shaded)]
    [InlineData((int)RenderMode.Shaded, true, (int)RenderMode.ShadedWithEdges)]
    [InlineData((int)RenderMode.Shaded, false, (int)RenderMode.Shaded)]
    [InlineData((int)RenderMode.Wireframe, true, (int)RenderMode.Wireframe)]
    [InlineData((int)RenderMode.Wireframe, false, (int)RenderMode.Wireframe)]
    [InlineData((int)RenderMode.Clay, true, (int)RenderMode.Clay)]
    [InlineData((int)RenderMode.Clay, false, (int)RenderMode.Clay)]
    [InlineData((int)RenderMode.Realistic, true, (int)RenderMode.Realistic)]
    [InlineData((int)RenderMode.Realistic, false, (int)RenderMode.Realistic)]
    public void EffectiveRenderMode_only_shaded_modes_follow_edges_toggle(
        int renderMode,
        bool edgesEnabled,
        int expected)
        => Assert.Equal(expected, AppSettingsValueGuards.EffectiveRenderMode(renderMode, edgesEnabled));

    [Theory]
    [InlineData((int)RenderMode.ShadedWithEdges, true)]
    [InlineData((int)RenderMode.Shaded, true)]
    [InlineData((int)RenderMode.Wireframe, false)]
    [InlineData((int)RenderMode.Clay, false)]
    [InlineData((int)RenderMode.Realistic, false)]
    public void IsShadedRenderMode_recognises_both_shaded_variants(int renderMode, bool expected)
        => Assert.Equal(expected, AppSettingsValueGuards.IsShadedRenderMode(renderMode));

    [Theory]
    [InlineData(float.NaN, 0.0f)]
    [InlineData(float.NegativeInfinity, 0.0f)]
    [InlineData(-0.25f, 0.0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.5f, 1.0f)]
    public void Clamp01_rejects_nonfinite_and_clamps_to_unit_range(float value, float expected)
        => Assert.Equal(expected, AppSettingsValueGuards.Clamp01(value));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    [InlineData(16, 8)]
    public void ClampAndroidMsaaSamples_maps_to_supported_android_values(int value, int expected)
        => Assert.Equal(expected, AppSettingsValueGuards.ClampAndroidMsaaSamples(value));

    [Fact]
    public void AndroidMsaaPersistenceRange_matches_supported_values()
    {
        Assert.Equal(0, AppSettingsValueGuards.MinAndroidMsaaSamples);
        Assert.Equal(8, AppSettingsValueGuards.MaxAndroidMsaaSamples);
    }
}
