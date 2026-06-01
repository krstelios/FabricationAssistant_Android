using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.MaterialSwitch;
using System.Globalization;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Settings popup launched from the nav-rail gear icon. Built programmatically
/// (rather than via XML) because the surface is large and repetitive: nine
/// sections with a mix of toggles, sensitivity SeekBars, and color
/// pickers. Every control writes to AppSettings + invokes
/// <see cref="OnSettingsChanged"/> so the host re-applies live without
/// waiting for the sheet to close.
/// </summary>
public sealed class PreferencesBottomSheet : BottomSheetDialogFragment, IDisposable
{
    private const float TabletBreakpointDp = 700f;
    private const float TabletPanelWidthDp = 520f;
    private const float CompactPanelWidthDp = 420f;
    private int _sheetWidthOverridePx;
    private FrameLayout? _resizeHandle;
    private readonly List<AlertDialog> _colorPickerDialogs = new();
    private readonly Handler _logRefreshHandler = new(Looper.MainLooper!);
    private AndroidLogcatFeed? _logFeed;
    private ScrollView? _logScroll;
    private HorizontalScrollView? _logHorizontalScroll;
    private TextView? _logStatus;
    private TextView? _logText;
    private MaterialButton? _logLiveButton;
    private bool _logPanelExpanded;
    private bool _logRefreshQueued;
    private bool _disposed;

    public Action? OnSettingsChanged { get; set; }

    public override global::Android.App.Dialog OnCreateDialog(Bundle? savedInstanceState)
    {
        var dialog = (BottomSheetDialog)base.OnCreateDialog(savedInstanceState);
        dialog.Behavior.PeekHeight = ResolvePeekHeightPx();
        dialog.Behavior.FitToContents = false;
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        dialog.Behavior.Hideable = false;
        dialog.Behavior.SkipCollapsed = true;
        dialog.Behavior.Draggable = false;
        return dialog;
    }

    private int ResolvePeekHeightPx()
    {
        DisplayMetrics? metrics = Resources?.DisplayMetrics;
        float density = metrics?.Density ?? 1.0f;
        int displayHeight = metrics?.HeightPixels ?? (int)(640f * density);
        int minViewportReserve = (int)(96f * density);
        int minPeek = (int)(280f * density);
        int maxPeek = System.Math.Max(minPeek, displayHeight - minViewportReserve);
        int desiredPeek = (int)(displayHeight * 0.72f);
        return System.Math.Clamp(desiredPeek, minPeek, maxPeek);
    }

    public override void OnStart()
    {
        base.OnStart();
        RefreshVisibleSettingsView();
        ApplySheetLayout();
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
        => CreateEmbeddedView(Context!);

    public override void OnDestroy()
    {
        DisposeManagedContent();
        base.OnDestroy();
    }

    public new void Dispose()
    {
        DisposeManagedContent();
        base.Dispose();
    }

    private void DisposeManagedContent()
    {
        if (_disposed)
            return;

        _disposed = true;
        DismissColorPickerDialogs();
        DisposeLogFeed();
        OnSettingsChanged = null;
    }

    public View CreateEmbeddedView(Context ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppSettings.Initialize(ctx);
        int pad = Dp(ctx, 16);

        var scroll = new NestedScrollView(ctx)
        {
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
            FillViewport = true,
        };
        scroll.SetBackgroundResource(Resource.Color.fa_app_background);
        scroll.SetPadding(pad, pad, pad, pad);

        var root = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent),
        };

        AddHeader(ctx, root, "Settings", Dp(ctx, 22), () =>
        {
            AppSettings.ResetToDefaults();
            NotifySettingsChanged();
            Toast.MakeText(ctx, "Settings reset", ToastLength.Short)?.Show();
            ReplaceVisibleSettingsView(ctx, scroll);
        });

        // Render mode
        var modeSection = AddSection(ctx, root, "Render Mode", "Shading style", expandedByDefault: true);
        AddToggleRow(ctx, modeSection, new[] { "Shaded", "Wireframe", "Clay" },
            AppSettings.RenderModeSelectionIndex, AppSettings.SetRenderModeSelectionIndex);

        // Camera and helpers
        var helpers = AddSection(ctx, root, "Camera & Helpers", "Grid, helpers, projection");
        AddSwitch(ctx, helpers, "Show ground grid", AppSettings.ShowGrid, v => AppSettings.ShowGrid = v);
        AddSwitch(ctx, helpers, "Double tap Fit Screen", AppSettings.DoubleTapFitScreenEnabled, v => AppSettings.DoubleTapFitScreenEnabled = v);
        AddSwitch(ctx, helpers, "Push grid to model min", AppSettings.ShiftGridToModelMin, v => AppSettings.ShiftGridToModelMin = v);
        var autoGridSwitch = AddSwitch(ctx, helpers, "Automatic grid spacing", AppSettings.UseAutomaticGridSpacing, v => AppSettings.UseAutomaticGridSpacing = v);
        // S10-F2: span the full grid-spacing clamp (0.001..1e6) on a log scale so a
        // persisted value above the old 1000 max is no longer pinned/truncated.
        AddFloatSlider(ctx, helpers, "Grid spacing (mm)", 0.001f, 1_000_000f, AppSettings.GridSpacingMm, value =>
        {
            AppSettings.SetManualGridSpacing(value);
            // S10-1: dragging spacing switches to manual mode (SetManualGridSpacing
            // already persisted auto_grid_spacing = false); reflect that in the switch.
            if (autoGridSwitch.Checked)
                autoGridSwitch.Checked = false;
        }, logarithmic: true);
        AddFloatSlider(ctx, helpers, "Grid line thickness", 1f, 8f, AppSettings.GridLineThickness, v => AppSettings.GridLineThickness = v);
        AddRgbRow(ctx, helpers, "Grid color",
            AppSettings.GridLineColorR, AppSettings.GridLineColorG, AppSettings.GridLineColorB,
            AppSettings.SetGridLineColor);
        AddSwitch(ctx, helpers, "Show axes gizmo", AppSettings.ShowAxes, v => AppSettings.ShowAxes = v);
        AddToggleRow(ctx, helpers, new[] { "Perspective", "Orthographic" },
            AppSettings.IsPerspective ? 0 : 1, idx => AppSettings.IsPerspective = (idx == 0));
        AddSwitch(ctx, helpers, "Manual clip planes", AppSettings.ManualCameraClipPlanesEnabled, v => AppSettings.ManualCameraClipPlanesEnabled = v);
        AddFloatField(ctx, helpers, "Near clip (mm)", AppSettings.CameraNearClipMm, AppSettings.SetCameraNearClipMm);
        AddFloatField(ctx, helpers, "Far clip (mm)", AppSettings.CameraFarClipMm, AppSettings.SetCameraFarClipMm);

        // Scene colors
        var colors = AddSection(ctx, root, "Scene Colors", "Background and surface");
        AddRgbRow(ctx, colors, "Background",
            AppSettings.BackgroundR, AppSettings.BackgroundG, AppSettings.BackgroundB,
            AppSettings.SetBackgroundColor);
        AddRgbRow(ctx, colors, "Surface",
            AppSettings.SurfaceR, AppSettings.SurfaceG, AppSettings.SurfaceB,
            AppSettings.SetSurfaceColor);
        AddFloatSlider(ctx, colors, "Surface opacity", 0f, 1f, AppSettings.SurfaceOpacity, v => AppSettings.SurfaceOpacity = v);

        // CAD edges
        var edges = AddSection(ctx, root, "CAD Edges", "Edge lines and tolerances");
        FloatSliderControl? edgeWidthSlider = null;
        AddSwitch(ctx, edges, "Enable edges", AppSettings.EdgesEnabled, enabled =>
        {
            AppSettings.SetEdgesEnabledFromUi(enabled);
            // S10-F4: re-enabling edges can bump a sub-visible width up to the
            // default, so re-sync the slider with the value actually persisted.
            edgeWidthSlider?.SetValue(AppSettings.EdgeWidth);
        });
        AddRgbRow(ctx, edges, "Edge color",
            AppSettings.EdgeR, AppSettings.EdgeG, AppSettings.EdgeB,
            AppSettings.SetEdgeColor);
        // S10-F7: min matches the durable visible floor (MinimumVisibleEdgeWidth);
        // values below it are bumped on re-enable and stripped by migration.
        edgeWidthSlider = AddFloatSlider(ctx, edges, "Normal edge thickness", 0.75f, 4f, AppSettings.EdgeWidth, v => AppSettings.EdgeWidth = v);
        AddFloatSlider(ctx, edges, "Feature angle (deg)", 1f, 150f, AppSettings.CadEdgeFeatureAngleDegrees, v => AppSettings.CadEdgeFeatureAngleDegrees = v);
        AddFloatSlider(ctx, edges, "Coplanar tolerance (deg)", 0f, 30f, AppSettings.CadEdgeCoplanarToleranceDegrees, v => AppSettings.CadEdgeCoplanarToleranceDegrees = v);
        // S10-F8: weld tolerance spans two decades (1e-6..1e-4); a log scale makes
        // the low end tunable instead of compressing it into ~9% of the track.
        AddFloatSlider(ctx, edges, "Weld tolerance", 1e-6f, 1e-4f, AppSettings.CadEdgeWeldToleranceScale, v => AppSettings.CadEdgeWeldToleranceScale = v, logarithmic: true);
        AddSwitch(ctx, edges, "Silhouettes", AppSettings.CadEdgeSilhouetteEnabled, v => AppSettings.CadEdgeSilhouetteEnabled = v);
        AddFloatSlider(ctx, edges, "Depth bias", 0f, 0.002f, AppSettings.EdgeDepthBias, v => AppSettings.EdgeDepthBias = v);
        AddFloatSlider(ctx, edges, "Surface offset F", 0f, 4f, AppSettings.SurfaceOffsetFactor, v => AppSettings.SurfaceOffsetFactor = v);
        AddFloatSlider(ctx, edges, "Surface offset U", 0f, 4f, AppSettings.SurfaceOffsetUnits, v => AppSettings.SurfaceOffsetUnits = v);

        // Clay
        var clay = AddSection(ctx, root, "Clay Render", "Clay colors");
        AddRgbRow(ctx, clay, "Clay surface",
            AppSettings.ClaySurfaceR, AppSettings.ClaySurfaceG, AppSettings.ClaySurfaceB,
            AppSettings.SetClaySurfaceColor);
        AddRgbRow(ctx, clay, "Clay background",
            AppSettings.ClayBackgroundR, AppSettings.ClayBackgroundG, AppSettings.ClayBackgroundB,
            AppSettings.SetClayBackgroundColor);
        AddSwitch(ctx, clay, "Clay feature edges", AppSettings.ClayFeatureEdgesEnabled, v => AppSettings.ClayFeatureEdgesEnabled = v);
        AddRgbRow(ctx, clay, "Clay edge",
            AppSettings.ClayFeatureEdgeR, AppSettings.ClayFeatureEdgeG, AppSettings.ClayFeatureEdgeB,
            AppSettings.SetClayFeatureEdgeColor);
        AddFloatSlider(ctx, clay, "Clay edge alpha", 0f, 1f, AppSettings.ClayFeatureEdgeA, v => AppSettings.ClayFeatureEdgeA = v);
        AddFloatSlider(ctx, clay, "Clay edge width", 0.05f, 4f, AppSettings.ClayFeatureEdgeWidth, v => AppSettings.ClayFeatureEdgeWidth = v);
        AddFloatSlider(ctx, clay, "Clay edge depth bias", 0f, 0.01f, AppSettings.ClayFeatureEdgeDepthBias, v => AppSettings.ClayFeatureEdgeDepthBias = v);
        AddFloatSlider(ctx, clay, "Clay crease angle", 1f, 150f, AppSettings.ClayFeatureEdgeCreaseAngleDegrees, v => AppSettings.ClayFeatureEdgeCreaseAngleDegrees = v);

