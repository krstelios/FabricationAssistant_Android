using Android.Content;
using Android.OS;
using Android.Views;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Bridges Android MotionEvent into the FabricationAssistant gesture recognizer.
/// Converts physical pixels to DIPs at the boundary so the recognizer's
/// thresholds (8 DIP drag, 6 DIP long-press drift, 30 DIP double-tap) feel
/// the same across density profiles. Posts a one-shot 500 ms Tick on each
/// first-finger PointerDown so LongPress can fire under
/// Rendermode.WhenDirty.
/// </summary>
public sealed class AndroidPointerSource : IDisposable
{
    private const int MotionEventFlagCanceled = 0x20;
    private const double DefaultMouseClickDragThresholdDip = 4.0;
    private static int _actionPointerFallbackLogged;

    private enum MouseButtonGesture
    {
        None,
        PrimaryClick,
        Orbit,
        Pan,
    }

    private readonly ViewportTouchGestureRecognizer _recognizer = new();
    private readonly Handler _handler = new(Looper.MainLooper!);
    private float _density;
    private bool _tickScheduled;
    private MouseButtonGesture _mouseGesture;
    private int _mousePointerId = -1;
    private Point2D _mouseDownPosition;
    private Point2D _mouseLastPosition;
    private bool _mouseMoved;
    private bool _mouseToolDragStarted;
    private bool _secondaryPressActive;
    private bool _disposed;
    private bool _suppressFingerPointers;

    public AndroidPointerSource(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _density = ResolveDensity(context);
    }

    /// <summary>Subscribe to receive gesture events on the UI thread.</summary>
    public event Action<TouchGestureEvent>? GestureRecognized;

    public double MouseClickDragThresholdDip { get; set; } = DefaultMouseClickDragThresholdDip;

    /// <summary>
    /// When enabled, viewport gestures suppress finger pointers only while
    /// stylus input is active. Normal finger gestures keep working when the
    /// S Pen is away from the screen.
    /// </summary>
    public bool SpenPalmRejectionEnabled { get; set; }

    public void RefreshDensity(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _density = ResolveDensity(context);
    }

