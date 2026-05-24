using FabricationAssistant.Input.Gestures.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

/// <summary>
/// Ported from src/FabricationAssistant.App.Tests/ViewportTouchGestureRecognizerTests.cs
/// with System.Windows.Point/Vector substituted for local Point2D/Vector2D.
/// State-machine logic in the recognizer is unchanged, so the timing values
/// and expected event sequences carry over verbatim.
/// </summary>
public sealed class ViewportTouchGestureRecognizerTests
{
    private static DateTime T(int ms) => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    private static TouchGestureKind[] Kinds(IReadOnlyList<TouchGestureEvent> events)
        => events.Select(e => e.Kind).ToArray();

    // -- Tap -------------------------------------------------------------

    [Fact]
    public void Tap_ShortPressNoMovement_EmitsTapOnUp()
    {
        var r = new ViewportTouchGestureRecognizer();
        Assert.Empty(r.PointerDown(1, new Point2D(100, 100), T(0)));

        var events = r.PointerUp(1, new Point2D(101, 101), T(150));

        Assert.Equal(new[] { TouchGestureKind.Tap }, Kinds(events));
        Assert.Equal(new Point2D(101, 101), events[0].Position);
    }

    [Fact]
    public void Tap_MovementAboveThreshold_EmitsOrbitNotTap()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));

        var moveEvents = r.PointerMove(1, new Point2D(120, 100), T(50));
        Assert.Contains(TouchGestureKind.OrbitBegin, Kinds(moveEvents));

        var upEvents = r.PointerUp(1, new Point2D(120, 100), T(100));
        Assert.Equal(new[] { TouchGestureKind.OrbitEnd }, Kinds(upEvents));
    }

    [Fact]
    public void Tap_DurationAboveTapMax_DoesNotEmitTap()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        var upEvents = r.PointerUp(1, new Point2D(100, 100), T(600));
        Assert.DoesNotContain(TouchGestureKind.Tap, Kinds(upEvents));
    }

    // -- Double tap ------------------------------------------------------

    [Fact]
    public void DoubleTap_TwoTapsCloseInTimeAndSpace_EmitsDoubleTap()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerUp(1, new Point2D(100, 100), T(100));

        r.PointerDown(2, new Point2D(105, 105), T(200));
        var events = r.PointerUp(2, new Point2D(105, 105), T(280));

        Assert.Contains(TouchGestureKind.DoubleTap, Kinds(events));
    }

    [Fact]
    public void DoubleTap_SecondTapTooFar_EmitsTwoTapsNoDoubleTap()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerUp(1, new Point2D(100, 100), T(100));

        r.PointerDown(2, new Point2D(200, 200), T(200));
        var events = r.PointerUp(2, new Point2D(200, 200), T(280));

        Assert.DoesNotContain(TouchGestureKind.DoubleTap, Kinds(events));
        Assert.Contains(TouchGestureKind.Tap, Kinds(events));
    }

    [Fact]
    public void DoubleTap_SecondTapTooLate_EmitsTwoTapsNoDoubleTap()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerUp(1, new Point2D(100, 100), T(100));

        r.PointerDown(2, new Point2D(105, 105), T(800));
        var events = r.PointerUp(2, new Point2D(105, 105), T(880));

        Assert.DoesNotContain(TouchGestureKind.DoubleTap, Kinds(events));
        Assert.Contains(TouchGestureKind.Tap, Kinds(events));
    }

    // -- Orbit -----------------------------------------------------------

    [Fact]
    public void Orbit_FirstMoveExceedsThreshold_EmitsBeginAndDelta()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));

        var events = r.PointerMove(1, new Point2D(120, 100), T(20));

        Assert.Equal(2, events.Count);
        Assert.Equal(TouchGestureKind.OrbitBegin, events[0].Kind);
        Assert.Equal(new Point2D(100, 100), events[0].Position);
        Assert.Equal(TouchGestureKind.OrbitDelta, events[1].Kind);
        Assert.Equal(new Vector2D(20, 0), events[1].PixelDelta);
    }

    [Fact]
    public void Orbit_SubsequentMoves_EmitOnlyDelta()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerMove(1, new Point2D(120, 100), T(20));

        var events = r.PointerMove(1, new Point2D(125, 105), T(30));

        Assert.Equal(new[] { TouchGestureKind.OrbitDelta }, Kinds(events));
        Assert.Equal(new Vector2D(5, 5), events[0].PixelDelta);
    }

    [Fact]
    public void Orbit_PointerUp_EmitsOrbitEndOnly()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerMove(1, new Point2D(120, 100), T(20));

        var events = r.PointerUp(1, new Point2D(120, 100), T(50));

        Assert.Equal(new[] { TouchGestureKind.OrbitEnd }, Kinds(events));
    }

    // -- Pinch / pan (two-finger) ----------------------------------------

    [Fact]
    public void PanZoom_SecondFingerDown_EmitsPanZoomBeginWithCentroid()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));

        var events = r.PointerDown(2, new Point2D(200, 200), T(20));

        Assert.Equal(new[] { TouchGestureKind.PanZoomBegin }, Kinds(events));
        Assert.Equal(new Point2D(150, 150), events[0].Position);
    }

    [Fact]
    public void PanZoom_FingersSpreadApart_EmitsPanZoomDeltaWithScaleAboveOne()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerDown(2, new Point2D(200, 100), T(20));
        r.PointerMove(1, new Point2D(50, 100), T(40));
        var events = r.PointerMove(2, new Point2D(250, 100), T(60));

        Assert.Single(events);
        Assert.Equal(TouchGestureKind.PanZoomDelta, events[0].Kind);
        Assert.True(events[0].PinchScale > 1.0,
            $"expected pinchScale > 1, got {events[0].PinchScale}");
    }

    [Fact]
    public void PanZoom_FingersComeTogether_EmitsPanZoomDeltaWithScaleBelowOne()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(50, 100), T(0));
        r.PointerDown(2, new Point2D(250, 100), T(20));
        r.PointerMove(1, new Point2D(100, 100), T(40));
        var events = r.PointerMove(2, new Point2D(200, 100), T(60));

        Assert.Single(events);
        Assert.True(events[0].PinchScale < 1.0,
            $"expected pinchScale < 1, got {events[0].PinchScale}");
    }

    [Fact]
    public void PanZoom_BothFingersTranslate_EmitsPanZoomDeltaWithCentroidShift()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerDown(2, new Point2D(200, 100), T(20));

        var first = r.PointerMove(1, new Point2D(150, 100), T(40));
        var second = r.PointerMove(2, new Point2D(250, 100), T(60));

        Assert.Equal(new[] { TouchGestureKind.PanZoomDelta }, Kinds(first));
        Assert.Equal(new[] { TouchGestureKind.PanZoomDelta }, Kinds(second));

        double cumulativeX = first[0].PixelDelta.X + second[0].PixelDelta.X;
        double cumulativeY = first[0].PixelDelta.Y + second[0].PixelDelta.Y;
        double netPinch = first[0].PinchScale * second[0].PinchScale;

        Assert.Equal(50.0, cumulativeX, 6);
        Assert.Equal(0.0, cumulativeY, 6);
        Assert.Equal(1.0, netPinch, 6);
    }

    [Fact]
    public void PanZoom_SecondFingerUp_EmitsPanZoomEndAndLocksRemaining()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerDown(2, new Point2D(200, 100), T(20));

        var upEvents = r.PointerUp(2, new Point2D(200, 100), T(40));
        Assert.Equal(new[] { TouchGestureKind.PanZoomEnd }, Kinds(upEvents));

        var moveEvents = r.PointerMove(1, new Point2D(180, 80), T(60));
        Assert.Empty(moveEvents);
    }

    [Fact]
    public void PanZoom_AllFingersUp_ReturnsToNoneState()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerDown(2, new Point2D(200, 100), T(20));
        r.PointerUp(2, new Point2D(200, 100), T(40));
        r.PointerUp(1, new Point2D(100, 100), T(60));

        r.PointerDown(3, new Point2D(300, 300), T(80));
        var tapEvents = r.PointerUp(3, new Point2D(300, 300), T(150));
        Assert.Equal(new[] { TouchGestureKind.Tap }, Kinds(tapEvents));
    }

    // -- Long press ------------------------------------------------------

    [Fact]
    public void LongPress_TickAfterThreshold_EmitsLongPress()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));

        var ticked = r.Tick(T(550));

        Assert.Equal(new[] { TouchGestureKind.LongPress }, Kinds(ticked));
        Assert.Equal(new Point2D(100, 100), ticked[0].Position);
    }

    [Fact]
    public void LongPress_TickBeforeThreshold_DoesNotFire()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));

        Assert.Empty(r.Tick(T(300)));
    }

    [Fact]
    public void LongPress_AfterMovementAboveLongPressBudget_DoesNotFire()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerMove(1, new Point2D(107, 100), T(100));

        Assert.Empty(r.Tick(T(600)));
    }

    [Fact]
    public void LongPress_FiresAtMostOncePerPress()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        Assert.NotEmpty(r.Tick(T(550)));
        Assert.Empty(r.Tick(T(900)));
    }

    [Fact]
    public void LongPress_SecondFingerArrives_LongPressNoLongerEligible()
    {
        var r = new ViewportTouchGestureRecognizer();
        r.PointerDown(1, new Point2D(100, 100), T(0));
        r.PointerDown(2, new Point2D(200, 200), T(100));

        Assert.Empty(r.Tick(T(700)));
    }
}
