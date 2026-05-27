using System.Text.Json;
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
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

        string? json = context
            .GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?.GetString(EntriesKey, null);

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RecentFileEntry>();

        try
        {
            var entries = JsonSerializer.Deserialize<List<RecentFileEntry>>(json, JsonOptions)
                ?? new List<RecentFileEntry>();

            return RecentFilesList.Normalize(entries, MaxEntries);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FA.Recent", "Failed to deserialize entries_json: " + ex.Message);
            return Array.Empty<RecentFileEntry>();
        }
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
    {
        try
        {
            context.ContentResolver?.TakePersistableUriPermission(
                uri,
                ActivityFlags.GrantReadUriPermission);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(
                "FA.Recent",
                "Could not persist read access for recent file: " + ex.Message);
        }
    }

    private static void Save(Context context, IReadOnlyList<RecentFileEntry> entries)
    {
        lock (Gate)
            SaveUnsafe(context, entries);
    }

    private static void SaveUnsafe(Context context, IReadOnlyList<RecentFileEntry> entries)
    {
        string json = JsonSerializer.Serialize(entries.Take(MaxEntries), JsonOptions);
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

    private static RecentFileAccessStatus ProbeReadableAccess(Context context, string uriText)
    {
        if (string.IsNullOrWhiteSpace(uriText))
            return RecentFileAccessStatus.Revoked;

        AndroidUri? uri = AndroidUri.Parse(uriText);
        if (uri is null)
            return RecentFileAccessStatus.Revoked;

        try
        {
            using var descriptor = context.ContentResolver?.OpenFileDescriptor(uri, "r");
            if (descriptor is not null)
                return RecentFileAccessStatus.Accessible;

            global::Android.Util.Log.Warn("FA.Recent", "Could not verify recent file access: descriptor unavailable.");
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
