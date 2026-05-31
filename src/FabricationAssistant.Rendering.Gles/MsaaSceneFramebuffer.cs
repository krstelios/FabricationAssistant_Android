using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Offscreen multisample framebuffer for the Android viewport. Owns a
/// multisample color renderbuffer (RGB8) + multisample depth-stencil
/// renderbuffer (D32FS8) attached to a single FBO. The renderer draws every
/// non-post-process pass into this FBO, then blit-resolves the color
/// attachment to FBO 0 before the selection outline composite.
///
/// When the requested sample count is &lt;= 1, single-sample storage is used
/// (still through this wrapper) so the render path stays uniform: scene
/// always renders into the FBO, then blits to default. The stencil
/// attachment is included regardless of sample count because section caps
/// use the stencil buffer for their projected contour mask.
/// </summary>
public sealed partial class MsaaSceneFramebuffer : IDisposable
{
    private const InternalFormat PreferredDepthStencilFormat = InternalFormat.Depth32fStencil8;

    private readonly GL _gl;
    private uint _fbo;
    private uint _colorRbo;
    private uint _depthStencilRbo;
    private int _width;
    private int _height;
    private int _samples;
    private int _requestedSamples;
    private int _maxSamples = -1;
    private string _lastDepthLogKey = "";
    private bool _disposed;

    public MsaaSceneFramebuffer(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
    }

    public uint FboHandle => _fbo;
    public int Width => _width;
    public int Height => _height;
    public int Samples => _samples;
    public int MaxSamples => _maxSamples;
    public InternalFormat DepthStencilFormat { get; private set; } = PreferredDepthStencilFormat;
    public int DepthBits => 32;
    public GLEnum LastResolveError { get; private set; } = GLEnum.NoError;

    /// <summary>
    /// Allocates (or re-allocates) the FBO + renderbuffers at the given
    /// dimensions and sample count. Re-uses the existing allocation when
    /// (width, height, samples) all match - safe to call every frame.
    /// The hardware GL_MAX_SAMPLES query is cached after the first call so
    /// no per-frame glGetIntegerv pipeline stall occurs on the steady-state
    /// no-op path.
    /// Throws <see cref="InvalidOperationException"/> if the FBO is
    /// incomplete after attachment.
    /// </summary>
    public unsafe void Ensure(int width, int height, int samples)
    {
        if (width <= 0 || height <= 0)
        {
            Destroy();
            return;
        }

        int clamped = _maxSamples >= 0
            ? ClampSamplesToHardwareLimit(samples, _maxSamples)
            : ClampSamples(samples);

        // Fast path: steady-state no-op. Hits every frame after the first
        // allocation, so it must avoid all GL calls (especially glGetIntegerv
        // which can flush the pipeline on some drivers).
        if (_fbo != 0 && _width == width && _height == height && _requestedSamples == clamped)
            return;

        // Query the hardware max sample count once and cache it. GL_MAX_SAMPLES
        // is constant per device, so re-querying every frame is wasted work.
        if (_maxSamples < 0)
        {
            int q = 0;
            _gl.GetInteger(GLEnum.MaxSamples, &q);
            _maxSamples = q;
        }
        clamped = ClampSamplesToHardwareLimit(samples, _maxSamples);

        // Re-check after hardware clamp. If the request and previous frame
        // both resolve to the same Android-supported sample count, there is
        // no FBO work to do.
        if (_fbo != 0 && _width == width && _height == height && _requestedSamples == clamped)
            return;

        try
        {
            Destroy();

            string? lastFailure = null;
            Exception? firstException = null;
            foreach (int actualSamples in SampleFallbackOrder(clamped))
            {
                DrainGlErrors();
                try
                {
                    if (TryAllocate(width, height, actualSamples, PreferredDepthStencilFormat, out lastFailure))
                    {
                        _width = width;
                        _height = height;
                        _samples = actualSamples;
                        _requestedSamples = clamped;
                        DepthStencilFormat = PreferredDepthStencilFormat;
                        LogDepthSelection(width, height, actualSamples, clamped);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    firstException ??= ex;
                    lastFailure = ex.GetBaseException().Message;
                }

                _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                Destroy();
            }

            throw new InvalidOperationException(
                "MsaaSceneFramebuffer: FBO incomplete with required Depth32FStencil8"
                + (string.IsNullOrWhiteSpace(lastFailure) ? "." : $": {lastFailure}"),
                firstException);
        }
        catch
        {
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Destroy();
            throw;
        }
    }

    private static int[] SampleFallbackOrder(int clampedSamples)
    {
        clampedSamples = System.Math.Max(1, clampedSamples);
        int[] candidates = [clampedSamples, 8, 4, 2, 1];
        var result = new List<int>(candidates.Length);
        foreach (int candidate in candidates)
        {
            if (candidate <= clampedSamples && !result.Contains(candidate))
                result.Add(candidate);
        }

        return result.ToArray();
    }

    private bool TryAllocate(
        int width,
        int height,
        int actualSamples,
        InternalFormat depthStencilFormat,
        out string? failureReason)
    {
        failureReason = null;
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRbo);
        // RenderbufferStorageMultisample with samples == 1 is legal in
        // GLES 3.x but implementations may silently treat it differently.
        // Take the explicit single-sample path when samples <= 1.
        AllocateRenderbuffer(InternalFormat.Rgb8, width, height, actualSamples);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _colorRbo);

