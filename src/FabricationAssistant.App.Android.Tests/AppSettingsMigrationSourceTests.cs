using System.Runtime.CompilerServices;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

/// <summary>
/// Source-level guards for AppSettings.MigrateDefaultsIfNeeded. The migration
/// helpers read SharedPreferences and cannot run in the net8.0 host runner, so
/// these assert the structure of the legacy default-removal list directly.
/// </summary>
public sealed class AppSettingsMigrationSourceTests
{
    // S9-F6 (verified false positive): the legacy-removal list strips a colour
    // channel ONLY when its default actually changed across schema versions.
    // surface_r (0.78) and surface_g (0.80) changed to 0.82 and must be listed;
    // surface_b has always defaulted to 0.82, so there is no stale value to
    // strip and it must NOT be listed. This test locks in that intent so the
    // asymmetry is not mistaken for a missing entry (or "fixed" by adding one).
    [Fact]
    public void LegacyDefaultRemoval_ListsChangedSurfaceChannelsButNotUnchangedBlue()
    {
        string settings = ReadAppSettingsSource();
        string list = ExtractLegacyFloatDefaultsList(settings);

        Assert.Contains("(\"surface_r\", 0.78f)", list);
        Assert.Contains("(\"surface_g\", 0.80f)", list);
        Assert.DoesNotContain("\"surface_b\"", list);

        // The rationale must stay documented next to the entries.
        Assert.Contains("surface_b has always defaulted to 0.82", settings);
    }

    private static string ExtractLegacyFloatDefaultsList(string source)
    {
        const string marker = "LegacyFloatDefaultsToRemove";
        int markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "LegacyFloatDefaultsToRemove was not found.");

        int open = source.IndexOf('[', markerIndex);
        Assert.True(open > markerIndex, "Legacy float defaults collection start was not found.");
        int close = source.IndexOf("];", open, StringComparison.Ordinal);
        Assert.True(close > open, "Legacy float defaults collection end was not found.");

        return source[open..close];
    }

    private static string ReadAppSettingsSource([CallerFilePath] string caller = "")
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(caller)!,
            @"..\FabricationAssistant.App.Android\AppSettings.cs")));
}
