using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.SceneGraph;
using System.Threading;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// The collection of GPU meshes for the currently-loaded document plus its
/// world-space bounding box. The renderer uses this to draw and the App layer
/// uses it to frame the camera at import time.
/// </summary>
public sealed class GpuScene : IDisposable
{
    private readonly GL _gl;
    private IReadOnlyList<GpuMesh> _meshes = Array.Empty<GpuMesh>();
    private DocumentDto? _document;
    private Scene? _lastTransformSyncScene;
    private long _lastTransientTransformVersion = -1;
    private long _lastMoveTransformVersion = -1;
    private long _sectionCapGeometryVersion;

    public GpuScene(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
    }

    public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

    public double MillimetersPerSceneUnit { get; private set; } = 1000.0;

    public IReadOnlyList<GpuMesh> Meshes => _meshes;

    internal DocumentDto? Document => _document;

    internal long SectionCapGeometryVersion => Volatile.Read(ref _sectionCapGeometryVersion);

    public bool TryGetSourceNodeIdForMeshIndex(int meshIndex, out int nodeId)
    {
        IReadOnlyList<GpuMesh> meshes = _meshes;
        foreach (GpuMesh mesh in meshes)
        {
            if (mesh.MeshIndex == meshIndex && mesh.SourceNodeId >= 0)
            {
                nodeId = mesh.SourceNodeId;
                return true;
            }
        }

        nodeId = -1;
        return false;
    }

    public bool TryGetMeshIndexForSourceNodeId(int nodeId, out int meshIndex)
    {
        IReadOnlyList<GpuMesh> meshes = _meshes;
        foreach (GpuMesh mesh in meshes)
        {
            if (mesh.SourceNodeId == nodeId)
            {
                meshIndex = mesh.MeshIndex;
                return true;
            }
        }

        meshIndex = 0;
        return false;
    }

    public void Load(DocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);

        double millimetersPerSceneUnit = ResolveMillimetersPerSceneUnit(document.Stats?.UnitSystem);
        BoundingBox bounds = document.Bounds.IsValid ? document.Bounds : ComputeBoundsFromMeshes(document);
        List<GpuMesh> meshes = SceneUploader.Upload(_gl, document);