        // Lighting
        var lighting = AddSection(ctx, root, "Lighting", "Light balance and specular");
        AddFloatSlider(ctx, lighting, "Base lift", 0f, 0.25f, AppSettings.BaseColorLift, v => AppSettings.BaseColorLift = v);
        AddFloatSlider(ctx, lighting, "Ambient", 0f, 1f, AppSettings.AmbientStrength, v => AppSettings.AmbientStrength = v);
        AddFloatSlider(ctx, lighting, "Headlight", 0f, 1f, AppSettings.HeadlightStrength, v => AppSettings.HeadlightStrength = v);
        AddFloatSlider(ctx, lighting, "Key", 0f, 1f, AppSettings.KeyLightStrength, v => AppSettings.KeyLightStrength = v);
        AddFloatSlider(ctx, lighting, "Fill", 0f, 1f, AppSettings.FillLightStrength, v => AppSettings.FillLightStrength = v);
        AddFloatSlider(ctx, lighting, "Bounce", 0f, 1f, AppSettings.BounceLightStrength, v => AppSettings.BounceLightStrength = v);
        AddFloatSlider(ctx, lighting, "Hemisphere", 0f, 1f, AppSettings.HemisphereStrength, v => AppSettings.HemisphereStrength = v);
        AddFloatSlider(ctx, lighting, "Specular strength", 0f, 1f, AppSettings.SpecularStrength, v => AppSettings.SpecularStrength = v);
        AddFloatSlider(ctx, lighting, "Specular power", 1f, 128f, AppSettings.SpecularPower, v => AppSettings.SpecularPower = v);

        // AA and occlusion
        var aa = AddSection(ctx, root, "Anti-aliasing & Occlusion", "MSAA, contour, SSAO");
        // 8x dropped: current target GPUs cap at GL_MAX_SAMPLES=4, so the 8x option
        // only ever clamped back down to 4x and was misleading.
        int msaaIdx = AppSettings.MsaaSamples switch { 0 => 0, 2 => 1, _ => 2 };
        AddToggleRow(ctx, aa, new[] { "Off", "2x", "4x" }, msaaIdx,
            idx => AppSettings.MsaaSamples = idx switch { 0 => 0, 1 => 2, _ => 4 });
        AddSubtle(ctx, aa, "MSAA applies immediately.");
        AddSwitch(ctx, aa, "High-res ambient occlusion", AppSettings.AoFullResolution, v => AppSettings.AoFullResolution = v);
        AddSubtle(ctx, aa, "Renders SSAO at full resolution - sharper shadows, more GPU.");
        AddSwitch(ctx, aa, "Post-process AA (FXAA)", AppSettings.FxaaEnabled, v => AppSettings.FxaaEnabled = v);
        AddSubtle(ctx, aa, "Smooths all edges in the final image, including outlines.");
        AddSwitch(ctx, aa, "Supersampling (1.5x)", AppSettings.RenderScalePercent >= 150, v => AppSettings.RenderScalePercent = v ? 150 : 100);
        AddSubtle(ctx, aa, "Renders the still view at 1.5x and downsamples - sharpest edges, highest GPU. Native res while you move the camera.");
        AddFloatSlider(ctx, aa, "Contour strength", 0f, 1.2f, AppSettings.ContourStrength, v => AppSettings.ContourStrength = v);
        AddFloatSlider(ctx, aa, "Contour falloff", 0.5f, 6f, AppSettings.ContourPower, v => AppSettings.ContourPower = v);
        AddSwitch(ctx, aa, "Contact shadows (SSAO)", AppSettings.AmbientOcclusionEnabled, v => AppSettings.AmbientOcclusionEnabled = v);
        AddIntSlider(ctx, aa, "AO samples", 1, 96, AppSettings.AoSampleCount, v => AppSettings.AoSampleCount = v);
        AddFloatSlider(ctx, aa, "AO radius", 0.001f, 0.08f, AppSettings.AoRadius, v => AppSettings.AoRadius = v);
        AddFloatSlider(ctx, aa, "AO bias", 0.0f, 0.01f, AppSettings.AoBias, v => AppSettings.AoBias = v);
        AddFloatSlider(ctx, aa, "AO intensity", 0f, 4f, AppSettings.AoIntensity, v => AppSettings.AoIntensity = v);
        AddFloatSlider(ctx, aa, "AO power", 0.25f, 4f, AppSettings.AoPower, v => AppSettings.AoPower = v);
        AddFloatSlider(ctx, aa, "AO contrast", 0f, 4f, AppSettings.AoContrast, v => AppSettings.AoContrast = v);
        AddFloatSlider(ctx, aa, "AO max distance", 0.05f, 2f, AppSettings.AoMaxDistance, v => AppSettings.AoMaxDistance = v);
        AddFloatSlider(ctx, aa, "AO fade start", 0f, 2f, AppSettings.AoFadeStart, v => AppSettings.AoFadeStart = v);
        AddFloatSlider(ctx, aa, "AO fade end", 0f, 2f, AppSettings.AoFadeEnd, v => AppSettings.AoFadeEnd = v);
        AddSwitch(ctx, aa, "AO blur", AppSettings.AoBlurEnabled, v => AppSettings.AoBlurEnabled = v);
        AddIntSlider(ctx, aa, "AO blur radius", 0, 24, AppSettings.AoBlurRadius, v => AppSettings.AoBlurRadius = v);
        AddFloatSlider(ctx, aa, "AO blur sharpness", 0f, 32f, AppSettings.AoBlurSharpness, v => AppSettings.AoBlurSharpness = v);
        AddIntSlider(ctx, aa, "AO blur passes", 0, 8, AppSettings.AoBlurPasses, v => AppSettings.AoBlurPasses = v);

        // Selection
        var sel = AddSection(ctx, root, "Selection", "Highlight and outline");
        AddSwitch(ctx, sel, "Highlight selected body", AppSettings.ShowSelectionHighlight, v => AppSettings.ShowSelectionHighlight = v);
        AddSwitch(ctx, sel, "Outline selected body", AppSettings.OutlineEnabled, v => AppSettings.OutlineEnabled = v);
        AddRgbRow(ctx, sel, "Outline color",
            AppSettings.OutlineR, AppSettings.OutlineG, AppSettings.OutlineB,
            AppSettings.SetOutlineColor);
        AddFloatSlider(ctx, sel, "Outline thickness", 1f, 8f, AppSettings.OutlineThicknessPx, v => AppSettings.OutlineThicknessPx = v);
        AddRgbRow(ctx, sel, "Hover outline",
            AppSettings.HoverOutlineR, AppSettings.HoverOutlineG, AppSettings.HoverOutlineB,
            AppSettings.SetHoverOutlineColor);
        AddFloatSlider(ctx, sel, "Hover thickness", 0.05f, 8f, AppSettings.HoverOutlineThicknessPx, v => AppSettings.HoverOutlineThicknessPx = v);
        AddFloatSlider(ctx, sel, "Hover tint", 0f, 1f, AppSettings.HoverTintStrength, v => AppSettings.HoverTintStrength = v);
        AddRgbRow(ctx, sel, "Dimension highlight",
            AppSettings.DimensionHighlightR, AppSettings.DimensionHighlightG, AppSettings.DimensionHighlightB,
            AppSettings.SetDimensionHighlightColor);

        // Measurement tools
        var measure = AddSection(ctx, root, "Measurement Tools", "Dimensions and boxes");
        AddLabeledToggleRow(ctx, measure, "Default measure tool", new[] { "Point", "Face-Point", "Face-Face" },
            AppSettings.MeasureModeSelectionIndex, idx => AppSettings.MeasureModeSelectionIndex = idx);
        AddLabeledToggleRow(ctx, measure, "Box mode", new[] { "Axis", "Best Fit" },
            AppSettings.MeasureBoxModeSelectionIndex, idx => AppSettings.MeasureBoxModeSelectionIndex = idx);
        AddFloatSlider(ctx, measure, "Dimension text scale", 0.5f, 4f, AppSettings.DimensionTextScale, v => AppSettings.DimensionTextScale = v);
        AddRgbRow(ctx, measure, "Face selection color",
            AppSettings.MeasurementFaceSelectionR, AppSettings.MeasurementFaceSelectionG, AppSettings.MeasurementFaceSelectionB,
            AppSettings.SetMeasurementFaceSelectionColor);
        AddRgbRow(ctx, measure, "Face hover color",
            AppSettings.MeasurementFaceHoverR, AppSettings.MeasurementFaceHoverG, AppSettings.MeasurementFaceHoverB,
            AppSettings.SetMeasurementFaceHoverColor);
        AddSwitch(ctx, measure, "Multi-measure", AppSettings.MeasureMultiMeasureEnabled, v => AppSettings.MeasureMultiMeasureEnabled = v);
        AddSwitch(ctx, measure, "Point delta breakdown", AppSettings.MeasureShowDeltaBreakdown, v => AppSettings.MeasureShowDeltaBreakdown = v);

        var snap = AddSection(ctx, root, "Point Snap", "Distance and visibility");
        AddSwitch(ctx, snap, "Enable point snap", AppSettings.MeasurePointSnapEnabled, v => AppSettings.MeasurePointSnapEnabled = v);
        AddSwitch(ctx, snap, "Endpoint snap", AppSettings.MeasureEndpointSnapEnabled, v => AppSettings.MeasureEndpointSnapEnabled = v);
        AddSwitch(ctx, snap, "Midpoint snap", AppSettings.MeasureMidpointSnapEnabled, v => AppSettings.MeasureMidpointSnapEnabled = v);
        AddFloatSlider(ctx, snap, "Edge snap distance", 0.1f, 4f, AppSettings.MeasureSnapEdgeFactor, v => AppSettings.MeasureSnapEdgeFactor = v);
        AddFloatSlider(ctx, snap, "Endpoint snap distance", 0.1f, 4f, AppSettings.MeasureSnapEndpointFactor, v => AppSettings.MeasureSnapEndpointFactor = v);
        AddSwitch(ctx, snap, "Visible edges only", AppSettings.MeasureSnapVisibleEdgesOnly, v => AppSettings.MeasureSnapVisibleEdgesOnly = v);
        AddFloatSlider(ctx, snap, "Visibility probe", 0.002f, 0.08f, AppSettings.MeasureSnapVisibilityProbe, v => AppSettings.MeasureSnapVisibilityProbe = v);
        AddFloatSlider(ctx, snap, "Occlusion tolerance", 0.1f, 10f, AppSettings.MeasureSnapOcclusionToleranceFactor, v => AppSettings.MeasureSnapOcclusionToleranceFactor = v);

