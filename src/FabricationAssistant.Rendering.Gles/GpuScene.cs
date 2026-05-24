using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;
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
    private readonly List<GpuMesh> _meshes = new();
    private DocumentDto? _document;

    public GpuScene(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
    }

    public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

    public IReadOnlyList<GpuMesh> Meshes => _meshes;

    public void Load(DocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Clear();
        _document = document;
        _meshes.AddRange(SceneUploader.Upload(_gl, document));
        Bounds = document.Bounds.IsValid ? document.Bounds : ComputeBoundsFromMeshes(document);
    }

    public void RebuildEdges(
        float featureAngleDegrees,
        float coplanarToleranceDegrees,
        float weldToleranceScale,
        bool silhouetteEnabled)
    {
        if (_document is null)
            return;

        foreach (var mesh in _meshes)
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

    public void Draw()
    {
        foreach (var m in _meshes) m.Draw();
    }

    public void Clear()
    {
        foreach (var m in _meshes) m.Dispose();
        _meshes.Clear();
        Bounds = BoundingBox.Empty;
        _document = null;
    }

    public void Dispose() => Clear();
}