        IReadOnlyList<GpuMesh> oldMeshes = _meshes;
        _document = document;
        MillimetersPerSceneUnit = millimetersPerSceneUnit;
        Bounds = bounds;
        _meshes = meshes;
        IncrementSectionCapGeometryVersion();
        InvalidateTransformSyncTracking();
        DisposeMeshes(oldMeshes);
    }

    public void RebuildEdges(
        float featureAngleDegrees,
        float coplanarToleranceDegrees,
        float weldToleranceScale,
        bool silhouetteEnabled)
    {
        if (_document is null)
            return;

        IReadOnlyList<GpuMesh> meshes = _meshes;
        foreach (var mesh in meshes)
        {
            int sourceMeshId = mesh.SourceMeshId;
            if (sourceMeshId < 0 || sourceMeshId >= _document.Meshes.Count)
                continue;

            mesh.RebuildEdges(
                _document.Meshes[sourceMeshId],
                featureAngleDegrees,
                coplanarToleranceDegrees,
                weldToleranceScale,
                silhouetteEnabled);
        }
    }

    public void SyncNodeTransforms(Scene scene)
    {
        if (_document is null)
            return;

        ArgumentNullException.ThrowIfNull(scene);

        long transientTransformVersion = scene.TransientTransformVersion;
        long moveTransformVersion = scene.MoveTransformVersion;
        if (ReferenceEquals(_lastTransformSyncScene, scene)
            && _lastTransientTransformVersion == transientTransformVersion
            && _lastMoveTransformVersion == moveTransformVersion)
        {
            return;
        }

        BoundingBox bounds = BoundingBox.Empty;
        IReadOnlyList<GpuMesh> meshes = _meshes;
        foreach (GpuMesh mesh in meshes)
        {
            if (mesh.SourceNodeId < 0
                || mesh.SourceMeshId < 0
                || mesh.SourceMeshId >= _document.Meshes.Count
                || scene.GetNode(mesh.SourceNodeId) is not SceneNode node)
            {
                continue;
            }

            MeshDto sourceMesh = _document.Meshes[mesh.SourceMeshId];
            Matrix4d worldMatrix = node.EffectiveWorldTransform;
            bool isIdentity = IsIdentity(worldMatrix);
            float[]? world = isIdentity ? null : ToRowMajorFloatArray(worldMatrix);
            mesh.WorldTransform = world;
            mesh.WorldNormalMatrix = mesh.WorldTransform is null
                ? null
                : GlesRenderUtil.NormalMatrixFromWorld(mesh.WorldTransform);
            mesh.HasMirroredHandedness = GlesRenderUtil.HasMirroredHandedness(mesh.WorldTransform);
            mesh.WorldCenter = ComputeWorldCenter(sourceMesh.Bounds, worldMatrix, isIdentity);
            mesh.WorldBounds = ComputeWorldBounds(sourceMesh.Bounds, worldMatrix, isIdentity);
            bounds.Merge(mesh.WorldBounds);
        }

        if (bounds.IsValid)
            Bounds = bounds;

        IncrementSectionCapGeometryVersion();

        long finalTransientTransformVersion = scene.TransientTransformVersion;
        long finalMoveTransformVersion = scene.MoveTransformVersion;
        if (finalTransientTransformVersion == transientTransformVersion
            && finalMoveTransformVersion == moveTransformVersion)
        {
            _lastTransformSyncScene = scene;
            _lastTransientTransformVersion = transientTransformVersion;
            _lastMoveTransformVersion = moveTransformVersion;
        }
        else
        {
            InvalidateTransformSyncTracking();
        }
    }

    public void SyncNodeVisibility(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        HashSet<int> visibleNodeIds = scene.GetVisibleNodes()
            .Select(node => node.Id)
            .ToHashSet();

        IReadOnlyList<GpuMesh> meshes = _meshes;
        foreach (GpuMesh mesh in meshes)
        {
            bool visible = mesh.SourceNodeId < 0 || visibleNodeIds.Contains(mesh.SourceNodeId);
            if (mesh.Visible != visible)
            {
                mesh.Visible = visible;
                IncrementSectionCapGeometryVersion();
            }
        }
    }

    private static BoundingBox ComputeBoundsFromMeshes(DocumentDto document)
    {
        bool any = false;
        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var mesh in document.Meshes)
        {
            var p = mesh.Positions;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                if (p[i] < min.X) min = new Vector3d(p[i], min.Y, min.Z);
                if (p[i + 1] < min.Y) min = new Vector3d(min.X, p[i + 1], min.Z);
                if (p[i + 2] < min.Z) min = new Vector3d(min.X, min.Y, p[i + 2]);
                if (p[i] > max.X) max = new Vector3d(p[i], max.Y, max.Z);
                if (p[i + 1] > max.Y) max = new Vector3d(max.X, p[i + 1], max.Z);
                if (p[i + 2] > max.Z) max = new Vector3d(max.X, max.Y, p[i + 2]);
                any = true;
            }
        }
        return any ? new BoundingBox(min, max) : BoundingBox.Empty;
    }

    private static Vector3d ComputeWorldCenter(BoundingBox localBounds, Matrix4d worldTransform, bool isIdentity)
    {
        if (!localBounds.IsValid) return Vector3d.Zero;
        return isIdentity ? localBounds.Center : worldTransform.TransformPoint(localBounds.Center);
    }

    private static BoundingBox ComputeWorldBounds(BoundingBox localBounds, Matrix4d worldTransform, bool isIdentity)
    {
        if (!localBounds.IsValid) return BoundingBox.Empty;
        if (isIdentity) return localBounds;

        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(
                (i & 1) == 0 ? localBounds.Min.X : localBounds.Max.X,
                (i & 2) == 0 ? localBounds.Min.Y : localBounds.Max.Y,
                (i & 4) == 0 ? localBounds.Min.Z : localBounds.Max.Z);
            Vector3d w = worldTransform.TransformPoint(corner);
            if (w.X < min.X) min = new Vector3d(w.X, min.Y, min.Z);
            if (w.Y < min.Y) min = new Vector3d(min.X, w.Y, min.Z);
            if (w.Z < min.Z) min = new Vector3d(min.X, min.Y, w.Z);
            if (w.X > max.X) max = new Vector3d(w.X, max.Y, max.Z);
            if (w.Y > max.Y) max = new Vector3d(max.X, w.Y, max.Z);
            if (w.Z > max.Z) max = new Vector3d(max.X, max.Y, w.Z);
        }

        return new BoundingBox(min, max);
    }

    private static float[] ToRowMajorFloatArray(Matrix4d matrix)
        =>
        [
            (float)matrix.M11, (float)matrix.M12, (float)matrix.M13, (float)matrix.M14,
            (float)matrix.M21, (float)matrix.M22, (float)matrix.M23, (float)matrix.M24,
            (float)matrix.M31, (float)matrix.M32, (float)matrix.M33, (float)matrix.M34,
            (float)matrix.M41, (float)matrix.M42, (float)matrix.M43, (float)matrix.M44,
        ];

    private static bool IsIdentity(float[] matrix)
    {
        ReadOnlySpan<float> identity =
        [
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f,
        ];

        for (int i = 0; i < 16; i++)
        {
            if (System.Math.Abs(matrix[i] - identity[i]) > 0.000001f)
                return false;
        }

        return true;
    }

    private static bool IsIdentity(Matrix4d matrix)
        => System.Math.Abs(matrix.M11 - 1.0) <= 0.000001
           && System.Math.Abs(matrix.M22 - 1.0) <= 0.000001
           && System.Math.Abs(matrix.M33 - 1.0) <= 0.000001
           && System.Math.Abs(matrix.M44 - 1.0) <= 0.000001
           && System.Math.Abs(matrix.M12) <= 0.000001
           && System.Math.Abs(matrix.M13) <= 0.000001
           && System.Math.Abs(matrix.M14) <= 0.000001
           && System.Math.Abs(matrix.M21) <= 0.000001
           && System.Math.Abs(matrix.M23) <= 0.000001
           && System.Math.Abs(matrix.M24) <= 0.000001
           && System.Math.Abs(matrix.M31) <= 0.000001
           && System.Math.Abs(matrix.M32) <= 0.000001
           && System.Math.Abs(matrix.M34) <= 0.000001
           && System.Math.Abs(matrix.M41) <= 0.000001
           && System.Math.Abs(matrix.M42) <= 0.000001
           && System.Math.Abs(matrix.M43) <= 0.000001;

    public void Draw()
    {
        IReadOnlyList<GpuMesh> meshes = _meshes;
        foreach (var m in meshes) m.Draw();
    }

    public void Clear()
    {
        IReadOnlyList<GpuMesh> oldMeshes = _meshes;
        _meshes = Array.Empty<GpuMesh>();
        Bounds = BoundingBox.Empty;
        MillimetersPerSceneUnit = 1000.0;
        _document = null;
        InvalidateTransformSyncTracking();
        DisposeMeshes(oldMeshes);
    }

    public void Dispose() => Clear();

    private static double ResolveMillimetersPerSceneUnit(string? unitLabel)
    {
        double metersPerUnit = SceneUnitResolver.MetersPerUnit(unitLabel);
        if (!double.IsFinite(metersPerUnit) || metersPerUnit <= 0.0)
            return 1000.0;

        return metersPerUnit * 1000.0;
    }

    private static void DisposeMeshes(IReadOnlyList<GpuMesh> meshes)
    {
        foreach (var mesh in meshes)
            mesh.Dispose();
    }

    private void InvalidateTransformSyncTracking()
    {
        _lastTransformSyncScene = null;
        _lastTransientTransformVersion = -1;
        _lastMoveTransformVersion = -1;
    }

    private void IncrementSectionCapGeometryVersion()
        => Interlocked.Increment(ref _sectionCapGeometryVersion);
}
