using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.App.Android.Measurement;

internal sealed class AndroidMeshRaycastAcceleration
{
    private const int MaxLeafTriangleCount = 12;
    private const double BoundsIntersectionEpsilon = 1e-12;
    private const double RayTriangleIntersectionEpsilon = 1e-10;

    private readonly float[] _positions;
    private readonly int[] _indices;
    private readonly int[] _triangleIndexOffsets;
    private readonly BvhNode[] _nodes;

    private AndroidMeshRaycastAcceleration(
        float[] positions,
        int[] indices,
        int[] triangleIndexOffsets,
        BvhNode[] nodes)
    {
        _positions = positions;
        _indices = indices;
        _triangleIndexOffsets = triangleIndexOffsets;
        _nodes = nodes;
    }

    public static AndroidMeshRaycastAcceleration Build(MeshDto mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.Positions.Length < 9 || mesh.Indices.Length < 3)
            return new AndroidMeshRaycastAcceleration(mesh.Positions, mesh.Indices, Array.Empty<int>(), Array.Empty<BvhNode>());

        TriangleBuildInfo[] triangles = CreateTriangleBuildInfos(mesh.Positions, mesh.Indices);
        if (triangles.Length == 0)
            return new AndroidMeshRaycastAcceleration(mesh.Positions, mesh.Indices, Array.Empty<int>(), Array.Empty<BvhNode>());

        var nodes = new List<BvhNode>(Math.Max(1, triangles.Length * 2));
        BuildNode(nodes, triangles, 0, triangles.Length);

        var triangleIndexOffsets = new int[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
            triangleIndexOffsets[i] = triangles[i].IndexOffset;

        return new AndroidMeshRaycastAcceleration(mesh.Positions, mesh.Indices, triangleIndexOffsets, nodes.ToArray());
    }

    public bool TryRaycast(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        out Vector3d hitPoint,
        out double hitDistance,
        out int triangleIndexOffset,
        Func<Vector3d, bool>? hitPointFilter = null)
    {
        hitPoint = default;
        hitDistance = double.PositiveInfinity;
        triangleIndexOffset = -1;

        if (_nodes.Length == 0)
            return false;

        if (!RayIntersectsBounds(rayOrigin, rayDirection, _nodes[0].Bounds, hitDistance, out _))
            return false;

        Span<int> stack = stackalloc int[128];
        int stackCount = 0;
        stack[stackCount++] = 0;

        double closestDistance = double.PositiveInfinity;
        Vector3d closestPoint = default;
        int closestTriangleIndexOffset = -1;

        while (stackCount > 0)
        {
            int nodeIndex = stack[--stackCount];
            BvhNode node = _nodes[nodeIndex];
            if (!RayIntersectsBounds(rayOrigin, rayDirection, node.Bounds, closestDistance, out _))
                continue;

            if (node.IsLeaf)
            {
                int triangleEnd = node.Start + node.Count;
                for (int triangleIndex = node.Start; triangleIndex < triangleEnd; triangleIndex++)
                {
                    int indexOffset = _triangleIndexOffsets[triangleIndex];
                    if (!TryReadTriangle(indexOffset, out Vector3d v0, out Vector3d v1, out Vector3d v2))
                        continue;

                    double t = MollerTrumboreIntersect(rayOrigin, rayDirection, v0, v1, v2);
                    if (t <= 0.0 || t >= closestDistance)
                        continue;

                    Vector3d candidatePoint = rayOrigin + (rayDirection * t);
                    if (hitPointFilter is not null && !hitPointFilter(candidatePoint))
                        continue;

                    closestDistance = t;
                    closestPoint = candidatePoint;
                    closestTriangleIndexOffset = indexOffset;
                }

                continue;
            }

            BvhNode left = _nodes[node.LeftChildIndex];
            BvhNode right = _nodes[node.RightChildIndex];
            bool hitLeft = RayIntersectsBounds(rayOrigin, rayDirection, left.Bounds, closestDistance, out double leftEntry);
            bool hitRight = RayIntersectsBounds(rayOrigin, rayDirection, right.Bounds, closestDistance, out double rightEntry);

            if (hitLeft && hitRight)
            {
                if (leftEntry <= rightEntry)
                {
                    stack[stackCount++] = node.RightChildIndex;
                    stack[stackCount++] = node.LeftChildIndex;
                }
                else
                {
                    stack[stackCount++] = node.LeftChildIndex;
                    stack[stackCount++] = node.RightChildIndex;
                }
            }
            else if (hitLeft)
            {
                stack[stackCount++] = node.LeftChildIndex;
            }
            else if (hitRight)
            {
                stack[stackCount++] = node.RightChildIndex;
            }
        }

        if (!double.IsFinite(closestDistance))
            return false;

        hitPoint = closestPoint;
        hitDistance = closestDistance;
        triangleIndexOffset = closestTriangleIndexOffset;
        return true;
    }

