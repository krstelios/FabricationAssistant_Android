using System.Runtime.CompilerServices;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Android point snapping for measure tools. Expensive edge welding runs once
/// per immutable edge buffer; hover-time work only scores cached snap targets.
/// </summary>
public sealed class EdgeSnapService
{
    private const double MinimumWeldTolerance = 1.0e-9;
    private const double DefaultWeldToleranceScale = 1.0e-5;
    private const double MinimumWeldToleranceScale = 1.0e-8;
    private const double MaximumWeldToleranceScale = 1.0e-3;
    private const double StraightLineToleranceScale = 1.0e-5;
    private const double BranchContinuationMaxTurnRadians = System.Math.PI / 6.0;
    private const double BranchContinuationAmbiguityDotMargin = 0.05;
    private const double SharpClosedLoopCornerRadians = System.Math.PI / 3.0;
    private const double ArcPlaneToleranceScale = 1.0e-4;
    private const double ArcRadialToleranceScale = 2.0e-2;
    private const double ArcSplitRadialToleranceScale = 1.0e-3;
    private const double ClosedMixedArcMinTurnRadians = 0.0025;
    private const double ClosedMixedArcMaxTurnRadians = TwoSegmentArcMaxTurnRadians;
    private const double ClosedMixedArcSegmentLengthRatio = 4.0;
    private const double TwoSegmentArcMaxTurnRadians = 35.0 * System.Math.PI / 180.0;
    private const double MinimumArcSweepRadians = 5.0 * System.Math.PI / 180.0;
    private const double MaximumArcSweepRadians = 2.0 * System.Math.PI - 1.0e-4;
    private const int MinimumClosedLoopMidpointTargetCount = 8;
    private const int MaximumClosedLoopMidpointTargetCount = 32;
    private const int MaxVisibilityProbeCandidates = 4;

    private static readonly ConditionalWeakTable<float[], SnapModelSlot> ModelCache = new();
    private static readonly object SnapScanLogLock = new();
    private static readonly Dictionary<string, long> SnapScanLogTimes = new();
    private const int MaxSnapScanLogsPerSecond = 8;
    private static long _snapScanLogWindowStartMs;
    private static int _snapScanLogWindowCount;

    public static bool SnapEnabled { get; set; } = true;

    public static bool EndpointSnapEnabled { get; set; } = true;

    public static bool MidpointSnapEnabled { get; set; } = true;

    public static double EdgeSnapToleranceFactor { get; set; } = 1.0;

    public static double EndpointSnapToleranceFactor { get; set; } = 1.0;

    public static double WeldToleranceScale { get; set; } = DefaultWeldToleranceScale;

    public static Func<EdgeSnapVisibilityRequest, bool>? VisibilityFilter { get; set; }

    public static Action<string>? DiagnosticsLog { get; set; }

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

        SnapModel model = ModelCache
            .GetValue(edgePositions, static _ => new SnapModelSlot())
            .GetOrBuild(edgePositions, ResolveWeldToleranceScale(WeldToleranceScale));