    /// <summary>
    /// Forwards a MotionEvent to the recognizer. Call from
    /// View.IOnTouchListener.OnTouch - return the value back to the
    /// framework. Always returns true; the source wants every touch event
    /// in the sequence.
    /// </summary>
    public bool OnTouch(MotionEvent? motionEvent)
    {
        if (_disposed || motionEvent is null) return false;

        DateTime time = DateTime.UtcNow;
        MotionEventActions action = motionEvent.ActionMasked;

        switch (action)
        {
            case MotionEventActions.Down:
            {
                if (TryBeginMouseButtonGesture(motionEvent, time))
                    break;
                if (ShouldIgnoreActionPointer(motionEvent))
                    break;
                if (TryFireSecondaryTap(motionEvent, time))
                    break;

                int id = motionEvent.GetPointerId(0);
                Fire(_recognizer.PointerDown(id, Sample(motionEvent, 0), time));
                ScheduleLongPressTick();
                break;
            }

            case MotionEventActions.PointerDown:
            {
                if (TryBeginMouseButtonGesture(motionEvent, time))
                    break;
                if (ShouldIgnoreActionPointer(motionEvent))
                    break;
                if (TryFireSecondaryTap(motionEvent, time))
                    break;

                int idx = motionEvent.ActionIndex;
                int id = motionEvent.GetPointerId(idx);
                Fire(_recognizer.PointerDown(id, Sample(motionEvent, idx), time));
                ScheduleLongPressTick();
                break;
            }

            case MotionEventActions.Move:
            {
                if (TryUpdateMouseButtonGesture(motionEvent, time))
                    break;

                int pointerCount = motionEvent.PointerCount;
                int historySize = motionEvent.HistorySize;

                for (int h = 0; h < historySize; h++)
                    Fire(_recognizer.PointerMoveBatch(
                        SampleBatch(motionEvent, pointerCount, h),
                        MotionEventTimeUtc(motionEvent, motionEvent.GetHistoricalEventTime(h))));

                Fire(_recognizer.PointerMoveBatch(
                    SampleBatch(motionEvent, pointerCount, historyIndex: null),
                    MotionEventTimeUtc(motionEvent, motionEvent.EventTime)));
                break;
            }

            case MotionEventActions.Up:
            {
                _secondaryPressActive = false;
                if (ShouldIgnoreActionPointer(motionEvent))
                    break;
                if (IsCanceled(motionEvent))
                {
                    CancelMouseButtonGesture(time);
                    Fire(_recognizer.Cancel(time));
                    ClearPalmRejectionIfStylusActionPointer(motionEvent);
                    break;
                }
                if (TryEndMouseButtonGesture(motionEvent, time))
                {
                    ClearPalmRejectionIfStylusActionPointer(motionEvent);
                    break;
                }
                int id = motionEvent.GetPointerId(0);
                Fire(_recognizer.PointerUp(id, Sample(motionEvent, 0), time));
                ClearPalmRejectionIfStylusActionPointer(motionEvent);
                break;
            }

            case MotionEventActions.PointerUp:
            {
                _secondaryPressActive = false;
                if (ShouldIgnoreActionPointer(motionEvent))
                    break;
                if (IsCanceled(motionEvent))
                {
                    CancelMouseButtonGesture(time);
                    Fire(_recognizer.Cancel(time));
                    ClearPalmRejectionIfStylusActionPointer(motionEvent);
                    break;
                }
                if (TryEndMouseButtonGesture(motionEvent, time))
                {
                    ClearPalmRejectionIfStylusActionPointer(motionEvent);
                    break;
                }
                int idx = motionEvent.ActionIndex;
                int id = motionEvent.GetPointerId(idx);
                Fire(_recognizer.PointerUp(id, Sample(motionEvent, idx), time));
                ClearPalmRejectionIfStylusActionPointer(motionEvent);
                break;
            }

            case MotionEventActions.Cancel:
            {
                CancelMouseButtonGesture(time);
                _secondaryPressActive = false;
                ClearPalmRejectionSuppression();
                Fire(_recognizer.Cancel(time));
                break;
            }

            case MotionEventActions.ButtonPress:
            {
                if (!TryBeginMouseButtonGesture(motionEvent, time))
                    TryFireSecondaryTap(motionEvent, time);
                break;
            }

            case MotionEventActions.ButtonRelease:
            {
                if (!TryEndMouseButtonGesture(motionEvent, time))
                    ResetSecondaryPress();
                break;
            }
        }
        return true;
    }

    public bool OnGenericMotion(MotionEvent? motionEvent)
    {
        if (_disposed || motionEvent is null) return false;

        DateTime time = DateTime.UtcNow;
        return motionEvent.ActionMasked switch
        {
            MotionEventActions.ButtonPress => TryBeginMouseButtonGesture(motionEvent, time)
                                               || TryFireSecondaryTap(motionEvent, time),
            MotionEventActions.ButtonRelease => TryEndMouseButtonGesture(motionEvent, time)
                                                 || ResetSecondaryPress(),
            MotionEventActions.Move or MotionEventActions.HoverMove => TryUpdateMouseButtonGesture(motionEvent, time),
            MotionEventActions.Scroll => TryFireMouseWheel(motionEvent),
            MotionEventActions.Cancel => CancelMouseButtonGesture(time),
            _ => false,
        };
    }

    private bool ResetSecondaryPress()
    {
        bool wasActive = _secondaryPressActive;
        _secondaryPressActive = false;
        return wasActive;
    }

    private bool TryFireSecondaryTap(MotionEvent ev, DateTime time)
    {
        if (IsMousePointerEvent(ev))
            return false;
        if (ShouldIgnoreActionPointer(ev))
            return false;
        if (!IsSecondaryButtonPressed(ev) || _secondaryPressActive)
            return false;

        _secondaryPressActive = true;
        Fire(_recognizer.Cancel(time));
        Fire(new[]
        {
            new TouchGestureEvent(TouchGestureKind.SecondaryTap, Sample(ev, ActionPointerIndex(ev)), Vector2D.Zero, 1.0),
        });
        return true;
    }

