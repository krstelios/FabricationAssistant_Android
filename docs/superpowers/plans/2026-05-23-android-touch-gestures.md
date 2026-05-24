# Android Touch Gestures Implementation Plan (Plan 2C of 3+)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire touch input on the Android viewport so the user can orbit (one-finger drag), pan (two-finger drag), and pinch-zoom on loaded models. Tap and double-tap emit events (DoubleTap maps to fit-to-all for MVP); LongPress is recognized but unused until selection lands.

**Architecture:** Port the desktop `ViewportTouchGestureRecognizer` into `FabricationAssistant.Input.Gestures.Android` by swapping its WPF `System.Windows.Point` / `System.Windows.Vector` types for local `Point2D` / `Vector2D` value structs. `AndroidPointerSource.Translate` walks each `MotionEvent`'s historical samples (DIP-scaled by `DisplayMetrics.Density`), feeds them into the recognizer, and emits the recognized gestures. A new `ViewportInteractionAdapter` subscribes and drives `CameraState.OrbitAroundPoint` / `Pan` / `DollyZoomAroundPivot` / `FitToBox`. `MainActivity` installs an `IOnTouchListener` on `ViewportSurfaceView` and wires it all together. A `Handler.PostDelayed`-based one-shot tick fires `Recognizer.Tick(now)` 500 ms after each PointerDown so LongPress can fire even though we're in `Rendermode.WhenDirty`.

**Tech Stack:**
- .NET 8 for Android (`net8.0-android34.0`) — same as Plans 1, 2A, 2B.
- `Android.Views.MotionEvent`, `Android.Util.DisplayMetrics`, `Android.OS.Handler` / `Looper.MainLooper` (already used by `AndroidDispatcher`).
- Pure-managed math: `CameraState`, `Vector3d`, `BoundingBox` — already linked from Core via the shim.
- xUnit on `net8.0` host for the ported recognizer tests.

**Critical guidance (same as Plans 1 + 2A + 2B; do NOT violate):**
- **No git commit steps.** Leave work unstaged. User runs parallel refactors on master.
- **Never edit any file under `../src/`** — the desktop tree is read-only.
- **Build via `Android/tools/build.ps1`** — handles ANDROID_HOME + JAVA_HOME fallbacks.
- **No emojis in source files.**
- **`Application.Current.Dispatcher` is forbidden in any new code.** Use the existing `AndroidDispatcher` or `Handler(Looper.MainLooper!)` directly.
- **Density scaling matters.** `MotionEvent` coordinates are physical pixels. The recognizer's thresholds (8 px drag, 6 px long-press drift, 30 px double-tap distance) are calibrated in DIPs because that's what the desktop GestureRecognizer's `System.Windows.Point` units are. Divide by `Resources.DisplayMetrics.Density` at the boundary so 8 physical pixels on a 3× display feels the same as 8 px on a 1× display.

