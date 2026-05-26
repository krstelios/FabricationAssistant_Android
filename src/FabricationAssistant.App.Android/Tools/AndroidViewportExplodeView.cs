using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.App.Android.Tools;

internal sealed class AndroidViewportExplodeLayout
{
    public AndroidViewportExplodeLayout(IReadOnlyList<AndroidViewportExplodeUnit> units)
    {
        Units = units ?? throw new ArgumentNullException(nameof(units));
    }

    public IReadOnlyList<AndroidViewportExplodeUnit> Units { get; }

    public bool HasExplodableUnits => Units.Any(unit => unit.FullOffsetWorld.LengthSquared > 1e-12);
}

internal readonly record struct AndroidViewportExplodeUnit(
    int NodeId,
    Matrix4d BaseTransientTransform,
    Vector3d FullOffsetWorld);

internal static class AndroidViewportExplodeView
{
    private const double DirectionEpsilonSquared = 1e-12;
    private const double MinimumSceneExtent = 1e-4;
    private const double MinimumThicknessSceneScale = 0.01;
    private const double LayerPitchSceneScale = 0.08;
    private const double LayerPitchThicknessScale = 1.1;
    private const double OverlapEpsilon = 1e-9;

    public static AndroidViewportExplodeLayout Build(Scene scene)
    {
        BoundingBox sceneBounds = SceneBoundsUtilities.TryComputeNodeBounds(scene, scene.Root, out BoundingBox liveSceneBounds)
            ? liveSceneBounds
            : scene.Bounds;
        if (!sceneBounds.IsValid)
            return new AndroidViewportExplodeLayout(Array.Empty<AndroidViewportExplodeUnit>());

        Vector3d sceneCenter = sceneBounds.Center;
        Vector3d sceneHalfExtents = ClampHalfExtents(sceneBounds.Size * 0.5);
        double sceneDiagonal = System.Math.Max(sceneBounds.Diagonal, MinimumSceneExtent);

        List<ExplodeCandidate> candidates = EnumerateCandidates(scene)
            .OrderBy(candidate => candidate.NodeId)
            .ToList();
        if (candidates.Count == 0)
            return new AndroidViewportExplodeLayout(Array.Empty<AndroidViewportExplodeUnit>());

        List<ExplodePartPlan> plans = candidates
            .Select(candidate => BuildPlan(candidate, sceneCenter, sceneHalfExtents))
            .ToList();

        ResolveLayersAndOffsets(plans, sceneHalfExtents, sceneDiagonal);

        IReadOnlyList<AndroidViewportExplodeUnit> units = plans
            .OrderBy(plan => plan.NodeId)
            .Select(plan => new AndroidViewportExplodeUnit(
                plan.NodeId,
                plan.BaseTransientTransform,
                plan.FullOffsetWorld))
            .ToArray();

        return new AndroidViewportExplodeLayout(units);
    }

    public static void Apply(Scene scene, AndroidViewportExplodeLayout layout, double amount)
    {
        double clampedAmount = double.IsFinite(amount)
            ? System.Math.Clamp(amount, 0.0, 1.0)
            : 0.0;

        foreach (AndroidViewportExplodeUnit unit in layout.Units)
        {
            if (scene.GetNode(unit.NodeId) is not SceneNode node)
            {
                global::Android.Util.Log.Warn("FA.Explode", $"Skipping missing node in explode layout: nodeId={unit.NodeId}.");
                continue;
            }

            Matrix4d translation = Matrix4d.CreateTranslation(unit.FullOffsetWorld * clampedAmount);
            node.TransientTransform = translation * unit.BaseTransientTransform;
        }
    }

    public static void Clear(Scene scene, AndroidViewportExplodeLayout layout)
    {
        foreach (AndroidViewportExplodeUnit unit in layout.Units)
        {
            if (scene.GetNode(unit.NodeId) is SceneNode node)
                node.TransientTransform = unit.BaseTransientTransform;
        }
    }

