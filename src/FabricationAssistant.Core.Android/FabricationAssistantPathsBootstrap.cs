// Android replacement for the desktop FabricationAssistantPaths static class.
// Lives in the Core.Android shim assembly so any linked Core/Import code that
// resolves `FabricationAssistant.Core.Runtime.FabricationAssistantPaths` finds it.
// MUST be initialized at startup via Configure(...) before any code that reads
// the static members runs.
namespace FabricationAssistant.Core.Runtime;

public static class FabricationAssistantPaths
{
    private sealed record PathSnapshot(
        string RootDirectory,
        string CacheDirectory,
        string TempDirectory,
        string LogsDirectory);

    private static PathSnapshot? _snapshot;

    public static void Configure(string rootDirectory, string cacheDirectory, string tempDirectory, string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);
        ArgumentException.ThrowIfNullOrEmpty(logsDirectory);

        var snapshot = new PathSnapshot(
            rootDirectory,
            cacheDirectory,
            tempDirectory,
            logsDirectory);
        System.Threading.Volatile.Write(ref _snapshot, snapshot);
    }

    private static PathSnapshot Snapshot
        => System.Threading.Volatile.Read(ref _snapshot) ?? throw new InvalidOperationException(
            "FabricationAssistantPaths accessed before Configure(...) was called. " +
            "Call FabricationAssistantPaths.Configure(...) once at application startup.");

    private static string EnsureConfigured(string value, string what)
        => value.Length > 0
            ? value
            : throw new InvalidOperationException(
            $"FabricationAssistantPaths.{what} accessed before Configure(...) was called. " +
            "Call FabricationAssistantPaths.Configure(...) once at application startup.");

    public static string RootDirectory => EnsureConfigured(Snapshot.RootDirectory, nameof(RootDirectory));
    public static string CacheDirectory => EnsureConfigured(Snapshot.CacheDirectory, nameof(CacheDirectory));
    public static string TempDirectory => EnsureConfigured(Snapshot.TempDirectory, nameof(TempDirectory));
    public static string LogsDirectory => EnsureConfigured(Snapshot.LogsDirectory, nameof(LogsDirectory));

    public static string FaImportExtractionDirectory => GetPath("import-temp", "fa");
    public static string DracoDecodeDirectory => GetPath("import-temp", "draco");

    public static string GetPath(params string[] segments)
    {
        string path = Snapshot.RootDirectory;
        if (segments is null || segments.Length == 0) return path;
        foreach (string segment in segments)
            path = Path.Combine(path, segment);
        return path;
    }

    public static string ConstrainDirectoryToRoot(string? candidate, params string[] fallbackSegments)
        => NormalizeToRoot(candidate, fallbackSegments);

    public static string ConstrainFileToRoot(string? candidate, params string[] fallbackSegments)
        => NormalizeToRoot(candidate, fallbackSegments);

    private static string NormalizeToRoot(string? candidate, params string[] fallbackSegments)
    {
        PathSnapshot snapshot = Snapshot;
        string fallbackPath = Combine(snapshot.RootDirectory, fallbackSegments);
        if (string.IsNullOrWhiteSpace(candidate))
            return fallbackPath;

        try
        {
            string trimmed = candidate.Trim();
            string fullPath = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(snapshot.RootDirectory, trimmed));

            return IsUnderRoot(fullPath, snapshot.RootDirectory) ? fullPath : fallbackPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"FabricationAssistantPaths.NormalizeToRoot failed for '{candidate}': {ex.Message}");
            return fallbackPath;
        }
    }

    private static string Combine(string root, params string[] segments)
    {
        string path = root;
        if (segments is null || segments.Length == 0) return path;
        foreach (string segment in segments)
            path = Path.Combine(path, segment);
        return path;
    }

    private static bool IsUnderRoot(string path, string rootDirectory)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(rootDirectory));
        string fullPath = Path.GetFullPath(path);
        return string.Equals(
                   fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
}
