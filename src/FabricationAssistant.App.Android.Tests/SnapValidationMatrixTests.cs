using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

[Collection("EdgeSnapState")]
public sealed class SnapValidationMatrixTests
{
    private const double EdgeTol = 0.1;
    private const double EndpointTol = 0.05;
    private readonly EdgeSnapService _svc = new();

    public SnapValidationMatrixTests()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;
    }

    // §11 Basic geometry: dense segmented model — many independent edges. Snapping
    // near one isolated segment's midpoint returns it; empty space returns null.
    [Fact]
    public void DenseIndependentSegments_SnapToNearestMidpoint_AndMissEmptySpace()
    {
        // 9 isolated horizontal unit segments on a 3x3 grid, spaced 5 apart.
        var list = new System.Collections.Generic.List<float>();
        for (int gy = 0; gy < 3; gy++)
        for (int gx = 0; gx < 3; gx++)
        {
            float x = gx * 5f, y = gy * 5f;
            list.AddRange(new[] { x, y, 0f, x + 1f, y, 0f });
        }
        float[] edges = list.ToArray();

        // Midpoint of the centre cell's segment is (5.5, 5, 0).
        EdgeSnapResult? hit = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(5.5, 5, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);
        Assert.NotNull(hit);
        Assert.Equal(5.5, hit!.Value.WorldPoint.X, 6);
        Assert.Equal(5.0, hit.Value.WorldPoint.Y, 6);

        // A point in the gap between cells snaps to nothing.
        EdgeSnapResult? miss = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(3.0, 2.5, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);
        Assert.Null(miss);
    }

    // §11 Arc geometry: very small arc segments still resolve to the arc midpoint.
    [Fact]
    public void VerySmallArcSegments_SnapToArcMidpoint()
    {
        float[] edges = ArcEdges(radius: 0.5, startRadians: 0.0, sweepRadians: System.Math.PI / 2.0, segments: 16);
        double c = System.Math.Sqrt(0.5) * 0.5; // midpoint at 45 deg on r=0.5

        EdgeSnapResult? hit = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(c, c, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);

        Assert.NotNull(hit);
        Assert.Equal(c, hit!.Value.WorldPoint.X, 5);
        Assert.Equal(c, hit.Value.WorldPoint.Y, 5);
    }

    // §11 Basic geometry: a non-square rectangle exposes its corners (endpoints),
    // not the interiors of its sides.
    [Fact]
    public void NonSquareRectangle_ExposesCorners_NotSideInteriors()
    {
        EdgeSnapService.MidpointSnapEnabled = false;
        // 4x2 rectangle as a closed loop.
        float[] edges =
        {
            0,0,0, 4,0,0,
            4,0,0, 4,2,0,
            4,2,0, 0,2,0,
            0,2,0, 0,0,0,
        };

        EdgeSnapResult? corner = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(4, 0, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);
        EdgeSnapResult? sideInterior = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(2, 0, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);

        Assert.NotNull(corner);
        Assert.Equal(4.0, corner!.Value.WorldPoint.X, 6);
        Assert.Null(sideInterior); // midpoint off -> no snap mid-side
    }

    private static float[] ArcEdges(double radius, double startRadians, double sweepRadians, int segments)
    {
        var values = new float[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            double a0 = startRadians + sweepRadians * i / segments;
            double a1 = startRadians + sweepRadians * (i + 1) / segments;
            int o = i * 6;
            values[o] = (float)(System.Math.Cos(a0) * radius);
            values[o + 1] = (float)(System.Math.Sin(a0) * radius);
            values[o + 2] = 0f;
            values[o + 3] = (float)(System.Math.Cos(a1) * radius);
            values[o + 4] = (float)(System.Math.Sin(a1) * radius);
            values[o + 5] = 0f;
        }
        return values;
    }
}
