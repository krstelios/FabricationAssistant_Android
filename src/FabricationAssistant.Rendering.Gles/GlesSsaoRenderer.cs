using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Screen-space ambient occlusion: samples the normal + depth textures
/// produced by <see cref="GlesNormalDepthRenderer"/>, computes per-pixel
/// occlusion via a hemispherical kernel rotated by a 4x4 noise texture,
/// then bilaterally blurs (depth + normal weighted) into a final R8 texture.
/// Algorithm and uniform set are a direct port of the desktop's
/// ssao.frag.glsl + ssao_blur.frag.glsl.
/// </summary>
public sealed class GlesSsaoRenderer : IDisposable
{
    private const int KernelSize = 32;       // Below the shader's 96-cap so per-frame work stays affordable on tablet GPUs.
    private const int BlurRadius = 6;        // Below the shader's 25-cap for Gaussian weights.
    private const int NoiseTextureSize = 4;

    private readonly GL _gl;
    private readonly ShaderProgram _ssaoProgram;
    private readonly ShaderProgram _blurProgram;
    private readonly uint _fullscreenVao;
    private readonly float[] _kernel;          // KernelSize * 4 floats (vec4 alignment per std140-ish habit)
    private readonly float[] _gaussian;        // BlurRadius + 1 floats
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