        // Section tools
        var sections = AddSection(ctx, root, "Section Tools", "Caps, planes, and gizmo");
        AddSwitch(ctx, sections, "Show section fill", AppSettings.SectionFillVisible, v => AppSettings.SectionFillVisible = v);
        AddSwitch(ctx, sections, "Show section edges", AppSettings.SectionEdgesVisible, v => AppSettings.SectionEdgesVisible = v);
        AddSwitch(ctx, sections, "Show section curves", AppSettings.SectionCurvesVisible, v => AppSettings.SectionCurvesVisible = v);
        AddSwitch(ctx, sections, "Show Caps", AppSettings.SectionCapsVisible, v => AppSettings.SectionCapsVisible = v);
        AddRgbRow(ctx, sections, "Plane color",
            AppSettings.SectionPlaneR, AppSettings.SectionPlaneG, AppSettings.SectionPlaneB,
            AppSettings.SetSectionPlaneColor);
        AddFloatSlider(ctx, sections, "Plane opacity", 0f, 1f, AppSettings.SectionPlaneOpacity, v => AppSettings.SectionPlaneOpacity = v);
        AddRgbRow(ctx, sections, "Edge color",
            AppSettings.SectionEdgeR, AppSettings.SectionEdgeG, AppSettings.SectionEdgeB,
            AppSettings.SetSectionEdgeColor);
        AddFloatSlider(ctx, sections, "Section edge thickness", 0.5f, 8f, AppSettings.SectionEdgeWidth, v => AppSettings.SectionEdgeWidth = v);
        AddRgbRow(ctx, sections, "Cap color",
            AppSettings.SectionCapR, AppSettings.SectionCapG, AppSettings.SectionCapB,
            AppSettings.SetSectionCapColor);
        AddFloatSlider(ctx, sections, "Plane size", 0.005f, 0.20f, AppSettings.SectionPlaneSizeFraction, v => AppSettings.SectionPlaneSizeFraction = v);
        // S10-F9: gizmo scale belongs with the Section Tools controls it affects.
        AddFloatSlider(ctx, sections, "Section gizmo scale", 0.5f, 4f, AppSettings.SectionGizmoScale, v => AppSettings.SectionGizmoScale = v);

        var cloud = AddSection(ctx, root, "FA Cloud", "Connection, account, and project");
        var cloudSecureStore = new CloudSecureStore(ctx.ApplicationContext ?? ctx);
        AddLabeledToggleRow(ctx, cloud, "Connection", new[] { "Local network", "Internet" },
            AppSettings.CloudServerProfileSelectionIndex, AppSettings.SetCloudServerProfileSelectionIndex);
        AddSubtle(ctx, cloud, "Connection changes apply to the next cloud sign-in.");
        AddTextField(ctx, cloud, "User / email", AppSettings.CloudUserEmail, value => AppSettings.CloudUserEmail = value);
        AddTextField(ctx, cloud, "Default project", AppSettings.CloudDefaultProjectName, value => AppSettings.CloudDefaultProjectName = value);
        AddSwitch(ctx, cloud, "Keep me signed in", AppSettings.CloudRememberCredentials, remember =>
        {
            AppSettings.CloudRememberCredentials = remember;
            if (!remember)
                cloudSecureStore.ClearRefreshToken();
        });
        AddSwitch(ctx, cloud, "Remember password", AppSettings.CloudRememberPassword, remember =>
        {
            AppSettings.CloudRememberPassword = remember;
            if (!remember)
                cloudSecureStore.ClearRememberedPassword();
        });

        // Navigation
        var nav = AddSection(ctx, root, "Navigation", "Orbit, pan, zoom");
        AddSwitch(ctx, nav, "Lightweight camera navigation", AppSettings.LightweightNavigationEnabled, v => AppSettings.LightweightNavigationEnabled = v);
        AddFloatSlider(ctx, nav, "Orbit sensitivity", 0.1f, 5f, AppSettings.OrbitSensitivity, v => AppSettings.OrbitSensitivity = v);
        AddFloatSlider(ctx, nav, "Pan sensitivity", 0.1f, 5f, AppSettings.PanSensitivity, v => AppSettings.PanSensitivity = v);
        AddFloatSlider(ctx, nav, "Pinch-zoom sensitivity", 0.1f, 5f, AppSettings.ZoomSensitivity, v => AppSettings.ZoomSensitivity = v);

        var mouse = AddSection(ctx, root, "Mouse", "Desktop-style mouse controls");
        AddSwitch(ctx, mouse, "Enable hover", AppSettings.MouseHoverEnabled, v => AppSettings.MouseHoverEnabled = v);
        AddSwitch(ctx, mouse, "Invert wheel zoom", AppSettings.MouseInvertWheelZoom, v => AppSettings.MouseInvertWheelZoom = v);
        AddFloatSlider(ctx, mouse, "Right-drag orbit speed", 0.1f, 5f, AppSettings.MouseOrbitSpeed, v => AppSettings.MouseOrbitSpeed = v);
        AddFloatSlider(ctx, mouse, "Middle-drag pan speed", 0.1f, 5f, AppSettings.MousePanSpeed, v => AppSettings.MousePanSpeed = v);
        AddFloatSlider(ctx, mouse, "Wheel zoom speed", 0.1f, 5f, AppSettings.MouseWheelZoomSpeed, v => AppSettings.MouseWheelZoomSpeed = v);
        AddFloatSlider(ctx, mouse, "Click vs drag threshold", 1f, 20f, AppSettings.MouseDragThresholdDip, v => AppSettings.MouseDragThresholdDip = v);

        var diagnostics = AddSection(
            ctx,
            root,
            "Diagnostics",
            "Live app console logs",
            expandedByDefault: false,
            onExpandedChanged: expanded => OnLogPanelExpandedChanged(ctx, expanded));
        AddLogPanel(ctx, diagnostics);

        AddSubtle(ctx, root, "Rendering controls apply live.");

