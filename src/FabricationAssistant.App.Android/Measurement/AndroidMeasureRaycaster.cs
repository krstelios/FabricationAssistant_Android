using Android.Util;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Sections;

namespace FabricationAssistant.App.Android.Measurement;

internal sealed class AndroidMeasureRaycaster : IMeasureRaycaster
{
    private readonly Func<Scene?> _sceneAccessor;
    private readonly Func<IReadOnlyList<SectionPlane>>? _sectionPlanesAccessor;
    private readonly Dictionary<int, AccelerationEntry> _accelerations = new();
    private Scene? _cachedScene;
    private string _lastRaycastLogKey = "";
    private long _lastRaycastLogMs;
    private int? _preferredNodeId;

    public AndroidMeasureRaycaster(
        Func<Scene?> sceneAccessor,
        Func<IReadOnlyList<SectionPlane>>? sectionPlanesAccessor = null)
    {
        _sceneAccessor = sceneAccessor ?? throw new ArgumentNullException(nameof(sceneAccessor));
        _sectionPlanesAccessor = sectionPlanesAccessor;
    }

    public double SceneDiagonal
    {
        get
        {
            var scene = _sceneAccessor();
            if (scene is null) return 1.0;
            var bounds = scene.Bounds;
            return bounds.IsValid && bounds.Diagonal > 0.0 ? bounds.Diagonal : 1.0;
        }
    }

    public MeshDto? GetMesh(int meshId) => _sceneAccessor()?.GetMesh(meshId);

    public SceneNode? GetNode(int nodeId) => _sceneAccessor()?.GetNode(nodeId);

    public Matrix4d GetWorldTransform(int nodeId)
        => _sceneAccessor()?.GetNode(nodeId)?.EffectiveWorldTransform ?? Matrix4d.Identity;

    public void ResetDiagnostics()
    {
        _lastRaycastLogKey = "";
        _lastRaycastLogMs = 0;
    }

    public void ClearAccelerationCache(string reason)
    {
        int count = _accelerations.Count;
        _accelerations.Clear();
        _cachedScene = null;
        _preferredNodeId = null;
        ResetDiagnostics();
        Log.Debug("FA.MeasureRaycast", $"Acceleration cache cleared: reason={reason}, entries={count}.");
    }

    public void SetPreferredNode(int nodeId)
        => _preferredNodeId = nodeId > 0 ? nodeId : null;

    public void ClearPreferredNode()
        => _preferredNodeId = null;

    public MeasureRaycastHit? Raycast(Vector3d origin, Vector3d direction)
        => Raycast(origin, direction, collectDiagnostics: false);

    public MeasureRaycastHit? Raycast(Vector3d origin, Vector3d direction, bool collectDiagnostics)
    {
        Scene? scene = _sceneAccessor();
        if (scene is null)
        {
            if (collectDiagnostics)
                LogRaycast("miss", "no scene", 0, origin, direction, null);
            return null;
        }

        if (!ReferenceEquals(scene, _cachedScene))
        {
            _accelerations.Clear();
            _cachedScene = scene;
            if (collectDiagnostics)
                Log.Debug("FA.MeasureRaycast", $"Scene cache reset: nodes={scene.NodesById.Count}, visibleMeshNodes={scene.GetVisibleNodes().Count}, meshes={scene.MeshesById.Count}.");
        }

        if (!IsFinite(origin) || !TryNormalize(direction, out Vector3d rayDir))
        {
            if (collectDiagnostics)
                LogRaycast("miss", "invalid ray", 0, origin, direction, null);
            return null;
        }

        List<RaycastCandidate> candidates = CollectCandidates(scene, origin, rayDir);
        IReadOnlyList<SectionPlane> sectionPlanes = GetActiveSectionPlanes();
        if (_preferredNodeId is int preferredNodeId)
        {
            foreach (RaycastCandidate candidate in candidates)
            {
                if (candidate.Node.Id != preferredNodeId)
                    continue;

                if (TryRaycastCandidate(candidate, origin, rayDir, sectionPlanes, out RaycastResult preferredHit))
                {
                    if (collectDiagnostics)
                        LogRaycast("hit", $"preferredNode={preferredNodeId}, mesh={preferredHit.MeshId}, triOffset={preferredHit.TriangleIndexOffset}, dist={preferredHit.Distance:0.###}, point={Format(preferredHit.WorldPoint)}", candidates.Count, origin, rayDir, preferredHit.NodeId);
                    return new MeasureRaycastHit(preferredHit.NodeId, preferredHit.MeshId, preferredHit.TriangleIndexOffset, preferredHit.WorldPoint);
                }

                if (collectDiagnostics)
                    LogRaycast("miss", $"preferredNode={preferredNodeId} missed; falling back", candidates.Count, origin, rayDir, preferredNodeId);
                break;
            }
        }

        double closestDistance = double.MaxValue;
        RaycastResult? closest = null;

        foreach (var candidate in candidates)
        {
            if (candidate.EntryDistance > closestDistance)
                break;

            if (!TryRaycastCandidate(candidate, origin, rayDir, sectionPlanes, out RaycastResult result))
                continue;

            if (result.Distance < closestDistance)
            {
                closestDistance = result.Distance;
                closest = result;
            }
        }

        if (closest is { } hit)
        {
            if (collectDiagnostics)
                LogRaycast("hit", $"node={hit.NodeId}, mesh={hit.MeshId}, triOffset={hit.TriangleIndexOffset}, dist={hit.Distance:0.###}, point={Format(hit.WorldPoint)}", candidates.Count, origin, rayDir, hit.NodeId);
            return new MeasureRaycastHit(hit.NodeId, hit.MeshId, hit.TriangleIndexOffset, hit.WorldPoint);
        }

        if (collectDiagnostics)
            LogRaycast("miss", "no triangle hit", candidates.Count, origin, rayDir, null);
        return null;
    }

