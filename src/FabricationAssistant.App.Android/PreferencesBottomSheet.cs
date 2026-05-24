using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.MaterialSwitch;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Settings popup launched from the nav-rail gear icon. Built programmatically
/// (rather than via XML) because the surface is large and repetitive: nine
/// sections each with a mix of toggles, sensitivity SeekBars, and 3-channel
/// RGB groups. Every control writes to AppSettings + invokes
/// <see cref="OnSettingsChanged"/> so the host re-applies live without
/// waiting for the sheet to close.
/// </summary>
public sealed class PreferencesBottomSheet : BottomSheetDialogFragment
{
    public Action? OnSettingsChanged { get; set; }

    public override global::Android.App.Dialog OnCreateDialog(Bundle? savedInstanceState)
    {
        var dialog = (BottomSheetDialog)base.OnCreateDialog(savedInstanceState);
        dialog.Behavior.PeekHeight = (int)(Resources?.DisplayMetrics?.HeightPixels * 0.80f ?? 800);
        dialog.Behavior.State = BottomSheetBehavior.StateExpanded;
        dialog.Behavior.SkipCollapsed = true;
        return dialog;
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        AppSettings.Initialize(Context!);
        var ctx = Context!;
        float density = Resources!.DisplayMetrics!.Density;
        int pad = Dp(ctx, 16);

        var scroll = new NestedScrollView(ctx)
        {
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
        };
        scroll.SetBackgroundResource(Resource.Color.md_theme_surface);
        scroll.SetPadding(pad, pad, pad, pad);

        var root = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent),
        };

        AddHeader(ctx, root, "Settings", Dp(ctx, 22));

        // ── Render mode ────────────────────────────────────────────────
        var modeSection = AddSection(ctx, root, "Render Mode");
        AddToggleRow(ctx, modeSection, new[] { "Shaded+Edges", "Shaded", "Wireframe", "Clay" },
            AppSettings.RenderMode, idx => AppSettings.RenderMode = idx);

        // ── Camera & helpers ───────────────────────────────────────────
        var helpers = AddSection(ctx, root, "Camera & Helpers");
        AddSwitch(ctx, helpers, "Show ground grid", AppSettings.ShowGrid, v => AppSettings.ShowGrid = v);
        AddSwitch(ctx, helpers, "Push grid to model min", AppSettings.ShiftGridToModelMin, v => AppSettings.ShiftGridToModelMin = v);
        AddSwitch(ctx, helpers, "Automatic grid spacing", AppSettings.UseAutomaticGridSpacing, v => AppSettings.UseAutomaticGridSpacing = v);
        AddFloatSlider(ctx, helpers, "Grid spacing (mm)", 1f, 500f, AppSettings.GridSpacingMm, v => AppSettings.GridSpacingMm = v);
        AddFloatSlider(ctx, helpers, "Grid line thickness", 1f, 8f, AppSettings.GridLineThickness, v => AppSettings.GridLineThickness = v);
        AddRgbRow(ctx, helpers, "Grid color",
            AppSettings.GridLineColorR, AppSettings.GridLineColorG, AppSettings.GridLineColorB,
            r => AppSettings.GridLineColorR = r,
            g => AppSettings.GridLineColorG = g,
            b => AppSettings.GridLineColorB = b);
        AddSwitch(ctx, helpers, "Show axes gizmo", AppSettings.ShowAxes, v => AppSettings.ShowAxes = v);
        AddSwitch(ctx, helpers, "Show view cube", AppSettings.ShowViewCube, v => AppSettings.ShowViewCube = v);
        AddToggleRow(ctx, helpers, new[] { "Perspective", "Orthographic" },
            AppSettings.IsPerspective ? 0 : 1, idx => AppSettings.IsPerspective = (idx == 0));

        // ── Scene colors ───────────────────────────────────────────────
        var colors = AddSection(ctx, root, "Scene Colors");
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
        var edges = AddSection(ctx, root, "CAD Edges");
        AddSwitch(ctx, edges, "Enable edges", AppSettings.EdgesEnabled, v => AppSettings.EdgesEnabled = v);
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
        var clay = AddSection(ctx, root, "Clay Render");
        AddRgbRow(ctx, clay, "Clay surface",
            AppSettings.ClaySurfaceR, AppSettings.ClaySurfaceG, AppSettings.ClaySurfaceB,
            r => AppSettings.ClaySurfaceR = r, g => AppSettings.ClaySurfaceG = g, b => AppSettings.ClaySurfaceB = b);
        AddRgbRow(ctx, clay, "Clay background",
            AppSettings.ClayBackgroundR, AppSettings.ClayBackgroundG, AppSettings.ClayBackgroundB,
            r => AppSettings.ClayBackgroundR = r, g => AppSettings.ClayBackgroundG = g, b => AppSettings.ClayBackgroundB = b);

        // ── Lighting ───────────────────────────────────────────────────
        var lighting = AddSection(ctx, root, "Lighting");
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
        var aa = AddSection(ctx, root, "Anti-aliasing & Occlusion");
        int msaaIdx = AppSettings.MsaaSamples switch { 0 => 0, 2 => 1, _ => 2 };
        AddToggleRow(ctx, aa, new[] { "Off", "2x", "4x" }, msaaIdx,
            idx => AppSettings.MsaaSamples = idx switch { 0 => 0, 1 => 2, _ => 4 });
        AddSubtle(ctx, aa, "MSAA changes apply on next app launch.");
        AddFloatSlider(ctx, aa, "Contour strength", 0f, 1.2f, AppSettings.ContourStrength, v => AppSettings.ContourStrength = v);
        AddFloatSlider(ctx, aa, "Contour falloff", 0.5f, 6f, AppSettings.ContourPower, v => AppSettings.ContourPower = v);
        AddSwitch(ctx, aa, "Contact shadows (SSAO)", AppSettings.AmbientOcclusionEnabled, v => AppSettings.AmbientOcclusionEnabled = v);
        AddFloatSlider(ctx, aa, "AO radius", 0.01f, 2.0f, AppSettings.AoRadius, v => AppSettings.AoRadius = v);
        AddFloatSlider(ctx, aa, "AO bias", 0.0f, 0.2f, AppSettings.AoBias, v => AppSettings.AoBias = v);
        AddFloatSlider(ctx, aa, "AO intensity", 0f, 3f, AppSettings.AoIntensity, v => AppSettings.AoIntensity = v);
        AddIntSlider(ctx, aa, "AO blur passes", 0, 8, AppSettings.AoBlurPasses, v => AppSettings.AoBlurPasses = v);

        // ── Selection ──────────────────────────────────────────────────
        var sel = AddSection(ctx, root, "Selection");
        AddSwitch(ctx, sel, "Highlight selected body", AppSettings.ShowSelectionHighlight, v => AppSettings.ShowSelectionHighlight = v);
        AddSwitch(ctx, sel, "Outline selected body", AppSettings.OutlineEnabled, v => AppSettings.OutlineEnabled = v);
        AddRgbRow(ctx, sel, "Outline color",
            AppSettings.OutlineR, AppSettings.OutlineG, AppSettings.OutlineB,
            r => AppSettings.OutlineR = r, g => AppSettings.OutlineG = g, b => AppSettings.OutlineB = b);
        AddFloatSlider(ctx, sel, "Outline thickness", 1f, 8f, AppSettings.OutlineThicknessPx, v => AppSettings.OutlineThicknessPx = v);

        // ── Navigation ─────────────────────────────────────────────────
        var nav = AddSection(ctx, root, "Navigation");
        AddFloatSlider(ctx, nav, "Orbit sensitivity", 0.1f, 5f, AppSettings.OrbitSensitivity, v => AppSettings.OrbitSensitivity = v);
        AddFloatSlider(ctx, nav, "Pan sensitivity", 0.1f, 5f, AppSettings.PanSensitivity, v => AppSettings.PanSensitivity = v);
        AddFloatSlider(ctx, nav, "Pinch-zoom sensitivity", 0.1f, 5f, AppSettings.ZoomSensitivity, v => AppSettings.ZoomSensitivity = v);

        AddSubtle(ctx, root, "Rendering controls apply live except MSAA, which is selected when the app starts.");

        scroll.AddView(root);
        return scroll;
    }

    // ── Builder helpers ────────────────────────────────────────────────

    private void AddHeader(Context ctx, ViewGroup parent, string text, int sizePx)
    {
        var tv = new TextView(ctx) { Text = text };
        tv.SetTextSize(ComplexUnitType.Px, sizePx);
        tv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        tv.SetTypeface(tv.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
        SetMarginBottom(tv, Dp(ctx, 12));
        parent.AddView(tv);
    }

    private LinearLayout AddSection(Context ctx, ViewGroup parent, string title)
    {
        var card = new MaterialCardView(ctx)
        {
            LayoutParameters = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent),
        };
        ((LinearLayout.LayoutParams)card.LayoutParameters!).BottomMargin = Dp(ctx, 12);
        card.SetCardBackgroundColor(GetColor(ctx, Resource.Color.md_theme_surfaceVariant));
        card.Radius = Dp(ctx, 12);
        card.StrokeWidth = 0;
        card.Elevation = 0f;

        var inner = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        inner.SetPadding(Dp(ctx, 16), Dp(ctx, 16), Dp(ctx, 16), Dp(ctx, 16));

        var titleTv = new TextView(ctx) { Text = title };
        titleTv.SetTextSize(ComplexUnitType.Px, Dp(ctx, 15));
        titleTv.SetTypeface(titleTv.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
        titleTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        SetMarginBottom(titleTv, Dp(ctx, 8));
        inner.AddView(titleTv);

        card.AddView(inner);
        parent.AddView(card);
        return inner;
    }

    private void AddSwitch(Context ctx, ViewGroup parent, string label, bool initial, Action<bool> save)
    {
        var sw = new MaterialSwitch(ctx) { Text = label, Checked = initial };
        sw.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        sw.LayoutParameters = lp;
        sw.CheckedChange += (_, e) => { save(e.IsChecked); OnSettingsChanged?.Invoke(); };
        parent.AddView(sw);
    }

    private void AddFloatSlider(Context ctx, ViewGroup parent, string label, float min, float max, float initial, Action<float> save)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 6);
        row.LayoutParameters = lp;

        var labelTv = new TextView(ctx) { Text = $"{label}: {initial:G3}" };
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));

        var seek = new SeekBar(ctx);
        seek.Max = 1000;
        int progressInit = System.Math.Clamp((int)System.Math.Round((initial - min) / (max - min) * 1000f), 0, 1000);
        seek.Progress = progressInit;
        seek.ProgressChanged += (_, e) =>
        {
            float v = min + e.Progress / 1000f * (max - min);
            labelTv.Text = $"{label}: {v:G3}";
            if (e.FromUser)
            {
                save(v);
                OnSettingsChanged?.Invoke();
            }
        };

        row.AddView(labelTv);
        row.AddView(seek);
        parent.AddView(row);
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
            if (e.FromUser) { save(v); OnSettingsChanged?.Invoke(); }
        };

        row.AddView(labelTv);
        row.AddView(seek);
        parent.AddView(row);
    }

    private void AddRgbRow(Context ctx, ViewGroup parent, string label, float r0, float g0, float b0,
        Action<float> saveR, Action<float> saveG, Action<float> saveB)
    {
        var row = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        var lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent);
        lp.BottomMargin = Dp(ctx, 8);
        row.LayoutParameters = lp;

        var labelTv = new TextView(ctx) { Text = label };
        labelTv.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        SetMarginBottom(labelTv, Dp(ctx, 2));
        row.AddView(labelTv);

        AddFloatSlider(ctx, row, "R", 0f, 1f, r0, saveR);
        AddFloatSlider(ctx, row, "G", 0f, 1f, g0, saveG);
        AddFloatSlider(ctx, row, "B", 0f, 1f, b0, saveB);

        parent.AddView(row);
    }

    private void AddToggleRow(Context ctx, ViewGroup parent, string[] labels, int initialIndex, Action<int> save)
    {
        var group = new MaterialButtonToggleGroup(ctx);
        group.SingleSelection = true;
        group.SelectionRequired = true;
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
            var blp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            btn.LayoutParameters = blp;
            group.AddView(btn);
        }
        group.Check(ids[System.Math.Clamp(initialIndex, 0, ids.Length - 1)]);
        group.AddOnButtonCheckedListener(new ToggleListener(ids, save, OnSettingsChanged));
        parent.AddView(group);
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
