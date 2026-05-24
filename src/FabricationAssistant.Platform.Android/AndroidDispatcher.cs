using Android.OS;

namespace FabricationAssistant.Platform.Android;

public sealed class AndroidDispatcher : IDispatcher
{
    private readonly Handler _mainHandler;
    private readonly global::Java.Lang.Thread _mainThread;

    public AndroidDispatcher()
    {
        var looper = Looper.MainLooper ?? throw new InvalidOperationException("Main Looper unavailable");
        _mainHandler = new Handler(looper);
        _mainThread = looper.Thread ?? throw new InvalidOperationException("Main thread unavailable");
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
        done.Wait();
        if (thrown is not null) throw thrown;
    }
}