    public static bool RayIntersectsBounds(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        BoundingBox bounds,
        double maxDistance,
        out double entryDistance)
    {
        entryDistance = 0.0;
        if (!bounds.IsValid)
            return false;

        double tMin = 0.0;
        double tMax = double.IsFinite(maxDistance) ? maxDistance : double.PositiveInfinity;
        double epsilon = BoundsIntersectionEpsilonFor(bounds);

        if (!ClipAxis(rayOrigin.X, rayDirection.X, bounds.Min.X, bounds.Max.X, epsilon, ref tMin, ref tMax)
            || !ClipAxis(rayOrigin.Y, rayDirection.Y, bounds.Min.Y, bounds.Max.Y, epsilon, ref tMin, ref tMax)
            || !ClipAxis(rayOrigin.Z, rayDirection.Z, bounds.Min.Z, bounds.Max.Z, epsilon, ref tMin, ref tMax))
        {
            return false;
        }

        if (tMax < epsilon)
            return false;

        entryDistance = tMin > 0.0 ? tMin : 0.0;
        return entryDistance <= tMax && entryDistance <= maxDistance;
    }

    private static TriangleBuildInfo[] CreateTriangleBuildInfos(float[] positions, int[] indices)
    {
        int triangleCount = indices.Length / 3;
        var triangles = new TriangleBuildInfo[triangleCount];
        int validCount = 0;

        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            int indexOffset = triangleIndex * 3;
            int i0 = indices[indexOffset] * 3;
            int i1 = indices[indexOffset + 1] * 3;
            int i2 = indices[indexOffset + 2] * 3;
            if (i0 + 2 >= positions.Length || i1 + 2 >= positions.Length || i2 + 2 >= positions.Length)
                continue;

            Vector3d v0 = new(positions[i0], positions[i0 + 1], positions[i0 + 2]);
            Vector3d v1 = new(positions[i1], positions[i1 + 1], positions[i1 + 2]);
            Vector3d v2 = new(positions[i2], positions[i2 + 1], positions[i2 + 2]);

            BoundingBox bounds = BoundingBox.Empty;
            bounds.Expand(v0);
            bounds.Expand(v1);
            bounds.Expand(v2);

            triangles[validCount++] = new TriangleBuildInfo(
                indexOffset,
                bounds,
                new Vector3d(
                    (v0.X + v1.X + v2.X) / 3.0,
                    (v0.Y + v1.Y + v2.Y) / 3.0,
                    (v0.Z + v1.Z + v2.Z) / 3.0));
        }

        if (validCount == triangles.Length)
            return triangles;

