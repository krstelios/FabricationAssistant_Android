using System.Text.Json;
using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace FabricationAssistant.App.Android;

public sealed class RecentFileEntry
{
    public string Uri { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long LastOpenedUnixMs { get; set; }
}

public static class RecentFilesStore
{
    private const string PreferencesName = "fa_recent_files";
    private const string EntriesKey = "entries_json";
    private const int MaxEntries = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<RecentFileEntry> Load(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? json = context
            .GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?.GetString(EntriesKey, null);

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RecentFileEntry>();

        try
        {
            var entries = JsonSerializer.Deserialize<List<RecentFileEntry>>(json, JsonOptions)
                ?? new List<RecentFileEntry>();

            return entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Uri))
                .OrderByDescending(e => e.LastOpenedUnixMs)
                .Take(MaxEntries)
                .ToArray();
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

        string displayName = ResolveDisplayName(context, uri);
        var entries = Load(context)
            .Where(e => !string.Equals(e.Uri, uriText, StringComparison.Ordinal))
            .Take(MaxEntries - 1)
            .ToList();

        entries.Insert(0, new RecentFileEntry
        {
            Uri = uriText,
            DisplayName = displayName,
            LastOpenedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        Save(context, entries);
    }

    public static void Remove(Context context, string uriText)
    {
        ArgumentNullException.ThrowIfNull(context);

        var entries = Load(context)
            .Where(e => !string.Equals(e.Uri, uriText, StringComparison.Ordinal))
            .ToList();

        Save(context, entries);
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
        string json = JsonSerializer.Serialize(entries.Take(MaxEntries), JsonOptions);
        context
            .GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?.Edit()
            ?.PutString(EntriesKey, json)
            ?.Apply();
    }

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
