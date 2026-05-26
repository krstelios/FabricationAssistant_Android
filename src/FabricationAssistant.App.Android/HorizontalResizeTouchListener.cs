using Android.Views;

namespace FabricationAssistant.App.Android;

internal sealed class HorizontalResizeTouchListener : Java.Lang.Object, View.IOnTouchListener
{
    private readonly Func<int> _getWidth;
    private readonly Func<int, int> _clampWidth;
    private readonly Action<int> _applyWidth;
    private readonly int _direction;
    private float _startRawX;
    private int _startWidth;
    private bool _dragging;

    public HorizontalResizeTouchListener(
        Func<int> getWidth,
        Func<int, int> clampWidth,
        Action<int> applyWidth,
        int direction)
    {
        _getWidth = getWidth;
        _clampWidth = clampWidth;
        _applyWidth = applyWidth;
        _direction = direction >= 0 ? 1 : -1;
    }

    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e is null)
            return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _dragging = true;
                _startRawX = e.RawX;
                _startWidth = _getWidth();
                v?.Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;

            case MotionEventActions.Move:
                if (!_dragging)
                    return false;

                int delta = (int)MathF.Round(e.RawX - _startRawX);
                _applyWidth(_clampWidth(_startWidth + _direction * delta));
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _dragging = false;
                v?.Parent?.RequestDisallowInterceptTouchEvent(false);
                return true;

            default:
                return false;
        }
    }
}
