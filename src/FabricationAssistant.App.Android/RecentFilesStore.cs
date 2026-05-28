using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace FabricationAssistant.App.Android;

public static class RecentFilesStore
{
    private const string PreferencesName = "fa_recent_files";
    private const string EntriesKey = "entries_json";
    private const string EntriesPendingKey = "entries_json_pending";
    private const int MaxEntries = 10;
    private static readonly TimeSpan ReadableAccessProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();

    public static IReadOnlyList<RecentFileEntry> Load(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (Gate)
        {
            var entries = LoadUnsafe(context);
            var accessible = RecentFilesList.FilterReadableOrUnknown(
                entries,
                uriText => ProbeReadableAccess(context, uriText),
                MaxEntries);
            if (accessible.Count != entries.Count)
                SaveUnsafe(context, accessible);
            return accessible;
        }
    }

    private static IReadOnlyList<RecentFileEntry> LoadUnsafe(Context context)
    {
        var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        if (prefs is null)
            return Array.Empty<RecentFileEntry>();

        string? pendingJson = prefs.GetString(EntriesPendingKey, null);
        string? committedJson = prefs.GetString(EntriesKey, null);
        RecentFilesRecovery recovery = RecentFilesPersistence.Recover(committedJson, pendingJson, MaxEntries);
        LogDeserializeError(EntriesPendingKey, recovery.PendingError);
        LogDeserializeError(EntriesKey, recovery.CommittedError);

        if (recovery.PromotePending)
            SaveUnsafe(context, recovery.Entries);
        else if (recovery.ClearPending)
            ClearPendingUnsafe(prefs);

        return recovery.Entries;
    }

    private static void LogDeserializeError(string key, Exception? error)
    {
        if (error is not null)
            global::Android.Util.Log.Warn("FA.Recent", $"Failed to deserialize {key}: {error.Message}");
    }

    public static void Add(Context context, AndroidUri uri)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(uri);

        string uriText = uri.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(uriText))
            return;

        lock (Gate)
        {
            string displayName = ResolveDisplayName(context, uri);
            var entry = new RecentFileEntry
            {
                Uri = uriText,
                DisplayName = displayName,
                LastOpenedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            var entries = RecentFilesList.AddOrPromote(
                LoadUnsafe(context),
                entry,
                entryUri => ProbeReadableAccess(context, entryUri),
                MaxEntries);

            SaveUnsafe(context, entries);
        }
    }

    public static void Remove(Context context, string uriText)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (Gate)
        {
            var entries = LoadUnsafe(context)
                .Where(e => !string.Equals(e.Uri, uriText, StringComparison.Ordinal))
                .ToList();

            SaveUnsafe(context, entries);
        }
    }

    public static void TryTakePersistableReadPermission(Context context, AndroidUri uri)
        => TryTakePersistableReadWritePermission(context, uri);

    public static void TryTakePersistableReadWritePermission(Context context, AndroidUri uri)
    {
        try
        {
            context.ContentResolver?.TakePersistableUriPermission(
                uri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(
                "FA.Recent",
                "Could not persist read/write access for recent file: " + ex.Message);
            try
            {
                context.ContentResolver?.TakePersistableUriPermission(
                    uri,
                    ActivityFlags.GrantReadUriPermission);
            }
            catch (Exception readEx)
            {
                global::Android.Util.Log.Warn(
                    "FA.Recent",
                    "Could not persist read access for recent file: " + readEx.Message);
            }
        }
    }

    private static void Save(Context context, IReadOnlyList<RecentFileEntry> entries)
    {
        lock (Gate)
            SaveUnsafe(context, entries);
    }

    private static void SaveUnsafe(Context context, IReadOnlyList<RecentFileEntry> entries)
    {
        string json = RecentFilesPersistence.Serialize(entries, MaxEntries);
        var prefs = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        if (prefs is null)
            return;

        var pendingEditor = prefs.Edit();
        if (pendingEditor is null)
            return;

        pendingEditor.PutString(EntriesPendingKey, json);
        if (!pendingEditor.Commit())
        {
            global::Android.Util.Log.Warn("FA.Recent", "Failed to stage recent files in SharedPreferences.");
            return;
        }

        var finalEditor = prefs.Edit();
        if (finalEditor is null)
            return;

        finalEditor.PutString(EntriesKey, json);
        finalEditor.Remove(EntriesPendingKey);
        if (!finalEditor.Commit())
            global::Android.Util.Log.Warn("FA.Recent", "Failed to commit recent files to SharedPreferences.");
    }

    private static void ClearPendingUnsafe(ISharedPreferences prefs)
    {
        var editor = prefs.Edit();
        if (editor is null)
            return;

        editor.Remove(EntriesPendingKey);
        if (!editor.Commit())
            global::Android.Util.Log.Warn("FA.Recent", "Failed to clear staged recent files from SharedPreferences.");
    }

    private static RecentFileAccessStatus ProbeReadableAccess(Context context, string uriText)
    {
        if (string.IsNullOrWhiteSpace(uriText))
            return RecentFileAccessStatus.Revoked;

        AndroidUri? uri = AndroidUri.Parse(uriText);
        if (uri is null)
            return RecentFileAccessStatus.Revoked;

        try
        {
            return Task
                .Run(() => ProbeReadableAccessCore(context, uri))
                .WaitAsync(ReadableAccessProbeTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            global::Android.Util.Log.Warn(
                "FA.Recent",
                "Recent file access probe timed out; keeping entry for retry: " + uriText);
            return RecentFileAccessStatus.Unknown;
        }
        catch (Exception ex) when (IsAccessRevoked(ex))
        {
            global::Android.Util.Log.Info("FA.Recent", "Pruned inaccessible recent file: " + uriText);
            return RecentFileAccessStatus.Revoked;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FA.Recent", "Could not verify recent file access; keeping entry for retry: " + ex.Message);
            return RecentFileAccessStatus.Unknown;
        }
    }

    private static RecentFileAccessStatus ProbeReadableAccessCore(Context context, AndroidUri uri)
    {
        using var descriptor = context.ContentResolver?.OpenFileDescriptor(uri, "r");
        if (descriptor is not null)
            return RecentFileAccessStatus.Accessible;

        global::Android.Util.Log.Warn("FA.Recent", "Could not verify recent file access: descriptor unavailable.");
        return RecentFileAccessStatus.Unknown;
    }

    private static bool IsAccessRevoked(Exception ex)
        => ex is Java.Lang.SecurityException
           || ex is UnauthorizedAccessException
           || ex is FileNotFoundException
           || ex.InnerException is not null && IsAccessRevoked(ex.InnerException);

    private static string ResolveDisplayName(Context context, AndroidUri uri)
    {
        try
        {
            using var cursor = context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                int idx = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (idx >= 0)
                {
                    string? displayName = cursor.GetString(idx);
                    if (!string.IsNullOrWhiteSpace(displayName))
                        return displayName;
                }
            }
        }
        catch
        {
            // Fall through to URI-derived fallback.
        }

        string? fallback = uri.LastPathSegment;
        return string.IsNullOrWhiteSpace(fallback) ? "Model" : fallback;
    }
}
