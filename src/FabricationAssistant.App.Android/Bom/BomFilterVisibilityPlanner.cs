using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// One scene node: its unique <paramref name="NodeId"/>, its parent node id
    /// (<c>-1</c> when none), the occurrence id it maps to (may be empty), and
    /// whether it carries mesh geometry. The chain is keyed by NODE id — several
    /// nodes can legitimately share one occurrence id (a part whose mesh is split
    /// across multiple primitive nodes), so the parent walk must follow node ids.
    /// </summary>
    public readonly record struct OccurrenceTreeNode(int NodeId, int ParentNodeId, string OccurrenceId, bool HasGeometry);

    /// <summary>
    /// Removes from <paramref name="candidateHidden"/> the occurrence ids of every
    /// ancestor of a still-visible geometry node. Scene visibility is hierarchical
    /// (a hidden node prunes its entire subtree), so a non-passing container
    /// assembly left in the hidden set would prune a passing part nested beneath
    /// it — making it impossible to view a leaf without also showing its whole
    /// parent assembly. Ancestors are kept visible as empty containers; the
    /// non-passing sibling leaves remain in the set and stay hidden individually.
    ///
    /// The walk follows unique parent NODE ids (not occurrence ids): a part's mesh
    /// can be split across several nodes that share one occurrence id, and keying
    /// the chain by occurrence id would collapse them into a self-loop that stops
    /// the walk before it reaches the hidden ancestor.
    /// </summary>
    public static IReadOnlySet<string> RetainVisibleContainers(
        IReadOnlySet<string> candidateHidden,
        IEnumerable<OccurrenceTreeNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(candidateHidden);
        ArgumentNullException.ThrowIfNull(nodes);

        IReadOnlyList<OccurrenceTreeNode> nodeList = nodes as IReadOnlyList<OccurrenceTreeNode> ?? nodes.ToList();
        if (nodeList.Count == 0 || candidateHidden.Count == 0)
            return candidateHidden;

        var byId = new Dictionary<int, OccurrenceTreeNode>(nodeList.Count);
        foreach (OccurrenceTreeNode node in nodeList)
            byId[node.NodeId] = node;

        var protect = new HashSet<string>(StringComparer.Ordinal);
        foreach (OccurrenceTreeNode node in nodeList)
        {
            if (!node.HasGeometry || string.IsNullOrEmpty(node.OccurrenceId))
                continue;
            if (candidateHidden.Contains(node.OccurrenceId))
                continue; // this geometry is hidden — it does not need its ancestors kept

            // Visible geometry: protect every ancestor's occurrence so the subtree
            // isn't pruned. Walk by node id with a visited-guard against cycles.
            int cur = node.ParentNodeId;
            var visited = new HashSet<int>();
            while (cur >= 0 && visited.Add(cur) && byId.TryGetValue(cur, out OccurrenceTreeNode parent))
            {
                if (!string.IsNullOrEmpty(parent.OccurrenceId))
                    protect.Add(parent.OccurrenceId);
                cur = parent.ParentNodeId;
            }
        }

        if (protect.Count == 0)
            return candidateHidden;

        var result = new HashSet<string>(candidateHidden, StringComparer.Ordinal);
        result.ExceptWith(protect);
        return result;
    }
}
