using System.Linq;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Pure state machine for touch gestures. No Android runtime dependencies.
/// Host (AndroidPointerSource) feeds it PointerDown / PointerMove / PointerUp
/// events from MotionEvent samples, plus a periodic Tick from a Handler-based
/// 500 ms one-shot scheduled at each PointerDown. Each call returns the events
/// the recognizer fired in response.
///
/// State transitions:
///
///   None
///    | PointerDown(id) when fingerCount == 1
///    v
///   Pending -- Tick() at &gt;= 500ms with no movement -&gt; Locked (LongPress emitted)
///    |
///    | PointerMove totalDelta &gt;= 8px       PointerDown 2nd finger
///    v                                    v
///   Orbit                               PanZoom
///    | PointerUp                          | PointerUp (one finger remains)
///    v                                    v
///   None  (after also emitting Tap       Locked  (residual finger ignored
///          when totalDelta &lt; 8 and        until all fingers up; then None)
///          duration &lt; 350ms)
///
/// Ported verbatim from src/FabricationAssistant.App/Services/
/// ViewportTouchGestureRecognizer.cs with System.Windows.Point/Vector
/// substituted for local Point2D/Vector2D structs, plus a new Cancel(time)
/// entry point for MotionEventActions.Cancel handling that the WPF host
/// did not need.
/// </summary>
public sealed class ViewportTouchGestureRecognizer
{
    public const double DragThresholdPx = 8.0;
    public const double TapMaxMovementPx = 8.0;
    public const double TapMaxDurationMs = 350.0;
    public const double DoubleTapMaxIntervalMs = 350.0;
    public const double DoubleTapMaxDistancePx = 30.0;
    public const double LongPressDurationMs = 500.0;
    public const double LongPressMaxMovementPx = 6.0;

    private enum InternalState { None, Pending, Orbit, PanZoom, Locked }

    private sealed class TouchPoint
    {
        public int Id;
        public Point2D Start;
        public Point2D Previous;
        public Point2D Current;
        public DateTime DownTime;
    }

    private static readonly IReadOnlyList<TouchGestureEvent> Empty = Array.Empty<TouchGestureEvent>();

    private readonly Dictionary<int, TouchPoint> _touches = new();
    private InternalState _state = InternalState.None;

    private bool _longPressFired;
    private DateTime? _lastTapTime;
    private Point2D _lastTapPosition;
    private double _twoFingerPreviousDistance;
    private Point2D _twoFingerPreviousCentroid;

    public IReadOnlyList<TouchGestureEvent> PointerDown(int id, Point2D position, DateTime time)
    {
        _touches[id] = new TouchPoint
        {
            Id = id,
            Start = position,
            Previous = position,
            Current = position,
            DownTime = time,
        };

        if (_touches.Count == 1)
        {
            _state = InternalState.Pending;
            _longPressFired = false;
            return Empty;
        }

        if (_touches.Count == 2 && _state != InternalState.Locked)
        {
            _longPressFired = true;
            BeginTwoFinger();

            var begin = new TouchGestureEvent(
                TouchGestureKind.PanZoomBegin,
                _twoFingerPreviousCentroid,
                Vector2D.Zero,
                1.0);

            if (_state == InternalState.Orbit)
            {
                _state = InternalState.PanZoom;
                return new[]
                {
                    new TouchGestureEvent(TouchGestureKind.OrbitEnd, _touches[id].Current, Vector2D.Zero, 1.0),
                    begin,
                };
            }

            _state = InternalState.PanZoom;
            return new[] { begin };
        }

        return Empty;
    }

    public IReadOnlyList<TouchGestureEvent> PointerMove(int id, Point2D position, DateTime time)
    {
        if (!_touches.TryGetValue(id, out TouchPoint? touch))
            return Empty;

        touch.Previous = touch.Current;
        touch.Current = position;
        return EmitMoveEvents(touch);
    }

