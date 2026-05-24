using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Walks a DocumentDto's scene-graph (document.Nodes) and creates one GpuMesh
/// per node that carries a MeshId. Each GpuMesh gets the node's precomputed
/// world transform (transposed from glTF's column-major to row-major), the
/// node's color override (falling back to MeshDto.DefaultColor), and a
/// world-space center computed from the mesh's local bounds.
///
/// Plan 2A's earlier "flat list of meshes" upload collapsed every body to the
/// origin because the model matrix was always identity. Plan 2G fixes that
/// by giving each rendered mesh its own world transform.
/// </summary>
public static class SceneUploader
{
    /// <summary>
    /// Default dihedral angle for the CAD edge extraction. Mirrors the
    /// desktop SceneAppearanceViewModel default.
    /// </summary>
    public const float DefaultFeatureAngleDeg = 28f;
    public const float DefaultCoplanarToleranceDeg = 5f;
    public const float DefaultWeldToleranceScale = 1.0e-5f;
    public const bool DefaultSilhouetteEnabled = true;

    public static List<GpuMesh> Upload(GL gl, DocumentDto document, float featureAngleDeg = DefaultFeatureAngleDeg)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(document);

        var meshes = new List<GpuMesh>(document.Nodes.Count);

        // Files that come through without scene nodes (rare; just raw meshes)
        // fall back to one identity-transformed GpuMesh per MeshDto so we
        // still render something instead of a black viewport.
        if (document.Nodes.Count == 0)
        {
            for (int i = 0; i < document.Meshes.Count; i++)
            {
                var meshDto = document.Meshes[i];
                var gpu = UploadOne(gl, meshDto, featureAngleDeg);
                gpu.MeshIndex = i + 1;
                gpu.SourceMeshId = i;
                gpu.DiffuseColor = TryExtractRgb(meshDto.DefaultColor) ?? new[] { 0.7f, 0.7f, 0.7f };
                gpu.DoubleSided = meshDto.IsDoubleSided;
                gpu.WorldTransform = null;
                gpu.HasMirroredHandedness = false;
                gpu.WorldCenter = meshDto.Bounds.IsValid ? meshDto.Bounds.Center : Vector3d.Zero;
                gpu.WorldBounds = meshDto.Bounds;
                meshes.Add(gpu);
            }
            return meshes;
        }

        int instance = 0;
        foreach (var node in document.Nodes)
        {
            if (node.MeshId is not int meshId) continue;
            if (meshId < 0 || meshId >= document.Meshes.Count) continue;

            var meshDto = document.Meshes[meshId];
            var gpu = UploadOne(gl, meshDto, featureAngleDeg);
            instance++;
            gpu.MeshIndex = instance;
            gpu.SourceMeshId = meshId;
            gpu.DiffuseColor = TryExtractRgb(node.Color)
                ?? TryExtractRgb(meshDto.DefaultColor)
                ?? new[] { 0.7f, 0.7f, 0.7f };
            gpu.DoubleSided = meshDto.IsDoubleSided;
            gpu.WorldTransform = TransposeColumnMajorToRowMajor(node.WorldTransform);
            gpu.HasMirroredHandedness = GlesRenderUtil.HasMirroredHandedness(gpu.WorldTransform);
            gpu.WorldCenter = ComputeWorldCenter(meshDto.Bounds, gpu.WorldTransform);
            gpu.WorldBounds = ComputeWorldBounds(meshDto.Bounds, gpu.WorldTransform);
            meshes.Add(gpu);
        }
        return meshes;
    }

    private static GpuMesh UploadOne(GL gl, MeshDto meshDto, float featureAngleDeg)
    {
        var gpu = new GpuMesh(gl);
        gpu.Upload(meshDto.Positions.AsSpan(), meshDto.Normals.AsSpan(), meshDto.Indices.AsSpan());

        // CAD edges - extracted at upload time via dihedral-angle test. The
        // user can disable them at runtime via Appearance.EdgesEnabled; the
        // GPU buffer is still allocated but unused.
        var edgeVertices = meshDto.EdgePositions.Length > 0
            ? CadEdgeBuilder.BuildImportedEdgeVertices(meshDto.EdgePositions)
            : CadEdgeBuilder.BuildFeatureEdgeVertices(
                meshDto,
                featureAngleDeg,
                DefaultCoplanarToleranceDeg,
                DefaultWeldToleranceScale,
                DefaultSilhouetteEnabled);
        if (edgeVertices.Length > 0)
            gpu.UploadEdges(edgeVertices);

        return gpu;
    }

    /// <summary>
    /// glTF/SceneNodeDto.WorldTransform is column-major float[16]; the renderer
    /// uses row-major float[16] (with glUniformMatrix4(transpose=true)).
    /// Convert by swapping the indexing convention.
    /// </summary>
    private static float[]? TransposeColumnMajorToRowMajor(float[]? columnMajor)
    {
        if (columnMajor is null || columnMajor.Length < 16) return null;
        var rowMajor = new float[16];
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                // column-major: m_col_row at index (col * 4 + row)
                // row-major:    m_row_col at index (row * 4 + col)
                rowMajor[row * 4 + col] = columnMajor[col * 4 + row];
            }
        }
        return rowMajor;
    }

    private static Vector3d ComputeWorldCenter(BoundingBox localBounds, float[]? worldTransformRowMajor)
    {
        if (!localBounds.IsValid) return Vector3d.Zero;
        return TransformPoint(localBounds.Center, worldTransformRowMajor);
    }

    private static BoundingBox ComputeWorldBounds(BoundingBox localBounds, float[]? worldTransformRowMajor)
    {
        if (!localBounds.IsValid) return BoundingBox.Empty;
        if (worldTransformRowMajor is null) return localBounds;

        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3d(
                (i & 1) == 0 ? localBounds.Min.X : localBounds.Max.X,
                (i & 2) == 0 ? localBounds.Min.Y : localBounds.Max.Y,
                (i & 4) == 0 ? localBounds.Min.Z : localBounds.Max.Z);
            Vector3d w = TransformPoint(corner, worldTransformRowMajor);
            if (w.X < min.X) min = new Vector3d(w.X, min.Y, min.Z);
            if (w.Y < min.Y) min = new Vector3d(min.X, w.Y, min.Z);
            if (w.Z < min.Z) min = new Vector3d(min.X, min.Y, w.Z);
            if (w.X > max.X) max = new Vector3d(w.X, max.Y, max.Z);
            if (w.Y > max.Y) max = new Vector3d(max.X, w.Y, max.Z);
            if (w.Z > max.Z) max = new Vector3d(max.X, max.Y, w.Z);
        }
        return new BoundingBox(min, max);
    }

    private static Vector3d TransformPoint(Vector3d local, float[]? worldTransformRowMajor)
    {
        if (worldTransformRowMajor is null || worldTransformRowMajor.Length < 16)
            return local;
        var M = worldTransformRowMajor;
        // Row-major M * column vector (x, y, z, 1).
        double x = M[0] * local.X + M[1] * local.Y + M[2] * local.Z + M[3];
        double y = M[4] * local.X + M[5] * local.Y + M[6] * local.Z + M[7];
        double z = M[8] * local.X + M[9] * local.Y + M[10] * local.Z + M[11];
        return new Vector3d(x, y, z);
    }

    private static float[]? TryExtractRgb(float[]? rgba)
    {
        if (rgba is null || rgba.Length < 3) return null;
        return new[] { rgba[0], rgba[1], rgba[2] };
    }
}
