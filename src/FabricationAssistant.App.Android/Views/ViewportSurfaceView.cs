using System.Collections.Concurrent;
using Android.Content;
using Android.Opengl;
using Android.OS;
using Android.Util;
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
    private readonly GlesViewportRenderer _renderer;
    private readonly GlesRendererBridge _bridge;
    private readonly ConcurrentQueue<Action<GL>> _pending = new();

    public GlesViewportRenderer Renderer => _renderer;

    public ViewportSurfaceView(Context context) : base(context)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _bridge = new GlesRendererBridge(_renderer);
        ConfigureContext();
    }

    public ViewportSurfaceView(Context context, IAttributeSet attrs) : base(context, attrs)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _bridge = new GlesRendererBridge(_renderer);
        ConfigureContext();
    }

    public void QueueRendererCommand(Action<GL> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pending.Enqueue(action);
        RequestRender();
    }

    /// <summary>
    /// Tap-to-pick helper. Queues an offscreen pick on the GL thread, then
    /// posts the result back to the main looper so callers can update
    /// UI-thread state (SelectionState, the Properties panel, etc).
    /// </summary>
    public void PickAsync(int x, int y, Action<int?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var mainHandler = new Handler(Looper.MainLooper!);
        QueueRendererCommand(_ =>
        {
            int? hit = _renderer.Pick(x, y);
            mainHandler.Post(() => callback(hit));
        });
    }

    private void ConfigureContext()
    {
        SetEGLContextClientVersion(3);
        // Request MSAA (4x -> 2x -> off, capped by AppSettings.MsaaSamples).
        // The simple integer overload of SetEGLConfigChooser cannot request
        // EGL_SAMPLE_BUFFERS / EGL_SAMPLES, so a custom chooser is needed.
        SetEGLConfigChooser(new MultisampleConfigChooser(AppSettings.MsaaSamples));
        SetRenderer(_bridge);
        RenderMode = Rendermode.WhenDirty;
    }
}