    private bool TryBeginMouseButtonGesture(MotionEvent ev, DateTime time)
    {
        if (!IsMousePointerEvent(ev))
            return false;
        if (ShouldIgnoreActionPointer(ev))
            return false;
        if (!TryGetPressedMouseGesture(ev, out MouseButtonGesture gesture))
            return false;

        if (_mouseGesture != MouseButtonGesture.None)
            return true;

        int pointerIndex = ActionPointerIndex(ev);
        _mouseGesture = gesture;
        _mousePointerId = ev.GetPointerId(pointerIndex);
        _mouseDownPosition = Sample(ev, pointerIndex);
        _mouseLastPosition = _mouseDownPosition;
        _mouseMoved = false;

        Fire(_recognizer.Cancel(time));

        TouchGestureKind? beginKind = gesture switch
        {
            MouseButtonGesture.Orbit => TouchGestureKind.MouseOrbitBegin,
            MouseButtonGesture.Pan => TouchGestureKind.MousePanBegin,
            _ => null,
        };

        if (beginKind is { } kind)
        {
            Fire(new[]
            {
                new TouchGestureEvent(kind, _mouseDownPosition, Vector2D.Zero, 1.0),
            });
        }

        return true;
    }

    private bool TryUpdateMouseButtonGesture(MotionEvent ev, DateTime time)
    {
        if (_mouseGesture == MouseButtonGesture.None)
            return false;

        MotionEventActions action = ev.ActionMasked;
        if (action is not (MotionEventActions.Move or MotionEventActions.HoverMove))
            return false;

        MotionEventButtonState activeButton = ActiveMouseButton(_mouseGesture);
        if (action == MotionEventActions.HoverMove
            && activeButton != 0
            && !IsButtonPressed(ev, activeButton))
        {
            return TryEndMouseButtonGesture(ev, time);
        }

        int pointerIndex = PointerIndexForIdOrAction(ev, _mousePointerId);
        Point2D position = Sample(ev, pointerIndex);
        UpdateMouseMoved(position);

        Vector2D delta = position - _mouseLastPosition;
        _mouseLastPosition = position;
        if (delta.Length <= 0.0)
            return true;

        if (_mouseGesture == MouseButtonGesture.PrimaryClick)
        {
            if (!_mouseMoved)
                return true;

            if (!_mouseToolDragStarted)
            {
                _mouseToolDragStarted = true;
                Fire(new[]
                {
                    new TouchGestureEvent(TouchGestureKind.MouseToolBegin, _mouseDownPosition, Vector2D.Zero, 1.0),
                });
            }

            Fire(new[]
            {
                new TouchGestureEvent(TouchGestureKind.MouseToolDelta, position, delta, 1.0),
            });
            return true;
        }

        TouchGestureKind? deltaKind = _mouseGesture switch
        {
            MouseButtonGesture.Orbit => TouchGestureKind.MouseOrbitDelta,
            MouseButtonGesture.Pan => TouchGestureKind.MousePanDelta,
            _ => null,
        };

        if (deltaKind is { } kind)
        {
            Fire(new[]
            {
                new TouchGestureEvent(kind, position, delta, 1.0),
            });
        }

        return true;
    }

    private bool TryEndMouseButtonGesture(MotionEvent ev, DateTime time)
    {
        if (_mouseGesture == MouseButtonGesture.None)
            return false;

        if (!IsMouseGestureRelease(ev, ActiveMouseButton(_mouseGesture)))
            return false;

        int pointerIndex = PointerIndexForIdOrAction(ev, _mousePointerId);
        Point2D position = Sample(ev, pointerIndex);
        UpdateMouseMoved(position);

        MouseButtonGesture endedGesture = _mouseGesture;
        bool moved = _mouseMoved;
        ResetMouseButtonGesture();

        switch (endedGesture)
        {
            case MouseButtonGesture.PrimaryClick:
                if (moved)
                {
                    if (!_mouseToolDragStarted)
                    {
                        Fire(new[]
                        {
                            new TouchGestureEvent(TouchGestureKind.MouseToolBegin, _mouseDownPosition, Vector2D.Zero, 1.0),
                        });
                    }

                    Fire(new[]
                    {
                        new TouchGestureEvent(TouchGestureKind.MouseToolEnd, position, Vector2D.Zero, 1.0),
                    });
                }
                else
                {
                    Fire(new[]
                    {
                        new TouchGestureEvent(TouchGestureKind.Tap, position, Vector2D.Zero, 1.0),
                    });
                }
                break;

            case MouseButtonGesture.Orbit:
                Fire(new[]
                {
                    new TouchGestureEvent(TouchGestureKind.MouseOrbitEnd, position, Vector2D.Zero, 1.0),
                });
                if (!moved)
                {
                    Fire(new[]
                    {
                        new TouchGestureEvent(TouchGestureKind.SecondaryTap, position, Vector2D.Zero, 1.0),
                    });
                }
                break;

            case MouseButtonGesture.Pan:
                Fire(new[]
                {
                    new TouchGestureEvent(TouchGestureKind.MousePanEnd, position, Vector2D.Zero, 1.0),
                });
                break;
        }

        return true;
    }

