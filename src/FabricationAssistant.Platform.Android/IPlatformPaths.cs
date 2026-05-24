namespace FabricationAssistant.Platform;

/// <summary>
/// Abstraction over platform-specific filesystem locations. On Windows backed by
/// Environment.SpecialFolder.LocalApplicationData; on Android backed by Context.FilesDir.
/// </summary>
public interface IPlatformPaths
{
    string AppDataRoot { get; }
    string CacheDir { get; }
    string TempDir { get; }
    string LogsDir { get; }
}