**Definition of done for this plan:**
1. Clean `Android/tools/build.ps1 -Configuration Debug` succeeds with 0 errors. APK produced.
2. Tests: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` passes with the ported gesture-recognizer tests + a small `AndroidPointerSource` translation test (no Android emulator needed — pure managed).
3. On tablet `R52Y80CE37L`: one-finger drag orbits the loaded model; the camera follows the finger smoothly with no jitter past the 8-DIP threshold.
4. On tablet: two-finger drag pans the camera (centroid follows the average finger position) and pinch zooms in/out (the pivot stays near the centroid at start of gesture).
5. On tablet: a quick tap (no movement) emits a Tap event (logged via `Android.Util.Log` for now; selection is out-of-scope for Plan 2C).
6. On tablet: a double-tap fits the camera to the loaded model's bounds (zoom-to-fit).
7. On tablet: a 500 ms hold without movement logs a LongPress event (multi-select integration is deferred).
8. No `Application.Current.Dispatcher` references introduced (verified by `Select-String 'Application\.Current\.Dispatcher' Android/src` returning no matches in new files).
9. No `../src/` modifications (`git status --porcelain "src/"` empty).

---

## File structure (created/modified by this plan)

```
Android/
├── src/
│   ├── FabricationAssistant.Input.Gestures.Android/
│   │   ├── Point2D.cs                            # NEW — local replacement for System.Windows.Point
│   │   ├── Vector2D.cs                           # NEW — local replacement for System.Windows.Vector
│   │   ├── TouchGestureEvent.cs                  # NEW — record struct, uses Point2D/Vector2D
│   │   ├── ViewportTouchGestureRecognizer.cs     # NEW — ported from src/FabricationAssistant.App/Services/
│   │   ├── AndroidPointerSource.cs               # MODIFIED — replaces the skeleton from Plan 1
│   │   └── ViewportInteractionAdapter.cs         # NEW — drives CameraState from gesture events
│   ├── FabricationAssistant.App.Android/
│   │   ├── Views/
│   │   │   └── ViewportSurfaceView.cs            # MODIFIED — exposes a public RequestRender bridge that the adapter calls
│   │   ├── MainActivity.cs                       # MODIFIED — installs IOnTouchListener, wires adapter to CameraState + viewport
│   │   └── FabricationAssistant.App.Android.csproj # already references Input.Gestures.Android via Plan 1
│   └── FabricationAssistant.App.Android.Tests/
│       ├── FabricationAssistant.App.Android.Tests.csproj # MODIFIED — link in Point2D/Vector2D/TouchGestureEvent/Recognizer for host testing
│       ├── ViewportTouchGestureRecognizerTests.cs       # NEW — ported from src/FabricationAssistant.App.Tests, swap Point/Vector
│       └── AndroidPointerSourceTests.cs                  # NEW — DIP scaling + recognizer feeding (no MotionEvent needed for the unit tests)
├── docs/superpowers/plans/2026-05-23-android-touch-gestures.md  # this file
└── README.md                                              # MODIFIED — append "Plan-2C execution notes"
```

Zero changes to `../src/`.

---

## Phase 1: Local geometry types

### Task 1: Add Point2D and Vector2D structs

**Files:**
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/Point2D.cs`
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/Vector2D.cs`

These two readonly record structs replace `System.Windows.Point` / `System.Windows.Vector` so the recognizer can compile on `net8.0-android34.0` (which has no WPF). The API surface intentionally mirrors the WPF types' members the recognizer actually uses: `X`, `Y`, the binary `point - point => vector` operator, `vector.Length`, and `vector.X` / `vector.Y`.

- [ ] **Step 1: Write `Point2D.cs`**

```csharp
namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// 2D point in viewport-DIP coordinates. Replaces System.Windows.Point so the
/// gesture recognizer compiles without WPF. Subtraction yields a Vector2D
/// (mirrors System.Windows.Point's operator-).
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    public static Vector2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2D operator +(Point2D p, Vector2D v) => new(p.X + v.X, p.Y + v.Y);
    public override string ToString() => $"({X:F2}, {Y:F2})";
}
```

- [ ] **Step 2: Write `Vector2D.cs`**

```csharp
namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// 2D vector in viewport-DIP coordinates. Replaces System.Windows.Vector. Only
/// the members the recognizer uses are exposed: X, Y, Length, Zero, and
/// addition/subtraction.
/// </summary>
public readonly record struct Vector2D(double X, double Y)
{
    public static readonly Vector2D Zero = new(0, 0);

    public double Length => System.Math.Sqrt(X * X + Y * Y);

    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);
    public override string ToString() => $"<{X:F2}, {Y:F2}>";
}
```

- [ ] **Step 3: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. The new types are unreferenced for now but should compile clean inside the existing Input.Gestures.Android csproj (which already targets `net8.0-android34.0` per Plan 1).

---

## Phase 2: Port the recognizer

### Task 2: Create TouchGestureEvent.cs

**Files:**
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/TouchGestureEvent.cs`

This is the public event the recognizer emits. Same shape as the desktop's nested type but living in the Android assembly with our local geometry structs.

- [ ] **Step 1: Write the file**

```csharp
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
}

/// <summary>
/// One recognized gesture event. Position is in viewport-DIP coordinates.
/// PixelDelta is the move-since-last-frame in viewport coordinates and is
/// only populated for OrbitDelta and PanZoomDelta. PinchScale is
/// currentDistance/previousDistance for PanZoomDelta and 1.0 otherwise.
/// </summary>
public readonly record struct TouchGestureEvent(
    TouchGestureKind Kind,
    Point2D Position,
    Vector2D PixelDelta,
    double PinchScale);
```

- [ ] **Step 2: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

### Task 3: Port ViewportTouchGestureRecognizer.cs