        return TrySnapTargets(
            model.Targets,
            $"edgeBuffer={RuntimeHelpers.GetHashCode(edgePositions)}",
            localToWorld,
            rayOrigin,
            rayDirection,
            angularTolerance,
            endpointAngularTolerance);
    }

    public void Prepare(float[] edgePositions)
    {
        if (edgePositions is null || edgePositions.Length < 6)
            return;

        ModelCache
            .GetValue(edgePositions, static _ => new SnapModelSlot())
            .GetOrBuild(edgePositions, ResolveWeldToleranceScale(WeldToleranceScale));
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

        SnapModel model = SnapModel.Build(worldEdgePositions, ResolveWeldToleranceScale(WeldToleranceScale));
        return TrySnapTargets(
            model.Targets,
            "worldEdgeList",
            Matrix4d.Identity,
            rayOrigin,
            rayDirection,
            angularTolerance,
            endpointAngularTolerance);
    }

    private static EdgeSnapResult? TrySnapTargets(
        SnapTarget[] targets,
        string diagnosticsSource,
        Matrix4d localToWorld,
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double angularTolerance,
        double endpointAngularTolerance)
    {
        if (!SnapEnabled || targets.Length == 0 || (!EndpointSnapEnabled && !MidpointSnapEnabled))
            return null;

        if (!TryNormalize(rayDirection, out Vector3d rayDir))
            return null;

        double endpointTanTolerance = EndpointSnapEnabled
            ? System.Math.Tan(ResolveAngularTolerance(endpointAngularTolerance, EndpointSnapToleranceFactor))
            : 0.0;
        double midpointTanTolerance = MidpointSnapEnabled
            ? System.Math.Tan(ResolveAngularTolerance(angularTolerance, EdgeSnapToleranceFactor))
            : 0.0;
        if (endpointTanTolerance <= 0.0 && midpointTanTolerance <= 0.0)
            return null;

        Span<SnapCandidate> visibilityCandidates = stackalloc SnapCandidate[MaxVisibilityProbeCandidates];
        int visibilityCandidateCount = 0;
        SnapScanDiagnostics diagnostics = new(
            targets.Length,
            ResolveAngularTolerance(endpointAngularTolerance, EndpointSnapToleranceFactor),
            ResolveAngularTolerance(angularTolerance, EdgeSnapToleranceFactor));

        for (int i = 0; i < targets.Length; i++)
        {
            SnapTarget target = targets[i];
            double tanTolerance = target.Kind == SnapTargetKind.Endpoint
                ? endpointTanTolerance
                : midpointTanTolerance;
            if (tanTolerance <= 0.0)
                continue;

            diagnostics.CountActiveTarget(target.Kind);

            Vector3d worldPoint = localToWorld.TransformPoint(target.Point);
            Vector3d to = worldPoint - rayOrigin;
            double depth = Vector3d.Dot(to, rayDir);
            if (depth <= 0.0 || !double.IsFinite(depth))
            {
                diagnostics.BehindRay++;
                continue;
            }

            Vector3d perpendicular = to - rayDir * depth;
            double perpSquared = perpendicular.LengthSquared;
            if (!double.IsFinite(perpSquared))
            {
                diagnostics.NonFiniteDistance++;
                continue;
            }

            double maxPerp = depth * tanTolerance;
            double angularDistance = System.Math.Sqrt(perpSquared) / depth;
            diagnostics.RecordClosest(target.Kind, angularDistance);
            if (perpSquared > maxPerp * maxPerp)
                continue;

            diagnostics.WithinTolerance++;
            double angularScore = perpSquared / (depth * depth);
            double perp = System.Math.Sqrt(perpSquared);
            var candidate = new SnapCandidate(worldPoint, depth, perp, angularScore, target);
            AddVisibilityCandidate(visibilityCandidates, ref visibilityCandidateCount, candidate);
        }

        SnapCandidate best = default;
        bool found = false;
        diagnostics.VisibilityCandidates = visibilityCandidateCount;
        for (int i = 0; i < visibilityCandidateCount; i++)
        {
            SnapCandidate candidate = visibilityCandidates[i];
            if (!IsTargetVisible(rayOrigin, rayDir, localToWorld, candidate))
            {
                diagnostics.VisibilityRejected++;
                continue;
            }

            best = candidate;
            found = true;
            break;
        }

        LogSnapScanDiagnostics(found ? "hit" : "miss", diagnosticsSource, diagnostics, found ? best : null);
        return found
            ? new EdgeSnapResult(best.WorldPoint, best.RayDepth, best.PerpendicularDistance)
            : null;
    }

    private static void AddVisibilityCandidate(
        Span<SnapCandidate> candidates,
        ref int count,
        SnapCandidate candidate)
    {
        int insert = 0;
        while (insert < count && !candidate.IsBetterThan(candidates[insert]))
            insert++;

        if (insert >= candidates.Length)
            return;

        if (count < candidates.Length)
            count++;

        for (int i = count - 1; i > insert; i--)
            candidates[i] = candidates[i - 1];

        candidates[insert] = candidate;
    }

    private static double ResolveAngularTolerance(double angularTolerance, double factor)
    {
        if (!double.IsFinite(angularTolerance) || angularTolerance <= 0.0)
            return 0.0;
        if (!double.IsFinite(factor) || factor <= 0.0)
            return 0.0;

        return angularTolerance * System.Math.Clamp(factor, 0.0, 16.0);
    }

    private static bool IsTargetVisible(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        Matrix4d localToWorld,
        SnapCandidate candidate)
    {
        if (VisibilityFilter is not { } filter)
            return true;

        SnapTarget target = candidate.Target;
        return filter(new EdgeSnapVisibilityRequest(
            rayOrigin,
            rayDirection,
            candidate.WorldPoint,
            candidate.RayDepth,
            candidate.PerpendicularDistance,
            localToWorld.TransformPoint(target.VisibilityStart),
            localToWorld.TransformPoint(target.VisibilityEnd)));
    }

    private static double ResolveWeldToleranceScale(double scale)
        => double.IsFinite(scale)
            ? System.Math.Clamp(scale, MinimumWeldToleranceScale, MaximumWeldToleranceScale)
            : DefaultWeldToleranceScale;

    private static double ResolveWeldTolerance(double boundsDiagonal, double weldToleranceScale)
    {
        double diagonal = double.IsFinite(boundsDiagonal) && boundsDiagonal > 0.0 ? boundsDiagonal : 1.0;
        return System.Math.Max(MinimumWeldTolerance, diagonal * weldToleranceScale);
    }

    private static bool TryNormalize(Vector3d value, out Vector3d normalized)
    {
        normalized = default;
        if (!IsFinite(value))
            return false;

        double length = value.Length;
        if (!double.IsFinite(length) || length <= 1.0e-12)
            return false;

        normalized = value / length;
        return true;
    }

    private static bool IsFinite(Vector3d p)
        => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static void LogSnapScanDiagnostics(
        string result,
        string source,
        SnapScanDiagnostics diagnostics,
        SnapCandidate? best)
    {
        if (DiagnosticsLog is not { } log)
            return;

        long now = Environment.TickCount64;
        string key = $"{source}|{result}|closest={diagnostics.ClosestKind}|within={Bucket(diagnostics.WithinTolerance)}|visibleReject={Bucket(diagnostics.VisibilityRejected)}";
        lock (SnapScanLogLock)
        {
            if (SnapScanLogTimes.TryGetValue(key, out long last) && now - last < 1000)
                return;

            if (now - _snapScanLogWindowStartMs >= 1000)
            {
                _snapScanLogWindowStartMs = now;
                _snapScanLogWindowCount = 0;
            }

            if (_snapScanLogWindowCount >= MaxSnapScanLogsPerSecond)
                return;

            if (SnapScanLogTimes.Count > 512)
                SnapScanLogTimes.Clear();

            SnapScanLogTimes[key] = now;
            _snapScanLogWindowCount++;
        }

        string bestDetail = best is { } hit
            ? $", bestKind={hit.Target.Kind}, bestDepth={hit.RayDepth:0.###}, bestPerp={hit.PerpendicularDistance:0.######}"
            : "";
        log(
            $"snap scan {result} source={source}, targets={diagnostics.Targets}, endpointTargets={diagnostics.EndpointTargets}, midpointTargets={diagnostics.MidpointTargets}, " +
            $"endpointEnabled={EndpointSnapEnabled}, midpointEnabled={MidpointSnapEnabled}, endpointLimit={diagnostics.EndpointTolerance:0.######}, midpointLimit={diagnostics.MidpointTolerance:0.######}, " +
            $"closestEndpoint={FormatAngular(diagnostics.ClosestEndpointAngular)}, closestMidpoint={FormatAngular(diagnostics.ClosestMidpointAngular)}, withinTolerance={diagnostics.WithinTolerance}, " +
            $"visibilityCandidates={diagnostics.VisibilityCandidates}, visibilityRejected={diagnostics.VisibilityRejected}, behindRay={diagnostics.BehindRay}, nonFiniteDistance={diagnostics.NonFiniteDistance}{bestDetail}.");
    }

    private static string FormatAngular(double value)
        => double.IsFinite(value) ? value.ToString("0.######") : "none";

    private static int Bucket(int value)
        => value switch
        {
            0 => 0,
            < 4 => value,
            < 16 => 4,
            < 64 => 16,
            _ => 64,
        };

    private sealed class SnapModelSlot
    {
        private SnapModel? _model;
        private double _weldToleranceScale;

        public SnapModel GetOrBuild(float[] edgePositions, double weldToleranceScale)
        {
            SnapModel? model = _model;
            if (model is not null && _weldToleranceScale == weldToleranceScale)
                return model;

            lock (this)
            {
                model = _model;
                if (model is not null && _weldToleranceScale == weldToleranceScale)
                    return model;

                model = SnapModel.Build(edgePositions, weldToleranceScale);
                _model = model;
                _weldToleranceScale = weldToleranceScale;
                return model;
            }
        }
    }

    private sealed class SnapModel
    {
        private SnapModel(SnapTarget[] targets)
        {
            Targets = targets;
        }

        public SnapTarget[] Targets { get; }

        public static SnapModel Build(float[] edgePositions, double weldToleranceScale)
        {
            var builder = new SnapModelBuilder(edgePositions, weldToleranceScale);
            SnapTarget[] targets = builder.Build();
            LogBuildDiagnostics(
                $"edgeBuffer={RuntimeHelpers.GetHashCode(edgePositions)}",
                builder,
                targets.Length);
            return new SnapModel(targets);
        }

        public static SnapModel Build(IReadOnlyList<Vector3d> edgePositions, double weldToleranceScale)
        {
            var builder = new SnapModelBuilder(edgePositions, weldToleranceScale);
            SnapTarget[] targets = builder.Build();
            LogBuildDiagnostics("worldEdgeList", builder, targets.Length);
            return new SnapModel(targets);
        }

        private static void LogBuildDiagnostics(string source, SnapModelBuilder builder, int targetCount)
        {
            if (DiagnosticsLog is not { } log)
                return;

            SnapBuildStats stats = builder.Stats;
            log(
                $"build source={source}, inputSegments={stats.InputSegments}, validSegments={stats.ValidSegments}, vertices={builder.VertexCount}, targets={targetCount}, " +
                $"boundsDiag={builder.BoundsDiagonal:0.######}, weldScale={builder.WeldToleranceScale:0.######E+0}, weldTol={builder.WeldTolerance:0.######E+0}, " +
                $"generatedPrimitives={stats.GeneratedPrimitives}, generatedLines={stats.GeneratedLines}, generatedArcs={stats.GeneratedArcs}, generatedFallbackLines={stats.GeneratedFallbackLines}, endpointTargets={stats.EndpointTargets}, midpointTargets={stats.MidpointTargets}, closedLoopMidpointTargets={stats.ClosedLoopMidpointTargets}, " +
                $"coveredSegments={stats.CoveredSourceSegments}, coveredByStraight={stats.SourceSegmentsCoveredByStraightLines}, coveredByArcs={stats.SourceSegmentsCoveredByArcs}, coveredByFallback={stats.SourceSegmentsCoveredByFallbackLines}, coverageMismatch={stats.ValidSegments - stats.CoveredSourceSegments}, " +
                $"chains={stats.Chains}, openChains={stats.OpenChains}, closedChains={stats.ClosedChains}, branchStops={stats.BranchStops}, branchContinuations={stats.BranchContinuations}, " +
                $"straightRuns={stats.StraightRuns}, arcRuns={stats.ArcRuns}, openMixedRuns={stats.OpenMixedRuns}, closedMixedRuns={stats.ClosedMixedRuns}, closedCircleRuns={stats.ClosedCircleRuns}, smoothClosedRuns={stats.SmoothClosedRuns}, fallbackChains={stats.FallbackChains}, fallbackSegments={stats.FallbackSegments}, " +
                $"arcCandidates={stats.ArcCandidates}, arcRejects=[{stats.FormatArcRejects()}], invalidSegments={stats.InvalidSegments}, shortSegments={stats.ShortSegments}, weldedSameVertex={stats.WeldedSameVertex}, duplicateSegments={stats.DuplicateSegments}.");

            log(
                $"generated edge primitives source={source}: total={stats.GeneratedPrimitives}, lines={stats.GeneratedLines}, arcs={stats.GeneratedArcs}, fallbackRawLines={stats.GeneratedFallbackLines}, coveredSegments={stats.CoveredSourceSegments}/{stats.ValidSegments}, coverageMismatch={stats.ValidSegments - stats.CoveredSourceSegments}, endpointTargets={stats.EndpointTargets}, midpointTargets={stats.MidpointTargets}, closedLoopMidpointTargets={stats.ClosedLoopMidpointTargets}, totalTargets={targetCount}.");

            foreach (string sample in stats.ArcRejectSamples)
                log($"arc reject sample source={source}, {sample}");
            foreach (string sample in stats.MixedRunSamples)
                log($"mixed run sample source={source}, {sample}");
        }
    }

    private sealed class SnapModelBuilder
    {
        private readonly List<SegmentInfo> _segments = new();
        private readonly List<VertexInfo> _vertices = new();
        private readonly List<SnapTarget> _targets = new();
        private readonly HashSet<SegmentKey> _segmentKeys = new();
        private readonly SnapBuildStats _stats = new();
        private readonly double _weldToleranceScale;
        private double _boundsDiagonal = 1.0;
        private double _weldTolerance = MinimumWeldTolerance;
        private double _weldToleranceSquared = MinimumWeldTolerance * MinimumWeldTolerance;

        public SnapModelBuilder(float[] edgePositions, double weldToleranceScale)
        {
            _weldToleranceScale = weldToleranceScale;
            InitializeFrom(edgePositions);
        }

        public SnapModelBuilder(IReadOnlyList<Vector3d> edgePositions, double weldToleranceScale)
        {
            _weldToleranceScale = weldToleranceScale;
            InitializeFrom(edgePositions);
        }

        public SnapBuildStats Stats => _stats;

        public int VertexCount => _vertices.Count;

        public double BoundsDiagonal => _boundsDiagonal;

        public double WeldToleranceScale => _weldToleranceScale;

        public double WeldTolerance => _weldTolerance;

        public SnapTarget[] Build()
        {
            if (_segments.Count == 0)
                return Array.Empty<SnapTarget>();

            var visited = new bool[_segments.Count];
            for (int vertexIndex = 0; vertexIndex < _vertices.Count; vertexIndex++)
            {
                VertexInfo vertex = _vertices[vertexIndex];
                if (vertex.Segments.Count == 2)
                    continue;

                foreach (int segmentIndex in vertex.Segments)
                {
                    if (!visited[segmentIndex])
                        AppendChain(TraceChain(vertexIndex, segmentIndex, visited));
                }
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                if (!visited[i])
                    AppendChain(TraceChain(_segments[i].VertexA, i, visited));
            }

            return _targets.Count == 0 ? Array.Empty<SnapTarget>() : _targets.ToArray();
        }

        private void InitializeFrom(float[] edgePositions)
        {
            int coordinateCount = edgePositions.Length - edgePositions.Length % 6;
            BoundsAccumulator bounds = default;
            for (int i = 0; i + 5 < coordinateCount; i += 6)
            {
                var a = new Vector3d(edgePositions[i], edgePositions[i + 1], edgePositions[i + 2]);
                var b = new Vector3d(edgePositions[i + 3], edgePositions[i + 4], edgePositions[i + 5]);
                if (!IsFinite(a) || !IsFinite(b))
                    continue;

                bounds.Include(a);
                bounds.Include(b);
            }

            InitializeTolerance(bounds);
            var welder = new VertexWelder(_vertices, _weldTolerance, _weldToleranceSquared);
            for (int i = 0; i + 5 < coordinateCount; i += 6)
            {
                var a = new Vector3d(edgePositions[i], edgePositions[i + 1], edgePositions[i + 2]);
                var b = new Vector3d(edgePositions[i + 3], edgePositions[i + 4], edgePositions[i + 5]);
                AddSegment(a, b, welder);
            }
        }

        private void InitializeFrom(IReadOnlyList<Vector3d> edgePositions)
        {
            BoundsAccumulator bounds = default;
            foreach (Vector3d p in edgePositions)
            {
                if (IsFinite(p))
                    bounds.Include(p);
            }

            InitializeTolerance(bounds);
            var welder = new VertexWelder(_vertices, _weldTolerance, _weldToleranceSquared);
            int segmentCount = edgePositions.Count / 2;
            for (int i = 0; i < segmentCount; i++)
            {
                int offset = i * 2;
                AddSegment(edgePositions[offset], edgePositions[offset + 1], welder);
            }
        }

        private void InitializeTolerance(BoundsAccumulator bounds)
        {
            _boundsDiagonal = bounds.DiagonalOrDefault();
            _weldTolerance = ResolveWeldTolerance(_boundsDiagonal, _weldToleranceScale);
            _weldToleranceSquared = _weldTolerance * _weldTolerance;
        }

        private void AddSegment(Vector3d a, Vector3d b, VertexWelder welder)
        {
            _stats.InputSegments++;
            if (!IsFinite(a) || !IsFinite(b))
            {
                _stats.InvalidSegments++;
                return;
            }

            Vector3d edge = b - a;
            double length = edge.Length;
            if (!double.IsFinite(length) || length <= _weldTolerance)
            {
                _stats.ShortSegments++;
                return;
            }

            int vertexA = welder.GetOrAdd(a);
            int vertexB = welder.GetOrAdd(b);
            if (vertexA == vertexB)
            {
                _stats.WeldedSameVertex++;
                return;
            }

            if (!_segmentKeys.Add(SegmentKey.From(vertexA, vertexB)))
            {
                _stats.DuplicateSegments++;
                return;
            }

            int index = _segments.Count;
            _segments.Add(new SegmentInfo(vertexA, vertexB, length));
            _stats.ValidSegments++;
            _vertices[vertexA].Segments.Add(index);
            _vertices[vertexB].Segments.Add(index);
        }

        private ChainInfo TraceChain(int startVertex, int firstSegment, bool[] visited)
        {
            var vertices = new List<int> { startVertex };
            int currentVertex = startVertex;
            int segment = firstSegment;
            bool closed = false;

            while (segment >= 0 && !visited[segment])
            {
                visited[segment] = true;
                SegmentInfo info = _segments[segment];
                int nextVertex = info.Other(currentVertex);
                if (nextVertex < 0)
                    break;

                vertices.Add(nextVertex);
                if (nextVertex == startVertex)
                {
                    closed = true;
                    break;
                }

                int nextSegment;
                if (_vertices[nextVertex].Segments.Count == 2)
                {
                    nextSegment = _vertices[nextVertex].Segments[0] == segment
                        ? _vertices[nextVertex].Segments[1]
                        : _vertices[nextVertex].Segments[0];
                }
                else if (TryFindSmoothContinuation(currentVertex, nextVertex, segment, visited, out nextSegment))
                {
                    _stats.BranchContinuations++;
                }
                else
                {
                    _stats.BranchStops++;
                    break;
                }

                currentVertex = nextVertex;
                segment = nextSegment;
            }

            return new ChainInfo(vertices, closed);
        }

        private bool TryFindSmoothContinuation(
            int previousVertex,
            int currentVertex,
            int incomingSegment,
            bool[] visited,
            out int nextSegment)
        {
            nextSegment = -1;

            Vector3d currentPoint = _vertices[currentVertex].Point;
            Vector3d incomingDirection = currentPoint - _vertices[previousVertex].Point;
            if (!TryNormalize(incomingDirection, out incomingDirection))
                return false;

            double minimumDot = System.Math.Cos(BranchContinuationMaxTurnRadians);
            double bestDot = double.NegativeInfinity;
            double secondBestDot = double.NegativeInfinity;
            int bestSegment = -1;

            foreach (int candidateSegment in _vertices[currentVertex].Segments)
            {
                if (candidateSegment == incomingSegment || visited[candidateSegment])
                    continue;

                int candidateVertex = _segments[candidateSegment].Other(currentVertex);
                if (candidateVertex < 0 || candidateVertex == previousVertex)
                    continue;

                Vector3d outgoingDirection = _vertices[candidateVertex].Point - currentPoint;
                if (!TryNormalize(outgoingDirection, out outgoingDirection))
                    continue;

                double dot = Vector3d.Dot(incomingDirection, outgoingDirection);
                if (dot < minimumDot)
                    continue;

                if (dot > bestDot)
                {
                    secondBestDot = bestDot;
                    bestDot = dot;
                    bestSegment = candidateSegment;
                }
                else if (dot > secondBestDot)
                {
                    secondBestDot = dot;
                }
            }

            if (bestSegment < 0)
                return false;

            if (double.IsFinite(secondBestDot) && bestDot - secondBestDot <= BranchContinuationAmbiguityDotMargin)
                return false;

            nextSegment = bestSegment;
            return true;
        }

        private void AppendChain(ChainInfo chain)
        {
            if (chain.VertexIndices.Count < 2)
                return;

            _stats.Chains++;
            int segmentCount = ChainSegmentCount(chain);
            if (chain.Closed)
            {
                _stats.ClosedChains++;
                Vector3d[] closedPoints = ResolvePoints(chain.VertexIndices);
                if (closedPoints.Length > 1 && Vector3d.Distance(closedPoints[0], closedPoints[^1]) <= _weldTolerance)
                    Array.Resize(ref closedPoints, closedPoints.Length - 1);

                if (TryAppendClosedCircleRun(closedPoints))
                {
                    _stats.SourceSegmentsCoveredByArcs += segmentCount;
                    return;
                }

                if (TryAppendClosedMixedRuns(closedPoints, out int closedStraightSegments, out int closedArcSegments))
                {
                    _stats.SourceSegmentsCoveredByStraightLines += closedStraightSegments;
                    _stats.SourceSegmentsCoveredByArcs += closedArcSegments;
                    return;
                }

                if (TryAppendSmoothClosedLoop(closedPoints))
                {
                    _stats.SourceSegmentsCoveredByArcs += segmentCount;
                    return;
                }

                _stats.FallbackChains++;
                AppendSegmentTargets(chain);
                return;
            }

            _stats.OpenChains++;
            Vector3d[] points = ResolvePoints(chain.VertexIndices);
            if (points.Length < 2)
                return;

            if (TryAppendStraightRun(points))
            {
                _stats.SourceSegmentsCoveredByStraightLines += segmentCount;
                return;
            }

            if (TryAppendArcThenStraightRun(points, out int arcSegments, out int straightSegments))
            {
                _stats.SourceSegmentsCoveredByArcs += arcSegments;
                _stats.SourceSegmentsCoveredByStraightLines += straightSegments;
                return;
            }

            if (TryAppendStraightThenArcRun(points, out straightSegments, out arcSegments))
            {
                _stats.SourceSegmentsCoveredByStraightLines += straightSegments;
                _stats.SourceSegmentsCoveredByArcs += arcSegments;
                return;
            }

            if (TryAppendOpenMixedRuns(points, out straightSegments, out arcSegments))
            {
                _stats.SourceSegmentsCoveredByStraightLines += straightSegments;
                _stats.SourceSegmentsCoveredByArcs += arcSegments;
                return;
            }

            if (TryAppendArcRun(points))
            {
                _stats.SourceSegmentsCoveredByArcs += segmentCount;
                return;
            }

            _stats.FallbackChains++;
            AppendSegmentTargets(chain);
        }

        private static int ChainSegmentCount(ChainInfo chain)
            => System.Math.Max(0, chain.VertexIndices.Count - 1);

        private Vector3d[] ResolvePoints(IReadOnlyList<int> vertexIndices)
        {
            var points = new Vector3d[vertexIndices.Count];
            for (int i = 0; i < vertexIndices.Count; i++)
                points[i] = _vertices[vertexIndices[i]].Point;
            return points;
        }

        private bool TryAppendStraightRun(Vector3d[] points)
        {
            if (!TryResolveStraightRun(points, 0, points.Length - 1, out Vector3d start, out Vector3d end))
                return false;

            AppendStraightRun(start, end);
            return true;
        }

        private void AppendStraightRun(Vector3d start, Vector3d end)
        {
            AppendLogicalRunTargets(start, end, fallbackRawLine: false);
            _stats.StraightRuns++;
        }

        private bool TryAppendArcThenStraightRun(
            Vector3d[] points,
            out int arcSegments,
            out int straightSegments)
        {
            arcSegments = 0;
            straightSegments = 0;
            if (points.Length < 4)
                return false;

            for (int split = points.Length - 2; split >= 2; split--)
            {
                if (!TryResolveStraightRun(points, split, points.Length - 1, out Vector3d straightStart, out Vector3d straightEnd))
                    continue;

                Vector3d[] arcPoints = Slice(points, 0, split);
                if (!TryResolveArcRun(arcPoints, recordRejects: false, out ArcFitInfo arc))
                    continue;

                if (!IsCleanArcSplitFit(arc) || !PointsDivergeFromArc(arc, points, split + 1, points.Length - 1))
                    continue;

                AppendArcRunTargets(arcPoints, arc);
                AppendStraightRun(straightStart, straightEnd);
                arcSegments = split;
                straightSegments = points.Length - 1 - split;
                return true;
            }

            return false;
        }

        private bool TryAppendStraightThenArcRun(
            Vector3d[] points,
            out int straightSegments,
            out int arcSegments)
        {
            straightSegments = 0;
            arcSegments = 0;
            if (points.Length < 4)
                return false;

            for (int split = 1; split <= points.Length - 3; split++)
            {
                if (!TryResolveStraightRun(points, 0, split, out Vector3d straightStart, out Vector3d straightEnd))
                    continue;

                Vector3d[] arcPoints = Slice(points, split, points.Length - 1);
                if (!TryResolveArcRun(arcPoints, recordRejects: false, out ArcFitInfo arc))
                    continue;

                if (!IsCleanArcSplitFit(arc) || !PointsDivergeFromArc(arc, points, 0, split - 1))
                    continue;

                AppendStraightRun(straightStart, straightEnd);
                AppendArcRunTargets(arcPoints, arc);
                straightSegments = split;
                arcSegments = points.Length - 1 - split;
                return true;
            }

            return false;
        }

        private bool TryResolveStraightRun(
            Vector3d[] points,
            int startIndex,
            int endIndex,
            out Vector3d start,
            out Vector3d end)
        {
            start = default;
            end = default;
            if (startIndex < 0 || endIndex >= points.Length || endIndex <= startIndex)
                return false;

            start = points[startIndex];
            end = points[endIndex];
            Vector3d edge = end - start;
            double length = edge.Length;
            if (!double.IsFinite(length) || length <= _weldTolerance)
                return false;

            Vector3d direction = edge / length;
            double lineTolerance = ResolveStraightLineTolerance(length);
            double lineToleranceSquared = lineTolerance * lineTolerance;
            for (int i = startIndex + 1; i < endIndex; i++)
            {
                if (DistanceSquaredToLine(points[i], start, direction) > lineToleranceSquared)
                    return false;
            }

            return true;
        }

        private static Vector3d[] Slice(Vector3d[] points, int startIndex, int endIndex)
        {
            int length = endIndex - startIndex + 1;
            var result = new Vector3d[length];
            Array.Copy(points, startIndex, result, 0, length);
            return result;
        }

        private bool TryAppendArcRun(Vector3d[] points)
        {
            _stats.ArcCandidates++;
            if (!TryResolveArcRun(points, recordRejects: true, out ArcFitInfo arc))
                return false;

            AppendArcRunTargets(points, arc);
            return true;
        }

        private bool TryResolveArcRun(Vector3d[] points, bool recordRejects, out ArcFitInfo arc)
        {
            arc = default;
            if (points.Length < 3)
            {
                Reject(ArcRejectReason.TooFewPoints, points);
                return false;
            }

            if (points.Length == 3 && !IsReliableTwoSegmentArc(points, out double twoSegmentTurn))
            {
                Reject(ArcRejectReason.TwoSegmentTurnTooSharp, points, value: twoSegmentTurn, tolerance: TwoSegmentArcMaxTurnRadians);
                return false;
            }

            double totalLength = PathLength(points);
            if (!double.IsFinite(totalLength) || totalLength <= _weldTolerance * 4.0)
            {
                Reject(ArcRejectReason.TooShort, points, totalLength, totalLength, _weldTolerance * 4.0);
                return false;
            }

            Vector3d start = points[0];
            Vector3d end = points[^1];
            Vector3d chord = end - start;
            double chordLength = chord.Length;
            if (!double.IsFinite(chordLength) || chordLength <= _weldTolerance)
            {
                Reject(ArcRejectReason.DegenerateChord, points, totalLength, chordLength, _weldTolerance);
                return false;
            }

            Vector3d chordDirection = chord / chordLength;
            double maxDeviationSquared = 0.0;
            int middleIndex = -1;
            for (int i = 1; i + 1 < points.Length; i++)
            {
                double deviationSquared = DistanceSquaredToLine(points[i], start, chordDirection);
                if (deviationSquared > maxDeviationSquared)
                {
                    maxDeviationSquared = deviationSquared;
                    middleIndex = i;
                }
            }

            if (middleIndex < 0)
            {
                Reject(ArcRejectReason.NoMiddlePoint, points, totalLength);
                return false;
            }

            double straightTolerance = ResolveStraightLineTolerance(totalLength);
            if (maxDeviationSquared <= straightTolerance * straightTolerance)
            {
                Reject(ArcRejectReason.TooStraight, points, totalLength, System.Math.Sqrt(maxDeviationSquared), straightTolerance);
                return false;
            }

            Vector3d middle = points[middleIndex];
            Vector3d normal = Vector3d.Cross(middle - start, end - start);
            if (!TryNormalize(normal, out normal))
            {
                Reject(ArcRejectReason.DegenerateNormal, points, totalLength);
                return false;
            }

            Vector3d u = chordDirection;
            Vector3d v = Vector3d.Cross(normal, u);
            if (!TryNormalize(v, out v))
            {
                Reject(ArcRejectReason.DegenerateBasis, points, totalLength);
                return false;
            }

            ProjectToPlane(middle, start, u, v, out _, out _, out double middlePlaneDistance);
            double arcPlaneTolerance = ResolveArcPlaneTolerance(totalLength);
            if (System.Math.Abs(middlePlaneDistance) > arcPlaneTolerance)
            {
                Reject(ArcRejectReason.MiddleOffPlane, points, totalLength, System.Math.Abs(middlePlaneDistance), arcPlaneTolerance);
                return false;
            }

            if (!TryFitCircleLeastSquares(points, start, u, v, out double centerX, out double centerY, out double radius))
            {
                Reject(ArcRejectReason.CircleFitFailed, points, totalLength);
                return false;
            }

            if (!ValidateArc(
                    points,
                    start,
                    u,
                    v,
                    centerX,
                    centerY,
                    radius,
                    totalLength,
                    out double sweep,
                    out double maxRadialError,
                    out double maxPlaneDistance,
                    out ArcRejectDetail reject))
            {
                Reject(reject.Reason, points, totalLength, reject.Value, reject.Tolerance, radius);
                return false;
            }

            arc = new ArcFitInfo(start, u, v, centerX, centerY, radius, sweep, totalLength, maxRadialError, maxPlaneDistance);
            return true;

            void Reject(
                ArcRejectReason reason,
                Vector3d[] rejectedPoints,
                double length = double.NaN,
                double value = double.NaN,
                double tolerance = double.NaN,
                double rejectedRadius = double.NaN)
            {
                if (recordRejects)
                    RejectArc(reason, rejectedPoints, length, value, tolerance, rejectedRadius);
            }
        }

        private void AppendArcRunTargets(Vector3d[] points, ArcFitInfo arc)
        {
            Vector3d start = points[0];
            Vector3d end = points[^1];
            double startAngle = System.Math.Atan2(-arc.CenterY, -arc.CenterX);
            double midpointAngle = startAngle + arc.Sweep * 0.5;
            Vector3d center = arc.Origin + arc.U * arc.CenterX + arc.V * arc.CenterY;
            Vector3d midpoint = center
                + arc.U * (System.Math.Cos(midpointAngle) * arc.Radius)
                + arc.V * (System.Math.Sin(midpointAngle) * arc.Radius);

            double visibilityDelta = System.Math.CopySign(
                System.Math.Min(System.Math.Abs(arc.Sweep) * 0.05, 0.05),
                arc.Sweep);
            Vector3d midpointBefore = center
                + arc.U * (System.Math.Cos(midpointAngle - visibilityDelta) * arc.Radius)
                + arc.V * (System.Math.Sin(midpointAngle - visibilityDelta) * arc.Radius);
            Vector3d midpointAfter = center
                + arc.U * (System.Math.Cos(midpointAngle + visibilityDelta) * arc.Radius)
                + arc.V * (System.Math.Sin(midpointAngle + visibilityDelta) * arc.Radius);

            AppendEndpointTarget(start, start, points.Length > 1 ? points[1] : end);
            AppendEndpointTarget(end, points.Length > 1 ? points[^2] : start, end);
            AppendMidpointTarget(midpoint, midpointBefore, midpointAfter);
            _stats.ArcRuns++;
            _stats.GeneratedArcs++;
        }

        private bool TryAppendOpenMixedRuns(
            Vector3d[] points,
            out int straightSegments,
            out int arcSegments)
        {
            straightSegments = 0;
            arcSegments = 0;
            int segmentCount = points.Length - 1;
            if (segmentCount < 4)
                return false;

            if (!TryClassifyOpenArcSegments(points, out bool[] arcSegmentMask))
                return false;

            RemoveShortOpenArcRuns(arcSegmentMask);
            int arcMaskCount = CountTrue(arcSegmentMask);
            if (arcMaskCount == 0 || arcMaskCount == segmentCount)
                return false;

            var runs = new List<LogicalRun>();
            int segment = 0;
            while (segment < segmentCount)
            {
                bool isArc = arcSegmentMask[segment];
                int runSegmentCount = 1;
                while (segment + runSegmentCount < segmentCount
                    && arcSegmentMask[segment + runSegmentCount] == isArc)
                {
                    runSegmentCount++;
                }

                Vector3d[] runPoints = OpenRunPoints(points, segment, runSegmentCount);
                if (isArc)
                {
                    _stats.ArcCandidates++;
                    if (!TryResolveArcRun(runPoints, recordRejects: true, out ArcFitInfo arc)
                        || !IsCleanArcSplitFit(arc))
                    {
                        return false;
                    }

                    runs.Add(LogicalRun.ArcRun(segment, runSegmentCount, arc));
                    arcSegments += runSegmentCount;
                }
                else
                {
                    if (!TryResolveStraightRun(runPoints, 0, runPoints.Length - 1, out Vector3d start, out Vector3d end))
                        return false;

                    runs.Add(LogicalRun.Line(segment, runSegmentCount, start, end));
                    straightSegments += runSegmentCount;
                }

                segment += runSegmentCount;
            }

            if (runs.Count < 2 || arcSegments == 0)
                return false;

            _stats.OpenMixedRuns++;
            _stats.RecordMixedRunSample(
                $"open accepted segments={segmentCount}, runs={runs.Count}, straightSegments={straightSegments}, arcSegments={arcSegments}");
            foreach (LogicalRun run in runs)
            {
                if (run.IsArc)
                {
                    Vector3d[] runPoints = OpenRunPoints(points, run.StartSegment, run.SegmentCount);
                    AppendArcRunTargets(runPoints, run.ArcFit);
                }
                else
                {
                    AppendStraightRun(run.Start, run.End);
                }
            }

            return true;
        }

        private bool TryClassifyOpenArcSegments(Vector3d[] points, out bool[] arcSegmentMask)
        {
            int segmentCount = points.Length - 1;
            arcSegmentMask = new bool[segmentCount];
            var directions = new Vector3d[segmentCount];
            var lengths = new double[segmentCount];
            for (int i = 0; i < segmentCount; i++)
            {
                Vector3d segment = points[i + 1] - points[i];
                double length = segment.Length;
                if (!double.IsFinite(length) || length <= _weldTolerance)
                    return false;

                lengths[i] = length;
                directions[i] = segment / length;
            }

            var turns = new double[points.Length];
            for (int i = 1; i < points.Length - 1; i++)
            {
                double dot = System.Math.Clamp(Vector3d.Dot(directions[i - 1], directions[i]), -1.0, 1.0);
                turns[i] = System.Math.Acos(dot);
            }

            for (int i = 0; i < segmentCount; i++)
            {
                double startTurn = i == 0 ? 0.0 : turns[i];
                double endTurn = i == segmentCount - 1 ? 0.0 : turns[i + 1];
                arcSegmentMask[i] = IsClosedMixedArcTurn(startTurn)
                    && IsClosedMixedArcTurn(endTurn)
                    && IsOpenMixedArcSegmentLength(lengths, i);
            }

            return true;
        }

        private static bool IsOpenMixedArcSegmentLength(double[] lengths, int index)
        {
            double length = lengths[index];
            double previousLength = index > 0 ? lengths[index - 1] : length;
            double nextLength = index + 1 < lengths.Length ? lengths[index + 1] : length;
            return IsClosedMixedArcSegmentLength(length, previousLength, nextLength);
        }

        private static void RemoveShortOpenArcRuns(bool[] arcSegmentMask)
        {
            int segmentCount = arcSegmentMask.Length;
            int segment = 0;
            while (segment < segmentCount)
            {
                bool isArc = arcSegmentMask[segment];
                int runSegmentCount = 1;
                while (segment + runSegmentCount < segmentCount
                    && arcSegmentMask[segment + runSegmentCount] == isArc)
                {
                    runSegmentCount++;
                }

                if (isArc && runSegmentCount < 2)
                {
                    for (int i = 0; i < runSegmentCount; i++)
                        arcSegmentMask[segment + i] = false;
                }

                segment += runSegmentCount;
            }
        }

        private static Vector3d[] OpenRunPoints(Vector3d[] points, int startSegment, int segmentCount)
        {
            var result = new Vector3d[segmentCount + 1];
            Array.Copy(points, startSegment, result, 0, result.Length);
            return result;
        }

        private bool TryAppendClosedMixedRuns(
            Vector3d[] points,
            out int straightSegments,
            out int arcSegments)
        {
            straightSegments = 0;
            arcSegments = 0;
            int segmentCount = points.Length;
            if (segmentCount < 4)
                return false;

            if (!TryClassifyClosedArcSegments(points, out bool[] arcSegmentMask))
                return false;

            RemoveShortClosedArcRuns(arcSegmentMask);
            int arcMaskCount = CountTrue(arcSegmentMask);
            if (arcMaskCount == 0 || arcMaskCount == segmentCount)
                return false;

            int startSegment = FindClosedRunStart(arcSegmentMask);
            if (startSegment < 0)
                return false;

            var runs = new List<LogicalRun>();
            int segment = startSegment;
            do
            {
                bool isArc = arcSegmentMask[segment];
                int runSegmentCount = 1;
                while (runSegmentCount < segmentCount
                    && arcSegmentMask[(segment + runSegmentCount) % segmentCount] == isArc)
                {
                    runSegmentCount++;
                }

                Vector3d[] runPoints = ClosedRunPoints(points, segment, runSegmentCount);
                if (isArc)
                {
                    _stats.ArcCandidates++;
                    if (!TryResolveArcRun(runPoints, recordRejects: true, out ArcFitInfo arc)
                        || !IsCleanArcSplitFit(arc))
                    {
                        _stats.RecordMixedRunSample(
                            $"closed rejected arc-run segments={runSegmentCount}, points={runPoints.Length}, startSegment={segment}, loopSegments={segmentCount}");
                        return false;
                    }

                    runs.Add(LogicalRun.ArcRun(segment, runSegmentCount, arc));
                    arcSegments += runSegmentCount;
                }
                else
                {
                    if (!TryResolveStraightRun(runPoints, 0, runPoints.Length - 1, out Vector3d start, out Vector3d end))
                    {
                        _stats.RecordMixedRunSample(
                            $"closed rejected line-run segments={runSegmentCount}, points={runPoints.Length}, startSegment={segment}, loopSegments={segmentCount}");
                        return false;
                    }

                    runs.Add(LogicalRun.Line(segment, runSegmentCount, start, end));
                    straightSegments += runSegmentCount;
                }

                segment = (segment + runSegmentCount) % segmentCount;
            }
            while (segment != startSegment);

            if (runs.Count < 2 || arcSegments == 0)
                return false;

            _stats.ClosedMixedRuns++;
            _stats.RecordMixedRunSample(
                $"closed accepted segments={segmentCount}, runs={runs.Count}, straightSegments={straightSegments}, arcSegments={arcSegments}");
            foreach (LogicalRun run in runs)
            {
                if (run.IsArc)
                {
                    Vector3d[] runPoints = ClosedRunPoints(points, run.StartSegment, run.SegmentCount);
                    AppendArcRunTargets(runPoints, run.ArcFit);
                }
                else
                {
                    AppendStraightRun(run.Start, run.End);
                }
            }

            return true;
        }

        private bool TryClassifyClosedArcSegments(Vector3d[] points, out bool[] arcSegmentMask)
        {
            int segmentCount = points.Length;
            arcSegmentMask = new bool[segmentCount];
            var directions = new Vector3d[segmentCount];
            var lengths = new double[segmentCount];
            for (int i = 0; i < segmentCount; i++)
            {
                Vector3d segment = points[(i + 1) % segmentCount] - points[i];
                double length = segment.Length;
                if (!double.IsFinite(length) || length <= _weldTolerance)
                    return false;

                lengths[i] = length;
                directions[i] = segment / length;
            }

            var turns = new double[segmentCount];
            for (int i = 0; i < segmentCount; i++)
            {
                Vector3d previous = directions[(i - 1 + segmentCount) % segmentCount];
                Vector3d next = directions[i];
                double dot = System.Math.Clamp(Vector3d.Dot(previous, next), -1.0, 1.0);
                turns[i] = System.Math.Acos(dot);
            }

            for (int i = 0; i < segmentCount; i++)
            {
                double startTurn = turns[i];
                double endTurn = turns[(i + 1) % segmentCount];
                arcSegmentMask[i] = IsClosedMixedArcTurn(startTurn)
                    && IsClosedMixedArcTurn(endTurn)
                    && IsClosedMixedArcSegmentLength(
                        lengths[i],
                        lengths[(i - 1 + segmentCount) % segmentCount],
                        lengths[(i + 1) % segmentCount]);
            }

            return true;
        }

        private static bool IsClosedMixedArcTurn(double turn)
            => double.IsFinite(turn)
               && turn >= ClosedMixedArcMinTurnRadians
               && turn <= ClosedMixedArcMaxTurnRadians;

        private static bool IsClosedMixedArcSegmentLength(double length, double previousLength, double nextLength)
        {
            if (!double.IsFinite(length) || !double.IsFinite(previousLength) || !double.IsFinite(nextLength))
                return false;

            double neighborMin = System.Math.Min(previousLength, nextLength);
            double neighborMax = System.Math.Max(previousLength, nextLength);
            return length <= neighborMax * ClosedMixedArcSegmentLengthRatio
                && length * ClosedMixedArcSegmentLengthRatio >= neighborMin;
        }

        private static void RemoveShortClosedArcRuns(bool[] arcSegmentMask)
        {
            int segmentCount = arcSegmentMask.Length;
            if (segmentCount == 0)
                return;

            int startSegment = FindClosedRunStart(arcSegmentMask);
            if (startSegment < 0)
                return;

            int segment = startSegment;
            do
            {
                bool isArc = arcSegmentMask[segment];
                int runSegmentCount = 1;
                while (runSegmentCount < segmentCount
                    && arcSegmentMask[(segment + runSegmentCount) % segmentCount] == isArc)
                {
                    runSegmentCount++;
                }

                if (isArc && runSegmentCount < 2)
                {
                    for (int i = 0; i < runSegmentCount; i++)
                        arcSegmentMask[(segment + i) % segmentCount] = false;
                }

                segment = (segment + runSegmentCount) % segmentCount;
            }
            while (segment != startSegment);
        }

        private static int FindClosedRunStart(bool[] arcSegmentMask)
        {
            int segmentCount = arcSegmentMask.Length;
            for (int i = 0; i < segmentCount; i++)
            {
                int previous = (i - 1 + segmentCount) % segmentCount;
                if (arcSegmentMask[i] != arcSegmentMask[previous])
                    return i;
            }

            return segmentCount > 0 ? 0 : -1;
        }

        private static int CountTrue(bool[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    count++;
            }

            return count;
        }

        private static Vector3d[] ClosedRunPoints(Vector3d[] points, int startSegment, int segmentCount)
        {
            var result = new Vector3d[segmentCount + 1];
            int pointCount = points.Length;
            for (int i = 0; i <= segmentCount; i++)
                result[i] = points[(startSegment + i) % pointCount];
            return result;
        }

        private bool TryAppendClosedCircleRun(Vector3d[] points)
        {
            _stats.ArcCandidates++;
            if (points.Length < 8)
            {
                RejectArc(ArcRejectReason.TooFewPoints, points);
                return false;
            }

            double totalLength = ClosedPathLength(points);
            if (!double.IsFinite(totalLength) || totalLength <= _weldTolerance * 8.0)
            {
                RejectArc(ArcRejectReason.TooShort, points, totalLength, totalLength, _weldTolerance * 8.0);
                return false;
            }

            Vector3d start = points[0];
            int oppositeIndex = 1;
            double oppositeDistanceSquared = 0.0;
            for (int i = 1; i < points.Length; i++)
            {
                double distanceSquared = (points[i] - start).LengthSquared;
                if (distanceSquared > oppositeDistanceSquared)
                {
                    oppositeDistanceSquared = distanceSquared;
                    oppositeIndex = i;
                }
            }

            double chordLength = System.Math.Sqrt(oppositeDistanceSquared);
            if (!double.IsFinite(chordLength) || chordLength <= _weldTolerance * 2.0)
            {
                RejectArc(ArcRejectReason.DegenerateChord, points, totalLength, chordLength, _weldTolerance * 2.0);
                return false;
            }

            Vector3d chordDirection = (points[oppositeIndex] - start) / chordLength;
            int middleIndex = -1;
            double maxDeviationSquared = 0.0;
            for (int i = 1; i < points.Length; i++)
            {
                if (i == oppositeIndex)
                    continue;

                double deviationSquared = DistanceSquaredToLine(points[i], start, chordDirection);
                if (deviationSquared > maxDeviationSquared)
                {
                    maxDeviationSquared = deviationSquared;
                    middleIndex = i;
                }
            }

            double straightTolerance = ResolveStraightLineTolerance(totalLength);
            if (middleIndex < 0 || maxDeviationSquared <= straightTolerance * straightTolerance)
            {
                RejectArc(ArcRejectReason.TooStraight, points, totalLength, System.Math.Sqrt(maxDeviationSquared), straightTolerance);
                return false;
            }

            Vector3d middle = points[middleIndex];
            Vector3d normal = Vector3d.Cross(middle - start, points[oppositeIndex] - start);
            if (!TryNormalize(normal, out normal))
            {
                RejectArc(ArcRejectReason.DegenerateNormal, points, totalLength);
                return false;
            }

            Vector3d u = chordDirection;
            Vector3d v = Vector3d.Cross(normal, u);
            if (!TryNormalize(v, out v))
            {
                RejectArc(ArcRejectReason.DegenerateBasis, points, totalLength);
                return false;
            }

            ProjectToPlane(middle, start, u, v, out _, out _, out double middlePlaneDistance);
            double arcPlaneTolerance = ResolveArcPlaneTolerance(totalLength);
            if (System.Math.Abs(middlePlaneDistance) > arcPlaneTolerance)
            {
                RejectArc(ArcRejectReason.MiddleOffPlane, points, totalLength, System.Math.Abs(middlePlaneDistance), arcPlaneTolerance);
                return false;
            }

            if (!TryFitCircleLeastSquares(points, start, u, v, out double centerX, out double centerY, out double radius))
            {
                RejectArc(ArcRejectReason.CircleFitFailed, points, totalLength);
                return false;
            }

            if (!ValidateClosedCircle(points, start, u, v, centerX, centerY, radius, totalLength, out ArcRejectDetail reject))
            {
                RejectArc(reject.Reason, points, totalLength, reject.Value, reject.Tolerance, radius);
                return false;
            }

            AppendClosedCircleMidpointTargets(start, u, v, centerX, centerY, radius, points.Length);
            _stats.ClosedCircleRuns++;
            _stats.GeneratedArcs++;
            return true;
        }

        private bool TryAppendSmoothClosedLoop(Vector3d[] points)
        {
            if (points.Length < 4)
                return false;

            for (int i = 0; i < points.Length; i++)
            {
                Vector3d previous = points[i] - points[(i - 1 + points.Length) % points.Length];
                Vector3d next = points[(i + 1) % points.Length] - points[i];
                if (!TryNormalize(previous, out previous) || !TryNormalize(next, out next))
                    return false;

                double dot = System.Math.Clamp(Vector3d.Dot(previous, next), -1.0, 1.0);
                double turn = System.Math.Acos(dot);
                if (turn > SharpClosedLoopCornerRadians)
                    return false;
            }

            _stats.SmoothClosedRuns++;
            AppendSmoothClosedLoopMidpointTargets(points);
            _stats.GeneratedArcs++;
            return true;
        }

        private void AppendClosedCircleMidpointTargets(
            Vector3d origin,
            Vector3d u,
            Vector3d v,
            double centerX,
            double centerY,
            double radius,
            int sourcePointCount)
        {
            if (!double.IsFinite(radius) || radius <= _weldTolerance * 2.0)
                return;

            Vector3d center = origin + u * centerX + v * centerY;
            double startAngle = System.Math.Atan2(-centerY, -centerX);
            int targetCount = ResolveClosedLoopMidpointTargetCount(sourcePointCount);
            double step = 2.0 * System.Math.PI / targetCount;
            double visibilityDelta = System.Math.Min(step * 0.08, 0.05);

            for (int i = 0; i < targetCount; i++)
            {
                double angle = startAngle + i * step;
                Vector3d point = PointOnCircle(center, u, v, radius, angle);
                Vector3d before = PointOnCircle(center, u, v, radius, angle - visibilityDelta);
                Vector3d after = PointOnCircle(center, u, v, radius, angle + visibilityDelta);
                AppendMidpointTarget(point, before, after);
                _stats.ClosedLoopMidpointTargets++;
            }
        }

        private void AppendSmoothClosedLoopMidpointTargets(Vector3d[] points)
        {
            double totalLength = ClosedPathLength(points);
            if (!double.IsFinite(totalLength) || totalLength <= _weldTolerance * 4.0)
                return;

            double visibilityOffset = System.Math.Max(_weldTolerance * 4.0, totalLength * 0.002);
            int targetCount = ResolveClosedLoopMidpointTargetCount(points.Length);
            for (int i = 0; i < targetCount; i++)
            {
                double distance = i * totalLength / targetCount;
                Vector3d point = ClosedPathPointAtDistance(points, distance);
                Vector3d before = ClosedPathPointAtDistance(points, distance - visibilityOffset);
                Vector3d after = ClosedPathPointAtDistance(points, distance + visibilityOffset);
                AppendMidpointTarget(point, before, after);
                _stats.ClosedLoopMidpointTargets++;
            }
        }

        private static int ResolveClosedLoopMidpointTargetCount(int sourcePointCount)
        {
            return System.Math.Clamp(
                sourcePointCount,
                MinimumClosedLoopMidpointTargetCount,
                MaximumClosedLoopMidpointTargetCount);
        }

        private bool IsCleanArcSplitFit(ArcFitInfo arc)
        {
            double radialTolerance = ResolveArcSplitRadialTolerance(arc.Radius);
            double planeTolerance = ResolveArcPlaneTolerance(arc.TotalLength);
            return arc.MaxRadialError <= radialTolerance
                && arc.MaxPlaneDistance <= planeTolerance;
        }

        private bool PointsDivergeFromArc(ArcFitInfo arc, Vector3d[] points, int startIndex, int endIndex)
        {
            if (startIndex < 0 || endIndex >= points.Length || endIndex < startIndex)
                return false;

            double radialTolerance = ResolveArcSplitRadialTolerance(arc.Radius);
            double planeTolerance = ResolveArcPlaneTolerance(arc.TotalLength);
            for (int i = startIndex; i <= endIndex; i++)
            {
                ProjectToPlane(points[i], arc.Origin, arc.U, arc.V, out double x, out double y, out double planeDistance);
                if (System.Math.Abs(planeDistance) > planeTolerance)
                    return true;

                double dx = x - arc.CenterX;
                double dy = y - arc.CenterY;
                double distance = System.Math.Sqrt(dx * dx + dy * dy);
                double radialError = System.Math.Abs(distance - arc.Radius);
                if (!double.IsFinite(distance) || radialError > radialTolerance)
                    return true;
            }

            return false;
        }

        private bool ValidateArc(
            Vector3d[] points,
            Vector3d origin,
            Vector3d u,
            Vector3d v,
            double centerX,
            double centerY,
            double radius,
            double totalLength,
            out double sweep,
            out double maxRadialError,
            out double maxPlaneDistance,
            out ArcRejectDetail reject)
        {
            sweep = 0.0;
            maxRadialError = 0.0;
            maxPlaneDistance = 0.0;
            reject = default;
            if (!double.IsFinite(radius) || radius <= _weldTolerance * 2.0)
            {
                reject = new ArcRejectDetail(ArcRejectReason.BadRadius, radius, _weldTolerance * 2.0);
                return false;
            }

            double planeTolerance = ResolveArcPlaneTolerance(totalLength);
            double radialTolerance = System.Math.Max(_weldTolerance * 20.0, radius * ArcRadialToleranceScale);
            double previousAngle = 0.0;
            int sign = 0;
            bool hasPrevious = false;

            for (int i = 0; i < points.Length; i++)
            {
                ProjectToPlane(points[i], origin, u, v, out double x, out double y, out double planeDistance);
                double absPlaneDistance = System.Math.Abs(planeDistance);
                if (absPlaneDistance > maxPlaneDistance)
                    maxPlaneDistance = absPlaneDistance;

                if (absPlaneDistance > planeTolerance)
                {
                    reject = new ArcRejectDetail(ArcRejectReason.PointOffPlane, absPlaneDistance, planeTolerance);
                    return false;
                }

                double dx = x - centerX;
                double dy = y - centerY;
                double distance = System.Math.Sqrt(dx * dx + dy * dy);
                double radialError = System.Math.Abs(distance - radius);
                if (radialError > maxRadialError)
                    maxRadialError = radialError;

                if (!double.IsFinite(distance) || radialError > radialTolerance)
                {
                    reject = new ArcRejectDetail(ArcRejectReason.RadialError, radialError, radialTolerance);
                    return false;
                }

                double angle = System.Math.Atan2(dy, dx);
                if (!hasPrevious)
                {
                    previousAngle = angle;
                    hasPrevious = true;
                    continue;
                }

                double delta = NormalizeAngleDelta(angle - previousAngle);
                if (System.Math.Abs(delta) <= 1.0e-9)
                    continue;

                int deltaSign = delta > 0.0 ? 1 : -1;
                if (sign == 0)
                {
                    sign = deltaSign;
                }
                else if (sign != deltaSign)
                {
                    reject = new ArcRejectDetail(ArcRejectReason.SweepDirectionChanged, delta, 0.0);
                    return false;
                }

                sweep += delta;
                previousAngle = angle;
            }

            double absSweep = System.Math.Abs(sweep);
            if (sign == 0)
            {
                reject = new ArcRejectDetail(ArcRejectReason.ZeroSweep, 0.0, MinimumArcSweepRadians);
                return false;
            }

            if (absSweep < MinimumArcSweepRadians)
            {
                reject = new ArcRejectDetail(ArcRejectReason.SweepTooSmall, absSweep, MinimumArcSweepRadians);
                return false;
            }

            if (absSweep > MaximumArcSweepRadians)
            {
                reject = new ArcRejectDetail(ArcRejectReason.SweepTooLarge, absSweep, MaximumArcSweepRadians);
                return false;
            }

            return true;
        }

        private static bool IsReliableTwoSegmentArc(Vector3d[] points, out double turn)
        {
            turn = double.NaN;
            if (points.Length != 3)
                return false;

            Vector3d first = points[1] - points[0];
            Vector3d second = points[2] - points[1];
            if (!TryNormalize(first, out first) || !TryNormalize(second, out second))
                return false;

            double dot = System.Math.Clamp(Vector3d.Dot(first, second), -1.0, 1.0);
            turn = System.Math.Acos(dot);
            return turn <= TwoSegmentArcMaxTurnRadians;
        }

        private bool ValidateClosedCircle(
            Vector3d[] points,
            Vector3d origin,
            Vector3d u,
            Vector3d v,
            double centerX,
            double centerY,
            double radius,
            double totalLength,
            out ArcRejectDetail reject)
        {
            reject = default;
            if (!double.IsFinite(radius) || radius <= _weldTolerance * 2.0)
            {
                reject = new ArcRejectDetail(ArcRejectReason.BadRadius, radius, _weldTolerance * 2.0);
                return false;
            }

            double planeTolerance = ResolveArcPlaneTolerance(totalLength);
            double radialTolerance = System.Math.Max(_weldTolerance * 20.0, radius * ArcRadialToleranceScale);
            for (int i = 0; i < points.Length; i++)
            {
                ProjectToPlane(points[i], origin, u, v, out double x, out double y, out double planeDistance);
                if (System.Math.Abs(planeDistance) > planeTolerance)
                {
                    reject = new ArcRejectDetail(ArcRejectReason.PointOffPlane, System.Math.Abs(planeDistance), planeTolerance);
                    return false;
                }

                double dx = x - centerX;
                double dy = y - centerY;
                double distance = System.Math.Sqrt(dx * dx + dy * dy);
                double radialError = System.Math.Abs(distance - radius);
                if (!double.IsFinite(distance) || radialError > radialTolerance)
                {
                    reject = new ArcRejectDetail(ArcRejectReason.RadialError, radialError, radialTolerance);
                    return false;
                }
            }

            return true;
        }

        private void RejectArc(
            ArcRejectReason reason,
            Vector3d[] points,
            double length = double.NaN,
            double value = double.NaN,
            double tolerance = double.NaN,
            double radius = double.NaN)
            => _stats.RecordArcReject(reason, points.Length, length, value, tolerance, radius, _weldTolerance);

        private void AppendLogicalRunTargets(Vector3d start, Vector3d end, bool fallbackRawLine)
        {
            _stats.GeneratedLines++;
            if (fallbackRawLine)
                _stats.GeneratedFallbackLines++;

            AppendEndpointTarget(start, start, end);
            AppendEndpointTarget(end, start, end);

            if (Vector3d.Distance(start, end) > _weldTolerance * 2.0)
                AppendMidpointTarget(Vector3d.Lerp(start, end, 0.5), start, end);
        }

        private void AppendSegmentTargets(ChainInfo chain)
        {
            int segmentCount = System.Math.Max(0, chain.VertexIndices.Count - 1);
            _stats.FallbackSegments += segmentCount;
            _stats.SourceSegmentsCoveredByFallbackLines += segmentCount;
            for (int i = 1; i < chain.VertexIndices.Count; i++)
            {
                Vector3d start = _vertices[chain.VertexIndices[i - 1]].Point;
                Vector3d end = _vertices[chain.VertexIndices[i]].Point;
                AppendLogicalRunTargets(start, end, fallbackRawLine: true);
            }
        }

        private void AppendEndpointTarget(Vector3d point, Vector3d visibilityStart, Vector3d visibilityEnd)
        {
            _stats.EndpointTargets++;
            _targets.Add(new SnapTarget(point, SnapTargetKind.Endpoint, visibilityStart, visibilityEnd));
        }

        private void AppendMidpointTarget(Vector3d point, Vector3d visibilityStart, Vector3d visibilityEnd)
        {
            _stats.MidpointTargets++;
            _targets.Add(new SnapTarget(point, SnapTargetKind.Midpoint, visibilityStart, visibilityEnd));
        }

        private double ResolveStraightLineTolerance(double length)
            => System.Math.Max(_weldTolerance * 2.0, length * StraightLineToleranceScale);

        private double ResolveArcPlaneTolerance(double length)
            => System.Math.Max(_weldTolerance * 4.0, length * ArcPlaneToleranceScale);

        private double ResolveArcSplitRadialTolerance(double radius)
            => System.Math.Max(_weldTolerance * 20.0, radius * ArcSplitRadialToleranceScale);

        private static double PathLength(Vector3d[] points)
        {
            double length = 0.0;
            for (int i = 1; i < points.Length; i++)
                length += Vector3d.Distance(points[i - 1], points[i]);
            return length;
        }

        private static double ClosedPathLength(Vector3d[] points)
        {
            double length = PathLength(points);
            if (points.Length > 1)
                length += Vector3d.Distance(points[^1], points[0]);
            return length;
        }

        private static Vector3d PointOnCircle(
            Vector3d center,
            Vector3d u,
            Vector3d v,
            double radius,
            double angle)
            => center
                + u * (System.Math.Cos(angle) * radius)
                + v * (System.Math.Sin(angle) * radius);

        private static Vector3d ClosedPathPointAtDistance(Vector3d[] points, double distance)
        {
            if (points.Length == 0)
                return default;
            if (points.Length == 1)
                return points[0];

            double totalLength = ClosedPathLength(points);
            if (!double.IsFinite(totalLength) || totalLength <= 0.0)
                return points[0];

            distance %= totalLength;
            if (distance < 0.0)
                distance += totalLength;

            double accumulated = 0.0;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3d start = points[i];
                Vector3d end = points[(i + 1) % points.Length];
                double segmentLength = Vector3d.Distance(start, end);
                if (!double.IsFinite(segmentLength) || segmentLength <= 0.0)
                    continue;

                if (accumulated + segmentLength >= distance)
                {
                    double t = (distance - accumulated) / segmentLength;
                    return Vector3d.Lerp(start, end, System.Math.Clamp(t, 0.0, 1.0));
                }

                accumulated += segmentLength;
            }

            return points[^1];
        }

        private static double DistanceSquaredToLine(Vector3d p, Vector3d linePoint, Vector3d lineDirection)
        {
            Vector3d to = p - linePoint;
            double t = Vector3d.Dot(to, lineDirection);
            Vector3d perpendicular = to - lineDirection * t;
            return perpendicular.LengthSquared;
        }

        private static void ProjectToPlane(
            Vector3d point,
            Vector3d origin,
            Vector3d u,
            Vector3d v,
            out double x,
            out double y,
            out double planeDistance)
        {
            Vector3d local = point - origin;
            x = Vector3d.Dot(local, u);
            y = Vector3d.Dot(local, v);
            Vector3d projected = u * x + v * y;
            planeDistance = (local - projected).Length;
        }

        private static bool TryFitCircleLeastSquares(
            Vector3d[] points,
            Vector3d origin,
            Vector3d u,
            Vector3d v,
            out double centerX,
            out double centerY,
            out double radius)
        {
            centerX = 0.0;
            centerY = 0.0;
            radius = 0.0;

            if (points.Length < 3)
                return false;

            double sumX = 0.0;
            double sumY = 0.0;
            double sumXX = 0.0;
            double sumYY = 0.0;
            double sumXY = 0.0;
            double sumZ = 0.0;
            double sumXZ = 0.0;
            double sumYZ = 0.0;

            for (int i = 0; i < points.Length; i++)
            {
                Vector3d local = points[i] - origin;
                double x = Vector3d.Dot(local, u);
                double y = Vector3d.Dot(local, v);
                double z = x * x + y * y;
                if (!double.IsFinite(z))
                    return false;

                sumX += x;
                sumY += y;
                sumXX += x * x;
                sumYY += y * y;
                sumXY += x * y;
                sumZ += z;
                sumXZ += x * z;
                sumYZ += y * z;
            }

            double m00 = sumXX;
            double m01 = sumXY;
            double m02 = sumX;
            double m11 = sumYY;
            double m12 = sumY;
            double m22 = points.Length;
            double b0 = -sumXZ;
            double b1 = -sumYZ;
            double b2 = -sumZ;

            double det = Determinant3(m00, m01, m02, m01, m11, m12, m02, m12, m22);
            double scale = System.Math.Max(
                System.Math.Max(System.Math.Abs(m00), System.Math.Abs(m11)),
                System.Math.Max(System.Math.Abs(m22), System.Math.Max(System.Math.Abs(m01), System.Math.Max(System.Math.Abs(m02), System.Math.Abs(m12)))));
            if (!double.IsFinite(det) || System.Math.Abs(det) <= System.Math.Max(1.0e-15, 1.0e-15 * scale * scale * scale))
                return false;

            double a = Determinant3(b0, m01, m02, b1, m11, m12, b2, m12, m22) / det;
            double b = Determinant3(m00, b0, m02, m01, b1, m12, m02, b2, m22) / det;
            double c = Determinant3(m00, m01, b0, m01, m11, b1, m02, m12, b2) / det;

            centerX = -a * 0.5;
            centerY = -b * 0.5;
            double radiusSquared = centerX * centerX + centerY * centerY - c;
            if (!double.IsFinite(radiusSquared) || radiusSquared <= 0.0)
                return false;

            radius = System.Math.Sqrt(radiusSquared);
            return double.IsFinite(centerX) && double.IsFinite(centerY) && double.IsFinite(radius);
        }

        private static double Determinant3(
            double a00,
            double a01,
            double a02,
            double a10,
            double a11,
            double a12,
            double a20,
            double a21,
            double a22)
            => a00 * (a11 * a22 - a12 * a21)
               - a01 * (a10 * a22 - a12 * a20)
               + a02 * (a10 * a21 - a11 * a20);

        private static double NormalizeAngleDelta(double angle)
        {
            while (angle <= -System.Math.PI)
                angle += 2.0 * System.Math.PI;
            while (angle > System.Math.PI)
                angle -= 2.0 * System.Math.PI;
            return angle;
        }
    }

    private sealed class VertexWelder
    {
        private readonly List<VertexInfo> _vertices;
        private readonly Dictionary<GridKey, List<int>> _cells = new();
        private readonly double _cellSize;
        private readonly double _toleranceSquared;

        public VertexWelder(List<VertexInfo> vertices, double tolerance, double toleranceSquared)
        {
            _vertices = vertices;
            _cellSize = System.Math.Max(tolerance, MinimumWeldTolerance);
            _toleranceSquared = toleranceSquared;
        }

        public int GetOrAdd(Vector3d point)
        {
            GridKey key = GridKey.From(point, _cellSize);
            int bestIndex = -1;
            double bestDistanceSquared = _toleranceSquared;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var neighbor = new GridKey(key.X + dx, key.Y + dy, key.Z + dz);
                        if (!_cells.TryGetValue(neighbor, out List<int>? candidates))
                            continue;

                        foreach (int candidate in candidates)
                        {
                            double distanceSquared = (_vertices[candidate].Point - point).LengthSquared;
                            if (distanceSquared <= bestDistanceSquared)
                            {
                                bestDistanceSquared = distanceSquared;
                                bestIndex = candidate;
                            }
                        }
                    }
                }
            }

            if (bestIndex >= 0)
                return bestIndex;

            int index = _vertices.Count;
            _vertices.Add(new VertexInfo(point));
            if (!_cells.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>(1);
                _cells.Add(key, bucket);
            }

            bucket.Add(index);
            return index;
        }
    }

    private readonly record struct GridKey(long X, long Y, long Z)
    {
        public static GridKey From(Vector3d point, double cellSize)
            => new(
                (long)System.Math.Floor(point.X / cellSize),
                (long)System.Math.Floor(point.Y / cellSize),
                (long)System.Math.Floor(point.Z / cellSize));
    }

    private sealed class VertexInfo
    {
        public VertexInfo(Vector3d point)
        {
            Point = point;
        }

        public Vector3d Point { get; }

        public List<int> Segments { get; } = new(2);
    }

    private readonly record struct SegmentInfo(int VertexA, int VertexB, double Length)
    {
        public int Other(int vertex)
        {
            if (vertex == VertexA)
                return VertexB;
            return vertex == VertexB ? VertexA : -1;
        }
    }

    private readonly record struct SegmentKey(int A, int B)
    {
        public static SegmentKey From(int a, int b)
            => a <= b ? new SegmentKey(a, b) : new SegmentKey(b, a);
    }

    private readonly record struct ChainInfo(List<int> VertexIndices, bool Closed);

    private readonly record struct LogicalRun(
        bool IsArc,
        int StartSegment,
        int SegmentCount,
        Vector3d Start,
        Vector3d End,
        ArcFitInfo ArcFit)
    {
        public static LogicalRun Line(int startSegment, int segmentCount, Vector3d start, Vector3d end)
            => new(false, startSegment, segmentCount, start, end, default);

        public static LogicalRun ArcRun(int startSegment, int segmentCount, ArcFitInfo arc)
            => new(true, startSegment, segmentCount, default, default, arc);
    }

    private sealed class SnapBuildStats
    {
        private const int MaxArcRejectSamples = 6;
        private const int MaxMixedRunSamples = 8;
        private readonly int[] _arcRejects = new int[ArcRejectReasonCount];

        public int InputSegments { get; set; }

        public int ValidSegments { get; set; }

        public int InvalidSegments { get; set; }

        public int ShortSegments { get; set; }

        public int WeldedSameVertex { get; set; }

        public int DuplicateSegments { get; set; }

        public int Chains { get; set; }

        public int OpenChains { get; set; }

        public int ClosedChains { get; set; }

        public int BranchStops { get; set; }

        public int BranchContinuations { get; set; }

        public int StraightRuns { get; set; }

        public int ArcRuns { get; set; }

        public int OpenMixedRuns { get; set; }

        public int ClosedMixedRuns { get; set; }

        public int ClosedCircleRuns { get; set; }

        public int SmoothClosedRuns { get; set; }

        public int FallbackChains { get; set; }

        public int FallbackSegments { get; set; }

        public int GeneratedLines { get; set; }

        public int GeneratedArcs { get; set; }

        public int GeneratedPrimitives => GeneratedLines + GeneratedArcs;

        public int GeneratedFallbackLines { get; set; }

        public int EndpointTargets { get; set; }

        public int MidpointTargets { get; set; }

        public int ClosedLoopMidpointTargets { get; set; }

        public int SourceSegmentsCoveredByStraightLines { get; set; }

        public int SourceSegmentsCoveredByArcs { get; set; }

        public int SourceSegmentsCoveredByFallbackLines { get; set; }

        public int CoveredSourceSegments
            => SourceSegmentsCoveredByStraightLines
               + SourceSegmentsCoveredByArcs
               + SourceSegmentsCoveredByFallbackLines;

        public int ArcCandidates { get; set; }

        public List<string> ArcRejectSamples { get; } = new(MaxArcRejectSamples);

        public List<string> MixedRunSamples { get; } = new(MaxMixedRunSamples);

        public void RecordMixedRunSample(string sample)
        {
            if (MixedRunSamples.Count < MaxMixedRunSamples)
                MixedRunSamples.Add(sample);
        }

        public void RecordArcReject(
            ArcRejectReason reason,
            int pointCount,
            double length,
            double value,
            double tolerance,
            double radius,
            double weldTolerance)
        {
            int index = (int)reason;
            if ((uint)index < _arcRejects.Length)
                _arcRejects[index]++;

            if (ArcRejectSamples.Count >= MaxArcRejectSamples)
                return;

            ArcRejectSamples.Add(
                $"reason={reason}, points={pointCount}, length={Format(length)}, value={Format(value)}, tolerance={Format(tolerance)}, radius={Format(radius)}, weldTol={Format(weldTolerance)}");
        }

        public string FormatArcRejects()
        {
            bool any = false;
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < _arcRejects.Length; i++)
            {
                int count = _arcRejects[i];
                if (count == 0)
                    continue;

                if (any)
                    result.Append(", ");
                any = true;
                result.Append((ArcRejectReason)i);
                result.Append('=');
                result.Append(count);
            }

            return any ? result.ToString() : "none";
        }

        private static string Format(double value)
            => double.IsFinite(value) ? value.ToString("0.######E+0") : "n/a";
    }

    private readonly record struct ArcFitInfo(
        Vector3d Origin,
        Vector3d U,
        Vector3d V,
        double CenterX,
        double CenterY,
        double Radius,
        double Sweep,
        double TotalLength,
        double MaxRadialError,
        double MaxPlaneDistance);

    private readonly record struct ArcRejectDetail(ArcRejectReason Reason, double Value, double Tolerance);

    private enum ArcRejectReason
    {
        TooFewPoints,
        TooShort,
        DegenerateChord,
        NoMiddlePoint,
        TooStraight,
        DegenerateNormal,
        DegenerateBasis,
        MiddleOffPlane,
        CircleFitFailed,
        BadRadius,
        PointOffPlane,
        RadialError,
        SweepDirectionChanged,
        ZeroSweep,
        SweepTooSmall,
        SweepTooLarge,
        TwoSegmentTurnTooSharp,
    }

    private const int ArcRejectReasonCount = 17;

    private struct BoundsAccumulator
    {
        private bool _any;
        private double _minX;
        private double _minY;
        private double _minZ;
        private double _maxX;
        private double _maxY;
        private double _maxZ;

        public void Include(Vector3d point)
        {
            if (!_any)
            {
                _any = true;
                _minX = _maxX = point.X;
                _minY = _maxY = point.Y;
                _minZ = _maxZ = point.Z;
                return;
            }

            _minX = System.Math.Min(_minX, point.X);
            _minY = System.Math.Min(_minY, point.Y);
            _minZ = System.Math.Min(_minZ, point.Z);
            _maxX = System.Math.Max(_maxX, point.X);
            _maxY = System.Math.Max(_maxY, point.Y);
            _maxZ = System.Math.Max(_maxZ, point.Z);
        }

        public double DiagonalOrDefault()
        {
            if (!_any)
                return 1.0;

            double dx = _maxX - _minX;
            double dy = _maxY - _minY;
            double dz = _maxZ - _minZ;
            double diagonal = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return double.IsFinite(diagonal) && diagonal > 0.0 ? diagonal : 1.0;
        }
    }

    private readonly record struct SnapTarget(
        Vector3d Point,
        SnapTargetKind Kind,
        Vector3d VisibilityStart,
        Vector3d VisibilityEnd);

    private readonly record struct SnapCandidate(
        Vector3d WorldPoint,
        double RayDepth,
        double PerpendicularDistance,
        double AngularScore,
        SnapTarget Target)
    {
        public bool IsBetterThan(SnapCandidate other)
        {
            const double TieTolerance = 1.0e-12;
            if (AngularScore + TieTolerance < other.AngularScore)
                return true;
            if (System.Math.Abs(AngularScore - other.AngularScore) <= TieTolerance)
                return RayDepth < other.RayDepth;
            return false;
        }
    }

    private struct SnapScanDiagnostics
    {
        public SnapScanDiagnostics(int targets, double endpointTolerance, double midpointTolerance)
        {
            Targets = targets;
            EndpointTolerance = endpointTolerance;
            MidpointTolerance = midpointTolerance;
            EndpointTargets = 0;
            MidpointTargets = 0;
            WithinTolerance = 0;
            VisibilityRejected = 0;
            BehindRay = 0;
            NonFiniteDistance = 0;
            ClosestEndpointAngular = double.PositiveInfinity;
            ClosestMidpointAngular = double.PositiveInfinity;
        }

        public int Targets { get; }

        public double EndpointTolerance { get; }

        public double MidpointTolerance { get; }

        public int EndpointTargets { get; private set; }

        public int MidpointTargets { get; private set; }

        public int WithinTolerance { get; set; }

        public int VisibilityRejected { get; set; }

        public int VisibilityCandidates { get; set; }

        public int BehindRay { get; set; }

        public int NonFiniteDistance { get; set; }

        public double ClosestEndpointAngular { get; private set; }

        public double ClosestMidpointAngular { get; private set; }

        public string ClosestKind
        {
            get
            {
                bool hasEndpoint = double.IsFinite(ClosestEndpointAngular);
                bool hasMidpoint = double.IsFinite(ClosestMidpointAngular);
                if (!hasEndpoint && !hasMidpoint)
                    return "none";
                if (hasEndpoint && (!hasMidpoint || ClosestEndpointAngular <= ClosestMidpointAngular))
                    return "Endpoint";
                return "Midpoint";
            }
        }

        public void CountActiveTarget(SnapTargetKind kind)
        {
            if (kind == SnapTargetKind.Endpoint)
                EndpointTargets++;
            else
                MidpointTargets++;
        }

        public void RecordClosest(SnapTargetKind kind, double angularDistance)
        {
            if (!double.IsFinite(angularDistance))
                return;

            if (kind == SnapTargetKind.Endpoint)
            {
                if (angularDistance < ClosestEndpointAngular)
                    ClosestEndpointAngular = angularDistance;
            }
            else if (angularDistance < ClosestMidpointAngular)
            {
                ClosestMidpointAngular = angularDistance;
            }
        }
    }

    private enum SnapTargetKind
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
