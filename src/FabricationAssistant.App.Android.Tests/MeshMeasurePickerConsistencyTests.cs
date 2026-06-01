using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class MeshMeasurePickerConsistencyTests
{
    private const double EdgeTol = 0.022;
    private const double EndpointTol = 0.0085;

    public MeshMeasurePickerConsistencyTests()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;
    }

    // A straight edge from (0,0,0) to (1,0,0); ray points down at its midpoint.
    private static float[] StraightEdge() => new float[] { 0, 0, 0, 1, 0, 0 };
    private static readonly Vector3d MidRayOrigin = new(0.5, 0, 1);
    private static readonly Vector3d DownRay = new(0, 0, -1);

    private static MeshDto MeshWith(float[] edgePositions) => new()
    {
        MeshId = 1,
        Positions = Array.Empty<float>(),
        Indices = Array.Empty<int>(),
        EdgePositions = edgePositions,
        Bounds = BoundingBox.Empty,
        TriangleCount = 0,
    };

    [Fact]
    public void PreparedOnly_WhenNotWarmed_HoverAndPickBothMiss()
    {
        float[] edges = StraightEdge();
        var mesh = MeshWith(edges);
        var hit = new MeasureRaycastHit(NodeId: 1, MeshId: 1, TriangleIndexOffset: 0, WorldPoint: new Vector3d(0.5, 0, 0));
        var raycaster = new FakeMeasureRaycaster(mesh, hit);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.Null(hover);                       // no red marker
        Assert.IsType<PickResult.Miss>(pick);     // ...and no pick
    }

    [Fact]
    public void PreparedOnly_WhenWarmed_HoverAndPickReturnSamePoint()
    {
        float[] edges = StraightEdge();
        new EdgeSnapService().Prepare(edges);     // warm the static model cache for THIS array
        var mesh = MeshWith(edges);
        var hit = new MeasureRaycastHit(1, 1, 0, new Vector3d(0.5, 0, 0));
        var raycaster = new FakeMeasureRaycaster(mesh, hit);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.NotNull(hover);
        var picked = Assert.IsType<PickResult.PickedPoint>(pick);
        Assert.Equal(hover!.Value.X, picked.Point.World.X, 9);
        Assert.Equal(hover!.Value.Y, picked.Point.World.Y, 9);
        Assert.Equal(hover!.Value.Z, picked.Point.World.Z, 9);
    }

    [Fact]
    public void PreparedOnly_SupplementalSnap_IsPreviewedAndCommittedIdentically()
    {
        // Mesh has no edges -> primary snap misses -> supplemental provides the point.
        var mesh = MeshWith(Array.Empty<float>());
        var hit = new MeasureRaycastHit(1, 1, 0, new Vector3d(2, 2, 0));
        var supplemental = new Vector3d(3, 3, 3);
        var raycaster = new FakeMeasureRaycaster(mesh, hit, supplementalPoint: supplemental);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.Equal(supplemental, hover);
        var picked = Assert.IsType<PickResult.PickedPoint>(pick);
        Assert.Equal(supplemental, picked.Point.World);
    }

    [Fact]
    public void DesktopMode_PickStillBuildsOnDemand_WhileHoverStaysPreparedOnly()
    {
        // PreparedOnlySelection = false (desktop default): pick may build (allowBuild
        // true) and snap even when not warmed; hover stays prepared-only. This locks in
        // that the desktop behaviour is unchanged by the flag.
        float[] edges = StraightEdge();
        var mesh = MeshWith(edges);
        var hit = new MeasureRaycastHit(1, 1, 0, new Vector3d(0.5, 0, 0));
        var raycaster = new FakeMeasureRaycaster(mesh, hit);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol);
        // PreparedOnlySelection defaults to false.

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);   // not warmed -> null
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.Null(hover);
        Assert.IsType<PickResult.PickedPoint>(pick);   // built synchronously
    }
}
