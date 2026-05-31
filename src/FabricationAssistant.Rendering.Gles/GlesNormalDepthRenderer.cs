using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Renders the scene's view-space normals plus linear view depth into a single
/// RGBA8 color attachment. RG stores octahedral view normals; BA stores packed
/// depth for the orthographic SSAO path. Perspective SSAO samples the real
/// depth attachment. Android uses a required 32-bit float depth texture here
/// so thin, close faces share the same precision policy as the scene FBO.
/// </summary>
public sealed class GlesNormalDepthRenderer : IDisposable
{
    private const InternalFormat PreferredDepthFormat = InternalFormat.DepthComponent32f;

    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private uint _fbo;
    private uint _normalTex;
    private uint _depthTex;
    private int _width;
    private int _height;
    private string? _lastFramebufferError;
    private bool _loggedFramebufferUnavailable;
    private string _lastDepthLogKey = "";
    private float _linearDepthMin;
    private float _linearDepthMax = 1f;
    private readonly float[] _viewScratch = new float[16];
    private readonly float[] _projectionScratch = new float[16];
    private readonly float[] _sectionUniformScratch = new float[32];

    /// <summary>Octahedron-encoded view-space normal in RG, packed depth in BA.</summary>
    public uint NormalTexture => _normalTex;

    /// <summary>Depth attachment as a sampleable texture.</summary>
    public uint DepthTexture => _depthTex;
    public InternalFormat DepthFormat { get; private set; } = PreferredDepthFormat;
    public int DepthBits => 32;

    public GlesNormalDepthRenderInfo LastRenderInfo { get; private set; }
    public float LinearDepthMin => _linearDepthMin;
    public float LinearDepthMax => _linearDepthMax;
    public IReadOnlyList<GlesSectionPlane> SectionPlanes { get; set; } = Array.Empty<GlesSectionPlane>();

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

        try
        {
            string? lastFailure = null;
            try
            {
                DrainGlErrors();
                if (TryAllocateFramebuffer(width, height, PreferredDepthFormat, out lastFailure))
                {
                    _width = width;
                    _height = height;
                    DepthFormat = PreferredDepthFormat;
                    _lastFramebufferError = null;
                    _loggedFramebufferUnavailable = false;
                    LogDepthSelection(width, height);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lastFailure = ex.GetBaseException().Message;
            }

            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            DestroyResources();
            _lastFramebufferError =
                "Normal/depth FBO incomplete with required DepthComponent32F"
                + (string.IsNullOrWhiteSpace(lastFailure) ? "." : $": {lastFailure}");
            _loggedFramebufferUnavailable = false;
            Android.Util.Log.Error("FA.NormalDepth", _lastFramebufferError);
        }
        catch
        {
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            DestroyResources();
            throw;
        }
    }

