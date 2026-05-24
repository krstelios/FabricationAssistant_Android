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
    private const int SettingsSchemaVersion = 2;

    private static ISharedPreferences? _prefs;

    public static void Initialize(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _prefs ??= context.GetSharedPreferences(FileName, FileCreationMode.Private);
        MigrateDefaultsIfNeeded();
    }

    private static ISharedPreferences Prefs =>
        _prefs ?? throw new InvalidOperationException("AppSettings.Initialize(context) must be called first.");

    // ── Navigation (existing) ──────────────────────────────────────────
    public static float OrbitSensitivity { get => Get("orbit_sensitivity", 1.0f); set => Put("orbit_sensitivity", System.Math.Clamp(value, 0.1f, 5.0f)); }
    public static float PanSensitivity { get => Get("pan_sensitivity", 1.0f); set => Put("pan_sensitivity", System.Math.Clamp(value, 0.1f, 5.0f)); }
    public static float ZoomSensitivity { get => Get("zoom_sensitivity", 1.0f); set => Put("zoom_sensitivity", System.Math.Clamp(value, 0.1f, 5.0f)); }

    // ── Mode ───────────────────────────────────────────────────────────
    public static int RenderMode { get => Get("render_mode", 0); set => Put("render_mode", System.Math.Clamp(value, 0, 3)); }

    // ── Camera & helpers ───────────────────────────────────────────────
    public static bool ShowGrid { get => Get("show_grid", true); set => Put("show_grid", value); }
    public static bool ShiftGridToModelMin { get => Get("shift_grid", true); set => Put("shift_grid", value); }
    public static bool UseAutomaticGridSpacing { get => Get("auto_grid_spacing", true); set => Put("auto_grid_spacing", value); }
    public static float GridSpacingMm { get => Get("grid_spacing_mm", 10.0f); set => Put("grid_spacing_mm", value); }
    public static float GridLineThickness { get => Get("grid_thickness", 1.0f); set => Put("grid_thickness", value); }
    public static float GridLineColorR { get => Get("grid_r", 0.28f); set => Put("grid_r", value); }
    public static float GridLineColorG { get => Get("grid_g", 0.30f); set => Put("grid_g", value); }
    public static float GridLineColorB { get => Get("grid_b", 0.33f); set => Put("grid_b", value); }
    public static bool ShowAxes { get => Get("show_axes", true); set => Put("show_axes", value); }
    public static bool ShowViewCube { get => Get("show_viewcube", true); set => Put("show_viewcube", value); }
    public static bool IsPerspective { get => Get("is_perspective", true); set => Put("is_perspective", value); }

    // ── Background + surface ───────────────────────────────────────────
    public static float BackgroundR { get => Get("bg_r", 0.10f); set => Put("bg_r", value); }
    public static float BackgroundG { get => Get("bg_g", 0.11f); set => Put("bg_g", value); }
    public static float BackgroundB { get => Get("bg_b", 0.12f); set => Put("bg_b", value); }
    public static float SurfaceR { get => Get("surface_r", 0.78f); set => Put("surface_r", value); }
    public static float SurfaceG { get => Get("surface_g", 0.80f); set => Put("surface_g", value); }
    public static float SurfaceB { get => Get("surface_b", 0.82f); set => Put("surface_b", value); }
    public static float SurfaceOpacity { get => Get("surface_opacity", 1.0f); set => Put("surface_opacity", value); }

    // ── CAD Edges ──────────────────────────────────────────────────────
    public static bool EdgesEnabled { get => Get("edges_enabled", true); set => Put("edges_enabled", value); }
    public static float EdgeR { get => Get("edge_r", 0.05f); set => Put("edge_r", value); }
    public static float EdgeG { get => Get("edge_g", 0.05f); set => Put("edge_g", value); }
    public static float EdgeB { get => Get("edge_b", 0.06f); set => Put("edge_b", value); }
    public static float EdgeWidth { get => Get("edge_width", 1.0f); set => Put("edge_width", value); }
    public static float CadEdgeFeatureAngleDegrees { get => Get("edge_feature_angle", 28.0f); set => Put("edge_feature_angle", value); }
    public static float CadEdgeCoplanarToleranceDegrees { get => Get("edge_coplanar_tol", 5.0f); set => Put("edge_coplanar_tol", value); }
    public static float CadEdgeWeldToleranceScale { get => Get("edge_weld_tol", 1.0e-5f); set => Put("edge_weld_tol", value); }
    public static bool CadEdgeSilhouetteEnabled { get => Get("edge_silhouette", true); set => Put("edge_silhouette", value); }
    public static float EdgeDepthBias { get => Get("edge_depth_bias", 0.0f); set => Put("edge_depth_bias", value); }
    public static float SurfaceOffsetFactor { get => Get("surface_offset_f", 1.0f); set => Put("surface_offset_f", value); }
    public static float SurfaceOffsetUnits { get => Get("surface_offset_u", 1.0f); set => Put("surface_offset_u", value); }

    // ── Clay ───────────────────────────────────────────────────────────
    public static float ClaySurfaceR { get => Get("clay_surface_r", 0.85f); set => Put("clay_surface_r", value); }
    public static float ClaySurfaceG { get => Get("clay_surface_g", 0.78f); set => Put("clay_surface_g", value); }
    public static float ClaySurfaceB { get => Get("clay_surface_b", 0.65f); set => Put("clay_surface_b", value); }
    public static float ClayBackgroundR { get => Get("clay_bg_r", 0.14f); set => Put("clay_bg_r", value); }
    public static float ClayBackgroundG { get => Get("clay_bg_g", 0.14f); set => Put("clay_bg_g", value); }
    public static float ClayBackgroundB { get => Get("clay_bg_b", 0.16f); set => Put("clay_bg_b", value); }

    // ── Lighting ───────────────────────────────────────────────────────
    public static float BaseColorLift { get => Get("light_base_lift", 0.04f); set => Put("light_base_lift", value); }
    public static float AmbientStrength { get => Get("light_ambient", 0.40f); set => Put("light_ambient", value); }
    public static float HeadlightStrength { get => Get("light_headlight", 0.30f); set => Put("light_headlight", value); }
    public static float KeyLightStrength { get => Get("light_key", 0.55f); set => Put("light_key", value); }
    public static float FillLightStrength { get => Get("light_fill", 0.20f); set => Put("light_fill", value); }
    public static float BounceLightStrength { get => Get("light_bounce", 0.15f); set => Put("light_bounce", value); }
    public static float HemisphereStrength { get => Get("light_hemisphere", 0.40f); set => Put("light_hemisphere", value); }
    public static float SpecularStrength { get => Get("light_spec_strength", 0.10f); set => Put("light_spec_strength", value); }
    public static float SpecularPower { get => Get("light_spec_power", 32.0f); set => Put("light_spec_power", value); }

    // ── AO + contour + MSAA ───────────────────────────────────────────
    public static bool AmbientOcclusionEnabled { get => Get("ao_enabled", true); set => Put("ao_enabled", value); }
    public static float AoRadius { get => Get("ao_radius", 0.50f); set => Put("ao_radius", value); }
    public static float AoBias { get => Get("ao_bias", 0.025f); set => Put("ao_bias", value); }
    public static float AoIntensity { get => Get("ao_intensity", 1.0f); set => Put("ao_intensity", value); }
    public static int AoBlurPasses { get => Get("ao_blur_passes", 2); set => Put("ao_blur_passes", System.Math.Clamp(value, 0, 8)); }
    public static float ContourStrength { get => Get("contour_strength", 0.50f); set => Put("contour_strength", value); }
    public static float ContourPower { get => Get("contour_power", 2.0f); set => Put("contour_power", value); }
    public static int MsaaSamples { get => Get("msaa_samples", 4); set => Put("msaa_samples", value); }

    // ── Selection ──────────────────────────────────────────────────────
    public static bool ShowSelectionHighlight { get => Get("show_selection", true); set => Put("show_selection", value); }
    public static bool OutlineEnabled { get => Get("outline_enabled", true); set => Put("outline_enabled", value); }
    public static float OutlineR { get => Get("outline_r", 1.0f); set => Put("outline_r", value); }
    public static float OutlineG { get => Get("outline_g", 0.62f); set => Put("outline_g", value); }
    public static float OutlineB { get => Get("outline_b", 0.20f); set => Put("outline_b", value); }
    public static float OutlineThicknessPx { get => Get("outline_thickness", 2.5f); set => Put("outline_thickness", value); }

    /// <summary>
    /// Populates <paramref name="appearance"/> with every persisted value in
    /// one call. MainActivity.ApplySettingsToScene uses this to refresh the
    /// renderer in response to PreferencesBottomSheet.OnSettingsChanged.
    /// </summary>
    public static void Apply(ref SceneAppearance appearance)
    {
        appearance.Mode = (RenderMode)RenderMode;

        appearance.ShowGrid = ShowGrid;
        appearance.ShiftGridToModelMin = ShiftGridToModelMin;
        appearance.UseAutomaticGridSpacing = UseAutomaticGridSpacing;
        appearance.GridSpacingMm = GridSpacingMm;
        appearance.GridLineThickness = GridLineThickness;
        appearance.GridLineColor = new[] { GridLineColorR, GridLineColorG, GridLineColorB };
        appearance.ShowAxes = ShowAxes;
        appearance.ShowViewCube = ShowViewCube;
        appearance.IsPerspective = IsPerspective;

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
        appearance.AoRadius = AoRadius;
        appearance.AoBias = AoBias;
        appearance.AoIntensity = AoIntensity;
        appearance.AoBlurPasses = AoBlurPasses;
        appearance.ContourStrength = ContourStrength;
        appearance.ContourPower = ContourPower;
        appearance.MsaaSamples = MsaaSamples;

        appearance.OutlineEnabled = OutlineEnabled;
        appearance.OutlineColor = new[] { OutlineR, OutlineG, OutlineB };
        appearance.OutlineThicknessPx = OutlineThicknessPx;
    }

    private static float Get(string k, float def) => Prefs.GetFloat(k, def);
    private static bool Get(string k, bool def) => Prefs.GetBoolean(k, def);
    private static int Get(string k, int def) => Prefs.GetInt(k, def);
    private static void Put(string k, float v) { var ed = Prefs.Edit()!; ed.PutFloat(k, v); ed.Apply(); }
    private static void Put(string k, bool v) { var ed = Prefs.Edit()!; ed.PutBoolean(k, v); ed.Apply(); }
    private static void Put(string k, int v) { var ed = Prefs.Edit()!; ed.PutInt(k, v); ed.Apply(); }

    private static void MigrateDefaultsIfNeeded()
    {
        if (Get("settings_schema_version", 1) >= SettingsSchemaVersion)
        {
            return;
        }

        var editor = Prefs.Edit()!;

        if (Prefs.Contains("edge_feature_angle") && Approximately(Get("edge_feature_angle", 25.0f), 25.0f))
        {
            editor.Remove("edge_feature_angle");
        }

        if (Prefs.Contains("edge_coplanar_tol") && Approximately(Get("edge_coplanar_tol", 1.5f), 1.5f))
        {
            editor.Remove("edge_coplanar_tol");
        }

        if (Prefs.Contains("edge_depth_bias") && Approximately(Get("edge_depth_bias", 0.00005f), 0.00005f))
        {
            editor.Remove("edge_depth_bias");
        }

        editor.PutInt("settings_schema_version", SettingsSchemaVersion);
        editor.Apply();
    }

    private static bool Approximately(float left, float right)
    {
        return System.Math.Abs(left - right) <= 0.000001f;
    }
}
