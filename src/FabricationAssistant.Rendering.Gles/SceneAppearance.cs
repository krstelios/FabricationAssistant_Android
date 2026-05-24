namespace FabricationAssistant.Rendering.Gles;

public enum RenderMode
{
    ShadedWithEdges = 0,
    Shaded = 1,
    Wireframe = 2,
    Clay = 3,
}

/// <summary>
/// Per-frame appearance state for the viewport renderer. Mirrors the desktop
/// SceneAppearanceViewModel so user-tunable settings (lighting, edges, clay
/// colors, AO, render mode) flow into the GPU as a single struct rather than
/// twenty individual fields. AppSettings.Apply(ref SceneAppearance) hydrates
/// the struct from SharedPreferences; the renderer reads it on every frame.
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
    public bool ShowViewCube;
    public bool IsPerspective;

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
    public float AoRadius;
    public float AoBias;
    public float AoIntensity;
    public int AoBlurPasses;
    public float ContourStrength;
    public float ContourPower;
    public int MsaaSamples;             // 0 / 2 / 4

    // ── Selection outline ──────────────────────────────────────────────
    public bool OutlineEnabled;
    public float[] OutlineColor;        // RGB, 0-1
    public float OutlineThicknessPx;

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
        GridSpacingMm = 10.0f,
        GridLineThickness = 1.0f,
        GridLineColor = new[] { 0.28f, 0.30f, 0.33f },
        ShowAxes = true,
        ShowViewCube = true,
        IsPerspective = true,

        BackgroundColor = new[] { 0.10f, 0.11f, 0.12f },
        SurfaceColor = new[] { 0.78f, 0.80f, 0.82f },
        SurfaceOpacity = 1.0f,

        EdgesEnabled = true,
        EdgeColor = new[] { 0.05f, 0.05f, 0.06f },
        EdgeWidth = 1.0f,
        CadEdgeFeatureAngleDegrees = 28.0f,
        CadEdgeCoplanarToleranceDegrees = 5.0f,
        CadEdgeWeldToleranceScale = 1.0e-5f,
        CadEdgeSilhouetteEnabled = true,
        EdgeDepthBias = 0.0f,
        SurfaceOffsetFactor = 1.0f,
        SurfaceOffsetUnits = 1.0f,

        ClaySurfaceColor = new[] { 0.85f, 0.78f, 0.65f },
        ClayBackgroundColor = new[] { 0.14f, 0.14f, 0.16f },

        BaseColorLift = 0.04f,
        AmbientStrength = 0.40f,
        HeadlightStrength = 0.30f,
        KeyLightStrength = 0.55f,
        FillLightStrength = 0.20f,
        BounceLightStrength = 0.15f,
        HemisphereStrength = 0.40f,
        SpecularStrength = 0.10f,
        SpecularPower = 32.0f,

        AmbientOcclusionEnabled = true,
        AoRadius = 0.50f,
        AoBias = 0.025f,
        AoIntensity = 1.0f,
        AoBlurPasses = 2,
        ContourStrength = 0.50f,
        ContourPower = 2.0f,
        MsaaSamples = 4,

        OutlineEnabled = true,
        OutlineColor = new[] { 1.0f, 0.62f, 0.20f },
        OutlineThicknessPx = 2.5f,
    };
}
