using FabricationAssistant.Import.Fa;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

/// <summary>
/// S25-3: the Android test project compile-links <see cref="FaArchive"/> +
/// <c>FaArchiveLimits</c> (the .fa trust boundary) but had no coverage of them.
/// These are real behavioural tests - the entry-name allow/deny policy that defends
/// against zip-slip / device-name / ADS attacks, and the malformed-archive paths
/// (zero-byte, garbage, missing) which must fail cleanly rather than hang or throw
/// an unexpected exception type.
/// </summary>
public sealed class FaArchiveSecurityTests
{
    [Theory]
    [InlineData("model.glb")]
    [InlineData("sub/dir/components.json")]
    [InlineData("Archive_Manifest.json")]
    [InlineData("snapshots/markup-001.png")]
    public void IsSafeRelativeEntryName_AcceptsBenignRelativePaths(string name)
        => Assert.True(FaArchive.IsSafeRelativeEntryName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape.glb")]          // parent traversal
    [InlineData("a/../../escape.glb")]     // traversal mid-path
    [InlineData("/abs/path.glb")]          // absolute (POSIX)
    [InlineData("\\abs\\path.glb")]        // absolute (backslash)
    [InlineData("C:\\windows\\system32")]  // drive-letter prefix
    [InlineData("a//b.glb")]               // empty segment
    [InlineData("a/./b.glb")]              // single-dot segment
    [InlineData("a/b:stream")]             // NTFS alternate data stream
    [InlineData("CON")]                    // reserved device name
    [InlineData("com1.txt")]               // reserved device name with extension
    [InlineData("name.")]                  // trailing dot
    [InlineData("name ")]                  // trailing space
    [InlineData(" name")]                  // leading space
    [InlineData("with\0nul")]              // embedded NUL
    public void IsSafeRelativeEntryName_RejectsMaliciousOrMalformed(string? name)
        => Assert.False(FaArchive.IsSafeRelativeEntryName(name));

    [Fact]
    public void IsSafeRelativeEntryName_RejectsOverlongNames()
        => Assert.False(FaArchive.IsSafeRelativeEntryName(new string('a', 1025)));

    [Fact]
    public void Open_ZeroByteFile_ThrowsCleanly()
    {
        string path = NewTempArchivePath();
        File.WriteAllBytes(path, Array.Empty<byte>());
        try
        {
            // A zero-byte file has no zip central directory: Open must fail fast
            // (no hang, no OOM) rather than returning a half-built archive.
            Assert.ThrowsAny<Exception>(() => FaArchive.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_GarbageNonZipFile_ThrowsCleanly()
    {
        string path = NewTempArchivePath();
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x7A, 0x10 });
        try
        {
            Assert.ThrowsAny<Exception>(() => FaArchive.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        string path = NewTempArchivePath();
        Assert.Throws<FileNotFoundException>(() => FaArchive.Open(path));
    }

    private static string NewTempArchivePath()
        => Path.Combine(Path.GetTempPath(), $"fa-archive-test-{Guid.NewGuid():N}.fa");
}