        Array.Resize(ref triangles, validCount);
        return triangles;
    }

    private static int BuildNode(List<BvhNode> nodes, TriangleBuildInfo[] triangles, int start, int count)
    {
        BoundingBox nodeBounds = ComputeBounds(triangles, start, count);
        int nodeIndex = nodes.Count;
        nodes.Add(default);

        if (count <= MaxLeafTriangleCount)
        {
            nodes[nodeIndex] = new BvhNode(nodeBounds, start, count, -1, -1, true);
            return nodeIndex;
        }

        BoundingBox centroidBounds = ComputeCentroidBounds(triangles, start, count);
        int axis = SelectSplitAxis(centroidBounds);
        double axisExtent = GetAxisExtent(centroidBounds, axis);
        if (axisExtent <= BoundsIntersectionEpsilon)
        {
            nodes[nodeIndex] = new BvhNode(nodeBounds, start, count, -1, -1, true);
            return nodeIndex;
        }

        Array.Sort(triangles, start, count, TriangleAxisComparer.ForAxis(axis));
        int leftCount = count / 2;
        int rightStart = start + leftCount;
        int rightCount = count - leftCount;

        int leftChild = BuildNode(nodes, triangles, start, leftCount);
        int rightChild = BuildNode(nodes, triangles, rightStart, rightCount);
        nodes[nodeIndex] = new BvhNode(nodeBounds, start, count, leftChild, rightChild, false);
        return nodeIndex;
    }

    private bool TryReadTriangle(int indexOffset, out Vector3d v0, out Vector3d v1, out Vector3d v2)
    {
        int i0 = _indices[indexOffset] * 3;
        int i1 = _indices[indexOffset + 1] * 3;
        int i2 = _indices[indexOffset + 2] * 3;

        if (i0 + 2 >= _positions.Length || i1 + 2 >= _positions.Length || i2 + 2 >= _positions.Length)
        {
            v0 = default;
            v1 = default;
            v2 = default;
            return false;
        }

        v0 = new Vector3d(_positions[i0], _positions[i0 + 1], _positions[i0 + 2]);
        v1 = new Vector3d(_positions[i1], _positions[i1 + 1], _positions[i1 + 2]);
        v2 = new Vector3d(_positions[i2], _positions[i2 + 1], _positions[i2 + 2]);
        return true;
    }

    private static BoundingBox ComputeBounds(TriangleBuildInfo[] triangles, int start, int count)
    {
        BoundingBox bounds = BoundingBox.Empty;
        int end = start + count;
        for (int i = start; i < end; i++)
            bounds.Merge(triangles[i].Bounds);

        return bounds;
    }

    private static BoundingBox ComputeCentroidBounds(TriangleBuildInfo[] triangles, int start, int count)
    {
        BoundingBox bounds = BoundingBox.Empty;
        int end = start + count;
        for (int i = start; i < end; i++)
            bounds.Expand(triangles[i].Centroid);

        return bounds;
    }

    private static int SelectSplitAxis(BoundingBox bounds)
    {
        Vector3d size = bounds.Size;
        if (size.Y >= size.X && size.Y >= size.Z)
            return 1;

        return size.Z >= size.X ? 2 : 0;
    }

    private static double GetAxisExtent(BoundingBox bounds, int axis)
    {
        return axis switch
        {
            1 => bounds.Max.Y - bounds.Min.Y,
            2 => bounds.Max.Z - bounds.Min.Z,
            _ => bounds.Max.X - bounds.Min.X
        };
    }

    private static bool ClipAxis(
        double origin,
        double direction,
        double min,
        double max,
        double epsilon,
        ref double tMin,
        ref double tMax)
    {
        if (Math.Abs(direction) <= epsilon)
            return origin >= min - epsilon && origin <= max + epsilon;

        double invDirection = 1.0 / direction;
        double axisEntry = (min - origin) * invDirection;
        double axisExit = (max - origin) * invDirection;
        if (axisEntry > axisExit)
            (axisEntry, axisExit) = (axisExit, axisEntry);

        tMin = Math.Max(tMin, axisEntry);
        tMax = Math.Min(tMax, axisExit);
        return tMin <= tMax;
    }

    private static double BoundsIntersectionEpsilonFor(BoundingBox bounds)
    {
        double extent = Math.Max(
            Math.Max(Math.Abs(bounds.Max.X - bounds.Min.X), Math.Abs(bounds.Max.Y - bounds.Min.Y)),
            Math.Abs(bounds.Max.Z - bounds.Min.Z));
        if (!double.IsFinite(extent) || extent <= 1.0)
            return BoundsIntersectionEpsilon;

        return Math.Max(BoundsIntersectionEpsilon, extent * 1e-12);
    }

    private static double MollerTrumboreIntersect(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        Vector3d v0,
        Vector3d v1,
        Vector3d v2)
    {
        Vector3d edge1 = v1 - v0;
        Vector3d edge2 = v2 - v0;
        double maxEdge = Math.Max(edge1.Length, edge2.Length);
        if (!double.IsFinite(maxEdge) || maxEdge <= 0.0)
            return -1.0;
        double determinantEpsilon = Math.Max(RayTriangleIntersectionEpsilon, maxEdge * maxEdge * 1e-14);
        double distanceEpsilon = Math.Max(RayTriangleIntersectionEpsilon, maxEdge * 1e-12);
        Vector3d h = Vector3d.Cross(rayDirection, edge2);
        double a = Vector3d.Dot(edge1, h);
        if (a > -determinantEpsilon && a < determinantEpsilon)
            return -1.0;

        double f = 1.0 / a;
        Vector3d s = rayOrigin - v0;
        double u = f * Vector3d.Dot(s, h);
        if (u < 0.0 || u > 1.0)
            return -1.0;

        Vector3d q = Vector3d.Cross(s, edge1);
        double v = f * Vector3d.Dot(rayDirection, q);
        if (v < 0.0 || u + v > 1.0)
            return -1.0;

        double t = f * Vector3d.Dot(edge2, q);
        return t > distanceEpsilon ? t : -1.0;
    }

    private readonly record struct TriangleBuildInfo(int IndexOffset, BoundingBox Bounds, Vector3d Centroid);

    private readonly record struct BvhNode(
        BoundingBox Bounds,
        int Start,
        int Count,
        int LeftChildIndex,
        int RightChildIndex,
        bool IsLeaf);

    private sealed class TriangleAxisComparer : IComparer<TriangleBuildInfo>
    {
        private static readonly TriangleAxisComparer[] Comparers =
        [
            new TriangleAxisComparer(0),
            new TriangleAxisComparer(1),
            new TriangleAxisComparer(2)
        ];

        private readonly int _axis;

        private TriangleAxisComparer(int axis) => _axis = axis;

        public static TriangleAxisComparer ForAxis(int axis)
            => Comparers[Math.Clamp(axis, 0, 2)];

        public int Compare(TriangleBuildInfo left, TriangleBuildInfo right)
            => GetAxisValue(left.Centroid, _axis).CompareTo(GetAxisValue(right.Centroid, _axis));

        private static double GetAxisValue(Vector3d value, int axis)
        {
            return axis switch
            {
                1 => value.Y,
                2 => value.Z,
                _ => value.X
            };
        }
    }
}
