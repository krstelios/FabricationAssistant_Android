using Android.Util;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Sections;

namespace FabricationAssistant.App.Android.Measurement;

internal sealed class AndroidMeasureRaycaster : IMeasureRaycaster, IMeasureSupplementalPointSnapProvider
{
    private const int PreparedSupplementalCandidateLimit = 24;
    private const long PreparedSupplementalBudgetMs = 48;

    private readonly Func<Scene?> _sceneAccessor;
    private readonly Func<IReadOnlyList<SectionPlane>>? _sectionPlanesAccessor;
    private readonly SectionMeasureGeometryProvider _sectionGeometry = new();
    private readonly EdgeSnapService _sectionEdgeSnap = new();
    private readonly EdgeSnapService _sceneEdgeSnap = new();
    private readonly FeatureEdgeExtractor _sceneFeatureEdges = new();
    private readonly Dictionary<int, AccelerationEntry> _accelerations = new();
    private Scene? _cachedScene;
    private float[]? _preparedSectionEdgePositions;
    private double _preparedSectionWeldToleranceScale = double.NaN;
    private long _cachedVisibilityVersion = -1;
    private long _cachedTransientTransformVersion = -1;
    private long _cachedMoveTransformVersion = -1;
    private string _lastRaycastLogKey = "";
    private long _lastRaycastLogMs;
    private readonly Dictionary<string, long> _supplementalSnapLogTimes = new();
    private int? _preferredNodeId;

    public bool SectionCurveSnapEnabled { get; set; } = true;

    public bool SectionCapRaycastEnabled { get; set; } = true;

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

    public MeshDto? GetMesh(int meshId)
    {
        Scene? scene = _sceneAccessor();
        if (SectionMeasureGeometryProvider.IsSectionMesh(meshId))
            return GetPreparedSectionMesh(scene, GetActiveSectionPlanes());

        return scene?.GetMesh(meshId);
    }

    public SceneNode? GetNode(int nodeId)
        => SectionMeasureGeometryProvider.IsSectionNode(nodeId)
            ? null
            : _sceneAccessor()?.GetNode(nodeId);

    public Matrix4d GetWorldTransform(int nodeId)
        => SectionMeasureGeometryProvider.IsSectionNode(nodeId)
            ? Matrix4d.Identity
            : _sceneAccessor()?.GetNode(nodeId)?.EffectiveWorldTransform ?? Matrix4d.Identity;

    public void ResetDiagnostics()
    {
        _lastRaycastLogKey = "";
        _lastRaycastLogMs = 0;
        _supplementalSnapLogTimes.Clear();
    }

