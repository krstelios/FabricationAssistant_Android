using FabricationAssistant.App.Android.Tools;
using FabricationAssistant.Core.BodyMove;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidViewportExplodeViewTests
{
    [Fact]
    public void ApplyScalesOffsetsByMainAndAxisAmounts()
    {
        Scene scene = CreateSceneWithNodes(1, 2, 3);
        var layout = new AndroidViewportExplodeLayout(new[]
        {
            new AndroidViewportExplodeUnit(1, Matrix4d.Identity, new Vector3d(10.0, 0.0, 0.0)),
            new AndroidViewportExplodeUnit(2, Matrix4d.Identity, new Vector3d(0.0, 8.0, 0.0)),
            new AndroidViewportExplodeUnit(3, Matrix4d.Identity, new Vector3d(0.0, 0.0, -3.0)),
        });

        AndroidViewportExplodeView.Apply(scene, layout, amount: 0.5, xAmount: 1.2, yAmount: 0.25, zAmount: 2.0);

        Assert.Equal(6.0, scene.GetNode(1)!.TransientTransform.M14, precision: 6);
        Assert.Equal(1.0, scene.GetNode(2)!.TransientTransform.M24, precision: 6);
        Assert.Equal(-3.0, scene.GetNode(3)!.TransientTransform.M34, precision: 6);
    }

    [Fact]
    public void ApplyClampsAxisAmountsToSupportedRange()
    {
        Scene scene = CreateSceneWithNodes(1, 2, 3);
        var layout = new AndroidViewportExplodeLayout(new[]
        {
            new AndroidViewportExplodeUnit(1, Matrix4d.Identity, new Vector3d(10.0, 0.0, 0.0)),
            new AndroidViewportExplodeUnit(2, Matrix4d.Identity, new Vector3d(0.0, 10.0, 0.0)),
            new AndroidViewportExplodeUnit(3, Matrix4d.Identity, new Vector3d(0.0, 0.0, 10.0)),
        });

        AndroidViewportExplodeView.Apply(scene, layout, amount: 1.0, xAmount: -1.0, yAmount: 6.0, zAmount: double.NaN);

        Assert.Equal(0.0, scene.GetNode(1)!.TransientTransform.M14, precision: 6);
        Assert.Equal(50.0, scene.GetNode(2)!.TransientTransform.M24, precision: 6);
        Assert.Equal(10.0, scene.GetNode(3)!.TransientTransform.M34, precision: 6);
    }

    [Fact]
    public void BuildCommittedMoveSnapshotsLayersExplodeOffsetOnExistingMoveTransform()
    {
        Scene scene = CreateSceneWithNodes(1, 2);
        scene.GetNode(1)!.MoveTransform = Matrix4d.CreateTranslation(2.0, 0.0, 0.0);
        var layout = new AndroidViewportExplodeLayout(new[]
        {
            new AndroidViewportExplodeUnit(1, Matrix4d.Identity, new Vector3d(10.0, 0.0, 0.0)),
            new AndroidViewportExplodeUnit(2, Matrix4d.Identity, Vector3d.Zero),
        });

        var snapshots = AndroidViewportExplodeView.BuildCommittedMoveSnapshots(
            scene,
            layout,
            amount: 0.5,
            xAmount: 2.0,
            yAmount: 1.0,
            zAmount: 1.0);

        BodyMoveSnapshot snapshot = Assert.Single(snapshots);
        Assert.Equal(1, snapshot.NodeId);
        Assert.Equal(Matrix4d.CreateTranslation(12.0, 0.0, 0.0), snapshot.MoveTransform);
    }

    [Fact]
    public void BuildUsesOnlyVisibleBodiesForCandidatesAndCenter()
    {
        Scene scene = CreateSceneWithMeshBounds(new[]
        {
            new ExplodeNodeSpec(1, new BoundingBox(new Vector3d(-11.0, -0.5, -0.5), new Vector3d(-9.0, 0.5, 0.5)), Visible: true),
            new ExplodeNodeSpec(2, new BoundingBox(new Vector3d(9.0, -0.5, -0.5), new Vector3d(11.0, 0.5, 0.5)), Visible: true),
            new ExplodeNodeSpec(3, new BoundingBox(new Vector3d(999.0, -0.5, -0.5), new Vector3d(1001.0, 0.5, 0.5)), Visible: false),
        });

        AndroidViewportExplodeLayout layout = AndroidViewportExplodeView.Build(scene);

        Assert.DoesNotContain(layout.Units, unit => unit.NodeId == 3);
        AndroidViewportExplodeUnit left = Assert.Single(layout.Units, unit => unit.NodeId == 1);
        AndroidViewportExplodeUnit right = Assert.Single(layout.Units, unit => unit.NodeId == 2);
        Assert.True(left.FullOffsetWorld.X < 0.0);
        Assert.True(right.FullOffsetWorld.X > 0.0);
    }

    private static Scene CreateSceneWithNodes(params int[] nodeIds)
    {
        var document = new DocumentDto();
        document.Nodes.Add(new SceneNodeDto
        {
            Id = 0,
            ParentId = -1,
            DisplayName = "Root",
            NodeType = SceneNodeType.Root,
        });

        foreach (int nodeId in nodeIds)
        {
            document.Nodes.Add(new SceneNodeDto
            {
                Id = nodeId,
                ParentId = 0,
                DisplayName = "Node " + nodeId,
                NodeType = SceneNodeType.Shape,
            });
        }

        return Scene.FromDocument(document);
    }

    private static Scene CreateSceneWithMeshBounds(IReadOnlyList<ExplodeNodeSpec> nodeSpecs)
    {
        var document = new DocumentDto();
        document.Nodes.Add(new SceneNodeDto
        {
            Id = 0,
            ParentId = -1,
            DisplayName = "Root",
            NodeType = SceneNodeType.Root,
        });

        foreach (ExplodeNodeSpec spec in nodeSpecs)
        {
            document.Nodes.Add(new SceneNodeDto
            {
                Id = spec.NodeId,
                ParentId = 0,
                DisplayName = "Node " + spec.NodeId,
                NodeType = SceneNodeType.Shape,
                MeshId = spec.NodeId,
                Visible = spec.Visible,
            });
            document.Meshes.Add(new MeshDto
            {
                MeshId = spec.NodeId,
                Bounds = spec.Bounds,
            });
        }

        return Scene.FromDocument(document);
    }

    private readonly record struct ExplodeNodeSpec(int NodeId, BoundingBox Bounds, bool Visible);
}
