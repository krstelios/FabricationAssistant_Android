using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Selection;

namespace FabricationAssistant.App.Android;

internal static class AndroidScenePackageState
{
    private const string FaSourceFormat = "fa";

    public static bool IsFa(ScenePackageInfoDto? packageInfo)
        => packageInfo is not null
           && string.Equals(packageInfo.SourceFormat, FaSourceFormat, StringComparison.OrdinalIgnoreCase);

    public static string[] NormalizeOccurrenceIds(IEnumerable<string> occurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(occurrenceIds);

        return occurrenceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string[] GetOccurrenceIdsForNodes(Scene scene, IEnumerable<int> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(nodeIds);

        return nodeIds
            .Select(scene.GetNode)
            .Select(node => TryGetOccurrenceId(scene, node, out string id) ? id : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static int[] GetNodeIdsForOccurrences(Scene scene, IEnumerable<string> occurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(scene);
        string[] normalizedOccurrenceIds = NormalizeOccurrenceIds(occurrenceIds);
        if (normalizedOccurrenceIds.Length == 0)
            return Array.Empty<int>();

        var occurrenceSet = new HashSet<string>(normalizedOccurrenceIds, StringComparer.Ordinal);
        return scene.NodesById.Values
            .Where(node => TryGetOccurrenceId(scene, node, out string occurrenceId)
                           && occurrenceSet.Contains(occurrenceId))
            .Select(node => node.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    public static bool ApplyVisibilityState(
        Scene scene,
        PackageSessionState packageSession,
        IEnumerable<string> hiddenOccurrenceIds,
        IEnumerable<string> isolatedOccurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(packageSession);

        string[] hidden = NormalizeOccurrenceIds(hiddenOccurrenceIds);
        string[] isolated = NormalizeOccurrenceIds(isolatedOccurrenceIds);
        if (packageSession.HiddenOccurrenceIds.SetEquals(hidden)
            && packageSession.IsolatedOccurrenceIds.SetEquals(isolated))
        {
            return false;
        }

        packageSession.SetVisibility(hidden, isolated);
        ApplyVisibilityToScene(scene, packageSession);
        return true;
    }

    public static void ApplyVisibilityToScene(Scene scene, PackageSessionState packageSession)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(packageSession);

        HashSet<string> hiddenOccurrenceIds = packageSession.HiddenOccurrenceIds;
        HashSet<string> isolatedOccurrenceIds = packageSession.IsolatedOccurrenceIds;
        bool hasIsolation = isolatedOccurrenceIds.Count > 0;
        var visibleNodeIds = new HashSet<int>();

        if (hasIsolation)
        {
            foreach (SceneNode node in scene.NodesById.Values.OrderBy(node => node.Id))
            {
                if (!TryGetOccurrenceId(scene, node, out string occurrenceId)
                    || !isolatedOccurrenceIds.Contains(occurrenceId))
                {
                    continue;
                }

                visibleNodeIds.Add(node.Id);
                CollectDescendantIds(node, visibleNodeIds);

                for (SceneNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    visibleNodeIds.Add(ancestor.Id);
            }
        }

        foreach (SceneNode node in scene.NodesById.Values)
        {
            bool hasOccurrence = TryGetOccurrenceId(scene, node, out string occurrenceId);
            bool visibleByIsolation = !hasIsolation || visibleNodeIds.Contains(node.Id);
            bool hiddenBySession = hasOccurrence && hiddenOccurrenceIds.Contains(occurrenceId);
            node.Visible = visibleByIsolation && !hiddenBySession;
        }
    }

    public static bool TryGetOccurrenceId(Scene scene, SceneNode? node, out string occurrenceId)
    {
        occurrenceId = string.Empty;
        if (node is null)
            return false;

        if (IsVersionedId(node.Metadata?.OccurrenceId, "occ"))
        {
            occurrenceId = node.Metadata!.OccurrenceId!;
            return true;
        }

        if (!TryResolvePackageId(scene, node, out string packageId))
            return false;

        string nodePath = !string.IsNullOrWhiteSpace(node.Metadata?.OccurrencePath)
            ? node.Metadata!.OccurrencePath!
            : node.SourceNodePath ?? node.Id.ToString(CultureInfo.InvariantCulture);

        occurrenceId = BuildDeterministicOpaqueId("occ", packageId, nodePath);
        return true;
    }

    private static bool TryResolvePackageId(Scene scene, SceneNode node, out string packageId)
    {
        if (IsVersionedId(node.Metadata?.PackageId, "pkg"))
        {
            packageId = node.Metadata!.PackageId!;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(node.Metadata?.PackageId))
        {
            packageId = FormatStableId("pkg", node.Metadata.PackageId!);
            return true;
        }

        string? fallback = scene.PackageInfo?.PackageId;
        if (IsVersionedId(fallback, "pkg"))
        {
            packageId = fallback!;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            packageId = FormatStableId("pkg", fallback!);
            return true;
        }

        packageId = string.Empty;
        return false;
    }

    private static void CollectDescendantIds(SceneNode node, HashSet<int> ids)
    {
        foreach (SceneNode child in node.Children)
        {
            if (ids.Add(child.Id))
                CollectDescendantIds(child, ids);
        }
    }

    private static bool IsVersionedId(string? value, string prefix)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith($"{prefix}_v1_", StringComparison.Ordinal);

    private static string FormatStableId(string prefix, string value)
    {
        if (IsVersionedId(value, prefix))
            return value;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC)));
        return $"{prefix}_v1_{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string BuildDeterministicOpaqueId(string prefix, params string[] fields)
    {
        string seed = string.Join("\n", fields.Select(value => value.Normalize(NormalizationForm.FormC)));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"{prefix}_v1_{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
