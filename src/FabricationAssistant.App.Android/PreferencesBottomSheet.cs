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
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Settings popup launched from the nav-rail gear icon. Built programmatically
/// (rather than via XML) because the surface is large and repetitive: nine
/// sections each with a mix of toggles, sensitivity SeekBars, and color
/// pickers. Every control writes to AppSettings + invokes
/// <see cref="OnSettingsChanged"/> so the host re-applies live without
/// waiting for the sheet to close.
/// </summary>
public sealed class PreferencesBottomSheet : BottomSheetDialogFragment
{
    private const float TabletBreakpointDp = 700f;
    private const float TabletPanelWidthDp = 520f;
    private const float CompactPanelWidthDp = 420f;
    private int _sheetWidthOverridePx;
    private FrameLayout? _resizeHandle;

    public Action? OnSettingsChanged { get; set; }

    public override global::Android.App.Dialog OnCreateDialog(Bundle? savedInstanceState)
    {
        var dialog = (BottomSheetDialog)base.OnCreateDialog(savedInstanceState);
        dialog.Behavior.PeekHeight = (int)(Resources?.DisplayMetrics?.HeightPixels * 0.80f ?? 800);
        dialog.Behavior.FitToContents = false;
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        dialog.Behavior.Hideable = false;
        dialog.Behavior.SkipCollapsed = true;
        dialog.Behavior.Draggable = false;
        return dialog;
    }

