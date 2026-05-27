namespace FabricationAssistant.Rendering.Gles;

public enum RenderMode
{
    ShadedWithEdges = 0,
    Shaded = 1,
    Wireframe = 2,
    Clay = 3,
    /// <summary>
    /// Not implemented in the Gles renderer. Android maps this to Shaded
    /// at draw time via AndroidRenderModeShim. The integer value matches
    /// Core.Services.RenderMode.Realistic so a persisted value of 4 round-
    /// trips correctly when the same project is reopened on desktop.
    /// </summary>
    Realistic = 4,
}

public static class SceneAppearanceDefaults
{
    public const RenderMode Mode = RenderMode.ShadedWithEdges;

    public const bool ShowGrid = true;
    public const bool ShiftGridToModelMin = true;
    public const bool UseAutomaticGridSpacing = true;
    public const float GridSpacingMm = 100.8f;
    public const float GridLineThickness = 1.0f;
    public const float GridLineColorR = 0.35f;
    public const float GridLineColorG = 0.35f;
    public const float GridLineColorB = 0.35f;
    public const bool ShowAxes = true;
    public const bool IsPerspective = true;
    public const bool LightweightNavigationEnabled = true;

    public const float BackgroundR = 0.079f;
    public const float BackgroundG = 0.086f;
    public const float BackgroundB = 0.097f;
    public const float SurfaceR = 0.82f;
    public const float SurfaceG = 0.82f;
    public const float SurfaceB = 0.82f;
    public const float SurfaceOpacity = 1.0f;

    public const bool EdgesEnabled = true;
    public const float EdgeR = 0.24028806f;
    public const float EdgeG = 0.24f;
    public const float EdgeB = 0.26f;
    public const float EdgeWidth = 1.0f;
    public const float CadEdgeFeatureAngleDegrees = 28.0f;
    public const float CadEdgeCoplanarToleranceDegrees = 5.0f;
    public const float CadEdgeWeldToleranceScale = 1.0e-5f;
    public const bool CadEdgeSilhouetteEnabled = true;
    public const float EdgeDepthBias = 0.0f;
    public const float SurfaceOffsetFactor = 2.44f;
    public const float SurfaceOffsetUnits = 0.0f;

    public const float ClaySurfaceR = 0.804f;
    public const float ClaySurfaceG = 0.796f;
    public const float ClaySurfaceB = 0.797f;
    public const float ClayBackgroundR = 1.0f;
    public const float ClayBackgroundG = 1.0f;
    public const float ClayBackgroundB = 1.0f;
    public const bool ClayFeatureEdgesEnabled = true;
    public const float ClayFeatureEdgeR = 0.11975311f;
    public const float ClayFeatureEdgeG = 0.12004116f;
    public const float ClayFeatureEdgeB = 0.11650209f;
    public const float ClayFeatureEdgeA = 0.48666665f;
    public const float ClayFeatureEdgeWidth = 0.95f;
    public const float ClayFeatureEdgeDepthBias = 0.0f;
    public const float ClayFeatureEdgeCreaseAngleDegrees = 35.0f;

    public const float BaseColorLift = 0.1095f;
    public const float AmbientStrength = 0.306f;
    public const float HeadlightStrength = 0.14f;
    public const float KeyLightStrength = 0.34f;
    public const float FillLightStrength = 0.24f;
    public const float BounceLightStrength = 0.0f;
    public const float HemisphereStrength = 0.28f;
    public const float SpecularStrength = 0.354f;
    public const float SpecularPower = 77.0f;

    public const bool AmbientOcclusionEnabled = true;
    public const int AoSampleCount = 4;
    public const float AoRadius = 0.018617f;
    public const float AoBias = 0.00064f;
    public const float AoIntensity = 1.516f;
    public const float AoPower = 1.33f;
    public const float AoContrast = 1.0f;
    public const float AoMaxDistance = 2.0f;
    public const float AoFadeStart = 1.094f;
    public const float AoFadeEnd = 2.0f;
    public const bool AoBlurEnabled = true;
    public const int AoBlurRadius = 24;
    public const float AoBlurSharpness = 8.96f;
    public const int AoBlurPasses = 1;
    public const float AoNoiseScale = 2.52075f;
    public const float ContourStrength = 0.20040001f;
    public const float ContourPower = 4.4105f;
    public const int MsaaSamples = 4;

