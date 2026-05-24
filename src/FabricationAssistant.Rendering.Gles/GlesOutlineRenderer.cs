using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Selection-outline post-process. Renders only the currently selected mesh
/// into a single-channel R8 mask FBO, then runs a Sobel-style edge-detect
/// shader as a fullscreen pass that alpha-blends the outline color over the
/// default framebuffer. The mesh shader's inline color highlight is the
/// fallback when SceneAppearance.OutlineEnabled is false.
/// </summary>
public sealed class GlesOutlineRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _maskProgram;
    private readonly ShaderProgram _outlineProgram;
    private readonly uint _fullscreenVao;

    private uint _maskFbo;
    private uint _maskTex;
    private int _width;
    private int _height;

    public GlesOutlineRenderer(
        GL gl,
        string maskVertSource,
        string maskFragSource,
        string fullscreenVertSource,
        string outlineFragSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _maskProgram = new ShaderProgram(_gl, "outline.mask", maskVertSource, maskFragSource);
        _outlineProgram = new ShaderProgram(_gl, "outline.composite", fullscreenVertSource, outlineFragSource);
        _fullscreenVao = _gl.GenVertexArray();
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _maskFbo != 0) return;
        DestroyResources();
        _width = width;
        _height = height;

        _maskTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _maskTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.R8, (uint)width, (uint)height,
                0, PixelFormat.Red, PixelType.UnsignedByte, (void*)0);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _maskFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _maskTex, 0);
        var st = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (st != GLEnum.FramebufferComplete)
            Android.Util.Log.Error("FA.Outline", $"Mask FBO incomplete: 0x{(int)st:X4}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Draws the outline composite onto the currently-bound framebuffer
    /// (must be the default surface FBO when called from
    /// GlesViewportRenderer.OnDrawFrame). No-op when no mesh is selected
    /// or the outline thickness collapses to zero.
    /// </summary>
    public void Render(GpuScene scene, CameraState camera, int selectedMeshIndex,
        float[] outlineColor, float thicknessPx, int width, int height)
    {
        if (selectedMeshIndex <= 0 || _maskFbo == 0 || width <= 0 || height <= 0) return;
        if (width != _width || height != _height) Resize(width, height);

        // Find the selected mesh up-front so we skip the mask pass entirely
        // if the selection is stale (mesh deleted, scene replaced, etc).
        GpuMesh? selected = null;
        foreach (var m in scene.Meshes)
        {
            if (m.MeshIndex == selectedMeshIndex) { selected = m; break; }
        }
        if (selected is null) return;

        // ── Mask pass ────────────────────────────────────────────────
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        _gl.Disable(EnableCap.DepthTest);

        _maskProgram.Use();
        float aspect = (float)_width / _height;
        var view = ViewportCameraMath.ViewMatrix(camera);
        var proj = ViewportCameraMath.ProjectionMatrix(camera, aspect);
        var identity = ViewportCameraMath.IdentityModelMatrix();
        SetMat4(_maskProgram, "uView", view);
        SetMat4(_maskProgram, "uProjection", proj);
        int modelLoc = _gl.GetUniformLocation(_maskProgram.Handle, "uModel");
        float[] model = selected.WorldTransform ?? identity;
        if (modelLoc >= 0) _gl.UniformMatrix4(modelLoc, true, model);
        GlesRenderUtil.ApplyMeshCulling(_gl, selected);
        selected.Draw();
        GlesRenderUtil.ResetMeshCulling(_gl);

        // ── Composite pass ──────────────────────────────────────────
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _outlineProgram.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _maskTex);
        SetInt(_outlineProgram, "uMask", 0);
        SetVec2(_outlineProgram, "uTexelSize", 1f / _width, 1f / _height);
        SetVec3(_outlineProgram, "uOutlineColor", outlineColor[0], outlineColor[1], outlineColor[2]);
        SetFloat(_outlineProgram, "uThicknessPx", thicknessPx);

        _gl.BindVertexArray(_fullscreenVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.Blend);
    }

    private void SetMat4(ShaderProgram p, string name, float[] m)
    {
        int loc = _gl.GetUniformLocation(p.Handle, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }
    private void SetVec2(ShaderProgram p, string name, float x, float y)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform2(loc, x, y); }
    private void SetVec3(ShaderProgram p, string name, float x, float y, float z)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform3(loc, x, y, z); }
    private void SetFloat(ShaderProgram p, string name, float v)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform1(loc, v); }
    private void SetInt(ShaderProgram p, string name, int v)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform1(loc, v); }

    private void DestroyResources()
    {
        if (_maskFbo != 0) { _gl.DeleteFramebuffer(_maskFbo); _maskFbo = 0; }
        if (_maskTex != 0) { _gl.DeleteTexture(_maskTex); _maskTex = 0; }
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        DestroyResources();
        if (_fullscreenVao != 0) _gl.DeleteVertexArray(_fullscreenVao);
        _maskProgram.Dispose();
        _outlineProgram.Dispose();
    }
}
