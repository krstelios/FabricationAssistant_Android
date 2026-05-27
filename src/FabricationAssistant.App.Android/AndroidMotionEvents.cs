using Android.Views;

namespace FabricationAssistant.App.Android;

internal static class AndroidMotionEvents
{
    public static int PreferredPointerIndex(MotionEvent e, bool preferStylusOrEraser = true)
    {
        if (preferStylusOrEraser)
        {
            for (int i = 0; i < e.PointerCount; i++)
            {
                if (IsPointerStylusOrEraser(e, i))
                    return i;
            }
        }

        int actionIndex = e.ActionIndex;
        if (actionIndex >= 0 && actionIndex < e.PointerCount)
            return actionIndex;

        return e.PointerCount > 0 ? 0 : -1;
    }

    public static bool TryFindPointerIndex(MotionEvent e, int pointerId, out int pointerIndex)
    {
        pointerIndex = e.FindPointerIndex(pointerId);
        return pointerIndex >= 0 && pointerIndex < e.PointerCount;
    }

    public static bool IsPointerStylusOrEraser(MotionEvent e, int pointerIndex)
        => pointerIndex >= 0
           && pointerIndex < e.PointerCount
           && IsStylusOrEraser(e.GetToolType(pointerIndex));

    public static bool IsStylusOrEraser(MotionEventToolType toolType)
        => toolType == MotionEventToolType.Stylus
           || toolType == MotionEventToolType.Eraser;

    public static float RawX(MotionEvent e, int pointerIndex)
        => e.GetX(pointerIndex) + e.RawX - e.GetX(0);

    public static float RawY(MotionEvent e, int pointerIndex)
        => e.GetY(pointerIndex) + e.RawY - e.GetY(0);
}
