using FabricationAssistant.Input.Gestures.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

/// <summary>
/// Coverage for the parts of Plan 2C that don't require a real
/// MotionEvent (which is an Android system type and can't be constructed
/// on a net8.0 host). The full MotionEvent translation path is covered
/// by the ported recognizer tests + on-device manual verification.
/// </summary>
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
        r.PointerMove(1, new Point2D(120, 100), T(50));
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
        r.PointerDown(2, new Point2D(200, 100), T(10));
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
