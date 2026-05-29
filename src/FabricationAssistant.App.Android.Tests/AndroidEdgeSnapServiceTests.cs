using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidEdgeSnapServiceTests
{
    private const double EdgeTolerance = 0.1;
    private const double EndpointTolerance = 0.05;

    private readonly EdgeSnapService _service = new();

    public AndroidEdgeSnapServiceTests()
    {
        ResetSnapSettings();
    }

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

    [Fact]
    public void TrySnap_EndpointSnapDisabled_DoesNotSnapToEndpoint()
    {
        EdgeSnapService.EndpointSnapEnabled = false;

        EdgeSnapResult? result = TrySnapAtX(1.0, Edges((0, 0), (1, 0)));

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_MidpointSnapDisabled_DoesNotSnapToMiddle()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        EdgeSnapResult? result = TrySnapAtX(0.5, Edges((0, 0), (1, 0)));

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_WhenClosestTargetIsHidden_FallsBackToVisibleCandidate()
    {
        EdgeSnapService.MidpointSnapEnabled = false;
        EdgeSnapService.VisibilityFilter = request => request.WorldPoint.X > 0.01;

        EdgeSnapResult? result = _service.TrySnap(
            Edges((0, 1), (0, 0.5f), (0.03f, 1), (1, 1)),
            Matrix4d.Identity,
            new Vector3d(0, 2, 0),
            new Vector3d(0, -1, 0),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(0.03, 1, 0), result.Value.WorldPoint, precision: 7);
    }

    [Fact]
    public void TrySnap_SegmentedArc_SnapsToArcMidpoint()
    {
        float[] edges = ArcEdges(radius: 1.0, startRadians: 0.0, sweepRadians: Math.PI / 2.0, segments: 8);
        double coordinate = Math.Sqrt(0.5);

        EdgeSnapResult? result = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(coordinate, coordinate, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(coordinate, coordinate, 0), result.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_TwoSegmentShallowArc_SnapsToArcMidpoint()
    {
        double midpointAngle = Math.PI / 12.0;
        var expected = new Vector3d(Math.Cos(midpointAngle), Math.Sin(midpointAngle), 0);

        EdgeSnapResult? result = _service.TrySnap(
            ArcEdges(radius: 1.0, startRadians: 0.0, sweepRadians: Math.PI / 6.0, segments: 2),
            Matrix4d.Identity,
            expected + new Vector3d(0, 0, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(expected, result.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_ArcWithTangentLine_DoesNotWeldLineIntoArc()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        float[] edges = ArcWithTangentLineEdges(
            radius: 10.0,
            startRadians: 0.0,
            sweepRadians: Math.PI / 2.0,
            arcSegments: 8,
            lineLength: 4.0,
            lineSegments: 2);

        EdgeSnapResult? arcEnd = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(0, 10, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        EdgeSnapResult? lineEnd = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(-4, 10, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);
        EdgeSnapResult? internalArcBoundary = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(10.0 * Math.Sqrt(0.5), 10.0 * Math.Sqrt(0.5), 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);
        EdgeSnapResult? internalLineBoundary = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(-2, 10, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(arcEnd);
        Assert.NotNull(lineEnd);
        Assert.Null(internalArcBoundary);
        Assert.Null(internalLineBoundary);
        AssertClose(new Vector3d(0, 10, 0), arcEnd.Value.WorldPoint, precision: 6);
        AssertClose(new Vector3d(-4, 10, 0), lineEnd.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_ArcWithShortTangentLine_KeepsArcEndSelectable()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        float[] edges = ArcWithTangentLineEdges(
            radius: 10.0,
            startRadians: 0.0,
            sweepRadians: Math.PI / 2.0,
            arcSegments: 8,
            lineLength: 0.8,
            lineSegments: 1);

        EdgeSnapResult? arcEnd = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(0, 10, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        EdgeSnapResult? lineEnd = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(-0.8, 10, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(arcEnd);
        Assert.NotNull(lineEnd);
        AssertClose(new Vector3d(0, 10, 0), arcEnd.Value.WorldPoint, precision: 6);
        AssertClose(new Vector3d(-0.8, 10, 0), lineEnd.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_LineArcLineChain_DoesNotExposeArcSegmentBoundaries()
    {
        EdgeSnapService.MidpointSnapEnabled = false;
        float[] edges = LineArcLineEdges(radius: 1.0, arcSegments: 8);

        EdgeSnapResult? arcStart = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(0, 0, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);
        EdgeSnapResult? arcEnd = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(1, 1, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);
        EdgeSnapResult? internalArcBoundary = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(0.7071067811865476, 0.2928932188134524, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(arcStart);
        Assert.NotNull(arcEnd);
        Assert.Null(internalArcBoundary);
        AssertClose(new Vector3d(0, 0, 0), arcStart.Value.WorldPoint, precision: 6);
        AssertClose(new Vector3d(1, 1, 0), arcEnd.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_DuplicatedSegmentedArc_SnapsToArcMidpoint()
    {
        float[] edges = DuplicateSegments(ArcEdges(radius: 1.0, startRadians: 0.0, sweepRadians: Math.PI / 2.0, segments: 8));
        double coordinate = Math.Sqrt(0.5);

        EdgeSnapResult? result = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(coordinate, coordinate, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(coordinate, coordinate, 0), result.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_BranchedSegmentedArc_UsesSmoothArcMidpoint()
    {
        EdgeSnapService.EndpointSnapEnabled = false;

        double radius = 10.0;
        double coordinate = radius * Math.Sqrt(0.5);

        EdgeSnapResult? result = _service.TrySnap(
            ArcEdgesWithSpurs(radius, startRadians: 0.0, sweepRadians: Math.PI / 2.0, segments: 8, spurLength: 1.0),
            Matrix4d.Identity,
            new Vector3d(coordinate, coordinate, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(coordinate, coordinate, 0), result.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_TwoSegmentCorner_IsNotForcedToArc()
    {
        EdgeSnapResult? result = _service.TrySnap(
            Edges((0, 0), (1, 0), (1, 0), (1, 1)),
            Matrix4d.Identity,
            new Vector3d(0.707, 0.293, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_SlightlyNoisyClosedCircle_DoesNotExposeInternalSegmentEndpoints()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        EdgeSnapResult? result = _service.TrySnap(
            NoisyClosedCircleEdges(radius: 60.0, segments: 64, radialNoise: 0.18),
            Matrix4d.Identity,
            new Vector3d(60.18, 0, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_ClosedSegmentedCircle_DoesNotExposeInternalSegmentEndpoints()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        EdgeSnapResult? result = _service.TrySnap(
            ClosedCircleEdges(radius: 1.0, segments: 32),
            Matrix4d.Identity,
            new Vector3d(1, 0, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_ClosedSegmentedCircle_SnapsToStableMidpointTarget()
    {
        double coordinate = Math.Sqrt(0.5);

        EdgeSnapResult? result = _service.TrySnap(
            ClosedCircleEdges(radius: 1.0, segments: 32),
            Matrix4d.Identity,
            new Vector3d(coordinate, coordinate, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(coordinate, coordinate, 0), result.Value.WorldPoint, precision: 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(Math.PI / 8.0)]
    [InlineData(Math.PI / 4.0)]
    [InlineData(Math.PI / 2.0)]
    public void TrySnap_ClosedSegmentedCircle_TunedTargetsCoverLoop(double angle)
    {
        var expected = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);

        EdgeSnapResult? result = _service.TrySnap(
            ClosedCircleEdges(radius: 1.0, segments: 32),
            Matrix4d.Identity,
            expected + new Vector3d(0, 0, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(expected, result.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_ClosedPolygon_StillExposesCorners()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        EdgeSnapResult? result = _service.TrySnap(
            Edges((0, 0), (1, 0), (1, 0), (1, 1), (1, 1), (0, 1), (0, 1), (0, 0)),
            Matrix4d.Identity,
            new Vector3d(1, 0, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(1, 0, 0), result.Value.WorldPoint);
    }

    [Fact]
    public void TrySnap_SmoothClosedRoundedLoop_DoesNotExposeSegmentBoundaries()
    {
        EdgeSnapService.MidpointSnapEnabled = false;

        EdgeSnapResult? result = _service.TrySnap(
            ClosedLoopEdges((1, 0), (2, 0), (3, 1), (3, 2), (2, 3), (1, 3), (0, 2), (0, 1)),
            Matrix4d.Identity,
            new Vector3d(3, 1, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.Null(result);
    }

    [Fact]
    public void TrySnap_ClosedRoundedRectangle_WeldsArcAndStraightRuns()
    {
        EdgeSnapService.MidpointSnapEnabled = false;
        float[] edges = RoundedRectangleEdges(width: 6.0, height: 4.0, radius: 1.0, arcSegments: 8);

        EdgeSnapResult? arcStart = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(2, -2, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        EdgeSnapResult? arcInternalBoundary = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(2.7071067811865475, -1.7071067811865475, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        EdgeSnapResult? straightInternalPoint = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(0, -2, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(arcStart);
        Assert.Null(arcInternalBoundary);
        Assert.Null(straightInternalPoint);
        AssertClose(new Vector3d(2, -2, 0), arcStart.Value.WorldPoint, precision: 6);
    }

    [Fact]
    public void TrySnap_ClosedRoundedRectangle_SnapsToArcMidpoint()
    {
        float[] edges = RoundedRectangleEdges(width: 6.0, height: 4.0, radius: 1.0, arcSegments: 8);

        EdgeSnapResult? result = _service.TrySnap(
            edges,
            Matrix4d.Identity,
            new Vector3d(2.7071067811865475, -1.7071067811865475, 1),
            new Vector3d(0, 0, -1),
            EdgeTolerance,
            EndpointTolerance);

        Assert.NotNull(result);
        AssertClose(new Vector3d(2.7071067811865475, -1.7071067811865475, 0), result.Value.WorldPoint, precision: 6);
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

    private static float[] ArcEdges(double radius, double startRadians, double sweepRadians, int segments)
    {
        var points = new (float X, float Y)[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double angle = startRadians + sweepRadians * i / segments;
            points[i] = ((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
        }

        var values = new float[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            int offset = i * 6;
            values[offset] = points[i].X;
            values[offset + 1] = points[i].Y;
            values[offset + 2] = 0f;
            values[offset + 3] = points[i + 1].X;
            values[offset + 4] = points[i + 1].Y;
            values[offset + 5] = 0f;
        }

        return values;
    }

    private static float[] ArcWithTangentLineEdges(
        double radius,
        double startRadians,
        double sweepRadians,
        int arcSegments,
        double lineLength,
        int lineSegments)
    {
        float[] arc = ArcEdges(radius, startRadians, sweepRadians, arcSegments);
        var values = new float[arc.Length + lineSegments * 6];
        Array.Copy(arc, values, arc.Length);

        double endAngle = startRadians + sweepRadians;
        double tangentSign = sweepRadians >= 0.0 ? 1.0 : -1.0;
        double tangentX = -Math.Sin(endAngle) * tangentSign;
        double tangentY = Math.Cos(endAngle) * tangentSign;
        double startX = Math.Cos(endAngle) * radius;
        double startY = Math.Sin(endAngle) * radius;

        int output = arc.Length;
        for (int i = 0; i < lineSegments; i++)
        {
            double t0 = i / (double)lineSegments;
            double t1 = (i + 1) / (double)lineSegments;
            values[output++] = (float)(startX + tangentX * lineLength * t0);
            values[output++] = (float)(startY + tangentY * lineLength * t0);
            values[output++] = 0f;
            values[output++] = (float)(startX + tangentX * lineLength * t1);
            values[output++] = (float)(startY + tangentY * lineLength * t1);
            values[output++] = 0f;
        }

        return values;
    }

    private static float[] LineArcLineEdges(double radius, int arcSegments)
    {
        var points = new List<(double X, double Y)>
        {
            (-2.0, 0.0),
            (0.0, 0.0),
        };

        for (int i = 1; i <= arcSegments; i++)
        {
            double angle = -Math.PI / 2.0 + (Math.PI / 2.0) * i / arcSegments;
            points.Add((Math.Cos(angle) * radius, 1.0 + Math.Sin(angle) * radius));
        }

        points.Add((1.0, 3.0));

        var values = new float[(points.Count - 1) * 6];
        for (int i = 0; i + 1 < points.Count; i++)
        {
            (double X, double Y) a = points[i];
            (double X, double Y) b = points[i + 1];
            int offset = i * 6;
            values[offset] = (float)a.X;
            values[offset + 1] = (float)a.Y;
            values[offset + 2] = 0f;
            values[offset + 3] = (float)b.X;
            values[offset + 4] = (float)b.Y;
            values[offset + 5] = 0f;
        }

        return values;
    }

    private static float[] ArcEdgesWithSpurs(double radius, double startRadians, double sweepRadians, int segments, double spurLength)
    {
        float[] arc = ArcEdges(radius, startRadians, sweepRadians, segments);
        var values = new float[arc.Length + (segments - 1) * 6];
        Array.Copy(arc, values, arc.Length);

        int output = arc.Length;
        for (int i = 1; i < segments; i++)
        {
            double angle = startRadians + sweepRadians * i / segments;
            float x = (float)(Math.Cos(angle) * radius);
            float y = (float)(Math.Sin(angle) * radius);
            values[output++] = x;
            values[output++] = y;
            values[output++] = 0f;
            values[output++] = (float)(x + Math.Cos(angle) * spurLength);
            values[output++] = (float)(y + Math.Sin(angle) * spurLength);
            values[output++] = 0f;
        }

        return values;
    }

    private static float[] DuplicateSegments(float[] edges)
    {
        var values = new float[edges.Length * 2];
        Array.Copy(edges, 0, values, 0, edges.Length);
        Array.Copy(edges, 0, values, edges.Length, edges.Length);
        return values;
    }

    private static float[] ClosedLoopEdges(params (float X, float Y)[] points)
    {
        var values = new float[points.Length * 6];
        for (int i = 0; i < points.Length; i++)
        {
            (float X, float Y) a = points[i];
            (float X, float Y) b = points[(i + 1) % points.Length];
            int offset = i * 6;
            values[offset] = a.X;
            values[offset + 1] = a.Y;
            values[offset + 2] = 0f;
            values[offset + 3] = b.X;
            values[offset + 4] = b.Y;
            values[offset + 5] = 0f;
        }

        return values;
    }

    private static float[] RoundedRectangleEdges(double width, double height, double radius, int arcSegments)
    {
        var points = new List<(double X, double Y)>();
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;

        AddPoint(halfWidth - radius, -halfHeight);
        AddArc(halfWidth - radius, -halfHeight + radius, -Math.PI / 2.0, 0.0);
        AddPoint(halfWidth, halfHeight - radius);
        AddArc(halfWidth - radius, halfHeight - radius, 0.0, Math.PI / 2.0);
        AddPoint(-halfWidth + radius, halfHeight);
        AddArc(-halfWidth + radius, halfHeight - radius, Math.PI / 2.0, Math.PI);
        AddPoint(-halfWidth, -halfHeight + radius);
        AddArc(-halfWidth + radius, -halfHeight + radius, Math.PI, Math.PI * 1.5);

        var values = new float[points.Count * 6];
        for (int i = 0; i < points.Count; i++)
        {
            (double X, double Y) a = points[i];
            (double X, double Y) b = points[(i + 1) % points.Count];
            int offset = i * 6;
            values[offset] = (float)a.X;
            values[offset + 1] = (float)a.Y;
            values[offset + 2] = 0f;
            values[offset + 3] = (float)b.X;
            values[offset + 4] = (float)b.Y;
            values[offset + 5] = 0f;
        }

        return values;

        void AddArc(double centerX, double centerY, double startAngle, double endAngle)
        {
            for (int i = 1; i <= arcSegments; i++)
            {
                double t = (double)i / arcSegments;
                double angle = startAngle + (endAngle - startAngle) * t;
                AddPoint(
                    centerX + Math.Cos(angle) * radius,
                    centerY + Math.Sin(angle) * radius);
            }
        }

        void AddPoint(double x, double y)
        {
            if (points.Count > 0)
            {
                (double X, double Y) previous = points[^1];
                if (Math.Abs(previous.X - x) <= 1.0e-9 && Math.Abs(previous.Y - y) <= 1.0e-9)
                    return;
            }

            points.Add((x, y));
        }
    }

    private static float[] NoisyClosedCircleEdges(double radius, int segments, double radialNoise)
    {
        var values = new float[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            WritePoint(i, i * 6);
            WritePoint((i + 1) % segments, i * 6 + 3);
        }

        return values;

        void WritePoint(int index, int offset)
        {
            double angle = 2.0 * Math.PI * index / segments;
            double localRadius = radius + radialNoise * Math.Sin(angle * 5.0);
            values[offset] = (float)(Math.Cos(angle) * localRadius);
            values[offset + 1] = (float)(Math.Sin(angle) * localRadius);
            values[offset + 2] = 0f;
        }
    }

    private static float[] ClosedCircleEdges(double radius, int segments)
    {
        var values = new float[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            double a0 = 2.0 * Math.PI * i / segments;
            double a1 = 2.0 * Math.PI * ((i + 1) % segments) / segments;
            int offset = i * 6;
            values[offset] = (float)(Math.Cos(a0) * radius);
            values[offset + 1] = (float)(Math.Sin(a0) * radius);
            values[offset + 2] = 0f;
            values[offset + 3] = (float)(Math.Cos(a1) * radius);
            values[offset + 4] = (float)(Math.Sin(a1) * radius);
            values[offset + 5] = 0f;
        }

        return values;
    }

    private static void ResetSnapSettings()
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

    private static void AssertClose(Vector3d expected, Vector3d actual, int precision = 9)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }
}
