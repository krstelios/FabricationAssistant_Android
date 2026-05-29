using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Android point snapping for measure tools. Straight collinear edge segments are
/// welded into runs, and each run exposes only two endpoints plus one midpoint.
/// </summary>
public sealed class EdgeSnapService
{
    public static bool SnapEnabled { get; set; } = true;

    public static double EdgeSnapToleranceFactor { get; set; } = 1.0;

    public static double EndpointSnapToleranceFactor { get; set; } = 1.0;

    public static Func<EdgeSnapVisibilityRequest, bool>? VisibilityFilter { get; set; }

    public EdgeSnapResult? TrySnap(
        float[] edgePositions,
        Matrix4d localToWorld,
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double angularTolerance,
        double endpointAngularTolerance = 0.0085)
    {
        if (edgePositions is null || edgePositions.Length < 6)
            return null;

        int pointCount = (edgePositions.Length / 6) * 2;
        var worldEdges = new Vector3d[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            int offset = i * 3;
            worldEdges[i] = localToWorld.TransformPoint(new Vector3d(
                edgePositions[offset],
                edgePositions[offset + 1],
                edgePositions[offset + 2]));
        }

        return TrySnap(worldEdges, rayOrigin, rayDirection, angularTolerance, endpointAngularTolerance);
    }

    public EdgeSnapResult? TrySnap(
        IReadOnlyList<Vector3d> worldEdgePositions,
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double angularTolerance,
        double endpointAngularTolerance = 0.0085)
    {
        if (worldEdgePositions is null || worldEdgePositions.Count < 2)
            return null;

        return TrySnapTargets(
            BuildStraightEdgeSnapTargets(worldEdgePositions),
            rayOrigin,
            rayDirection,
            angularTolerance,
            endpointAngularTolerance);
    }

    private static EdgeSnapResult? TrySnapTargets(
        IReadOnlyList<EdgeSnapTarget> targets,
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double angularTolerance,
        double endpointAngularTolerance)
    {
        if (!SnapEnabled)
            return null;

        if (targets.Count == 0)
            return null;

        Vector3d dir = rayDirection.Normalized();
        if (dir.LengthSquared < 1.0e-18)
            return null;

        if (TryFindBestTarget(
                targets,
            EdgeSnapTargetKind.Endpoint,
            rayOrigin,
            dir,
            System.Math.Tan(ResolveAngularTolerance(endpointAngularTolerance, EndpointSnapToleranceFactor))) is { } endpoint)
        {
            return endpoint;
        }

        return TryFindBestTarget(
            targets,
            EdgeSnapTargetKind.Midpoint,
            rayOrigin,
            dir,
            System.Math.Tan(ResolveAngularTolerance(angularTolerance, EdgeSnapToleranceFactor)));
    }

    private static double ResolveAngularTolerance(double angularTolerance, double factor)
    {
        if (!double.IsFinite(angularTolerance) || angularTolerance <= 0.0)
            return 0.0;
        if (!double.IsFinite(factor) || factor <= 0.0)
            return 0.0;

        return angularTolerance * System.Math.Clamp(factor, 0.0, 16.0);
    }

    private static EdgeSnapTarget[] BuildStraightEdgeSnapTargets(IReadOnlyList<Vector3d> worldEdgePositions)
    {
        double weldTolerance = ResolveWeldTolerance(worldEdgePositions);
        double weldToleranceSquared = weldTolerance * weldTolerance;
        var groups = new List<StraightEdgeGroup>();

        int segmentCount = worldEdgePositions.Count / 2;
        for (int i = 0; i < segmentCount; i++)
        {
            int offset = i * 2;
            Vector3d p0 = worldEdgePositions[offset];
            Vector3d p1 = worldEdgePositions[offset + 1];
            if (!IsFinite(p0) || !IsFinite(p1))
                continue;

            Vector3d edge = p1 - p0;
            double length = edge.Length;
            if (length <= weldTolerance)
                continue;

            Vector3d direction = edge / length;
            StraightEdgeGroup? group = null;
            foreach (StraightEdgeGroup candidate in groups)
            {
                if (candidate.CanAccept(p0, p1, direction, weldToleranceSquared))
                {
                    group = candidate;
                    break;
                }
            }

            if (group is null)
            {
                group = new StraightEdgeGroup(p0, direction);
                groups.Add(group);
            }

            group.Add(p0, p1);
        }

        if (groups.Count == 0)
            return Array.Empty<EdgeSnapTarget>();

        var targets = new List<EdgeSnapTarget>(groups.Count * 3);
        foreach (StraightEdgeGroup group in groups)
            group.AppendTargets(targets, weldTolerance);

        return targets.Count == 0 ? Array.Empty<EdgeSnapTarget>() : targets.ToArray();
    }

    private static EdgeSnapResult? TryFindBestTarget(
        IReadOnlyList<EdgeSnapTarget> targets,
        EdgeSnapTargetKind kind,
        Vector3d rayOrigin,
        Vector3d rayDir,
        double tanTolerance)
    {
        double bestDepth = double.PositiveInfinity;
        Vector3d bestPoint = default;
        double bestPerp = double.PositiveInfinity;
        EdgeSnapTarget bestTarget = default;
        bool found = false;

        foreach (EdgeSnapTarget target in targets)
        {
            if (target.Kind != kind)
                continue;

            Vector3d to = target.WorldPoint - rayOrigin;
            double depth = Vector3d.Dot(to, rayDir);
            if (depth <= 0.0)
                continue;

            Vector3d perpVec = to - rayDir * depth;
            double perp = perpVec.Length;
            if (perp / depth > tanTolerance)
                continue;

            if (depth >= bestDepth)
                continue;

            bestDepth = depth;
            bestPoint = target.WorldPoint;
            bestPerp = perp;
            bestTarget = target;
            found = true;
        }

        if (!found)
            return null;

        if (!IsTargetVisible(rayOrigin, rayDir, bestTarget, bestDepth, bestPerp))
            return null;

        return new EdgeSnapResult(bestPoint, bestDepth, bestPerp);
    }

