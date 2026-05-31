using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.Rendering.Gles;

public static class CadEdgeBuilder
{
    // S7-F6: edge endpoints carry only position. The silhouette test moved to a
    // screen-space pass, so the former per-vertex normalA/normalB/flags payload
    // (7 floats) was dead - UploadEdges only ever read xyz. The topology
    // classification below still gates which edges are emitted.
    public const int EdgeVertexFloatCount = 3;

    private const double FeatureEdgeToleranceScale = 1e-5;
    private const double FeatureEdgeMinimumTolerance = 1e-6;
    private const double FeatureEdgeMinimumLengthScale = 0.25;
    private const double FeatureEdgeCreaseAngleDegrees = 28.0;
    private const double DegenerateFaceNormalEpsilon = 1e-20;

    public static float[] BuildImportedEdgeVertices(float[] edgePositions)
    {
        if (edgePositions.Length < 6)
            return Array.Empty<float>();

        var vertices = new float[(edgePositions.Length / 3) * EdgeVertexFloatCount];
        int output = 0;
        for (int i = 0; i + 2 < edgePositions.Length; i += 3)
        {
            vertices[output++] = edgePositions[i];
            vertices[output++] = edgePositions[i + 1];
            vertices[output++] = edgePositions[i + 2];
        }

        return vertices;
    }

    public static float[] BuildFeatureEdgeVertices(
        MeshDto mesh,
        float featureAngleDegrees,
        float coplanarToleranceDegrees,
        float weldToleranceScale,
        bool includeAllTriangleEdges = false)
    {
        if (mesh.Positions.Length < 9 || mesh.Indices.Length < 3)
            return Array.Empty<float>();

        double diagonal = mesh.Bounds.IsValid ? mesh.Bounds.Diagonal : 0.0;
        double toleranceScale = weldToleranceScale > 0.0
            ? weldToleranceScale
            : FeatureEdgeToleranceScale;
        double tolerance = System.Math.Max(diagonal * toleranceScale, FeatureEdgeMinimumTolerance);
        double minEdgeLengthSquared = tolerance * tolerance * FeatureEdgeMinimumLengthScale;
        double scale = 1.0 / tolerance;
        double featureAngle = featureAngleDegrees > 0.0f
            ? featureAngleDegrees
            : FeatureEdgeCreaseAngleDegrees;
        double creaseDotThreshold = System.Math.Cos(featureAngle * System.Math.PI / 180.0);
        _ = coplanarToleranceDegrees; // silhouette candidates moved to a screen-space pass; coplanar tolerance kept on the appearance for the post-process.

        var topologyEdges = new Dictionary<TopologyEdgeKey, TopologyEdgeInfo>(
            System.Math.Max(64, mesh.Indices.Length / 6));

        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int v0 = mesh.Indices[i];
            int v1 = mesh.Indices[i + 1];
            int v2 = mesh.Indices[i + 2];

            int i0 = v0 * 3;
            int i1 = v1 * 3;
            int i2 = v2 * 3;

            if (i0 + 2 >= mesh.Positions.Length || i1 + 2 >= mesh.Positions.Length || i2 + 2 >= mesh.Positions.Length)
                continue;

            Vector3d p0 = new(mesh.Positions[i0], mesh.Positions[i0 + 1], mesh.Positions[i0 + 2]);
            Vector3d p1 = new(mesh.Positions[i1], mesh.Positions[i1 + 1], mesh.Positions[i1 + 2]);
            Vector3d p2 = new(mesh.Positions[i2], mesh.Positions[i2 + 1], mesh.Positions[i2 + 2]);

            Vector3d faceNormal = Vector3d.Cross(p1 - p0, p2 - p0);
            if (faceNormal.LengthSquared < DegenerateFaceNormalEpsilon)
                continue;

            faceNormal = faceNormal.Normalized();
            Vector3d n0 = GetVertexNormal(mesh.Normals, v0, faceNormal);
            Vector3d n1 = GetVertexNormal(mesh.Normals, v1, faceNormal);
            Vector3d n2 = GetVertexNormal(mesh.Normals, v2, faceNormal);

            AccumulateTopologyEdge(topologyEdges, p0, p1, faceNormal, n0, n1, minEdgeLengthSquared, scale);
            AccumulateTopologyEdge(topologyEdges, p1, p2, faceNormal, n1, n2, minEdgeLengthSquared, scale);
            AccumulateTopologyEdge(topologyEdges, p2, p0, faceNormal, n2, n0, minEdgeLengthSquared, scale);
        }

        var emittedEdges = new HashSet<PositionEdgeKey>();
        var edgeVertices = new List<float>(topologyEdges.Count * EdgeVertexFloatCount * 2);
        foreach (TopologyEdgeInfo edge in topologyEdges.Values)
        {
            bool isBoundaryEdge = edge.FaceCount == 1;
            bool isSmoothTessellationEdge = edge.FaceCount >= 2
                && edge.EndpointNormalDot >= creaseDotThreshold;
            bool isNonManifold = edge.FaceCount > 2
                && !isSmoothTessellationEdge;
            double normalDot = edge.FaceCount >= 2
                ? edge.FaceNormalDot
                : 1.0;
            bool isSharpCrease = edge.FaceCount == 2
                && normalDot < creaseDotThreshold
                && !isSmoothTessellationEdge;

            if (!includeAllTriangleEdges
                && !isBoundaryEdge
                && !isNonManifold
                && !isSharpCrease)
            {
                continue;
            }

            PositionEdgeKey dedupeKey = PositionEdgeKey.From(edge.Start, edge.End, scale);
            if (!emittedEdges.Add(dedupeKey))
                continue;

            AddEdgeVertex(edgeVertices, edge.Start);
            AddEdgeVertex(edgeVertices, edge.End);
        }

