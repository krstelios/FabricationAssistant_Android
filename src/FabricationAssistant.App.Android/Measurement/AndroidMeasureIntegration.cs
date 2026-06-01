using Android.Util;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Measurement.Presentation;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Sections;
using FabricationAssistant.Core.UndoRedo;

namespace FabricationAssistant.App.Android.Measurement;

internal sealed class AndroidMeasureIntegration : IDisposable
{
    internal const double AndroidEdgeSnapAngularTolerance = MeshMeasurePicker.DefaultEdgeSnapAngularTolerance * 1.0;
    internal const double AndroidEndpointSnapAngularTolerance = MeshMeasurePicker.DefaultEndpointSnapAngularTolerance * 1.0;
    private const long SnapWarmupBudgetMs = 2500;

    private readonly Func<Scene?> _sceneAccessor;
    private readonly Action _invalidate;
    private readonly MeasurementStore _store = new();
    private readonly SceneUnitSystemService _units = new();
    private readonly MeasurementSession _session;
    private readonly AndroidMeasureRaycaster _raycaster;
    private readonly MeasureTool _tool;
    private readonly MeasurementPresenter _presenter;
    private readonly AndroidSnapWarmupRunner _snapWarmup = new();
    private readonly Func<EdgeSnapVisibilityRequest, bool> _snapVisibilityFilter;
    private readonly Action<string> _snapDiagnosticsLog;
    private readonly EventHandler _measurementsChangedHandler;
    private readonly EventHandler _sessionStateChangedHandler;
    private string _lastStateLogKey = "";
    private long _lastStateLogMs;
    private string _lastPresentationLogKey = "";
    private bool _multiMeasureEnabled = true;
    private bool _snapVisibleEdgesOnly = true;
    private double _snapVisibilityProbe = 0.01;
    private double _snapOcclusionToleranceFactor = 1.0;
    private bool _showDeltaBreakdown;
    private BoundingBoxMode _boundingBoxMode = BoundingBoxMode.BestFit;
    private CancellationTokenSource? _snapWarmupCts;
    private Task? _snapWarmupTask;

    public AndroidMeasureIntegration(
        Func<Scene?> sceneAccessor,
        Action invalidate,
        Func<IReadOnlyList<SectionPlane>>? sectionPlanesAccessor = null,
        IUndoService? undoService = null)
    {
        _sceneAccessor = sceneAccessor ?? throw new ArgumentNullException(nameof(sceneAccessor));
        _invalidate = invalidate ?? throw new ArgumentNullException(nameof(invalidate));

        _session = new MeasurementSession(_store, _units, MeasurementTolerances.Default, undoService);
        _raycaster = new AndroidMeasureRaycaster(sceneAccessor, sectionPlanesAccessor);
        _snapVisibilityFilter = IsSnapTargetVisible;
        _snapDiagnosticsLog = message => Log.Debug("FA.MeasureSnap", message);
        EdgeSnapService.VisibilityFilter = _snapVisibilityFilter;
        EdgeSnapService.DiagnosticsLog = _snapDiagnosticsLog;
        MeshMeasurePicker.DiagnosticsLog = _snapDiagnosticsLog;
        var picker = new MeshMeasurePicker(
            _raycaster,
            MeasurementTolerances.Default,
            AndroidEdgeSnapAngularTolerance,
            AndroidEndpointSnapAngularTolerance)
        {
            PreparedOnlySelection = true,
        };
        _tool = new MeasureTool(_session, picker);
        _presenter = new MeasurementPresenter(_units);

        _measurementsChangedHandler = (_, _) =>
        {
            LogState("store changed");
            _invalidate();
        };
        _sessionStateChangedHandler = (_, _) =>
        {
            LogState("session changed");
            _invalidate();
        };
        _store.MeasurementsChanged += _measurementsChangedHandler;
        _session.StateChanged += _sessionStateChangedHandler;
    }

    public MeasureToolMode ActiveMode => _tool.ActiveMode;

    public bool IsActive => _tool.IsActive;

    public MeasurementId? SelectedMeasurementId => _store.SelectedId;

    public MeasurementId? HoveredMeasurementId => _store.HoveredId;

    internal IMeasurementStore Store => _store;

