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
    private int _activePointerId = -1;
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
            {
                int pointerIndex = AndroidMotionEvents.PreferredPointerIndex(e);
                if (pointerIndex < 0)
                    return false;

                _dragging = true;
                _activePointerId = e.GetPointerId(pointerIndex);
                _startRawX = AndroidMotionEvents.RawX(e, pointerIndex);
                _startWidth = _getWidth();
                v?.Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;
            }

            case MotionEventActions.PointerDown:
            {
                if (!_dragging)
                    return false;

                int pointerIndex = e.ActionIndex;
                if (AndroidMotionEvents.IsPointerStylusOrEraser(e, pointerIndex))
                    ResetDragOrigin(e, pointerIndex);

                return true;
            }

            case MotionEventActions.Move:
                if (!_dragging)
                    return false;

                if (!AndroidMotionEvents.TryFindPointerIndex(e, _activePointerId, out int movePointerIndex))
                    return true;

                int delta = (int)MathF.Round(AndroidMotionEvents.RawX(e, movePointerIndex) - _startRawX);
                _applyWidth(_clampWidth(_startWidth + _direction * delta));
                return true;

            case MotionEventActions.PointerUp:
                if (!_dragging)
                    return false;

                int actionIndex = e.ActionIndex;
                if (actionIndex >= 0
                    && actionIndex < e.PointerCount
                    && e.GetPointerId(actionIndex) == _activePointerId)
                    EndDrag(v);
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                EndDrag(v);
                return true;

            default:
                return false;
        }
    }

    private void EndDrag(View? v)
    {
        _dragging = false;
        _activePointerId = -1;
        v?.Parent?.RequestDisallowInterceptTouchEvent(false);
    }

    private void ResetDragOrigin(MotionEvent e, int pointerIndex)
    {
        _activePointerId = e.GetPointerId(pointerIndex);
        _startRawX = AndroidMotionEvents.RawX(e, pointerIndex);
        _startWidth = _getWidth();
    }
}
