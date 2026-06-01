namespace FabricationAssistant.Input.Gestures.Android;

public enum TouchGestureKind
{
    Tap,
    DoubleTap,
    LongPress,
    OrbitBegin,
    OrbitDelta,
    OrbitEnd,
    PanZoomBegin,
    PanZoomDelta,
    PanZoomEnd,
    SecondaryTap,
    MouseOrbitBegin,
    MouseOrbitDelta,
    MouseOrbitEnd,
    MousePanBegin,
    MousePanDelta,
    MousePanEnd,
    MouseWheel,
    MouseToolBegin,
    MouseToolDelta,
    MouseToolEnd,
    Cancel,
}

/// <summary>
/// One recognized gesture event. Position is in viewport-DIP coordinates.
/// PixelDelta is the move-since-last-frame in viewport coordinates and is
/// populated for drag and wheel delta events. PinchScale is
/// currentDistance/previousDistance for PanZoomDelta and 1.0 otherwise.
/// IsMultiTouchTransition is true only on the OrbitEnd that fires because a
/// second finger landed (an Orbit-&gt;PanZoom hand-off), as opposed to the
/// OrbitEnd that fires when the finger lifts. Consumers that treat OrbitEnd as
/// "the one-finger drag finished" (e.g. the Zoom Window marquee) can use it to
/// avoid committing on an accidental second contact.
/// </summary>
public readonly record struct TouchGestureEvent(
    TouchGestureKind Kind,
    Point2D Position,
    Vector2D PixelDelta,
    double PinchScale,
    bool IsMultiTouchTransition = false);