    public void ClearAccelerationCache(string reason)
    {
        int count = _accelerations.Count;
        _accelerations.Clear();
        _sectionGeometry.Clear();
        _preparedSectionEdgePositions = null;
        _preparedSectionWeldToleranceScale = double.NaN;
        _cachedScene = null;
        _cachedVisibilityVersion = -1;
        _cachedTransientTransformVersion = -1;
        _cachedMoveTransformVersion = -1;
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

    public MeasureRaycastHit? RaycastForVisibility(Vector3d origin, Vector3d direction)
    {
        int? preferredNodeId = _preferredNodeId;
        _preferredNodeId = null;
        try
        {
            return Raycast(origin, direction, collectDiagnostics: false);
        }
        finally
        {
            _preferredNodeId = preferredNodeId;
        }
    }

    public bool IsWorldPointVisible(Vector3d point)
        => AndroidSectionClipper.IsPointVisible(point, GetActiveSectionPlanes(), SceneDiagonal);

    public MeasureRaycastHit? Raycast(Vector3d origin, Vector3d direction, bool collectDiagnostics)
    {
        Scene? scene = _sceneAccessor();
        if (scene is null)
        {
            if (collectDiagnostics)
                LogRaycast("miss", "no scene", 0, origin, direction, null);
            return null;
        }

        ResetSceneCacheIfNeeded(scene, collectDiagnostics);

        if (!IsFinite(origin) || !TryNormalize(direction, out Vector3d rayDir))
        {
            if (collectDiagnostics)
                LogRaycast("miss", "invalid ray", 0, origin, direction, null);
            return null;
        }

        List<RaycastCandidate> candidates = CollectCandidates(scene, origin, rayDir);
        IReadOnlyList<SectionPlane> sectionPlanes = GetActiveSectionPlanes();
        SectionMeasureRaycastResult? sectionHit = SectionCapRaycastEnabled
            ? _sectionGeometry.Raycast(
                scene,
                sectionPlanes,
                origin,
                rayDir)
            : null;
        if (_preferredNodeId is int preferredNodeId)
        {
            foreach (RaycastCandidate candidate in candidates)
            {
                if (candidate.Node.Id != preferredNodeId)
                    continue;

                if (TryRaycastCandidate(candidate, origin, rayDir, sectionPlanes, out RaycastResult preferredHit))
                {
                    if (sectionHit is { } section && section.Distance <= preferredHit.Distance)
                    {
                        if (collectDiagnostics)
                            LogRaycast("hit", $"section-cap over preferredNode={preferredNodeId}, mesh={section.Hit.MeshId}, triOffset={section.Hit.TriangleIndexOffset}, dist={section.Distance:0.###}, point={Format(section.Hit.WorldPoint)}", candidates.Count, origin, rayDir, section.Hit.NodeId);
                        return section.Hit;
                    }

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
            if (sectionHit is { } section && section.Distance <= hit.Distance)
            {
                if (collectDiagnostics)
                    LogRaycast("hit", $"section-cap mesh={section.Hit.MeshId}, triOffset={section.Hit.TriangleIndexOffset}, dist={section.Distance:0.###}, point={Format(section.Hit.WorldPoint)}", candidates.Count, origin, rayDir, section.Hit.NodeId);
                return section.Hit;
            }

            if (collectDiagnostics)
                LogRaycast("hit", $"node={hit.NodeId}, mesh={hit.MeshId}, triOffset={hit.TriangleIndexOffset}, dist={hit.Distance:0.###}, point={Format(hit.WorldPoint)}", candidates.Count, origin, rayDir, hit.NodeId);
            return new MeasureRaycastHit(hit.NodeId, hit.MeshId, hit.TriangleIndexOffset, hit.WorldPoint);
        }

        if (sectionHit is { } sectionOnly)
        {
            if (collectDiagnostics)
                LogRaycast("hit", $"section-cap mesh={sectionOnly.Hit.MeshId}, triOffset={sectionOnly.Hit.TriangleIndexOffset}, dist={sectionOnly.Distance:0.###}, point={Format(sectionOnly.Hit.WorldPoint)}", candidates.Count, origin, rayDir, sectionOnly.Hit.NodeId);
            return sectionOnly.Hit;
        }

        if (collectDiagnostics)
            LogRaycast("miss", "no triangle hit", candidates.Count, origin, rayDir, null);
        return null;
    }

    public bool TrySnapPoint(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double edgeAngularTolerance,
        double endpointAngularTolerance,
        out Vector3d worldPoint,
        bool allowBuild = true)
    {
        worldPoint = default;

        long start = Environment.TickCount64;
        EdgeSnapResult? sceneSnap = TrySnapVisibleModelEdges(
            rayOrigin,
            rayDirection,
            edgeAngularTolerance,
            endpointAngularTolerance,
            allowBuild,
            out SupplementalSnapStats sceneStats);
        EdgeSnapResult? sectionSnap = TrySnapSectionCurves(
            rayOrigin,
            rayDirection,
            edgeAngularTolerance,
            endpointAngularTolerance,
            allowBuild,
            out bool sectionAvailable);

        EdgeSnapResult? best = null;
        string source = "none";
        if (sceneSnap is { } model)
        {
            best = model;
            source = "model";
        }

        if (sectionSnap is { } section
            && (best is null || IsBetterSupplementalSnap(section, best.Value)))
        {
            best = section;
            source = "section";
        }

        if (best is not { } snap)
        {
            LogSupplementalSnap(
                "miss",
                $"source=none, nodes={sceneStats.VisibleNodes}, boundsCandidates={sceneStats.BoundsCandidates}, edgeMeshes={sceneStats.EdgeMeshes}, meshHits={sceneStats.MeshHits}, capped={sceneStats.Capped}, budgeted={sceneStats.Budgeted}, section={(sectionAvailable ? "available" : "none")}, elapsedMs={Environment.TickCount64 - start}",
                $"miss|bounds={sceneStats.BoundsCandidates}|edgeMeshes={sceneStats.EdgeMeshes}|capped={sceneStats.Capped}|budgeted={sceneStats.Budgeted}|section={sectionAvailable}");
            return false;
        }

        worldPoint = snap.WorldPoint;
        LogSupplementalSnap(
            "hit",
            $"source={source}, nodes={sceneStats.VisibleNodes}, boundsCandidates={sceneStats.BoundsCandidates}, edgeMeshes={sceneStats.EdgeMeshes}, meshHits={sceneStats.MeshHits}, capped={sceneStats.Capped}, budgeted={sceneStats.Budgeted}, depth={snap.RayDepth:0.###}, perp={snap.PerpendicularDistance:0.######}, elapsedMs={Environment.TickCount64 - start}",
            $"hit|source={source}|bounds={sceneStats.BoundsCandidates}|edgeMeshes={sceneStats.EdgeMeshes}|meshHits={sceneStats.MeshHits}|capped={sceneStats.Capped}|budgeted={sceneStats.Budgeted}");
        return true;
    }

    private EdgeSnapResult? TrySnapSectionCurves(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double edgeAngularTolerance,
        double endpointAngularTolerance,
        bool allowBuild,
        out bool sectionAvailable)
    {
        sectionAvailable = false;
        if (!SectionCurveSnapEnabled)
            return null;

        Scene? scene = _sceneAccessor();
        IReadOnlyList<SectionPlane> sectionPlanes = GetActiveSectionPlanes();
        if (scene is null || sectionPlanes.Count == 0)
            return null;

        MeshDto? mesh = GetPreparedSectionMesh(scene, sectionPlanes);
        if (mesh is null || mesh.EdgePositions.Length < 6)
            return null;

        sectionAvailable = true;
        return allowBuild
            ? _sectionEdgeSnap.TrySnap(
                mesh.EdgePositions,
                Matrix4d.Identity,
                rayOrigin,
                rayDirection,
                edgeAngularTolerance,
                endpointAngularTolerance)
            : _sectionEdgeSnap.TrySnapPrepared(
                mesh.EdgePositions,
                Matrix4d.Identity,
                rayOrigin,
                rayDirection,
                edgeAngularTolerance,
                endpointAngularTolerance);
    }

    private MeshDto? GetPreparedSectionMesh(Scene? scene, IReadOnlyList<SectionPlane> sectionPlanes)
    {
        MeshDto? mesh = _sectionGeometry.GetMesh(scene, sectionPlanes);
        PrepareSectionSnapModel(mesh);
        return mesh;
    }

    private void PrepareSectionSnapModel(MeshDto? mesh)
    {
        if (mesh is null || mesh.EdgePositions.Length < 6)
            return;

        float[] edges = mesh.EdgePositions;
        double weldScale = EdgeSnapService.WeldToleranceScale;
        if (ReferenceEquals(_preparedSectionEdgePositions, edges)
            && _preparedSectionWeldToleranceScale == weldScale)
        {
            return;
        }

        _sectionEdgeSnap.Prepare(edges);
        _preparedSectionEdgePositions = edges;
        _preparedSectionWeldToleranceScale = weldScale;
    }

    private EdgeSnapResult? TrySnapVisibleModelEdges(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double edgeAngularTolerance,
        double endpointAngularTolerance,
        bool allowBuild,
        out SupplementalSnapStats stats)
    {
        stats = default;
        Scene? scene = _sceneAccessor();
        if (scene is null || !TryNormalize(rayDirection, out Vector3d rayDir))
            return null;

        double maxTanTolerance = ResolveSupplementalSnapTanTolerance(edgeAngularTolerance, endpointAngularTolerance);
        if (maxTanTolerance <= 0.0)
            return null;
        if (!BoundsMayContainSnapCandidate(scene.Bounds, rayOrigin, rayDir, maxTanTolerance))
            return null;

        EdgeSnapResult? best = null;
        IReadOnlyList<SceneNode> visibleNodes = scene.GetVisibleNodes();
        stats.VisibleNodes = visibleNodes.Count;
        var candidates = new List<SupplementalEdgeCandidate>(visibleNodes.Count);
        foreach (SceneNode node in visibleNodes)
        {
            if (node.MeshId is not int meshId)
                continue;

            MeshDto? mesh = scene.GetMesh(meshId);
            if (mesh is null)
                continue;

            Matrix4d world = node.EffectiveWorldTransform;
            BoundingBox worldBounds = SceneBoundsUtilities.TransformBounds(mesh.Bounds, world);
            if (!TryComputeBoundsSnapScore(worldBounds, rayOrigin, rayDir, maxTanTolerance, out double score))
                continue;

            stats.BoundsCandidates++;
            candidates.Add(new SupplementalEdgeCandidate(node, mesh, world, score));
        }

        candidates.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        long start = Environment.TickCount64;
        foreach (SupplementalEdgeCandidate candidate in candidates)
        {
            if (!allowBuild)
            {
                if (stats.EdgeMeshes >= PreparedSupplementalCandidateLimit)
                {
                    stats.Capped = true;
                    break;
                }

                if (Environment.TickCount64 - start >= PreparedSupplementalBudgetMs)
                {
                    stats.Budgeted = true;
                    break;
                }
            }

            MeshDto mesh = candidate.Mesh;
            float[] edges = mesh.EdgePositions.Length >= 6
                ? mesh.EdgePositions
                : allowBuild
                    ? _sceneFeatureEdges.Extract(mesh.Positions, mesh.Indices)
                    : _sceneFeatureEdges.TryGetCached(mesh.Positions, mesh.Indices, out float[] cachedEdges)
                        ? cachedEdges
                        : Array.Empty<float>();
            if (edges.Length < 6)
                continue;

            stats.EdgeMeshes++;
            EdgeSnapResult? snap = allowBuild
                ? _sceneEdgeSnap.TrySnap(
                    edges,
                    candidate.WorldTransform,
                    rayOrigin,
                    rayDir,
                    edgeAngularTolerance,
                    endpointAngularTolerance)
                : _sceneEdgeSnap.TrySnapPrepared(
                    edges,
                    candidate.WorldTransform,
                    rayOrigin,
                    rayDir,
                    edgeAngularTolerance,
                    endpointAngularTolerance);
            if (snap is null)
                continue;

            stats.MeshHits++;
            if (best is null || IsBetterSupplementalSnap(snap.Value, best.Value))
                best = snap;
        }

        return best;
    }

    private static bool IsBetterSupplementalSnap(EdgeSnapResult candidate, EdgeSnapResult current)
    {
        double candidateScore = candidate.RayDepth > 0.0
            ? candidate.PerpendicularDistance / candidate.RayDepth
            : double.PositiveInfinity;
        double currentScore = current.RayDepth > 0.0
            ? current.PerpendicularDistance / current.RayDepth
            : double.PositiveInfinity;

        if (candidateScore < currentScore - 1.0e-10)
            return true;
        if (candidateScore > currentScore + 1.0e-10)
            return false;

        return candidate.RayDepth < current.RayDepth;
    }

    private static double ResolveSupplementalSnapTanTolerance(double edgeAngularTolerance, double endpointAngularTolerance)
    {
        double maxAngle = 0.0;
        if (EdgeSnapService.EndpointSnapEnabled)
            maxAngle = System.Math.Max(maxAngle, ResolveSnapAngle(endpointAngularTolerance, EdgeSnapService.EndpointSnapToleranceFactor));
        if (EdgeSnapService.MidpointSnapEnabled)
            maxAngle = System.Math.Max(maxAngle, ResolveSnapAngle(edgeAngularTolerance, EdgeSnapService.EdgeSnapToleranceFactor));

        return maxAngle > 0.0 ? System.Math.Tan(maxAngle) : 0.0;
    }

    private static double ResolveSnapAngle(double angularTolerance, double factor)
    {
        if (!double.IsFinite(angularTolerance) || angularTolerance <= 0.0)
            return 0.0;
        if (!double.IsFinite(factor) || factor <= 0.0)
            return 0.0;

        return angularTolerance * System.Math.Clamp(factor, 0.0, 16.0);
    }

    private static bool BoundsMayContainSnapCandidate(
        BoundingBox bounds,
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double tanTolerance)
        => TryComputeBoundsSnapScore(bounds, rayOrigin, rayDirection, tanTolerance, out _);

    private static bool TryComputeBoundsSnapScore(
        BoundingBox bounds,
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double tanTolerance,
        out double score)
    {
        score = double.PositiveInfinity;
        if (!bounds.IsValid || tanTolerance <= 0.0)
            return false;

        Vector3d center = bounds.Center;
        double radius = bounds.Diagonal * 0.5;
        if (!IsFinite(center) || !double.IsFinite(radius) || radius < 0.0)
            return false;

        Vector3d toCenter = center - rayOrigin;
        double depth = Vector3d.Dot(toCenter, rayDirection);
        if (!double.IsFinite(depth) || depth + radius <= 0.0)
            return false;

        Vector3d perpendicular = toCenter - rayDirection * depth;
        double perp = perpendicular.Length;
        if (!double.IsFinite(perp))
            return false;

        double effectiveDepth = System.Math.Max(0.0, depth) + radius;
        double maxPerp = radius + effectiveDepth * tanTolerance;
        if (perp > maxPerp)
            return false;

        score = System.Math.Max(0.0, perp - radius) / System.Math.Max(effectiveDepth, 1.0e-9);
        return double.IsFinite(score);
    }

    private void LogSupplementalSnap(string result, string detail, string? throttleKey = null)
    {
        long now = Environment.TickCount64;
        string key = $"{result}|{throttleKey ?? detail}";
        if (_supplementalSnapLogTimes.TryGetValue(key, out long last) && now - last < 750)
            return;

        if (_supplementalSnapLogTimes.Count > 256)
            _supplementalSnapLogTimes.Clear();

        _supplementalSnapLogTimes[key] = now;
        Log.Debug("FA.MeasureSnap", $"Supplemental snap {result}: {detail}.");
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

    private void ResetSceneCacheIfNeeded(Scene scene, bool collectDiagnostics)
    {
        long visibilityVersion = scene.VisibilityVersion;
        long transientTransformVersion = scene.TransientTransformVersion;
        long moveTransformVersion = scene.MoveTransformVersion;
        bool sceneChanged = !ReferenceEquals(scene, _cachedScene);
        bool versionChanged = !sceneChanged
            && (visibilityVersion != _cachedVisibilityVersion
                || transientTransformVersion != _cachedTransientTransformVersion
                || moveTransformVersion != _cachedMoveTransformVersion);

        if (!sceneChanged && !versionChanged)
            return;

        int entries = _accelerations.Count;
        _accelerations.Clear();
        _cachedScene = scene;
        _cachedVisibilityVersion = visibilityVersion;
        _cachedTransientTransformVersion = transientTransformVersion;
        _cachedMoveTransformVersion = moveTransformVersion;

        if (collectDiagnostics)
        {
            string reason = sceneChanged ? "scene-reference" : "scene-version";
            Log.Debug(
                "FA.MeasureRaycast",
                $"Scene cache reset: reason={reason}, entries={entries}, nodes={scene.NodesById.Count}, visibleMeshNodes={scene.GetVisibleNodes().Count}, meshes={scene.MeshesById.Count}, visibilityVersion={visibilityVersion}, transientTransformVersion={transientTransformVersion}, moveTransformVersion={moveTransformVersion}.");
        }
    }

    private AndroidMeshRaycastAcceleration GetAcceleration(MeshDto mesh)
    {
        MeshAccelerationKey key = MeshAccelerationKey.Create(mesh);
        if (_accelerations.TryGetValue(mesh.MeshId, out AccelerationEntry entry)
            && ReferenceEquals(entry.Mesh, mesh)
            && entry.Key.Equals(key))
        {
            return entry.Acceleration;
        }

        AndroidMeshRaycastAcceleration acceleration = AndroidMeshRaycastAcceleration.Build(mesh);
        _accelerations[mesh.MeshId] = new AccelerationEntry(mesh, key, acceleration);
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
                CreateLocalHitFilter(candidate.WorldTransform, sectionPlanes, candidate.Mesh.Bounds.Diagonal)))
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
        IReadOnlyList<SectionPlane> sectionPlanes,
        double sceneDiagonal)
    {
        if (sectionPlanes.Count == 0)
            return null;

        return localHit =>
        {
            Vector3d worldHit = localToWorld.TransformPoint(localHit);
            return AndroidSectionClipper.IsPointVisible(worldHit, sectionPlanes, sceneDiagonal);
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

    private readonly record struct MeshAccelerationKey(
        float[] Positions,
        int[] Indices,
        int PositionLength,
        int IndexLength,
        int TriangleCount,
        int ContentSampleHash)
    {
        public static MeshAccelerationKey Create(MeshDto mesh)
            => new(
                mesh.Positions,
                mesh.Indices,
                mesh.Positions.Length,
                mesh.Indices.Length,
                mesh.TriangleCount,
                ComputeContentSampleHash(mesh));

        private static int ComputeContentSampleHash(MeshDto mesh)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + mesh.MeshId;
                hash = hash * 31 + mesh.TriangleCount;
                hash = hash * 31 + SampleHash(mesh.Positions);
                hash = hash * 31 + SampleHash(mesh.Indices);
                hash = hash * 31 + mesh.Bounds.Min.X.GetHashCode();
                hash = hash * 31 + mesh.Bounds.Min.Y.GetHashCode();
                hash = hash * 31 + mesh.Bounds.Min.Z.GetHashCode();
                hash = hash * 31 + mesh.Bounds.Max.X.GetHashCode();
                hash = hash * 31 + mesh.Bounds.Max.Y.GetHashCode();
                hash = hash * 31 + mesh.Bounds.Max.Z.GetHashCode();
                return hash;
            }
        }

        private static int SampleHash(float[] values)
        {
            if (values.Length == 0)
                return 0;

            unchecked
            {
                int hash = values.Length;
                hash = hash * 31 + values[0].GetHashCode();
                hash = hash * 31 + values[values.Length / 2].GetHashCode();
                hash = hash * 31 + values[^1].GetHashCode();
                return hash;
            }
        }

        private static int SampleHash(int[] values)
        {
            if (values.Length == 0)
                return 0;

            unchecked
            {
                int hash = values.Length;
                hash = hash * 31 + values[0];
                hash = hash * 31 + values[values.Length / 2];
                hash = hash * 31 + values[^1];
                return hash;
            }
        }
    }

    private readonly record struct AccelerationEntry(
        MeshDto Mesh,
        MeshAccelerationKey Key,
        AndroidMeshRaycastAcceleration Acceleration);

    private readonly record struct RaycastCandidate(
        SceneNode Node,
        MeshDto Mesh,
        AndroidMeshRaycastAcceleration Acceleration,
        Matrix4d WorldTransform,
        double EntryDistance);

    private readonly record struct SupplementalEdgeCandidate(
        SceneNode Node,
        MeshDto Mesh,
        Matrix4d WorldTransform,
        double Score);

    private readonly record struct RaycastResult(
        int NodeId,
        int MeshId,
        int TriangleIndexOffset,
        Vector3d WorldPoint,
        double Distance);

    private struct SupplementalSnapStats
    {
        public int VisibleNodes;
        public int BoundsCandidates;
        public int EdgeMeshes;
        public int MeshHits;
        public bool Capped;
        public bool Budgeted;
    }
}
