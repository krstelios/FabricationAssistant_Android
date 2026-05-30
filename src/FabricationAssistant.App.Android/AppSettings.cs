using Android.Content;
using FabricationAssistant.Rendering.Gles;

namespace FabricationAssistant.App.Android;

/// <summary>
/// SharedPreferences-backed settings store. Holds every value the desktop
/// SceneAppearanceViewModel + viewport preferences expose, persisted across
/// app launches. <see cref="Apply"/> reads them into a SceneAppearance struct
/// in a single call so the renderer can consume them in one read.
/// </summary>
public static class AppSettings
{
    private const string FileName = "fa_settings";
    private const int SettingsSchemaVersion = 22;

    private const int DefaultAoSampleCount = SceneAppearanceDefaults.AoSampleCount;
    private const int MinAoSampleCount = 1;
    private const int MaxAoSampleCount = 96;
    private const float DefaultAoRadius = SceneAppearanceDefaults.AoRadius;
    private const float MinAoRadius = 0.001f;
    private const float MaxAoRadius = 0.08f;
    private const float DefaultAoBias = SceneAppearanceDefaults.AoBias;
    private const float MinAoBias = 0.0f;
    private const float MaxAoBias = 0.01f;
    private const float DefaultAoIntensity = SceneAppearanceDefaults.AoIntensity;
    private const float MinAoIntensity = 0.0f;
    private const float MaxAoIntensity = 4.0f;
    private const float DefaultAoPower = SceneAppearanceDefaults.AoPower;
    private const float MinAoPower = 0.25f;
    private const float MaxAoPower = 4.0f;
    private const float DefaultAoContrast = SceneAppearanceDefaults.AoContrast;
    private const float MinAoContrast = 0.0f;
    private const float MaxAoContrast = 4.0f;
    private const float DefaultAoMaxDistance = SceneAppearanceDefaults.AoMaxDistance;
    private const float MinAoMaxDistance = 0.05f;
    private const float MaxAoMaxDistance = 2.0f;
    private const float DefaultAoFadeStart = SceneAppearanceDefaults.AoFadeStart;
    private const float DefaultAoFadeEnd = SceneAppearanceDefaults.AoFadeEnd;
    private const float MinAoFade = 0.0f;
    private const float MaxAoFade = 2.0f;
    private const bool DefaultAoBlurEnabled = SceneAppearanceDefaults.AoBlurEnabled;
    private const int DefaultAoBlurRadius = SceneAppearanceDefaults.AoBlurRadius;
    private const int MinAoBlurRadius = 0;
    private const int MaxAoBlurRadius = 24;
    private const float DefaultAoBlurSharpness = SceneAppearanceDefaults.AoBlurSharpness;
    private const float MinAoBlurSharpness = 0.0f;
    private const float MaxAoBlurSharpness = 32.0f;
    private const int DefaultAoBlurPasses = SceneAppearanceDefaults.AoBlurPasses;
    private const int MinAoBlurPasses = 0;
    private const int MaxAoBlurPasses = 8;
    private const int ModeShadedWithEdges = (int)FabricationAssistant.Rendering.Gles.RenderMode.ShadedWithEdges;
    private const int ModeShaded = (int)FabricationAssistant.Rendering.Gles.RenderMode.Shaded;
    private const int ModeWireframe = (int)FabricationAssistant.Rendering.Gles.RenderMode.Wireframe;
    private const int ModeClay = (int)FabricationAssistant.Rendering.Gles.RenderMode.Clay;
    private const int ModeRealistic = (int)FabricationAssistant.Rendering.Gles.RenderMode.Realistic;
    private const float DefaultEdgeWidth = 1.0f;
    private const float MinEdgeWidth = 0.05f;
    private const float MaxEdgeWidth = 4.0f;
    private const float DefaultSurfaceOffsetFactor = SceneAppearanceDefaults.SurfaceOffsetFactor;
    private const float DefaultSurfaceOffsetUnits = SceneAppearanceDefaults.SurfaceOffsetUnits;
    private const float MinimumVisibleEdgeWidth = 0.75f;
    private const int MeasureModePointToPoint = 0;
    private const int MeasureModeFaceToPoint = 1;
    private const int MeasureModeFaceToFace = 2;
    private const int MeasureBoxModeAxisAligned = 0;
    private const int MeasureBoxModeBestFit = 1;
    private const float DefaultDimensionTextScale = 1.4484999f;
    private const float MinDimensionTextScale = 0.5f;
    private const float MaxDimensionTextScale = 4.0f;
    private const float DefaultMeasureSnapFactor = 1.0f;
    private const float MinMeasureSnapFactor = 0.1f;
    private const float MaxMeasureSnapFactor = 4.0f;
    private const float DefaultMeasureSnapVisibilityProbe = 0.01f;
    private const float MinMeasureSnapVisibilityProbe = 0.002f;
    private const float MaxMeasureSnapVisibilityProbe = 0.08f;
    private const float DefaultMeasureSnapOcclusionToleranceFactor = 1.0f;
    private const float MinMeasureSnapOcclusionToleranceFactor = 0.1f;
    private const float MaxMeasureSnapOcclusionToleranceFactor = 10.0f;
    private const float DefaultSectionGizmoSizeFraction = 0.10f;
    private const float LegacySectionGizmoSizeFractionBase = 0.10f;
    private const float MinSectionGizmoSizeFraction = 0.005f;
    private const float MaxSectionGizmoSizeFraction = 0.5f;
    private const float DefaultSectionGizmoScale = 1.0845f;
    private const float MinSectionGizmoScale = 0.5f;
    private const float MaxSectionGizmoScale = 4.0f;
    private const float DefaultSectionEdgeWidth = 2.4f;
    private const float MinSectionEdgeWidth = 0.5f;
    private const float MaxSectionEdgeWidth = 8.0f;
    private const float DefaultMouseOrbitSpeed = 1.0f;
    private const float DefaultMousePanSpeed = 1.1f;
    private const float DefaultMouseWheelZoomSpeed = 1.0f;
    private const float DefaultMouseDragThresholdDip = 4.0f;
    private const float MinMouseSpeed = 0.1f;
    private const float MaxMouseSpeed = 5.0f;
    private const float MinMouseDragThresholdDip = 1.0f;
    private const float MaxMouseDragThresholdDip = 20.0f;
    private const float DefaultCameraNearClipMm = 10.0f;
    private const float DefaultCameraFarClipMm = 5000.0f;
    private const float MinCameraClipMm = 0.001f;
    private const float MaxCameraNearClipMm = 10000000.0f;
    private const float MaxCameraFarClipMm = 100000000.0f;
    private const string DefaultCloudUserEmail = "";
    private const string DefaultCloudProjectName = "";

