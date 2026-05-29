using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidEdgeSnapServiceTests
{
    private const double EdgeTolerance = 0.1;
    private const double EndpointTolerance = 0.05;

    private readonly EdgeSnapService _service = new();

    [Fact]
    public void TrySnap_SingleStraightEdge_SnapsToMiddle()
    {
        EdgeSnapResult? result = TrySnapAtX(0.5, Edges((0, 0), (1, 0)));

        Assert.NotNull(result);
        AssertClose(new Vector3d(0.5, 0, 0), result.Value.WorldPoint);
    }

    [Fact]
    public void TrySnap_SingleStraightEdge_DoesNotSnapToArbitraryPoint()
    {
        EdgeSnapResult? result = TrySnapAtX(0.25, Edges((0, 0), (1, 0)));

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_WeldedStraightEdges_SnapsToRunMiddle()
    {
        EdgeSnapResult? result = TrySnapAtX(1.5, Edges((0, 0), (1, 0), (1, 0), (2, 0), (2, 0), (3, 0)));

        Assert.NotNull(result);
        AssertClose(new Vector3d(1.5, 0, 0), result.Value.WorldPoint);
    }

    [Fact]
    public void TrySnap_WeldedStraightEdges_DoesNotSnapToInternalJoint()
    {
        EdgeSnapResult? result = TrySnapAtX(1.0, Edges((0, 0), (1, 0), (1, 0), (2, 0), (2, 0), (3, 0)));

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_WeldedStraightEdges_SnapsToRunEndpoint()
    {
        EdgeSnapResult? result = TrySnapAtX(3.0, Edges((0, 0), (1, 0), (1, 0), (2, 0), (2, 0), (3, 0)));

        Assert.NotNull(result);
        AssertClose(new Vector3d(3, 0, 0), result.Value.WorldPoint);
    }

    [Fact]
    public void TrySnap_WhenVisibilityFilterRejectsTarget_ReturnsNull()
    {
        Func<EdgeSnapVisibilityRequest, bool>? previous = EdgeSnapService.VisibilityFilter;
        EdgeSnapService.VisibilityFilter = _ => false;

        try
        {
            EdgeSnapResult? result = TrySnapAtX(0.5, Edges((0, 0), (1, 0)));

            Assert.Null(result);
        }
        finally
        {
            EdgeSnapService.VisibilityFilter = previous;
        }
    }

    private EdgeSnapResult? TrySnapAtX(double x, float[] edgePositions)
        => _service.TrySnap(
            edgePositions,
            Matrix4d.Identity,
            new Vector3d(x, 1, 0),
            new Vector3d(0, -1, 0),
            EdgeTolerance,
            EndpointTolerance);

    private static float[] Edges(params (float X, float Y)[] points)
    {
        var values = new float[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            values[i * 3] = points[i].X;
            values[i * 3 + 1] = points[i].Y;
            values[i * 3 + 2] = 0f;
        }

        return values;
    }

    private static void AssertClose(Vector3d expected, Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 9);
        Assert.Equal(expected.Y, actual.Y, precision: 9);
        Assert.Equal(expected.Z, actual.Z, precision: 9);
    }
}
