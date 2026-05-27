using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Owns the offscreen R32UI framebuffer and pick shader. Renders the scene
/// with per-mesh uMeshIndex as the fragment output, then reads back the pixel
/// at the tap location to recover which mesh was hit. Index 0 is the
/// background / no-hit value (set by glClear).
/// Mesh IDs are encoded as 1-based unsigned integers because R32UI cannot
/// distinguish "mesh 0" from the cleared no-hit value.
///
/// All entry points must run on the GL render thread. The host typically
/// calls Pick from a render-thread command queued via
/// ViewportSurfaceView.QueueRendererCommand.
/// </summary>
public sealed class GlesPickRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private uint _fbo;
    private uint _colorTexture;
    private uint _depthRb;
    private int _width;
    private int _height;
    private string? _lastFramebufferError;
    private bool _loggedFramebufferUnavailable;
    private readonly float[] _sectionUniformScratch = new float[32];
    private HashSet<int> _xrayBackgroundNodeIdLookup = new();
    public IReadOnlyList<GlesSectionPlane> SectionPlanes { get; set; } = Array.Empty<GlesSectionPlane>();

    public IReadOnlyList<int> XrayBackgroundNodeIds
    {
        set => _xrayBackgroundNodeIdLookup = value is null
            ? new HashSet<int>()
            : value.ToHashSet();
    }

    public GlesPickRenderer(GL gl, string vertSource, string fragSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(_gl, "pick", vertSource, fragSource);
    }

    /// <summary>
    /// (Re)allocates the R32UI color texture and D24 depth renderbuffer at the
    /// given size. Called from GlesViewportRenderer.OnSurfaceChanged so the
    /// pick FBO tracks the main viewport size.
    /// </summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _fbo != 0) return;

        DestroyResources();

        _width = width;
        _height = height;

        try
        {
            _colorTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
            unsafe
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    level: 0,
                    InternalFormat.R32ui,
                    (uint)width,
                    (uint)height,
                    border: 0,
                    PixelFormat.RedInteger,
                    PixelType.UnsignedInt,
                    pixels: (void*)0);
            }
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            _depthRb = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
            _gl.RenderbufferStorage(
                RenderbufferTarget.Renderbuffer,
                InternalFormat.DepthComponent24,
                (uint)width,
                (uint)height);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

            _fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _colorTexture,
                level: 0);
            _gl.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer,
                _depthRb);

            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                _lastFramebufferError = $"Pick FBO incomplete: 0x{(int)status:X4} ({width}x{height})";
                _loggedFramebufferUnavailable = false;
                Android.Util.Log.Error("FA.Pick", _lastFramebufferError);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                DestroyResources();
                return;
            }
            _lastFramebufferError = null;
            _loggedFramebufferUnavailable = false;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        catch
        {
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            DestroyResources();
            throw;
        }
    }

    /// <summary>
    /// Renders the scene to the pick FBO using mesh indices as fragment output
    /// and reads back the pixel at (x, viewportY) where viewportY is in
    /// Android (top-down) coordinates. Returns the 1-based mesh index that
    /// was hit, or null when the background was hit.
    /// </summary>
    public unsafe int? Pick(int x, int y, GpuScene scene, CameraState camera)
    {
        if (_fbo == 0 || _width == 0 || _height == 0)
        {
            if (!_loggedFramebufferUnavailable && _lastFramebufferError is not null)
            {
                Android.Util.Log.Warn("FA.Pick", "Pick unavailable after framebuffer setup failure: " + _lastFramebufferError);
                _loggedFramebufferUnavailable = true;
            }
            return null;
        }
        if (x < 0 || y < 0 || x >= _width || y >= _height) return null;

        // glReadPixels uses bottom-up Y. Tap input is top-down. Flip before
        // rendering so the pick pass can scissor to the one pixel being read.
        int glY = _height - 1 - y;
        if (glY < 0 || glY >= _height)
            return null;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(x, glY, 1u, 1u);

        try
        {
            uint clear = 0u;
            _gl.ClearBuffer(GLEnum.Color, 0, &clear);
            _gl.Clear((uint)ClearBufferMask.DepthBufferBit);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);

            _program.Use();

            float aspect = (float)_width / _height;
            var identity = ViewportCameraMath.IdentityModelMatrix();
            var view = ViewportCameraMath.ViewMatrix(camera);
            var proj = ViewportCameraMath.ProjectionMatrix(camera, aspect);

            SetMat4("uView", view);
            SetMat4("uProjection", proj);
            SetSectionUniforms(_program);

            int modelLoc = _gl.GetUniformLocation(_program.Handle, "uModel");
            int indexLoc = _gl.GetUniformLocation(_program.Handle, "uMeshIndex");
            foreach (var mesh in scene.Meshes)
            {
                if (!mesh.Visible && !IsXrayBackgroundMesh(mesh))
                    continue;

                float[] model = mesh.WorldTransform ?? identity;
                if (modelLoc >= 0)
                    _gl.UniformMatrix4(modelLoc, true, model);
                if (indexLoc >= 0)
                    _gl.Uniform1(indexLoc, (uint)mesh.MeshIndex);
                GlesRenderUtil.ApplyMeshCulling(_gl, mesh);
                mesh.Draw();
            }
            GlesRenderUtil.ResetMeshCulling(_gl);

            uint pixel = 0u;
            DrainGlErrors("before-readpixels");
            _gl.Flush();
            _gl.ReadPixels(x, glY, 1u, 1u, PixelFormat.RedInteger, PixelType.UnsignedInt, &pixel);
            DrainGlErrors("readpixels");

            return pixel == 0u ? null : (int)pixel;
        }
        finally
        {
            GlesRenderUtil.ResetMeshCulling(_gl);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0u);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
    }

    private bool IsXrayBackgroundMesh(GpuMesh mesh)
        => mesh.SourceNodeId >= 0 && _xrayBackgroundNodeIdLookup.Contains(mesh.SourceNodeId);

    private void SetMat4(string name, float[] m)
    {
        int loc = _gl.GetUniformLocation(_program.Handle, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }

    private void SetSectionUniforms(ShaderProgram program)
    {
        int count = System.Math.Min(SectionPlanes.Count, 8);
        int countLoc = program.UniformLocation("uSectionPlaneCount");
        if (countLoc >= 0)
            _gl.Uniform1(countLoc, count);

        int planesLoc = program.UniformArrayLocation("uSectionPlanes");
        if (planesLoc < 0 || count <= 0)
            return;

        float[] values = _sectionUniformScratch;
        Array.Clear(values, 0, values.Length);
        for (int i = 0; i < count; i++)
        {
            GlesSectionPlane plane = SectionPlanes[i];
            int offset = i * 4;
            values[offset + 0] = plane.NormalX;
            values[offset + 1] = plane.NormalY;
            values[offset + 2] = plane.NormalZ;
            values[offset + 3] = plane.Offset;
        }

        unsafe
        {
            fixed (float* ptr = values)
                _gl.Uniform4(planesLoc, (uint)count, ptr);
        }
    }

    private void DestroyResources()
    {
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        if (_colorTexture != 0) { _gl.DeleteTexture(_colorTexture); _colorTexture = 0; }
        if (_depthRb != 0) { _gl.DeleteRenderbuffer(_depthRb); _depthRb = 0; }
        _width = 0;
        _height = 0;
    }

    private void DrainGlErrors(string reason)
    {
        for (int i = 0; i < 32; i++)
        {
            GLEnum error = _gl.GetError();
            if (error == GLEnum.NoError)
                return;

            Android.Util.Log.Warn("FA.Pick", $"GL error during {reason}: 0x{(int)error:X4}");
        }

        Android.Util.Log.Warn("FA.Pick", $"GL error drain reached cap during {reason}.");
    }

    public void Dispose()
    {
        DestroyResources();
        _program.Dispose();
    }
}