    private static ISharedPreferences? _prefs;
    private static readonly (string Key, float Expected)[] LegacyFloatDefaultsToRemove =
    [
        // Duplicate keys intentionally cover separate historical schema defaults from schema v9-v15.
        // Each value is stripped only when a user still has that exact legacy default, so newer defaults apply.
        ("edge_feature_angle", 25.0f),
        ("edge_coplanar_tol", 1.5f),
        ("edge_depth_bias", 0.00005f),
        ("ao_radius", 0.50f),
        ("ao_bias", 0.025f),
        ("ao_intensity", 1.0f),
        ("ao_radius", 0.009f),
        ("ao_bias", 0.0002f),
        ("ao_intensity", 1.45f),
        ("ao_power", 1.33f),
        ("ao_blur_sharpness", 10.9f),
        ("grid_spacing_mm", 10.0f),
        ("grid_r", 0.28f),
        ("grid_g", 0.30f),
        ("grid_b", 0.33f),
        ("bg_r", 0.10f),
        ("bg_g", 0.11f),
        ("bg_b", 0.12f),
        ("surface_r", 0.78f),
        ("surface_g", 0.80f),
        ("edge_r", 0.05f),
        ("edge_g", 0.05f),
        ("edge_b", 0.06f),
        ("edge_width", 1.0f),
        ("edge_width", 0.28595f),
        ("clay_surface_r", 0.85f),
        ("clay_surface_g", 0.78f),
        ("clay_surface_b", 0.65f),
        ("clay_bg_r", 0.14f),
        ("clay_bg_g", 0.14f),
        ("clay_bg_b", 0.16f),
        ("light_base_lift", 0.04f),
        ("light_ambient", 0.40f),
        ("light_headlight", 0.30f),
        ("light_key", 0.55f),
        ("light_fill", 0.20f),
        ("light_bounce", 0.15f),
        ("light_hemisphere", 0.40f),
        ("light_spec_strength", 0.10f),
        ("light_spec_power", 32.0f),
        ("contour_strength", 0.50f),
        ("contour_power", 2.0f),
        ("outline_g", 0.62f),
        ("outline_b", 0.20f),
        ("outline_thickness", 2.5f),
        ("camera_far_clip_mm", 50000.0f),
    ];
    private static readonly (string Key, int Expected)[] LegacyIntDefaultsToRemove =
    [
        // Duplicate keys intentionally cover separate historical schema defaults from schema v9-v15.
        // Each value is stripped only when a user still has that exact legacy default, so newer defaults apply.
        ("ao_blur_passes", 2),
        ("ao_sample_count", 32),
        ("ao_blur_radius", 6),
        ("ao_blur_passes", 1),
    ];
    private static readonly (string Key, float Min, float Max)[] FloatRangeGuards =
    [
        ("ao_radius", MinAoRadius, MaxAoRadius),
        ("ao_bias", MinAoBias, MaxAoBias),
        ("ao_intensity", MinAoIntensity, MaxAoIntensity),
        ("ao_power", MinAoPower, MaxAoPower),
        ("ao_contrast", MinAoContrast, MaxAoContrast),
        ("ao_max_distance", MinAoMaxDistance, MaxAoMaxDistance),
        ("ao_fade_start", MinAoFade, MaxAoFade),
        ("ao_fade_end", MinAoFade, MaxAoFade),
        ("ao_blur_sharpness", MinAoBlurSharpness, MaxAoBlurSharpness),
        ("dimension_text_scale", MinDimensionTextScale, MaxDimensionTextScale),
        ("measure_snap_edge_factor", MinMeasureSnapFactor, MaxMeasureSnapFactor),
        ("measure_snap_endpoint_factor", MinMeasureSnapFactor, MaxMeasureSnapFactor),
        ("measure_snap_visibility_probe", MinMeasureSnapVisibilityProbe, MaxMeasureSnapVisibilityProbe),
        ("measure_snap_occlusion_tolerance_factor", MinMeasureSnapOcclusionToleranceFactor, MaxMeasureSnapOcclusionToleranceFactor),
        ("section_gizmo_scale", MinSectionGizmoScale, MaxSectionGizmoScale),
        ("mouse_orbit_speed", MinMouseSpeed, MaxMouseSpeed),
        ("mouse_pan_speed", MinMouseSpeed, MaxMouseSpeed),
        ("mouse_wheel_zoom_speed", MinMouseSpeed, MaxMouseSpeed),
        ("mouse_drag_threshold_dip", MinMouseDragThresholdDip, MaxMouseDragThresholdDip),
        ("camera_near_clip_mm", MinCameraClipMm, MaxCameraNearClipMm),
        ("camera_far_clip_mm", MinCameraClipMm, MaxCameraFarClipMm),
    ];
    private static readonly (string Key, int Min, int Max)[] IntRangeGuards =
    [
        ("ao_sample_count", MinAoSampleCount, MaxAoSampleCount),
        ("ao_blur_radius", MinAoBlurRadius, MaxAoBlurRadius),
        ("ao_blur_passes", MinAoBlurPasses, MaxAoBlurPasses),
        ("msaa_samples", AppSettingsValueGuards.MinAndroidMsaaSamples, AppSettingsValueGuards.MaxAndroidMsaaSamples),
    ];

