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
public sealed class AndroidPointerSource
{
    private readonly ViewportTouchGestureRecognizer _recognizer = new();
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly float _density;
    private bool _tickScheduled;

    public AndroidPointerSource(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _density = context.Resources?.DisplayMetrics?.Density ?? 1.0f;
        if (_density <= 0f) _density = 1.0f;
    }

    /// <summary>Subscribe to receive gesture events on the UI thread.</summary>
    public event Action<TouchGestureEvent>? GestureRecognized;

    /// <summary>
    /// Forwards a MotionEvent to the recognizer. Call from
    /// View.IOnTouchListener.OnTouch - return the value back to the
    /// framework. Always returns true; the source wants every touch event
    /// in the sequence.
    /// </summary>
    public bool OnTouch(MotionEvent? motionEvent)
    {
        if (motionEvent is null) return false;

        DateTime time = DateTime.UtcNow;
        MotionEventActions action = motionEvent.ActionMasked;

        switch (action)
        {
            case MotionEventActions.Down:
            {
                int id = motionEvent.GetPointerId(0);
                Fire(_recognizer.PointerDown(id, Sample(motionEvent, 0), time));
                ScheduleLongPressTick();
                break;
            }

            case MotionEventActions.PointerDown:
            {
                int idx = motionEvent.ActionIndex;
                int id = motionEvent.GetPointerId(idx);
                Fire(_recognizer.PointerDown(id, Sample(motionEvent, idx), time));
                break;
            }

            case MotionEventActions.Move:
            {
                int pointerCount = motionEvent.PointerCount;
                int historySize = motionEvent.HistorySize;
                // Walk historical samples first (in order) for fidelity, then
                // the current sample, for every finger.
                for (int h = 0; h < historySize; h++)
                {
                    for (int p = 0; p < pointerCount; p++)
                    {
                        int id = motionEvent.GetPointerId(p);
                        Fire(_recognizer.PointerMove(id, HistoricalSample(motionEvent, p, h), time));
                    }
                }
                for (int p = 0; p < pointerCount; p++)
                {
                    int id = motionEvent.GetPointerId(p);
                    Fire(_recognizer.PointerMove(id, Sample(motionEvent, p), time));
                }
                break;
            }

            case MotionEventActions.Up:
            {
                int id = motionEvent.GetPointerId(0);
                Fire(_recognizer.PointerUp(id, Sample(motionEvent, 0), time));
                break;
            }

            case MotionEventActions.PointerUp:
            {
                int idx = motionEvent.ActionIndex;
                int id = motionEvent.GetPointerId(idx);
                Fire(_recognizer.PointerUp(id, Sample(motionEvent, idx), time));
                break;
            }

            case MotionEventActions.Cancel:
            {
                Fire(_recognizer.Cancel(time));
                break;
            }
        }
        return true;
    }

    private Point2D Sample(MotionEvent ev, int pointerIndex)
    {
        float x = ev.GetX(pointerIndex);
        float y = ev.GetY(pointerIndex);
        return new Point2D(x / _density, y / _density);
    }

    private Point2D HistoricalSample(MotionEvent ev, int pointerIndex, int historyIndex)
    {
        float x = ev.GetHistoricalX(pointerIndex, historyIndex);
        float y = ev.GetHistoricalY(pointerIndex, historyIndex);
        return new Point2D(x / _density, y / _density);
    }

    private void ScheduleLongPressTick()
    {
        if (_tickScheduled) return;
        _tickScheduled = true;
        _handler.PostDelayed(() =>
        {
            _tickScheduled = false;
            Fire(_recognizer.Tick(DateTime.UtcNow));
        }, (long)ViewportTouchGestureRecognizer.LongPressDurationMs);
    }

    private void Fire(IReadOnlyList<TouchGestureEvent> events)
    {
        for (int i = 0; i < events.Count; i++)
            GestureRecognized?.Invoke(events[i]);
    }
}
