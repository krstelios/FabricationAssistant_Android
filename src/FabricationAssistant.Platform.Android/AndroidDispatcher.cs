using Android.OS;

namespace FabricationAssistant.Platform.Android;

public sealed class AndroidDispatcher : IDispatcher
{
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(30);
    private readonly Handler _mainHandler;
    private readonly Looper _mainLooper;
    private readonly TimeSpan _sendTimeout;

    public AndroidDispatcher(TimeSpan? sendTimeout = null)
    {
        var looper = Looper.MainLooper ?? throw new InvalidOperationException("Main Looper unavailable");
        _ = looper.Thread ?? throw new InvalidOperationException("Main thread unavailable");
        _mainHandler = new Handler(looper);
        _mainLooper = looper;
        _sendTimeout = sendTimeout ?? DefaultSendTimeout;
    }

    public bool CheckAccess() => Looper.MyLooper() == _mainLooper;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_mainHandler.Post(action))
            throw new InvalidOperationException("Android main thread dispatch was rejected.");
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
        bool posted = _mainHandler.Post(() =>
        {
            try { action(); }
            catch (Exception ex) { thrown = ex; }
            finally { done.Set(); }
        });
        if (!posted)
            throw new InvalidOperationException("Android main thread dispatch was rejected.");
        if (!done.Wait(_sendTimeout))
            throw new TimeoutException($"Timed out waiting {_sendTimeout.TotalSeconds:0.#}s for Android main thread dispatch.");
        if (thrown is not null) throw thrown;
    }
}