    private List<RaycastCandidate> CollectCandidates(Scene scene, Vector3d rayOrigin, Vector3d rayDir)
    {
        IReadOnlyList<SceneNode> visibleNodes = scene.GetVisibleNodes();
        var candidates = new List<RaycastCandidate>(visibleNodes.Count);
        foreach (var node in visibleNodes)
        {
            if (node.MeshId is not int meshId)
                continue;
            MeshDto? mesh = scene.GetMesh(meshId);
            if (mesh is null || mesh.Positions.Length == 0 || mesh.Indices.Length == 0)
                continue;

            Matrix4d world = node.EffectiveWorldTransform;
            BoundingBox worldBounds = SceneBoundsUtilities.TransformBounds(mesh.Bounds, world);
            if (!AndroidMeshRaycastAcceleration.RayIntersectsBounds(
                    rayOrigin,
                    rayDir,
                    worldBounds,
                    double.MaxValue,
                    out double entryDistance)
                || !double.IsFinite(entryDistance))
            {
                continue;
            }

            candidates.Add(new RaycastCandidate(node, mesh, GetAcceleration(mesh), world, entryDistance));
        }

        candidates.Sort(static (a, b) => a.EntryDistance.CompareTo(b.EntryDistance));
        return candidates;
    }

    private AndroidMeshRaycastAcceleration GetAcceleration(MeshDto mesh)
    {
        if (_accelerations.TryGetValue(mesh.MeshId, out AccelerationEntry entry)
            && ReferenceEquals(entry.Mesh, mesh))
        {
            return entry.Acceleration;
        }

        AndroidMeshRaycastAcceleration acceleration = AndroidMeshRaycastAcceleration.Build(mesh);
        _accelerations[mesh.MeshId] = new AccelerationEntry(mesh, acceleration);
        return acceleration;
    }

    private static bool TryRaycastCandidate(
        RaycastCandidate candidate,
        Vector3d rayOrigin,
        Vector3d rayDir,
        IReadOnlyList<SectionPlane> sectionPlanes,
        out RaycastResult result)
    {
        result = default;

        if (!candidate.WorldTransform.TryInvert(out Matrix4d worldToLocal))
            return false;

        Vector3d localOrigin = worldToLocal.TransformPoint(rayOrigin);
        Vector3d localDirection = worldToLocal.TransformDirection(rayDir);
        if (!IsFinite(localOrigin) || !TryNormalize(localDirection, out Vector3d localDir))
            return false;

        if (!candidate.Acceleration.TryRaycast(
                localOrigin,
                localDir,
                out Vector3d localHit,
                out _,
                out int triangleIndexOffset,
                CreateLocalHitFilter(candidate.WorldTransform, sectionPlanes)))
        {
            return false;
        }

        Vector3d worldHit = candidate.WorldTransform.TransformPoint(localHit);
        double worldDistance = Vector3d.Distance(rayOrigin, worldHit);
        if (!double.IsFinite(worldDistance))
            return false;

        result = new RaycastResult(
            candidate.Node.Id,
            candidate.Mesh.MeshId,
            triangleIndexOffset,
            worldHit,
            worldDistance);
        return true;
    }

    private IReadOnlyList<SectionPlane> GetActiveSectionPlanes()
    {
        IReadOnlyList<SectionPlane>? planes = _sectionPlanesAccessor?.Invoke();
        return planes is { Count: > 0 } ? planes : Array.Empty<SectionPlane>();
    }

    private static Func<Vector3d, bool>? CreateLocalHitFilter(
        Matrix4d localToWorld,
        IReadOnlyList<SectionPlane> sectionPlanes)
    {
        if (sectionPlanes.Count == 0)
            return null;

        return localHit =>
        {
            Vector3d worldHit = localToWorld.TransformPoint(localHit);
            return AndroidSectionClipper.IsPointVisible(worldHit, sectionPlanes);
        };
    }

    private static bool TryNormalize(Vector3d value, out Vector3d normalized)
    {
        normalized = default;
        if (!IsFinite(value))
            return false;

        double length = value.Length;
        if (!double.IsFinite(length) || length <= 1e-12)
            return false;

        normalized = value / length;
        return true;
    }

    private static bool IsFinite(Vector3d value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private void LogRaycast(
        string result,
        string detail,
        int candidateCount,
        Vector3d origin,
        Vector3d direction,
        int? nodeId)
    {
        long now = Environment.TickCount64;
        string key = $"{result}|{detail}|{candidateCount}|{nodeId}";
        if (key == _lastRaycastLogKey && now - _lastRaycastLogMs < 1000)
            return;

        _lastRaycastLogKey = key;
        _lastRaycastLogMs = now;
        Log.Debug(
            "FA.MeasureRaycast",
            $"Raycast {result}: {detail}, candidates={candidateCount}, origin={Format(origin)}, dir={Format(direction)}.");
    }

    private static string Format(Vector3d value)
        => $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";

    private readonly record struct AccelerationEntry(MeshDto Mesh, AndroidMeshRaycastAcceleration Acceleration);

    private readonly record struct RaycastCandidate(
        SceneNode Node,
        MeshDto Mesh,
        AndroidMeshRaycastAcceleration Acceleration,
        Matrix4d WorldTransform,
        double EntryDistance);

    private readonly record struct RaycastResult(
        int NodeId,
        int MeshId,
        int TriangleIndexOffset,
        Vector3d WorldPoint,
        double Distance);
}
