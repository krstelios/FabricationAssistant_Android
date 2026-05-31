// Use only the Javax.Microedition.Khronos.Egl namespace - Android.Opengl
// also defines EGLConfig/EGLDisplay with the same names and importing both
// makes every reference ambiguous. GLSurfaceView.IEGLConfigChooser expects
// the Khronos types.
using GLSurfaceView = Android.Opengl.GLSurfaceView;
using Javax.Microedition.Khronos.Egl;

namespace FabricationAssistant.App.Android.Views;

/// <summary>
/// EGL config chooser for the default backbuffer. In this app it is always
/// constructed with 0 samples (scene MSAA is done in the offscreen
/// MsaaSceneFramebuffer, not the backbuffer), so it selects a compatible
/// single-sample RGB8 + Depth24 + Stencil8 config. The scene precision path is
/// the required D32FS8 offscreen FBO; forcing a Depth32 EGL backbuffer prevents
/// some Samsung/MediaTek drivers from creating the GLES context. The
/// higher-sample fallbacks remain only for the parameterized maxSamples path.
/// Replaces the simple
/// SetEGLConfigChooser overload, which cannot request EGL_SAMPLE_BUFFERS /
/// EGL_SAMPLES.
/// </summary>
public sealed class MultisampleConfigChooser : Java.Lang.Object, GLSurfaceView.IEGLConfigChooser
{
    // EGL_OPENGL_ES3_BIT_KHR - matches the ES 3.x context we request via
    // SetEGLContextClientVersion(3). Not in EGL10 constants, but the value
    // is stable across implementations.
    private const int EglOpenGlEs3Bit = 0x0040;
    private const int BackbufferDepthSize = 24;

    private readonly int _maxSamples;

    /// <param name="maxSamples">Highest MSAA level to attempt. 0 disables
    /// multisampling entirely. Values fall through to lower levels when
    /// the GPU/driver doesn't offer the requested count.</param>
    public MultisampleConfigChooser(int maxSamples = 4)
    {
        _maxSamples = maxSamples;
    }

    public EGLConfig? ChooseConfig(IEGL10? egl, EGLDisplay? display)
    {
        if (egl is null || display is null)
            return null;

        // Try the requested sample counts in descending order so we end up
        // with the highest MSAA the device supports below the configured cap.
        int[] order = _maxSamples switch
        {
            <= 0 => new[] { 0 },
            <= 2 => new[] { 2, 0 },
            _ => new[] { 4, 2, 0 },
        };
        foreach (int samples in order)
        {
            EGLConfig? config = TryFindConfig(egl, display, samples);
            if (config is not null) return config;
        }

        // S6-F4: no EGL config matched. Returning null makes GLSurfaceView throw an
        // opaque IllegalArgumentException with no context, so log what was requested
        // (RGB8 + Depth24 + Stencil8 + ES3, MSAA <= cap) to aid diagnosis on a
        // constrained driver.
        global::Android.Util.Log.Warn(
            "FA.Renderer",
            $"No EGL config matched (RGB888, Depth{BackbufferDepthSize}, Stencil8, ES3, MSAA<={_maxSamples}); GLSurfaceView config selection will fail.");
        return null;
    }

    private static EGLConfig? TryFindConfig(IEGL10 egl, EGLDisplay display, int samples)
    {
        var attrs = new[]
        {
            IEGL10.EglRedSize, 8,
            IEGL10.EglGreenSize, 8,
            IEGL10.EglBlueSize, 8,
            IEGL10.EglAlphaSize, 0,
            IEGL10.EglDepthSize, BackbufferDepthSize,
            IEGL10.EglStencilSize, 8,
            IEGL10.EglRenderableType, EglOpenGlEs3Bit,
            IEGL10.EglSampleBuffers, samples > 0 ? 1 : 0,
            IEGL10.EglSamples, samples,
            IEGL10.EglNone,
        };

        var configCount = new int[1];
        if (!egl.EglChooseConfig(display, attrs, null, 0, configCount) || configCount[0] <= 0)
            return null;

        var configs = new EGLConfig[configCount[0]];
        if (!egl.EglChooseConfig(display, attrs, configs, configs.Length, configCount) || configCount[0] <= 0)
            return null;

        // Pick the first config that matches the requested sample count. EGL is
        // allowed to return configs with more samples than asked; we want an
        // exact match so 2x doesn't accidentally select the 4x driver path.
        EGLConfig? fallback = null;
        foreach (var c in configs)
        {
            if (c is null) continue;
            if (!HasMinimumAttributes(egl, display, c))
                continue;

            fallback ??= c;
            if (GetConfigAttrib(egl, display, c, IEGL10.EglSamples) >= samples)
            {
                return c;
            }
        }
        return fallback;
    }

    private static bool HasMinimumAttributes(IEGL10 egl, EGLDisplay display, EGLConfig config)
        => GetConfigAttrib(egl, display, config, IEGL10.EglRedSize) >= 8
           && GetConfigAttrib(egl, display, config, IEGL10.EglGreenSize) >= 8
           && GetConfigAttrib(egl, display, config, IEGL10.EglBlueSize) >= 8
           && GetConfigAttrib(egl, display, config, IEGL10.EglDepthSize) >= BackbufferDepthSize
           && GetConfigAttrib(egl, display, config, IEGL10.EglStencilSize) >= 8;

    private static int GetConfigAttrib(IEGL10 egl, EGLDisplay display, EGLConfig config, int attribute)
    {
        var value = new int[1];
        return egl.EglGetConfigAttrib(display, config, attribute, value) ? value[0] : 0;
    }
}
