using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Selection-outline post-process. Renders the currently selected mesh set
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
    private readonly uint _fullscreenVbo;

    private uint _maskFbo;
    private uint _maskTex;
    private int _width;
    private int _height;
    private string? _lastFramebufferError;
    private bool _loggedFramebufferUnavailable;
    private readonly float[] _viewScratch = new float[16];
    private readonly float[] _projectionScratch = new float[16];
    private readonly float[] _sectionUniformScratch = new float[32];
    public IReadOnlyList<GlesSectionPlane> SectionPlanes { get; set; } = Array.Empty<GlesSectionPlane>();

    public GlesOutlineRenderer(
        GL gl,
        string maskVertSource,
        string maskFragSource,
        string fullscreenVertSource,
        string outlineFragSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        ShaderProgram? maskProgram = null;
        ShaderProgram? outlineProgram = null;
        uint fullscreenVao = 0;
        uint fullscreenVbo = 0;
        try
        {
            maskProgram = new ShaderProgram(_gl, "outline.mask", maskVertSource, maskFragSource);
            outlineProgram = new ShaderProgram(_gl, "outline.composite", fullscreenVertSource, outlineFragSource);
            (fullscreenVao, fullscreenVbo) = GlesFullscreenTriangle.Create(_gl);

            _maskProgram = maskProgram;
            _outlineProgram = outlineProgram;
            _fullscreenVao = fullscreenVao;
            _fullscreenVbo = fullscreenVbo;
        }
        catch
        {
            if (fullscreenVbo != 0) _gl.DeleteBuffer(fullscreenVbo);
            if (fullscreenVao != 0) _gl.DeleteVertexArray(fullscreenVao);
            outlineProgram?.Dispose();
            maskProgram?.Dispose();
            throw;
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _maskFbo != 0) return;
        DestroyResources();
        _width = width;
        _height = height;

        try
        {
            _maskTex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _maskTex);
            unsafe
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0,
                    InternalFormat.R8, (uint)width, (uint)height,
                    0, PixelFormat.Red, PixelType.UnsignedByte, (void*)0);
            }
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            _maskFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _maskTex, 0);
            var st = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (st != GLEnum.FramebufferComplete)
            {
                _lastFramebufferError = $"Outline mask FBO incomplete: 0x{(int)st:X4} ({width}x{height})";
                _loggedFramebufferUnavailable = false;
                Android.Util.Log.Error("FA.Outline", _lastFramebufferError);
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
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            DestroyResources();
            throw;
        }
    }

    public void TrimFramebuffers()
        => DestroyResources();

    /// <summary>
    /// Draws the outline composite onto the currently-bound framebuffer
    /// (must be the default surface FBO when called from
    /// GlesViewportRenderer.OnDrawFrame). No-op when no mesh is selected
    /// or the outline thickness collapses to zero.
    /// </summary>
    public void Render(GpuScene scene, CameraState camera, int selectedMeshIndex,
        float[] outlineColor, float thicknessPx, int width, int height)
        => Render(
            scene,
            camera,
            selectedMeshIndex > 0 ? new[] { selectedMeshIndex } : Array.Empty<int>(),
            outlineColor,
            thicknessPx,
            width,
            height);

    /// <summary>
    /// Draws one outline around a logical selection that may contain multiple
    /// rendered meshes, such as an assembly selected from the Model Explorer.
    /// </summary>
    public void Render(GpuScene scene, CameraState camera, IReadOnlyCollection<int> selectedMeshIndices,
        float[] outlineColor, float thicknessPx, int width, int height)
    {
        if (selectedMeshIndices is null || selectedMeshIndices.Count == 0 || width <= 0 || height <= 0) return;
        if (width != _width || height != _height || _maskFbo == 0)
            Resize(width, height);
        if (_maskFbo == 0)
        {
            if (!_loggedFramebufferUnavailable && _lastFramebufferError is not null)
            {
                Android.Util.Log.Warn("FA.Outline", "Outline unavailable after framebuffer setup failure: " + _lastFramebufferError);
                _loggedFramebufferUnavailable = true;
            }
            return;
        }

        var selectedLookup = new HashSet<int>();
        foreach (int selectedMeshIndex in selectedMeshIndices)
        {
            if (selectedMeshIndex > 0)
                selectedLookup.Add(selectedMeshIndex);
        }
        if (selectedLookup.Count == 0) return;

        var selectedMeshes = new List<GpuMesh>(selectedLookup.Count);
        foreach (GpuMesh mesh in scene.Meshes)
        {
            if (mesh.Visible && selectedLookup.Contains(mesh.MeshIndex))
                selectedMeshes.Add(mesh);
        }
        if (selectedMeshes.Count == 0) return;

        try
        {
            // Mask pass.
            // S7/19-F4 (decide-intent): the mask FBO is colour-only and the mask
            // draw runs with depth test OFF, so the outline traces the full
            // silhouette of the selected bodies even where another body occludes
            // them. This is intentional - the selection outline stays visible
            // through occluders so the user can see what is selected. It is
            // knowingly inconsistent with the depth-tested inline fill highlight.
            // Making the outline depth-consistent is NOT a simple "attach a depth
            // renderbuffer" change: depth-testing only the selected meshes here
            // would merely self-occlude them, not clip by other bodies. A correct
            // fix would have to share the full scene depth into this FBO (or run a
            // depth pre-pass of all visible meshes) and be verified visually on
            // device; deferred unless occluded-hidden outlines become a requirement.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear((uint)ClearBufferMask.ColorBufferBit);
            _gl.Disable(EnableCap.DepthTest);

            _maskProgram.Use();
            float aspect = (float)_width / _height;
            ViewportCameraMath.FillViewMatrix(camera, _viewScratch);
            ViewportCameraMath.FillProjectionMatrix(camera, aspect, _projectionScratch);
            var identity = ViewportCameraMath.IdentityModelMatrix();
            SetMat4(_maskProgram, "uView", _viewScratch);
            SetMat4(_maskProgram, "uProjection", _projectionScratch);
            SetSectionUniforms(_maskProgram);
            int modelLoc = _maskProgram.UniformLocation("uModel");
            foreach (GpuMesh selected in selectedMeshes)
            {
                float[] model = selected.WorldTransform ?? identity;
                if (modelLoc >= 0) _gl.UniformMatrix4(modelLoc, true, model);
                GlesRenderUtil.ApplyMeshCulling(_gl, selected);
                selected.Draw();
            }
            GlesRenderUtil.ResetMeshCulling(_gl);

            // Composite pass.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(0, 0, (uint)width, (uint)height);

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);

            _outlineProgram.Use();
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _maskTex);
            SetInt(_outlineProgram, "uMask", 0);
            SetVec2(_outlineProgram, "uTexelSize", 1f / _width, 1f / _height);
            SetVec3(_outlineProgram, "uOutlineColor", outlineColor[0], outlineColor[1], outlineColor[2]);
            SetFloat(_outlineProgram, "uThicknessPx", thicknessPx);

            GlesFullscreenTriangle.Draw(_gl, _fullscreenVao);
        }
        finally
        {
            GlesRenderUtil.ResetMeshCulling(_gl);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.CullFace);
        }
    }

    private void SetMat4(ShaderProgram p, string name, float[] m)
    {
        int loc = p.UniformLocation(name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }
    private void SetVec2(ShaderProgram p, string name, float x, float y)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform2(loc, x, y); }
    private void SetVec3(ShaderProgram p, string name, float x, float y, float z)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform3(loc, x, y, z); }
    private void SetFloat(ShaderProgram p, string name, float v)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform1(loc, v); }
    private void SetInt(ShaderProgram p, string name, int v)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform1(loc, v); }

    private void SetSectionUniforms(ShaderProgram p)
    {
        int count = System.Math.Min(SectionPlanes.Count, 8);
        int countLoc = p.UniformLocation("uSectionPlaneCount");
        if (countLoc >= 0)
            _gl.Uniform1(countLoc, count);

        int planesLoc = p.UniformArrayLocation("uSectionPlanes");
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
        if (_maskFbo != 0) { _gl.DeleteFramebuffer(_maskFbo); _maskFbo = 0; }
        if (_maskTex != 0) { _gl.DeleteTexture(_maskTex); _maskTex = 0; }
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        DestroyResources();
        if (_fullscreenVao != 0) _gl.DeleteVertexArray(_fullscreenVao);
        if (_fullscreenVbo != 0) _gl.DeleteBuffer(_fullscreenVbo);
        _maskProgram.Dispose();
        _outlineProgram.Dispose();
    }
}
