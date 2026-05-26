using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Screen-space ambient occlusion. The shader pair is shared in structure with
/// the desktop renderer, while the Android texture formats stay GLES-friendly.
/// </summary>
public sealed class GlesSsaoRenderer : IDisposable
{
    private const int MaxKernelSamples = 96;
    private const int MaxBlurKernelRadius = 24;
    private const int NoiseTextureSize = 4;

    private readonly GL _gl;
    private readonly ShaderProgram _ssaoProgram;
    private readonly ShaderProgram _blurProgram;
    private readonly uint _fullscreenVao;
    private readonly uint _fullscreenVbo;
    private readonly float[] _kernel;
    private readonly float[] _gaussianWeights = new float[MaxBlurKernelRadius + 1];
    private readonly float[] _projectionScratch = new float[16];
    private readonly int _samplesLocation;
    private readonly int _gaussianWeightsLocation;
    private int _gaussianWeightsRadius = -1;
    private int _uploadedGaussianWeightsRadius = -1;
    private bool _kernelUploaded;
    private uint _noiseTexture;

    private uint _ssaoFbo;
    private uint _ssaoTex;
    private uint _blurFboA;
    private uint _blurTexA;
    private uint _blurFboB;
    private uint _blurTexB;
    private int _width;
    private int _height;

    public uint AoTexture { get; private set; }
    public GlesSsaoRenderInfo LastRenderInfo { get; private set; }