    private static IEnumerable<ExplodeCandidate> EnumerateCandidates(Scene scene)
    {
        foreach (SceneNode node in scene.NodesById.Values)
        {
            if (node.MeshId is not int meshId)
                continue;

            MeshDto? mesh = scene.GetMesh(meshId);
            if (mesh is not { Bounds.IsValid: true })
                continue;

            BoundingBox directBounds = SceneBoundsUtilities.TransformBounds(mesh.Bounds, node.EffectiveWorldTransform);
            if (!directBounds.IsValid)
                continue;

            yield return new ExplodeCandidate(node.Id, directBounds, node.TransientTransform);
        }
    }

    private static ExplodePartPlan BuildPlan(ExplodeCandidate candidate, Vector3d sceneCenter, Vector3d sceneHalfExtents)
    {
        (ExplodeSector sector, Vector3d direction) = ClassifySector(candidate.Bounds, sceneCenter, sceneHalfExtents, candidate.NodeId);
        double supportOwn = ComputeSupport(candidate.Bounds, sceneCenter, direction);
        double boundary = GetBoundaryAlongSector(sceneHalfExtents, sector);
        double gap = System.Math.Max(0.0, boundary - supportOwn);
        double thicknessAlongDirection = GetThicknessAlongSector(candidate.Bounds, sector);
        ProjectedRect footprint = ProjectFootprint(candidate.Bounds, sector);

        return new ExplodePartPlan(candidate.NodeId, candidate.BaseTransientTransform, sector, direction, supportOwn, gap, thicknessAlongDirection, footprint);
    }

    private static void ResolveLayersAndOffsets(IReadOnlyList<ExplodePartPlan> plans, Vector3d sceneHalfExtents, double sceneDiagonal)
    {
        foreach (IGrouping<ExplodeSector, ExplodePartPlan> group in plans.GroupBy(plan => plan.Sector))
        {
            List<ExplodePartPlan> sectorPlans = group.ToList();
            double layerPitch = ComputeLayerPitch(sectorPlans, sceneDiagonal);

            foreach (ExplodePartPlan plan in sectorPlans)
            {
                plan.BaseLayer = 1 + (int)System.Math.Floor(plan.Gap / layerPitch);
                plan.Layer = plan.BaseLayer;
            }

            AssignConflictResolvedLayers(sectorPlans);

            int maxLayer = sectorPlans.Max(plan => plan.Layer);
            double boundary = GetBoundaryAlongSector(sceneHalfExtents, group.Key);

            foreach (ExplodePartPlan plan in sectorPlans)
            {
                double targetSupport = boundary + (maxLayer - plan.Layer + 1) * layerPitch;
                double moveDistance = System.Math.Max(0.0, targetSupport - plan.SupportOwn);
                plan.FullOffsetWorld = plan.Direction * moveDistance;
            }
        }
    }

    private static void AssignConflictResolvedLayers(List<ExplodePartPlan> sectorPlans)
    {
        sectorPlans.Sort(static (left, right) =>
        {
            int baseLayerComparison = left.BaseLayer.CompareTo(right.BaseLayer);
            if (baseLayerComparison != 0) return baseLayerComparison;
            int gapComparison = left.Gap.CompareTo(right.Gap);
            return gapComparison != 0 ? gapComparison : left.NodeId.CompareTo(right.NodeId);
        });

        var placedByLayer = new Dictionary<int, List<ProjectedRect>>();
        foreach (ExplodePartPlan plan in sectorPlans)
        {
            int layer = plan.BaseLayer;
            while (placedByLayer.TryGetValue(layer, out List<ProjectedRect>? placed)
                   && placed.Any(plan.Footprint.Overlaps))
            {
                layer++;
            }

            plan.Layer = layer;
            if (!placedByLayer.TryGetValue(layer, out List<ProjectedRect>? footprints))
            {
                footprints = new List<ProjectedRect>();
                placedByLayer[layer] = footprints;
            }

            footprints.Add(plan.Footprint);
        }
    }

