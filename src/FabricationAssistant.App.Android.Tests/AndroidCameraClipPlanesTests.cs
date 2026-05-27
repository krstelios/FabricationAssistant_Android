using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Input.Gestures.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidCameraClipPlanesTests
{
    [Fact]
    public void Update_PerspectiveCloseUpInsideBounds_TightensFarPlane()
    {
        var camera = new CameraState
        {
            Position = new Vector3d(0, -0.18, 0),
            Target = new Vector3d(0, 0.32, 0),
            UpDirection = Vector3d.UnitZ,
            WorldUpDirection = Vector3d.UnitZ,
            IsPerspective = true,
            NearPlane = 0.001,
            FarPlane = 1_000_000,
        };
        var bounds = new BoundingBox(new Vector3d(-1, -1, -1), new Vector3d(1, 1, 1));

        AndroidCameraClipPlanes.Update(camera, bounds);

        Assert.True(camera.NearPlane >= bounds.Diagonal * 0.0005);
        Assert.InRange(camera.FarPlane, 1.0, 2.0);
        Assert.True(camera.FarPlane / camera.NearPlane < 1000.0);
    }

    [Fact]
    public void Update_PerspectiveOutsideBounds_EnclosesProjectedDepthRange()
    {
        var camera = new CameraState
        {
            Position = new Vector3d(0, -10, 0),
            Target = Vector3d.Zero,
            UpDirection = Vector3d.UnitZ,
            WorldUpDirection = Vector3d.UnitZ,
            IsPerspective = true,
        };
        var bounds = new BoundingBox(new Vector3d(-1, -1, -1), new Vector3d(1, 1, 1));

        AndroidCameraClipPlanes.Update(camera, bounds);

        Assert.InRange(camera.NearPlane, 8.0, 9.0);
        Assert.InRange(camera.FarPlane, 11.0, 12.0);
        Assert.True(camera.NearPlane < camera.FarPlane);
    }

    [Fact]
    public void IncludeGroundGrid_WhenGridVisible_ExpandsToRenderedGridQuad()
    {
        var bounds = new BoundingBox(new Vector3d(-1, -2, 3), new Vector3d(3, 4, 5));
        double diagonal = bounds.Diagonal;
        double scale = diagonal * 8.0;
        Vector3d center = bounds.Center;

        BoundingBox expanded = AndroidCameraClipPlanes.IncludeGroundGrid(
            bounds,
            showGrid: true,
            shiftGridToModelMin: true);

        Assert.True(expanded.IsValid);
        Assert.Equal(center.X - scale, expanded.Min.X, precision: 9);
        Assert.Equal(center.X + scale, expanded.Max.X, precision: 9);
        Assert.Equal(center.Y - scale, expanded.Min.Y, precision: 9);
        Assert.Equal(center.Y + scale, expanded.Max.Y, precision: 9);
        Assert.Equal(bounds.Min.Z - diagonal * 0.001, expanded.Min.Z, precision: 9);
        Assert.Equal(bounds.Max.Z, expanded.Max.Z, precision: 9);
    }

    [Fact]
    public void IncludeGroundGrid_WhenGridHidden_LeavesBoundsUnchanged()
    {
        var bounds = new BoundingBox(new Vector3d(-1, -2, 3), new Vector3d(3, 4, 5));

        BoundingBox expanded = AndroidCameraClipPlanes.IncludeGroundGrid(
            bounds,
            showGrid: false,
            shiftGridToModelMin: true);

        Assert.Equal(bounds, expanded);
    }
}
