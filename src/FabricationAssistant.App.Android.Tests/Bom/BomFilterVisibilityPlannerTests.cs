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
}