    public void ApplySettings(
        bool multiMeasureEnabled,
        bool showDeltaBreakdown,
        BoundingBoxMode boundingBoxMode,
        bool pointSnapEnabled,
        bool endpointSnapEnabled,
        bool midpointSnapEnabled,
        double edgeSnapFactor,
        double endpointSnapFactor,
        double weldToleranceScale,
        bool snapVisibleEdgesOnly,
        double snapVisibilityProbe,
        double snapOcclusionToleranceFactor)
    {
        _multiMeasureEnabled = multiMeasureEnabled;
        _showDeltaBreakdown = showDeltaBreakdown;
        _boundingBoxMode = boundingBoxMode is BoundingBoxMode.AxisAligned or BoundingBoxMode.BestFit
            ? boundingBoxMode
            : BoundingBoxMode.BestFit;
        _snapVisibleEdgesOnly = snapVisibleEdgesOnly;
        _snapVisibilityProbe = double.IsFinite(snapVisibilityProbe)
            ? Math.Clamp(snapVisibilityProbe, 0.002, 0.08)
            : 0.01;
        _snapOcclusionToleranceFactor = double.IsFinite(snapOcclusionToleranceFactor)
            ? Math.Clamp(snapOcclusionToleranceFactor, 0.1, 10.0)
            : 1.0;
        EdgeSnapService.SnapEnabled = pointSnapEnabled;
        EdgeSnapService.EndpointSnapEnabled = endpointSnapEnabled;
        EdgeSnapService.MidpointSnapEnabled = midpointSnapEnabled;
        EdgeSnapService.EdgeSnapToleranceFactor = edgeSnapFactor;
        EdgeSnapService.EndpointSnapToleranceFactor = endpointSnapFactor;
        EdgeSnapService.WeldToleranceScale = weldToleranceScale;
    }

    public void ApplySectionVisibility(bool sectionCurvesVisible, bool sectionCapsVisible)
    {
        _raycaster.SectionCurveSnapEnabled = sectionCurvesVisible;
        _raycaster.SectionCapRaycastEnabled = sectionCapsVisible;
    }

    public void ClearRaycastAccelerationCache(string reason)
        => _raycaster.ClearAccelerationCache(reason);

    private void BeginSnapWarmup(string reason)
    {
        if (!EdgeSnapService.SnapEnabled)
            return;

        Scene? scene = _sceneAccessor();
        if (scene is null)
            return;

        if (_snapWarmupTask is { IsCompleted: false } && !string.Equals(reason, "scene attached", StringComparison.Ordinal))
        {
            Log.Debug("FA.MeasureSnap", $"Warmup already running; skipped request: reason={reason}.");
            return;
        }

        var snapshots = new List<AndroidSnapWarmupMesh>();
        var seenMeshIds = new HashSet<int>();
        foreach (SceneNode node in scene.GetVisibleNodes())
        {
            if (node.MeshId is not int meshId || !seenMeshIds.Add(meshId))
                continue;

            MeshDto? mesh = scene.GetMesh(meshId);
            if (mesh is null)
                continue;

            snapshots.Add(new AndroidSnapWarmupMesh(mesh.EdgePositions, mesh.Positions, mesh.Indices));
        }

        if (snapshots.Count == 0)
            return;

        _snapWarmupCts?.Cancel();
        var cts = new CancellationTokenSource();
        _snapWarmupCts = cts;
        _snapWarmupTask = Task.Run(() => WarmSnapModels(snapshots, reason, cts.Token), cts.Token);
    }

    private void WarmSnapModels(IReadOnlyList<AndroidSnapWarmupMesh> snapshots, string reason, CancellationToken token)
    {
        long start = Environment.TickCount64;
        try
        {
            AndroidSnapWarmupResult result = _snapWarmup.WarmSnapModels(snapshots, SnapWarmupBudgetMs, token);
            if (result.BudgetReached)
            {
                Log.Debug(
                    "FA.MeasureSnap",
                    $"Warmup budget reached: reason={reason}, meshes={result.WarmedMeshes}/{snapshots.Count}, segments={result.SegmentCount}, elapsedMs={Environment.TickCount64 - start}.");
                return;
            }

            Log.Debug(
                "FA.MeasureSnap",
                $"Warmup complete: reason={reason}, meshes={result.WarmedMeshes}, segments={result.SegmentCount}, elapsedMs={Environment.TickCount64 - start}.");
        }
        catch (OperationCanceledException)
        {
            Log.Debug("FA.MeasureSnap", $"Warmup canceled: reason={reason}, elapsedMs={Environment.TickCount64 - start}.");
        }
        catch (Exception ex)
        {
            Log.Warn("FA.MeasureSnap", $"Warmup failed: reason={reason}, error={ex.Message}");
        }
    }

