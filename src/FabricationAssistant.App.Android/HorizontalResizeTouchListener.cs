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
                // S12-F3: ignore additional pointers (e.g. a transient S Pen tap or
                // a palm contact) while a divider drag is in progress. Adopting them
                // here meant the drag could end when that second pointer lifted even
                // though the original finger was still down.
                return _dragging;
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
                {
                    // S12-F3: the pointer driving the drag lifted. If another pointer
                    // is still down on the divider, keep dragging with it instead of
                    // ending - so lifting a transient second contact first does not
                    // abandon a drag the user is still performing with another finger.
                    if (TryFindOtherPointer(e, actionIndex, out int remainingIndex))
                        ResetDragOrigin(e, remainingIndex);
                    else
                        EndDrag(v);
                }
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

    // Finds any pointer still down on the divider other than the one at
    // <paramref name="excludedIndex"/> (the pointer being lifted in a PointerUp).
    private static bool TryFindOtherPointer(MotionEvent e, int excludedIndex, out int otherIndex)
    {
        for (int i = 0; i < e.PointerCount; i++)
        {
            if (i != excludedIndex)
            {
                otherIndex = i;
                return true;
            }
        }

        otherIndex = -1;
        return false;
    }
}