        scroll.AddView(root);
        DisposeDiagnosticsLogTooltips();
        return scroll;
    }

    private void ReplaceVisibleSettingsView(Context ctx, View currentView)
    {
        if (currentView.Parent is not ViewGroup parent)
        {
            currentView.Post(() => ReanchorSheetAfterContentChange(currentView));
            return;
        }

        int index = parent.IndexOfChild(currentView);
        if (index < 0)
            return;

        ViewGroup.LayoutParams? layoutParams = currentView.LayoutParameters;
        DisposeLogFeed();
        parent.RemoveViewAt(index);
        View replacement = CreateEmbeddedView(ctx);
        parent.AddView(replacement, index, layoutParams);
        replacement.Post(() => ReanchorSheetAfterContentChange(replacement));
    }

    private void RefreshVisibleSettingsView()
    {
        if (_disposed || Context is not { } ctx || View is not { } view)
            return;

        ReplaceVisibleSettingsView(ctx, view);
    }

    private void ApplySheetLayout()
    {
        var ctx = Context;
        if (ctx is null || Dialog is not BottomSheetDialog dialog)
        {
            return;
        }

        var sheet = dialog.FindViewById<FrameLayout>(Resource.Id.design_bottom_sheet);
        if (sheet is null)
        {
            return;
        }

        var viewport = Activity?.FindViewById<FrameLayout>(Resource.Id.viewportContainer);
        sheet.Post(() => ApplySheetLayoutNow(ctx, dialog, sheet, viewport));
    }

    private void ApplySheetLayoutNow(Context ctx, BottomSheetDialog dialog, FrameLayout sheet, View? viewport)
    {
        int startOffset = System.Math.Max(0, CalculateSheetStartOffset(ctx, sheet, viewport));
        int defaultWidth = CalculateSheetWidth(ctx, startOffset, viewport);
        int width = _sheetWidthOverridePx > 0
            ? ClampSheetWidth(ctx, startOffset, viewport, _sheetWidthOverridePx)
            : defaultWidth;
        _sheetWidthOverridePx = _sheetWidthOverridePx > 0 ? width : 0;
        int topOffset = System.Math.Max(0, CalculateSheetTopOffset(ctx, sheet, viewport));
        int bottomOffset = System.Math.Max(0, CalculateSheetBottomOffset(ctx, sheet, viewport));
        int height = CalculateSheetHeight(ctx, sheet, viewport, topOffset, bottomOffset);
        var layoutParams = sheet.LayoutParameters;

        if (layoutParams is AndroidX.CoordinatorLayout.Widget.CoordinatorLayout.LayoutParams coordinatorParams)
        {
            coordinatorParams.Width = width;
            coordinatorParams.Height = height;
            coordinatorParams.Gravity = (int)(GravityFlags.Start | GravityFlags.Top);
            coordinatorParams.LeftMargin = startOffset;
            coordinatorParams.RightMargin = 0;
            coordinatorParams.TopMargin = topOffset;
            coordinatorParams.BottomMargin = bottomOffset;
            sheet.LayoutParameters = coordinatorParams;
        }
        else if (layoutParams is ViewGroup.MarginLayoutParams marginParams)
        {
            marginParams.Width = width;
            marginParams.Height = height;
            marginParams.LeftMargin = startOffset;
            marginParams.RightMargin = 0;
            marginParams.TopMargin = topOffset;
            marginParams.BottomMargin = bottomOffset;
            sheet.LayoutParameters = marginParams;
        }
        else
        {
            sheet.LayoutParameters = new ViewGroup.LayoutParams(width, ViewGroup.LayoutParams.MatchParent);
        }

        sheet.SetBackgroundColor(GetColor(ctx, Resource.Color.fa_app_background));
        dialog.Behavior.FitToContents = false;
        dialog.Behavior.ExpandedOffset = topOffset;
        dialog.Behavior.Hideable = false;
        dialog.Behavior.PeekHeight = height;
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        dialog.Behavior.SkipCollapsed = true;
        dialog.Behavior.Draggable = false;
        EnsureResizeHandle(ctx, sheet, startOffset, viewport);
        sheet.RequestLayout();
    }

    private void EnsureResizeHandle(Context ctx, FrameLayout sheet, int startOffset, View? viewport)
    {
        if (_resizeHandle is not null && _resizeHandle.Parent == sheet)
        {
            return;
        }

        _resizeHandle?.SetOnTouchListener(null);
        _resizeHandle = CreateResizeHandle(ctx, lineAtEnd: true);
        _resizeHandle.ContentDescription = "Resize settings panel";
        SetTooltip(ctx, _resizeHandle, "Resize settings panel");
        _resizeHandle.SetOnTouchListener(new HorizontalResizeTouchListener(
            () => sheet.Width > 0 ? sheet.Width : CalculateSheetWidth(ctx, startOffset, viewport),
            width => ClampSheetWidth(ctx, startOffset, viewport, width),
            width =>
            {
                _sheetWidthOverridePx = width;
                if (Dialog is BottomSheetDialog currentDialog)
                {
                    ApplySheetLayoutNow(ctx, currentDialog, sheet, viewport);
                }
            },
            direction: 1));

        sheet.AddView(_resizeHandle, new FrameLayout.LayoutParams(
            ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_panel_resize_handle_width),
            ViewGroup.LayoutParams.MatchParent,
            GravityFlags.End));
    }

    private static FrameLayout CreateResizeHandle(Context ctx, bool lineAtEnd)
    {
        var handle = new FrameLayout(ctx)
        {
            Clickable = true,
            Focusable = false,
        };

        var line = new View(ctx);
        line.SetBackgroundColor(GetColor(ctx, Resource.Color.fa_border));
        handle.AddView(line, new FrameLayout.LayoutParams(
            ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_panel_resize_handle_line_width),
            ViewGroup.LayoutParams.MatchParent,
            lineAtEnd ? GravityFlags.End : GravityFlags.Start));
        return handle;
    }

    private static int CalculateSheetHeight(Context ctx, View sheet, View? viewport, int topOffset, int bottomOffset)
    {
        int available = CalculateDesiredSheetBottom(ctx, sheet, viewport) - CalculateDesiredSheetTop(ctx, sheet, viewport);
        if (sheet.Parent is View parent && parent.Height > 0)
        {
            available = System.Math.Min(available, parent.Height - topOffset - bottomOffset);
        }

        return available > 0 ? available : ViewGroup.LayoutParams.MatchParent;
    }

    private static int CalculateSheetTopOffset(Context ctx, View sheet, View? viewport)
        => CalculateDesiredSheetTop(ctx, sheet, viewport) - GetParentScreenTop(sheet);

    private static int CalculateSheetBottomOffset(Context ctx, View sheet, View? viewport)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        int parentBottom = GetParentScreenTop(sheet) + ((sheet.Parent as View)?.Height ?? metrics?.HeightPixels ?? 0);
        return parentBottom - CalculateDesiredSheetBottom(ctx, sheet, viewport);
    }

    private static int CalculateDesiredSheetTop(Context ctx, View sheet, View? viewport)
    {
        if (TryGetViewportBounds(viewport, out _, out int viewportTop, out _))
        {
            return viewportTop;
        }

        int appBarHeight = ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_app_bar_height);
        if (sheet.Parent is View parent && parent.Height > 0)
        {
            return GetParentScreenTop(sheet) + appBarHeight;
        }

        return GetSystemBarHeight(ctx, "status_bar_height") + appBarHeight;
    }

    private static int CalculateDesiredSheetBottom(Context ctx, View sheet, View? viewport)
    {
        if (TryGetViewportBounds(viewport, out _, out _, out int viewportBottom))
        {
            return viewportBottom;
        }

        int bottomBarHeight = ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_bottom_bar_height);
        if (sheet.Parent is View parent && parent.Height > 0)
        {
            return GetParentScreenTop(sheet) + parent.Height - bottomBarHeight;
        }

        var metrics = ctx.Resources?.DisplayMetrics;
        int screenBottom = metrics?.HeightPixels ?? 0;
        return screenBottom
               - GetSystemBarHeight(ctx, "navigation_bar_height")
               - bottomBarHeight;
    }

    private static int CalculateSheetStartOffset(Context ctx, View sheet, View? viewport)
    {
        if (TryGetViewportBounds(viewport, out int viewportLeft, out _, out _))
        {
            return viewportLeft - GetParentScreenLeft(sheet);
        }

        return CalculateFallbackSheetStartOffset(ctx);
    }

    private static bool TryGetViewportBounds(View? viewport, out int left, out int top, out int bottom)
    {
        left = 0;
        top = 0;
        bottom = 0;

        if (viewport is null || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return false;
        }

        var location = new int[2];
        viewport.GetLocationOnScreen(location);
        left = location[0];
        top = location[1];
        bottom = top + viewport.Height;
        return true;
    }

    private static int GetParentScreenLeft(View sheet)
    {
        if (sheet.Parent is not View parent)
        {
            return 0;
        }

        var location = new int[2];
        parent.GetLocationOnScreen(location);
        return location[0];
    }

    private static int GetParentScreenTop(View sheet)
    {
        if (sheet.Parent is not View parent)
        {
            return 0;
        }

        var location = new int[2];
        parent.GetLocationOnScreen(location);
        return location[1];
    }

    private static int GetSystemBarHeight(Context ctx, string resourceName)
    {
        int resourceId = ctx.Resources?.GetIdentifier(resourceName, "dimen", "android") ?? 0;
        return resourceId > 0 ? ctx.Resources!.GetDimensionPixelSize(resourceId) : 0;
    }

    private static int CalculateSheetWidth(Context ctx)
        => CalculateSheetWidth(ctx, CalculateFallbackSheetStartOffset(ctx));

    private static int CalculateSheetWidth(Context ctx, int startOffset)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        if (metrics is null)
        {
            return ViewGroup.LayoutParams.MatchParent;
        }

        float widthDp = metrics.WidthPixels / metrics.Density;
        int edgePadding = Dp(ctx, widthDp >= TabletBreakpointDp ? 16f : 0f);
        int desired = Dp(ctx, widthDp >= TabletBreakpointDp ? TabletPanelWidthDp : CompactPanelWidthDp);
        int available = metrics.WidthPixels - startOffset - edgePadding;
        int minimum = System.Math.Min(Dp(ctx, 320f), available);
        return System.Math.Max(minimum, System.Math.Min(desired, available));
    }

    private static int CalculateSheetWidth(Context ctx, int startOffset, View? viewport)
    {
        int width = CalculateSheetWidth(ctx, startOffset);
        if (viewport is { Width: > 0 })
        {
            width = System.Math.Min(width, System.Math.Max(1, viewport.Width - Dp(ctx, 16f)));
        }

        return width;
    }

    private static int ClampSheetWidth(Context ctx, int startOffset, View? viewport, int requestedWidth)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        if (metrics is null)
        {
            return requestedWidth;
        }

        float widthDp = metrics.WidthPixels / metrics.Density;
        int edgePadding = Dp(ctx, widthDp >= TabletBreakpointDp ? 16f : 0f);
        int available = metrics.WidthPixels - startOffset - edgePadding;
        if (viewport is { Width: > 0 })
        {
            available = System.Math.Min(available, System.Math.Max(1, viewport.Width - Dp(ctx, 16f)));
        }

        int minimum = System.Math.Min(Dp(ctx, 320f), available);
        return System.Math.Max(minimum, System.Math.Min(requestedWidth, available));
    }

    private static int CalculateFallbackSheetStartOffset(Context ctx)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        if (metrics is null)
        {
            return 0;
        }

        float widthDp = metrics.WidthPixels / metrics.Density;
        return widthDp >= TabletBreakpointDp
            ? ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_nav_rail_width)
            : 0;
    }

    // Builder helpers

    private void AddHeader(Context ctx, ViewGroup parent, string text, int sizePx, Action resetToDefaults)
    {
        var row = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent,
                LinearLayout.LayoutParams.WrapContent),
        };
        row.SetGravity(GravityFlags.CenterVertical);
        SetMarginBottom(row, Dp(ctx, 12));

        var tv = new TextView(ctx) { Text = text };
        tv.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        tv.SetTextSize(ComplexUnitType.Px, sizePx);
        tv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        tv.SetTypeface(tv.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
        row.AddView(tv);

        var reset = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = "Reset",
        };
        reset.SetMinWidth(0);
        reset.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
        reset.SetTextSize(ComplexUnitType.Px, Dp(ctx, 13));
        reset.SetPadding(Dp(ctx, 12), 0, Dp(ctx, 12), 0);
        reset.LayoutParameters = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, Dp(ctx, 36));
        reset.ContentDescription = "Reset settings";
        SetTooltip(ctx, reset, "Reset settings");
        reset.Click += (_, _) => resetToDefaults();
        row.AddView(reset);

        parent.AddView(row);
    }

    private LinearLayout AddSection(
        Context ctx,
        ViewGroup parent,
        string title,
        string summary,
        bool expandedByDefault = false,
        Action<bool>? onExpandedChanged = null)
    {
        var card = new MaterialCardView(ctx)
        {
            LayoutParameters = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent),
        };
        ((LinearLayout.LayoutParams)card.LayoutParameters!).BottomMargin = Dp(ctx, 12);
        card.SetCardBackgroundColor(GetColor(ctx, Resource.Color.fa_surface_background));
        card.Radius = Dp(ctx, 8);
        card.StrokeWidth = Dp(ctx, 1);
        card.SetStrokeColor(global::Android.Content.Res.ColorStateList.ValueOf(GetColor(ctx, Resource.Color.fa_border)));
        card.Elevation = 0f;

        var inner = new LinearLayout(ctx) { Orientation = Orientation.Vertical };

        var header = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true,
            Focusable = true,
        };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(ctx, 16), Dp(ctx, 12), Dp(ctx, 12), Dp(ctx, 12));

        var titleGroup = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        titleGroup.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);

        var titleTv = new TextView(ctx) { Text = title };
        titleTv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 15));
        titleTv.SetTypeface(titleTv.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
        titleTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        titleGroup.AddView(titleTv);

        var summaryTv = new TextView(ctx) { Text = summary };
        summaryTv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        summaryTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        titleGroup.AddView(summaryTv);

        var arrow = new ImageView(ctx);
        arrow.SetImageResource(Resource.Drawable.ic_chevron_right);
        arrow.SetColorFilter(GetColor(ctx, Resource.Color.fa_text_secondary));
        arrow.ContentDescription = title + ", expand or collapse section";
        arrow.LayoutParameters = new LinearLayout.LayoutParams(Dp(ctx, 24), Dp(ctx, 24));
        SetTooltip(ctx, header, title);
        SetTooltip(ctx, arrow, title + ", expand or collapse section");

        header.AddView(titleGroup);
        header.AddView(arrow);
        inner.AddView(header);

        var content = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(ctx, 16), Dp(ctx, 0), Dp(ctx, 16), Dp(ctx, 16));
        inner.AddView(content);

        var isExpanded = expandedByDefault;
        ApplySectionState(ctx, card, content, arrow, isExpanded);
        header.Click += (_, _) =>
        {
            isExpanded = !isExpanded;
            ApplySectionState(ctx, card, content, arrow, isExpanded);
            onExpandedChanged?.Invoke(isExpanded);
            ReanchorSheetAfterContentChange(content);
        };

        card.AddView(inner);
        parent.AddView(card);
        return content;
    }

    private void ReanchorSheetAfterContentChange(View anchor)
    {
        anchor.Post(ApplySheetLayout);
        anchor.PostDelayed(ApplySheetLayout, 80);
        anchor.PostDelayed(ApplySheetLayout, 180);
    }

    private static void ApplySectionState(Context ctx, MaterialCardView card, View content, ImageView arrow, bool expanded)
    {
        content.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;
        arrow.Rotation = expanded ? 90f : 0f;
        arrow.SetColorFilter(GetColor(ctx, expanded ? Resource.Color.fa_accent_500 : Resource.Color.fa_text_secondary));
        card.SetStrokeColor(global::Android.Content.Res.ColorStateList.ValueOf(
            GetColor(ctx, expanded ? Resource.Color.fa_accent_700 : Resource.Color.fa_border)));
    }

    private MaterialSwitch AddSwitch(Context ctx, ViewGroup parent, string label, bool initial, Action<bool> save)
    {
        var sw = new MaterialSwitch(ctx) { Text = label, Checked = initial };
        sw.ContentDescription = FormatSwitchContentDescription(label, initial);
        SetTooltip(ctx, sw, label);
        sw.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        sw.LayoutParameters = lp;
        sw.CheckedChange += (_, e) =>
        {
            sw.ContentDescription = FormatSwitchContentDescription(label, e.IsChecked);
            save(e.IsChecked);
            NotifySettingsChanged();
        };
        parent.AddView(sw);
        return sw;
    }

    private static string FormatSwitchContentDescription(string label, bool isChecked)
        => label + (isChecked ? ", on" : ", off");

    private EditText AddTextField(Context ctx, ViewGroup parent, string label, string initial, Action<string> save, bool isPassword = false, bool commitOnChange = true)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 8);
        row.LayoutParameters = lp;

        var labelTv = new TextView(ctx) { Text = label };
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        labelTv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        row.AddView(labelTv);

        var input = new EditText(ctx)
        {
            Text = initial ?? "",
        };
        input.SetSingleLine(true);
        input.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        input.SetHintTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        input.InputType = isPassword
            ? InputTypes.ClassText | InputTypes.TextVariationPassword
            : InputTypes.ClassText | InputTypes.TextVariationUri;
        input.ContentDescription = label;
        SetTooltip(ctx, row, label);
        SetTooltip(ctx, labelTv, label);
        SetTooltip(ctx, input, label, useLongClick: false);
        if (commitOnChange)
            input.TextChanged += (_, _) => save(input.Text ?? "");
        row.AddView(input, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            Dp(ctx, 44)));

        parent.AddView(row);
        return input;
    }

    private EditText AddFloatField(Context ctx, ViewGroup parent, string label, float initial, Action<float> save)
    {
        // S10-2: commit only on Enter/Done or focus loss, not on every keystroke.
        // Numeric setters (e.g. clip planes) rewrite dependent state and clamp on
        // each call, so committing partially-typed values shuffles them per digit.
        EditText input = null!;
        void Commit()
        {
            if (!TryParseFloatField(input.Text, out float value))
                return;

            save(value);
            NotifySettingsChanged();
        }

        input = AddTextField(
            ctx,
            parent,
            label,
            initial.ToString("G9", CultureInfo.InvariantCulture),
            _ => { },
            commitOnChange: false);
        input.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal;
        DialogKeyboard.ConfirmOnEnter(input, Commit);
        input.FocusChange += (_, e) =>
        {
            if (!e.HasFocus)
                Commit();
        };
        return input;
    }

    private static bool TryParseFloatField(string? text, out float value)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return float.IsFinite(value);

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return float.IsFinite(value);

        value = 0.0f;
        return false;
    }

    private FloatSliderControl AddFloatSlider(Context ctx, ViewGroup parent, string label, float min, float max, float initial, Action<float> save, bool logarithmic = false)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        row.LayoutParameters = lp;

        var labelTv = new TextView(ctx) { Text = FormatSliderValue(label, initial) };
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        SetTooltip(ctx, row, label);
        SetTooltip(ctx, labelTv, label);

        var seek = new SeekBar(ctx) { ContentDescription = label };
        SetTooltip(ctx, seek, label);
        seek.Max = 1000;

        // Logarithmic mapping (requires min > 0) gives each decade equal travel so
        // sliders spanning several orders of magnitude (e.g. grid spacing
        // 0.001..1e6 or weld tolerance 1e-6..1e-4) stay usable instead of crushing
        // the low end into a few pixels.
        float ProgressToValue(int progress) => logarithmic
            ? (float)(min * System.Math.Pow(max / (double)min, progress / 1000.0))
            : min + progress / 1000f * (max - min);
        int ValueToProgress(float value) => System.Math.Clamp(
            (int)System.Math.Round(logarithmic
                ? System.Math.Log(value / (double)min) / System.Math.Log(max / (double)min) * 1000.0
                : (value - min) / (double)(max - min) * 1000.0),
            0,
            1000);

        seek.Progress = ValueToProgress(initial);
        seek.ProgressChanged += (_, e) =>
        {
            float v = ProgressToValue(e.Progress);
            labelTv.Text = FormatSliderValue(label, v);
            if (e.FromUser)
            {
                save(v);
                NotifySettingsChanged();
            }
        };

        row.AddView(labelTv);
        row.AddView(seek);
        parent.AddView(row);
        return new FloatSliderControl(seek, ValueToProgress);
    }

    // Handle returned by AddFloatSlider so a caller can refresh the displayed value
    // when another control changes the underlying setting. SetValue updates the
    // thumb + label without invoking the save callback (the programmatic progress
    // change fires ProgressChanged with FromUser == false).
    private sealed class FloatSliderControl
    {
        private readonly SeekBar _seek;
        private readonly Func<float, int> _valueToProgress;

        public FloatSliderControl(SeekBar seek, Func<float, int> valueToProgress)
        {
            _seek = seek;
            _valueToProgress = valueToProgress;
        }

        public void SetValue(float value) => _seek.Progress = _valueToProgress(value);
    }

    private static string FormatSliderValue(string label, float value)
    {
        string format = Math.Abs(value) < 0.01f && value != 0f ? "F6" : "F4";
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}: {1}",
            label,
            value.ToString(format, System.Globalization.CultureInfo.InvariantCulture));
    }

    private void AddIntSlider(Context ctx, ViewGroup parent, string label, int min, int max, int initial, Action<int> save)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        row.LayoutParameters = lp;

        var labelTv = new TextView(ctx) { Text = $"{label}: {initial}" };
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        SetTooltip(ctx, row, label);
        SetTooltip(ctx, labelTv, label);

        var seek = new SeekBar(ctx) { ContentDescription = label };
        SetTooltip(ctx, seek, label);
        seek.Max = max - min;
        seek.Progress = System.Math.Clamp(initial - min, 0, max - min);
        seek.ProgressChanged += (_, e) =>
        {
            int v = min + e.Progress;
            labelTv.Text = $"{label}: {v}";
            if (e.FromUser) { save(v); NotifySettingsChanged(); }
        };

        row.AddView(labelTv);
        row.AddView(seek);
        parent.AddView(row);
    }

    private void AddRgbRow(Context ctx, ViewGroup parent, string label, float r0, float g0, float b0,
        Action<float, float, float> save)
    {
        float currentR = Clamp01(r0);
        float currentG = Clamp01(g0);
        float currentB = Clamp01(b0);

        var row = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true,
            Focusable = true,
        };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        row.LayoutParameters = lp;
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, Dp(ctx, 3), 0, Dp(ctx, 3));
        SetTooltip(ctx, row, label);

        var labelTv = new TextView(ctx) { Text = label };
        labelTv.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        labelTv.Ellipsize = TextUtils.TruncateAt.End;
        labelTv.SetSingleLine(true);
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        SetTooltip(ctx, labelTv, label);
        row.AddView(labelTv);

        var swatch = new View(ctx);
        var swatchLp = new LinearLayout.LayoutParams(Dp(ctx, 42), Dp(ctx, 28));
        swatchLp.LeftMargin = Dp(ctx, 8);
        swatchLp.RightMargin = Dp(ctx, 10);
        swatch.LayoutParameters = swatchLp;
        swatch.ContentDescription = label + " color swatch";
        SetTooltip(ctx, swatch, label);
        row.AddView(swatch);

        var hexTv = new TextView(ctx);
        hexTv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        hexTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        hexTv.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        hexTv.SetMinWidth(Dp(ctx, 72));
        hexTv.ContentDescription = label + " color value";
        SetTooltip(ctx, hexTv, label);
        row.AddView(hexTv);

        var pick = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = "Pick",
        };
        pick.SetMinWidth(0);
        pick.SetMinHeight(0);
        pick.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        pick.SetPadding(Dp(ctx, 10), 0, Dp(ctx, 10), 0);
        var pickLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, Dp(ctx, 34));
        pickLp.LeftMargin = Dp(ctx, 8);
        pick.LayoutParameters = pickLp;
        pick.ContentDescription = $"Pick {label} color";
        SetTooltip(ctx, pick, $"Pick {label} color");
        row.AddView(pick);

        void ApplyRowState()
        {
            SetColorSwatch(ctx, swatch, currentR, currentG, currentB);
            hexTv.Text = ToHexColor(currentR, currentG, currentB);
        }

        void SaveColor(float r, float g, float b)
        {
            currentR = Clamp01(r);
            currentG = Clamp01(g);
            currentB = Clamp01(b);
            save(currentR, currentG, currentB);
            ApplyRowState();
            NotifySettingsChanged();
        }

        void OpenPicker() => ShowColorPickerDialog(ctx, label, currentR, currentG, currentB, SaveColor);

        ApplyRowState();
        row.Click += (_, _) => OpenPicker();
        swatch.Click += (_, _) => OpenPicker();
        hexTv.Click += (_, _) => OpenPicker();
        pick.Click += (_, _) => OpenPicker();

        parent.AddView(row);
    }

    private void ShowColorPickerDialog(Context ctx, string label, float r0, float g0, float b0, Action<float, float, float> save)
    {
        int currentR = ColorByte(r0);
        int currentG = ColorByte(g0);
        int currentB = ColorByte(b0);
        RgbToHsv(currentR, currentG, currentB, out float hue, out float saturation, out float value);
        bool updatingControls = false;

        var root = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetPadding(Dp(ctx, 20), Dp(ctx, 12), Dp(ctx, 20), Dp(ctx, 4));
        root.Background = CreateColorDialogContentBackground(ctx);

        var valueRow = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
        };
        valueRow.SetGravity(GravityFlags.CenterVertical);
        var valueRowLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        valueRowLp.BottomMargin = Dp(ctx, 14);
        root.AddView(valueRow, valueRowLp);

        var preview = new View(ctx);
        preview.ContentDescription = $"{label} color preview";
        var previewLp = new LinearLayout.LayoutParams(Dp(ctx, 74), Dp(ctx, 44));
        previewLp.RightMargin = Dp(ctx, 12);
        valueRow.AddView(preview, previewLp);

        var hexInput = new EditText(ctx)
        {
            Text = ToHexColor(currentR, currentG, currentB),
            InputType = InputTypes.ClassText | InputTypes.TextFlagCapCharacters | InputTypes.TextFlagNoSuggestions,
        };
        hexInput.ContentDescription = $"{label} hex color";
        hexInput.SetSingleLine(true);
        hexInput.SetSelectAllOnFocus(true);
        hexInput.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        hexInput.SetHintTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        hexInput.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        hexInput.SetPadding(Dp(ctx, 12), 0, Dp(ctx, 12), 0);
        hexInput.Background = CreateColorInputBackground(ctx);
        valueRow.AddView(hexInput, new LinearLayout.LayoutParams(0, Dp(ctx, 44), 1f));

        var colorPlane = new ColorPlaneView(ctx);
        colorPlane.ContentDescription = $"{label} saturation and brightness";
        colorPlane.SetColor(hue, saturation, value);
        var planeLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, Dp(ctx, 212));
        planeLp.BottomMargin = Dp(ctx, 12);
        root.AddView(colorPlane, planeLp);

        var hueSlider = new HueSliderView(ctx);
        hueSlider.ContentDescription = $"{label} hue";
        hueSlider.SetHue(hue);
        var hueLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, Dp(ctx, 34));
        hueLp.BottomMargin = Dp(ctx, 10);
        root.AddView(hueSlider, hueLp);

        var detail = new TextView(ctx);
        detail.ContentDescription = $"{label} hue, saturation, and brightness value";
        detail.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        detail.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        detail.Gravity = GravityFlags.CenterHorizontal;
        var detailLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        detailLp.BottomMargin = Dp(ctx, 2);
        root.AddView(detail, detailLp);

        void ApplyHsvToRgb()
        {
            HsvToRgb(hue, saturation, value, out currentR, out currentG, out currentB);
        }

        void RefreshControls(bool updateHex, bool updatePickers)
        {
            ApplyHsvToRgb();
            SetColorSwatch(ctx, preview, currentR, currentG, currentB);
            detail.Text = $"H {System.Math.Round(hue):0}   S {System.Math.Round(saturation * 100f):0}%   V {System.Math.Round(value * 100f):0}%";

            if (updatePickers)
            {
                colorPlane.SetColor(hue, saturation, value);
                hueSlider.SetHue(hue);
            }

            if (!updateHex)
            {
                return;
            }

            updatingControls = true;
            hexInput.Text = ToHexColor(currentR, currentG, currentB);
            hexInput.SetSelection(hexInput.Text?.Length ?? 0);
            updatingControls = false;
        }

        colorPlane.ColorChanged += (_, e) =>
        {
            if (updatingControls)
                return;
            saturation = e.Saturation;
            value = e.Value;
            RefreshControls(updateHex: true, updatePickers: false);
        };
        hueSlider.HueChanged += (_, e) =>
        {
            if (updatingControls)
                return;
            hue = e.Hue;
            colorPlane.SetColor(hue, saturation, value);
            RefreshControls(updateHex: true, updatePickers: false);
        };

        hexInput.TextChanged += (_, _) =>
        {
            if (updatingControls || !TryParseHexColor(hexInput.Text, out int parsedR, out int parsedG, out int parsedB))
                return;

            updatingControls = true;
            currentR = parsedR;
            currentG = parsedG;
            currentB = parsedB;
            RgbToHsv(currentR, currentG, currentB, out hue, out saturation, out value);
            colorPlane.SetColor(hue, saturation, value);
            hueSlider.SetHue(hue);
            SetColorSwatch(ctx, preview, currentR, currentG, currentB);
            detail.Text = $"H {System.Math.Round(hue):0}   S {System.Math.Round(saturation * 100f):0}%   V {System.Math.Round(value * 100f):0}%";
            updatingControls = false;
        };

        RefreshControls(updateHex: false, updatePickers: true);

        AlertDialog dialog = new AlertDialog.Builder(ctx)
            .SetTitle(label)!
            .SetView(root)!
            .SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { })!
            .SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => { })!
            .Create()!;

        dialog.Show();
        GradientDrawable? windowBackground = CreateColorDialogWindowBackground(ctx);
        dialog.Window?.SetBackgroundDrawable(windowBackground);
        TrackColorPickerDialog(dialog, windowBackground, root, preview, hexInput);

        Button? positive = dialog.GetButton((int)global::Android.Content.DialogButtonType.Positive);
        if (positive is not null)
        {
            positive.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
            positive.Click += (_, _) =>
            {
                if (!TryParseHexColor(hexInput.Text, out int parsedR, out int parsedG, out int parsedB))
                {
                    Toast.MakeText(ctx, "Enter a valid hex color.", ToastLength.Short)?.Show();
                    hexInput.SelectAll();
                    return;
                }

                save(parsedR / 255f, parsedG / 255f, parsedB / 255f);
                dialog.Dismiss();
            };
            DialogKeyboard.ConfirmOnEnter(hexInput, positive);
        }

        Button? negative = dialog.GetButton((int)global::Android.Content.DialogButtonType.Negative);
        if (negative is not null)
        {
            negative.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
        }
    }

    private static GradientDrawable CreateColorDialogContentBackground(Context ctx)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(GetColor(ctx, Resource.Color.fa_surface_background));
        drawable.SetCornerRadius(Dp(ctx, 8));
        return drawable;
    }

    private static GradientDrawable CreateColorDialogWindowBackground(Context ctx)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(GetColor(ctx, Resource.Color.fa_surface_background));
        drawable.SetCornerRadius(Dp(ctx, 8));
        drawable.SetStroke(Dp(ctx, 1), GetColor(ctx, Resource.Color.fa_border));
        return drawable;
    }

    private static GradientDrawable CreateColorInputBackground(Context ctx)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(GetColor(ctx, Resource.Color.fa_control_background));
        drawable.SetCornerRadius(Dp(ctx, 6));
        drawable.SetStroke(Dp(ctx, 1), GetColor(ctx, Resource.Color.fa_border));
        return drawable;
    }

    private static void SetColorSwatch(Context ctx, View swatch, float r, float g, float b)
        => SetColorSwatch(ctx, swatch, ColorByte(r), ColorByte(g), ColorByte(b));

    private static void SetColorSwatch(Context ctx, View swatch, int r, int g, int b)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(new global::Android.Graphics.Color(ToArgb(r, g, b)));
        drawable.SetCornerRadius(Dp(ctx, 6));
        drawable.SetStroke(Dp(ctx, 1), GetColor(ctx, Resource.Color.fa_border));
        Drawable? previous = swatch.Background;
        swatch.Background = drawable;
        previous?.Dispose();
    }

    private void TrackColorPickerDialog(AlertDialog dialog, Drawable? windowBackground, params View[] ownedViews)
    {
        _colorPickerDialogs.Add(dialog);
        dialog.SetOnDismissListener(new DialogDismissListener(() =>
        {
            _colorPickerDialogs.Remove(dialog);
            DisposeColorPickerDrawables(dialog, windowBackground, ownedViews);
        }));
    }

    private void DismissColorPickerDialogs()
    {
        foreach (AlertDialog dialog in _colorPickerDialogs.ToArray())
        {
            if (dialog.IsShowing)
                dialog.Dismiss();
            else
                DisposeColorPickerDrawables(dialog, null);
        }

        _colorPickerDialogs.Clear();
    }

    private static void DisposeColorPickerDrawables(AlertDialog dialog, Drawable? windowBackground, params View[] ownedViews)
    {
        dialog.Window?.SetBackgroundDrawable(null);
        windowBackground?.Dispose();

        foreach (View view in ownedViews)
        {
            Drawable? background = view.Background;
            if (background is null)
                continue;

            view.Background = null;
            background.Dispose();
        }
    }

    private sealed class DialogDismissListener : Java.Lang.Object, IDialogInterfaceOnDismissListener
    {
        private readonly Action _onDismiss;

        public DialogDismissListener(Action onDismiss)
            => _onDismiss = onDismiss;

        public void OnDismiss(IDialogInterface? dialog)
            => _onDismiss();
    }

    private static void RgbToHsv(int r, int g, int b, out float hue, out float saturation, out float value)
    {
        float rf = System.Math.Clamp(r, 0, 255) / 255f;
        float gf = System.Math.Clamp(g, 0, 255) / 255f;
        float bf = System.Math.Clamp(b, 0, 255) / 255f;
        float max = System.Math.Max(rf, System.Math.Max(gf, bf));
        float min = System.Math.Min(rf, System.Math.Min(gf, bf));
        float delta = max - min;

        if (delta <= 1e-6f)
        {
            hue = 0f;
        }
        else if (max == rf)
        {
            hue = 60f * (((gf - bf) / delta) % 6f);
        }
        else if (max == gf)
        {
            hue = 60f * (((bf - rf) / delta) + 2f);
        }
        else
        {
            hue = 60f * (((rf - gf) / delta) + 4f);
        }

        if (hue < 0f)
            hue += 360f;

        saturation = max <= 1e-6f ? 0f : delta / max;
        value = max;
    }

    private static void HsvToRgb(float hue, float saturation, float value, out int r, out int g, out int b)
    {
        hue = NormalizeHue(hue);
        saturation = Clamp01(saturation);
        value = Clamp01(value);

        float c = value * saturation;
        float x = c * (1f - System.Math.Abs((hue / 60f % 2f) - 1f));
        float m = value - c;

        float rf;
        float gf;
        float bf;
        if (hue < 60f)
        {
            rf = c; gf = x; bf = 0f;
        }
        else if (hue < 120f)
        {
            rf = x; gf = c; bf = 0f;
        }
        else if (hue < 180f)
        {
            rf = 0f; gf = c; bf = x;
        }
        else if (hue < 240f)
        {
            rf = 0f; gf = x; bf = c;
        }
        else if (hue < 300f)
        {
            rf = x; gf = 0f; bf = c;
        }
        else
        {
            rf = c; gf = 0f; bf = x;
        }

        r = System.Math.Clamp((int)System.Math.Round((rf + m) * 255f), 0, 255);
        g = System.Math.Clamp((int)System.Math.Round((gf + m) * 255f), 0, 255);
        b = System.Math.Clamp((int)System.Math.Round((bf + m) * 255f), 0, 255);
    }

    private static string ToHexColor(float r, float g, float b)
        => ToHexColor(ColorByte(r), ColorByte(g), ColorByte(b));

    private static string ToHexColor(int r, int g, int b)
        => $"#{System.Math.Clamp(r, 0, 255):X2}{System.Math.Clamp(g, 0, 255):X2}{System.Math.Clamp(b, 0, 255):X2}";

    private static int ColorByte(float v)
        => System.Math.Clamp((int)System.Math.Round(Clamp01(v) * 255f), 0, 255);

    private static float Clamp01(float v)
        => float.IsFinite(v) ? System.Math.Clamp(v, 0f, 1f) : 0f;

    private static float NormalizeHue(float hue)
    {
        if (!float.IsFinite(hue))
            return 0f;

        hue %= 360f;
        return hue < 0f ? hue + 360f : hue;
    }

    private static int ToArgb(int r, int g, int b)
        => unchecked((int)0xFF000000) | (System.Math.Clamp(r, 0, 255) << 16) | (System.Math.Clamp(g, 0, 255) << 8) | System.Math.Clamp(b, 0, 255);

    private static bool TryParseHexColor(string? text, out int r, out int g, out int b)
    {
        r = 0;
        g = 0;
        b = 0;

        string value = (text ?? string.Empty).Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
            value = value[1..];

        if (value.Length == 3)
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);

        if (value.Length != 6 ||
            !int.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
        {
            return false;
        }

        r = (rgb >> 16) & 0xFF;
        g = (rgb >> 8) & 0xFF;
        b = rgb & 0xFF;
        return true;
    }

    private readonly record struct ColorPlaneChangedEventArgs(float Saturation, float Value);

    private readonly record struct HueChangedEventArgs(float Hue);

    private sealed class ColorPlaneView : View
    {
        private readonly Paint _paint = new() { AntiAlias = true };
        private readonly Paint _border = new() { AntiAlias = true };
        private readonly Paint _selectorOuter = new() { AntiAlias = true };
        private readonly Paint _selectorInner = new() { AntiAlias = true };
        private readonly float _radius;
        private readonly float _selectorRadius;
        private float _hue;
        private float _saturation;
        private float _value;
        private int _activePointerId = -1;

        public event EventHandler<ColorPlaneChangedEventArgs>? ColorChanged;

        public ColorPlaneView(Context context) : base(context)
        {
            _radius = Dp(context, 8);
            _selectorRadius = Dp(context, 9);
            _border.SetStyle(Paint.Style.Stroke);
            _border.StrokeWidth = Dp(context, 1);
            _border.Color = GetColor(context, Resource.Color.fa_border);
            _selectorOuter.SetStyle(Paint.Style.Stroke);
            _selectorOuter.StrokeWidth = Dp(context, 4);
            _selectorOuter.Color = new Color(unchecked((int)0xCC000000));
            _selectorInner.SetStyle(Paint.Style.Stroke);
            _selectorInner.StrokeWidth = Dp(context, 2);
            _selectorInner.Color = new Color(unchecked((int)0xFFFFFFFF));
        }

        public void SetColor(float hue, float saturation, float value)
        {
            _hue = NormalizeHue(hue);
            _saturation = Clamp01(saturation);
            _value = Clamp01(value);
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            RectF rect = ContentRect();
            if (rect.Width() <= 0f || rect.Height() <= 0f)
                return;

            HsvToRgb(_hue, 1f, 1f, out int hueR, out int hueG, out int hueB);
            _paint.SetShader(new LinearGradient(
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Top,
                new Color(unchecked((int)0xFFFFFFFF)),
                new Color(ToArgb(hueR, hueG, hueB)),
                Shader.TileMode.Clamp!));
            canvas.DrawRoundRect(rect, _radius, _radius, _paint);

            _paint.SetShader(new LinearGradient(
                rect.Left,
                rect.Top,
                rect.Left,
                rect.Bottom,
                new Color(0x00000000),
                new Color(unchecked((int)0xFF000000)),
                Shader.TileMode.Clamp!));
            canvas.DrawRoundRect(rect, _radius, _radius, _paint);
            _paint.SetShader(null);

            canvas.DrawRoundRect(rect, _radius, _radius, _border);

            float x = rect.Left + _saturation * rect.Width();
            float y = rect.Top + (1f - _value) * rect.Height();
            canvas.DrawCircle(x, y, _selectorRadius, _selectorOuter);
            canvas.DrawCircle(x, y, _selectorRadius, _selectorInner);
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null)
                return false;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                {
                    int pointerIndex = AndroidMotionEvents.PreferredPointerIndex(e);
                    if (pointerIndex < 0)
                        return false;

                    _activePointerId = e.GetPointerId(pointerIndex);
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(pointerIndex), e.GetY(pointerIndex));
                    return true;
                }

                case MotionEventActions.PointerDown:
                {
                    int pointerIndex = e.ActionIndex;
                    if (!AndroidMotionEvents.IsPointerStylusOrEraser(e, pointerIndex))
                        return true;

                    _activePointerId = e.GetPointerId(pointerIndex);
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(pointerIndex), e.GetY(pointerIndex));
                    return true;
                }

                case MotionEventActions.Move:
                {
                    if (!AndroidMotionEvents.TryFindPointerIndex(e, _activePointerId, out int pointerIndex))
                        return true;

                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(pointerIndex), e.GetY(pointerIndex));
                    return true;
                }

                case MotionEventActions.PointerUp:
                {
                    int pointerIndex = e.ActionIndex;
                    if (pointerIndex >= 0
                        && pointerIndex < e.PointerCount
                        && e.GetPointerId(pointerIndex) == _activePointerId)
                    {
                        _activePointerId = -1;
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                    }

                    return true;
                }

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    _activePointerId = -1;
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    return true;
                default:
                    return true;
            }
        }

        private RectF ContentRect()
        {
            float inset = _selectorRadius + Dp(Context!, 3);
            return new RectF(inset, inset, Width - inset, Height - inset);
        }

        private void UpdateFromTouch(float x, float y)
        {
            RectF rect = ContentRect();
            if (rect.Width() <= 0f || rect.Height() <= 0f)
                return;

            _saturation = System.Math.Clamp((x - rect.Left) / rect.Width(), 0f, 1f);
            _value = 1f - System.Math.Clamp((y - rect.Top) / rect.Height(), 0f, 1f);
            ColorChanged?.Invoke(this, new ColorPlaneChangedEventArgs(_saturation, _value));
            Invalidate();
        }
    }

    private sealed class HueSliderView : View
    {
        private static readonly float[] HueStops = { 0f, 60f, 120f, 180f, 240f, 300f, 360f };
        private readonly Paint _paint = new() { AntiAlias = true };
        private readonly Paint _border = new() { AntiAlias = true };
        private readonly Paint _handleOuter = new() { AntiAlias = true };
        private readonly Paint _handleInner = new() { AntiAlias = true };
        private readonly float _radius;
        private float _hue;
        private int _activePointerId = -1;

        public event EventHandler<HueChangedEventArgs>? HueChanged;

        public HueSliderView(Context context) : base(context)
        {
            _radius = Dp(context, 8);
            _border.SetStyle(Paint.Style.Stroke);
            _border.StrokeWidth = Dp(context, 1);
            _border.Color = GetColor(context, Resource.Color.fa_border);
            _handleOuter.SetStyle(Paint.Style.Stroke);
            _handleOuter.StrokeWidth = Dp(context, 4);
            _handleOuter.Color = new Color(unchecked((int)0xCC000000));
            _handleInner.SetStyle(Paint.Style.Stroke);
            _handleInner.StrokeWidth = Dp(context, 2);
            _handleInner.Color = new Color(unchecked((int)0xFFFFFFFF));
        }

        public void SetHue(float hue)
        {
            _hue = NormalizeHue(hue);
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            RectF rect = ContentRect();
            if (rect.Width() <= 0f || rect.Height() <= 0f)
                return;

            float segmentWidth = rect.Width() / (HueStops.Length - 1);
            for (int i = 0; i < HueStops.Length - 1; i++)
            {
                HsvToRgb(HueStops[i], 1f, 1f, out int r0, out int g0, out int b0);
                HsvToRgb(HueStops[i + 1], 1f, 1f, out int r1, out int g1, out int b1);
                float left = rect.Left + i * segmentWidth;
                float right = i == HueStops.Length - 2 ? rect.Right : left + segmentWidth;
                _paint.SetShader(new LinearGradient(
                    left,
                    rect.Top,
                    right,
                    rect.Top,
                    new Color(ToArgb(r0, g0, b0)),
                    new Color(ToArgb(r1, g1, b1)),
                    Shader.TileMode.Clamp!));
                canvas.DrawRect(left, rect.Top, right, rect.Bottom, _paint);
            }
            _paint.SetShader(null);

            canvas.DrawRoundRect(rect, _radius, _radius, _border);

            float x = rect.Left + (_hue / 360f) * rect.Width();
            float cy = rect.CenterY();
            float handleRadius = rect.Height() * 0.5f + Dp(Context!, 2);
            canvas.DrawCircle(x, cy, handleRadius, _handleOuter);
            canvas.DrawCircle(x, cy, handleRadius, _handleInner);
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null)
                return false;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                {
                    int pointerIndex = AndroidMotionEvents.PreferredPointerIndex(e);
                    if (pointerIndex < 0)
                        return false;

                    _activePointerId = e.GetPointerId(pointerIndex);
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(pointerIndex));
                    return true;
                }

                case MotionEventActions.PointerDown:
                {
                    int pointerIndex = e.ActionIndex;
                    if (!AndroidMotionEvents.IsPointerStylusOrEraser(e, pointerIndex))
                        return true;

                    _activePointerId = e.GetPointerId(pointerIndex);
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(pointerIndex));
                    return true;
                }

                case MotionEventActions.Move:
                {
                    if (!AndroidMotionEvents.TryFindPointerIndex(e, _activePointerId, out int pointerIndex))
                        return true;

                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(pointerIndex));
                    return true;
                }

                case MotionEventActions.PointerUp:
                {
                    int pointerIndex = e.ActionIndex;
                    if (pointerIndex >= 0
                        && pointerIndex < e.PointerCount
                        && e.GetPointerId(pointerIndex) == _activePointerId)
                    {
                        _activePointerId = -1;
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                    }

                    return true;
                }

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    _activePointerId = -1;
                    Parent?.RequestDisallowInterceptTouchEvent(false);
                    return true;
                default:
                    return true;
            }
        }

        private RectF ContentRect()
        {
            float inset = Dp(Context!, 9);
            return new RectF(inset, Dp(Context!, 7), Width - inset, Height - Dp(Context!, 7));
        }

        private void UpdateFromTouch(float x)
        {
            RectF rect = ContentRect();
            if (rect.Width() <= 0f)
                return;

            _hue = System.Math.Clamp((x - rect.Left) / rect.Width(), 0f, 1f) * 360f;
            HueChanged?.Invoke(this, new HueChangedEventArgs(_hue));
            Invalidate();
        }
    }

    private void AddLabeledToggleRow(Context ctx, ViewGroup parent, string label, string[] labels, int initialIndex, Action<int> save)
    {
        var tv = new TextView(ctx) { Text = label };
        tv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        SetTooltip(ctx, tv, label);
        SetMarginBottom(tv, Dp(ctx, 4));
        parent.AddView(tv);
        AddToggleRow(ctx, parent, labels, initialIndex, save);
    }

    private void AddToggleRow(Context ctx, ViewGroup parent, string[] labels, int initialIndex, Action<int> save)
    {
        var group = new MaterialButtonToggleGroup(ctx);
        group.SingleSelection = true;
        group.SelectionRequired = true;
        bool stackButtons = labels.Length >= 4 && CalculateSheetWidth(ctx) / (ctx.Resources?.DisplayMetrics?.Density ?? 1f) < 460f;
        group.Orientation = stackButtons ? Orientation.Vertical : Orientation.Horizontal;
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 8);
        group.LayoutParameters = lp;

        var ids = new int[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            var btn = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
            {
                Text = labels[i],
            };
            btn.Id = View.GenerateViewId();
            btn.ContentDescription = labels[i];
            SetTooltip(ctx, btn, labels[i]);
            ids[i] = btn.Id;
            if (labels.Length >= 4)
            {
                btn.SetMinWidth(0);
                btn.SetSingleLine(true);
                btn.SetTextSize(ComplexUnitType.Px, Dp(ctx, 13));
                btn.SetPadding(Dp(ctx, 8), 0, Dp(ctx, 8), 0);
            }

            var blp = stackButtons
                ? new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                : new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            btn.LayoutParameters = blp;
            group.AddView(btn);
        }
        group.Check(ids[System.Math.Clamp(initialIndex, 0, ids.Length - 1)]);
        group.AddOnButtonCheckedListener(new ToggleListener(ids, save, NotifySettingsChanged));
        parent.AddView(group);
    }

    private void AddLogPanel(Context ctx, ViewGroup parent)
    {
        _logStatus = new TextView(ctx)
        {
            Text = "Expand Diagnostics to start the live app log feed.",
        };
        _logStatus.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        _logStatus.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        SetMarginBottom(_logStatus, Dp(ctx, 8));
        parent.AddView(_logStatus);

        var controls = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
        };
        controls.SetGravity(GravityFlags.CenterVertical);
        SetMarginBottom(controls, Dp(ctx, 8));

        _logLiveButton = CreateLogButton(ctx, "Start", "Start live logs");
        _logLiveButton.Click += (_, _) =>
        {
            if (_logFeed?.IsRunning == true)
                StopLogFeed();
            else
                StartLogFeed(ctx);
        };
        controls.AddView(_logLiveButton, LogButtonLayout(ctx, first: true));

        MaterialButton refresh = CreateLogButton(ctx, "Refresh", "Refresh visible logs");
        refresh.Click += (_, _) => UpdateLogPanelText(scrollToBottom: true);
        controls.AddView(refresh, LogButtonLayout(ctx, first: false));

        MaterialButton copy = CreateLogButton(ctx, "Copy", "Copy visible logs");
        copy.Click += (_, _) => CopyVisibleLogs(ctx);
        controls.AddView(copy, LogButtonLayout(ctx, first: false));

        MaterialButton clear = CreateLogButton(ctx, "Clear", "Clear visible logs");
        clear.Click += (_, _) =>
        {
            _logFeed?.Clear();
            UpdateLogPanelText(scrollToBottom: false);
        };
        controls.AddView(clear, LogButtonLayout(ctx, first: false));

        parent.AddView(controls, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            LinearLayout.LayoutParams.WrapContent));

        _logScroll = new ScrollView(ctx)
        {
            FillViewport = true,
            Background = CreateLogPanelBackground(ctx),
            HorizontalScrollBarEnabled = false,
        };
        _logScroll.Focusable = false;
        _logScroll.FocusableInTouchMode = false;
        _logScroll.SetPadding(Dp(ctx, 10), Dp(ctx, 8), Dp(ctx, 10), Dp(ctx, 8));

        _logHorizontalScroll = new HorizontalScrollView(ctx)
        {
            FillViewport = true,
            HorizontalScrollBarEnabled = true,
        };
        _logHorizontalScroll.Focusable = false;
        _logHorizontalScroll.FocusableInTouchMode = false;
        _logHorizontalScroll.OverScrollMode = OverScrollMode.IfContentScrolls;

        _logText = new TextView(ctx)
        {
            Text = "(no logs yet)",
        };
        _logText.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        _logText.SetTextSize(ComplexUnitType.Px, Dp(ctx, 11));
        _logText.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        _logText.Focusable = false;
        _logText.FocusableInTouchMode = false;
        _logText.SetSingleLine(false);
        _logText.SetHorizontallyScrolling(true);
        _logHorizontalScroll.AddView(_logText, new HorizontalScrollView.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent));
        _logScroll.AddView(_logHorizontalScroll, new ScrollView.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        parent.AddView(_logScroll, new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            Dp(ctx, 220)));

        AddSubtle(ctx, parent, "Shows logcat entries for this app process only. Use Copy before closing Settings if you want to keep the visible log.");
    }

    private MaterialButton CreateLogButton(Context ctx, string text, string tooltip)
    {
        var button = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = text,
            ContentDescription = tooltip,
        };
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
        button.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        button.SetPadding(Dp(ctx, 8), 0, Dp(ctx, 8), 0);
        SetTooltip(ctx, button, tooltip);
        return button;
    }

    private static LinearLayout.LayoutParams LogButtonLayout(Context ctx, bool first)
    {
        var lp = new LinearLayout.LayoutParams(0, Dp(ctx, 34), 1f);
        if (!first)
            lp.LeftMargin = Dp(ctx, 6);
        return lp;
    }

    private static GradientDrawable CreateLogPanelBackground(Context ctx)
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(GetColor(ctx, Resource.Color.fa_app_background));
        background.SetCornerRadius(Dp(ctx, 6));
        background.SetStroke(Dp(ctx, 1), GetColor(ctx, Resource.Color.fa_border));
        return background;
    }

    private void OnLogPanelExpandedChanged(Context ctx, bool expanded)
    {
        _logPanelExpanded = expanded;
        if (expanded)
            StartLogFeed(ctx);
        else
            StopLogFeed();
    }

    private void StartLogFeed(Context ctx)
    {
        if (_disposed)
            return;

        _logFeed ??= new AndroidLogcatFeed();
        _logFeed.Start();
        UpdateLogPanelText(scrollToBottom: true);
        ScheduleLogRefresh();
    }

    private void StopLogFeed()
    {
        _logFeed?.Stop();
        UpdateLogPanelText(scrollToBottom: false);
    }

    private void DisposeLogFeed()
    {
        _logRefreshHandler.RemoveCallbacksAndMessages(null);
        _logRefreshQueued = false;
        _logPanelExpanded = false;
        _logFeed?.Dispose();
        _logFeed = null;
        _logScroll = null;
        _logHorizontalScroll = null;
        _logStatus = null;
        _logText = null;
        _logLiveButton = null;
    }

    private void ScheduleLogRefresh()
    {
        if (_disposed || !_logPanelExpanded || _logRefreshQueued)
            return;

        _logRefreshQueued = true;
        _logRefreshHandler.PostDelayed(() =>
        {
            _logRefreshQueued = false;
            if (_disposed || !_logPanelExpanded)
                return;

            UpdateLogPanelText(scrollToBottom: true);
            if (_logFeed?.IsRunning == true)
                ScheduleLogRefresh();
        }, 500);
    }

    private void UpdateLogPanelText(bool scrollToBottom)
    {
        NestedScrollView? settingsScroll = FindSettingsScrollForLogPanel();
        int settingsScrollX = settingsScroll?.ScrollX ?? 0;
        int settingsScrollY = settingsScroll?.ScrollY ?? 0;
        AndroidLogcatFeed? feed = _logFeed;
        bool running = feed?.IsRunning == true;

        if (_logLiveButton is not null)
            _logLiveButton.Text = running ? "Pause" : "Start";

        if (_logStatus is not null)
        {
            string statusText = feed is null
                ? "Expand Diagnostics to start the live app log feed."
                : running
                    ? "Live log feed running."
                    : !string.IsNullOrWhiteSpace(feed.LastError)
                        ? "Log feed stopped: " + feed.LastError
                        : "Live log feed paused.";
            if (!string.Equals(_logStatus.Text?.ToString(), statusText, StringComparison.Ordinal))
                _logStatus.Text = statusText;
        }

        string text = feed?.SnapshotText() ?? string.Empty;
        string visibleText = string.IsNullOrWhiteSpace(text) ? "(no logs yet)" : text;
        if (_logText is not null)
        {
            if (!string.Equals(_logText.Text?.ToString(), visibleText, StringComparison.Ordinal))
                _logText.Text = visibleText;
        }

        if (settingsScroll is not null)
        {
            settingsScroll.ScrollTo(settingsScrollX, settingsScrollY);
            settingsScroll.Post(() => settingsScroll.ScrollTo(settingsScrollX, settingsScrollY));
        }

        if (scrollToBottom && _logScroll is not null)
            _logScroll.Post(ScrollLogPanelToBottom);
    }

    private NestedScrollView? FindSettingsScrollForLogPanel()
    {
        View? view = _logScroll;
        while (view is not null)
        {
            if (view is NestedScrollView nested)
                return nested;
            view = view.Parent as View;
        }

        return null;
    }

    private void ScrollLogPanelToBottom()
    {
        if (_logScroll is null)
            return;

        NestedScrollView? settingsScroll = FindSettingsScrollForLogPanel();
        int settingsScrollX = settingsScroll?.ScrollX ?? 0;
        int settingsScrollY = settingsScroll?.ScrollY ?? 0;
        View? content = _logScroll.ChildCount > 0 ? _logScroll.GetChildAt(0) : null;
        int maxScrollY = System.Math.Max(0, (content?.Height ?? 0) - _logScroll.Height);
        _logScroll.ScrollTo(0, maxScrollY);
        _logHorizontalScroll?.ScrollTo(0, 0);
        if (settingsScroll is not null)
            settingsScroll.Post(() => settingsScroll.ScrollTo(settingsScrollX, settingsScrollY));
    }

    private void CopyVisibleLogs(Context ctx)
    {
        string text = _logFeed?.SnapshotText() ?? _logText?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "(no logs yet)", StringComparison.Ordinal))
        {
            Toast.MakeText(ctx, "No logs to copy", ToastLength.Short)?.Show();
            return;
        }

        if (ctx.GetSystemService(Context.ClipboardService) is global::Android.Content.ClipboardManager clipboard)
        {
            clipboard.PrimaryClip = ClipData.NewPlainText("Fabrication Assistant logs", text);
            Toast.MakeText(ctx, "Logs copied", ToastLength.Short)?.Show();
        }
    }

    private void NotifySettingsChanged()
    {
        if (Activity is { IsDestroyed: true })
            return;

        OnSettingsChanged?.Invoke();
    }

    private void SetTooltip(Context ctx, View? view, string? text, bool useLongClick = true)
    {
        // Tooltips were removed everywhere except the top/left/bottom toolbars.
        // Keep the accessibility label so screen readers still describe the control.
        if (view is null || string.IsNullOrWhiteSpace(text))
            return;

        if (view.ContentDescription is null)
            view.ContentDescription = text;
    }

    private void DisposeDiagnosticsLogTooltips()
    {
        if (_logScroll is not null)
        {
            _logScroll.TooltipText = null;
            _logScroll.ContentDescription = null;
        }

        if (_logHorizontalScroll is not null)
        {
            _logHorizontalScroll.TooltipText = null;
            _logHorizontalScroll.ContentDescription = null;
        }

        if (_logText is not null)
        {
            _logText.TooltipText = null;
            _logText.ContentDescription = null;
        }
    }

    private void AddSubtle(Context ctx, ViewGroup parent, string text)
    {
        var tv = new TextView(ctx) { Text = text };
        tv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        tv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        SetMarginBottom(tv, Dp(ctx, 8));
        parent.AddView(tv);
    }

    private static int Dp(Context ctx, float dp) => (int)(dp * (ctx.Resources?.DisplayMetrics?.Density ?? 1.0f));

    private static void SetMarginBottom(View v, int px)
    {
        if (v.LayoutParameters is ViewGroup.MarginLayoutParams mlp) { mlp.BottomMargin = px; v.LayoutParameters = mlp; return; }
        var lp = new ViewGroup.MarginLayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.BottomMargin = px;
        v.LayoutParameters = lp;
    }

    private static global::Android.Graphics.Color GetColor(Context ctx, int resId)
        => new global::Android.Graphics.Color(ctx.GetColor(resId));

    private sealed class ToggleListener : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        private readonly int[] _buttonIds;
        private readonly Action<int> _save;
        private readonly Action? _settingsChanged;
        public ToggleListener(int[] buttonIds, Action<int> save, Action? settingsChanged)
        {
            _buttonIds = buttonIds;
            _save = save;
            _settingsChanged = settingsChanged;
        }

        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked)
        {
            if (!isChecked) return;
            for (int i = 0; i < _buttonIds.Length; i++)
            {
                if (_buttonIds[i] == checkedId) { _save(i); _settingsChanged?.Invoke(); return; }
            }
        }
    }
}