    public const bool OutlineEnabled = true;
    public const float OutlineR = 1.0f;
    public const float OutlineG = 0.0f;
    public const float OutlineB = 0.0f;
    public const float OutlineThicknessPx = 3.2098765f;
    public const float HoverOutlineR = 0.0f;
    public const float HoverOutlineG = 1.0f;
    public const float HoverOutlineB = 0.0f;
    public const float HoverOutlineThicknessPx = 0.37757202f;
    public const float HoverTintStrength = 0.2f;

    public static SceneAppearance Create() => new()
    {
        Mode = Mode,

        ShowGrid = ShowGrid,
        ShiftGridToModelMin = ShiftGridToModelMin,
        UseAutomaticGridSpacing = UseAutomaticGridSpacing,
        GridSpacingMm = GridSpacingMm,
        GridLineThickness = GridLineThickness,
        GridLineColor = new[] { GridLineColorR, GridLineColorG, GridLineColorB },
        ShowAxes = ShowAxes,
        IsPerspective = IsPerspective,
        LightweightNavigationEnabled = LightweightNavigationEnabled,

        BackgroundColor = new[] { BackgroundR, BackgroundG, BackgroundB },
        SurfaceColor = new[] { SurfaceR, SurfaceG, SurfaceB },
        SurfaceOpacity = SurfaceOpacity,

        EdgesEnabled = EdgesEnabled,
        EdgeColor = new[] { EdgeR, EdgeG, EdgeB },
        EdgeWidth = EdgeWidth,
        CadEdgeFeatureAngleDegrees = CadEdgeFeatureAngleDegrees,
        CadEdgeCoplanarToleranceDegrees = CadEdgeCoplanarToleranceDegrees,
        CadEdgeWeldToleranceScale = CadEdgeWeldToleranceScale,
        CadEdgeSilhouetteEnabled = CadEdgeSilhouetteEnabled,
        EdgeDepthBias = EdgeDepthBias,
        SurfaceOffsetFactor = SurfaceOffsetFactor,
        SurfaceOffsetUnits = SurfaceOffsetUnits,

        ClaySurfaceColor = new[] { ClaySurfaceR, ClaySurfaceG, ClaySurfaceB },
        ClayBackgroundColor = new[] { ClayBackgroundR, ClayBackgroundG, ClayBackgroundB },
        ClayFeatureEdgesEnabled = ClayFeatureEdgesEnabled,
        ClayFeatureEdgeColor = new[] { ClayFeatureEdgeR, ClayFeatureEdgeG, ClayFeatureEdgeB, ClayFeatureEdgeA },
        ClayFeatureEdgeWidth = ClayFeatureEdgeWidth,
        ClayFeatureEdgeDepthBias = ClayFeatureEdgeDepthBias,
        ClayFeatureEdgeCreaseAngleDegrees = ClayFeatureEdgeCreaseAngleDegrees,

        BaseColorLift = BaseColorLift,
        AmbientStrength = AmbientStrength,
        HeadlightStrength = HeadlightStrength,
        KeyLightStrength = KeyLightStrength,
        FillLightStrength = FillLightStrength,
        BounceLightStrength = BounceLightStrength,
        HemisphereStrength = HemisphereStrength,
        SpecularStrength = SpecularStrength,
        SpecularPower = SpecularPower,

        AmbientOcclusionEnabled = AmbientOcclusionEnabled,
        AoSampleCount = AoSampleCount,
        AoRadius = AoRadius,
        AoBias = AoBias,
        AoIntensity = AoIntensity,
        AoPower = AoPower,
        AoContrast = AoContrast,
        AoMaxDistance = AoMaxDistance,
        AoFadeStart = AoFadeStart,
        AoFadeEnd = AoFadeEnd,
        AoBlurEnabled = AoBlurEnabled,
        AoBlurRadius = AoBlurRadius,
        AoBlurSharpness = AoBlurSharpness,
        AoBlurPasses = AoBlurPasses,
        AoNoiseScale = AoNoiseScale,
        ContourStrength = ContourStrength,
        ContourPower = ContourPower,
        MsaaSamples = MsaaSamples,

        OutlineEnabled = OutlineEnabled,
        OutlineColor = new[] { OutlineR, OutlineG, OutlineB },
        OutlineThicknessPx = OutlineThicknessPx,
        HoverOutlineColor = new[] { HoverOutlineR, HoverOutlineG, HoverOutlineB },
        HoverOutlineThicknessPx = HoverOutlineThicknessPx,
        HoverTintStrength = HoverTintStrength,
    };
}

