using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.App.Android.Tests;

/// Minimal in-memory raycaster for picker tests. Returns a single configured
/// surface hit and a single mesh (identity transform). Optionally answers the
/// supplemental snap provider with a fixed point.
internal sealed class FakeMeasureRaycaster : IMeasureRaycaster, IMeasureSupplementalPointSnapProvider
{
    private readonly MeshDto? _mesh;
    private readonly MeasureRaycastHit? _hit;
    private readonly Vector3d? _supplementalPoint;

    public FakeMeasureRaycaster(
        MeshDto? mesh,
        MeasureRaycastHit? hit,
        Vector3d? supplementalPoint = null,
        double sceneDiagonal = 10.0)
    {
        _mesh = mesh;
        _hit = hit;
        _supplementalPoint = supplementalPoint;
        SceneDiagonal = sceneDiagonal;
    }

    public double SceneDiagonal { get; }

    public MeasureRaycastHit? Raycast(Vector3d origin, Vector3d direction) => _hit;

    public MeshDto? GetMesh(int meshId) => _mesh;

    // No SceneNode in unit tests -> point picks get no attachment, which is fine.
    public SceneNode? GetNode(int nodeId) => null;

    public Matrix4d GetWorldTransform(int nodeId) => Matrix4d.Identity;

    public bool TrySnapPoint(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double edgeAngularTolerance,
        double endpointAngularTolerance,
        out Vector3d worldPoint,
        bool allowBuild = true)
    {
        if (_supplementalPoint is { } p)
        {
            worldPoint = p;
            return true;
        }

        worldPoint = default;
        return false;
    }
}
