using System;
using System.Collections.Generic;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// The NX flatten (<c>Bom_Flatten.json</c> → the <c>bom_flat</c> table) lists an
/// assembly's <em>components</em> but omits the top-level/master assembly itself,
/// so the consolidated BOM table — a faithful render of <c>bom_flat</c> — drops the
/// master while keeping every sub-assembly. This helper recovers the missing master
/// row(s) from the hierarchy (<c>bom_node</c>) roots: a hierarchy node with no parent
/// whose part key is not already present in the flat set.
///
/// Kept pure (generic over selectors, no SQLite / Android dependency) so the decision
/// is host-testable independently of the panel that consumes it.
/// </summary>
internal static class BomConsolidatedMasterRow
{
    /// <summary>
    /// Returns the hierarchy roots that must be injected into the consolidated BOM:
    /// nodes with no parent whose key is absent from <paramref name="flatKeys"/>.
    /// Keys are compared ordinally (BOM part keys are case-sensitive identifiers) and
    /// duplicate root keys are emitted at most once.
    /// </summary>
    public static IReadOnlyList<T> MissingRoots<T>(
        IReadOnlyList<T> nodes,
        Func<T, string?> parentSelector,
        Func<T, string> keySelector,
        IReadOnlyCollection<string> flatKeys)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(parentSelector);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(flatKeys);

        if (nodes.Count == 0)
            return Array.Empty<T>();

        var flat = new HashSet<string>(flatKeys, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<T>();
        foreach (T node in nodes)
        {
            // A root carries no parent occurrence path; non-root occurrences are
            // already represented in the flat BOM and must never be injected.
            if (!string.IsNullOrEmpty(parentSelector(node)))
                continue;

            string key = keySelector(node);
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (flat.Contains(key))
                continue; // master already present in the flat set — nothing to add
            if (!seen.Add(key))
                continue; // dedupe repeated root occurrences of the same part

            result.Add(node);
        }

        return result;
    }
}
