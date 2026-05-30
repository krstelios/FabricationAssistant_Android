using System.Globalization;

namespace FabricationAssistant.App.Android;

internal sealed class AndroidLogcatFeed : IDisposable
{
    private const int MaxLines = 700;

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
        AppendInternal("Diagnostics log feed started.");
        _readerTask = Task.Run(() => ReadLogcatAsync(cts));
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        try { _cts?.Cancel(); } catch { }
        try { _process?.Destroy(); } catch { }
        AppendInternal("Diagnostics log feed paused.");
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
            AppendInternal("Diagnostics log feed stopped: " + LastError);
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

    private void AppendInternal(string line)
    {
        string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string entry = timestamp + " " + line;

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
}
