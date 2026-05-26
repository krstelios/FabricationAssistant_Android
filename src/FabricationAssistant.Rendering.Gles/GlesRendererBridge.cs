using Android.Opengl;
using Javax.Microedition.Khronos.Opengles;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Bridge from Android's GLSurfaceView.IRenderer interface to our managed
/// GlesViewportRenderer. The Android view system invokes OnSurface*/OnDrawFrame
/// on the GL render thread (not the UI thread). The IGL10/EGLConfig parameters
/// are required by the interface signature but unused (we call modern ES 3.1
/// directly through Silk.NET GLES bindings, not through the legacy GL10 emulator).
/// </summary>
public sealed class GlesRendererBridge : Java.Lang.Object, GLSurfaceView.IRenderer
{
    private readonly GlesViewportRenderer _renderer;
    private readonly Action? _surfaceCreated;

    public GlesRendererBridge(GlesViewportRenderer renderer, Action? surfaceCreated = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _surfaceCreated = surfaceCreated;
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config)
    {
        _renderer.OnSurfaceCreated();
        _surfaceCreated?.Invoke();
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height) => _renderer.OnSurfaceChanged(width, height);

    public void OnDrawFrame(IGL10? gl) => _renderer.OnDrawFrame();
}