    private bool CancelMouseButtonGesture(DateTime time)
    {
        if (_mouseGesture == MouseButtonGesture.None)
            return false;

        MouseButtonGesture endedGesture = _mouseGesture;
        Point2D position = _mouseLastPosition;
        ResetMouseButtonGesture();

        var events = new List<TouchGestureEvent>
        {
            new(TouchGestureKind.Cancel, position, Vector2D.Zero, 1.0),
        };

        if (endedGesture == MouseButtonGesture.Orbit)
            events.Add(new TouchGestureEvent(TouchGestureKind.MouseOrbitEnd, position, Vector2D.Zero, 1.0));
        else if (endedGesture == MouseButtonGesture.Pan)
            events.Add(new TouchGestureEvent(TouchGestureKind.MousePanEnd, position, Vector2D.Zero, 1.0));

        Fire(events);
        return true;
    }

    private bool TryFireMouseWheel(MotionEvent ev)
    {
        if (!IsMousePointerEvent(ev))
            return false;

        double vscroll = ev.GetAxisValue(Axis.Vscroll);
        if (!double.IsFinite(vscroll) || System.Math.Abs(vscroll) <= 0.0)
            return false;

        int pointerIndex = ActionPointerIndex(ev);
        Fire(new[]
        {
            new TouchGestureEvent(
                TouchGestureKind.MouseWheel,
                Sample(ev, pointerIndex),
                new Vector2D(0.0, vscroll),
                1.0),
        });
        return true;
    }

    private void UpdateMouseMoved(Point2D position)
    {
        double dx = position.X - _mouseDownPosition.X;
        double dy = position.Y - _mouseDownPosition.Y;
        double threshold = double.IsFinite(MouseClickDragThresholdDip)
            ? System.Math.Clamp(MouseClickDragThresholdDip, 1.0, 20.0)
            : DefaultMouseClickDragThresholdDip;
        if (System.Math.Abs(dx) > threshold
            || System.Math.Abs(dy) > threshold)
        {
            _mouseMoved = true;
        }
    }

    private void ResetMouseButtonGesture()
    {
        _mouseGesture = MouseButtonGesture.None;
        _mousePointerId = -1;
        _mouseDownPosition = default;
        _mouseLastPosition = default;
        _mouseMoved = false;
        _mouseToolDragStarted = false;
    }

    private static bool TryGetPressedMouseGesture(MotionEvent ev, out MouseButtonGesture gesture)
    {
        MotionEventButtonState actionButton = (MotionEventButtonState)ev.ActionButton;
        if ((actionButton & MotionEventButtonState.Tertiary) == MotionEventButtonState.Tertiary)
        {
            gesture = MouseButtonGesture.Pan;
            return true;
        }

        if ((actionButton & MotionEventButtonState.Secondary) == MotionEventButtonState.Secondary)
        {
            gesture = MouseButtonGesture.Orbit;
            return true;
        }

        if ((actionButton & MotionEventButtonState.Primary) == MotionEventButtonState.Primary)
        {
            gesture = MouseButtonGesture.PrimaryClick;
            return true;
        }

        if (IsButtonPressed(ev, MotionEventButtonState.Tertiary))
        {
            gesture = MouseButtonGesture.Pan;
            return true;
        }

        if (IsButtonPressed(ev, MotionEventButtonState.Secondary))
        {
            gesture = MouseButtonGesture.Orbit;
            return true;
        }

        if (IsButtonPressed(ev, MotionEventButtonState.Primary))
        {
            gesture = MouseButtonGesture.PrimaryClick;
            return true;
        }

        gesture = MouseButtonGesture.None;
        return false;
    }