/// <summary>
/// Per-frame appearance state for the viewport renderer. Mirrors the desktop
/// SceneAppearanceViewModel so user-tunable settings (lighting, edges, clay
/// colors, AO, render mode) flow into the GPU as a single struct rather than
/// twenty individual fields. AppSettings.Apply(ref SceneAppearance) hydrates
/// the struct from SharedPreferences; the renderer reads it on every frame.
/// Treat every float[] field as immutable after assignment. The UI thread
/// should replace arrays, never mutate them in place, so GL-thread snapshots
/// remain coherent.
/// </summary>
public struct SceneAppearance
{
    public RenderMode Mode;

    // Camera and helpers.
    public bool ShowGrid;
    public bool ShiftGridToModelMin;
    public bool UseAutomaticGridSpacing;
    public float GridSpacingMm;
    public float GridLineThickness;
    public float[] GridLineColor;       // RGB, 0-1
    public bool ShowAxes;
    public bool IsPerspective;
    public bool LightweightNavigationEnabled;

    // Scene colors.
    public float[] BackgroundColor;     // RGB, 0-1
    public float[] SurfaceColor;        // RGB, 0-1
    public float SurfaceOpacity;        // 0-1

    // CAD edges.
    public bool EdgesEnabled;
    public float[] EdgeColor;           // RGB, 0-1
    public float EdgeWidth;             // pixels
    public float CadEdgeFeatureAngleDegrees;
    public float CadEdgeCoplanarToleranceDegrees;
    public float CadEdgeWeldToleranceScale;
    public bool CadEdgeSilhouetteEnabled;
    public float EdgeDepthBias;
    public float SurfaceOffsetFactor;
    public float SurfaceOffsetUnits;

    // Clay render.
    public float[] ClaySurfaceColor;    // RGB, 0-1
    public float[] ClayBackgroundColor; // RGB, 0-1
    public bool ClayFeatureEdgesEnabled;
    public float[] ClayFeatureEdgeColor; // RGBA, 0-1
    public float ClayFeatureEdgeWidth;   // pixels
    public float ClayFeatureEdgeDepthBias;
    public float ClayFeatureEdgeCreaseAngleDegrees;

    // Lighting.
    public float BaseColorLift;
    public float AmbientStrength;
    public float HeadlightStrength;
    public float KeyLightStrength;
    public float FillLightStrength;
    public float BounceLightStrength;
    public float HemisphereStrength;
    public float SpecularStrength;
    public float SpecularPower;

    // AA, occlusion, and contour.
    public bool AmbientOcclusionEnabled;
    public int AoSampleCount;
    public float AoRadius;
    public float AoBias;
    public float AoIntensity;
    public float AoPower;
    public float AoContrast;
    public float AoMaxDistance;
    public float AoFadeStart;
    public float AoFadeEnd;
    public bool AoBlurEnabled;
    public int AoBlurRadius;
    public float AoBlurSharpness;
    public int AoBlurPasses;
    public float AoNoiseScale;
    public float ContourStrength;
    public float ContourPower;
    public int MsaaSamples;             // 0 / 2 / 4 / 8

    // Selection outline.
    public bool OutlineEnabled;
    public float[] OutlineColor;        // RGB, 0-1
    public float OutlineThicknessPx;
    public float[] HoverOutlineColor;   // RGB, 0-1
    public float HoverOutlineThicknessPx;
    public float HoverTintStrength;

    /// <summary>
    /// Returns the defaults consumed by Android before SharedPreferences
    /// overrides are applied. Keep these values aligned with AppSettings.
    /// </summary>
    public static SceneAppearance CreateDefault() => SceneAppearanceDefaults.Create();

    /// <summary>
    /// Returns a GL-thread handoff copy whose array fields cannot be changed
    /// by future UI-side edits to this instance.
    /// </summary>
    public SceneAppearance CreateRendererSnapshot()
    {
        var snapshot = this;
        snapshot.GridLineColor = CloneArray(GridLineColor);
        snapshot.BackgroundColor = CloneArray(BackgroundColor);
        snapshot.SurfaceColor = CloneArray(SurfaceColor);
        snapshot.EdgeColor = CloneArray(EdgeColor);
        snapshot.ClaySurfaceColor = CloneArray(ClaySurfaceColor);
        snapshot.ClayBackgroundColor = CloneArray(ClayBackgroundColor);
        snapshot.ClayFeatureEdgeColor = CloneArray(ClayFeatureEdgeColor);
        snapshot.OutlineColor = CloneArray(OutlineColor);
        snapshot.HoverOutlineColor = CloneArray(HoverOutlineColor);
        return snapshot;
    }

    private static float[] CloneArray(float[]? values)
        => values is { Length: > 0 } ? (float[])values.Clone() : Array.Empty<float>();
}