    public void Dispose()
    {
        _snapWarmupCts?.Cancel();
        _snapWarmupCts?.Dispose();

        if (ReferenceEquals(EdgeSnapService.VisibilityFilter, _snapVisibilityFilter))
            EdgeSnapService.VisibilityFilter = null;
        if (ReferenceEquals(EdgeSnapService.DiagnosticsLog, _snapDiagnosticsLog))
            EdgeSnapService.DiagnosticsLog = null;
        if (ReferenceEquals(MeshMeasurePicker.DiagnosticsLog, _snapDiagnosticsLog))
            MeshMeasurePicker.DiagnosticsLog = null;

        _store.MeasurementsChanged -= _measurementsChangedHandler;
        _session.StateChanged -= _sessionStateChangedHandler;
    }

    public void OnSceneAttached(Scene? scene)
    {
        Log.Info(
            "FA.Measure",
            scene is null
                ? "Scene attached: none; measurement disabled until a node-backed scene is loaded."
                : $"Scene attached: nodes={scene.NodesById.Count}, visibleMeshNodes={scene.GetVisibleNodes().Count}, meshes={scene.MeshesById.Count}, units='{scene.Stats?.UnitSystem ?? "<null>"}', diagonal={(scene.Bounds.IsValid ? scene.Bounds.Diagonal : 0.0):0.###}.");
        _units.SetUnitLabel(scene?.Stats?.UnitSystem);
        _store.Clear();
        _session.SelectTool(MeasureToolMode.None);
        _raycaster.ResetDiagnostics();
        BeginSnapWarmup("scene attached");
        LogState("scene attached", force: true);
    }

    public void SetMode(MeasureToolMode mode)
    {
        MeasureToolMode previous = _tool.ActiveMode;
        _tool.SetMode(mode);
        Log.Info("FA.Measure", $"Mode changed: {previous} -> {_tool.ActiveMode}.");
        if (_tool.ActiveMode is MeasureToolMode.PointToPoint or MeasureToolMode.FaceToPoint)
            BeginSnapWarmup("measure mode");
        LogState("mode changed", force: true);
    }

    public void Cancel()
    {
        bool hadState = HasActiveMeasureState();
        if (hadState)
            Log.Info("FA.Measure", $"Cancel requested: active={_tool.IsActive}, mode={_tool.ActiveMode}, draft={DraftName()}.");

        _tool.HandleCancel();
        _tool.SetMode(MeasureToolMode.None);
        _store.Hover(null);
        if (hadState)
            LogState("cancel", force: true);
    }

    public void CancelCurrentDraft()
    {
        Log.Info("FA.Measure", $"Current draft cancel requested: active={_tool.IsActive}, mode={_tool.ActiveMode}, draft={DraftName()}.");
        _tool.HandleCancel();
        _store.Hover(null);
        LogState("cancel draft", force: true);
    }

    public void ClearMeasurements()
    {
        int before = _store.Snapshot().Count;
        _store.Clear();
        _session.CancelCurrent();
        Log.Info("FA.Measure", $"Clear measurements requested: before={before}, after={_store.Snapshot().Count}.");
        LogState("clear", force: true);
    }

    public bool SelectMeasurement(MeasurementId id)
    {
        if (!_store.Snapshot().Any(measurement => measurement.Id.Equals(id)))
        {
            Log.Info("FA.Measure", $"Measurement select ignored: id={id} not found.");
            return false;
        }

        _store.Select(id);
        Log.Info("FA.Measure", $"Measurement selected: id={id}.");
        LogState("select measurement", force: true);
        return true;
    }

    public void ClearMeasurementSelection()
    {
        if (_store.SelectedId is null && _store.HoveredId is null)
            return;

        _store.Select(null);
        _store.Hover(null);
        Log.Info("FA.Measure", "Measurement selection cleared.");
        LogState("clear measurement selection", force: true);
    }

    public bool HoverMeasurement(MeasurementId? id)
    {
        if (_store.HoveredId == id)
            return false;

        if (id is { } measurementId
            && !_store.Snapshot().Any(measurement => measurement.Id.Equals(measurementId)))
        {
            id = null;
        }

        _store.Hover(id);
        LogState(id is null ? "clear measurement hover" : "hover measurement", force: true);
        return true;
    }

