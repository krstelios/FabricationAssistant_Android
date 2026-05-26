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

    // ── Camera & helpers ───────────────────────────────────────────────
    public bool ShowGrid;
    public bool ShiftGridToModelMin;
    public bool UseAutomaticGridSpacing;
    public float GridSpacingMm;
    public float GridLineThickness;
    public float[] GridLineColor;       // RGB, 0-1
    public bool ShowAxes;
    public bool IsPerspective;
    public bool LightweightNavigationEnabled;

    // ── Scene colors ───────────────────────────────────────────────────
    public float[] BackgroundColor;     // RGB, 0-1
    public float[] SurfaceColor;        // RGB, 0-1
    public float SurfaceOpacity;        // 0-1

    // ── CAD edges ──────────────────────────────────────────────────────
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

    // ── Clay render ────────────────────────────────────────────────────
    public float[] ClaySurfaceColor;    // RGB, 0-1
    public float[] ClayBackgroundColor; // RGB, 0-1
    public bool ClayFeatureEdgesEnabled;
    public float[] ClayFeatureEdgeColor; // RGBA, 0-1
    public float ClayFeatureEdgeWidth;   // pixels
    public float ClayFeatureEdgeDepthBias;
    public float ClayFeatureEdgeCreaseAngleDegrees;

    // ── Lighting ───────────────────────────────────────────────────────
    public float BaseColorLift;
    public float AmbientStrength;
    public float HeadlightStrength;
    public float KeyLightStrength;
    public float FillLightStrength;
    public float BounceLightStrength;
    public float HemisphereStrength;
    public float SpecularStrength;
    public float SpecularPower;

    // ── AA + occlusion + contour ───────────────────────────────────────
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

    // ── Selection outline ──────────────────────────────────────────────
    public bool OutlineEnabled;
    public float[] OutlineColor;        // RGB, 0-1
    public float OutlineThicknessPx;
    public float[] HoverOutlineColor;   // RGB, 0-1
    public float HoverOutlineThicknessPx;
    public float HoverTintStrength;

    /// <summary>
    /// Returns the defaults that match the desktop SceneAppearanceViewModel
    /// at boot. Each field's value mirrors the desktop default so first-load
    /// behavior is consistent between the two apps.
    /// </summary>
    public static SceneAppearance CreateDefault() => new()
    {
        Mode = RenderMode.ShadedWithEdges,

        ShowGrid = true,
        ShiftGridToModelMin = true,
        UseAutomaticGridSpacing = true,
        GridSpacingMm = 100.8f,
        GridLineThickness = 1.0f,
        GridLineColor = new[] { 0.35f, 0.35f, 0.35f },
        ShowAxes = true,
        IsPerspective = true,
        LightweightNavigationEnabled = false,

        BackgroundColor = new[] { 0.079f, 0.086f, 0.097f },
        SurfaceColor = new[] { 0.82f, 0.82f, 0.82f },
        SurfaceOpacity = 1.0f,

        EdgesEnabled = true,
        EdgeColor = new[] { 0.24028806f, 0.24f, 0.26f },
        EdgeWidth = 1.0f,
        CadEdgeFeatureAngleDegrees = 28.0f,
        CadEdgeCoplanarToleranceDegrees = 5.0f,
        CadEdgeWeldToleranceScale = 1.0e-5f,
        CadEdgeSilhouetteEnabled = true,
        EdgeDepthBias = 0.0f,
        SurfaceOffsetFactor = 1.0f,
        SurfaceOffsetUnits = 1.0f,

        ClaySurfaceColor = new[] { 0.804f, 0.796f, 0.797f },
        ClayBackgroundColor = new[] { 1.0f, 1.0f, 1.0f },
        ClayFeatureEdgesEnabled = true,
        ClayFeatureEdgeColor = new[] { 0.11975311f, 0.12004116f, 0.11650209f, 0.48666665f },
        ClayFeatureEdgeWidth = 0.95f,
        ClayFeatureEdgeDepthBias = 0.0f,
        ClayFeatureEdgeCreaseAngleDegrees = 35.0f,

        BaseColorLift = 0.1095f,
        AmbientStrength = 0.306f,
        HeadlightStrength = 0.14f,
        KeyLightStrength = 0.34f,
        FillLightStrength = 0.24f,
        BounceLightStrength = 0.0f,
        HemisphereStrength = 0.28f,
        SpecularStrength = 0.354f,
        SpecularPower = 77.0f,

        AmbientOcclusionEnabled = true,
        AoSampleCount = 32,
        AoRadius = 0.009f,
        AoBias = 0.0002f,
        AoIntensity = 1.45f,
        AoPower = 1.33f,
        AoContrast = 1.0f,
        AoMaxDistance = 1.50f,
        AoFadeStart = 0.50f,
        AoFadeEnd = 1.31f,
        AoBlurEnabled = true,
        AoBlurRadius = 6,
        AoBlurSharpness = 10.9f,
        AoBlurPasses = 1,
        AoNoiseScale = 8.0f,
        ContourStrength = 0.16080001f,
        ContourPower = 4.4105f,
        MsaaSamples = 0,

        OutlineEnabled = true,
        OutlineColor = new[] { 1.0f, 0.0f, 0.0f },
        OutlineThicknessPx = 3.2098765f,
        HoverOutlineColor = new[] { 0.0f, 1.0f, 0.0f },
        HoverOutlineThicknessPx = 0.37757202f,
        HoverTintStrength = 0.2f,
    };

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
