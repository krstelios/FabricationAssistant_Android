using System.Numerics;
using FabricationAssistant.App.Android.Measurement;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Sections;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

[Collection("EdgeSnapState")]
public sealed class AndroidSnapPreparedOnlyTests
{
    private const double EdgeTol = 0.022;
    private const double EndpointTol = 0.0085;

    public AndroidSnapPreparedOnlyTests()
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

    [Fact]
    public void PreparedOnlyPicker_HoverAndPickCanUseSectionCurveSnap()
    {
        Scene scene = CreateCubeScene();
        SectionPlane plane = CreateZSectionPlane();
        var raycaster = new AndroidMeasureRaycaster(() => scene, () => new[] { plane });
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };
        var origin = new Vector3d(0.0, -0.98, 3.0);
        var dir = new Vector3d(0.0, 0.0, -1.0);

        Vector3d? hover = picker.TryHoverSnap(origin, dir);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, origin, dir));

        Assert.NotNull(hover);
        Assert.InRange(hover!.Value.Y, -1.0001, -0.9999);
        Assert.InRange(hover.Value.Z, -1.0e-5, 1.0e-5);

        PickResult.PickedPoint picked = Assert.IsType<PickResult.PickedPoint>(pick);
        Assert.Equal(hover.Value.X, picked.Point.World.X, 9);
        Assert.Equal(hover.Value.Y, picked.Point.World.Y, 9);
        Assert.Equal(hover.Value.Z, picked.Point.World.Z, 9);
    }

    [Fact]
    public void Warmup_PreparesLargeMeshesInsteadOfLeavingThemPermanentlyUnsnappable()
    {
        float[] edges = WeldedRuns(10_001);
        var runner = new AndroidSnapWarmupRunner();

        AndroidSnapWarmupResult result = runner.WarmSnapModels(
            [new AndroidSnapWarmupMesh(edges, Array.Empty<float>(), Array.Empty<int>())],
            budgetMs: 0,
            CancellationToken.None);

        EdgeSnapResult? snap = new EdgeSnapService().TrySnapPrepared(
            edges,
            Matrix4d.Identity,
            new Vector3d(5, 0, 1000),
            new Vector3d(0, 0, -1),
            EdgeTol,
            EndpointTol);

        Assert.Equal(1, result.WarmedMeshes);
        Assert.Equal(10_001, result.SegmentCount);
        Assert.True(result.BudgetReached);
        Assert.NotNull(snap);
    }

    [Fact]
    public void Warmup_ContinuesAcrossBudgetSlicesUntilTrailingMeshesArePrepared()
    {
        float[] firstEdges = WeldedRuns(32);
        float[] secondEdges = WeldedRuns(48);
        float[] trailingEdges = WeldedRuns(64);
        var runner = new AndroidSnapWarmupRunner();

        AndroidSnapWarmupResult result = runner.WarmSnapModels(
            [
                new AndroidSnapWarmupMesh(firstEdges, Array.Empty<float>(), Array.Empty<int>()),
                new AndroidSnapWarmupMesh(secondEdges, Array.Empty<float>(), Array.Empty<int>()),
                new AndroidSnapWarmupMesh(trailingEdges, Array.Empty<float>(), Array.Empty<int>()),
            ],
            budgetMs: 0,
            CancellationToken.None);

        EdgeSnapResult? snap = new EdgeSnapService().TrySnapPrepared(
            trailingEdges,
            Matrix4d.Identity,
            new Vector3d(5, 10, 1000),
            new Vector3d(0, 0, -1),
            EdgeTol,
            EndpointTol);

        Assert.Equal(3, result.WarmedMeshes);
        Assert.Equal(32 + 48 + 64, result.SegmentCount);
        Assert.True(result.BudgetReached);
        Assert.NotNull(snap);
    }

    private static SectionPlane CreateZSectionPlane()
        => new(Guid.NewGuid(), Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    private static Scene CreateCubeScene()
    {
        MeshDto mesh = CreateCube();
        return Scene.FromDocument(new DocumentDto
        {
            Nodes =
            [
                new SceneNodeDto
                {
                    Id = 1,
                    ParentId = -1,
                    DisplayName = "Cube",
                    NodeType = SceneNodeType.Part,
                    MeshId = mesh.MeshId,
                    Visible = true,
                },
            ],
            Meshes = [mesh],
            Bounds = mesh.Bounds,
        });
    }

    private static MeshDto CreateCube()
    {
        float[] positions =
        [
            -1f, -1f, -1f,
             1f, -1f, -1f,
             1f,  1f, -1f,
            -1f,  1f, -1f,
            -1f, -1f,  1f,
             1f, -1f,  1f,
             1f,  1f,  1f,
            -1f,  1f,  1f,
        ];
        int[] indices =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7,
        ];
        BoundingBox bounds = BoundingBox.Empty;
        for (int i = 0; i + 2 < positions.Length; i += 3)
            bounds.Expand(new Vector3d(positions[i], positions[i + 1], positions[i + 2]));

        return new MeshDto
        {
            MeshId = 10,
            Positions = positions,
            Indices = indices,
            Bounds = bounds,
            TriangleCount = indices.Length / 3,
        };
    }

    private static float[] WeldedRuns(int segments)
    {
        var v = new float[segments * 6];
        const int perRun = 10;
        for (int s = 0; s < segments; s++)
        {
            int i = s % perRun;
            float y = (s / perRun) * 5.0f;
            int o = s * 6;
            v[o] = i;
            v[o + 1] = y;
            v[o + 2] = 0;
            v[o + 3] = i + 1;
            v[o + 4] = y;
            v[o + 5] = 0;
        }

        return v;
    }
}
