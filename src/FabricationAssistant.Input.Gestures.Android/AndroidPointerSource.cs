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

    private readonly ViewportTouchGestureRecognizer _recognizer = new();
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly float _density;
    private bool _tickScheduled;
    private bool _secondaryPressActive;
    private bool _disposed;
    private bool _suppressFingerPointers;

    public AndroidPointerSource(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        if (_density <= 0f) _density = 1.0f;
    }

    /// <summary>Subscribe to receive gesture events on the UI thread.</summary>
    public event Action<TouchGestureEvent>? GestureRecognized;

    /// <summary>
    /// When enabled, viewport gestures suppress finger pointers only while
    /// stylus input is active. Normal finger gestures keep working when the
    /// S Pen is away from the screen.
    /// </summary>
    public bool SpenPalmRejectionEnabled { get; set; }

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
                int pointerCount = motionEvent.PointerCount;
                int historySize = motionEvent.HistorySize;

                for (int h = 0; h < historySize; h++)
                    Fire(_recognizer.PointerMoveBatch(SampleBatch(motionEvent, pointerCount, h), time));

                Fire(_recognizer.PointerMoveBatch(SampleBatch(motionEvent, pointerCount, historyIndex: null), time));
                break;
            }

            case MotionEventActions.Up:
            {
                _secondaryPressActive = false;
                if (ShouldIgnoreActionPointer(motionEvent))
                    break;
                if (IsCanceled(motionEvent))
                {
                    Fire(_recognizer.Cancel(time));
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
                    Fire(_recognizer.Cancel(time));
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
                _secondaryPressActive = false;
                ClearPalmRejectionSuppression();
                Fire(_recognizer.Cancel(time));
                break;
            }

            case MotionEventActions.ButtonPress:
            {
                TryFireSecondaryTap(motionEvent, time);
                break;
            }

            case MotionEventActions.ButtonRelease:
            {
                _secondaryPressActive = false;
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
            MotionEventActions.ButtonPress => TryFireSecondaryTap(motionEvent, time),
            MotionEventActions.ButtonRelease => ResetSecondaryPress(),
            _ => false,
        };
    }

    private bool ResetSecondaryPress()
    {
        _secondaryPressActive = false;
        return false;
    }

    private bool TryFireSecondaryTap(MotionEvent ev, DateTime time)
    {
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

    private static bool IsSecondaryButtonPressed(MotionEvent ev)
        => ev.IsButtonPressed(MotionEventButtonState.Secondary)
            || ev.IsButtonPressed(MotionEventButtonState.StylusPrimary)
            || ev.IsButtonPressed(MotionEventButtonState.StylusSecondary);

    public void CancelActiveGesture()
    {
        if (_disposed) return;
        _secondaryPressActive = false;
        Fire(_recognizer.Cancel(DateTime.UtcNow));
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

        if (ContainsStylusOrEraser(ev))
            NotifyStylusInput();

        return _suppressFingerPointers;
    }

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
        return index >= 0 && index < pointerCount ? index : 0;
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