    public static void Initialize(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ISharedPreferences prefs = context.GetSharedPreferences(FileName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Context.GetSharedPreferences returned null.");
        System.Threading.Interlocked.CompareExchange(ref _prefs, prefs, null);
        CloudServerConfig.Initialize(context);
        MigrateDefaultsIfNeeded();
    }

    public static void ResetToDefaults()
    {
        Edit(editor =>
        {
            editor.Clear();
            editor.PutInt("settings_schema_version", SettingsSchemaVersion);
        });
    }

    private static ISharedPreferences Prefs =>
        _prefs ?? throw new InvalidOperationException("AppSettings.Initialize(context) must be called first.");

    // ── Navigation (existing) ──────────────────────────────────────────
    public static float OrbitSensitivity { get => Get("orbit_sensitivity", 1.0f); set => Put("orbit_sensitivity", System.Math.Clamp(value, 0.1f, 5.0f)); }
    // Tuned on Galaxy S-series touch input to reduce pan overshoot on dense CAD scenes.
    public static float PanSensitivity { get => Get("pan_sensitivity", 0.87420005f); set => Put("pan_sensitivity", System.Math.Clamp(value, 0.1f, 5.0f)); }
    public static float ZoomSensitivity { get => Get("zoom_sensitivity", 1.0f); set => Put("zoom_sensitivity", System.Math.Clamp(value, 0.1f, 5.0f)); }
    public static bool LightweightNavigationEnabled { get => Get("lightweight_navigation_enabled", true); set => Put("lightweight_navigation_enabled", value); }
    public static bool SpenPalmRejectionEnabled { get => Get("spen_palm_rejection", false); set => Put("spen_palm_rejection", value); }
    public static float SectionGizmoScale { get => GetFloatInRange("section_gizmo_scale", DefaultSectionGizmoScale, MinSectionGizmoScale, MaxSectionGizmoScale); set => Put("section_gizmo_scale", System.Math.Clamp(value, MinSectionGizmoScale, MaxSectionGizmoScale)); }
    public static float MouseOrbitSpeed { get => GetFloatInRange("mouse_orbit_speed", DefaultMouseOrbitSpeed, MinMouseSpeed, MaxMouseSpeed); set => Put("mouse_orbit_speed", System.Math.Clamp(value, MinMouseSpeed, MaxMouseSpeed)); }
    public static float MousePanSpeed { get => GetFloatInRange("mouse_pan_speed", DefaultMousePanSpeed, MinMouseSpeed, MaxMouseSpeed); set => Put("mouse_pan_speed", System.Math.Clamp(value, MinMouseSpeed, MaxMouseSpeed)); }
    public static float MouseWheelZoomSpeed { get => GetFloatInRange("mouse_wheel_zoom_speed", DefaultMouseWheelZoomSpeed, MinMouseSpeed, MaxMouseSpeed); set => Put("mouse_wheel_zoom_speed", System.Math.Clamp(value, MinMouseSpeed, MaxMouseSpeed)); }
    public static float MouseDragThresholdDip { get => GetFloatInRange("mouse_drag_threshold_dip", DefaultMouseDragThresholdDip, MinMouseDragThresholdDip, MaxMouseDragThresholdDip); set => Put("mouse_drag_threshold_dip", System.Math.Clamp(value, MinMouseDragThresholdDip, MaxMouseDragThresholdDip)); }
    public static bool MouseHoverEnabled { get => Get("mouse_hover_enabled", true); set => Put("mouse_hover_enabled", value); }
    public static bool MouseInvertWheelZoom { get => Get("mouse_invert_wheel_zoom", false); set => Put("mouse_invert_wheel_zoom", value); }

    // ── Mode ───────────────────────────────────────────────────────────
    // Upper bound is ModeRealistic (4) so a persisted Realistic value
    // survives across sessions. AndroidRenderModeShim maps it to Shaded
    // at draw time; the UI picker never offers Realistic as a choice.
    public static int RenderMode { get => Get("render_mode", ModeShadedWithEdges); set => Put("render_mode", System.Math.Clamp(value, ModeShadedWithEdges, ModeRealistic)); }

    public static int RenderModeSelectionIndex => RenderMode switch
    {
        ModeWireframe => 1,
        ModeClay => 2,
        _ => 0,
    };

    public static void SetRenderModeSelectionIndex(int index)
    {
        int mode = index switch
        {
            1 => ModeWireframe,
            2 => ModeClay,
            _ => EdgesEnabled ? ModeShadedWithEdges : ModeShaded,
        };
        RenderMode = mode;
    }

    // ── Camera & helpers ───────────────────────────────────────────────
    public static bool ShowGrid { get => Get("show_grid", true); set => Put("show_grid", value); }
    public static bool ShiftGridToModelMin { get => Get("shift_grid", true); set => Put("shift_grid", value); }
    public static bool UseAutomaticGridSpacing { get => Get("auto_grid_spacing", true); set => Put("auto_grid_spacing", value); }
    public static bool DoubleTapFitScreenEnabled { get => Get("double_tap_fit_screen", false); set => Put("double_tap_fit_screen", value); }
    public static float GridSpacingMm { get => GetFloatInRange("grid_spacing_mm", 100.8f, 0.001f, 1_000_000.0f); set => Put("grid_spacing_mm", System.Math.Clamp(value, 0.001f, 1_000_000.0f)); }
    public static float GridLineThickness { get => GetFloatInRange("grid_thickness", 1.0f, 1.0f, 8.0f); set => Put("grid_thickness", System.Math.Clamp(value, 1.0f, 8.0f)); }
    public static float GridLineColorR { get => Get("grid_r", 0.35f); set => Put("grid_r", Clamp01(value)); }
    public static float GridLineColorG { get => Get("grid_g", 0.35f); set => Put("grid_g", Clamp01(value)); }
    public static float GridLineColorB { get => Get("grid_b", 0.35f); set => Put("grid_b", Clamp01(value)); }
    public static void SetManualGridSpacing(float value)
    {
        float spacing = float.IsFinite(value) ? System.Math.Clamp(value, 0.001f, 1_000_000.0f) : 0.0f;
        Edit(editor =>
        {
            editor.PutFloat("grid_spacing_mm", spacing);
            editor.PutBoolean("auto_grid_spacing", false);
        });
    }
    public static void SetGridLineColor(float r, float g, float b) => PutRgb("grid_r", "grid_g", "grid_b", r, g, b);
    public static bool ShowAxes { get => Get("show_axes", true); set => Put("show_axes", value); }
    public static bool IsPerspective { get => Get("is_perspective", true); set => Put("is_perspective", value); }
    public static bool ManualCameraClipPlanesEnabled { get => Get("manual_camera_clip_planes", false); set => Put("manual_camera_clip_planes", value); }
    public static float CameraNearClipMm => GetFloatInRange("camera_near_clip_mm", DefaultCameraNearClipMm, MinCameraClipMm, MaxCameraNearClipMm);
    public static float CameraFarClipMm => GetFloatInRange("camera_far_clip_mm", DefaultCameraFarClipMm, MinCameraClipMm, MaxCameraFarClipMm);

    public static void SetCameraNearClipMm(float value)
    {
        float near = ClampCameraClipNearMm(value);
        float far = CameraFarClipMm;
        if (far <= near)
            far = ResolveFarClipAboveNear(near);

        Edit(editor =>
        {
            editor.PutFloat("camera_near_clip_mm", near);
            editor.PutFloat("camera_far_clip_mm", far);
        });
    }

    public static void SetCameraFarClipMm(float value)
    {
        float near = CameraNearClipMm;
        float far = ClampCameraClipFarMm(value);
        if (far <= near)
            far = ResolveFarClipAboveNear(near);

        Put("camera_far_clip_mm", far);
    }

    // ── Background + surface ───────────────────────────────────────────
    public static float BackgroundR { get => Get("bg_r", 0.079f); set => Put("bg_r", Clamp01(value)); }
    public static float BackgroundG { get => Get("bg_g", 0.086f); set => Put("bg_g", Clamp01(value)); }
    public static float BackgroundB { get => Get("bg_b", 0.097f); set => Put("bg_b", Clamp01(value)); }
    public static void SetBackgroundColor(float r, float g, float b) => PutRgb("bg_r", "bg_g", "bg_b", r, g, b);
    public static float SurfaceR { get => Get("surface_r", 0.82f); set => Put("surface_r", Clamp01(value)); }
    public static float SurfaceG { get => Get("surface_g", 0.82f); set => Put("surface_g", Clamp01(value)); }
    public static float SurfaceB { get => Get("surface_b", 0.82f); set => Put("surface_b", Clamp01(value)); }
    public static void SetSurfaceColor(float r, float g, float b) => PutRgb("surface_r", "surface_g", "surface_b", r, g, b);
    public static float SurfaceOpacity { get => GetFloatInRange("surface_opacity", 1.0f, 0.0f, 1.0f); set => Put("surface_opacity", Clamp01(value)); }

    // ── CAD Edges ──────────────────────────────────────────────────────
    public static bool EdgesEnabled { get => Get("edges_enabled", true); set => Put("edges_enabled", value); }

    public static void SetEdgesEnabledFromUi(bool enabled)
    {
        Edit(editor =>
        {
            editor.PutBoolean("edges_enabled", enabled);
            if (enabled && EdgeWidth < MinimumVisibleEdgeWidth)
                editor.PutFloat("edge_width", DefaultEdgeWidth);
            int renderMode = RenderMode;
            if (IsShadedRenderMode(renderMode))
                editor.PutInt("render_mode", enabled ? ModeShadedWithEdges : ModeShaded);
        });
    }
    public static float EdgeR { get => Get("edge_r", 0.24028806f); set => Put("edge_r", Clamp01(value)); }
    public static float EdgeG { get => Get("edge_g", 0.24f); set => Put("edge_g", Clamp01(value)); }
    public static float EdgeB { get => Get("edge_b", 0.26f); set => Put("edge_b", Clamp01(value)); }
    public static void SetEdgeColor(float r, float g, float b) => PutRgb("edge_r", "edge_g", "edge_b", r, g, b);
    public static float EdgeWidth { get => GetFloatInRange("edge_width", DefaultEdgeWidth, MinEdgeWidth, MaxEdgeWidth); set => Put("edge_width", System.Math.Clamp(value, MinEdgeWidth, MaxEdgeWidth)); }
    public static float CadEdgeFeatureAngleDegrees { get => GetFloatInRange("edge_feature_angle", 28.0f, 1.0f, 150.0f); set => Put("edge_feature_angle", System.Math.Clamp(value, 1.0f, 150.0f)); }
    public static float CadEdgeCoplanarToleranceDegrees { get => GetFloatInRange("edge_coplanar_tol", 5.0f, 0.0f, 30.0f); set => Put("edge_coplanar_tol", System.Math.Clamp(value, 0.0f, 30.0f)); }
    public static float CadEdgeWeldToleranceScale { get => GetFloatInRange("edge_weld_tol", 1.0e-5f, 1.0e-6f, 1.0e-4f); set => Put("edge_weld_tol", System.Math.Clamp(value, 1.0e-6f, 1.0e-4f)); }
    public static bool CadEdgeSilhouetteEnabled { get => Get("edge_silhouette", true); set => Put("edge_silhouette", value); }
    public static float EdgeDepthBias { get => GetFloatInRange("edge_depth_bias", 0.0f, 0.0f, 0.002f); set => Put("edge_depth_bias", System.Math.Clamp(value, 0.0f, 0.002f)); }
    public static float SurfaceOffsetFactor { get => GetFloatInRange("surface_offset_f", DefaultSurfaceOffsetFactor, 0.0f, 4.0f); set => Put("surface_offset_f", System.Math.Clamp(value, 0.0f, 4.0f)); }
    public static float SurfaceOffsetUnits { get => GetFloatInRange("surface_offset_u", DefaultSurfaceOffsetUnits, 0.0f, 4.0f); set => Put("surface_offset_u", System.Math.Clamp(value, 0.0f, 4.0f)); }

    // ── Clay ───────────────────────────────────────────────────────────
    public static float ClaySurfaceR { get => Get("clay_surface_r", 0.804f); set => Put("clay_surface_r", Clamp01(value)); }
    public static float ClaySurfaceG { get => Get("clay_surface_g", 0.796f); set => Put("clay_surface_g", Clamp01(value)); }
    public static float ClaySurfaceB { get => Get("clay_surface_b", 0.797f); set => Put("clay_surface_b", Clamp01(value)); }
    public static void SetClaySurfaceColor(float r, float g, float b) => PutRgb("clay_surface_r", "clay_surface_g", "clay_surface_b", r, g, b);
    public static float ClayBackgroundR { get => Get("clay_bg_r", 1.0f); set => Put("clay_bg_r", Clamp01(value)); }
    public static float ClayBackgroundG { get => Get("clay_bg_g", 1.0f); set => Put("clay_bg_g", Clamp01(value)); }
    public static float ClayBackgroundB { get => Get("clay_bg_b", 1.0f); set => Put("clay_bg_b", Clamp01(value)); }
    public static void SetClayBackgroundColor(float r, float g, float b) => PutRgb("clay_bg_r", "clay_bg_g", "clay_bg_b", r, g, b);
    public static bool ClayFeatureEdgesEnabled { get => Get("clay_feature_edges", true); set => Put("clay_feature_edges", value); }
    public static float ClayFeatureEdgeR { get => Get("clay_edge_r", 0.11975311f); set => Put("clay_edge_r", Clamp01(value)); }
    public static float ClayFeatureEdgeG { get => Get("clay_edge_g", 0.12004116f); set => Put("clay_edge_g", Clamp01(value)); }
    public static float ClayFeatureEdgeB { get => Get("clay_edge_b", 0.11650209f); set => Put("clay_edge_b", Clamp01(value)); }
    public static void SetClayFeatureEdgeColor(float r, float g, float b) => PutRgb("clay_edge_r", "clay_edge_g", "clay_edge_b", r, g, b);
    public static float ClayFeatureEdgeA { get => GetFloatInRange("clay_edge_a", 0.48666665f, 0.0f, 1.0f); set => Put("clay_edge_a", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float ClayFeatureEdgeWidth { get => GetFloatInRange("clay_edge_width", 0.95f, 0.05f, 4.0f); set => Put("clay_edge_width", System.Math.Clamp(value, 0.05f, 4.0f)); }
    public static float ClayFeatureEdgeDepthBias { get => GetFloatInRange("clay_edge_depth_bias", 0.0f, 0.0f, 0.01f); set => Put("clay_edge_depth_bias", System.Math.Clamp(value, 0.0f, 0.01f)); }
    public static float ClayFeatureEdgeCreaseAngleDegrees { get => GetFloatInRange("clay_edge_crease_angle", 35.0f, 1.0f, 150.0f); set => Put("clay_edge_crease_angle", System.Math.Clamp(value, 1.0f, 150.0f)); }

    // ── Lighting ───────────────────────────────────────────────────────
    public static float BaseColorLift { get => GetFloatInRange("light_base_lift", 0.1095f, 0.0f, 0.25f); set => Put("light_base_lift", System.Math.Clamp(value, 0.0f, 0.25f)); }
    public static float AmbientStrength { get => GetFloatInRange("light_ambient", 0.306f, 0.0f, 1.0f); set => Put("light_ambient", Clamp01(value)); }
    public static float HeadlightStrength { get => GetFloatInRange("light_headlight", 0.14f, 0.0f, 1.0f); set => Put("light_headlight", Clamp01(value)); }
    public static float KeyLightStrength { get => GetFloatInRange("light_key", 0.34f, 0.0f, 1.0f); set => Put("light_key", Clamp01(value)); }
    public static float FillLightStrength { get => GetFloatInRange("light_fill", 0.24f, 0.0f, 1.0f); set => Put("light_fill", Clamp01(value)); }
    public static float BounceLightStrength { get => GetFloatInRange("light_bounce", 0.0f, 0.0f, 1.0f); set => Put("light_bounce", Clamp01(value)); }
    public static float HemisphereStrength { get => GetFloatInRange("light_hemisphere", 0.28f, 0.0f, 1.0f); set => Put("light_hemisphere", Clamp01(value)); }
    public static float SpecularStrength { get => GetFloatInRange("light_spec_strength", 0.354f, 0.0f, 1.0f); set => Put("light_spec_strength", Clamp01(value)); }
    public static float SpecularPower { get => GetFloatInRange("light_spec_power", 77.0f, 1.0f, 128.0f); set => Put("light_spec_power", System.Math.Clamp(value, 1.0f, 128.0f)); }

    // ── AO + contour + MSAA ───────────────────────────────────────────
    public static bool AmbientOcclusionEnabled { get => Get("ao_enabled", true); set => Put("ao_enabled", value); }
    public static int AoSampleCount { get => GetIntInRange("ao_sample_count", DefaultAoSampleCount, MinAoSampleCount, MaxAoSampleCount); set => Put("ao_sample_count", System.Math.Clamp(value, MinAoSampleCount, MaxAoSampleCount)); }
    public static float AoRadius { get => GetFloatInRange("ao_radius", DefaultAoRadius, MinAoRadius, MaxAoRadius); set => Put("ao_radius", System.Math.Clamp(value, MinAoRadius, MaxAoRadius)); }
    public static float AoBias { get => GetFloatInRange("ao_bias", DefaultAoBias, MinAoBias, MaxAoBias); set => Put("ao_bias", System.Math.Clamp(value, MinAoBias, MaxAoBias)); }
    public static float AoIntensity { get => GetFloatInRange("ao_intensity", DefaultAoIntensity, MinAoIntensity, MaxAoIntensity); set => Put("ao_intensity", System.Math.Clamp(value, MinAoIntensity, MaxAoIntensity)); }
    public static float AoPower { get => GetFloatInRange("ao_power", DefaultAoPower, MinAoPower, MaxAoPower); set => Put("ao_power", System.Math.Clamp(value, MinAoPower, MaxAoPower)); }
    public static float AoContrast { get => GetFloatInRange("ao_contrast", DefaultAoContrast, MinAoContrast, MaxAoContrast); set => Put("ao_contrast", System.Math.Clamp(value, MinAoContrast, MaxAoContrast)); }
    public static float AoMaxDistance { get => GetFloatInRange("ao_max_distance", DefaultAoMaxDistance, MinAoMaxDistance, MaxAoMaxDistance); set => Put("ao_max_distance", System.Math.Clamp(value, MinAoMaxDistance, MaxAoMaxDistance)); }
    public static float AoFadeStart { get => GetFloatInRange("ao_fade_start", DefaultAoFadeStart, MinAoFade, MaxAoFade); set => Put("ao_fade_start", System.Math.Clamp(value, MinAoFade, MaxAoFade)); }
    public static float AoFadeEnd { get => GetFloatInRange("ao_fade_end", DefaultAoFadeEnd, MinAoFade, MaxAoFade); set => Put("ao_fade_end", System.Math.Clamp(value, MinAoFade, MaxAoFade)); }
    public static bool AoBlurEnabled { get => Get("ao_blur_enabled", DefaultAoBlurEnabled); set => Put("ao_blur_enabled", value); }
    public static int AoBlurRadius { get => GetIntInRange("ao_blur_radius", DefaultAoBlurRadius, MinAoBlurRadius, MaxAoBlurRadius); set => Put("ao_blur_radius", System.Math.Clamp(value, MinAoBlurRadius, MaxAoBlurRadius)); }
    public static float AoBlurSharpness { get => GetFloatInRange("ao_blur_sharpness", DefaultAoBlurSharpness, MinAoBlurSharpness, MaxAoBlurSharpness); set => Put("ao_blur_sharpness", System.Math.Clamp(value, MinAoBlurSharpness, MaxAoBlurSharpness)); }
    public static int AoBlurPasses { get => GetIntInRange("ao_blur_passes", DefaultAoBlurPasses, MinAoBlurPasses, MaxAoBlurPasses); set => Put("ao_blur_passes", System.Math.Clamp(value, MinAoBlurPasses, MaxAoBlurPasses)); }
    public static float ContourStrength { get => GetFloatInRange("contour_strength", 0.20040001f, 0.0f, 1.2f); set => Put("contour_strength", System.Math.Clamp(value, 0.0f, 1.2f)); }
    public static float ContourPower { get => GetFloatInRange("contour_power", 4.4105f, 0.5f, 6.0f); set => Put("contour_power", System.Math.Clamp(value, 0.5f, 6.0f)); }
    // Read-side clamp remains defence-in-depth; schema migration removes invalid persisted values.
    public static int MsaaSamples { get => ClampAndroidMsaaSamples(Get("msaa_samples", SceneAppearanceDefaults.MsaaSamples)); set => Put("msaa_samples", ClampAndroidMsaaSamples(value)); }

    // ── Selection ──────────────────────────────────────────────────────
    public static bool ShowSelectionHighlight { get => Get("show_selection", true); set => Put("show_selection", value); }
    public static bool OutlineEnabled { get => Get("outline_enabled", true); set => Put("outline_enabled", value); }
    public static float OutlineR { get => Get("outline_r", 1.0f); set => Put("outline_r", Clamp01(value)); }
    public static float OutlineG { get => Get("outline_g", 0.0f); set => Put("outline_g", Clamp01(value)); }
    public static float OutlineB { get => Get("outline_b", 0.0f); set => Put("outline_b", Clamp01(value)); }
    public static void SetOutlineColor(float r, float g, float b) => PutRgb("outline_r", "outline_g", "outline_b", r, g, b);
    public static float OutlineThicknessPx { get => GetFloatInRange("outline_thickness", 3.2098765f, 1.0f, 8.0f); set => Put("outline_thickness", System.Math.Clamp(value, 1.0f, 8.0f)); }
    public static float HoverOutlineR { get => Get("hover_outline_r", 0.0f); set => Put("hover_outline_r", Clamp01(value)); }
    public static float HoverOutlineG { get => Get("hover_outline_g", 1.0f); set => Put("hover_outline_g", Clamp01(value)); }
    public static float HoverOutlineB { get => Get("hover_outline_b", 0.0f); set => Put("hover_outline_b", Clamp01(value)); }
    public static void SetHoverOutlineColor(float r, float g, float b) => PutRgb("hover_outline_r", "hover_outline_g", "hover_outline_b", r, g, b);
    public static float HoverOutlineThicknessPx { get => GetFloatInRange("hover_outline_thickness", 0.37757202f, 0.05f, 8.0f); set => Put("hover_outline_thickness", System.Math.Clamp(value, 0.05f, 8.0f)); }
    public static float HoverTintStrength { get => GetFloatInRange("hover_tint_strength", 0.2f, 0.0f, 1.0f); set => Put("hover_tint_strength", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float DimensionHighlightR { get => Get("dimension_highlight_r", 1.0f); set => Put("dimension_highlight_r", Clamp01(value)); }
    public static float DimensionHighlightG { get => Get("dimension_highlight_g", 0.5019608f); set => Put("dimension_highlight_g", Clamp01(value)); }
    public static float DimensionHighlightB { get => Get("dimension_highlight_b", 0.2509804f); set => Put("dimension_highlight_b", Clamp01(value)); }
    public static void SetDimensionHighlightColor(float r, float g, float b) => PutRgb("dimension_highlight_r", "dimension_highlight_g", "dimension_highlight_b", r, g, b);

    // Measurement tools
    public static int MeasureModeSelectionIndex { get => GetIntInRange("measure_mode", MeasureModeFaceToFace, MeasureModePointToPoint, MeasureModeFaceToFace); set => Put("measure_mode", System.Math.Clamp(value, MeasureModePointToPoint, MeasureModeFaceToFace)); }
    public static int MeasureBoxModeSelectionIndex { get => GetIntInRange("measure_box_mode", MeasureBoxModeBestFit, MeasureBoxModeAxisAligned, MeasureBoxModeBestFit); set => Put("measure_box_mode", System.Math.Clamp(value, MeasureBoxModeAxisAligned, MeasureBoxModeBestFit)); }
    public static bool MeasureMultiMeasureEnabled { get => Get("measure_multi_enabled", true); set => Put("measure_multi_enabled", value); }
    public static bool MeasureShowDeltaBreakdown { get => Get("measure_show_deltas", true); set => Put("measure_show_deltas", value); }
    public static bool MeasurePointSnapEnabled { get => Get("measure_snap_enabled", true); set => Put("measure_snap_enabled", value); }
    public static bool MeasureEndpointSnapEnabled { get => Get("measure_snap_endpoint_enabled", true); set => Put("measure_snap_endpoint_enabled", value); }
    public static bool MeasureMidpointSnapEnabled { get => Get("measure_snap_midpoint_enabled", true); set => Put("measure_snap_midpoint_enabled", value); }
    public static bool MeasureSnapVisibleEdgesOnly { get => Get("measure_snap_visible_edges_only", true); set => Put("measure_snap_visible_edges_only", value); }
    public static float MeasureSnapEdgeFactor { get => GetFloatInRange("measure_snap_edge_factor", DefaultMeasureSnapFactor, MinMeasureSnapFactor, MaxMeasureSnapFactor); set => Put("measure_snap_edge_factor", System.Math.Clamp(value, MinMeasureSnapFactor, MaxMeasureSnapFactor)); }
    public static float MeasureSnapEndpointFactor { get => GetFloatInRange("measure_snap_endpoint_factor", DefaultMeasureSnapFactor, MinMeasureSnapFactor, MaxMeasureSnapFactor); set => Put("measure_snap_endpoint_factor", System.Math.Clamp(value, MinMeasureSnapFactor, MaxMeasureSnapFactor)); }
    public static float MeasureSnapVisibilityProbe { get => GetFloatInRange("measure_snap_visibility_probe", DefaultMeasureSnapVisibilityProbe, MinMeasureSnapVisibilityProbe, MaxMeasureSnapVisibilityProbe); set => Put("measure_snap_visibility_probe", System.Math.Clamp(value, MinMeasureSnapVisibilityProbe, MaxMeasureSnapVisibilityProbe)); }
    public static float MeasureSnapOcclusionToleranceFactor { get => GetFloatInRange("measure_snap_occlusion_tolerance_factor", DefaultMeasureSnapOcclusionToleranceFactor, MinMeasureSnapOcclusionToleranceFactor, MaxMeasureSnapOcclusionToleranceFactor); set => Put("measure_snap_occlusion_tolerance_factor", System.Math.Clamp(value, MinMeasureSnapOcclusionToleranceFactor, MaxMeasureSnapOcclusionToleranceFactor)); }
    public static float DimensionTextScale { get => GetFloatInRange("dimension_text_scale", DefaultDimensionTextScale, MinDimensionTextScale, MaxDimensionTextScale); set => Put("dimension_text_scale", System.Math.Clamp(value, MinDimensionTextScale, MaxDimensionTextScale)); }
    public static float MeasurementFaceSelectionR { get => Get("measurement_face_selection_r", 0.18f); set => Put("measurement_face_selection_r", Clamp01(value)); }
    public static float MeasurementFaceSelectionG { get => Get("measurement_face_selection_g", 0.83f); set => Put("measurement_face_selection_g", Clamp01(value)); }
    public static float MeasurementFaceSelectionB { get => Get("measurement_face_selection_b", 0.75f); set => Put("measurement_face_selection_b", Clamp01(value)); }
    public static void SetMeasurementFaceSelectionColor(float r, float g, float b) => PutRgb("measurement_face_selection_r", "measurement_face_selection_g", "measurement_face_selection_b", r, g, b);
    public static float MeasurementFaceHoverR { get => Get("measurement_face_hover_r", 1.0f); set => Put("measurement_face_hover_r", Clamp01(value)); }
    public static float MeasurementFaceHoverG { get => Get("measurement_face_hover_g", 0.92156863f); set => Put("measurement_face_hover_g", Clamp01(value)); }
    public static float MeasurementFaceHoverB { get => Get("measurement_face_hover_b", 0.16078432f); set => Put("measurement_face_hover_b", Clamp01(value)); }
    public static void SetMeasurementFaceHoverColor(float r, float g, float b) => PutRgb("measurement_face_hover_r", "measurement_face_hover_g", "measurement_face_hover_b", r, g, b);

    // Section tools
    public static bool SectionFillVisible { get => Get("section_fill_visible", false); set => Put("section_fill_visible", value); }
    public static bool SectionEdgesVisible { get => Get("section_edges_visible", true); set => Put("section_edges_visible", value); }
    public static bool SectionCurvesVisible { get => Get("section_curves_visible", true); set => Put("section_curves_visible", value); }
    public static bool SectionCapsVisible { get => Get("section_caps_visible", true); set => Put("section_caps_visible", value); }
    public static float SectionPlaneR { get => Get("section_plane_r", 0.19607843f); set => Put("section_plane_r", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionPlaneG { get => Get("section_plane_g", 0.9019608f); set => Put("section_plane_g", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionPlaneB { get => Get("section_plane_b", 0.19607843f); set => Put("section_plane_b", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static void SetSectionPlaneColor(float r, float g, float b) => PutRgb("section_plane_r", "section_plane_g", "section_plane_b", r, g, b);
    public static float SectionPlaneOpacity { get => GetFloatInRange("section_plane_opacity", 0.20f, 0.0f, 1.0f); set => Put("section_plane_opacity", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionEdgeR { get => Get("section_edge_r", 0.19607843f); set => Put("section_edge_r", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionEdgeG { get => Get("section_edge_g", 0.9019608f); set => Put("section_edge_g", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionEdgeB { get => Get("section_edge_b", 0.19607843f); set => Put("section_edge_b", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static void SetSectionEdgeColor(float r, float g, float b) => PutRgb("section_edge_r", "section_edge_g", "section_edge_b", r, g, b);
    public static float SectionEdgeWidth { get => GetFloatInRange("section_edge_width", DefaultSectionEdgeWidth, MinSectionEdgeWidth, MaxSectionEdgeWidth); set => Put("section_edge_width", System.Math.Clamp(value, MinSectionEdgeWidth, MaxSectionEdgeWidth)); }
    public static float SectionCapR { get => Get("section_cap_r", 0.85490197f); set => Put("section_cap_r", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionCapG { get => Get("section_cap_g", 0.85490197f); set => Put("section_cap_g", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static float SectionCapB { get => Get("section_cap_b", 0.2f); set => Put("section_cap_b", System.Math.Clamp(value, 0.0f, 1.0f)); }
    public static void SetSectionCapColor(float r, float g, float b) => PutRgb("section_cap_r", "section_cap_g", "section_cap_b", r, g, b);
    public static float SectionPlaneSizeFraction { get => GetFloatInRange("section_plane_size", 0.025f, 0.001f, 1.0f); set => Put("section_plane_size", System.Math.Clamp(value, 0.001f, 1.0f)); }
    public static float SectionGizmoSizeFraction
    {
        get => System.Math.Clamp(DefaultSectionGizmoSizeFraction * SectionGizmoScale, MinSectionGizmoSizeFraction, MaxSectionGizmoSizeFraction);
        set => SectionGizmoScale = value / LegacySectionGizmoSizeFractionBase;
    }

    // FA Cloud
    public static string CloudServerUrl
    {
        get => CloudServerConfig.ServerUrl;
        set => Edit(editor => editor.Remove("cloud_server_url"));
    }
    public static string CloudServerConfigPath => CloudServerConfig.ConfigPath;
    public static int CloudServerProfileSelectionIndex => CloudServerConfig.ProfileSelectionIndex;
    public static string CloudServerProfileDisplayName => CloudServerConfig.ProfileDisplayName;
    public static string CloudServerSelectedUrlKey => CloudServerConfig.SelectedUrlKey;
    public static void SetCloudServerProfileSelectionIndex(int index) => CloudServerConfig.SetProfileSelectionIndex(index);
    public static string CloudUserEmail { get => Get("cloud_user_email", DefaultCloudUserEmail); set => Put("cloud_user_email", value.Trim()); }
    public static string CloudDefaultProjectName { get => Get("cloud_default_project", DefaultCloudProjectName); set => Put("cloud_default_project", value.Trim()); }
    public static bool CloudRememberCredentials { get => Get("cloud_remember_credentials", false); set => Put("cloud_remember_credentials", value); }

    /// <summary>
    /// Populates <paramref name="appearance"/> with every persisted value in
    /// one call. MainActivity.ApplySettingsToScene uses this to refresh the
    /// renderer in response to PreferencesBottomSheet.OnSettingsChanged.
    /// Array fields are freshly allocated on each call; treat them as
    /// immutable after assignment so the GL thread never observes in-place
    /// mutations.
    /// UI-only settings, such as measurement colors and section visibility,
    /// are read by their owning Android services and intentionally stay out
    /// of this renderer snapshot.
    /// </summary>
    public static void Apply(ref SceneAppearance appearance)
    {
        int renderMode = EffectiveRenderMode(RenderMode, EdgesEnabled);
        var requestedMode = (FabricationAssistant.Rendering.Gles.RenderMode)renderMode;
        appearance.Mode = AndroidRenderModeShim.Effective(requestedMode);

        appearance.ShowGrid = ShowGrid;
        appearance.ShiftGridToModelMin = ShiftGridToModelMin;
        appearance.UseAutomaticGridSpacing = UseAutomaticGridSpacing;
        appearance.GridSpacingMm = GridSpacingMm;
        appearance.GridLineThickness = GridLineThickness;
        appearance.GridLineColor = new[] { GridLineColorR, GridLineColorG, GridLineColorB };
        appearance.ShowAxes = ShowAxes;
        appearance.IsPerspective = IsPerspective;
        appearance.LightweightNavigationEnabled = LightweightNavigationEnabled;

        appearance.BackgroundColor = new[] { BackgroundR, BackgroundG, BackgroundB };
        appearance.SurfaceColor = new[] { SurfaceR, SurfaceG, SurfaceB };
        appearance.SurfaceOpacity = SurfaceOpacity;

        appearance.EdgesEnabled = EdgesEnabled;
        appearance.EdgeColor = new[] { EdgeR, EdgeG, EdgeB };
        appearance.EdgeWidth = EdgeWidth;
        appearance.CadEdgeFeatureAngleDegrees = CadEdgeFeatureAngleDegrees;
        appearance.CadEdgeCoplanarToleranceDegrees = CadEdgeCoplanarToleranceDegrees;
        appearance.CadEdgeWeldToleranceScale = CadEdgeWeldToleranceScale;
        appearance.CadEdgeSilhouetteEnabled = CadEdgeSilhouetteEnabled;
        appearance.EdgeDepthBias = EdgeDepthBias;
        appearance.SurfaceOffsetFactor = SurfaceOffsetFactor;
        appearance.SurfaceOffsetUnits = SurfaceOffsetUnits;

        appearance.ClaySurfaceColor = new[] { ClaySurfaceR, ClaySurfaceG, ClaySurfaceB };
        appearance.ClayBackgroundColor = new[] { ClayBackgroundR, ClayBackgroundG, ClayBackgroundB };
        appearance.ClayFeatureEdgesEnabled = ClayFeatureEdgesEnabled;
        appearance.ClayFeatureEdgeColor = new[] { ClayFeatureEdgeR, ClayFeatureEdgeG, ClayFeatureEdgeB, ClayFeatureEdgeA };
        appearance.ClayFeatureEdgeWidth = ClayFeatureEdgeWidth;
        appearance.ClayFeatureEdgeDepthBias = ClayFeatureEdgeDepthBias;
        appearance.ClayFeatureEdgeCreaseAngleDegrees = ClayFeatureEdgeCreaseAngleDegrees;

        appearance.BaseColorLift = BaseColorLift;
        appearance.AmbientStrength = AmbientStrength;
        appearance.HeadlightStrength = HeadlightStrength;
        appearance.KeyLightStrength = KeyLightStrength;
        appearance.FillLightStrength = FillLightStrength;
        appearance.BounceLightStrength = BounceLightStrength;
        appearance.HemisphereStrength = HemisphereStrength;
        appearance.SpecularStrength = SpecularStrength;
        appearance.SpecularPower = SpecularPower;

        appearance.AmbientOcclusionEnabled = AmbientOcclusionEnabled;
        appearance.AoSampleCount = AoSampleCount;
        appearance.AoRadius = AoRadius;
        appearance.AoBias = AoBias;
        appearance.AoIntensity = AoIntensity;
        appearance.AoPower = AoPower;
        appearance.AoContrast = AoContrast;
        appearance.AoMaxDistance = AoMaxDistance;
        appearance.AoFadeStart = AoFadeStart;
        appearance.AoFadeEnd = AoFadeEnd;
        appearance.AoBlurEnabled = AoBlurEnabled;
        appearance.AoBlurRadius = AoBlurRadius;
        appearance.AoBlurSharpness = AoBlurSharpness;
        appearance.AoBlurPasses = AoBlurPasses;
        appearance.ContourStrength = ContourStrength;
        appearance.ContourPower = ContourPower;
        appearance.MsaaSamples = MsaaSamples;

        appearance.OutlineEnabled = OutlineEnabled;
        appearance.OutlineColor = new[] { OutlineR, OutlineG, OutlineB };
        appearance.OutlineThicknessPx = OutlineThicknessPx;
        appearance.HoverOutlineColor = new[] { HoverOutlineR, HoverOutlineG, HoverOutlineB };
        appearance.HoverOutlineThicknessPx = HoverOutlineThicknessPx;
        appearance.HoverTintStrength = HoverTintStrength;
    }

    private static float Get(string k, float def) => Prefs.GetFloat(k, def);
    private static bool Get(string k, bool def) => Prefs.GetBoolean(k, def);
    private static int Get(string k, int def) => Prefs.GetInt(k, def);
    private static string Get(string k, string def) => Prefs.GetString(k, def) ?? def;
    public static void Edit(Action<ISharedPreferencesEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var editor = Prefs.Edit()!;
        edit(editor);
        editor.Apply();
    }

    private static void Put(string k, float v) => Edit(ed => ed.PutFloat(k, float.IsFinite(v) ? v : 0.0f));
    private static void Put(string k, bool v) => Edit(ed => ed.PutBoolean(k, v));
    private static void Put(string k, int v) => Edit(ed => ed.PutInt(k, v));
    private static void Put(string k, string v) => Edit(ed => ed.PutString(k, v ?? ""));

    private static float ClampCameraClipNearMm(float value)
        => float.IsFinite(value)
            ? System.Math.Clamp(value, MinCameraClipMm, MaxCameraNearClipMm)
            : DefaultCameraNearClipMm;

    private static float ClampCameraClipFarMm(float value)
        => float.IsFinite(value)
            ? System.Math.Clamp(value, MinCameraClipMm, MaxCameraFarClipMm)
            : DefaultCameraFarClipMm;

    private static float ResolveFarClipAboveNear(float near)
    {
        float margin = System.Math.Max(near * 0.01f, 1.0f);
        return System.Math.Clamp(near + margin, MinCameraClipMm, MaxCameraFarClipMm);
    }

    private static void PutRgb(string rKey, string gKey, string bKey, float r, float g, float b)
    {
        Edit(editor =>
        {
            editor.PutFloat(rKey, Clamp01(r));
            editor.PutFloat(gKey, Clamp01(g));
            editor.PutFloat(bKey, Clamp01(b));
        });
    }

    private static int EffectiveRenderMode(int renderMode, bool edgesEnabled)
        => IsShadedRenderMode(renderMode)
            ? edgesEnabled ? ModeShadedWithEdges : ModeShaded
            : renderMode;

    private static bool IsShadedRenderMode(int renderMode)
        => renderMode == ModeShadedWithEdges || renderMode == ModeShaded;

    private static void MigrateDefaultsIfNeeded()
    {
        int previousSchema = Get("settings_schema_version", 1);
        if (previousSchema >= SettingsSchemaVersion)
        {
            return;
        }

        var editor = Prefs.Edit()!;

        if (previousSchema < 9)
            ResetAmbientOcclusionTuning(editor);

        if (previousSchema < 13)
            DoubleSectionGizmoSize(editor);

        if (previousSchema < 14)
            ConvertSectionGizmoSizeToScale(editor);

        if (previousSchema < 16)
            editor.Remove("ao_noise_scale"); // setting removed: noise texture replaced with IGN in the SSAO shader.

        if (previousSchema < 21)
            editor.Remove("cloud_server_url");

        RemoveLegacyDefaultValues(editor);
        RemoveFloatIfBelow(editor, "edge_width", MinimumVisibleEdgeWidth);
        RemoveOutOfRangeValues(editor);

        editor.PutInt("settings_schema_version", SettingsSchemaVersion);
        editor.Apply();
    }

    private static void RemoveLegacyDefaultValues(ISharedPreferencesEditor editor)
    {
        foreach ((string key, float expected) in LegacyFloatDefaultsToRemove)
            RemoveFloatIfApproximately(editor, key, expected);

        foreach ((string key, int expected) in LegacyIntDefaultsToRemove)
            RemoveIntIfEquals(editor, key, expected);
    }

    private static void RemoveOutOfRangeValues(ISharedPreferencesEditor editor)
    {
        foreach ((string key, float min, float max) in FloatRangeGuards)
            RemoveFloatIfOutOfRange(editor, key, min, max);

        foreach ((string key, int min, int max) in IntRangeGuards)
            RemoveIntIfOutOfRange(editor, key, min, max);
    }

    private static bool Approximately(float left, float right)
    {
        return System.Math.Abs(left - right) <= 0.000001f;
    }

    private static float GetFloatInRange(string key, float defaultValue, float min, float max)
    {
        float value = Get(key, defaultValue);
        return float.IsFinite(value) && value >= min && value <= max ? value : defaultValue;
    }

    private static int GetIntInRange(string key, int defaultValue, int min, int max)
    {
        int value = Get(key, defaultValue);
        return value >= min && value <= max ? value : defaultValue;
    }

    private static void RemoveFloatIfOutOfRange(ISharedPreferencesEditor editor, string key, float min, float max)
    {
        if (!Prefs.Contains(key))
            return;

        float value = Get(key, float.NaN);
        if (!float.IsFinite(value) || value < min || value > max)
            editor.Remove(key);
    }

    private static void RemoveFloatIfApproximately(ISharedPreferencesEditor editor, string key, float expected)
    {
        if (!Prefs.Contains(key))
            return;

        float value = Get(key, float.NaN);
        if (float.IsFinite(value) && Approximately(value, expected))
            editor.Remove(key);
    }

    private static void RemoveFloatIfBelow(ISharedPreferencesEditor editor, string key, float minimum)
    {
        if (!Prefs.Contains(key))
            return;

        float value = Get(key, float.NaN);
        if (float.IsFinite(value) && value < minimum)
            editor.Remove(key);
    }

    private static void RemoveIntIfOutOfRange(ISharedPreferencesEditor editor, string key, int min, int max)
    {
        if (!Prefs.Contains(key))
            return;

        int value = Get(key, int.MinValue);
        if (value < min || value > max)
            editor.Remove(key);
    }

    private static void RemoveIntIfEquals(ISharedPreferencesEditor editor, string key, int expected)
    {
        if (Prefs.Contains(key) && Get(key, int.MinValue) == expected)
            editor.Remove(key);
    }

    private static void ResetAmbientOcclusionTuning(ISharedPreferencesEditor editor)
    {
        editor.Remove("ao_sample_count");
        editor.Remove("ao_radius");
        editor.Remove("ao_bias");
        editor.Remove("ao_intensity");
        editor.Remove("ao_power");
        editor.Remove("ao_contrast");
        editor.Remove("ao_max_distance");
        editor.Remove("ao_fade_start");
        editor.Remove("ao_fade_end");
        editor.Remove("ao_blur_radius");
        editor.Remove("ao_blur_sharpness");
        editor.Remove("ao_blur_passes");
    }

    private static void DoubleSectionGizmoSize(ISharedPreferencesEditor editor)
    {
        if (!Prefs.Contains("section_gizmo_size") || Prefs.Contains("section_gizmo_scale"))
            return;

        float value = Get("section_gizmo_size", LegacySectionGizmoSizeFractionBase);
        if (!float.IsFinite(value))
        {
            editor.Remove("section_gizmo_size");
            return;
        }

        editor.PutFloat(
            "section_gizmo_size",
            System.Math.Clamp(value * 2.0f, MinSectionGizmoSizeFraction, MaxSectionGizmoSizeFraction));
    }

    private static void ConvertSectionGizmoSizeToScale(ISharedPreferencesEditor editor)
    {
        if (!Prefs.Contains("section_gizmo_size") || Prefs.Contains("section_gizmo_scale"))
            return;

        float value = Get("section_gizmo_size", LegacySectionGizmoSizeFractionBase);
        editor.Remove("section_gizmo_size");
        if (!float.IsFinite(value))
            return;

        float scale = value / LegacySectionGizmoSizeFractionBase;
        editor.PutFloat("section_gizmo_scale", System.Math.Clamp(scale, MinSectionGizmoScale, MaxSectionGizmoScale));
    }

    private static float Clamp01(float value)
        => AppSettingsValueGuards.Clamp01(value);

    private static int ClampAndroidMsaaSamples(int value)
        => AppSettingsValueGuards.ClampAndroidMsaaSamples(value);
}