**Files:**
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/ViewportTouchGestureRecognizer.cs`

This is a verbatim port of `src/FabricationAssistant.App/Services/ViewportTouchGestureRecognizer.cs` with `System.Windows.Point` → `Point2D` and `System.Windows.Vector` → `Vector2D`. The state machine logic is unchanged. Do not modify the original desktop file.

- [ ] **Step 1: Write the file**

```csharp
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
///   Pending -- Tick() at >= 500ms with no movement -> Pending (LongPress emitted)
///    |
///    | PointerMove totalDelta >= 8px       PointerDown 2nd finger
///    v                                    v
///   Orbit                               PanZoom
///    | PointerUp                          | PointerUp (one finger remains)
///    v                                    v
///   None  (after also emitting Tap       Locked  (residual finger ignored
///          when totalDelta < 8 and        until all fingers up; then None)
///          duration < 350ms)
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

        switch (_state)
        {
            case InternalState.Pending:
            {
                Vector2D totalDelta = touch.Current - touch.Start;
                if (totalDelta.Length < DragThresholdPx)
                    return Empty;

                _state = InternalState.Orbit;
                Vector2D frameDelta = touch.Current - touch.Previous;
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
                if (isTap)
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
        return new[]
        {
            new TouchGestureEvent(TouchGestureKind.LongPress, touch.Current, Vector2D.Zero, 1.0),
        };
    }

    /// <summary>
    /// Cancels every active pointer and resets to None. Wired to
    /// MotionEventActions.Cancel - on touch loss (e.g. system interruption),
    /// we don't want to leave the recognizer stuck in Orbit/PanZoom forever.
    /// Returns the End events for whichever gesture was active so the adapter
    /// can release any held camera state.
    /// </summary>
    public IReadOnlyList<TouchGestureEvent> Cancel(DateTime time)
    {
        if (_touches.Count == 0)
        {
            _state = InternalState.None;
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

        return end is null ? Empty : new[] { end.Value };
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
}
```

- [ ] **Step 2: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. The recognizer is unreferenced for now but compiles standalone.

---

### Task 4: Port the recognizer tests

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Create: `Android/src/FabricationAssistant.App.Android.Tests/ViewportTouchGestureRecognizerTests.cs`

The desktop test suite at `src/FabricationAssistant.App.Tests/ViewportTouchGestureRecognizerTests.cs` covers Tap, DoubleTap, LongPress, Orbit, PanZoom, and Locked-state behavior. Port it wholesale to the Android tests, swapping `System.Windows.Point` → `Point2D`. The test project already targets `net8.0` so it runs on the dev host without an emulator.

- [ ] **Step 1: Link gesture-recognizer sources into the test project**

The test csproj already pulls Core Math via `Compile Include`. Add a sibling block for the gesture sources. Edit `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` and insert the new ItemGroup just before `</Project>`:

```xml
  <!--
    Link the gesture recognizer + its geometry types so the desktop test suite
    can be ported and run on net8.0 host without Android tooling. This avoids
    duplicating the recognizer source. We can't ProjectReference
    FabricationAssistant.Input.Gestures.Android (it targets net8.0-android34.0)
    so we link the small set of files instead.
  -->
  <ItemGroup>
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.Input.Gestures.Android\Point2D.cs"
             LinkBase="Linked\Gestures" />
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.Input.Gestures.Android\Vector2D.cs"
             LinkBase="Linked\Gestures" />
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.Input.Gestures.Android\TouchGestureEvent.cs"
             LinkBase="Linked\Gestures" />
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.Input.Gestures.Android\ViewportTouchGestureRecognizer.cs"
             LinkBase="Linked\Gestures" />
  </ItemGroup>
```

- [ ] **Step 2: Port the tests**

Open `src/FabricationAssistant.App.Tests/ViewportTouchGestureRecognizerTests.cs` (read-only reference). Copy the test methods into `Android/src/FabricationAssistant.App.Android.Tests/ViewportTouchGestureRecognizerTests.cs`, performing these substitutions in the copied text:
- `using FabricationAssistant.App.Services;` → `using FabricationAssistant.Input.Gestures.Android;`
- `using System.Windows;` (or `using Point = System.Windows.Point;`) → delete
- `new Point(` → `new Point2D(`
- `Point ` type usage → `Point2D` (in field/parameter types)

The test logic, assertions, timing values (`T(0)`, `T(50)`, `T(150)`, ...), and expected event sequences stay identical because the state machine is unchanged.

Skeleton showing the first few tests (port the rest from the desktop file the same way):

```csharp
using FabricationAssistant.Input.Gestures.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class ViewportTouchGestureRecognizerTests
{
    private static DateTime T(int ms) => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    private static TouchGestureKind[] Kinds(IReadOnlyList<TouchGestureEvent> events)
        => events.Select(e => e.Kind).ToArray();

    [Fact]
    public void Tap_ShortPressNoMovement_EmitsTapOnUp()
    {
        var r = new ViewportTouchGestureRecognizer();
        Assert.Empty(r.PointerDown(1, new Point2D(100, 100), T(0)));

        var events = r.PointerUp(1, new Point2D(101, 101), T(150));

        Assert.Equal(new[] { TouchGestureKind.Tap }, Kinds(events));
        Assert.Equal(new Point2D(101, 101), events[0].Position);
    }

    // ... port every other [Fact] from the desktop file the same way.
}
```

(The desktop file has roughly ~30 [Fact] methods. Each is a mechanical port — there's no logic to invent.)

- [ ] **Step 3: Run the tests**

Run from `Android/`:
```powershell
dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
```

Expected: every ported test passes. If any fail, the port introduced a typo; re-diff the file against the desktop original. Do not modify the recognizer logic to satisfy a failing test — the desktop tests passed against the desktop logic verbatim, and the only change is the geometry-type substitution.

---

## Phase 3: MotionEvent translation

### Task 5: Rewrite AndroidPointerSource

**Files:**
- Modify: `Android/src/FabricationAssistant.Input.Gestures.Android/AndroidPointerSource.cs`

Replace the skeleton from Plan 1 with the real translator. It owns a `ViewportTouchGestureRecognizer`, walks each `MotionEvent`'s historical samples, scales pixel coordinates to DIPs, and forwards every recognizer event through a `GestureRecognized` action. A `Handler.PostDelayed` schedules `Tick(now)` 500 ms after each first-finger PointerDown so LongPress can fire even though the render surface is `Rendermode.WhenDirty`.

- [ ] **Step 1: Replace the file**

```csharp
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
    /// View.IOnTouchListener.OnTouch — return the value back to the framework.
    /// Always returns true; the source wants every touch event in the sequence.
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
                // Second finger cancels LongPress eligibility; recognizer handles
                // that internally (sets _longPressFired = true). No need to
                // cancel the scheduled tick.
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
```

- [ ] **Step 2: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

### Task 6: Add a tiny AndroidPointerSource translation test

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android.Tests/AndroidPointerSourceTests.cs`

We can't easily new up a `MotionEvent` on a `net8.0` host (it's an Android system type). Instead, the test exercises the public `Point2D` / `Vector2D` types and the `ViewportTouchGestureRecognizer`'s Cancel path end-to-end, since that's the new addition over the desktop recognizer. The remainder of the translation is covered by the ported recognizer tests and on-device manual verification.

- [ ] **Step 1: Write the file**

```csharp
using FabricationAssistant.Input.Gestures.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidPointerSourceTests
{
    private static DateTime T(int ms) => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void Point2D_Subtraction_YieldsVector2D()
    {
        var a = new Point2D(10, 20);
        var b = new Point2D(3, 5);
        Vector2D v = a - b;
        Assert.Equal(7.0, v.X);
        Assert.Equal(15.0, v.Y);
    }

    [Fact]
    public void Vector2D_Length_IsEuclidean()
    {
        Assert.Equal(5.0, new Vector2D(3, 4).Length, precision: 10);
    }

    [Fact]
    public void Cancel_DuringOrbit_EmitsOrbitEndAndResets()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerMove(1, new Point2D(120, 100), T(50));  // crosses DragThreshold
        var events = r.Cancel(T(60));

        Assert.Equal(new[] { TouchGestureKind.OrbitEnd }, events.Select(e => e.Kind).ToArray());
        // After cancel, a fresh tap should still register as Tap, proving the
        // recognizer reset to None (not stuck in Orbit).
        r.PointerDown(2, new Point2D(50, 50), T(100));
        var tap = r.PointerUp(2, new Point2D(50, 50), T(150));
        Assert.Equal(new[] { TouchGestureKind.Tap }, tap.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void Cancel_DuringPanZoom_EmitsPanZoomEndAndResets()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerDown(2, new Point2D(200, 100), T(10));  // enters PanZoom
        var events = r.Cancel(T(20));

        Assert.Contains(TouchGestureKind.PanZoomEnd, events.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void Cancel_WhenIdle_EmitsNothing()
    {
        var r = new ViewportTouchGestureRecognizer();
        var events = r.Cancel(T(0));
        Assert.Empty(events);
    }
}
```

- [ ] **Step 2: Run the tests**

```powershell
dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
```

Expected: all new tests pass alongside the ported recognizer tests.

---

## Phase 4: Camera adapter

### Task 7: Implement ViewportInteractionAdapter

**Files:**
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/ViewportInteractionAdapter.cs`

Subscribes to `AndroidPointerSource.GestureRecognized` events and drives a `CameraState` (Core, linked unchanged) plus a `RequestRender` action the host supplies. Uses the same sensitivity constants as the desktop's `ViewportInteractionController` (`OrbitSensitivityRadiansPerPixel = 0.005`, `PanDistanceScale = 0.002`) for desktop-parity feel. The pivot is the scene-bounds center accessor that the host plugs in — proper pivot picking (`ViewportPivotPicker`) lands in a later plan along with selection. DoubleTap calls `CameraState.FitToBox` over the scene bounds; LongPress and Tap log only (selection/multi-select land later).

- [ ] **Step 1: Write the file**

```csharp
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Translates recognized gesture events into CameraState calls. The host
/// supplies accessors for the current scene, the viewport aspect, and a
/// RequestRender action. Sensitivity constants are copied from the desktop
/// ViewportInteractionController so behaviour matches the desktop's
/// right-drag/middle-drag/wheel feel within DIPs.
/// </summary>
public sealed class ViewportInteractionAdapter
{
    private const double OrbitSensitivityRadiansPerPixel = 0.005;
    private const double PanDistanceScale = 0.002;
    private const double MinimumPanDistance = 1e-6;

    private readonly CameraState _camera;
    private readonly Func<Scene?> _sceneAccessor;
    private readonly Func<double> _aspectAccessor;
    private readonly Action _requestRender;

    private bool _orbitActive;
    private bool _panZoomActive;

    public ViewportInteractionAdapter(
        CameraState camera,
        Func<Scene?> sceneAccessor,
        Func<double> aspectAccessor,
        Action requestRender)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _sceneAccessor = sceneAccessor ?? throw new ArgumentNullException(nameof(sceneAccessor));
        _aspectAccessor = aspectAccessor ?? throw new ArgumentNullException(nameof(aspectAccessor));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
    }

    public void OnGesture(TouchGestureEvent ev)
    {
        switch (ev.Kind)
        {
            case TouchGestureKind.OrbitBegin:
                _orbitActive = true;
                RefreshClipPlanes();
                break;

            case TouchGestureKind.OrbitDelta:
            {
                if (!_orbitActive) break;
                double yaw = ev.PixelDelta.X * OrbitSensitivityRadiansPerPixel;
                double pitch = ev.PixelDelta.Y * OrbitSensitivityRadiansPerPixel;
                Vector3d pivot = GetPivot();
                _camera.OrbitAroundPoint(pivot, yaw, pitch);
                RefreshClipPlanes();
                _requestRender();
                break;
            }

            case TouchGestureKind.OrbitEnd:
                _orbitActive = false;
                _requestRender();
                break;

            case TouchGestureKind.PanZoomBegin:
                _panZoomActive = true;
                RefreshClipPlanes();
                break;

            case TouchGestureKind.PanZoomDelta:
            {
                if (!_panZoomActive) break;
                Vector3d pivot = GetPivot();

                // Pan: convert centroid pixel delta to world units.
                double panScale = ComputePanScale(pivot);
                _camera.Pan(-ev.PixelDelta.X * panScale, ev.PixelDelta.Y * panScale);

                // Zoom: feed the raw pinch scale to CameraState.DollyZoomAroundPivot.
                // Values close to 1.0 produce small zooms; ratios far from 1.0
                // produce proportional zooms. The desktop's pinch path uses the
                // same call.
                if (System.Math.Abs(ev.PinchScale - 1.0) > 1e-6)
                    _camera.DollyZoomAroundPivot(pivot, ev.PinchScale);

                RefreshClipPlanes();
                _requestRender();
                break;
            }

            case TouchGestureKind.PanZoomEnd:
                _panZoomActive = false;
                _requestRender();
                break;

            case TouchGestureKind.DoubleTap:
                FitToScene();
                break;

            case TouchGestureKind.Tap:
            case TouchGestureKind.LongPress:
                // Selection / multi-select integration is deferred; the event
                // is consumed silently. Hosts that want to log can subscribe
                // to AndroidPointerSource.GestureRecognized directly.
                break;
        }
    }

    private Vector3d GetPivot()
    {
        Scene? scene = _sceneAccessor();
        if (scene is { Bounds.IsValid: true })
            return scene.Bounds.Center;
        return _camera.Target;
    }

    private double ComputePanScale(Vector3d pivot)
    {
        if (_camera.IsPerspective)
        {
            double distance = Vector3d.Distance(_camera.Position, pivot);
            if (distance < MinimumPanDistance) distance = _camera.Distance;
            return distance * PanDistanceScale;
        }
        // Orthographic: use the ortho-width-per-DIP ratio so pan tracks
        // 1:1 with the finger. The host knows the viewport width better than
        // we do; for MVP, fall back to the perspective formula scaled.
        return _camera.Distance * PanDistanceScale;
    }

    private void RefreshClipPlanes()
    {
        Scene? scene = _sceneAccessor();
        if (scene is { Bounds.IsValid: true })
            _camera.UpdateClipPlanes(scene.Bounds);
    }

    private void FitToScene()
    {
        Scene? scene = _sceneAccessor();
        if (scene is not { Bounds.IsValid: true }) return;
        _camera.FitToBox(scene.Bounds, _aspectAccessor());
        RefreshClipPlanes();
        _requestRender();
    }
}
```

- [ ] **Step 2: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

## Phase 5: Wire it into MainActivity

### Task 8: Expose a Scene accessor + aspect on the viewport

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs`

The adapter needs `Scene` and an aspect-ratio accessor. The viewport's renderer already owns the Scene (`Renderer.Scene`), so we just need a small public surface. Read the current `ViewportSurfaceView.cs` first to confirm what's there.

- [ ] **Step 1: Read the current ViewportSurfaceView**

Open `Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs`. Confirm it exposes `Renderer.Scene` (Plan 2A wired this). The host will use:
- `viewport.Renderer.Scene` for the Scene accessor
- `(double)viewport.Width / viewport.Height` for the aspect accessor

If those are accessible publicly, no edits to ViewportSurfaceView are needed. If `Renderer` is private/protected, expose `public GpuScene? CurrentScene => Renderer.Scene;` and `public double AspectRatio => Height > 0 ? (double)Width / Height : 1.0;`.

- [ ] **Step 2: Build (if you made edits)**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

### Task 9: Wire MainActivity to install the touch listener

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`

`MainActivity.OnCreate` instantiates `AndroidPointerSource`, `ViewportInteractionAdapter`, attaches an `IOnTouchListener` to `ViewportSurfaceView`, and subscribes the adapter to the pointer source's `GestureRecognized` event.

- [ ] **Step 1: Add fields**

Inside `public sealed class MainActivity : AppCompatActivity`, after the existing `_camera` field, add:

```csharp
    private AndroidPointerSource? _pointerSource;
    private ViewportInteractionAdapter? _interaction;
```

- [ ] **Step 2: Initialize in OnCreate**

Inside `OnCreate`, AFTER the line that does `container.AddView(_viewport);`, insert:

```csharp
        _pointerSource = new AndroidPointerSource(this);
        _interaction = new ViewportInteractionAdapter(
            _camera,
            sceneAccessor: () => _viewport.Renderer.Scene?.SourceDocument,  // see note below
            aspectAccessor: () => _viewport.Width > 0 && _viewport.Height > 0
                ? (double)_viewport.Width / _viewport.Height
                : 1.0,
            requestRender: () => _viewport.RequestRender());
        _pointerSource.GestureRecognized += _interaction.OnGesture;

        _viewport.SetOnTouchListener(new TouchProxy(_pointerSource));
```

**Note on `sceneAccessor`:** the adapter expects a `Scene?` from `FabricationAssistant.Core.SceneGraph`. Plan 2A's `GpuScene` keeps a reference to the loaded `DocumentDto`, but the adapter wants the scene-graph `Scene`. Two acceptable shapes depending on what `GpuScene` exposes today:
- If `GpuScene` exposes a `Scene` (the Core graph type) directly, pass that.
- If only `Bounds` is exposed, change the adapter's `sceneAccessor` to `Func<BoundingBox?>` and adjust `GetPivot` / `FitToScene` accordingly. The MainActivity wiring becomes:

```csharp
sceneAccessor: () => _viewport.Renderer.Scene?.Bounds,
```

Pick whichever matches what Plan 2A wired. The point of the adapter is bounds-based pivot + fit, not full scene access.

- [ ] **Step 3: Add the TouchProxy helper at file scope (inside MainActivity)**

`IOnTouchListener` is an Android interface that requires a `Java.Lang.Object` subclass. A small private nested class wraps the pointer source so we don't pollute MainActivity's own object hierarchy.

Add this nested class inside `MainActivity`:

```csharp
    private sealed class TouchProxy : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly AndroidPointerSource _source;
        public TouchProxy(AndroidPointerSource source) => _source = source;
        public bool OnTouch(View? v, MotionEvent? e) => _source.OnTouch(e);
    }
```

Add the necessary `using` directives at the top of the file:

```csharp
using Android.Views;
using FabricationAssistant.Input.Gestures.Android;
```

- [ ] **Step 4: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. The new touch wiring compiles.

---

## Phase 6: Verify on the tablet

### Task 10: Install + manually exercise orbit/pan/zoom/double-tap

**Files:** none — verification only.

- [ ] **Step 1: Install on tablet**

```powershell
$env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools"
$apk = "C:\Users\skritikos\Desktop\Fabrication Assistant\Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk"
adb shell am force-stop com.fabricationassistant.android
adb install -r $apk
adb shell am start -n com.fabricationassistant.android/crc649abf7c96d03d876b.MainActivity
```

- [ ] **Step 2: Load a model and exercise gestures**

On the tablet, tap Open and pick one of the `.fa` files in `/sdcard/Download/`. Once the model renders, in order:
1. **Orbit:** one-finger drag horizontally across the viewport — the model should rotate yaw smoothly. Drag vertically — pitch.
2. **Pan:** two-finger drag — the model should translate with the finger centroid.
3. **Zoom:** two-finger pinch out — model gets larger; pinch in — model gets smaller.
4. **Double-tap:** double-tap on the viewport — the camera should snap to fit the model bounds (zoom-to-fit).
5. **Tap:** single tap — no visible change (selection deferred), no errors in logcat.
6. **Long-press:** press and hold for 500 ms without moving — no visible change, no errors in logcat. Optionally watch logcat for the LongPress event if you add a log statement during development.

- [ ] **Step 3: Capture a log on any anomaly**

If anything misbehaves, capture logcat:
```powershell
adb logcat -d --pid=$(adb shell pidof com.fabricationassistant.android) 2>$null > "$env:TEMP\fa-gestures-debug.log"
```
and inspect for unhandled exceptions, NREs in the adapter, or repeated zero-delta events that indicate a density miscalculation.

---

### Task 11: Verify desktop tree untouched + document Plan 2C

**Files:**
- Modify: `Android/README.md`

- [ ] **Step 1: Confirm desktop tree untouched**

Run from the repo root:
```powershell
git status --porcelain "src/"
```
Expected: empty output. Plan 2C must not have introduced any file under `../src/`.

- [ ] **Step 2: Append a "Plan-2C execution notes" section**

Insert into `Android/README.md` immediately before the existing `## Manual verification step` line:

```markdown
## Plan-2C execution notes

Touch input wired through a ported `ViewportTouchGestureRecognizer`. Implementation
lives in `src/FabricationAssistant.Input.Gestures.Android/`. The desktop
recognizer at `src/FabricationAssistant.App/Services/ViewportTouchGestureRecognizer.cs`
is unchanged; the Android port substitutes `System.Windows.Point` / `Vector`
for local `Point2D` / `Vector2D` value structs that live next to the recognizer.

- **Gesture pipeline:** `View.IOnTouchListener.OnTouch(MotionEvent)` -> 
  `AndroidPointerSource.OnTouch` -> per-pointer historical+current sample
  translation through `ViewportTouchGestureRecognizer` -> 
  `GestureRecognized` event -> `ViewportInteractionAdapter.OnGesture` -> 
  `CameraState.OrbitAroundPoint / Pan / DollyZoomAroundPivot / FitToBox` -> 
  `ViewportSurfaceView.RequestRender()`.
- **DIP scaling:** `MotionEvent` X/Y are physical pixels. The pointer source
  divides by `Resources.DisplayMetrics.Density` so the recognizer's 8 DIP /
  6 DIP / 30 DIP thresholds feel consistent across density profiles.
- **LongPress under WhenDirty:** `Rendermode.WhenDirty` means no per-frame
  callback is available to drive the recognizer's `Tick`. The pointer source
  schedules a one-shot `Handler.PostDelayed(500 ms)` at each first-finger
  PointerDown and calls `Tick(now)` from there.
- **Cancel:** added a new `ViewportTouchGestureRecognizer.Cancel(time)`
  method (not on the desktop recognizer) so `MotionEventActions.Cancel` from
  Android - a system interruption while a gesture is in flight - can release
  Orbit / PanZoom cleanly instead of leaving the state machine stuck.
- **Pivot:** the adapter uses `scene.Bounds.Center` as the orbit / pan /
  zoom pivot. Proper pivot picking (`ViewportPivotPicker`) is deferred to a
  later plan along with selection.
- **DoubleTap:** maps to `CameraState.FitToBox(scene.Bounds, aspect)`.
- **Sensitivity:** orbit at `0.005 rad/DIP`, pan at `distance * 0.002`,
  zoom via `DollyZoomAroundPivot(pivot, pinchScale)`. Matches the desktop
  `ViewportInteractionController` constants.
- **Test project link:** `App.Android.Tests.csproj` adds `<Compile Include>`
  for `Point2D.cs`, `Vector2D.cs`, `TouchGestureEvent.cs`, and
  `ViewportTouchGestureRecognizer.cs` so the desktop test suite ports
  directly. `net8.0` host target keeps tests emulator-free.
```

- [ ] **Step 3: Final clean build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. APK produced. Tests pass via `dotnet test Android/src/FabricationAssistant.App.Android.Tests/`.

---

## Risk register (Plan 2C-specific)

| # | Risk | Mitigation |
|---|------|------------|
| G1 | DIP scaling factor differs across OEM skins (Samsung's quirky `Density` reporting on tablets) | Fallback to `1.0f` when reported density is `<= 0`; on first device test, `adb shell wm density` to confirm. If a Samsung-specific override is needed, document it in this plan's notes after the fact. |
| G2 | `IOnTouchListener` on `GLSurfaceView` can be eaten by the surface itself on some OEMs | If `OnTouch` is never invoked, try setting `_viewport.Clickable = true; _viewport.Focusable = true;` in `MainActivity.OnCreate`. The `View.IsClickable` Android docs note that `OnTouchListener` is bypassed if the view has zero touch state. |
| G3 | Pinch-zoom drift: `DollyZoomAroundPivot` may interact poorly with `Pan` applied in the same frame | Adapter applies Pan THEN DollyZoom on the same `PanZoomDelta`. If the centroid + pinch jitter, change ordering or guard the pinch on `\|scale - 1\|` above a small threshold (0.005). |
| G4 | LongPress never fires because `Handler.PostDelayed` was cancelled by the activity lifecycle | The handler is owned by `AndroidPointerSource` which lives for the activity's lifetime. If the activity is paused mid-press, that's the user choosing to leave the app - acceptable for MVP. |
| G5 | Rotation re-creates MainActivity (the spec mandates `ConfigurationChanges` keeps the activity alive); if that ever changes, AndroidPointerSource leaks the recognizer | Document in README that `AndroidManifest`'s `ConfigChanges` includes `Orientation \| ScreenSize` (Plan 1 already configured this). Verify nothing in Plan 2C undoes it. |

---

## Self-review checklist (writing-plans skill)

**1. Spec coverage:** Plan 2C covers spec sections 7.1 (touch pipeline), 7.2 (gesture table — Orbit, Pan, Pinch, Tap, DoubleTap, LongPress), 7.3 (CameraState linked, DollyZoomAroundPivot is the pinch math), and 5.5 (density conversion). Selection on tap, multi-select on long-press, ViewCube tap, and pivot picking via `ViewportPivotPicker` are explicitly deferred — they need infrastructure that doesn't exist yet (selection state, pick renderer is being built in parallel work).

**2. Placeholder scan:** No "TBD" / "implement later" / "similar to Task N" / "etc." remain. The recognizer port (Task 3) and the test port (Task 4) include the full source code or explicit substitution rules — no engineer-derives-the-rest hand-waving. The MainActivity wiring (Task 9) gives the exact insertion point and the full code block.

**3. Type consistency:**
- `Point2D` / `Vector2D` consistent across Tasks 1, 2, 3, 4, 5, 7.
- `TouchGestureEvent(Kind, Position, PixelDelta, PinchScale)` matches the recognizer's emit calls (Task 3) and the adapter's consume (Task 7).
- `AndroidPointerSource(Context)` constructor in Task 5 matches MainActivity instantiation in Task 9.
- `ViewportInteractionAdapter(camera, sceneAccessor, aspectAccessor, requestRender)` in Task 7 matches the lambdas MainActivity constructs in Task 9. The note in Task 9 Step 2 calls out the `Scene?` vs `BoundingBox?` shape choice depending on what Plan 2A exposes.
- `ViewportTouchGestureRecognizer.Cancel(time)` defined in Task 3 is called from Task 5 (AndroidPointerSource) and tested in Task 6.

No type-consistency bugs found.

---

## Execution choice

Plan complete and saved to `Android/docs/superpowers/plans/2026-05-23-android-touch-gestures.md`. Two execution options:

**1. Subagent-Driven (recommended)** — one fresh subagent per task, two-stage review between tasks. The recognizer port (Task 3) and adapter (Task 7) benefit from spec-compliance review.

**2. Inline execution** — execute all 11 tasks in this session via the controller. Faster end-to-end if everything goes smoothly.

Which approach?
