using System.Text;
using Android.Content;
using Android.Runtime;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Process-wide crash logger (design spec S11.1). Registers the three global
/// unhandled-exception hooks and persists each crash to a size-bounded,
/// ASCII-only log under {CacheDir}/logs so a field crash leaves an artifact
/// after logcat is gone.
///
/// Strictly best-effort: every path is wrapped so the logger can never throw
/// from a crash handler, and it never marks an exception handled/observed -
/// the original crash path is preserved unchanged.
/// </summary>
public static class AndroidCrashLogger
{
    private const long MaxLogBytes = 1L * 1024 * 1024; // Rotate at 1 MB.
    private const string LogFileName = "crash.log";
    private const string PreviousLogFileName = "crash.previous.log";

    private static readonly object WriteGate = new();
    private static int _installed;
    private static string? _logDirectory;

    /// <summary>
    /// Registers the global exception handlers and resolves the log directory.
    /// Idempotent and safe to call from the Application subclass before
    /// MainActivity exists, so early-startup crashes are also captured.
    /// </summary>
    public static void Install(Context? context)
    {
        if (context is null)
            return;
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        try
        {
            string? cacheDir = context.CacheDir?.AbsolutePath
                ?? context.ApplicationContext?.CacheDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(cacheDir))
                _logDirectory = Path.Combine(cacheDir, "logs");
        }
        catch
        {
            // Leave _logDirectory null; handlers still log to logcat.
        }

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandledExceptionRaiser;
    }

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => Persist("AppDomain.UnhandledException", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        // Do NOT call e.SetObserved(): keep default behavior so the fault is not silently swallowed.
        => Persist("TaskScheduler.UnobservedTaskException", e.Exception);

    private static void OnAndroidUnhandledExceptionRaiser(object? sender, RaiseThrowableEventArgs e)
        // Leave e.Handled = false so Android's default crash path still runs.
        => Persist("AndroidEnvironment.UnhandledExceptionRaiser", e.Exception);

    private static void Persist(string source, Exception? exception)
    {
        try
        {
            string record = BuildRecord(source, exception);
            global::Android.Util.Log.Error("FA.Crash", record);
            WriteToFile(record);
        }
        catch
        {
            // Best-effort only; never throw from a crash handler.
        }
    }

    private static string BuildRecord(string source, Exception? exception)
    {
        var sb = new StringBuilder(512);
        sb.Append("=== ")
          .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"))
          .Append(' ').Append(source).Append(" ===\n");

        if (exception is null)
        {
            sb.Append("(no exception object)\n");
        }
        else
        {
            AppendException(sb, exception);
            for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            {
                sb.Append("--- inner exception ---\n");
                AppendException(sb, inner);
            }
        }

        sb.Append('\n');
        return ToAscii(sb.ToString());
    }

    private static void AppendException(StringBuilder sb, Exception exception)
    {
        sb.Append(exception.GetType().FullName).Append(": ").Append(exception.Message).Append('\n');
        sb.Append(exception.StackTrace ?? "(no stack trace)").Append('\n');
    }

    private static void WriteToFile(string record)
    {
        string? dir = _logDirectory;
        if (string.IsNullOrEmpty(dir))
            return;

        lock (WriteGate)
        {
            Directory.CreateDirectory(dir);
            string logPath = Path.Combine(dir, LogFileName);

            try
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxLogBytes)
                {
                    File.Copy(logPath, Path.Combine(dir, PreviousLogFileName), overwrite: true);
                    File.WriteAllText(logPath, string.Empty);
                }
            }
            catch
            {
                // Rotation is best-effort; fall through and keep appending.
            }

            File.AppendAllText(logPath, record);
        }
    }

    private static string ToAscii(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(c <= 0x7F ? c : '?');
        return sb.ToString();
    }
}