    private bool TryAllocateFramebuffer(
        int width,
        int height,
        InternalFormat depthFormat,
        out string? failureReason)
    {
        failureReason = null;
        _normalTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _normalTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _depthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _depthTex);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                depthFormat, (uint)width, (uint)height, 0,
                PixelFormat.DepthComponent, PixelType.Float, (void*)0);
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
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        var error = _gl.GetError();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status == GLEnum.FramebufferComplete && error == GLEnum.NoError)
            return true;

        failureReason = $"format={depthFormat}, status=0x{(int)status:X4}, glError=0x{(int)error:X4}";
        return false;
    }

    private void LogDepthSelection(int width, int height)
    {
        string key = $"{width}x{height}:{PreferredDepthFormat}";
        if (key == _lastDepthLogKey)
            return;

        _lastDepthLogKey = key;
        Android.Util.Log.Info("FA.NormalDepth", $"Normal/depth framebuffer depth={PreferredDepthFormat}, viewport={width}x{height}.");
    }

    public void TrimFramebuffers()
        => DestroyResources();

    public void Render(
        GpuScene scene,
        CameraState camera,
        int width,
        int height,
        SceneAppearance appearance,
        bool collectDiagnostics = false)
    {
        LastRenderInfo = default;
        if (width <= 0 || height <= 0)
            return;
        if (width != _width || height != _height || _fbo == 0)
            Resize(width, height);
        if (_fbo == 0)
        {
            if (!_loggedFramebufferUnavailable && _lastFramebufferError is not null)
            {
                Android.Util.Log.Warn("FA.NormalDepth", "Normal/depth unavailable after framebuffer setup failure: " + _lastFramebufferError);
                _loggedFramebufferUnavailable = true;
            }
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        // Clear normal RG to (0.5, 0.5) and packed color depth BA to 1.0.
        // The SSAO shader skips pixels with depth >= 0.999999, so unrendered
        // fragments naturally drop out.
        _gl.ClearColor(0.5f, 0.5f, 1f, 0f);
        _gl.ClearDepth(1.0f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        try
        {
            _program.Use();

            float aspect = (float)_width / _height;
            var identity = ViewportCameraMath.IdentityModelMatrix();
            var identityN = ViewportCameraMath.NormalMatrixFromIdentity();
            ViewportCameraMath.FillViewMatrix(camera, _viewScratch);
            ViewportCameraMath.FillProjectionMatrix(camera, aspect, _projectionScratch);
            (_linearDepthMin, _linearDepthMax) = ComputeLinearDepthRange(camera, scene.Bounds);

            SetMat4("uView", _viewScratch);
            SetMat4("uProjection", _projectionScratch);
            SetVec2("uLinearDepthRange", _linearDepthMin, _linearDepthMax);
            SetSectionUniforms();

            int modelLoc = _program.UniformLocation("uModel");
            int normalMatLoc = _program.UniformLocation("uNormalMatrix");

            bool clay = appearance.Mode == RenderMode.Clay;
            float surfaceOpacity = clay ? 1.0f : appearance.SurfaceOpacity;
            int renderedMeshes = 0;
            foreach (var mesh in scene.Meshes)
            {
                if (!mesh.Visible)
                    continue;

                if (!ShouldRenderMeshForDepth(mesh, surfaceOpacity, clay))
                    continue;

                float[] model = mesh.WorldTransform ?? identity;
                if (modelLoc >= 0) _gl.UniformMatrix4(modelLoc, true, model);
                float[] nm = identityN;
                if (mesh.WorldNormalMatrix is not null)
                    nm = mesh.WorldNormalMatrix;
                if (normalMatLoc >= 0) _gl.UniformMatrix3(normalMatLoc, true, nm);
                GlesRenderUtil.ApplyMeshCulling(_gl, mesh);
                mesh.Draw();
                renderedMeshes++;
            }
            GlesRenderUtil.ResetMeshCulling(_gl);

            GlesNormalDepthStats stats = default;
#if FA_ENABLE_GPU_DIAGNOSTIC_READBACK
            if (collectDiagnostics)
                stats = ReadStats();
#endif
            LastRenderInfo = new GlesNormalDepthRenderInfo(true, _width, _height, renderedMeshes, _linearDepthMin, _linearDepthMax, stats);
        }
        finally
        {
            GlesRenderUtil.ResetMeshCulling(_gl);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    private static bool ShouldRenderMeshForDepth(GpuMesh mesh, float surfaceOpacity, bool clay)
        => clay || (surfaceOpacity >= 0.999f && GetMeshColorAlpha(mesh) >= 0.999f);

    private static float GetMeshColorAlpha(GpuMesh mesh)
        => mesh.MaterialAlpha;

#if FA_ENABLE_GPU_DIAGNOSTIC_READBACK
    private unsafe GlesNormalDepthStats ReadStats()
    {
        if (_fbo == 0 || _width <= 0 || _height <= 0)
            return default;

        const int roiSize = 32;
        int roiWidth = System.Math.Min(roiSize, _width);
        int roiHeight = System.Math.Min(roiSize, _height);
        int x0 = System.Math.Max(0, (_width - roiWidth) / 2);
        int y0 = System.Math.Max(0, (_height - roiHeight) / 2);
        var rgba = new byte[roiWidth * roiHeight * 4];
        _gl.Finish();
        fixed (byte* p = rgba)
        {
            _gl.ReadPixels(x0, y0, (uint)roiWidth, (uint)roiHeight, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        byte minR = byte.MaxValue;
        byte maxR = byte.MinValue;
        float minDepth = float.MaxValue;
        float maxDepth = float.MinValue;
        float depthSum = 0f;
        int changedNormals = 0;
        int count = roiWidth * roiHeight;

        for (int i = 0; i < count; i++)
        {
            int offset = i * 4;
            byte r = rgba[offset];
            byte g = rgba[offset + 1];
            byte b = rgba[offset + 2];
            byte a = rgba[offset + 3];

            minR = System.Math.Min(minR, r);
            maxR = System.Math.Max(maxR, r);
            if (System.Math.Abs(r - 128) + System.Math.Abs(g - 128) > 4)
                changedNormals++;

            float depth = DecodeLinearDepth(DecodePackedDepth(b, a));
            minDepth = System.Math.Min(minDepth, depth);
            maxDepth = System.Math.Max(maxDepth, depth);
            depthSum += depth;
        }

        int centerX = System.Math.Clamp(_width / 2 - x0, 0, roiWidth - 1);
        int centerY = System.Math.Clamp(_height / 2 - y0, 0, roiHeight - 1);
        int centerOffset = (centerY * roiWidth + centerX) * 4;
        byte centerR = rgba[centerOffset];
        byte centerG = rgba[centerOffset + 1];
        float centerDepth = DecodeLinearDepth(DecodePackedDepth(rgba[centerOffset + 2], rgba[centerOffset + 3]));

        GLEnum error = _gl.GetError();
        return new GlesNormalDepthStats(
            error == GLEnum.NoError && count > 0,
            minR,
            maxR,
            changedNormals,
            count,
            centerR,
            centerG,
            count > 0 ? minDepth : 1f,
            count > 0 ? maxDepth : 1f,
            count > 0 ? depthSum / count : 1f,
            centerDepth,
            error);
    }
#endif

    private static float DecodePackedDepth(byte high, byte low)
    {
        float depth = high / 255f + (low / 255f) / 255f;
        return System.Math.Clamp(depth, 0f, 1f);
    }

    private float DecodeLinearDepth(float normalizedDepth)
        => _linearDepthMin + normalizedDepth * (_linearDepthMax - _linearDepthMin);

    private static (float Min, float Max) ComputeLinearDepthRange(CameraState camera, BoundingBox bounds)
    {
        double near = System.Math.Max(camera.NearPlane, 0.000001);
        double fallbackMax = System.Math.Max(near + 1.0, camera.Distance + 1.0);
        if (!bounds.IsValid)
            return ((float)near, (float)fallbackMax);

        Vector3d viewForward = camera.Forward;
        if (viewForward.LengthSquared < 1e-12)
            viewForward = (camera.Target - camera.Position).Normalized();
        if (viewForward.LengthSquared < 1e-12)
            viewForward = new Vector3d(-1, -1, -1).Normalized();

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3d corner = new(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            double depth = Vector3d.Dot(corner - camera.Position, viewForward);
            min = System.Math.Min(min, depth);
            max = System.Math.Max(max, depth);
        }

        double diagonal = bounds.Diagonal;
        if (!double.IsFinite(diagonal) || diagonal <= 1e-9)
            diagonal = 1.0;

        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= near)
            return ((float)near, (float)System.Math.Max(near + diagonal, fallbackMax));

        double span = System.Math.Max(max - min, diagonal * 0.001);
        double padding = System.Math.Max(span * 0.05, diagonal * 0.01);
        min = System.Math.Max(near, min - padding);
        max = System.Math.Max(min + span + padding, max + padding);
        return ((float)min, (float)max);
    }

    private void SetMat4(string name, float[] m)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }

    private void SetVec2(string name, float x, float y)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.Uniform2(loc, x, y);
    }

    private void SetSectionUniforms()
    {
        int count = System.Math.Min(SectionPlanes.Count, 8);
        int countLoc = _program.UniformLocation("uSectionPlaneCount");
        if (countLoc >= 0)
            _gl.Uniform1(countLoc, count);

        int planesLoc = _program.UniformArrayLocation("uSectionPlanes");
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
        if (_normalTex != 0) { _gl.DeleteTexture(_normalTex); _normalTex = 0; }
        if (_depthTex != 0) { _gl.DeleteTexture(_depthTex); _depthTex = 0; }
        LastRenderInfo = default;
        _width = 0;
        _height = 0;
    }

    private void DrainGlErrors()
    {
        for (int i = 0; i < 32; i++)
        {
            if (_gl.GetError() == GLEnum.NoError)
                return;
        }
    }

    public void Dispose()
    {
        DestroyResources();
        _program.Dispose();
    }
}

public readonly record struct GlesNormalDepthRenderInfo(
    bool Rendered,
    int Width,
    int Height,
    int MeshCount,
    float LinearDepthMin,
    float LinearDepthMax,
    GlesNormalDepthStats Stats);

public readonly record struct GlesNormalDepthStats(
    bool Valid,
    byte MinNormalR,
    byte MaxNormalR,
    int ChangedNormalSamples,
    int SampleCount,
    byte CenterNormalR,
    byte CenterNormalG,
    float MinDepth,
    float MaxDepth,
    float AverageDepth,
    float CenterDepth,
    GLEnum ReadError);