        _depthStencilRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthStencilRbo);
        AllocateRenderbuffer(depthStencilFormat, width, height, actualSamples);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthStencilRbo);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        var error = _gl.GetError();
        // Unbind before any further work so a successful path and the failure
        // path leave the same clean binding state (renderbuffer = 0, FBO = 0).
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status == GLEnum.FramebufferComplete && error == GLEnum.NoError)
            return true;

        failureReason = $"format={depthStencilFormat}, samples={actualSamples}, status=0x{(int)status:X4}, glError=0x{(int)error:X4}";
        return false;
    }

    private void AllocateRenderbuffer(InternalFormat format, int width, int height, int clampedSamples)
    {
        if (clampedSamples > 1)
        {
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
                (uint)clampedSamples, format, (uint)width, (uint)height);
        }
        else
        {
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                format, (uint)width, (uint)height);
        }
    }

    private void LogDepthSelection(int width, int height, int actualSamples, int requestedSamples)
    {
        string key = $"{width}x{height}:{PreferredDepthStencilFormat}:{actualSamples}:{requestedSamples}";
        if (key == _lastDepthLogKey)
            return;

        _lastDepthLogKey = key;
        string message = $"Scene framebuffer depth={PreferredDepthStencilFormat}, samples={actualSamples}, requestedSamples={requestedSamples}, viewport={width}x{height}.";
        if (actualSamples == requestedSamples)
            Android.Util.Log.Info("FA.Renderer", message);
        else
            Android.Util.Log.Warn("FA.Renderer", message + " Sample count was reduced; 32-bit depth is still required.");
    }

    /// <summary>
    /// Binds this FBO as the current draw target. Caller is responsible
    /// for setting the viewport and clearing color/depth/stencil after
    /// binding.
    /// </summary>
    public void Bind()
    {
        if (_fbo == 0) return;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
    }

    /// <summary>
    /// Blit-resolves the multisample color attachment into FBO 0 (the default
    /// backbuffer). See <see cref="TryResolveTo"/>.
    /// </summary>
    public bool TryResolveToDefault() => TryResolveTo(0);

    /// <summary>
    /// Blit-resolves the multisample color attachment into <paramref name="targetFbo"/>
    /// (0 = default backbuffer) at the same dimensions, and leaves that FBO bound so
    /// the post-process passes (silhouette/outline, then optional FXAA) draw into it.
    /// Depth and stencil are not resolved - those passes only read color.
    /// </summary>
    public bool TryResolveTo(uint targetFbo)
    {
        LastResolveError = GLEnum.NoError;
        if (_fbo == 0 || _width <= 0 || _height <= 0) return true;
        DrainGlErrors();
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetFbo);
        _gl.ReadBuffer(GLEnum.ColorAttachment0);
        _gl.BlitFramebuffer(
            0, 0, _width, _height,
            0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        LastResolveError = _gl.GetError();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        return LastResolveError == GLEnum.NoError;
    }

    public void ResolveToDefault()
    {
        TryResolveToDefault();
    }

    public void Reset()
    {
        _maxSamples = -1;
        LastResolveError = GLEnum.NoError;
        Destroy();
    }

    private void DrainGlErrors()
    {
        for (int i = 0; i < 32; i++)
        {
            if (_gl.GetError() == GLEnum.NoError)
                return;
        }
    }

    /// <summary>
    /// Releases all GL resources. Safe to call when nothing is allocated.
    /// Must be called on the GL render thread.
    /// </summary>
    public void Destroy()
    {
        if (_colorRbo != 0) { _gl.DeleteRenderbuffer(_colorRbo); _colorRbo = 0; }
        if (_depthStencilRbo != 0) { _gl.DeleteRenderbuffer(_depthStencilRbo); _depthStencilRbo = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        _width = 0;
        _height = 0;
        _samples = 0;
        _requestedSamples = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Destroy();
        _disposed = true;
    }
}
