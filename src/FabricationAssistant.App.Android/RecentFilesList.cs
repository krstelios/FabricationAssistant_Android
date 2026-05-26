namespace FabricationAssistant.App.Android;

internal static class RecentFilesList
{
    public static IReadOnlyList<RecentFileEntry> Normalize(IEnumerable<RecentFileEntry>? entries, int maxEntries)
    {
        if (entries is null || maxEntries <= 0)
            return Array.Empty<RecentFileEntry>();

        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Uri))
            .OrderByDescending(e => e.LastOpenedUnixMs)
            .GroupBy(e => CanonicalizeUri(e.Uri), StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(maxEntries)
            .ToArray();
    }

    public static IReadOnlyList<RecentFileEntry> FilterAccessible(
        IEnumerable<RecentFileEntry> entries,
        Func<string, bool> hasReadableAccess,
        int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(hasReadableAccess);

        return Normalize(entries, maxEntries)
            .Where(e => hasReadableAccess(e.Uri))
            .ToArray();
    }

    public static IReadOnlyList<RecentFileEntry> AddOrPromote(
        IEnumerable<RecentFileEntry> existingEntries,
        RecentFileEntry newEntry,
        Func<string, bool> hasReadableAccess,
        int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(existingEntries);
        ArgumentNullException.ThrowIfNull(newEntry);
        ArgumentNullException.ThrowIfNull(hasReadableAccess);

        if (string.IsNullOrWhiteSpace(newEntry.Uri) || maxEntries <= 0)
            return Normalize(existingEntries, maxEntries);

        var entries = Normalize(existingEntries, maxEntries)
            .Where(e => hasReadableAccess(e.Uri))
            .Where(e => !string.Equals(
                CanonicalizeUri(e.Uri),
                CanonicalizeUri(newEntry.Uri),
                StringComparison.Ordinal))
            .Take(maxEntries - 1)
            .ToList();

        entries.Insert(0, newEntry);
        return entries;
    }

    private static string CanonicalizeUri(string uri)
    {
        string trimmed = uri.Trim();
        try
        {
            return Uri.UnescapeDataString(trimmed);
        }
        catch (UriFormatException)
        {
            return trimmed;
        }
    }
}
