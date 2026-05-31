using System.Globalization;

namespace FabricationAssistant.App.Android;

internal sealed class AndroidLogcatFeed : IDisposable
{
    private const int MaxLines = 700;
    private const int MaxLineCharacters = 1000;
    private static readonly string[] ImportantCrashTags =
    [
        "AndroidRuntime",
        "DEBUG",
        "DOTNET",
        "libc",
        "mono",
        "monodroid",
    ];
    private static readonly string[] ImportantCrashFragments =
    [
        "FATAL EXCEPTION",
        "Fatal signal",
        "SIGABRT",
        "SIGSEGV",
        "OutOfMemoryError",
        "ANR in ",
    ];

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private Java.Lang.Process? _process;
    private int _droppedLines;
    private int _running;
    private bool _disposed;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public string? LastError { get; private set; }

    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _running, 1) != 0)
            return;

        LastError = null;
        var cts = new CancellationTokenSource();
        _cts = cts;
        AppendInternal("Diagnostics log feed started. Showing FA.* and crash/error logs only.", synthetic: true);
        _readerTask = Task.Run(() => ReadLogcatAsync(cts));
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        try { _cts?.Cancel(); } catch { }
        try { _process?.Destroy(); } catch { }
        AppendInternal("Diagnostics log feed paused.", synthetic: true);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _droppedLines = 0;
        }
    }

    public string SnapshotText()
    {
        lock (_gate)
        {
            if (_lines.Count == 0)
                return string.Empty;

            string prefix = _droppedLines > 0
                ? $"... {_droppedLines.ToString("N0", CultureInfo.InvariantCulture)} older log lines dropped ...{Environment.NewLine}"
                : string.Empty;
            return prefix + string.Join(Environment.NewLine, _lines);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _readerTask = null;
    }

    private async Task ReadLogcatAsync(CancellationTokenSource cts)
    {
        Java.Lang.Process? process = null;
        CancellationToken token = cts.Token;
        try
        {
            int pid = global::Android.OS.Process.MyPid();
            string pidText = pid.ToString(CultureInfo.InvariantCulture);
            var builder = new Java.Lang.ProcessBuilder(
                "/system/bin/logcat",
                "--pid",
                pidText,
                "-v",
                "time",
                "-T",
                "200");
            builder.RedirectErrorStream(true);
            process = builder.Start();
            if (process is null)
                throw new InvalidOperationException("Unable to start logcat.");

            _process = process;

            using Stream? input = process.InputStream;
            using var reader = new StreamReader(input ?? Stream.Null);
            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                    break;

                AppendInternal(line);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LastError = ex.GetBaseException().Message;
            AppendInternal("Diagnostics log feed stopped: " + LastError, synthetic: true);
        }
        finally
        {
            try { process?.Destroy(); } catch { }
            if (ReferenceEquals(_process, process))
                _process = null;
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void AppendInternal(string line, bool synthetic = false)
    {
        if (!synthetic && !ShouldIncludeLogcatLine(line))
            return;

        string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string normalizedLine = NormalizeLine(line);
        string entry = synthetic ? timestamp + " " + normalizedLine : normalizedLine;

        lock (_gate)
        {
            _lines.Enqueue(entry);
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
                _droppedLines++;
            }
        }
    }

    private static string NormalizeLine(string line)
    {
        if (line.Length <= MaxLineCharacters)
            return line;

        return line[..MaxLineCharacters] + $" ... [truncated {line.Length - MaxLineCharacters} chars]";
    }

    private static bool ShouldIncludeLogcatLine(string line)
    {
        string tag = ExtractLogcatTag(line);
        if (tag.Length > 0
            && (tag.Equals("FA", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("FA.", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (string importantTag in ImportantCrashTags)
        {
            if (tag.Equals(importantTag, StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith(importantTag + ".", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith(importantTag + "-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string fragment in ImportantCrashFragments)
        {
            if (line.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ExtractLogcatTag(string line)
    {
        int slash = line.IndexOf('/');
        if (slash < 0 || slash + 1 >= line.Length)
            return string.Empty;

        int start = slash + 1;
        int paren = line.IndexOf('(', start);
        int colon = line.IndexOf(':', start);
        int end = paren >= 0 && colon >= 0
            ? Math.Min(paren, colon)
            : paren >= 0
                ? paren
                : colon >= 0
                    ? colon
                    : line.Length;
        return end > start ? line[start..end].Trim() : string.Empty;
    }
}