    public IReadOnlyList<TouchGestureEvent> PointerMoveBatch(
        IReadOnlyList<(int Id, Point2D Position)> samples,
        DateTime time)
    {
        if (samples.Count == 0)
            return Empty;

        TouchPoint? firstMoved = null;
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (!_touches.TryGetValue(sample.Id, out TouchPoint? touch))
                continue;

            touch.Previous = touch.Current;
            touch.Current = sample.Position;
            firstMoved ??= touch;
        }

        return firstMoved is null ? Empty : EmitMoveEvents(firstMoved);
    }

    private IReadOnlyList<TouchGestureEvent> EmitMoveEvents(TouchPoint touch)
    {
        switch (_state)
        {
            case InternalState.Pending:
            {
                Vector2D totalDelta = touch.Current - touch.Start;
                if (totalDelta.Length < DragThresholdPx)
                    return Empty;

                _state = InternalState.Orbit;
                Vector2D frameDelta = DeltaBeyondDragThreshold(touch.Start, touch.Current);
                return new[]
                {
                    new TouchGestureEvent(TouchGestureKind.OrbitBegin, touch.Start, Vector2D.Zero, 1.0),
                    new TouchGestureEvent(TouchGestureKind.OrbitDelta, touch.Current, frameDelta, 1.0),
                };
            }

            case InternalState.Orbit:
            {
                Vector2D frameDelta = touch.Current - touch.Previous;
                return new[]
                {
                    new TouchGestureEvent(TouchGestureKind.OrbitDelta, touch.Current, frameDelta, 1.0),
                };
            }

            case InternalState.PanZoom:
            {
                if (_touches.Count < 2)
                    return Empty;

                Point2D[] points = TwoFingerPoints();
                Point2D centroid = Average(points[0], points[1]);
                double distance = Distance(points[0], points[1]);

                Vector2D centroidDelta = centroid - _twoFingerPreviousCentroid;
                double pinchScale = _twoFingerPreviousDistance > 1e-6
                    ? distance / _twoFingerPreviousDistance
                    : 1.0;

                _twoFingerPreviousCentroid = centroid;
                _twoFingerPreviousDistance = distance;

                return new[]
                {
                    new TouchGestureEvent(TouchGestureKind.PanZoomDelta, centroid, centroidDelta, pinchScale),
                };
            }

            default:
                return Empty;
        }
    }

    public IReadOnlyList<TouchGestureEvent> PointerUp(int id, Point2D position, DateTime time)
    {
        if (!_touches.TryGetValue(id, out TouchPoint? touch))
            return Empty;

        touch.Current = position;
        Point2D upPosition = touch.Current;
        DateTime downTime = touch.DownTime;
        Vector2D totalDelta = upPosition - touch.Start;

        _touches.Remove(id);

        var emitted = new List<TouchGestureEvent>(2);

        switch (_state)
        {
            case InternalState.Pending:
            {
                double durationMs = (time - downTime).TotalMilliseconds;
                bool isTap = totalDelta.Length < TapMaxMovementPx
                          && durationMs < TapMaxDurationMs;
                if (!_longPressFired && isTap)
                {
                    if (IsDoubleTap(upPosition, time))
                    {
                        emitted.Add(new TouchGestureEvent(TouchGestureKind.DoubleTap, upPosition, Vector2D.Zero, 1.0));
                        _lastTapTime = null;
                    }
                    else
                    {
                        emitted.Add(new TouchGestureEvent(TouchGestureKind.Tap, upPosition, Vector2D.Zero, 1.0));
                        _lastTapTime = time;
                        _lastTapPosition = upPosition;
                    }
                }
                _state = _touches.Count == 0 ? InternalState.None : InternalState.Locked;
                break;
            }

            case InternalState.Orbit:
            {
                emitted.Add(new TouchGestureEvent(TouchGestureKind.OrbitEnd, upPosition, Vector2D.Zero, 1.0));
                _state = _touches.Count == 0 ? InternalState.None : InternalState.Locked;
                break;
            }

            case InternalState.PanZoom:
            {
                emitted.Add(new TouchGestureEvent(TouchGestureKind.PanZoomEnd, upPosition, Vector2D.Zero, 1.0));
                _state = _touches.Count == 0 ? InternalState.None : InternalState.Locked;
                break;
            }

            case InternalState.Locked:
            {
                if (_touches.Count == 0)
                    _state = InternalState.None;
                break;
            }
        }

        return emitted.Count == 0 ? Empty : emitted;
    }

    public IReadOnlyList<TouchGestureEvent> Tick(DateTime time)
    {
        if (_state != InternalState.Pending || _longPressFired || _touches.Count != 1)
            return Empty;

        TouchPoint touch = _touches.Values.First();
        double durationMs = (time - touch.DownTime).TotalMilliseconds;
        if (durationMs < LongPressDurationMs)
            return Empty;

        Vector2D totalDelta = touch.Current - touch.Start;
        if (totalDelta.Length > LongPressMaxMovementPx)
            return Empty;

        _longPressFired = true;
        _state = InternalState.Locked;
        return new[]
        {
            new TouchGestureEvent(TouchGestureKind.LongPress, touch.Current, Vector2D.Zero, 1.0),
        };
    }

    /// <summary>
    /// Cancels every active pointer and resets to None. Wired to
    /// MotionEventActions.Cancel - on touch loss (e.g. system interruption),
    /// we don't want to leave the recognizer stuck in Orbit/PanZoom forever.
    /// Returns a Cancel event plus the End event for whichever gesture was
    /// active so modal tools can abort and the adapter can release any held
    /// camera state. Idle Cancel returns empty.
    /// </summary>
    public IReadOnlyList<TouchGestureEvent> Cancel(DateTime time)
    {
        if (_touches.Count == 0)
        {
            _state = InternalState.None;
            _lastTapTime = null;
            return Empty;
        }

        TouchGestureEvent? end = _state switch
        {
            InternalState.Orbit => new TouchGestureEvent(TouchGestureKind.OrbitEnd, _touches.Values.First().Current, Vector2D.Zero, 1.0),
            InternalState.PanZoom => new TouchGestureEvent(TouchGestureKind.PanZoomEnd, _twoFingerPreviousCentroid, Vector2D.Zero, 1.0),
            _ => null,
        };

        _touches.Clear();
        _state = InternalState.None;
        _longPressFired = false;
        _lastTapTime = null;

        var cancel = new TouchGestureEvent(TouchGestureKind.Cancel, end?.Position ?? new Point2D(0, 0), Vector2D.Zero, 1.0);
        return end is null ? new[] { cancel } : new[] { cancel, end.Value };
    }

    private bool IsDoubleTap(Point2D upPosition, DateTime time)
    {
        if (_lastTapTime is not { } previous)
            return false;

        double interval = (time - previous).TotalMilliseconds;
        if (interval > DoubleTapMaxIntervalMs)
            return false;

        Vector2D offset = upPosition - _lastTapPosition;
        return offset.Length <= DoubleTapMaxDistancePx;
    }

    private void BeginTwoFinger()
    {
        Point2D[] points = TwoFingerPoints();
        _twoFingerPreviousCentroid = Average(points[0], points[1]);
        _twoFingerPreviousDistance = Distance(points[0], points[1]);
    }

    private Point2D[] TwoFingerPoints()
    {
        return _touches.Values
            .OrderBy(t => t.Id)
            .Take(2)
            .Select(t => t.Current)
            .ToArray();
    }

    private static Point2D Average(Point2D a, Point2D b)
        => new Point2D((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

    private static double Distance(Point2D a, Point2D b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    private static Vector2D DeltaBeyondDragThreshold(Point2D start, Point2D current)
    {
        Vector2D totalDelta = current - start;
        double length = totalDelta.Length;
        if (length <= DragThresholdPx || length <= 1e-9)
            return Vector2D.Zero;

        double scale = (length - DragThresholdPx) / length;
        return new Vector2D(totalDelta.X * scale, totalDelta.Y * scale);
    }
}
