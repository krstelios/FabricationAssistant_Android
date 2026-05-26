using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class RecentFilesListTests
{
    [Fact]
    public void FilterAccessible_removes_revoked_entries_and_keeps_recent_order()
    {
        var entries = new[]
        {
            Entry("content://old", 10),
            Entry("content://revoked", 30),
            Entry("content://new", 20),
        };

        var filtered = RecentFilesList.FilterAccessible(
            entries,
            uri => uri != "content://revoked",
            maxEntries: 10);

        Assert.Collection(
            filtered,
            e => Assert.Equal("content://new", e.Uri),
            e => Assert.Equal("content://old", e.Uri));
    }

    [Fact]
    public void AddOrPromote_preserves_other_accessible_entries_and_deduplicates_uri()
    {
        var entries = new[]
        {
            Entry("content://a", 30),
            Entry("content://b", 20),
            Entry("content://revoked", 40),
        };

        var promoted = RecentFilesList.AddOrPromote(
            entries,
            Entry("content://b", 50, "B2"),
            uri => uri != "content://revoked",
            maxEntries: 10);

        Assert.Collection(
            promoted,
            e =>
            {
                Assert.Equal("content://b", e.Uri);
                Assert.Equal("B2", e.DisplayName);
            },
            e => Assert.Equal("content://a", e.Uri));
    }

    [Fact]
    public void AddOrPromote_caps_result_after_inserting_new_entry()
    {
        var entries = Enumerable.Range(0, 12)
            .Select(i => Entry($"content://{i}", 100 - i))
            .ToArray();

        var promoted = RecentFilesList.AddOrPromote(
            entries,
            Entry("content://new", 200),
            _ => true,
            maxEntries: 10);

        Assert.Equal(10, promoted.Count);
        Assert.Equal("content://new", promoted[0].Uri);
        Assert.DoesNotContain(promoted, e => e.Uri == "content://9");
    }

    [Fact]
    public void AddOrPromote_deduplicates_percent_encoded_uri_variants()
    {
        var entries = new[]
        {
            Entry("content://provider/models/part%20a.glb", 30),
            Entry("content://provider/models/other.glb", 20),
        };

        var promoted = RecentFilesList.AddOrPromote(
            entries,
            Entry("content://provider/models/part a.glb", 50),
            _ => true,
            maxEntries: 10);

        Assert.Collection(
            promoted,
            e => Assert.Equal("content://provider/models/part a.glb", e.Uri),
            e => Assert.Equal("content://provider/models/other.glb", e.Uri));
    }

    private static RecentFileEntry Entry(string uri, long lastOpenedUnixMs, string? displayName = null)
        => new()
        {
            Uri = uri,
            DisplayName = displayName ?? uri,
            LastOpenedUnixMs = lastOpenedUnixMs,
        };
}