        return edgeVertices.Count == 0
            ? Array.Empty<float>()
            : edgeVertices.ToArray();
    }

    private static Vector3d GetVertexNormal(float[] normals, int vertexIndex, Vector3d fallback)
    {
        int offset = vertexIndex * 3;
        if (offset + 2 >= normals.Length)
            return fallback;

        Vector3d normal = new(normals[offset], normals[offset + 1], normals[offset + 2]);
        return normal.LengthSquared > DegenerateFaceNormalEpsilon
            ? normal.Normalized()
            : fallback;
    }

    private static void AddEdgeVertex(List<float> edgeVertices, Vector3d position)
    {
        edgeVertices.Add((float)position.X);
        edgeVertices.Add((float)position.Y);
        edgeVertices.Add((float)position.Z);
    }

    private static void AccumulateTopologyEdge(
        Dictionary<TopologyEdgeKey, TopologyEdgeInfo> topologyEdges,
        Vector3d start,
        Vector3d end,
        Vector3d faceNormal,
        Vector3d startNormal,
        Vector3d endNormal,
        double minEdgeLengthSquared,
        double scale)
    {
        if ((end - start).LengthSquared <= minEdgeLengthSquared)
            return;

        QuantizedPoint q0 = QuantizedPoint.From(start, scale);
        QuantizedPoint q1 = QuantizedPoint.From(end, scale);
        TopologyEdgeKey key;
        Vector3d canonicalStart;
        Vector3d canonicalEnd;

        if (q0.CompareTo(q1) <= 0)
        {
            key = new TopologyEdgeKey(q0, q1);
            canonicalStart = start;
            canonicalEnd = end;
        }
        else
        {
            key = new TopologyEdgeKey(q1, q0);
            canonicalStart = end;
            canonicalEnd = start;
            (startNormal, endNormal) = (endNormal, startNormal);
        }

        if (topologyEdges.TryGetValue(key, out TopologyEdgeInfo existing))
        {
            existing.AddFace(faceNormal, startNormal, endNormal);
            topologyEdges[key] = existing;
            return;
        }

        topologyEdges.Add(key, new TopologyEdgeInfo(
            canonicalStart,
            canonicalEnd,
            faceNormal,
            startNormal,
            endNormal));
    }

    private readonly record struct TopologyEdgeKey(QuantizedPoint Start, QuantizedPoint End);

    private readonly record struct PositionEdgeKey(QuantizedPoint Start, QuantizedPoint End)
    {
        public static PositionEdgeKey From(Vector3d start, Vector3d end, double scale)
        {
            QuantizedPoint q0 = QuantizedPoint.From(start, scale);
            QuantizedPoint q1 = QuantizedPoint.From(end, scale);

            return q0.CompareTo(q1) <= 0
                ? new PositionEdgeKey(q0, q1)
                : new PositionEdgeKey(q1, q0);
        }
    }

    private readonly record struct QuantizedPoint(long X, long Y, long Z) : IComparable<QuantizedPoint>
    {
        public static QuantizedPoint From(Vector3d point, double scale)
        {
            return new QuantizedPoint(
                (long)System.Math.Round(point.X * scale),
                (long)System.Math.Round(point.Y * scale),
                (long)System.Math.Round(point.Z * scale));
        }

        public int CompareTo(QuantizedPoint other)
        {
            int cmp = X.CompareTo(other.X);
            if (cmp != 0) return cmp;

            cmp = Y.CompareTo(other.Y);
            if (cmp != 0) return cmp;

            return Z.CompareTo(other.Z);
        }
    }

    private struct TopologyEdgeInfo
    {
        public TopologyEdgeInfo(
            Vector3d start,
            Vector3d end,
            Vector3d normal,
            Vector3d startNormal,
            Vector3d endNormal)
        {
            Start = start;
            End = end;
            NormalA = normal;
            NormalB = normal;
            StartNormalA = startNormal;
            EndNormalA = endNormal;
            StartNormalB = startNormal;
            EndNormalB = endNormal;
            FaceCount = 1;
            FaceNormalDot = 1.0;
            EndpointNormalDot = 1.0;
        }

        public Vector3d Start { get; }
        public Vector3d End { get; }
        public Vector3d NormalA { get; private set; }
        public Vector3d NormalB { get; private set; }
        public Vector3d StartNormalA { get; }
        public Vector3d EndNormalA { get; }
        public Vector3d StartNormalB { get; private set; }
        public Vector3d EndNormalB { get; private set; }
        public int FaceCount { get; private set; }
        public double FaceNormalDot { get; private set; }
        public double EndpointNormalDot { get; private set; }

        public void AddFace(Vector3d normal, Vector3d startNormal, Vector3d endNormal)
        {
            double faceDot = ClampedDot(NormalA, normal);
            double startDot = ClampedDot(StartNormalA, startNormal);
            double endDot = ClampedDot(EndNormalA, endNormal);

            if (FaceCount == 1)
            {
                NormalB = normal;
                StartNormalB = startNormal;
                EndNormalB = endNormal;
            }
            else if (faceDot < ClampedDot(NormalA, NormalB))
            {
                NormalB = normal;
                StartNormalB = startNormal;
                EndNormalB = endNormal;
            }

            FaceNormalDot = System.Math.Min(FaceNormalDot, faceDot);
            EndpointNormalDot = System.Math.Min(EndpointNormalDot, System.Math.Min(startDot, endDot));
            FaceCount++;
        }
    }

    private static double ClampedDot(Vector3d left, Vector3d right)
        => System.Math.Clamp(Vector3d.Dot(left, right), -1.0, 1.0);
}