    private static bool IsMouseGestureRelease(MotionEvent ev, MotionEventButtonState button)
    {
        MotionEventActions action = ev.ActionMasked;
        if (action is MotionEventActions.Up or MotionEventActions.PointerUp or MotionEventActions.Cancel)
            return true;

        if (action != MotionEventActions.ButtonRelease)
            return false;

        if (button == 0)
            return true;

        if (IsActionButton(ev, button))
            return true;

        return ((MotionEventButtonState)ev.ActionButton) == 0
               && !IsButtonPressed(ev, button);
    }

    private static MotionEventButtonState ActiveMouseButton(MouseButtonGesture gesture)
        => gesture switch
        {
            MouseButtonGesture.PrimaryClick => MotionEventButtonState.Primary,
            MouseButtonGesture.Orbit => MotionEventButtonState.Secondary,
            MouseButtonGesture.Pan => MotionEventButtonState.Tertiary,
            _ => 0,
        };

    private static bool IsPrimaryButtonPressed(MotionEvent ev)
        => IsButtonPressed(ev, MotionEventButtonState.Primary);

    private static bool IsSecondaryButtonPressed(MotionEvent ev)
        => IsButtonPressed(ev, MotionEventButtonState.Secondary)
            || IsButtonPressed(ev, MotionEventButtonState.StylusPrimary)
            || IsButtonPressed(ev, MotionEventButtonState.StylusSecondary);

    private static bool IsButtonPressed(MotionEvent ev, MotionEventButtonState button)
        => ev.IsButtonPressed(button);

    private static bool IsMousePointerEvent(MotionEvent ev)
    {
        if ((ev.Source & InputSourceType.Mouse) == InputSourceType.Mouse)
            return true;

        int count = ev.PointerCount;
        for (int i = 0; i < count; i++)
        {
            if (ev.GetToolType(i) == MotionEventToolType.Mouse)
                return true;
        }

        return false;
    }

    public void CancelActiveGesture()
    {
        if (_disposed) return;
        DateTime time = DateTime.UtcNow;
        CancelMouseButtonGesture(time);
        _secondaryPressActive = false;
        Fire(_recognizer.Cancel(time));
    }

    public void NotifyStylusInput()
    {
        if (_disposed) return;
        _suppressFingerPointers = true;
    }

    public void ClearPalmRejectionSuppression()
    {
        _suppressFingerPointers = false;
    }

    private bool ShouldIgnoreActionPointer(MotionEvent ev)
    {
        if (!ShouldSuppressFingerPointers(ev))
            return false;

        if (ev.ActionMasked == MotionEventActions.Down && IsFingerOnly(ev))
        {
            ClearPalmRejectionSuppression();
            return false;
        }

        return IsFingerPointer(ev, ActionPointerIndex(ev));
    }

    private bool ShouldSuppressFingerPointers(MotionEvent ev)
    {
        if (!SpenPalmRejectionEnabled)
            return false;

        if (!_suppressFingerPointers
            && IsStylusDownAction(ev)
            && ContainsStylusOrEraser(ev))
            NotifyStylusInput();

        return _suppressFingerPointers;
    }

    private static bool IsStylusDownAction(MotionEvent ev)
        => ev.ActionMasked is MotionEventActions.Down or MotionEventActions.PointerDown;

    private static bool IsFingerPointer(MotionEvent ev, int pointerIndex)
        => pointerIndex >= 0
           && pointerIndex < ev.PointerCount
           && ev.GetToolType(pointerIndex) == MotionEventToolType.Finger;

    private static bool ContainsStylusOrEraser(MotionEvent ev)
    {
        int count = ev.PointerCount;
        for (int i = 0; i < count; i++)
        {
            MotionEventToolType toolType = ev.GetToolType(i);
            if (toolType == MotionEventToolType.Stylus || toolType == MotionEventToolType.Eraser)
                return true;
        }

        return false;
    }

    private static bool IsFingerOnly(MotionEvent ev)
    {
        int count = ev.PointerCount;
        if (count <= 0)
            return false;

        for (int i = 0; i < count; i++)
        {
            if (!IsFingerPointer(ev, i))
                return false;
        }

        return true;
    }