    private static double ComputeLayerPitch(IReadOnlyList<ExplodePartPlan> sectorPlans, double sceneDiagonal)
    {
        double[] thicknesses = sectorPlans
            .Select(plan => System.Math.Max(plan.ThicknessAlongDirection, sceneDiagonal * MinimumThicknessSceneScale))
            .OrderBy(thickness => thickness)
            .ToArray();

        double typicalThickness = thicknesses.Length switch
        {
            0 => sceneDiagonal * MinimumThicknessSceneScale,
            int count when count % 2 == 0 => (thicknesses[count / 2 - 1] + thicknesses[count / 2]) * 0.5,
            _ => thicknesses[thicknesses.Length / 2],
        };

        return System.Math.Max(sceneDiagonal * LayerPitchSceneScale, typicalThickness * LayerPitchThicknessScale);
    }

    private static (ExplodeSector Sector, Vector3d Direction) ClassifySector(
        BoundingBox bounds,
        Vector3d sceneCenter,
        Vector3d sceneHalfExtents,
        int nodeId)
    {
        Vector3d delta = bounds.Center - sceneCenter;
        if (delta.LengthSquared < DirectionEpsilonSquared)
        {
            Vector3d fallbackDirection = BuildAxisFallback(bounds, sceneCenter, nodeId);
            return (ToSector(fallbackDirection), fallbackDirection);
        }

        double scoreX = System.Math.Abs(delta.X) / System.Math.Max(sceneHalfExtents.X, MinimumSceneExtent);
        double scoreY = System.Math.Abs(delta.Y) / System.Math.Max(sceneHalfExtents.Y, MinimumSceneExtent);
        double scoreZ = System.Math.Abs(delta.Z) / System.Math.Max(sceneHalfExtents.Z, MinimumSceneExtent);

        int axis = 0;
        double bestScore = scoreX;
        if (scoreY > bestScore) { axis = 1; bestScore = scoreY; }
        if (scoreZ > bestScore) axis = 2;

        double signedComponent = axis switch { 0 => delta.X, 1 => delta.Y, _ => delta.Z };
        if (System.Math.Abs(signedComponent) < 1e-9)
        {
            Vector3d fallbackDirection = BuildAxisFallback(bounds, sceneCenter, nodeId);
            return (ToSector(fallbackDirection), fallbackDirection);
        }

        Vector3d direction = GetSignedAxis(axis, signedComponent);
        return (ToSector(direction), direction);
    }

    private static Vector3d ClampHalfExtents(Vector3d halfExtents)
        => new(
            System.Math.Max(halfExtents.X, MinimumSceneExtent),
            System.Math.Max(halfExtents.Y, MinimumSceneExtent),
            System.Math.Max(halfExtents.Z, MinimumSceneExtent));

    private static Vector3d GetSignedAxis(int axis, double signedComponent)
        => axis switch
        {
            0 => signedComponent >= 0.0 ? Vector3d.UnitX : -Vector3d.UnitX,
            1 => signedComponent >= 0.0 ? Vector3d.UnitY : -Vector3d.UnitY,
            _ => signedComponent >= 0.0 ? Vector3d.UnitZ : -Vector3d.UnitZ,
        };

    private static ExplodeSector ToSector(Vector3d direction)
    {
        if (System.Math.Abs(direction.X) > 0.5)
            return direction.X >= 0.0 ? ExplodeSector.PositiveX : ExplodeSector.NegativeX;
        if (System.Math.Abs(direction.Y) > 0.5)
            return direction.Y >= 0.0 ? ExplodeSector.PositiveY : ExplodeSector.NegativeY;

        return direction.Z >= 0.0 ? ExplodeSector.PositiveZ : ExplodeSector.NegativeZ;
    }

    private static double ComputeSupport(BoundingBox bounds, Vector3d sceneCenter, Vector3d direction)
    {
        Vector3d centerOffset = bounds.Center - sceneCenter;
        Vector3d halfExtents = bounds.Size * 0.5;
        return Vector3d.Dot(centerOffset, direction)
            + System.Math.Abs(direction.X) * halfExtents.X
            + System.Math.Abs(direction.Y) * halfExtents.Y
            + System.Math.Abs(direction.Z) * halfExtents.Z;
    }

