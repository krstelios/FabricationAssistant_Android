using System;
using System.Collections.Generic;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// Computes the set of FA occurrence ids to hide so the 3D view matches the
/// filter: the union of the occurrence ids of every part that does NOT pass the
/// filter. Pure — the part→occurrence resolver is injected.
/// </summary>
public static class BomFilterVisibilityPlanner
{
    public static IReadOnlySet<string> HiddenOccurrenceIds(
        IEnumerable<string> allPartKeys,
        IReadOnlySet<string> passingPartKeys,
        Func<string, IEnumerable<string>> occurrenceIdsForPart)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (string partKey in allPartKeys)
        {
            if (passingPartKeys.Contains(partKey))
                continue;
            foreach (string occ in occurrenceIdsForPart(partKey))
                if (!string.IsNullOrEmpty(occ))
                    hidden.Add(occ);
        }
        return hidden;
    }
}
