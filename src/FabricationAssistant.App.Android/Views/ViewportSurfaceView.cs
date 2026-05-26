using System.Collections.Concurrent;
using System.Diagnostics;
using Android.Content;
using Android.Opengl;
using Android.OS;
using Android.Util;
using Android.Views;
using FabricationAssistant.Rendering.Gles;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.App.Android.Views;

/// <summary>
/// Hosts the GLES viewport. Owns the renderer + bridge and exposes a thread-safe
/// command queue so UI-thread import work can enqueue GL upload calls that run
/// on the GL render thread.
/// </summary>
public sealed class ViewportSurfaceView : GLSurfaceView
{
    private const double SlowGlCommandMs = 8.0;
    private const double SlowGlCommandWaitMs = 25.0;
    private const int SoftPendingCommandWarningCount = 256;

    private readonly GlesViewportRenderer _renderer;
    private readonly GlesRendererBridge _bridge;
    private readonly ConcurrentQueue<Action<GL>> _pending = new();
    private readonly Handler _mainHandler = new(Looper.MainLooper!);
    private readonly VsyncRenderCallback _vsyncRenderCallback;
    private int _renderRequestPending;
    private int _renderRequestScheduled;
    private int _rendererDisposeQueued;
    private int _queueSoftCapWarningArmed;
    private int _paused;

    public GlesViewportRenderer Renderer => _renderer;

    public event Action? RendererSurfaceCreated;

    private static Choreographer MainChoreographer => Choreographer.Instance!;