    public GlesSsaoRenderer(GL gl, string fullscreenVert, string ssaoFrag, string blurFrag)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _ssaoProgram = new ShaderProgram(_gl, "ssao", fullscreenVert, ssaoFrag);
        _blurProgram = new ShaderProgram(_gl, "ssao_blur", fullscreenVert, blurFrag);
        (_fullscreenVao, _fullscreenVbo) = GlesFullscreenTriangle.Create(_gl);
        _kernel = BuildKernel(MaxKernelSamples);
        _noiseTexture = BuildNoiseTexture();
        _samplesLocation = GetArrayUniformLocation(_ssaoProgram, "uSamples");
        _gaussianWeightsLocation = GetArrayUniformLocation(_blurProgram, "uGaussianWeights");
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _ssaoFbo != 0) return;
        DestroyResources();
        _width = width;
        _height = height;

        _ssaoTex = MakeAoTexture(width, height);
        _ssaoFbo = MakeFboAround(_ssaoTex, "ssao");
        _blurTexA = MakeAoTexture(width, height);
        _blurFboA = MakeFboAround(_blurTexA, "ssao.blurA");
        _blurTexB = MakeAoTexture(width, height);
        _blurFboB = MakeFboAround(_blurTexB, "ssao.blurB");
        AoTexture = _ssaoFbo != 0 ? _ssaoTex : 0;
    }

    public void TrimFramebuffers()
        => DestroyResources();

    /// <summary>
    /// Runs the SSAO pass against the supplied normal + depth textures, then
    /// optional horizontal/vertical bilateral blur passes.
    /// </summary>
    public void Render(
        uint normalTexture,
        uint depthTexture,
        float linearDepthMin,
        float linearDepthMax,
        CameraState camera,
        BoundingBox sceneBounds,
        SceneAppearance appearance,
        bool collectDiagnostics = false)
    {
        LastRenderInfo = default;
        if (_ssaoFbo == 0 || normalTexture == 0 || depthTexture == 0) return;
        if (linearDepthMax <= linearDepthMin)
        {
            Android.Util.Log.Warn(
                "FA.SSAO",
                $"Invalid linear depth range; clamping max. min={linearDepthMin:0.###}, max={linearDepthMax:0.###}.");
            linearDepthMax = linearDepthMin + 1f;
        }

        float aspect = (float)_width / _height;
        ViewportCameraMath.FillProjectionMatrix(camera, aspect, _projectionScratch);

        // ViewportCameraMath emits row-major float[16].
        float scaleX = _projectionScratch[0];
        float scaleY = _projectionScratch[5];
        float offsetX = _projectionScratch[2];
        float offsetY = _projectionScratch[6];
        float depthA = _projectionScratch[10];
        float depthB = _projectionScratch[11];

        float sceneDiagonal = GetSceneDiagonal(sceneBounds);
        float radius = ClampPositive(appearance.AoRadius * sceneDiagonal, sceneDiagonal * 0.00005f);
        float bias = System.Math.Max(0.0f, appearance.AoBias * sceneDiagonal);
        float maxDistance = ClampPositive(appearance.AoMaxDistance * sceneDiagonal, radius);
        float fadeStart = System.Math.Max(0.0f, appearance.AoFadeStart * sceneDiagonal);
        float fadeEnd = System.Math.Max(fadeStart + radius, appearance.AoFadeEnd * sceneDiagonal);
        float cameraSceneDistance = GetCameraSceneDistance(camera, sceneBounds);
        if (cameraSceneDistance > 0f)
        {
            float visibilityPad = System.Math.Max(sceneDiagonal * 0.75f, radius * 8f);
            if (fadeStart < cameraSceneDistance)
                fadeStart = cameraSceneDistance + radius * 2f;
            if (fadeEnd < fadeStart + visibilityPad)
                fadeEnd = fadeStart + visibilityPad;
            if (maxDistance < fadeEnd)
                maxDistance = fadeEnd;
        }
        int sampleCount = System.Math.Clamp(appearance.AoSampleCount, 1, MaxKernelSamples);
        int blurRadius = System.Math.Clamp(appearance.AoBlurRadius, 0, MaxBlurKernelRadius);
        int blurPassCount = System.Math.Clamp(appearance.AoBlurPasses, 0, 8);
        float intensity = System.Math.Max(0.0f, appearance.AoIntensity);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(1.0f, 1.0f, 1.0f, 1.0f);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);

        _ssaoProgram.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, normalTexture);
        SetInt(_ssaoProgram, "uNormalTexture", 0);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _noiseTexture);
        SetInt(_ssaoProgram, "uNoiseTexture", 1);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, depthTexture);
        SetInt(_ssaoProgram, "uDepthTexture", 2);

        SetVec2(_ssaoProgram, "uProjectionOffset", offsetX, offsetY);
        SetVec2(_ssaoProgram, "uProjectionDepth", depthA, depthB);
        SetVec2(_ssaoProgram, "uInvProjectionScale",
            scaleX != 0 ? 1f / scaleX : 0f,
            scaleY != 0 ? 1f / scaleY : 0f);
        SetVec2(_ssaoProgram, "uProjectionUvScale", -scaleX * 0.5f, -scaleY * 0.5f);
        SetVec2(_ssaoProgram, "uProjectionUvBias", 0.5f - offsetX * 0.5f, 0.5f - offsetY * 0.5f);
        SetVec2(_ssaoProgram, "uNoiseScaleClamped",
            MathF.Max(_width / (float)NoiseTextureSize * appearance.AoNoiseScale, 0.05f),
            MathF.Max(_height / (float)NoiseTextureSize * appearance.AoNoiseScale, 0.05f));
        SetVec2(_ssaoProgram, "uLinearDepthRange", linearDepthMin, linearDepthMax);
        SetInt(_ssaoProgram, "uIsPerspective", camera.IsPerspective ? 1 : 0);

        SetInt(_ssaoProgram, "uSampleCount", sampleCount);
        SetFloat(_ssaoProgram, "uRadius", radius);
        SetFloat(_ssaoProgram, "uBias", bias);
        SetFloat(_ssaoProgram, "uPlaneWeightRange", MathF.Max(radius * 0.08f, bias));
        SetFloat(_ssaoProgram, "uIntensity", intensity);
        SetFloat(_ssaoProgram, "uPower", System.Math.Max(0.05f, appearance.AoPower));
        SetFloat(_ssaoProgram, "uContrast", System.Math.Max(0.0f, appearance.AoContrast));
        SetFloat(_ssaoProgram, "uMaxDistance", maxDistance);
        SetFloat(_ssaoProgram, "uFadeStart", fadeStart);
        SetFloat(_ssaoProgram, "uFadeEnd", fadeEnd);

        if (!_kernelUploaded)
        {
            UploadVec4Array(_samplesLocation, _kernel, MaxKernelSamples);
            _kernelUploaded = true;
        }

        DrawFullscreen();
        GLEnum renderError = _gl.GetError();
        GlesSsaoTextureStats rawStats = collectDiagnostics ? ReadAoStats(_ssaoFbo) : default;

        if (!appearance.AoBlurEnabled || blurRadius <= 0 || blurPassCount <= 0 || _blurFboA == 0 || _blurFboB == 0)
        {
            AoTexture = _ssaoTex;
            LastRenderInfo = new GlesSsaoRenderInfo(
                true,
                _width,
                _height,
                sampleCount,
                radius,
                bias,
                intensity,
                maxDistance,
                fadeStart,
                fadeEnd,
                cameraSceneDistance,
                sceneDiagonal,
                camera.IsPerspective,
                false,
                blurRadius,
                blurPassCount,
                AoTexture,
                linearDepthMin,
                linearDepthMax,
                rawStats,
                rawStats,
                renderError);
            _gl.DepthMask(true);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            return;
        }

        _blurProgram.Use();
        SetVec2(_blurProgram, "uTexelSize", 1f / _width, 1f / _height);
        SetInt(_blurProgram, "uAoTexture", 0);
        SetInt(_blurProgram, "uNormalTexture", 1);
        SetInt(_blurProgram, "uDepthTexture", 2);
        SetInt(_blurProgram, "uRadius", blurRadius);
        SetInt(_blurProgram, "uIsPerspective", camera.IsPerspective ? 1 : 0);
        SetFloat(_blurProgram, "uSharpness", System.Math.Max(0.0f, appearance.AoBlurSharpness));

        float[] weights = GetGaussianWeights(blurRadius);
        if (_uploadedGaussianWeightsRadius != blurRadius)
        {
            UploadFloatArray(_gaussianWeightsLocation, weights, blurRadius + 1);
            _uploadedGaussianWeightsRadius = blurRadius;
        }

        uint srcTex = _ssaoTex;
        uint dstFbo = _blurFboA;
        uint dstTex = _blurTexA;
        int totalPasses = blurPassCount * 2;
        for (int i = 0; i < totalPasses; i++)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, dstFbo);
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.DepthMask(false);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, srcTex);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, normalTexture);
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, depthTexture);

            if ((i & 1) == 0) SetVec2(_blurProgram, "uDirection", 1f, 0f);
            else SetVec2(_blurProgram, "uDirection", 0f, 1f);
            DrawFullscreen();

            srcTex = dstTex;
            if (dstFbo == _blurFboA) { dstFbo = _blurFboB; dstTex = _blurTexB; }
            else { dstFbo = _blurFboA; dstTex = _blurTexA; }
        }

        AoTexture = srcTex;
        uint finalFbo = srcTex == _blurTexA ? _blurFboA : _blurFboB;
        if (renderError == GLEnum.NoError)
            renderError = _gl.GetError();
        GlesSsaoTextureStats finalStats = collectDiagnostics ? ReadAoStats(finalFbo) : default;
        LastRenderInfo = new GlesSsaoRenderInfo(
            true,
            _width,
            _height,
            sampleCount,
            radius,
            bias,
            intensity,
            maxDistance,
            fadeStart,
            fadeEnd,
            cameraSceneDistance,
            sceneDiagonal,
            camera.IsPerspective,
            true,
            blurRadius,
            blurPassCount,
            AoTexture,
            linearDepthMin,
            linearDepthMax,
            rawStats,
            finalStats,
            renderError);
        _gl.DepthMask(true);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private unsafe GlesSsaoTextureStats ReadAoStats(uint fbo)
    {
        if (fbo == 0 || _width <= 0 || _height <= 0)
            return default;

        try
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

            const int roiSize = 32;
            int roiWidth = System.Math.Min(roiSize, _width);
            int roiHeight = System.Math.Min(roiSize, _height);
            int x0 = System.Math.Max(0, (_width - roiWidth) / 2);
            int y0 = System.Math.Max(0, (_height - roiHeight) / 2);
            var pixels = new byte[roiWidth * roiHeight];
            _gl.Finish();
            fixed (byte* p = pixels)
            {
                _gl.ReadPixels(x0, y0, (uint)roiWidth, (uint)roiHeight, PixelFormat.Red, PixelType.UnsignedByte, p);
            }

            byte min = byte.MaxValue;
            byte max = byte.MinValue;
            int sum = 0;
            int count = pixels.Length;
            for (int i = 0; i < pixels.Length; i++)
            {
                byte value = pixels[i];
                min = System.Math.Min(min, value);
                max = System.Math.Max(max, value);
                sum += value;
            }

            int centerX = System.Math.Clamp(_width / 2 - x0, 0, roiWidth - 1);
            int centerY = System.Math.Clamp(_height / 2 - y0, 0, roiHeight - 1);
            byte center = pixels[centerY * roiWidth + centerX];
            var error = _gl.GetError();
            return new GlesSsaoTextureStats(
                count > 0 && error == GLEnum.NoError,
                min,
                max,
                count > 0 ? sum / (float)count : 0f,
                center,
                error);
        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
    }

    private void DrawFullscreen()
    {
        GlesFullscreenTriangle.Draw(_gl, _fullscreenVao);
    }

    private unsafe uint MakeAoTexture(int w, int h)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.R8, (uint)w, (uint)h, 0,
            PixelFormat.Red, PixelType.UnsignedByte, (void*)0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private uint MakeFboAround(uint colorTex, string label)
    {
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, colorTex, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status == GLEnum.FramebufferComplete)
            return fbo;

        Android.Util.Log.Error("FA.Ssao", $"{label} FBO incomplete: 0x{(int)status:X4}");
        _gl.DeleteFramebuffer(fbo);
        return 0;
    }

    private static float[] BuildKernel(int count)
    {
        var random = new Random(9247);
        var data = new float[count * 4];
        for (int i = 0; i < count; i++)
        {
            double x = random.NextDouble() * 2.0 - 1.0;
            double y = random.NextDouble() * 2.0 - 1.0;
            double z = random.NextDouble();
            double length = System.Math.Sqrt(x * x + y * y + z * z);
            if (length < 1e-7)
            {
                x = 0.0;
                y = 0.0;
                z = 1.0;
                length = 1.0;
            }

            double scale = (double)i / count;
            scale = 0.1 + 0.9 * scale * scale;
            double randomRadius = 0.35 + random.NextDouble() * 0.65;
            data[i * 4] = (float)(x / length * scale * randomRadius);
            data[i * 4 + 1] = (float)(y / length * scale * randomRadius);
            data[i * 4 + 2] = (float)(z / length * scale * randomRadius);
            data[i * 4 + 3] = 0.0f;
        }

        return data;
    }

    private unsafe uint BuildNoiseTexture()
    {
        var random = new Random(7301);
        var data = new byte[NoiseTextureSize * NoiseTextureSize * 3];
        for (int i = 0; i < NoiseTextureSize * NoiseTextureSize; i++)
        {
            double angle = random.NextDouble() * System.Math.PI * 2.0;
            float x = (float)System.Math.Cos(angle);
            float y = (float)System.Math.Sin(angle);
            data[i * 3] = ToUnormByte(x);
            data[i * 3 + 1] = ToUnormByte(y);
            data[i * 3 + 2] = 128;
        }

        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = data)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0,
                InternalFormat.Rgb8, NoiseTextureSize, NoiseTextureSize, 0,
                PixelFormat.Rgb, PixelType.UnsignedByte, p);
        }
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private float[] GetGaussianWeights(int radius)
    {
        radius = System.Math.Clamp(radius, 1, MaxBlurKernelRadius);
        if (_gaussianWeightsRadius == radius)
            return _gaussianWeights;

        Array.Clear(_gaussianWeights);
        float radiusScale = radius;
        for (int offset = 0; offset <= radius; offset++)
        {
            float x = offset / radiusScale;
            _gaussianWeights[offset] = MathF.Exp(-x * x * 2.0f);
        }

        _gaussianWeightsRadius = radius;
        return _gaussianWeights;
    }

    private static float GetSceneDiagonal(BoundingBox bounds)
        => bounds.IsValid && bounds.Diagonal > 0.0 ? (float)bounds.Diagonal : 1.0f;

    private static float GetCameraSceneDistance(CameraState camera, BoundingBox bounds)
        => bounds.IsValid
            ? (float)Vector3d.Distance(camera.Position, bounds.Center)
            : (float)camera.Distance;

    private static float ClampPositive(float value, float fallback)
        => value > 1e-7f ? value : fallback;

    private static byte ToUnormByte(float signedUnit)
        => (byte)System.Math.Clamp((int)System.Math.Round((signedUnit * 0.5f + 0.5f) * 255.0f), 0, 255);

    private void SetVec2(ShaderProgram p, string name, float x, float y)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform2(loc, x, y); }
    private void SetFloat(ShaderProgram p, string name, float v)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform1(loc, v); }
    private void SetInt(ShaderProgram p, string name, int v)
    { int loc = p.UniformLocation(name); if (loc >= 0) _gl.Uniform1(loc, v); }

    private unsafe void UploadVec4Array(int loc, float[] values, int count)
    {
        if (count <= 0 || values.Length < count * 4)
            return;

        if (loc < 0)
            return;

        fixed (float* ptr = values)
            _gl.Uniform4(loc, (uint)count, ptr);
    }

    private unsafe void UploadFloatArray(int loc, float[] values, int count)
    {
        if (count <= 0 || values.Length < count)
            return;

        if (loc < 0)
            return;

        fixed (float* ptr = values)
            _gl.Uniform1(loc, (uint)count, ptr);
    }

    private int GetArrayUniformLocation(ShaderProgram p, string name)
    {
        return p.UniformArrayLocation(name);
    }

    private void DestroyResources()
    {
        if (_ssaoFbo != 0) { _gl.DeleteFramebuffer(_ssaoFbo); _ssaoFbo = 0; }
        if (_blurFboA != 0) { _gl.DeleteFramebuffer(_blurFboA); _blurFboA = 0; }
        if (_blurFboB != 0) { _gl.DeleteFramebuffer(_blurFboB); _blurFboB = 0; }
        if (_ssaoTex != 0) { _gl.DeleteTexture(_ssaoTex); _ssaoTex = 0; }
        if (_blurTexA != 0) { _gl.DeleteTexture(_blurTexA); _blurTexA = 0; }
        if (_blurTexB != 0) { _gl.DeleteTexture(_blurTexB); _blurTexB = 0; }
        AoTexture = 0;
        LastRenderInfo = default;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        DestroyResources();
        if (_fullscreenVao != 0) _gl.DeleteVertexArray(_fullscreenVao);
        if (_fullscreenVbo != 0) _gl.DeleteBuffer(_fullscreenVbo);
        if (_noiseTexture != 0) _gl.DeleteTexture(_noiseTexture);
        _ssaoProgram.Dispose();
        _blurProgram.Dispose();
    }
}

public readonly record struct GlesSsaoRenderInfo(
    bool Rendered,
    int Width,
    int Height,
    int SampleCount,
    float Radius,
    float Bias,
    float Intensity,
    float MaxDistance,
    float FadeStart,
    float FadeEnd,
    float CameraSceneDistance,
    float SceneDiagonal,
    bool IsPerspective,
    bool BlurEnabled,
    int BlurRadius,
    int BlurPasses,
    uint AoTexture,
    float LinearDepthMin,
    float LinearDepthMax,
    GlesSsaoTextureStats RawTextureStats,
    GlesSsaoTextureStats TextureStats,
    GLEnum RenderError);

public readonly record struct GlesSsaoTextureStats(
    bool Valid,
    byte Min,
    byte Max,
    float Average,
    byte Center,
    GLEnum ReadError);