    public bool RemoveMeasurement(MeasurementId id)
    {
        int before = _store.Snapshot().Count;
        _store.Remove(id);
        int after = _store.Snapshot().Count;
        bool removed = after < before;
        Log.Info("FA.Measure", $"Measurement delete requested: id={id}, removed={removed}, before={before}, after={after}.");
        LogState("delete measurement", force: true);
        return removed;
    }

    public bool HandleClick(Vector3d rayOrigin, Vector3d rayDirection, int? preferredNodeId = null)
    {
        if (!_tool.IsActive || _tool.ActiveMode == MeasureToolMode.BoundingBox)
        {
            Log.Info("FA.Measure", $"Click ignored: active={_tool.IsActive}, mode={_tool.ActiveMode}.");
            return false;
        }

        try
        {
            int beforeCount = _store.Snapshot().Count;
            string beforeDraft = DraftName();
            Log.Info(
                "FA.Measure",
                $"Click begin: mode={_tool.ActiveMode}, draftBefore={beforeDraft}, measurementsBefore={beforeCount}, preferredNode={preferredNodeId?.ToString() ?? "<none>"}, origin={Format(rayOrigin)}, dir={Format(rayDirection)}.");

            if (preferredNodeId is int nodeId && nodeId > 0)
                _raycaster.SetPreferredNode(nodeId);

            bool handled;
            try
            {
                handled = _tool.HandleClick(rayOrigin, rayDirection);
            }
            finally
            {
                _raycaster.ClearPreferredNode();
            }

            int afterCount = _store.Snapshot().Count;
            EnforceSingleMeasurementIfNeeded(beforeCount, afterCount, "click");
            afterCount = _store.Snapshot().Count;
            Log.Info(
                "FA.Measure",
                $"Click end: handled={handled}, mode={_tool.ActiveMode}, draftAfter={DraftName()}, measurementsAfter={afterCount}, delta={afterCount - beforeCount}, selectedFaces={_session.SelectedFaces.Count}.");
            LogState("click", force: true);
            return handled;
        }
        catch (UnitSystemUnavailableException ex)
        {
            Log.Warn("FA.Measure", "Measurement ignored because scene units are unavailable: " + ex.Message);
            return true;
        }
    }

    public void HandleHover(Vector3d rayOrigin, Vector3d rayDirection)
    {
        if (!_tool.IsActive)
        {
            if (_session.HoverPoint is null && _session.HoverFace is null)
                return;

            _session.SetHoverPoint(null);
            _session.SetHoverFace(null);
            LogState("hover inactive");
            return;
        }

        _tool.HandleHover(rayOrigin, rayDirection);
        LogState("hover");
    }

    public void ClearHover()
    {
        if (_session.HoverPoint is null && _session.HoverFace is null)
            return;

        _session.SetHoverPoint(null);
        _session.SetHoverFace(null);
        LogState("clear hover");
    }

    public async Task<bool> TryCommitBoundingBoxFromSelectionAsync(IReadOnlyList<int> selectedNodeIds)
    {
        MeasurementId? id = await TryCommitBoundingBoxFromSelectionWithIdAsync(selectedNodeIds).ConfigureAwait(true);
        return id is not null;
    }