    public override void OnStart()
    {
        base.OnStart();
        ApplySheetLayout();
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
        => CreateEmbeddedView(Context!);

    public override void OnDestroy()
    {
        OnSettingsChanged = null;
        base.OnDestroy();
    }

    public View CreateEmbeddedView(Context ctx)
    {
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

        // ── Render mode ────────────────────────────────────────────────
        var modeSection = AddSection(ctx, root, "Render Mode", "Shading style", expandedByDefault: true);
        AddToggleRow(ctx, modeSection, new[] { "Shaded", "Wireframe", "Clay" },
            AppSettings.RenderModeSelectionIndex, AppSettings.SetRenderModeSelectionIndex);

        // ── Camera & helpers ───────────────────────────────────────────
        var helpers = AddSection(ctx, root, "Camera & Helpers", "Grid, helpers, projection");
        AddSwitch(ctx, helpers, "Show ground grid", AppSettings.ShowGrid, v => AppSettings.ShowGrid = v);
        AddSwitch(ctx, helpers, "Push grid to model min", AppSettings.ShiftGridToModelMin, v => AppSettings.ShiftGridToModelMin = v);
        AddSwitch(ctx, helpers, "Automatic grid spacing", AppSettings.UseAutomaticGridSpacing, v => AppSettings.UseAutomaticGridSpacing = v);
        AddFloatSlider(ctx, helpers, "Grid spacing (mm)", 0.001f, 1000f, AppSettings.GridSpacingMm, v =>
        {
            AppSettings.GridSpacingMm = v;
            AppSettings.UseAutomaticGridSpacing = false;
        });
        AddFloatSlider(ctx, helpers, "Grid line thickness", 1f, 8f, AppSettings.GridLineThickness, v => AppSettings.GridLineThickness = v);
        AddRgbRow(ctx, helpers, "Grid color",
            AppSettings.GridLineColorR, AppSettings.GridLineColorG, AppSettings.GridLineColorB,
            r => AppSettings.GridLineColorR = r,
            g => AppSettings.GridLineColorG = g,
            b => AppSettings.GridLineColorB = b);
        AddSwitch(ctx, helpers, "Show axes gizmo", AppSettings.ShowAxes, v => AppSettings.ShowAxes = v);
        AddToggleRow(ctx, helpers, new[] { "Perspective", "Orthographic" },
            AppSettings.IsPerspective ? 0 : 1, idx => AppSettings.IsPerspective = (idx == 0));

        // ── Scene colors ───────────────────────────────────────────────
        var colors = AddSection(ctx, root, "Scene Colors", "Background and surface");
        AddRgbRow(ctx, colors, "Background",
            AppSettings.BackgroundR, AppSettings.BackgroundG, AppSettings.BackgroundB,
            r => AppSettings.BackgroundR = r,
            g => AppSettings.BackgroundG = g,
            b => AppSettings.BackgroundB = b);
        AddRgbRow(ctx, colors, "Surface",
            AppSettings.SurfaceR, AppSettings.SurfaceG, AppSettings.SurfaceB,
            r => AppSettings.SurfaceR = r,
            g => AppSettings.SurfaceG = g,
            b => AppSettings.SurfaceB = b);
        AddFloatSlider(ctx, colors, "Surface opacity", 0f, 1f, AppSettings.SurfaceOpacity, v => AppSettings.SurfaceOpacity = v);

        // ── CAD Edges ──────────────────────────────────────────────────
        var edges = AddSection(ctx, root, "CAD Edges", "Edge lines and tolerances");
        AddSwitch(ctx, edges, "Enable edges", AppSettings.EdgesEnabled, AppSettings.SetEdgesEnabledFromUi);
        AddRgbRow(ctx, edges, "Edge color",
            AppSettings.EdgeR, AppSettings.EdgeG, AppSettings.EdgeB,
            r => AppSettings.EdgeR = r, g => AppSettings.EdgeG = g, b => AppSettings.EdgeB = b);
        AddFloatSlider(ctx, edges, "Width", 0.05f, 2f, AppSettings.EdgeWidth, v => AppSettings.EdgeWidth = v);
        AddFloatSlider(ctx, edges, "Feature angle (deg)", 1f, 150f, AppSettings.CadEdgeFeatureAngleDegrees, v => AppSettings.CadEdgeFeatureAngleDegrees = v);
        AddFloatSlider(ctx, edges, "Coplanar tolerance (deg)", 0f, 30f, AppSettings.CadEdgeCoplanarToleranceDegrees, v => AppSettings.CadEdgeCoplanarToleranceDegrees = v);
        AddFloatSlider(ctx, edges, "Weld tolerance", 1e-6f, 1e-4f, AppSettings.CadEdgeWeldToleranceScale, v => AppSettings.CadEdgeWeldToleranceScale = v);
        AddSwitch(ctx, edges, "Silhouettes", AppSettings.CadEdgeSilhouetteEnabled, v => AppSettings.CadEdgeSilhouetteEnabled = v);
        AddFloatSlider(ctx, edges, "Depth bias", 0f, 0.002f, AppSettings.EdgeDepthBias, v => AppSettings.EdgeDepthBias = v);
        AddFloatSlider(ctx, edges, "Surface offset F", 0f, 4f, AppSettings.SurfaceOffsetFactor, v => AppSettings.SurfaceOffsetFactor = v);
        AddFloatSlider(ctx, edges, "Surface offset U", 0f, 4f, AppSettings.SurfaceOffsetUnits, v => AppSettings.SurfaceOffsetUnits = v);

        // ── Clay ───────────────────────────────────────────────────────
        var clay = AddSection(ctx, root, "Clay Render", "Clay colors");
        AddRgbRow(ctx, clay, "Clay surface",
            AppSettings.ClaySurfaceR, AppSettings.ClaySurfaceG, AppSettings.ClaySurfaceB,
            r => AppSettings.ClaySurfaceR = r, g => AppSettings.ClaySurfaceG = g, b => AppSettings.ClaySurfaceB = b);
        AddRgbRow(ctx, clay, "Clay background",
            AppSettings.ClayBackgroundR, AppSettings.ClayBackgroundG, AppSettings.ClayBackgroundB,
            r => AppSettings.ClayBackgroundR = r, g => AppSettings.ClayBackgroundG = g, b => AppSettings.ClayBackgroundB = b);
        AddSwitch(ctx, clay, "Clay feature edges", AppSettings.ClayFeatureEdgesEnabled, v => AppSettings.ClayFeatureEdgesEnabled = v);
        AddRgbRow(ctx, clay, "Clay edge",
            AppSettings.ClayFeatureEdgeR, AppSettings.ClayFeatureEdgeG, AppSettings.ClayFeatureEdgeB,
            r => AppSettings.ClayFeatureEdgeR = r, g => AppSettings.ClayFeatureEdgeG = g, b => AppSettings.ClayFeatureEdgeB = b);
        AddFloatSlider(ctx, clay, "Clay edge alpha", 0f, 1f, AppSettings.ClayFeatureEdgeA, v => AppSettings.ClayFeatureEdgeA = v);
        AddFloatSlider(ctx, clay, "Clay edge width", 0.05f, 4f, AppSettings.ClayFeatureEdgeWidth, v => AppSettings.ClayFeatureEdgeWidth = v);
        AddFloatSlider(ctx, clay, "Clay edge depth bias", 0f, 0.01f, AppSettings.ClayFeatureEdgeDepthBias, v => AppSettings.ClayFeatureEdgeDepthBias = v);
        AddFloatSlider(ctx, clay, "Clay crease angle", 1f, 150f, AppSettings.ClayFeatureEdgeCreaseAngleDegrees, v => AppSettings.ClayFeatureEdgeCreaseAngleDegrees = v);

        // ── Lighting ───────────────────────────────────────────────────
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

        // ── AA + occlusion ─────────────────────────────────────────────
        var aa = AddSection(ctx, root, "Anti-aliasing & Occlusion", "MSAA, contour, SSAO");
        int msaaIdx = AppSettings.MsaaSamples switch { 0 => 0, 2 => 1, _ => 2 };
        AddToggleRow(ctx, aa, new[] { "Off", "2x", "4x" }, msaaIdx,
            idx => AppSettings.MsaaSamples = idx switch { 0 => 0, 1 => 2, _ => 4 });
        AddSubtle(ctx, aa, "MSAA applies immediately.");
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
        AddFloatSlider(ctx, aa, "AO noise scale", 0.25f, 8f, AppSettings.AoNoiseScale, v => AppSettings.AoNoiseScale = v);

        // ── Selection ──────────────────────────────────────────────────
        var sel = AddSection(ctx, root, "Selection", "Highlight and outline");
        AddSwitch(ctx, sel, "Highlight selected body", AppSettings.ShowSelectionHighlight, v => AppSettings.ShowSelectionHighlight = v);
        AddSwitch(ctx, sel, "Outline selected body", AppSettings.OutlineEnabled, v => AppSettings.OutlineEnabled = v);
        AddRgbRow(ctx, sel, "Outline color",
            AppSettings.OutlineR, AppSettings.OutlineG, AppSettings.OutlineB,
            r => AppSettings.OutlineR = r, g => AppSettings.OutlineG = g, b => AppSettings.OutlineB = b);
        AddFloatSlider(ctx, sel, "Outline thickness", 1f, 8f, AppSettings.OutlineThicknessPx, v => AppSettings.OutlineThicknessPx = v);
        AddRgbRow(ctx, sel, "Hover outline",
            AppSettings.HoverOutlineR, AppSettings.HoverOutlineG, AppSettings.HoverOutlineB,
            r => AppSettings.HoverOutlineR = r, g => AppSettings.HoverOutlineG = g, b => AppSettings.HoverOutlineB = b);
        AddFloatSlider(ctx, sel, "Hover thickness", 0.05f, 8f, AppSettings.HoverOutlineThicknessPx, v => AppSettings.HoverOutlineThicknessPx = v);
        AddFloatSlider(ctx, sel, "Hover tint", 0f, 1f, AppSettings.HoverTintStrength, v => AppSettings.HoverTintStrength = v);
        AddRgbRow(ctx, sel, "Dimension highlight",
            AppSettings.DimensionHighlightR, AppSettings.DimensionHighlightG, AppSettings.DimensionHighlightB,
            r => AppSettings.DimensionHighlightR = r,
            g => AppSettings.DimensionHighlightG = g,
            b => AppSettings.DimensionHighlightB = b);

        // Measurement tools
        var measure = AddSection(ctx, root, "Measurement Tools", "Dimensions and boxes");
        AddLabeledToggleRow(ctx, measure, "Default measure tool", new[] { "Point", "Face-Point", "Face-Face" },
            AppSettings.MeasureModeSelectionIndex, idx => AppSettings.MeasureModeSelectionIndex = idx);
        AddLabeledToggleRow(ctx, measure, "Box mode", new[] { "Axis", "Best Fit" },
            AppSettings.MeasureBoxModeSelectionIndex, idx => AppSettings.MeasureBoxModeSelectionIndex = idx);
        AddFloatSlider(ctx, measure, "Dimension text scale", 0.5f, 4f, AppSettings.DimensionTextScale, v => AppSettings.DimensionTextScale = v);
        AddSwitch(ctx, measure, "Multi-measure", AppSettings.MeasureMultiMeasureEnabled, v => AppSettings.MeasureMultiMeasureEnabled = v);
        AddSwitch(ctx, measure, "Point delta breakdown", AppSettings.MeasureShowDeltaBreakdown, v => AppSettings.MeasureShowDeltaBreakdown = v);

        // Section tools
        var sections = AddSection(ctx, root, "Section Tools", "Caps, planes, and gizmo");
        AddSwitch(ctx, sections, "Show section fill", AppSettings.SectionFillVisible, v => AppSettings.SectionFillVisible = v);
        AddSwitch(ctx, sections, "Show section edges", AppSettings.SectionEdgesVisible, v => AppSettings.SectionEdgesVisible = v);
        AddRgbRow(ctx, sections, "Plane color",
            AppSettings.SectionPlaneR, AppSettings.SectionPlaneG, AppSettings.SectionPlaneB,
            r => AppSettings.SectionPlaneR = r,
            g => AppSettings.SectionPlaneG = g,
            b => AppSettings.SectionPlaneB = b);
        AddFloatSlider(ctx, sections, "Plane opacity", 0f, 1f, AppSettings.SectionPlaneOpacity, v => AppSettings.SectionPlaneOpacity = v);
        AddRgbRow(ctx, sections, "Edge color",
            AppSettings.SectionEdgeR, AppSettings.SectionEdgeG, AppSettings.SectionEdgeB,
            r => AppSettings.SectionEdgeR = r,
            g => AppSettings.SectionEdgeG = g,
            b => AppSettings.SectionEdgeB = b);
        AddRgbRow(ctx, sections, "Selected edge",
            AppSettings.SectionEdgeHighlightR, AppSettings.SectionEdgeHighlightG, AppSettings.SectionEdgeHighlightB,
            r => AppSettings.SectionEdgeHighlightR = r,
            g => AppSettings.SectionEdgeHighlightG = g,
            b => AppSettings.SectionEdgeHighlightB = b);
        AddRgbRow(ctx, sections, "Cap color",
            AppSettings.SectionCapR, AppSettings.SectionCapG, AppSettings.SectionCapB,
            r => AppSettings.SectionCapR = r,
            g => AppSettings.SectionCapG = g,
            b => AppSettings.SectionCapB = b);
        AddFloatSlider(ctx, sections, "Plane size", 0.005f, 0.20f, AppSettings.SectionPlaneSizeFraction, v => AppSettings.SectionPlaneSizeFraction = v);

        // ── Navigation ─────────────────────────────────────────────────
        var nav = AddSection(ctx, root, "Navigation", "Orbit, pan, zoom");
        AddSwitch(ctx, nav, "Lightweight camera navigation", AppSettings.LightweightNavigationEnabled, v => AppSettings.LightweightNavigationEnabled = v);
        AddFloatSlider(ctx, nav, "Section gizmo scale", 0.5f, 4f, AppSettings.SectionGizmoScale, v => AppSettings.SectionGizmoScale = v);
        AddFloatSlider(ctx, nav, "Orbit sensitivity", 0.1f, 5f, AppSettings.OrbitSensitivity, v => AppSettings.OrbitSensitivity = v);
        AddFloatSlider(ctx, nav, "Pan sensitivity", 0.1f, 5f, AppSettings.PanSensitivity, v => AppSettings.PanSensitivity = v);
        AddFloatSlider(ctx, nav, "Pinch-zoom sensitivity", 0.1f, 5f, AppSettings.ZoomSensitivity, v => AppSettings.ZoomSensitivity = v);

        AddSubtle(ctx, root, "Rendering controls apply live.");

        scroll.AddView(root);
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
        parent.RemoveViewAt(index);
        View replacement = CreateEmbeddedView(ctx);
        parent.AddView(replacement, index, layoutParams);
        replacement.Post(() => ReanchorSheetAfterContentChange(replacement));
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

    // ── Builder helpers ────────────────────────────────────────────────

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
        reset.Click += (_, _) => resetToDefaults();
        row.AddView(reset);

        parent.AddView(row);
    }

    private LinearLayout AddSection(
        Context ctx,
        ViewGroup parent,
        string title,
        string summary,
        bool expandedByDefault = false)
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
        arrow.ContentDescription = title;
        arrow.LayoutParameters = new LinearLayout.LayoutParams(Dp(ctx, 24), Dp(ctx, 24));

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

    private void AddSwitch(Context ctx, ViewGroup parent, string label, bool initial, Action<bool> save)
    {
        var sw = new MaterialSwitch(ctx) { Text = label, Checked = initial };
        sw.ContentDescription = label;
        sw.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        sw.LayoutParameters = lp;
        sw.CheckedChange += (_, e) => { save(e.IsChecked); NotifySettingsChanged(); };
        parent.AddView(sw);
    }

    private void AddFloatSlider(Context ctx, ViewGroup parent, string label, float min, float max, float initial, Action<float> save)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        row.LayoutParameters = lp;

        var labelTv = new TextView(ctx) { Text = FormatSliderValue(label, initial) };
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));

        var seek = new SeekBar(ctx);
        seek.Max = 1000;
        int progressInit = System.Math.Clamp((int)System.Math.Round((initial - min) / (max - min) * 1000f), 0, 1000);
        seek.Progress = progressInit;
        seek.ProgressChanged += (_, e) =>
        {
            float v = min + e.Progress / 1000f * (max - min);
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

        var seek = new SeekBar(ctx);
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
        Action<float> saveR, Action<float> saveG, Action<float> saveB)
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

        var labelTv = new TextView(ctx) { Text = label };
        labelTv.LayoutParameters = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f);
        labelTv.Ellipsize = TextUtils.TruncateAt.End;
        labelTv.SetSingleLine(true);
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        row.AddView(labelTv);

        var swatch = new View(ctx);
        var swatchLp = new LinearLayout.LayoutParams(Dp(ctx, 42), Dp(ctx, 28));
        swatchLp.LeftMargin = Dp(ctx, 8);
        swatchLp.RightMargin = Dp(ctx, 10);
        swatch.LayoutParameters = swatchLp;
        row.AddView(swatch);

        var hexTv = new TextView(ctx);
        hexTv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        hexTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        hexTv.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        hexTv.SetMinWidth(Dp(ctx, 72));
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
            saveR(currentR);
            saveG(currentG);
            saveB(currentB);
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
        var previewLp = new LinearLayout.LayoutParams(Dp(ctx, 74), Dp(ctx, 44));
        previewLp.RightMargin = Dp(ctx, 12);
        valueRow.AddView(preview, previewLp);

        var hexInput = new EditText(ctx)
        {
            Text = ToHexColor(currentR, currentG, currentB),
            InputType = InputTypes.ClassText | InputTypes.TextFlagCapCharacters | InputTypes.TextFlagNoSuggestions,
        };
        hexInput.SetSingleLine(true);
        hexInput.SetSelectAllOnFocus(true);
        hexInput.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        hexInput.SetHintTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        hexInput.SetTypeface(global::Android.Graphics.Typeface.Monospace, global::Android.Graphics.TypefaceStyle.Normal);
        hexInput.SetPadding(Dp(ctx, 12), 0, Dp(ctx, 12), 0);
        hexInput.Background = CreateColorInputBackground(ctx);
        valueRow.AddView(hexInput, new LinearLayout.LayoutParams(0, Dp(ctx, 44), 1f));

        var colorPlane = new ColorPlaneView(ctx);
        colorPlane.SetColor(hue, saturation, value);
        var planeLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, Dp(ctx, 212));
        planeLp.BottomMargin = Dp(ctx, 12);
        root.AddView(colorPlane, planeLp);

        var hueSlider = new HueSliderView(ctx);
        hueSlider.SetHue(hue);
        var hueLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, Dp(ctx, 34));
        hueLp.BottomMargin = Dp(ctx, 10);
        root.AddView(hueSlider, hueLp);

        var detail = new TextView(ctx);
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
        dialog.Window?.SetBackgroundDrawable(CreateColorDialogWindowBackground(ctx));

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
        }

        Button? negative = dialog.GetButton((int)global::Android.Content.DialogButtonType.Negative);
        negative?.SetTextColor(GetColor(ctx, Resource.Color.fa_accent_500));
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
        swatch.Background = drawable;
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
                case MotionEventActions.Move:
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX(), e.GetY());
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
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
                case MotionEventActions.Move:
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    UpdateFromTouch(e.GetX());
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
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

    private void NotifySettingsChanged()
    {
        if (Activity is { IsDestroyed: true })
            return;

        OnSettingsChanged?.Invoke();
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
