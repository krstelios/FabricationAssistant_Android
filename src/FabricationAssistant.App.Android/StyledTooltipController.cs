using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace FabricationAssistant.App.Android;

internal sealed class StyledTooltipController : Java.Lang.Object, View.IOnHoverListener, View.IOnLongClickListener, IDisposable
{
    private const int ShowDelayMs = 420;
    private const int AutoDismissMs = 2200;
    private const int MinWidthDp = 36;
    private const int MaxWidthDp = 220;
    private const int CornerRadiusDp = 10;

    private readonly Context _context;
    private readonly WeakReference<View> _anchor;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly Action<StyledTooltipController> _requestDismissOthers;
    private readonly bool _useLongClick;
    private PopupWindow? _popup;
    private string _text;
    private bool _disposed;
    private int _showGeneration;

    public StyledTooltipController(
        Context context,
        View anchor,
        string text,
        Action<StyledTooltipController> requestDismissOthers,
        bool useLongClick = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(requestDismissOthers);

        _context = context;
        _anchor = new WeakReference<View>(anchor);
        _text = text;
        _requestDismissOthers = requestDismissOthers;
        _useLongClick = useLongClick;

        anchor.SetOnHoverListener(this);
        if (_useLongClick)
            anchor.SetOnLongClickListener(this);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            anchor.TooltipText = null;
    }

    public void UpdateText(string text)
    {
        _text = text;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            if (_anchor.TryGetTarget(out View? anchor))
                anchor.TooltipText = null;
        }
    }

    public bool OnHover(View? v, MotionEvent? e)
    {
        if (_disposed || e is null || v is null || !v.Enabled)
            return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.HoverEnter:
            case MotionEventActions.HoverMove:
                ScheduleShow();
                break;

            case MotionEventActions.HoverExit:
            case MotionEventActions.Cancel:
                CancelPendingShow();
                Dismiss();
                break;
        }

        return false;
    }

    public bool OnLongClick(View? v)
    {
        if (_disposed || v is null || !v.Enabled)
            return false;

        CancelPendingShow();
        Show();
        return true;
    }

    public void Dismiss()
    {
        CancelPendingShow();
        DismissPopupOnly();
    }

    public void ShowNow(string? textOverride = null, string? accessibilityAnnouncement = null)
    {
        if (_disposed)
            return;

        CancelPendingShow();
        int generation = Interlocked.Increment(ref _showGeneration);
        Show(generation, textOverride, accessibilityAnnouncement);
    }

    private void DismissPopupOnly()
    {
        if (_popup is null)
            return;

        try
        {
            _popup.Dismiss();
        }
        catch
        {
            // The popup can already be detached during activity teardown.
        }

        _popup = null;
    }

    public new void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _handler.RemoveCallbacksAndMessages(null);
        if (_anchor.TryGetTarget(out View? anchor))
        {
            anchor.SetOnHoverListener(null);
            if (_useLongClick)
                anchor.SetOnLongClickListener(null);
        }
        Dismiss();
    }

    private void ScheduleShow()
    {
        CancelPendingShow();
        int generation = Interlocked.Increment(ref _showGeneration);
        _handler.PostDelayed(() => Show(generation), ShowDelayMs);
    }

    private void CancelPendingShow()
    {
        Interlocked.Increment(ref _showGeneration);
        _handler.RemoveCallbacksAndMessages(null);
    }

    private void Show()
        => Show(Volatile.Read(ref _showGeneration), textOverride: null, accessibilityAnnouncement: null);

    private void Show(int generation)
        => Show(generation, textOverride: null, accessibilityAnnouncement: null);

    private void Show(int generation, string? textOverride, string? accessibilityAnnouncement)
    {
        string displayText = string.IsNullOrWhiteSpace(textOverride) ? _text : textOverride!;
        if (_disposed
            || generation != Volatile.Read(ref _showGeneration)
            || !_anchor.TryGetTarget(out View? anchor)
            || !anchor.IsShown
            || !anchor.Enabled
            || string.IsNullOrWhiteSpace(displayText))
        {
            return;
        }

        _requestDismissOthers(this);
        if (_disposed
            || generation != Volatile.Read(ref _showGeneration)
            || !_anchor.TryGetTarget(out anchor)
            || !anchor.IsShown
            || !anchor.Enabled)
        {
            return;
        }
        DismissPopupOnly();

        TextView label = CreateLabel(displayText);
        int maxWidth = Dp(MaxWidthDp);
        label.Measure(
            View.MeasureSpec.MakeMeasureSpec(maxWidth, MeasureSpecMode.AtMost),
            View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));

        int width = Math.Max(label.MeasuredWidth, Dp(MinWidthDp));
        int height = label.MeasuredHeight;
        var popup = new PopupWindow(label, width, height, focusable: false)
        {
            Touchable = false,
            OutsideTouchable = false,
            ClippingEnabled = true,
        };
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            popup.Elevation = Dp(8);

        if (!TryComputeLocation(width, height, out int x, out int y))
            return;

        _popup = popup;
        try
        {
            popup.ShowAtLocation(anchor.RootView, GravityFlags.NoGravity, x, y);
            if (!string.IsNullOrWhiteSpace(accessibilityAnnouncement))
                label.Post(() => label.AnnounceForAccessibility(accessibilityAnnouncement));
            _handler.PostDelayed(() =>
            {
                if (generation == Volatile.Read(ref _showGeneration))
                    Dismiss();
            }, AutoDismissMs);
        }
        catch
        {
            _popup = null;
            popup.Dismiss();
        }
    }

    private TextView CreateLabel(string text)
    {
        var label = new TextView(_context)
        {
            Text = text,
            TextSize = 12.5f,
            Gravity = GravityFlags.Center,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        label.SetSingleLine(true);
        label.SetIncludeFontPadding(false);
        label.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
        label.SetPadding(Dp(9), Dp(6), Dp(9), Dp(6));
        label.Background = CreateBackground();
        return label;
    }

    private GradientDrawable CreateBackground()
    {
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetColor(ColorRes(Resource.Color.fa_context_menu_background));
        background.SetCornerRadius(Dp(CornerRadiusDp));
        background.SetStroke(Dp(1), ColorRes(Resource.Color.fa_context_menu_border));
        return background;
    }

    private Color ColorRes(int resourceId)
        => new(global::AndroidX.Core.Content.ContextCompat.GetColor(_context, resourceId));

    private bool TryComputeLocation(int width, int height, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (!_anchor.TryGetTarget(out View? anchor) || anchor.RootView is null)
            return false;

        int[] location = new int[2];
        anchor.GetLocationOnScreen(location);
        int screenWidth = _context.Resources?.DisplayMetrics?.WidthPixels ?? 0;
        int screenHeight = _context.Resources?.DisplayMetrics?.HeightPixels ?? 0;
        if (screenWidth <= 0 || screenHeight <= 0)
            return false;

        int margin = Dp(8);
        int gap = Dp(7);
        int anchorCenterX = location[0] + anchor.Width / 2;
        x = Math.Clamp(anchorCenterX - width / 2, margin, Math.Max(margin, screenWidth - width - margin));

        int aboveY = location[1] - height - gap;
        int belowY = location[1] + anchor.Height + gap;
        y = aboveY >= margin || aboveY >= screenHeight - belowY
            ? aboveY
            : belowY;
        y = Math.Clamp(y, margin, Math.Max(margin, screenHeight - height - margin));
        return true;
    }

    private int Dp(float dp)
    {
        float density = _context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        if (density <= 0f)
            density = 1.0f;
        return (int)Math.Round(dp * density);
    }
}