    public async Task<MeasurementId?> TryCommitBoundingBoxFromSelectionWithIdAsync(IReadOnlyList<int> selectedNodeIds)
    {
        Scene? scene = _sceneAccessor();
        if (scene is null || selectedNodeIds.Count == 0)
        {
            Log.Info("FA.Measure", $"BBox skipped: scene={(scene is null ? "null" : "ok")}, selectedNodes={selectedNodeIds.Count}.");
            return null;
        }
        Scene sceneAtStart = scene;

        Log.Info("FA.Measure", $"BBox begin: mode={_boundingBoxMode}, selectedNodes=[{string.Join(",", selectedNodeIds)}].");
        var meshSnapshots = new List<MeshSnapshot>(selectedNodeIds.Count * 2);
        foreach (int id in selectedNodeIds)
        {
            SceneNode? node = scene.GetNode(id);
            if (node is null)
                continue;
            CollectMeshSnapshots(scene, node, meshSnapshots);
        }

        if (meshSnapshots.Count == 0)
        {
            Log.Info("FA.Measure", "BBox skipped: selected nodes contained no mesh snapshots.");
            return null;
        }

        BoundingBoxMode mode = _boundingBoxMode;
        List<Vector3d> points = await Task.Run(() =>
        {
            // S17#1: never subsample for extents - dropping vertices silently
            // under-reports the box dimensions (a wrong number in a quantitative
            // tool). AxisAligned only needs the exact world AABB, so compute a
            // running min/max over ALL transformed vertices (O(1) memory) and
            // feed its 8 corners. BestFit's oriented-box solver re-measures
            // extents along its chosen basis, so it needs the full transformed
            // vertex cloud to be exact.
            if (mode == BoundingBoxMode.AxisAligned)
            {
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
                bool any = false;
                foreach (MeshSnapshot snap in meshSnapshots)
                {
                    float[] positions = snap.Positions;
                    int count = Math.Min(snap.VertexCount, positions.Length / 3);
                    for (int i = 0; i < count; i++)
                    {
                        int o = i * 3;
                        Vector3d p = snap.Transform.TransformPoint(new Vector3d(
                            positions[o], positions[o + 1], positions[o + 2]));
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                        if (p.Z < minZ) minZ = p.Z;
                        if (p.Z > maxZ) maxZ = p.Z;
                        any = true;
                    }
                }

                if (!any)
                    return new List<Vector3d>();

                return new List<Vector3d>(8)
                {
                    new(minX, minY, minZ), new(maxX, minY, minZ),
                    new(minX, maxY, minZ), new(maxX, maxY, minZ),
                    new(minX, minY, maxZ), new(maxX, minY, maxZ),
                    new(minX, maxY, maxZ), new(maxX, maxY, maxZ),
                };
            }

            long total = 0;
            foreach (MeshSnapshot snap in meshSnapshots)
                total += Math.Min(snap.VertexCount, snap.Positions.Length / 3);
            var pts = new List<Vector3d>((int)Math.Min(total, 1_000_000));
            foreach (MeshSnapshot snap in meshSnapshots)
            {
                float[] positions = snap.Positions;
                int count = Math.Min(snap.VertexCount, positions.Length / 3);
                for (int i = 0; i < count; i++)
                {
                    int o = i * 3;
                    pts.Add(snap.Transform.TransformPoint(new Vector3d(
                        positions[o], positions[o + 1], positions[o + 2])));
                }
            }

            return pts;
        }).ConfigureAwait(true);

        if (!ReferenceEquals(_sceneAccessor(), sceneAtStart))
        {
            Log.Warn("FA.Measure", "BBox skipped: scene changed while bounding box points were being sampled.");
            return null;
        }

        if (points.Count == 0)
        {
            Log.Info("FA.Measure", $"BBox skipped: meshSnapshots={meshSnapshots.Count}, sampledPoints=0.");
            return null;
        }

        try
        {
            int beforeCount = _store.Snapshot().Count;
            // Run the (potentially multi-second) BestFit oriented-box solver off
            // the UI thread so the viewport stays responsive and the measurement
            // busy chip can animate; only the cheap store commit runs on the UI thread.
            BoundingBoxMeasurement? boundingBox =
                await Task.Run(() => _tool.ComputeBoundingBox(points, mode)).ConfigureAwait(true);

            if (!ReferenceEquals(_sceneAccessor(), sceneAtStart))
            {
                Log.Warn("FA.Measure", "BBox skipped: scene changed while the bounding box was being solved.");
                return null;
            }

            _tool.CommitComputedBoundingBox(boundingBox);
            int afterCount = _store.Snapshot().Count;
            EnforceSingleMeasurementIfNeeded(beforeCount, afterCount, "bbox");
            IReadOnlyList<MeasurementResult> afterSnapshot = _store.Snapshot();
            afterCount = afterSnapshot.Count;
            MeasurementId? committedId = afterCount > 0
                ? afterSnapshot[^1].Id
                : null;
            Log.Info(
                "FA.Measure",
                $"BBox committed: mode={mode}, meshSnapshots={meshSnapshots.Count}, sampledPoints={points.Count}, measurementsBefore={beforeCount}, measurementsAfter={afterCount}, id={committedId?.ToString() ?? "<none>"}.");
            LogState("bbox", force: true);
            return committedId;
        }
        catch (UnitSystemUnavailableException ex)
        {
            Log.Warn("FA.Measure", "Bounding box ignored because scene units are unavailable: " + ex.Message);
            // The solve threw before anything was committed; reset transient state
            // and notify, matching the recovery the single-threaded CommitBoundingBox
            // performed internally before this split.
            _tool.CommitComputedBoundingBox(null);
            return null;
        }
    }

