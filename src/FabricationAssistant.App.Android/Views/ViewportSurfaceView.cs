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
    private int _delayedRenderRequestScheduled;
    private int _rendererDisposeQueued;
    private int _queueSoftCapWarningArmed;
    private int _paused;
    private int _disposed;

    public GlesViewportRenderer Renderer => _renderer;

    public event Action? RendererSurfaceCreated;

    private static Choreographer MainChoreographer => Choreographer.Instance!;

    public ViewportSurfaceView(Context context) : base(context)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _renderer.DelayedRenderRequested += OnRendererDelayedRenderRequested;
        _bridge = new GlesRendererBridge(_renderer, OnRendererSurfaceCreated);
        _vsyncRenderCallback = new VsyncRenderCallback(this);
        ConfigureContext();
    }

    public ViewportSurfaceView(Context context, IAttributeSet attrs) : base(context, attrs)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _renderer.DelayedRenderRequested += OnRendererDelayedRenderRequested;
        _bridge = new GlesRendererBridge(_renderer, OnRendererSurfaceCreated);
        _vsyncRenderCallback = new VsyncRenderCallback(this);
        ConfigureContext();
    }

    public new void RequestRender()
    {
        if (!CanScheduleRendering())
            return;

        Volatile.Write(ref _renderRequestPending, 1);
        if (Interlocked.Exchange(ref _renderRequestScheduled, 1) != 0)
            return;

        if (Looper.MyLooper() == Looper.MainLooper)
            PostVsyncRenderCallback();
        else if (!TryPostToMain(PostVsyncRenderCallback, "schedule-vsync-render"))
            ClearPendingRenderCallbacks();
    }

    private void OnRendererDelayedRenderRequested(int delayMilliseconds)
    {
        if (!CanScheduleRendering())
            return;

        int clampedDelay = Math.Clamp(delayMilliseconds, 1, 1_000);
        if (Interlocked.Exchange(ref _delayedRenderRequestScheduled, 1) != 0)
            return;

        _mainHandler.PostDelayed(
            () =>
            {
                Interlocked.Exchange(ref _delayedRenderRequestScheduled, 0);
                if (CanScheduleRendering())
                    RequestRender();
            },
            clampedDelay);
    }

    protected override void OnDetachedFromWindow()
    {
        MarkDisposed("detached");
        DisposeRendererOnGlThread();
        base.OnDetachedFromWindow();
    }

    public new void OnPause()
    {
        Volatile.Write(ref _paused, 1);
        ClearPendingRenderCallbacks();
        // Intentional: queued GL work is tied to the current Activity/Surface.
        // In-flight commands re-check CanScheduleRendering before invoking UI continuations.
        ClearPendingRendererCommands("pause");
        RemoveVsyncRenderCallback("pause");
        base.OnPause();
    }

    public new void OnResume()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        Volatile.Write(ref _paused, 0);
        base.OnResume();
        if (!_pending.IsEmpty)
            RequestRender();
    }

    public bool QueueRendererCommand(Action<GL> action)
        => QueueRendererCommand("renderer-command", action);

    public bool QueueRendererCommand(string name, Action<GL> action, bool logSlow = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        string commandName = string.IsNullOrWhiteSpace(name) ? "renderer-command" : name;
        if (Volatile.Read(ref _disposed) != 0)
        {
            Log.Warn("FA.RenderQueue", $"Dropped GL command: name={commandName}, reason=disposed.");
            return false;
        }

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
        if (CanScheduleRendering())
            RequestRender();

        return true;
    }

    public void DisposeRendererOnGlThread()
    {
        MarkDisposed("dispose");
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
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(ViewportSurfaceView)));

        if (Volatile.Read(ref _paused) != 0)
            return Task.FromCanceled(new CancellationToken(canceled: true));

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

        // Subscribe before queueing the marker. QueueRendererCommand schedules
        // a render; the marker arms completion on the GL thread before the
        // next FrameRendered callback is raised.
        _renderer.FrameRendered += Complete;
        if (cancellationToken.CanBeCanceled)
            registration = cancellationToken.Register(Cancel);

        if (cancellationToken.IsCancellationRequested)
            Cancel();
        else if (!QueueRendererCommand("first-frame-marker", _ => Volatile.Write(ref armed, 1)))
            Cancel();

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
        string commandName = "pick:" + reason;
        if (!CanScheduleRendering())
        {
            Log.Warn("FA.RenderQueue", $"Dropped pick request: name={commandName}, reason={GetRenderUnavailableReason()}.");
            return;
        }

        QueueRendererCommand(commandName, _ =>
        {
            if (!CanScheduleRendering())
                return;

            int? hit = _renderer.Pick(x, y);
            TryPostToMain(() =>
            {
                if (CanScheduleRendering())
                    callback(hit);
            }, commandName);
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
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        TryPostToMain(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                RendererSurfaceCreated?.Invoke();
        }, "renderer-surface-created");
    }

    private void PostVsyncRenderCallback()
    {
        if (!CanScheduleRendering()
            || Volatile.Read(ref _renderRequestScheduled) == 0)
        {
            return;
        }

        try
        {
            MainChoreographer.PostFrameCallback(_vsyncRenderCallback);
        }
        catch (Exception ex)
        {
            ClearPendingRenderCallbacks();
            Log.Warn("FA.Renderer", "Could not post vsync render callback: " + ex.Message);
        }
    }

    private void OnVsyncRender(long frameTimeNanos)
    {
        Interlocked.Exchange(ref _renderRequestScheduled, 0);
        if (!CanScheduleRendering())
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
        Interlocked.Exchange(ref _delayedRenderRequestScheduled, 0);
    }

    private void ClearPendingRendererCommands(string reason)
    {
        int count = 0;
        while (_pending.TryDequeue(out _))
            count++;

        if (count > 0)
            Log.Warn("FA.RenderQueue", $"Dropped {count} pending GL command(s): reason={reason}.");
    }

    private bool CanScheduleRendering()
        => Volatile.Read(ref _paused) == 0 && Volatile.Read(ref _disposed) == 0;

    private string GetRenderUnavailableReason()
        => Volatile.Read(ref _disposed) != 0
            ? "disposed"
            : Volatile.Read(ref _paused) != 0
                ? "paused"
                : "unavailable";

    private void MarkDisposed(string reason)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        RendererSurfaceCreated = null;
        _renderer.DelayedRenderRequested -= OnRendererDelayedRenderRequested;
        ClearPendingRenderCallbacks();
        ClearPendingRendererCommands(reason);
        RemoveVsyncRenderCallback(reason);
    }

    private void RemoveVsyncRenderCallback(string reason)
    {
        void Remove()
        {
            try
            {
                MainChoreographer.RemoveFrameCallback(_vsyncRenderCallback);
            }
            catch (Exception ex)
            {
                Log.Warn("FA.Renderer", $"Could not remove vsync render callback: reason={reason}, error={ex.Message}");
            }
        }

        if (Looper.MyLooper() == Looper.MainLooper)
            Remove();
        else
            TryPostToMain(Remove, "remove-vsync-render-" + reason);
    }

    private bool TryPostToMain(Action action, string reason)
    {
        try
        {
            if (_mainHandler.Post(action))
                return true;

            Log.Warn("FA.Renderer", $"Main looper post rejected: reason={reason}.");
        }
        catch (Exception ex)
        {
            Log.Warn("FA.Renderer", $"Main looper post failed: reason={reason}, error={ex.Message}");
        }

        return false;
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
