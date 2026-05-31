using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Card;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace FabricationAssistant.App.Android;

public sealed class RecentFilesBottomSheet : BottomSheetDialogFragment, IDisposable
{
    private const float TabletBreakpointDp = 700f;
    private const float TabletPanelWidthDp = 260f;
    private const float CompactPanelWidthDp = 240f;
    private int _sheetWidthOverridePx;
    private FrameLayout? _resizeHandle;
    private LinearLayout? _contentRoot;
    private readonly List<MaterialCardView> _recentCards = [];
    private bool _disposed;
    private int _refreshGeneration;

    public Action<RecentFileEntry>? OnRecentFileSelected { get; set; }

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
        if (Context is { } ctx)
            RefreshContent(ctx);
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

    public View CreateEmbeddedView(Context ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recentCards.Clear();

        int pad = Dp(ctx, 16);

        var scroll = new NestedScrollView(ctx)
        {
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
            FillViewport = true,
        };
        scroll.SetBackgroundResource(Resource.Color.fa_app_background);
        scroll.SetPadding(pad, pad, pad, pad);

        var root = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent),
        };
        _contentRoot = root;

        RefreshContent(ctx);

        scroll.AddView(root);
        return scroll;
    }

    private void RefreshContent(Context ctx)
    {
        LinearLayout? root = _contentRoot;
        if (root is null)
            return;

        foreach (MaterialCardView card in _recentCards)
            card.SetOnClickListener(null);
        _recentCards.Clear();
        root.RemoveAllViews();
        AddHeader(ctx, root);

        // RecentFilesStore.Load probes each entry's access via a ContentResolver
        // binder round-trip (up to a 5s timeout per offline/cloud URI), so it can
        // block for many seconds and ANR if run on the UI thread. Load off-thread
        // and post the rows back; the panel shows its header immediately. A
        // generation token + still-attached checks make stale/post-dispose
        // results no-ops.
        int generation = ++_refreshGeneration;
        var handler = new Handler(Looper.MainLooper!);
        Task.Run(() =>
        {
            RecentFileEntry[] entries;
            try
            {
                entries = RecentFilesStore.Load(ctx).Take(10).ToArray();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("FA.Recent", "Failed to load recent files: " + ex.Message);
                entries = Array.Empty<RecentFileEntry>();
            }

            handler.Post(() => PopulateRecentRows(ctx, root, generation, entries));
        });
    }

    private void PopulateRecentRows(Context ctx, LinearLayout root, int generation, RecentFileEntry[] entries)
    {
        // Ignore results from a superseded refresh or after the sheet's content
        // was torn down / replaced. The header was already added synchronously.
        if (_disposed
            || generation != _refreshGeneration
            || !ReferenceEquals(root, _contentRoot))
        {
            return;
        }

        if (entries.Length == 0)
        {
            AddEmptyState(ctx, root);
            return;
        }

        foreach (RecentFileEntry entry in entries)
            AddRecentRow(ctx, root, entry);
    }

    private void DisposeManagedContent()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (MaterialCardView card in _recentCards)
            card.SetOnClickListener(null);
        _recentCards.Clear();
        _resizeHandle?.SetOnTouchListener(null);
        _resizeHandle = null;
        _contentRoot = null;
        OnRecentFileSelected = null;
    }

    private void ApplySheetLayout()
    {
        var ctx = Context;
        if (ctx is null || Dialog is not BottomSheetDialog dialog)
            return;

        var sheet = dialog.FindViewById<FrameLayout>(Resource.Id.design_bottom_sheet);
        if (sheet is null)
            return;

        var viewport = Activity?.FindViewById<FrameLayout>(Resource.Id.viewportContainer);
        sheet.Post(() => ApplySheetLayoutNow(ctx, dialog, sheet, viewport));
    }

    private void ApplySheetLayoutNow(Context ctx, BottomSheetDialog dialog, FrameLayout sheet, View? viewport)
    {
        int startOffset = Math.Max(0, CalculateSheetStartOffset(ctx, sheet, viewport));
        int defaultWidth = CalculateSheetWidth(ctx, startOffset, viewport);
        int width = _sheetWidthOverridePx > 0
            ? ClampSheetWidth(ctx, startOffset, viewport, _sheetWidthOverridePx)
            : defaultWidth;
        _sheetWidthOverridePx = _sheetWidthOverridePx > 0 ? width : 0;
        int topOffset = Math.Max(0, CalculateSheetTopOffset(ctx, sheet, viewport));
        int bottomOffset = Math.Max(0, CalculateSheetBottomOffset(ctx, sheet, viewport));
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
            return;

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
            available = Math.Min(available, parent.Height - topOffset - bottomOffset);

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
            return viewportTop;

        int appBarHeight = ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_app_bar_height);
        if (sheet.Parent is View parent && parent.Height > 0)
            return GetParentScreenTop(sheet) + appBarHeight;

        return GetSystemBarHeight(ctx, "status_bar_height") + appBarHeight;
    }

    private static int CalculateDesiredSheetBottom(Context ctx, View sheet, View? viewport)
    {
        if (TryGetViewportBounds(viewport, out _, out _, out int viewportBottom))
            return viewportBottom;

        int bottomBarHeight = ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_bottom_bar_height);
        if (sheet.Parent is View parent && parent.Height > 0)
            return GetParentScreenTop(sheet) + parent.Height - bottomBarHeight;

        var metrics = ctx.Resources?.DisplayMetrics;
        int screenBottom = metrics?.HeightPixels ?? 0;
        return screenBottom
               - GetSystemBarHeight(ctx, "navigation_bar_height")
               - bottomBarHeight;
    }

    private static int CalculateSheetStartOffset(Context ctx, View sheet, View? viewport)
    {
        if (TryGetViewportBounds(viewport, out int viewportLeft, out _, out _))
            return viewportLeft - GetParentScreenLeft(sheet);

        return CalculateFallbackSheetStartOffset(ctx);
    }

    private static bool TryGetViewportBounds(View? viewport, out int left, out int top, out int bottom)
    {
        left = 0;
        top = 0;
        bottom = 0;

        if (viewport is null || viewport.Width <= 0 || viewport.Height <= 0)
            return false;

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
            return 0;

        var location = new int[2];
        parent.GetLocationOnScreen(location);
        return location[0];
    }

    private static int GetParentScreenTop(View sheet)
    {
        if (sheet.Parent is not View parent)
            return 0;

        var location = new int[2];
        parent.GetLocationOnScreen(location);
        return location[1];
    }

    private static int GetSystemBarHeight(Context ctx, string resourceName)
    {
        int resourceId = ctx.Resources?.GetIdentifier(resourceName, "dimen", "android") ?? 0;
        return resourceId > 0 ? ctx.Resources!.GetDimensionPixelSize(resourceId) : 0;
    }

    private static int CalculateSheetWidth(Context ctx, int startOffset, View? viewport)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        if (metrics is null)
            return ViewGroup.LayoutParams.MatchParent;

        float widthDp = metrics.WidthPixels / metrics.Density;
        int edgePadding = Dp(ctx, widthDp >= TabletBreakpointDp ? 16f : 0f);
        int desired = Dp(ctx, widthDp >= TabletBreakpointDp ? TabletPanelWidthDp : CompactPanelWidthDp);
        int available = metrics.WidthPixels - startOffset - edgePadding;
        if (viewport is { Width: > 0 })
            available = Math.Min(available, Math.Max(1, viewport.Width - Dp(ctx, 16f)));

        int minimum = Math.Min(Dp(ctx, 220f), available);
        return Math.Max(minimum, Math.Min(desired, available));
    }

    private static int ClampSheetWidth(Context ctx, int startOffset, View? viewport, int requestedWidth)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        if (metrics is null)
            return requestedWidth;

        float widthDp = metrics.WidthPixels / metrics.Density;
        int edgePadding = Dp(ctx, widthDp >= TabletBreakpointDp ? 16f : 0f);
        int available = metrics.WidthPixels - startOffset - edgePadding;
        if (viewport is { Width: > 0 })
            available = Math.Min(available, Math.Max(1, viewport.Width - Dp(ctx, 16f)));

        int minimum = Math.Min(Dp(ctx, 220f), available);
        return Math.Max(minimum, Math.Min(requestedWidth, available));
    }

    private static int CalculateFallbackSheetStartOffset(Context ctx)
    {
        var metrics = ctx.Resources?.DisplayMetrics;
        if (metrics is null)
            return 0;

        float widthDp = metrics.WidthPixels / metrics.Density;
        return widthDp >= TabletBreakpointDp
            ? ctx.Resources!.GetDimensionPixelSize(Resource.Dimension.fa_nav_rail_width)
            : 0;
    }

    private void AddHeader(Context ctx, ViewGroup parent)
    {
        var title = new TextView(ctx) { Text = "Recent files" };
        title.SetTextSize(ComplexUnitType.Px, Dp(ctx, 22));
        title.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        SetMarginBottom(title, Dp(ctx, 12));
        parent.AddView(title);
    }

    private static void AddEmptyState(Context ctx, ViewGroup parent)
    {
        var card = CreateCard(ctx);
        var text = new TextView(ctx) { Text = "No recent files" };
        text.SetTextSize(ComplexUnitType.Px, Dp(ctx, 15));
        text.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        text.Gravity = GravityFlags.Center;
        text.SetPadding(Dp(ctx, 16), Dp(ctx, 24), Dp(ctx, 16), Dp(ctx, 24));
        card.AddView(text);
        parent.AddView(card);
    }

    private void AddRecentRow(Context ctx, ViewGroup parent, RecentFileEntry entry)
    {
        var card = CreateCard(ctx);
        card.Clickable = true;
        card.Focusable = true;
        card.ContentDescription = "Open recent file " + (string.IsNullOrWhiteSpace(entry.DisplayName) ? "Model" : entry.DisplayName);
        card.SetOnClickListener(new RecentFileClickListener(this, entry));
        _recentCards.Add(card);

        var row = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(ctx, 14), Dp(ctx, 12), Dp(ctx, 14), Dp(ctx, 12));
        card.AddView(row);

        var icon = new ImageView(ctx);
        icon.SetImageResource(Resource.Drawable.ic_recent);
        icon.ImageTintList = ColorStateList.ValueOf(GetColor(ctx, Resource.Color.fa_accent_500));
        var iconParams = new LinearLayout.LayoutParams(Dp(ctx, 24), Dp(ctx, 24));
        iconParams.RightMargin = Dp(ctx, 12);
        row.AddView(icon, iconParams);

        var textGroup = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
        textGroup.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(textGroup);

        var title = new TextView(ctx)
        {
            Text = string.IsNullOrWhiteSpace(entry.DisplayName) ? "Model" : entry.DisplayName,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        title.SetSingleLine(true);
        title.SetTextSize(ComplexUnitType.Px, Dp(ctx, 15));
        title.SetTextColor(GetColor(ctx, Resource.Color.fa_text_primary));
        title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
        textGroup.AddView(title);

        var opened = new TextView(ctx) { Text = FormatOpened(entry.LastOpenedUnixMs) };
        opened.SetTextSize(ComplexUnitType.Px, Dp(ctx, 12));
        opened.SetTextColor(GetColor(ctx, Resource.Color.fa_text_secondary));
        textGroup.AddView(opened);

        parent.AddView(card);
    }

    private static MaterialCardView CreateCard(Context ctx)
    {
        var card = new MaterialCardView(ctx)
        {
            LayoutParameters = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MatchParent,
                LinearLayout.LayoutParams.WrapContent),
        };
        ((LinearLayout.LayoutParams)card.LayoutParameters!).BottomMargin = Dp(ctx, 10);
        card.SetCardBackgroundColor(GetColor(ctx, Resource.Color.fa_surface_background));
        card.Radius = Dp(ctx, 8);
        card.StrokeWidth = Dp(ctx, 1);
        card.SetStrokeColor(ColorStateList.ValueOf(GetColor(ctx, Resource.Color.fa_border)));
        card.Elevation = 0f;
        return card;
    }

    private static string FormatOpened(long unixMs)
    {
        try
        {
            return DateTimeOffset
                .FromUnixTimeMilliseconds(unixMs)
                .ToLocalTime()
                .ToString("MMM d, HH:mm");
        }
        catch
        {
            return "Recent";
        }
    }

    private static int Dp(Context ctx, float dp)
        => (int)(dp * (ctx.Resources?.DisplayMetrics?.Density ?? 1.0f));

    private static void SetMarginBottom(View v, int px)
    {
        if (v.LayoutParameters is ViewGroup.MarginLayoutParams mlp)
        {
            mlp.BottomMargin = px;
            v.LayoutParameters = mlp;
            return;
        }

        var lp = new ViewGroup.MarginLayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent);
        lp.BottomMargin = px;
        v.LayoutParameters = lp;
    }

    private static Color GetColor(Context ctx, int resId) => new(ctx.GetColor(resId));

    private sealed class RecentFileClickListener(
        RecentFilesBottomSheet owner,
        RecentFileEntry entry) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v)
        {
            if (!owner._disposed)
                owner.OnRecentFileSelected?.Invoke(entry);
        }
    }
}
