using FabricationAssistant.Rendering.Gles;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class SceneAppearanceTests
{
    [Fact]
    public void CreateDefault_uses_runtime_app_defaults()
    {
        SceneAppearance appearance = SceneAppearance.CreateDefault();

        Assert.Equal(SceneAppearanceDefaults.LightweightNavigationEnabled, appearance.LightweightNavigationEnabled);
        Assert.Equal(SceneAppearanceDefaults.SurfaceOffsetFactor, appearance.SurfaceOffsetFactor);
        Assert.Equal(SceneAppearanceDefaults.SurfaceOffsetUnits, appearance.SurfaceOffsetUnits);
        Assert.Equal(SceneAppearanceDefaults.AoSampleCount, appearance.AoSampleCount);
        Assert.Equal(SceneAppearanceDefaults.AoRadius, appearance.AoRadius);
        Assert.Equal(SceneAppearanceDefaults.AoBias, appearance.AoBias);
        Assert.Equal(SceneAppearanceDefaults.AoIntensity, appearance.AoIntensity);
        Assert.Equal(SceneAppearanceDefaults.AoMaxDistance, appearance.AoMaxDistance);
        Assert.Equal(SceneAppearanceDefaults.AoFadeStart, appearance.AoFadeStart);
        Assert.Equal(SceneAppearanceDefaults.AoFadeEnd, appearance.AoFadeEnd);
        Assert.Equal(SceneAppearanceDefaults.AoBlurRadius, appearance.AoBlurRadius);
        Assert.Equal(SceneAppearanceDefaults.AoBlurSharpness, appearance.AoBlurSharpness);
        Assert.Equal(SceneAppearanceDefaults.AoNoiseScale, appearance.AoNoiseScale);
        Assert.Equal(SceneAppearanceDefaults.ContourStrength, appearance.ContourStrength);
        Assert.Equal(SceneAppearanceDefaults.MsaaSamples, appearance.MsaaSamples);
    }

    [Fact]
    public void CreateRendererSnapshot_clones_array_fields()
    {
        SceneAppearance appearance = SceneAppearance.CreateDefault();

        SceneAppearance snapshot = appearance.CreateRendererSnapshot();

        AssertCloned(appearance.GridLineColor, snapshot.GridLineColor);
        AssertCloned(appearance.BackgroundColor, snapshot.BackgroundColor);
        AssertCloned(appearance.SurfaceColor, snapshot.SurfaceColor);
        AssertCloned(appearance.EdgeColor, snapshot.EdgeColor);
        AssertCloned(appearance.ClaySurfaceColor, snapshot.ClaySurfaceColor);
        AssertCloned(appearance.ClayBackgroundColor, snapshot.ClayBackgroundColor);
        AssertCloned(appearance.ClayFeatureEdgeColor, snapshot.ClayFeatureEdgeColor);
        AssertCloned(appearance.OutlineColor, snapshot.OutlineColor);
        AssertCloned(appearance.HoverOutlineColor, snapshot.HoverOutlineColor);

        appearance.BackgroundColor[0] = 0.42f;
        Assert.NotEqual(appearance.BackgroundColor[0], snapshot.BackgroundColor[0]);
    }

    [Fact]
    public void CreateRendererSnapshot_preserves_scalar_fields()
    {
        SceneAppearance appearance = SceneAppearance.CreateDefault();
        appearance.Mode = RenderMode.Clay;
        appearance.ShowGrid = false;
        appearance.MsaaSamples = 8;
        appearance.IsPerspective = false;

        SceneAppearance snapshot = appearance.CreateRendererSnapshot();

        Assert.Equal(appearance.Mode, snapshot.Mode);
        Assert.Equal(appearance.ShowGrid, snapshot.ShowGrid);
        Assert.Equal(appearance.MsaaSamples, snapshot.MsaaSamples);
        Assert.Equal(appearance.IsPerspective, snapshot.IsPerspective);
    }

    private static void AssertCloned(float[] source, float[] clone)
    {
        Assert.NotSame(source, clone);
        Assert.Equal(source, clone);
    }
}
