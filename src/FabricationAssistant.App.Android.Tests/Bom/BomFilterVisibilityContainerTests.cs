using System;
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.App.Android.Bom;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Bom;

/// <summary>
/// Scene visibility is hierarchical — Scene.CollectVisible prunes the whole
/// subtree of any node with Visible=false. So when the consolidated filter hides
/// a non-passing container assembly, that assembly must NOT be allowed to prune a
/// passing part nested beneath it. RetainVisibleContainers strips ancestors of
/// still-visible geometry out of the hidden set; the non-passing sibling leaves
/// stay hidden individually. The walk follows unique parent NODE ids so a part
/// whose mesh is split across several nodes sharing one occurrence id still
/// resolves its true ancestors.
/// </summary>
public class BomFilterVisibilityContainerTests
{
    // (nodeId, parentNodeId, occurrenceId, hasGeometry)
    private static BomFilterVisibilityPlanner.OccurrenceTreeNode N(int id, int parent, string occ, bool geom)
        => new(id, parent, occ, geom);

    private static IReadOnlySet<string> Hidden(params string[] ids)
        => new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void AncestorsOfVisibleLeaf_AreRemovedFromHidden()
    {
        // 0:R(asm) > 1:W(asm) > { 2:I(IGU, visible), 3:O(other, hidden) }
        var nodes = new[]
        {
            N(0, -1, "R", false),
            N(1, 0, "W", false),
            N(2, 1, "I", true),
            N(3, 1, "O", true),
        };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("R", "W", "O"), nodes);
        Assert.Equal(new[] { "O" }, result.OrderBy(x => x)); // only the other leaf stays hidden
    }

    [Fact]
    public void SplitMeshNodes_SharingOneOccurrenceId_StillResolveAncestors()
    {
        // The real-world bug: an IGU part's mesh is split across several nodes that
        // all carry the SAME occurrence id, nested under a hidden sub-assembly.
        // Keying the parent chain by occurrence id self-loops and leaves the
        // sub-assembly hidden; keying by node id resolves it.
        var nodes = new[]
        {
            N(1, -1, "W", false),   // hidden sub-assembly
            N(2, 1, "IGU", false),  // part occurrence node
            N(3, 2, "IGU", true),   // split mesh node #1 (shares occ "IGU")
            N(4, 2, "IGU", true),   // split mesh node #2 (shares occ "IGU")
        };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("W"), nodes);
        Assert.Empty(result); // "W" is the IGU's ancestor → un-hidden
    }

    [Fact]
    public void NoVisibleGeometry_LeavesHiddenUnchanged()
    {
        var nodes = new[] { N(0, -1, "R", false), N(1, 0, "W", false), N(2, 1, "I", true) };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("R", "W", "I"), nodes);
        Assert.Equal(new[] { "I", "R", "W" }, result.OrderBy(x => x));
    }

    [Fact]
    public void VisibleLeafAtRoot_NoAncestorsToProtect()
    {
        var nodes = new[] { N(0, -1, "I", true), N(1, -1, "O", true) };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("O"), nodes);
        Assert.Equal(new[] { "O" }, result.OrderBy(x => x));
    }

    [Fact]
    public void DeepNesting_AllAncestorsProtected()
    {
        var nodes = new[]
        {
            N(0, -1, "R", false),
            N(1, 0, "A", false),
            N(2, 1, "B", false),
            N(3, 2, "L", true), // visible deep leaf
        };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("R", "A", "B"), nodes);
        Assert.Empty(result); // R, A, B all protected as ancestors of L
    }

    [Fact]
    public void SharedAncestors_AcrossTwoVisibleLeaves_Deduped()
    {
        var nodes = new[]
        {
            N(0, -1, "R", false),
            N(1, 0, "W", false),
            N(2, 1, "I1", true),
            N(3, 1, "I2", true),
            N(4, 1, "O", true),
        };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("R", "W", "O"), nodes);
        Assert.Equal(new[] { "O" }, result.OrderBy(x => x));
    }

    [Fact]
    public void EmptyHidden_ReturnsEmpty()
    {
        var nodes = new[] { N(0, -1, "R", false), N(1, 0, "I", true) };
        Assert.Empty(BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden(), nodes));
    }

    [Fact]
    public void MissingParentNode_StopsWalkGracefully()
    {
        // Parent node id 9 is absent — the walk must protect what it can reach
        // (here nothing reachable above the visible leaf) and not throw or loop.
        var nodes = new[] { N(3, 9, "I", true) };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(Hidden("W"), nodes);
        Assert.Equal(new[] { "W" }, result.OrderBy(x => x)); // unreachable ancestor stays hidden
    }
}
