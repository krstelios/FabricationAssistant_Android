using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Renders the scene's view-space normals into an RG8 color attachment via
/// octahedral encoding, and writes depth into a GL_DEPTH_COMPONENT24 *texture*
/// (not renderbuffer) so the SSAO pass can sample it. Mirrors the desktop's
/// normal-depth pre-pass.
/// </summary>
public sealed class GlesNormalDepthRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private uint _fbo;
    private uint _normalTex;
    private uint _depthTex;
    private int _width;
    private int _height;

    /// <summary>Octahedron-encoded view-space normal (RG8).</summary>
    public uint NormalTexture => _normalTex;

    /// <summary>Depth attachment as a sampleable texture.</summary>
    public uint DepthTexture => _depthTex;

    public GlesNormalDepthRenderer(GL gl, string vertSource, string fragSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(_gl, "normal_depth", vertSource, fragSource);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _fbo != 0) return;
        DestroyResources();
        _width = width;
        _height = height;

        _normalTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _normalTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.RG8, (uint)width, (uint)height, 0,
                PixelFormat.RG, PixelType.UnsignedByte, (void*)0);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _depthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _depthTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.DepthComponent24, (uint)width, (uint)height, 0,
                PixelFormat.DepthComponent, PixelType.UnsignedInt, (void*)0);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _normalTex, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _depthTex, 0);
        var st = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (st != GLEnum.FramebufferComplete)
            Android.Util.Log.Error("FA.NormalDepth", $"FBO incomplete: 0x{(int)st:X4} ({width}x{height})");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Render(GpuScene scene, CameraState camera, int width, int height)
    {
        if (_fbo == 0) return;
        if (width != _width || height != _height) Resize(width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        // Clear normal RG to (0.5, 0.5) which decodes to "no orientation"
        // and depth to 1.0 (far plane). The SSAO shader skips pixels with
        // depth >= 0.999999, so unrendered fragments naturally drop out.
        _gl.ClearColor(0.5f, 0.5f, 0f, 0f);
        _gl.ClearDepth(1.0f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);

        _program.Use();

        float aspect = (float)_width / _height;
        var identity = ViewportCameraMath.IdentityModelMatrix();
        var identityN = ViewportCameraMath.NormalMatrixFromIdentity();
        var view = ViewportCameraMath.ViewMatrix(camera);
        var proj = ViewportCameraMath.ProjectionMatrix(camera, aspect);

        SetMat4("uView", view);
        SetMat4("uProjection", proj);

        int modelLoc = _gl.GetUniformLocation(_program.Handle, "uModel");
        int normalMatLoc = _gl.GetUniformLocation(_program.Handle, "uNormalMatrix");

        foreach (var mesh in scene.Meshes)
        {
            float[] model = mesh.WorldTransform ?? identity;
            if (modelLoc >= 0) _gl.UniformMatrix4(modelLoc, true, model);
            float[] nm = mesh.WorldTransform is null ? identityN : GlesRenderUtil.NormalMatrixFromWorld(mesh.WorldTransform);
            if (normalMatLoc >= 0) _gl.UniformMatrix3(normalMatLoc, true, nm);
            GlesRenderUtil.ApplyMeshCulling(_gl, mesh);
            mesh.Draw();
        }
        GlesRenderUtil.ResetMeshCulling(_gl);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void SetMat4(string name, float[] m)
    {
        int loc = _gl.GetUniformLocation(_program.Handle, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }

    private void DestroyResources()
    {
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        if (_normalTex != 0) { _gl.DeleteTexture(_normalTex); _normalTex = 0; }
        if (_depthTex != 0) { _gl.DeleteTexture(_depthTex); _depthTex = 0; }
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        DestroyResources();
        _program.Dispose();
    }
}