    private void ClearPalmRejectionIfStylusActionPointer(MotionEvent ev)
    {
        int pointerIndex = ActionPointerIndex(ev);
        if (pointerIndex < 0 || pointerIndex >= ev.PointerCount)
            return;

        MotionEventToolType toolType = ev.GetToolType(pointerIndex);
        if (toolType == MotionEventToolType.Stylus || toolType == MotionEventToolType.Eraser)
            ClearPalmRejectionSuppression();
    }

    private static bool IsCanceled(MotionEvent ev)
        => ((int)ev.Flags & MotionEventFlagCanceled) != 0;

    private static int ActionPointerIndex(MotionEvent ev)
    {
        int pointerCount = ev.PointerCount;
        if (pointerCount <= 0)
            return 0;

        int index = ev.ActionIndex;
        if (index >= 0 && index < pointerCount)
            return index;

        if (Interlocked.Exchange(ref _actionPointerFallbackLogged, 1) == 0)
        {
            global::Android.Util.Log.Warn(
                "FA.Input",
                $"MotionEvent action pointer index out of range; falling back to pointer 0. actionIndex={index}, pointerCount={pointerCount}.");
        }

        return 0;
    }

    private static int PointerIndexForIdOrAction(MotionEvent ev, int pointerId)
    {
        if (pointerId >= 0)
        {
            int index = ev.FindPointerIndex(pointerId);
            if (index >= 0 && index < ev.PointerCount)
                return index;
        }

        return ActionPointerIndex(ev);
    }

    private static bool IsActionButton(MotionEvent ev, MotionEventButtonState button)
        => (((MotionEventButtonState)ev.ActionButton) & button) == button;

    private static float ResolveDensity(Context context)
    {
        float density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        return density > 0f ? density : 1.0f;
    }

    private Point2D Sample(MotionEvent ev, int pointerIndex)
    {
        float x = ev.GetX(pointerIndex);
        float y = ev.GetY(pointerIndex);
        return new Point2D(x / _density, y / _density);
    }

    private IReadOnlyList<(int Id, Point2D Position)> SampleBatch(MotionEvent ev, int pointerCount, int? historyIndex)
    {
        if (ShouldSuppressFingerPointers(ev))
        {
            var filtered = new List<(int Id, Point2D Position)>(pointerCount);
            for (int p = 0; p < pointerCount; p++)
            {
                if (IsFingerPointer(ev, p))
                    continue;

                filtered.Add(SampleAt(ev, p, historyIndex));
            }

            return filtered;
        }

        var samples = new (int Id, Point2D Position)[pointerCount];
        for (int p = 0; p < pointerCount; p++)
            samples[p] = SampleAt(ev, p, historyIndex);

        return samples;
    }

    private (int Id, Point2D Position) SampleAt(MotionEvent ev, int pointerIndex, int? historyIndex)
    {
        float x;
        float y;
        if (historyIndex is int h)
        {
            x = ev.GetHistoricalX(pointerIndex, h);
            y = ev.GetHistoricalY(pointerIndex, h);
        }
        else
        {
            x = ev.GetX(pointerIndex);
            y = ev.GetY(pointerIndex);
        }

        return (ev.GetPointerId(pointerIndex), new Point2D(x / _density, y / _density));
    }

    private static DateTime MotionEventTimeUtc(MotionEvent ev, long eventTimeMs)
    {
        long deltaMs = eventTimeMs - ev.EventTime;
        return DateTime.UtcNow.AddMilliseconds(deltaMs);
    }

    private void ScheduleLongPressTick()
    {
        if (_disposed || _tickScheduled) return;
        _tickScheduled = true;
        _handler.PostDelayed(() =>
        {
            if (_disposed) return;
            _tickScheduled = false;
            Fire(_recognizer.Tick(DateTime.UtcNow));
        }, (long)ViewportTouchGestureRecognizer.LongPressDurationMs);
    }

    private void Fire(IReadOnlyList<TouchGestureEvent> events)
    {
        if (_disposed) return;
        for (int i = 0; i < events.Count; i++)
            GestureRecognized?.Invoke(events[i]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickScheduled = false;
        _handler.RemoveCallbacksAndMessages(null);
        GestureRecognized = null;
    }
}