    private static double GetBoundaryAlongSector(Vector3d sceneHalfExtents, ExplodeSector sector)
        => sector switch
        {
            ExplodeSector.PositiveX or ExplodeSector.NegativeX => sceneHalfExtents.X,
            ExplodeSector.PositiveY or ExplodeSector.NegativeY => sceneHalfExtents.Y,
            _ => sceneHalfExtents.Z,
        };

    private static double GetThicknessAlongSector(BoundingBox bounds, ExplodeSector sector)
        => sector switch
        {
            ExplodeSector.PositiveX or ExplodeSector.NegativeX => bounds.Size.X,
            ExplodeSector.PositiveY or ExplodeSector.NegativeY => bounds.Size.Y,
            _ => bounds.Size.Z,
        };

    private static ProjectedRect ProjectFootprint(BoundingBox bounds, ExplodeSector sector)
        => sector switch
        {
            ExplodeSector.PositiveX or ExplodeSector.NegativeX => new ProjectedRect(bounds.Min.Y, bounds.Max.Y, bounds.Min.Z, bounds.Max.Z),
            ExplodeSector.PositiveY or ExplodeSector.NegativeY => new ProjectedRect(bounds.Min.X, bounds.Max.X, bounds.Min.Z, bounds.Max.Z),
            _ => new ProjectedRect(bounds.Min.X, bounds.Max.X, bounds.Min.Y, bounds.Max.Y),
        };

    private static Vector3d BuildAxisFallback(BoundingBox bounds, Vector3d sceneCenter, int nodeId)
    {
        Vector3d bias = bounds.Center - sceneCenter;
        Vector3d size = bounds.Size;

        if (size.X >= size.Y && size.X >= size.Z)
            return bias.X >= 0.0 ? Vector3d.UnitX : -Vector3d.UnitX;
        if (size.Y >= size.X && size.Y >= size.Z)
            return bias.Y >= 0.0 ? Vector3d.UnitY : -Vector3d.UnitY;
        if (size.Z > 0.0)
            return bias.Z >= 0.0 ? Vector3d.UnitZ : -Vector3d.UnitZ;

        return System.Math.Abs(nodeId % 3) switch
        {
            0 => Vector3d.UnitX,
            1 => Vector3d.UnitY,
            _ => Vector3d.UnitZ,
        };
    }

    private enum ExplodeSector { PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ }

    private readonly record struct ExplodeCandidate(int NodeId, BoundingBox Bounds, Matrix4d BaseTransientTransform);

    private sealed class ExplodePartPlan
    {
        public ExplodePartPlan(int nodeId, Matrix4d baseTransientTransform, ExplodeSector sector, Vector3d direction, double supportOwn, double gap, double thicknessAlongDirection, ProjectedRect footprint)
        {
            NodeId = nodeId;
            BaseTransientTransform = baseTransientTransform;
            Sector = sector;
            Direction = direction;
            SupportOwn = supportOwn;
            Gap = gap;
            ThicknessAlongDirection = thicknessAlongDirection;
            Footprint = footprint;
            BaseLayer = 1;
            Layer = 1;
            FullOffsetWorld = Vector3d.Zero;
        }

        public int NodeId { get; }
        public Matrix4d BaseTransientTransform { get; }
        public ExplodeSector Sector { get; }
        public Vector3d Direction { get; }
        public double SupportOwn { get; }
        public double Gap { get; }
        public double ThicknessAlongDirection { get; }
        public ProjectedRect Footprint { get; }
        public int BaseLayer { get; set; }
        public int Layer { get; set; }
        public Vector3d FullOffsetWorld { get; set; }
    }

    private readonly record struct ProjectedRect(double MinU, double MaxU, double MinV, double MaxV)
    {
        public bool Overlaps(ProjectedRect other)
            => !(MaxU < other.MinU - OverlapEpsilon
                 || other.MaxU < MinU - OverlapEpsilon
                 || MaxV < other.MinV - OverlapEpsilon
                 || other.MaxV < MinV - OverlapEpsilon);
    }
}