    public IReadOnlyList<PresentationSnapshot> BuildPresentation()
    {
        IReadOnlyList<PresentationSnapshot> snapshots = _presenter.Build(
            _store.Snapshot(),
            _store.SelectedId,
            _session.CurrentDraft,
            _session.ActiveTool,
            _session.HoverPoint,
            _store.HoveredId,
            _showDeltaBreakdown);
        LogPresentation(snapshots);
        return snapshots;
    }

    public IReadOnlyList<FaceHighlight> BuildFaceHighlights()
    {
        IReadOnlyList<FaceHighlight> selected = _session.SelectedFaces;
        FaceHighlight? hover = _session.HoverFace;
        if (selected.Count == 0 && hover is null)
            return Array.Empty<FaceHighlight>();

        var result = new List<FaceHighlight>(selected.Count + (hover is null ? 0 : 1));
        result.AddRange(selected);
        if (hover is not null)
            result.Add(hover);
        return result;
    }

    private void LogState(string reason, bool force = false)
    {
        int measurementCount = _store.Snapshot().Count;
        string key = $"{_tool.ActiveMode}|{measurementCount}|{DraftName()}|{(_session.HoverPoint is not null ? 1 : 0)}|{(_session.HoverFace is not null ? 1 : 0)}|{_session.SelectedFaces.Count}";
        long now = Environment.TickCount64;
        if (!force && key == _lastStateLogKey && now - _lastStateLogMs < 1000)
            return;

        _lastStateLogKey = key;
        _lastStateLogMs = now;
        Log.Info(
            "FA.Measure",
            $"State: reason={reason}, mode={_tool.ActiveMode}, active={_tool.IsActive}, measurements={measurementCount}, draft={DraftName()}, hoverPoint={(_session.HoverPoint is not null)}, hoverFace={(_session.HoverFace is not null)}, selectedFaces={_session.SelectedFaces.Count}.");
    }

    private void EnforceSingleMeasurementIfNeeded(int beforeCount, int afterCount, string reason)
    {
        if (_multiMeasureEnabled || afterCount <= beforeCount)
            return;

        IReadOnlyList<MeasurementResult> snapshot = _store.Snapshot();
        if (snapshot.Count <= 1)
            return;

        MeasurementId keep = snapshot[^1].Id;
        foreach (MeasurementResult measurement in snapshot)
        {
            if (!measurement.Id.Equals(keep))
                _store.Remove(measurement.Id);
        }

        Log.Info("FA.Measure", $"Single-measure mode kept latest result after {reason}: kept={keep}, removed={snapshot.Count - 1}.");
    }

    private void LogPresentation(IReadOnlyList<PresentationSnapshot> snapshots)
    {
        int balls = 0;
        int disks = 0;
        int lines = 0;
        int labels = 0;
        foreach (PresentationSnapshot snapshot in snapshots)
        {
            balls += snapshot.Balls.Count;
            disks += snapshot.Disks.Count;
            lines += snapshot.Lines.Count;
            labels += snapshot.Labels.Count;
        }

        string key = $"{snapshots.Count}|{balls}|{disks}|{lines}|{labels}|{_tool.ActiveMode}|{DraftName()}|{_store.Snapshot().Count}";
        if (key == _lastPresentationLogKey)
            return;

        _lastPresentationLogKey = key;
        Log.Info(
            "FA.Measure",
            $"Presentation: snapshots={snapshots.Count}, balls={balls}, disks={disks}, lines={lines}, labels={labels}, mode={_tool.ActiveMode}, draft={DraftName()}, measurements={_store.Snapshot().Count}.");
    }

    private string DraftName() => _session.CurrentDraft?.GetType().Name ?? "<none>";

    private bool HasActiveMeasureState()
        => _tool.IsActive
           || _tool.ActiveMode != MeasureToolMode.None
           || _session.CurrentDraft is not null
           || _session.HoverPoint is not null
           || _session.HoverFace is not null
           || _store.HoveredId is not null;

