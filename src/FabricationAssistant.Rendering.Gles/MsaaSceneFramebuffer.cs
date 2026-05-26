using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Offscreen multisample framebuffer for the Android viewport. Owns a
/// multisample color renderbuffer (RGB8) + multisample depth-stencil
/// renderbuffer (D24S8) attached to a single FBO. The renderer draws every
/// non-post-process pass into this FBO, then blit-resolves the color
/// attachment to FBO 0 before the selection outline composite.
///
/// When the requested sample count is &lt;= 1, single-sample storage is used
/// (still through this wrapper) so the render path stays uniform: scene
/// always renders into the FBO, then blits to default. The stencil
/// attachment is included regardless of sample count because Plan 3A's
/// section-cap algorithm depends on it.
/// </summary>
public sealed partial class MsaaSceneFramebuffer : IDisposable
{
    private readonly GL _gl;
    private uint _fbo;
    private uint _colorRbo;
    private uint _depthStencilRbo;
    private int _width;
    private int _height;
    private int _samples;
    private int _maxSamples = -1;
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
        if (_fbo != 0 && _width == width && _height == height && _samples == clamped)
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
        if (_fbo != 0 && _width == width && _height == height && _samples == clamped)
            return;

        Destroy();
        _width = width;
        _height = height;
        _samples = clamped;

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRbo);
        // RenderbufferStorageMultisample with samples == 1 is legal in
        // GLES 3.x but implementations may silently treat it differently.
        // Take the explicit single-sample path when samples <= 1.
        if (clamped > 1)
        {
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
                (uint)clamped, InternalFormat.Rgb8, (uint)width, (uint)height);
        }
        else
        {
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                InternalFormat.Rgb8, (uint)width, (uint)height);
        }
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _colorRbo);

        _depthStencilRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthStencilRbo);
        if (clamped > 1)
        {
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
                (uint)clamped, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        }
        else
        {
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        }
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthStencilRbo);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        // Unbind before any further work so a successful path and the throw
        // path leave the same clean binding state (renderbuffer = 0, FBO = 0).
        // Otherwise a caller that catches the exception inherits bindings
        // pointing at the just-deleted handles, which is driver-undefined.
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Destroy();
            throw new InvalidOperationException(
                $"MsaaSceneFramebuffer: FBO incomplete after attachment, status = 0x{(int)status:X4}");
        }
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
    /// Blit-resolves the multisample color attachment into FBO 0 (the
    /// default backbuffer) at the same dimensions. Depth and stencil are
    /// not resolved - the selection outline post-process only reads color.
    /// </summary>
    public bool TryResolveToDefault()
    {
        LastResolveError = GLEnum.NoError;
        if (_fbo == 0 || _width <= 0 || _height <= 0) return true;
        DrainGlErrors();
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.ReadBuffer(GLEnum.ColorAttachment0);
        _gl.BlitFramebuffer(
            0, 0, _width, _height,
            0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        LastResolveError = _gl.GetError();
        // Restore the standard binding so subsequent draws hit the default
        // framebuffer (the selection outline runs after Resolve).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        Destroy();
        _disposed = true;
    }
}
