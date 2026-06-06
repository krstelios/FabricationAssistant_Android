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
    [InlineData(1.4377f, 0.1f, 5.0f, 0.01f, 1.44f)]
    [InlineData(1.00025f, 0.75f, 4.0f, 0.05f, 1.0f)]
    [InlineData(0.000552f, 0.0f, 0.01f, 0.00005f, 0.00055f)]
    [InlineData(float.NaN, 0.1f, 5.0f, 0.01f, 1.25f)]
    public void ClampAndSnap_rounds_to_step_before_persistence(
        float value,
        float min,
        float max,
        float step,
        float expected)
        => AssertFloatClose(expected, AppSettingsValueGuards.ClampAndSnap(value, min, max, step, fallback: 1.25f));

    [Theory]
    [InlineData(0.004476f, 0.001f, 0.08f, 2, 0.0045f)]
    [InlineData(100.8f, 0.001f, 1_000_000.0f, 3, 101.0f)]
    [InlineData(float.PositiveInfinity, 0.001f, 1_000_000.0f, 3, 10.0f)]
    public void ClampAndRoundToSignificantDigits_keeps_log_slider_values_readable(
        float value,
        float min,
        float max,
        int significantDigits,
        float expected)
        => AssertFloatClose(
            expected,
            AppSettingsValueGuards.ClampAndRoundToSignificantDigits(value, min, max, significantDigits, fallback: 10.0f));

    [Theory]
    [InlineData(0.0f, 0.002f, 0.00005f)]
    [InlineData(0.0f, 0.01f, 0.0005f)]
    [InlineData(0.001f, 0.08f, 0.0005f)]
    [InlineData(0.0f, 1.0f, 0.01f)]
    [InlineData(0.1f, 5.0f, 0.05f)]
    [InlineData(1.0f, 20.0f, 0.1f)]
    [InlineData(1.0f, 150.0f, 1.0f)]
    public void ResolveLinearSliderStep_uses_coarse_readable_increments(float min, float max, float expected)
        => AssertFloatClose(expected, AppSettingsValueGuards.ResolveLinearSliderStep(min, max));

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

    private static void AssertFloatClose(float expected, float actual)
        => Assert.True(
            Math.Abs(expected - actual) <= 0.00001f,
            $"Expected {expected:R}, actual {actual:R}.");
}
