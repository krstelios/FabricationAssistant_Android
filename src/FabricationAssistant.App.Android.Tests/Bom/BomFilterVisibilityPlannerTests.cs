using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.App.Android.Bom;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Bom;

public class BomFilterVisibilityPlannerTests
{
    private static readonly Dictionary<string, string[]> Occ = new()
    {
        ["p1"] = new[] { "o1a", "o1b" },
        ["p2"] = new[] { "o2" },
        ["p3"] = new[] { "o3" },
    };

    private static IEnumerable<string> OccOf(string partKey) =>
        Occ.TryGetValue(partKey, out string[]? v) ? v : System.Array.Empty<string>();

    [Fact]
    public void HidesOccurrencesOfFailingParts_AndKeepsPassing()
    {
        var hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            allPartKeys: new[] { "p1", "p2", "p3" },
            passingPartKeys: new HashSet<string> { "p1" },
            occurrenceIdsForPart: OccOf);
        Assert.Equal(new[] { "o2", "o3" }.OrderBy(s => s), hidden.OrderBy(s => s));
    }

    [Fact]
    public void MultiOccurrencePart_ContributesAllIds_WhenFailing()
    {
        var hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            allPartKeys: new[] { "p1", "p2" },
            passingPartKeys: new HashSet<string> { "p2" },
            occurrenceIdsForPart: OccOf);
        Assert.Equal(new[] { "o1a", "o1b" }.OrderBy(s => s), hidden.OrderBy(s => s));
    }

    [Fact]
    public void AllPassing_HidesNothing()
    {
        var hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            allPartKeys: new[] { "p1", "p2", "p3" },
            passingPartKeys: new HashSet<string> { "p1", "p2", "p3" },
            occurrenceIdsForPart: OccOf);
        Assert.Empty(hidden);
    }

    private static OccNode Node(int id, int parent, string occ, bool geom) => new(id, parent, occ, geom);

    private sealed record OccNode(int Id, int Parent, string Occ, bool Geom);

    private static BomFilterVisibilityPlanner.OccurrenceTreeNode[] Tree(params OccNode[] ns) =>
        ns.Select(n => new BomFilterVisibilityPlanner.OccurrenceTreeNode(n.Id, n.Parent, n.Occ, n.Geom)).ToArray();

    [Fact]
    public void RetainVisibleContainers_ProtectsAncestor_OfVisibleOccBearingGeometry()
    {
        // container "c" (no geometry) -> visible leaf "leaf" (geometry, passing).
        var nodes = Tree(Node(1, -1, "c", false), Node(2, 1, "leaf", true));
        var candidateHidden = new HashSet<string>(System.StringComparer.Ordinal) { "c" };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(candidateHidden, nodes);
        Assert.DoesNotContain("c", result); // ancestor kept visible so the leaf renders
    }

    [Fact]
    public void RetainVisibleContainers_ProtectsAncestor_OfVisibleGeometryWithEmptyOccurrenceId()
    {
        // The visible mesh sits on a node with NO occurrence id; its ancestor "c"
        // must still be protected, otherwise the passing leaf is pruned.
        var nodes = Tree(Node(1, -1, "c", false), Node(2, 1, "", true));
        var candidateHidden = new HashSet<string>(System.StringComparer.Ordinal) { "c" };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(candidateHidden, nodes);
        Assert.DoesNotContain("c", result);
    }

    [Fact]
    public void RetainVisibleContainers_KeepsContainerHidden_WhenWholeSubtreeHidden()
    {
        // container "c" with its only geometry leaf also hidden -> nothing visible
        // beneath it, so the container stays hidden.
        var nodes = Tree(Node(1, -1, "c", false), Node(2, 1, "leaf", true));
        var candidateHidden = new HashSet<string>(System.StringComparer.Ordinal) { "c", "leaf" };
        var result = BomFilterVisibilityPlanner.RetainVisibleContainers(candidateHidden, nodes);
        Assert.Contains("c", result);
        Assert.Contains("leaf", result);
    }
}
