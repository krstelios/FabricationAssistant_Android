// Android replacement for the desktop FabricationAssistantPaths static class.
// Lives in the Core.Android shim assembly so any linked Core/Import code that
// resolves `FabricationAssistant.Core.Runtime.FabricationAssistantPaths` finds it.
// MUST be initialized once at startup via Configure(...) before any code that
// reads the static members runs.
namespace FabricationAssistant.Core.Runtime;

public static class FabricationAssistantPaths
{
    private static string? _rootDirectory;
    private static string? _cacheDirectory;
    private static string? _tempDirectory;
    private static string? _logsDirectory;

    public static void Configure(string rootDirectory, string cacheDirectory, string tempDirectory, string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);
        ArgumentException.ThrowIfNullOrEmpty(logsDirectory);

        _rootDirectory = rootDirectory;
        _cacheDirectory = cacheDirectory;
        _tempDirectory = tempDirectory;
        _logsDirectory = logsDirectory;
    }

    private static string EnsureConfigured(string? value, string what)
        => value ?? throw new InvalidOperationException(
            $"FabricationAssistantPaths.{what} accessed before Configure(...) was called. " +
            "Call FabricationAssistantPaths.Configure(...) once at application startup.");

    public static string RootDirectory => EnsureConfigured(_rootDirectory, nameof(RootDirectory));
    public static string CacheDirectory => GetPath("cache");
    public static string TempDirectory => EnsureConfigured(_tempDirectory, nameof(TempDirectory));
    public static string LogsDirectory => EnsureConfigured(_logsDirectory, nameof(LogsDirectory));

    public static string FaImportExtractionDirectory => GetPath("import-temp", "fa");
    public static string DracoDecodeDirectory => GetPath("import-temp", "draco");

    public static string GetPath(params string[] segments)
    {
        string path = RootDirectory;
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
        string fallbackPath = GetPath(fallbackSegments);
        if (string.IsNullOrWhiteSpace(candidate))
            return fallbackPath;

        try
        {
            string trimmed = candidate.Trim();
            string fullPath = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(RootDirectory, trimmed));

            return IsUnderRoot(fullPath) ? fullPath : fallbackPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"FabricationAssistantPaths.NormalizeToRoot failed for '{candidate}': {ex.Message}");
            return fallbackPath;
        }
    }

    private static bool IsUnderRoot(string path)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(RootDirectory));
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
