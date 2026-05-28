using FabricationAssistant.App.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class RecentFilesPersistenceTests
{
    [Fact]
    public void Recover_promotes_valid_pending_entries_after_interrupted_write()
    {
        string committed = RecentFilesPersistence.Serialize(
            [Entry("content://committed", 10)],
            maxEntries: 10);
        string pending = RecentFilesPersistence.Serialize(
            [Entry("content://pending", 20)],
            maxEntries: 10);

        RecentFilesRecovery recovery = RecentFilesPersistence.Recover(committed, pending, maxEntries: 10);

        Assert.True(recovery.PromotePending);
        Assert.False(recovery.ClearPending);
        Assert.Null(recovery.PendingError);
        Assert.Null(recovery.CommittedError);
        Assert.Collection(
            recovery.Entries,
            entry => Assert.Equal("content://pending", entry.Uri));
    }

    [Fact]
    public void Recover_clears_invalid_pending_entries_and_uses_committed_entries()
    {
        string committed = RecentFilesPersistence.Serialize(
            [Entry("content://committed", 10)],
            maxEntries: 10);

        RecentFilesRecovery recovery = RecentFilesPersistence.Recover(
            committed,
            pendingJson: "{not-json",
            maxEntries: 10);

        Assert.False(recovery.PromotePending);
        Assert.True(recovery.ClearPending);
        Assert.NotNull(recovery.PendingError);
        Assert.Null(recovery.CommittedError);
        Assert.Collection(
            recovery.Entries,
            entry => Assert.Equal("content://committed", entry.Uri));
    }

    [Fact]
    public void Recover_returns_empty_entries_for_malformed_committed_json()
    {
        RecentFilesRecovery recovery = RecentFilesPersistence.Recover(
            committedJson: "{not-json",
            pendingJson: null,
            maxEntries: 10);

        Assert.False(recovery.PromotePending);
        Assert.False(recovery.ClearPending);
        Assert.Null(recovery.PendingError);
        Assert.NotNull(recovery.CommittedError);
        Assert.Empty(recovery.Entries);
    }

    private static RecentFileEntry Entry(string uri, long lastOpenedUnixMs)
        => new()
        {
            Uri = uri,
            DisplayName = uri,
            LastOpenedUnixMs = lastOpenedUnixMs,
        };
}