    public ViewportSurfaceView(Context context) : base(context)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _bridge = new GlesRendererBridge(_renderer, OnRendererSurfaceCreated);
        _vsyncRenderCallback = new VsyncRenderCallback(this);
        ConfigureContext();
    }

    public ViewportSurfaceView(Context context, IAttributeSet attrs) : base(context, attrs)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _bridge = new GlesRendererBridge(_renderer, OnRendererSurfaceCreated);
        _vsyncRenderCallback = new VsyncRenderCallback(this);
        ConfigureContext();
    }

    public new void RequestRender()
    {
        if (Volatile.Read(ref _paused) != 0)
            return;

        Volatile.Write(ref _renderRequestPending, 1);
        if (Interlocked.Exchange(ref _renderRequestScheduled, 1) != 0)
            return;

        if (Looper.MyLooper() == Looper.MainLooper)
            PostVsyncRenderCallback();
        else
            _mainHandler.Post(PostVsyncRenderCallback);
    }

    protected override void OnDetachedFromWindow()
    {
        DisposeRendererOnGlThread();
        ClearPendingRenderCallbacks();

        if (Looper.MyLooper() == Looper.MainLooper)
            MainChoreographer.RemoveFrameCallback(_vsyncRenderCallback);
        else
            _mainHandler.Post(() => MainChoreographer.RemoveFrameCallback(_vsyncRenderCallback));

        base.OnDetachedFromWindow();
    }

    public new void OnPause()
    {
        Volatile.Write(ref _paused, 1);
        ClearPendingRenderCallbacks();
        ClearPendingRendererCommands("pause");
        if (Looper.MyLooper() == Looper.MainLooper)
            MainChoreographer.RemoveFrameCallback(_vsyncRenderCallback);
        else
            _mainHandler.Post(() => MainChoreographer.RemoveFrameCallback(_vsyncRenderCallback));
        base.OnPause();
    }

    public new void OnResume()
    {
        Volatile.Write(ref _paused, 0);
        base.OnResume();
    }

    public void QueueRendererCommand(Action<GL> action)
        => QueueRendererCommand("renderer-command", action);

    public void QueueRendererCommand(string name, Action<GL> action, bool logSlow = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        string commandName = string.IsNullOrWhiteSpace(name) ? "renderer-command" : name;
        long queuedTicks = Stopwatch.GetTimestamp();
        _pending.Enqueue(gl =>
        {
            long startTicks = Stopwatch.GetTimestamp();
            try
            {
                action(gl);
            }
            finally
            {
                long endTicks = Stopwatch.GetTimestamp();
                double waitMs = (startTicks - queuedTicks) * 1000.0 / Stopwatch.Frequency;
                double runMs = (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
                if (logSlow && (runMs >= SlowGlCommandMs || waitMs >= SlowGlCommandWaitMs))
                {
                    Log.Warn(
                        "FA.RenderQueue",
                        $"GL command timing: name={commandName}, run={runMs:0.0}ms, wait={waitMs:0.0}ms.");
                }
            }
        });
        int pendingCount = _pending.Count;
        if (pendingCount > SoftPendingCommandWarningCount)
        {
            if (Interlocked.Exchange(ref _queueSoftCapWarningArmed, 1) == 0)
            {
                Log.Warn(
                    "FA.RenderQueue",
                    $"GL command queue above soft cap: count={pendingCount}, cap={SoftPendingCommandWarningCount}, newest={commandName}.");
            }
        }
        else
        {
            Interlocked.Exchange(ref _queueSoftCapWarningArmed, 0);
        }
        RequestRender();
    }

    public void DisposeRendererOnGlThread()
    {
        if (Interlocked.Exchange(ref _rendererDisposeQueued, 1) != 0)
            return;

        try
        {
            QueueEvent(() =>
            {
                try { _renderer.Dispose(); }
                catch (Exception ex)
                {
                    Log.Warn("FA.Renderer", "Renderer disposal failed: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("FA.Renderer", "Could not queue renderer disposal: " + ex.Message);
        }
    }

    public Task WaitForNextRenderedFrameAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        int completed = 0;
        int armed = 0;

        void Complete()
        {
            if (Volatile.Read(ref armed) == 0)
                return;

            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;

            _renderer.FrameRendered -= Complete;
            registration.Dispose();
            tcs.TrySetResult();
        }

        void Cancel()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;

            _renderer.FrameRendered -= Complete;
            tcs.TrySetCanceled(cancellationToken);
        }

        _renderer.FrameRendered += Complete;
        if (cancellationToken.CanBeCanceled)
            registration = cancellationToken.Register(Cancel);

        if (cancellationToken.IsCancellationRequested)
            Cancel();
        else
            QueueRendererCommand("first-frame-marker", _ => Volatile.Write(ref armed, 1));

        return tcs.Task;
    }

    /// <summary>
    /// Tap-to-pick helper. Queues an offscreen pick on the GL thread, then
    /// posts the result back to the main looper so callers can update
    /// UI-thread state (SelectionState, the Properties panel, etc).
    /// </summary>
    public void PickAsync(int x, int y, Action<int?> callback)
        => PickAsync(x, y, "pick", callback);

    public void PickAsync(int x, int y, string reason, Action<int?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        QueueRendererCommand("pick:" + reason, _ =>
        {
            int? hit = _renderer.Pick(x, y);
            _mainHandler.Post(() => callback(hit));
        });
    }

    private void ConfigureContext()
    {
        SetEGLContextClientVersion(3);
        // Preserve the EGL context across app pause/resume cycles. Without
        // this, going to background destroys the context and every GL
        // handle in the scene (GpuMesh VBOs, FBOs, textures) becomes
        // invalid. Set BEFORE SetEGLConfigChooser per GLSurfaceView contract.
        PreserveEGLContextOnPause = true;
        // Default backbuffer: RGB8 + Depth24 + Stencil8, single-sample.
        // MSAA is handled by the renderer's offscreen FBO; see Plan 3B
        // (MsaaSceneFramebuffer). The simple integer overload of
        // SetEGLConfigChooser cannot request EGL_STENCIL_SIZE, so we still
        // use MultisampleConfigChooser - just at 0 samples.
        SetEGLConfigChooser(new MultisampleConfigChooser(0));
        SetRenderer(_bridge);
        RenderMode = Rendermode.WhenDirty;
        Log.Info("FA.Renderer", "Render pacing: Choreographer vsync request coalescing enabled.");
    }

    private void OnRendererSurfaceCreated()
        => _mainHandler.Post(() => RendererSurfaceCreated?.Invoke());

    private void PostVsyncRenderCallback()
    {
        if (Volatile.Read(ref _paused) == 0
            && Volatile.Read(ref _renderRequestScheduled) != 0)
            MainChoreographer.PostFrameCallback(_vsyncRenderCallback);
    }

    private void OnVsyncRender(long frameTimeNanos)
    {
        Interlocked.Exchange(ref _renderRequestScheduled, 0);
        if (Volatile.Read(ref _paused) != 0)
        {
            Interlocked.Exchange(ref _renderRequestPending, 0);
            return;
        }

        if (Interlocked.Exchange(ref _renderRequestPending, 0) != 0)
            base.RequestRender();
    }

    private void ClearPendingRenderCallbacks()
    {
        Interlocked.Exchange(ref _renderRequestPending, 0);
        Interlocked.Exchange(ref _renderRequestScheduled, 0);
    }

    private void ClearPendingRendererCommands(string reason)
    {
        int count = 0;
        while (_pending.TryDequeue(out _))
            count++;

        if (count > 0)
            Log.Warn("FA.RenderQueue", $"Dropped {count} pending GL command(s): reason={reason}.");
    }

    private sealed class VsyncRenderCallback : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private readonly WeakReference<ViewportSurfaceView> _owner;

        public VsyncRenderCallback(ViewportSurfaceView owner)
            => _owner = new WeakReference<ViewportSurfaceView>(owner);

        public void DoFrame(long frameTimeNanos)
        {
            if (_owner.TryGetTarget(out var owner))
                owner.OnVsyncRender(frameTimeNanos);
        }
    }
}
