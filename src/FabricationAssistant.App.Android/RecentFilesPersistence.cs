using System.Text.Json;

namespace FabricationAssistant.App.Android;

internal static class RecentFilesPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(IReadOnlyList<RecentFileEntry> entries, int maxEntries)
        => JsonSerializer.Serialize(entries.Take(maxEntries), JsonOptions);

    public static RecentFilesRecovery Recover(string? committedJson, string? pendingJson, int maxEntries)
    {
        if (TryDeserialize(pendingJson, maxEntries, out var pendingEntries, out var pendingError))
            return new RecentFilesRecovery(
                pendingEntries,
                PromotePending: true,
                ClearPending: false,
                PendingError: null,
                CommittedError: null);

        bool clearPending = !string.IsNullOrWhiteSpace(pendingJson);

        if (TryDeserialize(committedJson, maxEntries, out var committedEntries, out var committedError))
            return new RecentFilesRecovery(
                committedEntries,
                PromotePending: false,
                ClearPending: clearPending,
                PendingError: pendingError,
                CommittedError: null);

        return new RecentFilesRecovery(
            Array.Empty<RecentFileEntry>(),
            PromotePending: false,
            ClearPending: clearPending,
            PendingError: pendingError,
            CommittedError: committedError);
    }

    private static bool TryDeserialize(
        string? json,
        int maxEntries,
        out IReadOnlyList<RecentFileEntry> entries,
        out Exception? error)
    {
        entries = Array.Empty<RecentFileEntry>();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var deserialized = JsonSerializer.Deserialize<List<RecentFileEntry>>(json, JsonOptions)
                ?? new List<RecentFileEntry>();
            entries = RecentFilesList.Normalize(deserialized, maxEntries);
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}

internal readonly record struct RecentFilesRecovery(
    IReadOnlyList<RecentFileEntry> Entries,
    bool PromotePending,
    bool ClearPending,
    Exception? PendingError,
    Exception? CommittedError);