    public GlesSsaoRenderer(GL gl, string fullscreenVert, string ssaoFrag, string blurFrag)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _ssaoProgram = new ShaderProgram(_gl, "ssao", fullscreenVert, ssaoFrag);
        _blurProgram = new ShaderProgram(_gl, "ssao_blur", fullscreenVert, blurFrag);
        _fullscreenVao = _gl.GenVertexArray();
        _kernel = BuildKernel();
        _gaussian = BuildGaussianWeights(BlurRadius, sigma: 2.0f);
        _noiseTexture = BuildNoiseTexture();
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _ssaoFbo != 0) return;
        DestroyResources();
        _width = width;
        _height = height;

        _ssaoTex = MakeR8Texture(width, height);
        _ssaoFbo = MakeFboAround(_ssaoTex, "ssao");
        _blurTexA = MakeR8Texture(width, height);
        _blurFboA = MakeFboAround(_blurTexA, "ssao.blurA");
        _blurTexB = MakeR8Texture(width, height);
        _blurFboB = MakeFboAround(_blurTexB, "ssao.blurB");
        AoTexture = _blurTexB;
    }

    /// <summary>
    /// Runs the SSAO pass against the supplied normal + depth textures, then
    /// N x 2 bilateral blur passes (horizontal then vertical). Final AO ends
    /// up in <see cref="AoTexture"/> for the mesh shader to sample.
    /// </summary>
    public void Render(
        uint normalTexture,
        uint depthTexture,
        CameraState camera,
        SceneAppearance appearance)
    {
        if (_ssaoFbo == 0 || normalTexture == 0 || depthTexture == 0) return;

        float aspect = (float)_width / _height;
        var proj = ViewportCameraMath.ProjectionMatrix(camera, aspect);

        // Extract the projection-decomposition uniforms the SSAO shader
        // needs to reconstruct view positions from depth. ViewportCameraMath
        // emits a row-major float[16]; proj[row][col] = arr[row * 4 + col].
        float scaleX = proj[0];
        float scaleY = proj[5];
        float offsetX = proj[8];
        float offsetY = proj[9];
        float depthA = proj[10];
        float depthB = proj[11];

        // SSAO pass
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _ssaoFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Disable(EnableCap.DepthTest);

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
        // Forward proj: ndc.x = -scaleX * viewX/viewZ - offsetX; uv = (ndc + 1)/2
        // so uv = (-scaleX/2) * (viewX/viewZ) + (1 - offsetX) / 2.
        SetVec2(_ssaoProgram, "uProjectionUvScale", -scaleX * 0.5f, -scaleY * 0.5f);
        SetVec2(_ssaoProgram, "uProjectionUvBias", 0.5f - offsetX * 0.5f, 0.5f - offsetY * 0.5f);
        SetVec2(_ssaoProgram, "uNoiseScaleClamped", (float)_width / NoiseTextureSize, (float)_height / NoiseTextureSize);

        SetInt(_ssaoProgram, "uSampleCount", KernelSize);
        SetFloat(_ssaoProgram, "uRadius", appearance.AoRadius);
        SetFloat(_ssaoProgram, "uBias", appearance.AoBias);
        SetFloat(_ssaoProgram, "uPlaneWeightRange", 0.10f);
        SetFloat(_ssaoProgram, "uIntensity", appearance.AoIntensity);
        SetFloat(_ssaoProgram, "uPower", 1.0f);
        SetFloat(_ssaoProgram, "uContrast", 1.0f);
        SetFloat(_ssaoProgram, "uMaxDistance", 0f);
        SetFloat(_ssaoProgram, "uFadeStart", 0f);
        SetFloat(_ssaoProgram, "uFadeEnd", 0f);

        // GLSL spec guarantees consecutive locations for array elements -
        // upload one vec4 at a time from the first element's location. Avoids
        // pinning the array and Silk.NET overload-resolution pitfalls.
        int kernelLoc = _gl.GetUniformLocation(_ssaoProgram.Handle, "uSamples[0]");
        if (kernelLoc >= 0)
        {
            for (int i = 0; i < KernelSize; i++)
            {
                _gl.Uniform4(kernelLoc + i,
                    _kernel[i * 4 + 0],
                    _kernel[i * 4 + 1],
                    _kernel[i * 4 + 2],
                    _kernel[i * 4 + 3]);
            }
        }

        DrawFullscreen();

        // Bilateral blur passes
        _blurProgram.Use();
        SetVec2(_blurProgram, "uTexelSize", 1f / _width, 1f / _height);
        SetInt(_blurProgram, "uAoTexture", 0);
        SetInt(_blurProgram, "uNormalTexture", 1);
        SetInt(_blurProgram, "uDepthTexture", 2);
        SetInt(_blurProgram, "uRadius", BlurRadius);
        SetFloat(_blurProgram, "uSharpness", 8f);

        int gaussLoc = _gl.GetUniformLocation(_blurProgram.Handle, "uGaussianWeights[0]");
        if (gaussLoc >= 0)
        {
            for (int i = 0; i < _gaussian.Length; i++)
                _gl.Uniform1(gaussLoc + i, _gaussian[i]);
        }

        uint srcTex = _ssaoTex;
        uint dstFbo = _blurFboA;
        uint dstTex = _blurTexA;
        int totalPasses = System.Math.Max(appearance.AoBlurPasses, 1) * 2;
        for (int i = 0; i < totalPasses; i++)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, dstFbo);
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);
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

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void DrawFullscreen()
    {
        _gl.BindVertexArray(_fullscreenVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    private unsafe uint MakeR8Texture(int w, int h)
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
        var st = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (st != GLEnum.FramebufferComplete)
            Android.Util.Log.Error("FA.Ssao", $"{label} FBO incomplete: 0x{(int)st:X4}");
        return fbo;
    }

    /// <summary>
    /// Cosine-weighted hemisphere kernel - same shape as the desktop. Stored
    /// as vec4 with .w = 0 to match the shader's uSamples[96] declaration.
    /// </summary>
    private static float[] BuildKernel()
    {
        var rng = new Random(1337);
        var k = new float[KernelSize * 4];
        for (int i = 0; i < KernelSize; i++)
        {
            float x = (float)(rng.NextDouble() * 2.0 - 1.0);
            float y = (float)(rng.NextDouble() * 2.0 - 1.0);
            float z = (float)rng.NextDouble();
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len < 1e-4f) { x = 0; y = 0; z = 1; len = 1; }
            x /= len; y /= len; z /= len;
            // Scale samples toward the centre so close samples weigh more.
            float scale = (float)i / KernelSize;
            scale = 0.1f + scale * scale * 0.9f;
            k[i * 4 + 0] = x * scale;
            k[i * 4 + 1] = y * scale;
            k[i * 4 + 2] = z * scale;
            k[i * 4 + 3] = 0f;
        }
        return k;
    }

    /// <summary>
    /// 1D Gaussian weights for the bilateral blur. weights[i] = exp(-i^2 / (2*sigma^2)).
    /// Length = radius + 1 (the shader indexes by abs(offset)).
    /// </summary>
    private static float[] BuildGaussianWeights(int radius, float sigma)
    {
        var w = new float[radius + 1];
        float sum = 0f;
        for (int i = 0; i <= radius; i++)
        {
            w[i] = MathF.Exp(-(i * i) / (2f * sigma * sigma));
            sum += i == 0 ? w[i] : 2f * w[i];
        }
        for (int i = 0; i <= radius; i++) w[i] /= sum;
        return w;
    }

    /// <summary>
    /// 4x4 RGB8 noise texture - random unit vectors with z = 0 in the
    /// tangent plane. Stored as [0, 1] and remapped to [-1, 1] in the shader.
    /// </summary>
    private unsafe uint BuildNoiseTexture()
    {
        var rng = new Random(42);
        var data = new byte[NoiseTextureSize * NoiseTextureSize * 3];
        for (int i = 0; i < NoiseTextureSize * NoiseTextureSize; i++)
        {
            float x = (float)(rng.NextDouble() * 2.0 - 1.0);
            float y = (float)(rng.NextDouble() * 2.0 - 1.0);
            float len = MathF.Sqrt(x * x + y * y);
            if (len < 1e-4f) { x = 1; y = 0; len = 1; }
            x /= len; y /= len;
            data[i * 3 + 0] = (byte)((x * 0.5f + 0.5f) * 255f);
            data[i * 3 + 1] = (byte)((y * 0.5f + 0.5f) * 255f);
            data[i * 3 + 2] = 128; // z = 0 in [-1, 1] -> 0.5 in [0, 1]
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

    private void SetVec2(ShaderProgram p, string name, float x, float y)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform2(loc, x, y); }
    private void SetFloat(ShaderProgram p, string name, float v)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform1(loc, v); }
    private void SetInt(ShaderProgram p, string name, int v)
    { int loc = _gl.GetUniformLocation(p.Handle, name); if (loc >= 0) _gl.Uniform1(loc, v); }

    private void DestroyResources()
    {
        if (_ssaoFbo != 0) { _gl.DeleteFramebuffer(_ssaoFbo); _ssaoFbo = 0; }
        if (_blurFboA != 0) { _gl.DeleteFramebuffer(_blurFboA); _blurFboA = 0; }
        if (_blurFboB != 0) { _gl.DeleteFramebuffer(_blurFboB); _blurFboB = 0; }
        if (_ssaoTex != 0) { _gl.DeleteTexture(_ssaoTex); _ssaoTex = 0; }
        if (_blurTexA != 0) { _gl.DeleteTexture(_blurTexA); _blurTexA = 0; }
        if (_blurTexB != 0) { _gl.DeleteTexture(_blurTexB); _blurTexB = 0; }
        AoTexture = 0;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        DestroyResources();
        if (_fullscreenVao != 0) _gl.DeleteVertexArray(_fullscreenVao);
        if (_noiseTexture != 0) _gl.DeleteTexture(_noiseTexture);
        _ssaoProgram.Dispose();
        _blurProgram.Dispose();
    }
}