    private static bool IsTargetVisible(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        EdgeSnapTarget target,
        double rayDepth,
        double perpendicularDistance)
        => VisibilityFilter?.Invoke(new EdgeSnapVisibilityRequest(
            rayOrigin,
            rayDirection,
            target.WorldPoint,
            rayDepth,
            perpendicularDistance,
            target.RunStart,
            target.RunEnd)) ?? true;

    private static double ResolveWeldTolerance(IReadOnlyList<Vector3d> points)
    {
        double diagonal = EstimateBoundsDiagonal(points);
        return System.Math.Max(1.0e-9, diagonal * 1.0e-7);
    }

    private static double EstimateBoundsDiagonal(IReadOnlyList<Vector3d> points)
    {
        bool any = false;
        double minX = 0.0, minY = 0.0, minZ = 0.0;
        double maxX = 0.0, maxY = 0.0, maxZ = 0.0;

        foreach (Vector3d p in points)
        {
            if (!IsFinite(p))
                continue;

            if (!any)
            {
                minX = maxX = p.X;
                minY = maxY = p.Y;
                minZ = maxZ = p.Z;
                any = true;
                continue;
            }

            minX = System.Math.Min(minX, p.X);
            minY = System.Math.Min(minY, p.Y);
            minZ = System.Math.Min(minZ, p.Z);
            maxX = System.Math.Max(maxX, p.X);
            maxY = System.Math.Max(maxY, p.Y);
            maxZ = System.Math.Max(maxZ, p.Z);
        }

        if (!any)
            return 1.0;

        double dx = maxX - minX;
        double dy = maxY - minY;
        double dz = maxZ - minZ;
        double diagonal = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return double.IsFinite(diagonal) && diagonal > 0.0 ? diagonal : 1.0;
    }

    private static bool IsFinite(Vector3d p)
        => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private sealed class StraightEdgeGroup
    {
        private const double DirectionDotTolerance = 1.0e-6;
        private readonly List<EdgeInterval> _intervals = new();

        public StraightEdgeGroup(Vector3d origin, Vector3d direction)
        {
            Origin = origin;
            Direction = direction;
        }

        private Vector3d Origin { get; }

        private Vector3d Direction { get; }

        public bool CanAccept(Vector3d p0, Vector3d p1, Vector3d direction, double weldToleranceSquared)
        {
            double dot = System.Math.Abs(Vector3d.Dot(Direction, direction));
            if (1.0 - dot > DirectionDotTolerance)
                return false;

            return DistanceSquaredToLine(p0) <= weldToleranceSquared
                && DistanceSquaredToLine(p1) <= weldToleranceSquared;
        }

        public void Add(Vector3d p0, Vector3d p1)
        {
            double t0 = Vector3d.Dot(p0 - Origin, Direction);
            double t1 = Vector3d.Dot(p1 - Origin, Direction);
            _intervals.Add(new EdgeInterval(System.Math.Min(t0, t1), System.Math.Max(t0, t1)));
        }

        public void AppendTargets(List<EdgeSnapTarget> targets, double weldTolerance)
        {
            if (_intervals.Count == 0)
                return;

            _intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            double start = _intervals[0].Start;
            double end = _intervals[0].End;

            for (int i = 1; i < _intervals.Count; i++)
            {
                EdgeInterval interval = _intervals[i];
                if (interval.Start <= end + weldTolerance)
                {
                    end = System.Math.Max(end, interval.End);
                    continue;
                }

                AppendRunTargets(targets, start, end, weldTolerance);
                start = interval.Start;
                end = interval.End;
            }

            AppendRunTargets(targets, start, end, weldTolerance);
        }

        private double DistanceSquaredToLine(Vector3d p)
        {
            Vector3d to = p - Origin;
            double t = Vector3d.Dot(to, Direction);
            Vector3d perpendicular = to - Direction * t;
            return perpendicular.LengthSquared;
        }

        private void AppendRunTargets(List<EdgeSnapTarget> targets, double start, double end, double weldTolerance)
        {
            Vector3d a = Origin + Direction * start;
            Vector3d b = Origin + Direction * end;
            targets.Add(new EdgeSnapTarget(a, EdgeSnapTargetKind.Endpoint, a, b));
            targets.Add(new EdgeSnapTarget(b, EdgeSnapTargetKind.Endpoint, a, b));

            if (end - start > weldTolerance * 2.0)
            {
                Vector3d mid = Origin + Direction * ((start + end) * 0.5);
                targets.Add(new EdgeSnapTarget(mid, EdgeSnapTargetKind.Midpoint, a, b));
            }
        }
    }

    private readonly record struct EdgeInterval(double Start, double End);

    private readonly record struct EdgeSnapTarget(
        Vector3d WorldPoint,
        EdgeSnapTargetKind Kind,
        Vector3d RunStart,
        Vector3d RunEnd);

    private enum EdgeSnapTargetKind
    {
        Endpoint,
        Midpoint,
    }
}

public readonly record struct EdgeSnapVisibilityRequest(
    Vector3d RayOrigin,
    Vector3d RayDirection,
    Vector3d WorldPoint,
    double RayDepth,
    double PerpendicularDistance,
    Vector3d EdgeStart,
    Vector3d EdgeEnd);

public readonly record struct EdgeSnapResult(Vector3d WorldPoint, double RayDepth, double PerpendicularDistance);
