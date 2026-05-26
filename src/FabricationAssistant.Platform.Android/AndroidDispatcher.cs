using Android.OS;

namespace FabricationAssistant.Platform.Android;

public sealed class AndroidDispatcher : IDispatcher
{
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(30);
    private readonly Handler _mainHandler;
    private readonly global::Java.Lang.Thread _mainThread;
    private readonly TimeSpan _sendTimeout;

    public AndroidDispatcher(TimeSpan? sendTimeout = null)
    {
        var looper = Looper.MainLooper ?? throw new InvalidOperationException("Main Looper unavailable");
        _mainHandler = new Handler(looper);
        _mainThread = looper.Thread ?? throw new InvalidOperationException("Main thread unavailable");
        _sendTimeout = sendTimeout ?? DefaultSendTimeout;
    }

    public bool CheckAccess() => global::Java.Lang.Thread.CurrentThread() == _mainThread;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _mainHandler.Post(action);
    }

    public void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? thrown = null;
        _mainHandler.Post(() =>
        {
            try { action(); }
            catch (Exception ex) { thrown = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(_sendTimeout))
            throw new TimeoutException($"Timed out waiting {_sendTimeout.TotalSeconds:0.#}s for Android main thread dispatch.");
        if (thrown is not null) throw thrown;
    }
}
