using Android.Content;

namespace FabricationAssistant.Platform.Android;

public sealed class AndroidPlatformPaths : IPlatformPaths
{
    private static readonly object CreateDirectoryGate = new();

    public AndroidPlatformPaths(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AppDataRoot = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Context.FilesDir is null");
        CacheDir = context.CacheDir?.AbsolutePath
            ?? throw new InvalidOperationException("Context.CacheDir is null");
        TempDir = Path.Combine(CacheDir, "temp");
        LogsDir = Path.Combine(CacheDir, "logs");

        lock (CreateDirectoryGate)
        {
            Directory.CreateDirectory(TempDir);
            Directory.CreateDirectory(LogsDir);
        }
    }

    public string AppDataRoot { get; }
    public string CacheDir { get; }
    public string TempDir { get; }
    public string LogsDir { get; }
}
