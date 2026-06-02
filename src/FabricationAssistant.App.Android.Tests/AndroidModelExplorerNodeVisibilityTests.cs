using FabricationAssistant.App.Android;
using FabricationAssistant.Core.SceneGraph;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

/// <summary>
/// The model-explorer visibility toggle must read "is ANY part under here
/// visible?" — an assembly or packed group shows ON when at least one descendant
/// part is visible (partial counts as on), and OFF only when everything beneath
/// it is hidden. Label dimming (IsEffectivelyVisible) must still reflect the real
/// rendered visibility (own flag AND ancestors' own flags).
/// </summary>
public class AndroidModelExplorerNodeVisibilityTests
{
    private static AndroidModelExplorerNode Leaf(int id, bool visible)
        => AndroidModelExplorerNode.Create(new SceneNode
        {
            Id = id,
            DisplayName = "P" + id,
            NodeType = SceneNodeType.Part,
            Visible = visible,
        });

    private static AndroidModelExplorerNode Assembly(int id, bool ownVisible)
        => AndroidModelExplorerNode.Create(new SceneNode
        {
            Id = id,
            DisplayName = "A" + id,
            NodeType = SceneNodeType.Assembly,
            Visible = ownVisible,
        });

    [Fact]
    public void Leaf_UsesOwnVisibility()
    {
        Assert.True(Leaf(1, visible: true).IsVisible);
        Assert.False(Leaf(2, visible: false).IsVisible);
    }

    [Fact]
    public void Assembly_OnePartVisible_TogglesOn()
    {
        var asm = Assembly(1, ownVisible: true);
        asm.AddChild(Leaf(2, visible: true));
        asm.AddChild(Leaf(3, visible: false));
        Assert.True(asm.IsVisible); // partial visibility still reads ON
    }

    [Fact]
    public void Assembly_AllPartsHidden_TogglesOff_EvenWhenOwnFlagTrue()
    {
        // Parts hidden individually leave the assembly's own flag untouched; the
        // toggle must still read OFF because nothing under it is visible.
        var asm = Assembly(1, ownVisible: true);
        asm.AddChild(Leaf(2, visible: false));
        asm.AddChild(Leaf(3, visible: false));
        Assert.False(asm.IsVisible);
    }

    [Fact]
    public void VirtualGroup_OneDuplicateVisible_TogglesOn()
    {
        var group = AndroidModelExplorerNode.CreateVirtual(-1, "Window (2)", SceneNodeType.Assembly);
        group.AddChild(Leaf(2, visible: true));
        group.AddChild(Leaf(3, visible: false));
        Assert.True(group.IsVisible); // any one duplicate visible -> ON
    }

    [Fact]
    public void VirtualGroup_AllDuplicatesHidden_TogglesOff()
    {
        var group = AndroidModelExplorerNode.CreateVirtual(-1, "Window (2)", SceneNodeType.Assembly);
        group.AddChild(Leaf(2, visible: false));
        group.AddChild(Leaf(3, visible: false));
        Assert.False(group.IsVisible);
    }

    [Fact]
    public void Nested_VisibleLeafDeepInside_BubblesUpToRoot()
    {
        var root = Assembly(1, ownVisible: true);
        var mid = Assembly(2, ownVisible: true);
        root.AddChild(mid);
        mid.AddChild(Leaf(3, visible: false));
        mid.AddChild(Leaf(4, visible: true)); // one visible deep leaf
        Assert.True(mid.IsVisible);
        Assert.True(root.IsVisible);
    }

    [Fact]
    public void EffectiveVisibility_HiddenAncestor_DimsVisibleLeaf()
    {
        // Label dimming must reflect real rendering: a visible leaf under an
        // own-hidden assembly is NOT effectively visible.
        var asm = Assembly(1, ownVisible: false);
        var leaf = Leaf(2, visible: true);
        asm.AddChild(leaf);
        Assert.False(leaf.IsEffectivelyVisible);
    }

    [Fact]
    public void EffectiveVisibility_AllAncestorsVisible_LeafIsEffectivelyVisible()
    {
        var asm = Assembly(1, ownVisible: true);
        var leaf = Leaf(2, visible: true);
        asm.AddChild(leaf);
        Assert.True(leaf.IsEffectivelyVisible);
    }
}
