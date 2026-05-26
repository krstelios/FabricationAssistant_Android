using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Input.Gestures.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class ViewportInteractionAdapterTests
{
    [Fact]
    public void OrbitBegin_WithPickedPivot_NormalizesNarrowFov()
    {
        var camera = NarrowFovCamera();
        var pivot = new Vector3d(0, 0, 0);
        int renders = 0;

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-1, -1, -1), new Vector3d(1, 1, 1)),
            aspectAccessor: () => 1.0,
            requestRender: () => renders++,
            pivotPicker: _ => pivot);

        adapter.OnGesture(new TouchGestureEvent(
            TouchGestureKind.OrbitBegin,
            new Point2D(100, 100),
            Vector2D.Zero,
            1.0));

        Assert.Equal(System.Math.PI / 4.0, camera.FieldOfView, 9);
        Assert.True(Vector3d.Distance(camera.Position, pivot) < 1000.0);
        Assert.Equal(0, renders);
    }

    [Fact]
    public void PanZoomBegin_WithPickedPivot_NormalizesNarrowFov()
    {
        var camera = NarrowFovCamera();
        var pivot = new Vector3d(0, 0, 0);

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-1, -1, -1), new Vector3d(1, 1, 1)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            pivotPicker: _ => pivot);

        adapter.OnGesture(new TouchGestureEvent(
            TouchGestureKind.PanZoomBegin,
            new Point2D(100, 100),
            Vector2D.Zero,
            1.0));

        Assert.Equal(System.Math.PI / 4.0, camera.FieldOfView, 9);
        Assert.True(Vector3d.Distance(camera.Position, pivot) < 1000.0);
    }

    [Fact]
    public void OrbitBegin_WhenPickMisses_ReusesLastResolvedPivot()
    {
        var camera = InteractionCamera();
        var lastPivot = new Vector3d(5, 0, 0);
        Vector3d? nextPick = lastPivot;

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-20, -20, -20), new Vector3d(20, 20, 20)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            pivotPicker: _ => nextPick);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitBegin, new Point2D(100, 100), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitEnd, new Point2D(100, 100), Vector2D.Zero, 1.0));

        nextPick = null;
        double distanceBefore = Vector3d.Distance(camera.Position, lastPivot);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitBegin, new Point2D(300, 300), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitDelta, new Point2D(360, 300), new Vector2D(60, 0), 1.0));

        double distanceAfter = Vector3d.Distance(camera.Position, lastPivot);
        Assert.Equal(distanceBefore, distanceAfter, 9);
    }

    [Fact]
    public void SetNavigationPivot_SeedsNextOrbitWhenPickMisses()
    {
        var camera = InteractionCamera();
        var selectedPivot = new Vector3d(5, 0, 0);

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-20, -20, -20), new Vector3d(20, 20, 20)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            pivotPicker: _ => null);

        adapter.SetNavigationPivot(selectedPivot);
        double distanceBefore = Vector3d.Distance(camera.Position, selectedPivot);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitBegin, new Point2D(300, 300), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitDelta, new Point2D(360, 300), new Vector2D(60, 0), 1.0));

        double distanceAfter = Vector3d.Distance(camera.Position, selectedPivot);
        Assert.Equal(distanceBefore, distanceAfter, 9);
    }

    [Fact]
    public void PanZoomBegin_WhenPickMisses_ReusesLastResolvedPivot()
    {
        var camera = InteractionCamera();
        var lastPivot = new Vector3d(5, 0, 0);
        Vector3d? nextPick = lastPivot;

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-20, -20, -20), new Vector3d(20, 20, 20)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            pivotPicker: _ => nextPick);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomBegin, new Point2D(100, 100), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomEnd, new Point2D(100, 100), Vector2D.Zero, 1.0));

        nextPick = null;
        double distanceBefore = Vector3d.Distance(camera.Position, lastPivot);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomBegin, new Point2D(300, 300), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomDelta, new Point2D(300, 300), Vector2D.Zero, 2.0));

        double distanceAfter = Vector3d.Distance(camera.Position, lastPivot);
        Assert.Equal(distanceBefore * 0.5, distanceAfter, 9);
    }

    [Fact]
    public void OrbitDelta_BelowTouchDeadband_DoesNotMoveCamera()
    {
        var camera = InteractionCamera();
        Vector3d beforePosition = camera.Position;
        Vector3d beforeTarget = camera.Target;
        int renders = 0;

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-20, -20, -20), new Vector3d(20, 20, 20)),
            aspectAccessor: () => 1.0,
            requestRender: () => renders++,
            pivotPicker: _ => Vector3d.Zero);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitBegin, new Point2D(100, 100), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitDelta, new Point2D(100.1, 100.1), new Vector2D(0.1, 0.1), 1.0));

        Assert.Equal(0, renders);
        Assert.True(Vector3d.Distance(beforePosition, camera.Position) < 1e-12);
        Assert.True(Vector3d.Distance(beforeTarget, camera.Target) < 1e-12);
    }

    [Fact]
    public void OrbitDelta_FirstSampleIsRampedIn()
    {
        var filteredCamera = InteractionCamera();
        var rawCamera = InteractionCamera();
        Vector3d beforePosition = filteredCamera.Position;
        Vector3d pivot = Vector3d.Zero;

        var adapter = new ViewportInteractionAdapter(
            filteredCamera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-20, -20, -20), new Vector3d(20, 20, 20)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            pivotPicker: _ => pivot);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitBegin, new Point2D(100, 100), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitDelta, new Point2D(120, 100), new Vector2D(20, 0), 1.0));

        rawCamera.OrbitAroundPoint(pivot, 20 * 0.005, 0.0);

        double filteredDistance = Vector3d.Distance(beforePosition, filteredCamera.Position);
        double rawDistance = Vector3d.Distance(beforePosition, rawCamera.Position);

        Assert.True(filteredDistance > 0.0);
        Assert.True(filteredDistance < rawDistance);
    }

    [Fact]
    public void OrbitGesture_WhenFixedViewLocked_DoesNotMoveCamera()
    {
        var camera = InteractionCamera();
        Vector3d beforePosition = camera.Position;
        Vector3d beforeTarget = camera.Target;
        int renders = 0;

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-20, -20, -20), new Vector3d(20, 20, 20)),
            aspectAccessor: () => 1.0,
            requestRender: () => renders++,
            pivotPicker: _ => Vector3d.Zero,
            isFixedViewLockedAccessor: () => true);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitBegin, new Point2D(100, 100), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitDelta, new Point2D(160, 100), new Vector2D(60, 0), 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.OrbitEnd, new Point2D(160, 100), Vector2D.Zero, 1.0));

        Assert.Equal(0, renders);
        Assert.True(Vector3d.Distance(beforePosition, camera.Position) < 1e-12);
        Assert.True(Vector3d.Distance(beforeTarget, camera.Target) < 1e-12);
    }

    [Fact]
    public void OrthographicPan_UsesViewportWorldUnitsPerDip()
    {
        var camera = OrthographicCamera(distance: 1000.0, orthoWidth: 100.0);

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-50, -50, -50), new Vector3d(50, 50, 50)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            viewportWidthDipAccessor: () => 500.0,
            pivotPicker: _ => Vector3d.Zero);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomBegin, new Point2D(100, 100), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomDelta, new Point2D(120, 100), new Vector2D(20, 0), 1.0));

        Assert.Equal(-4.0, camera.Target.X, precision: 9);
        Assert.Equal(0.0, camera.Target.Y, precision: 9);
    }

    [Fact]
    public void OrthographicPinch_UsesRawFingerScale()
    {
        var camera = OrthographicCamera(distance: 100.0, orthoWidth: 100.0);

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => new BoundingBox(new Vector3d(-50, -50, -50), new Vector3d(50, 50, 50)),
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            viewportWidthDipAccessor: () => 400.0,
            pivotPicker: _ => Vector3d.Zero);

        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomBegin, new Point2D(200, 200), Vector2D.Zero, 1.0));
        adapter.OnGesture(new TouchGestureEvent(TouchGestureKind.PanZoomDelta, new Point2D(200, 200), Vector2D.Zero, 2.0));

        Assert.Equal(50.0, camera.OrthoWidth, precision: 9);
        Assert.Equal(0.0, camera.Target.X, precision: 9);
        Assert.Equal(0.0, camera.Target.Z, precision: 9);
    }

    [Fact]
    public void OrthographicPinch_KeepsForwardDistanceStable()
    {
        var camera = OrthographicCamera(distance: 100.0, orthoWidth: 100.0);
        camera.Position = new Vector3d(0, -100, 0);
        camera.Target = Vector3d.Zero;
        Vector3d pivot = new(10, 0, 0);

        camera.DollyZoomAroundPivot(pivot, 2.0);

        Assert.Equal(50.0, camera.OrthoWidth, precision: 9);
        Assert.Equal(100.0, camera.Distance, precision: 9);
        Assert.Equal(5.0, camera.Position.X, precision: 9);
        Assert.Equal(5.0, camera.Target.X, precision: 9);
    }

    [Fact]
    public void FitToScene_UsesNavigationBoundsButRefreshesClipPlanesFromClipBounds()
    {
        var camera = InteractionCamera();
        var navigationBounds = new BoundingBox(new Vector3d(90, -1, -1), new Vector3d(110, 1, 1));
        var clipBounds = new BoundingBox(new Vector3d(-1000, -1000, -1000), new Vector3d(1000, 1000, 1000));

        var adapter = new ViewportInteractionAdapter(
            camera,
            boundsAccessor: () => navigationBounds,
            aspectAccessor: () => 1.0,
            requestRender: () => { },
            clipBoundsAccessor: () => clipBounds);

        adapter.FitToScene();

        Assert.Equal(navigationBounds.Center.X, camera.Target.X, precision: 9);
        Assert.Equal(navigationBounds.Center.Y, camera.Target.Y, precision: 9);
        Assert.Equal(navigationBounds.Center.Z, camera.Target.Z, precision: 9);
        Assert.Equal(navigationBounds.Diagonal * 0.001, camera.MinOrthoWidth, precision: 9);
        Assert.True(camera.FarPlane > 100000.0);
    }

    private static CameraState NarrowFovCamera()
        => new()
        {
            Position = new Vector3d(0, 0, 1000),
            Target = new Vector3d(0, 0, 0),
            UpDirection = Vector3d.UnitY,
            WorldUpDirection = Vector3d.UnitY,
            IsPerspective = true,
            FieldOfView = System.Math.PI / 36.0,
            NearPlane = 0.1,
            FarPlane = 5000,
        };

    private static CameraState InteractionCamera()
        => new()
        {
            Position = new Vector3d(0, -10, 0),
            Target = Vector3d.Zero,
            UpDirection = Vector3d.UnitZ,
            WorldUpDirection = Vector3d.UnitZ,
            IsPerspective = true,
            FieldOfView = System.Math.PI / 4.0,
            NearPlane = 0.01,
            FarPlane = 1000,
        };

    private static CameraState OrthographicCamera(double distance, double orthoWidth)
        => new()
        {
            Position = new Vector3d(0, -distance, 0),
            Target = Vector3d.Zero,
            UpDirection = Vector3d.UnitZ,
            WorldUpDirection = Vector3d.UnitZ,
            IsPerspective = false,
            OrthoWidth = orthoWidth,
            NearPlane = 0.01,
            FarPlane = distance * 2.0,
        };
}