    private bool IsSnapTargetVisible(EdgeSnapVisibilityRequest request)
    {
        if (!_snapVisibleEdgesOnly)
            return true;

        if (!IsFinite(request.WorldPoint)
            || !IsFinite(request.EdgeStart)
            || !IsFinite(request.EdgeEnd)
            || !_raycaster.IsWorldPointVisible(request.WorldPoint))
        {
            return false;
        }

        return IsEdgeVisibleNearSnapTarget(request);
    }

    private bool IsEdgeVisibleNearSnapTarget(EdgeSnapVisibilityRequest request)
    {
        Vector3d edge = request.EdgeEnd - request.EdgeStart;
        double length = edge.Length;
        if (!double.IsFinite(length) || length <= 1e-9)
            return IsWorldSampleVisibleFromCamera(request, request.WorldPoint);

        Vector3d direction = edge / length;
        double t = Vector3d.Dot(request.WorldPoint - request.EdgeStart, direction) / length;
        t = Math.Clamp(t, 0.0, 1.0);
        double sampleStep = _snapVisibilityProbe;
        double sampleT;
        if (t <= sampleStep)
        {
            sampleT = Math.Min(1.0, sampleStep);
        }
        else if (t >= 1.0 - sampleStep)
        {
            sampleT = Math.Max(0.0, 1.0 - sampleStep);
        }
        else
        {
            sampleT = t;
        }

        Vector3d sample = request.EdgeStart + edge * sampleT;
        return IsWorldSampleVisibleFromCamera(request, sample);
    }

    private bool IsWorldSampleVisibleFromCamera(EdgeSnapVisibilityRequest request, Vector3d sample)
    {
        if (!IsFinite(sample) || !_raycaster.IsWorldPointVisible(sample))
            return false;

        Vector3d rayOrigin = request.RayOrigin;
        Vector3d toTarget = sample - rayOrigin;
        double targetDistance = toTarget.Length;
        if (!double.IsFinite(targetDistance) || targetDistance <= 1e-9)
            return false;

        MeasureRaycastHit? hit = _raycaster.RaycastForVisibility(rayOrigin, toTarget / targetDistance);
        if (hit is null)
            return true;

        double hitDistance = Vector3d.Distance(rayOrigin, hit.Value.WorldPoint);
        double occlusionTolerance = ResolveSnapVisibilityTolerance(targetDistance);
        if (Vector3d.Distance(hit.Value.WorldPoint, sample) <= ResolveSnapSurfaceCoincidenceTolerance(targetDistance))
            return true;

        return hitDistance + occlusionTolerance >= targetDistance;
    }

    private double ResolveSnapVisibilityTolerance(double targetDistance)
    {
        double referenceLength = ResolveSnapVisibilityReferenceLength(targetDistance);
        return Math.Max(1.0e-6, referenceLength * 5.0e-5 * _snapOcclusionToleranceFactor);
    }

    private double ResolveSnapSurfaceCoincidenceTolerance(double targetDistance)
    {
        double referenceLength = ResolveSnapVisibilityReferenceLength(targetDistance);
        return Math.Max(
            ResolveSnapVisibilityTolerance(targetDistance) * 4.0,
            referenceLength * 1.0e-4 * _snapOcclusionToleranceFactor);
    }

    private double ResolveSnapVisibilityReferenceLength(double targetDistance)
    {
        double sceneDiagonal = _raycaster.SceneDiagonal;
        return Math.Max(
            double.IsFinite(sceneDiagonal) && sceneDiagonal > 0.0 ? sceneDiagonal : 1.0,
            targetDistance);
    }

    private static bool IsFinite(Vector3d value)
        => double.IsFinite(value.X)
           && double.IsFinite(value.Y)
           && double.IsFinite(value.Z);

    private static string Format(Vector3d value)
        => $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";

    private readonly record struct MeshSnapshot(Matrix4d Transform, float[] Positions, int VertexCount);

    private static void CollectMeshSnapshots(Scene scene, SceneNode node, List<MeshSnapshot> sink)
    {
        if (node.MeshId is int meshId)
        {
            MeshDto? mesh = scene.GetMesh(meshId);
            if (mesh is not null && mesh.VertexCount > 0)
                sink.Add(new MeshSnapshot(node.EffectiveWorldTransform, mesh.Positions, mesh.VertexCount));
        }

        foreach (SceneNode child in node.Children)
            CollectMeshSnapshots(scene, child, sink);
    }
}
