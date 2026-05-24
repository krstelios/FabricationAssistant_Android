using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Plan 2A scene renderer. Compiles the mesh + pick programs, drains a
/// thread-safe command queue at the top of every frame (so UI-thread import
/// work can enqueue GL upload commands), then draws the scene with the
/// current CameraState. Plan 2E adds the offscreen pick FBO and selection
/// highlight uniforms.
/// </summary>
public sealed class GlesViewportRenderer
{
    private const double CameraBasisEpsilon = 1e-8;

    private readonly GlThreadGuard _guard = new();
    private GL? _gl;
    private ShaderProgram? _meshProgram;
    private ShaderProgram? _edgeProgram;
    private GlesPickRenderer? _pickRenderer;
    private GlesGridRenderer? _gridRenderer;
    private GlesNormalDepthRenderer? _normalDepthRenderer;
    private GlesSsaoRenderer? _ssaoRenderer;
    private GlesOutlineRenderer? _outlineRenderer;
    private MsaaSceneFramebuffer? _msaaFbo;
    private uint _whiteAoTexture;
    private bool _initialized;
    private int _width;
    private int _height;
    private GpuScene? _edgeSettingsScene;
    private float _edgeFeatureAngle = float.NaN;
    private float _edgeCoplanarTolerance = float.NaN;
    private float _edgeWeldTolerance = float.NaN;
    private bool _edgeSilhouetteEnabled;

    public GpuScene? Scene { get; set; }
    public CameraState? Camera { get; set; }
    public ConcurrentQueue<Action<GL>>? CommandQueue { get; set; }

    /// <summary>
    /// 1-based mesh index to highlight in the next frame, or 0 for none.
    /// Set by the host (MainActivity) after a successful tap-pick.
    /// </summary>
    public int SelectedMeshIndex { get; set; }

    /// <summary>
    /// Per-frame appearance state. Mirrors the desktop SceneAppearanceViewModel.
    /// PreferencesBottomSheet writes through AppSettings; MainActivity rebuilds
    /// this struct on every change via AppSettings.Apply.
    /// </summary>
    public SceneAppearance Appearance { get; set; } = SceneAppearance.CreateDefault();

    // Backwards-compatible aliases (read by callers that haven't migrated to
    // Appearance yet). Removed once nothing references them.
    public bool ShowGrid { get => Appearance.ShowGrid; set { var a = Appearance; a.ShowGrid = value; Appearance = a; } }
    public bool HighlightSelection { get; set; } = true;
    public float[] ClearColor
    {
        get => Appearance.BackgroundColor;
        set { var a = Appearance; a.BackgroundColor = value; Appearance = a; }
    }

    public void OnSurfaceCreated()
    {
        _guard.Initialize();

        _gl = GL.GetApi(new SurfaceViewGlContext());

        _gl.ClearColor(0.10f, 0.11f, 0.12f, 1.0f);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

        var vs = LoadEmbeddedShader("mesh.gles.vert");
        var fs = LoadEmbeddedShader("mesh.gles.frag");
        _meshProgram = new ShaderProgram(_gl, "mesh", vs, fs);

        // Clay mode shares the same mesh shader as Shaded; the renderer
        // simply uploads clay-specific uniform values (per the desktop
        // SceneRenderer.ConfigureSurfaceShader clayLighting branch).

        var edgeVs = LoadEmbeddedShader("edge.ribbon.gles.vert");
        var edgeFs = LoadEmbeddedShader("edge.ribbon.gles.frag");
        _edgeProgram = new ShaderProgram(_gl, "edge.ribbon", edgeVs, edgeFs);

        var pickVs = LoadEmbeddedShader("pick.gles.vert");
        var pickFs = LoadEmbeddedShader("pick.gles.frag");
        _pickRenderer = new GlesPickRenderer(_gl, pickVs, pickFs);

        var gridVs = LoadEmbeddedShader("grid.gles.vert");
        var gridFs = LoadEmbeddedShader("grid.gles.frag");
        _gridRenderer = new GlesGridRenderer(_gl, gridVs, gridFs);

        // SSAO pipeline (Phase E): normal-depth pass + SSAO + separable blur.
        var ndVs = LoadEmbeddedShader("normal_depth.gles.vert");
        var ndFs = LoadEmbeddedShader("normal_depth.gles.frag");
        _normalDepthRenderer = new GlesNormalDepthRenderer(_gl, ndVs, ndFs);

        var fsVs = LoadEmbeddedShader("fullscreen.gles.vert");
        var ssaoFs = LoadEmbeddedShader("ssao.gles.frag");
        var ssaoBlurFs = LoadEmbeddedShader("ssao_blur.gles.frag");
        _ssaoRenderer = new GlesSsaoRenderer(_gl, fsVs, ssaoFs, ssaoBlurFs);

        // 1x1 white AO texture - bound when SSAO is off so the mesh shader's
        // AO multiply is identity.
        _whiteAoTexture = CreateWhiteTexture(_gl);

        // Selection outline (Phase G). Reuses pick.gles.vert for the mask
        // and fullscreen.gles.vert for the Sobel composite.
        var maskFs = LoadEmbeddedShader("mask.gles.frag");
        var outlineFs = LoadEmbeddedShader("outline.gles.frag");
        _outlineRenderer = new GlesOutlineRenderer(_gl, pickVs, maskFs, fsVs, outlineFs);

        // Plan 3B: offscreen multisample FBO. All scene passes (grid, mesh,
        // edge) render into this FBO and the resolved color is blitted to
        // the default backbuffer before the selection outline post-process.
        _msaaFbo = new MsaaSceneFramebuffer(_gl);

        _initialized = true;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        _guard.EnsureOnRenderThread();
        if (_gl is null) return;
        _width = width;
        _height = height;
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _pickRenderer?.Resize(width, height);
        _normalDepthRenderer?.Resize(width, height);
        _ssaoRenderer?.Resize(width, height);
        _outlineRenderer?.Resize(width, height);
        _msaaFbo?.Ensure(width, height, Appearance.MsaaSamples);
    }

    public void OnDrawFrame()
    {
        _guard.EnsureOnRenderThread();
        if (!_initialized || _gl is null || _meshProgram is null) return;

        if (CommandQueue is { } q)
        {
            while (q.TryDequeue(out var cmd))
            {
                try { cmd(_gl); }
                catch (Exception ex) { Android.Util.Log.Error("FA.Renderer", Java.Lang.Throwable.FromException(ex), "GL command failed: " + ex.Message); }
            }
        }

        var a = Appearance;
        EnsureEdgesMatchAppearance(a);

        // Re-allocate the MSAA FBO if the user changed the sample count
        // via the preferences sheet, or if the viewport was resized. Cheap
        // when nothing has changed (Ensure compares cached dims + samples).
        _msaaFbo?.Ensure(_width, _height, a.MsaaSamples);

        // SSAO pre-pass: render scene normals+depth, then compute occlusion.
        // Bound back to the default FBO before the main mesh pass, which
        // samples the resulting AO texture.
        uint aoTextureToBind = _whiteAoTexture;
        if (a.AmbientOcclusionEnabled
            && a.Mode != RenderMode.Clay
            && a.Mode != RenderMode.Wireframe
            && Scene is not null && Camera is not null
            && _normalDepthRenderer is not null && _ssaoRenderer is not null)
        {
            _normalDepthRenderer.Render(Scene, Camera, _width, _height);
            _ssaoRenderer.Render(
                _normalDepthRenderer.NormalTexture,
                _normalDepthRenderer.DepthTexture,
                Camera, a);
            aoTextureToBind = _ssaoRenderer.AoTexture;
        }

        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        // Bind the offscreen MSAA FBO. ResetMainFramebufferState restores
        // depth/blend/cull defaults; the bind itself happens here so all
        // subsequent draw calls land in the multisample renderbuffer.
        if (_msaaFbo is not null && _msaaFbo.FboHandle != 0)
            _msaaFbo.Bind();
        else
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        ResetMainFramebufferState();

        // Clay mode swaps to its own background so the scene reads as a
        // matte studio shot rather than the operator's main background.
        float[] bg = a.Mode == RenderMode.Clay ? a.ClayBackgroundColor : a.BackgroundColor;
        _gl.ClearColor(bg[0], bg[1], bg[2], 1.0f);
        _gl.ClearStencil(0);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit));

        if (Scene is null || Camera is null || _width == 0 || _height == 0)
            return;

        // Ground grid first so the depth pre-fill sits behind opaque meshes
        // (which will overwrite the grid where they cover it). Skipped when
        // SceneAppearance.ShowGrid is false.
        if (a.ShowGrid)
            _gridRenderer?.Draw(Scene.Bounds, Camera, _width, _height, a);

        // Single mesh shader for both Shaded and Clay - per-mode uniform
        // overrides happen below (mirrors the desktop's
        // SceneRenderer.ConfigureSurfaceShader clayLighting branch).
        _meshProgram.Use();
        ShaderProgram activeProgram = _meshProgram;

        var aspect = (float)_width / _height;
        var identityModel = ViewportCameraMath.IdentityModelMatrix();
        var identityNormal = ViewportCameraMath.NormalMatrixFromIdentity();
        var view = ViewportCameraMath.ViewMatrix(Camera);
        var proj = ViewportCameraMath.ProjectionMatrix(Camera, aspect);

        // Build a stable camera basis (forward / right / up) and derive the
        // key / fill / bounce light directions from it - matches the desktop
        // SceneRenderer.ConfigureSurfaceShader exactly.
        Vector3d worldUp = GetFallbackNormalizedAxis(Camera.WorldUpDirection, Camera.UpDirection);
        var (forward, right, up) = BuildCameraLightBasis(Camera, worldUp);
        Vector3d keyLightDir = GetFallbackNormalizedAxis(
            forward * -0.70 + up * 0.55 + right * -0.45, forward * -1.0);
        Vector3d fillLightDir = GetFallbackNormalizedAxis(
            forward * -0.28 + up * 0.12 + right * 0.95, right);
        Vector3d bounceLightDir = GetFallbackNormalizedAxis(
            forward * -0.10 + up * -0.98 + right * 0.18, up * -1.0);

        SetMat4(activeProgram, "uView", view);
        SetMat4(activeProgram, "uProjection", proj);
        SetVec3(activeProgram, "uCameraPos",
            (float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        SetVec3(activeProgram, "uCameraForwardDir",
            (float)forward.X, (float)forward.Y, (float)forward.Z);
        SetVec3(activeProgram, "uWorldUpDir",
            (float)worldUp.X, (float)worldUp.Y, (float)worldUp.Z);
        SetVec3(activeProgram, "uKeyLightDir",
            (float)keyLightDir.X, (float)keyLightDir.Y, (float)keyLightDir.Z);
        SetVec3(activeProgram, "uFillLightDir",
            (float)fillLightDir.X, (float)fillLightDir.Y, (float)fillLightDir.Z);
        SetVec3(activeProgram, "uBounceLightDir",
            (float)bounceLightDir.X, (float)bounceLightDir.Y, (float)bounceLightDir.Z);

        // Lighting strengths - clay mode overrides each one per the desktop
        // ConfigureSurfaceShader clayLighting branch (lines 1917-1929).
        bool clay = a.Mode == RenderMode.Clay;
        SetFloat(activeProgram, "uSurfaceOpacity", clay ? 1.0f : a.SurfaceOpacity);
        SetFloat(activeProgram, "uBaseColorLift", clay ? 0.0f : a.BaseColorLift);
        SetFloat(activeProgram, "uAmbientStrength", clay ? 0.46f : a.AmbientStrength);
        SetFloat(activeProgram, "uHeadlightStrength", clay ? 0.08f : a.HeadlightStrength);
        SetFloat(activeProgram, "uKeyLightStrength", clay ? 0.42f : a.KeyLightStrength);
        SetFloat(activeProgram, "uFillLightStrength", clay ? 0.20f : a.FillLightStrength);
        SetFloat(activeProgram, "uBounceLightStrength", clay ? 0.0f : a.BounceLightStrength);
        SetFloat(activeProgram, "uHemisphereStrength", clay ? 0.36f : a.HemisphereStrength);
        SetFloat(activeProgram, "uSpecularStrength", clay ? 0.0f : a.SpecularStrength);
        SetFloat(activeProgram, "uSpecularPower", clay ? 16.0f : a.SpecularPower);
        SetFloat(activeProgram, "uContourStrength", clay ? 0.10f : a.ContourStrength);
        SetFloat(activeProgram, "uContourPower", clay ? 3.0f : a.ContourPower);
        SetVec3(activeProgram, "uTintColor", 0f, 0f, 0f);
        SetFloat(activeProgram, "uTintStrength", 0f);
        SetInt(activeProgram, "uSelectedMeshIndex", HighlightSelection && !a.OutlineEnabled ? SelectedMeshIndex : 0);
        SetVec3(activeProgram, "uHighlightColor", a.OutlineColor[0], a.OutlineColor[1], a.OutlineColor[2]);

        // AO binding: matches desktop's texture-unit-4 convention.
        // uAmbientOcclusionEnabled gates the sample; uViewportInvSize is the
        // reciprocal of the viewport for gl_FragCoord -> UV math.
        bool useAo = a.AmbientOcclusionEnabled
                     && a.Mode != RenderMode.Wireframe
                     && a.Mode != RenderMode.Clay;
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, aoTextureToBind);
        SetInt(activeProgram, "uAmbientOcclusionTexture", 4);
        SetBool(activeProgram, "uAmbientOcclusionEnabled", useAo);
        SetVec2(activeProgram, "uViewportInvSize",
            _width > 0 ? 1f / _width : 0f,
            _height > 0 ? 1f / _height : 0f);
        _gl.ActiveTexture(TextureUnit.Texture0);

        int modelLoc = _gl.GetUniformLocation(activeProgram.Handle, "uModel");
        int normalLoc = _gl.GetUniformLocation(activeProgram.Handle, "uNormalMatrix");
        int meshIndexLoc = _gl.GetUniformLocation(activeProgram.Handle, "uMeshIndex");
        int colorLoc = _gl.GetUniformLocation(activeProgram.Handle, "uColor");

        // Polygon offset pushes the surface fragments slightly back in depth
        // so edge lines drawn afterward sit cleanly above them without
        // z-fighting. Disabled before the edge pass.
        if (a.EdgesEnabled && a.Mode != RenderMode.Wireframe)
        {
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(a.SurfaceOffsetFactor, a.SurfaceOffsetUnits);
        }

        bool transparentSurface = !clay && a.Mode != RenderMode.Wireframe && a.SurfaceOpacity < 0.999f;
        if (transparentSurface)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            // Keep the visible surface in the depth buffer so the later CAD
            // edge pass cannot draw imported/back-side edges through front
            // faces. Desktop's aggregate path writes depth for the same
            // shaded surface pass; Android must do the same because edges are
            // rendered after surfaces.
            _gl.DepthMask(true);
        }

        foreach (var m in Scene.Meshes)
        {
            float[] model = m.WorldTransform ?? identityModel;
            if (modelLoc >= 0) _gl.UniformMatrix4(modelLoc, true, model);

            float[] normalMatrix = m.WorldTransform is null ? identityNormal : GlesRenderUtil.NormalMatrixFromWorld(m.WorldTransform);
            if (normalLoc >= 0) _gl.UniformMatrix3(normalLoc, true, normalMatrix);

            if (colorLoc >= 0)
            {
                // Clay mode forces every body to use the clay surface color;
                // Shaded mode uses the per-instance diffuse from upload.
                if (clay)
                {
                    var c = a.ClaySurfaceColor;
                    _gl.Uniform4(colorLoc, c[0], c[1], c[2], 1f);
                }
                else
                {
                    var c = m.DiffuseColor;
                    _gl.Uniform4(colorLoc, c[0], c[1], c[2], 1f);
                }
            }
            if (meshIndexLoc >= 0) _gl.Uniform1(meshIndexLoc, m.MeshIndex);
            GlesRenderUtil.ApplyMeshCulling(_gl, m);

            // Wireframe: skip the surface fill, defer to the edge pass.
            if (a.Mode != RenderMode.Wireframe)
                m.Draw();
        }
        GlesRenderUtil.ResetMeshCulling(_gl);

        if (transparentSurface)
        {
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        _gl.Disable(EnableCap.PolygonOffsetFill);

        // ── Edge pass ─────────────────────────────────────────────────
        bool wantEdges = (a.EdgesEnabled && a.Mode == RenderMode.ShadedWithEdges)
                         || a.Mode == RenderMode.Wireframe;
        if (wantEdges && _edgeProgram is not null)
        {
            _edgeProgram.Use();
            SetMat4(_edgeProgram, "uView", view);
            SetMat4(_edgeProgram, "uProjection", proj);
            // In Wireframe mode the surface color drives the wire color so a
            // plain wireframe doesn't disappear against the background.
            var edgeColor = a.Mode == RenderMode.Wireframe ? a.SurfaceColor : a.EdgeColor;
            SetVec4(_edgeProgram, "uEdgeColor", edgeColor[0], edgeColor[1], edgeColor[2], 0.82f);
            SetVec2(_edgeProgram, "uViewportSize", _width, _height);
            SetFloat(_edgeProgram, "uLineWidthPixels", System.Math.Clamp(a.EdgeWidth, 0.05f, 2.0f));
            SetFloat(_edgeProgram, "uDepthBias", System.Math.Max(0.0f, a.EdgeDepthBias));
            SetBool(_edgeProgram, "uSilhouetteEnabled", a.CadEdgeSilhouetteEnabled);
            int edgeModelLoc = _gl.GetUniformLocation(_edgeProgram.Handle, "uModel");
            int edgeNormalLoc = _gl.GetUniformLocation(_edgeProgram.Handle, "uNormalMatrix");

            _gl.Disable(EnableCap.CullFace);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            foreach (var m in Scene.Meshes)
            {
                if (m.EdgeVertexCount == 0) continue;
                float[] model = m.WorldTransform ?? identityModel;
                if (edgeModelLoc >= 0) _gl.UniformMatrix4(edgeModelLoc, true, model);
                float[] normalMatrix = m.WorldTransform is null ? identityNormal : GlesRenderUtil.NormalMatrixFromWorld(m.WorldTransform);
                if (edgeNormalLoc >= 0) _gl.UniformMatrix3(edgeNormalLoc, true, normalMatrix);
                m.DrawEdges();
            }

            _gl.DepthMask(true);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.Enable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
        }

        // Plan 3B: resolve the MSAA color attachment to the default
        // backbuffer. The selection outline post-process draws into the
        // default FBO over the resolved color.
        if (_msaaFbo is not null && _msaaFbo.FboHandle != 0)
            _msaaFbo.ResolveToDefault();

        // ── Selection outline post-process (Phase G) ──────────────────
        // Renders a Sobel-edged outline of the picked mesh over the default
        // framebuffer. The inline color highlight in mesh.gles.frag stays
        // as a fallback when OutlineEnabled is false (host writes
        // SelectedMeshIndex = 0 in that case before this method runs).
        if (a.OutlineEnabled && HighlightSelection && SelectedMeshIndex > 0 && _outlineRenderer is not null)
        {
            _outlineRenderer.Render(Scene, Camera, SelectedMeshIndex,
                a.OutlineColor, a.OutlineThicknessPx, _width, _height);
        }
    }

    private void SetMat4(ShaderProgram program, string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }

    private void SetVec3(ShaderProgram program, string name, float x, float y, float z)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.Uniform3(loc, x, y, z);
    }

    private void SetVec4(ShaderProgram program, string name, float x, float y, float z, float w)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.Uniform4(loc, x, y, z, w);
    }

    private void SetInt(ShaderProgram program, string name, int value)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value);
    }

    private void SetBool(ShaderProgram program, string name, bool value)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value ? 1 : 0);
    }

    private void SetFloat(ShaderProgram program, string name, float value)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value);
    }

    private void SetVec2(ShaderProgram program, string name, float x, float y)
    {
        int loc = _gl!.GetUniformLocation(program.Handle, name);
        if (loc < 0) return;
        _gl.Uniform2(loc, x, y);
    }

    private void EnsureEdgesMatchAppearance(SceneAppearance appearance)
    {
        if (Scene is null)
            return;

        bool sceneChanged = !ReferenceEquals(_edgeSettingsScene, Scene);
        bool settingsChanged =
            sceneChanged
            || System.Math.Abs(_edgeFeatureAngle - appearance.CadEdgeFeatureAngleDegrees) > 0.0001f
            || System.Math.Abs(_edgeCoplanarTolerance - appearance.CadEdgeCoplanarToleranceDegrees) > 0.0001f
            || System.Math.Abs(_edgeWeldTolerance - appearance.CadEdgeWeldToleranceScale) > 0.000000001f
            || _edgeSilhouetteEnabled != appearance.CadEdgeSilhouetteEnabled;

        if (!settingsChanged)
            return;

        Scene.RebuildEdges(
            appearance.CadEdgeFeatureAngleDegrees,
            appearance.CadEdgeCoplanarToleranceDegrees,
            appearance.CadEdgeWeldToleranceScale,
            appearance.CadEdgeSilhouetteEnabled);

        _edgeSettingsScene = Scene;
        _edgeFeatureAngle = appearance.CadEdgeFeatureAngleDegrees;
        _edgeCoplanarTolerance = appearance.CadEdgeCoplanarToleranceDegrees;
        _edgeWeldTolerance = appearance.CadEdgeWeldToleranceScale;
        _edgeSilhouetteEnabled = appearance.CadEdgeSilhouetteEnabled;
    }

    private void ResetMainFramebufferState()
    {
        // Framebuffer is bound by the caller (OnDrawFrame). Do NOT re-bind
        // FBO 0 here - that would discard the MSAA target the caller just
        // selected. This method now only resets pipeline state.
        _gl!.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
    }

    /// <summary>
    /// Port of SceneRenderer.BuildCameraLightBasis (desktop) - returns a
    /// stable orthonormal basis from the camera so per-frame light direction
    /// math stays well-defined even when the camera up axis briefly collapses.
    /// </summary>
    private static (Vector3d Forward, Vector3d Right, Vector3d Up) BuildCameraLightBasis(
        CameraState camera, Vector3d fallbackUp)
    {
        Vector3d forward = GetFallbackNormalizedAxis(camera.Target - camera.Position, Vector3d.UnitY);
        Vector3d up = GetFallbackNormalizedAxis(camera.UpDirection, fallbackUp);
        Vector3d right = Vector3d.Cross(forward, up).Normalized();

        if (right.LengthSquared < CameraBasisEpsilon)
        {
            Vector3d alt = System.Math.Abs(Vector3d.Dot(forward, fallbackUp)) < 0.98
                ? fallbackUp
                : Vector3d.UnitX;
            right = Vector3d.Cross(forward, alt).Normalized();
        }
        if (right.LengthSquared < CameraBasisEpsilon) right = Vector3d.UnitX;

        up = Vector3d.Cross(right, forward).Normalized();
        if (up.LengthSquared < CameraBasisEpsilon) up = fallbackUp;

        return (forward, right, up);
    }

    /// <summary>
    /// Port of SceneRenderer.GetFallbackNormalizedAxis (desktop).
    /// </summary>
    private static Vector3d GetFallbackNormalizedAxis(Vector3d value, Vector3d fallback)
    {
        Vector3d normalized = value.Normalized();
        if (normalized.LengthSquared >= CameraBasisEpsilon) return normalized;
        Vector3d fb = fallback.Normalized();
        return fb.LengthSquared >= CameraBasisEpsilon ? fb : Vector3d.UnitZ;
    }

    /// <summary>
    /// Runs a synchronous offscreen pick at (x, y) in Android viewport
    /// coordinates (top-down). Must be called on the GL render thread,
    /// typically queued from the UI thread via
    /// ViewportSurfaceView.QueueRendererCommand. Returns the 1-based mesh
    /// index that was hit, or null when nothing was hit.
    /// </summary>
    public int? Pick(int x, int y)
    {
        _guard.EnsureOnRenderThread();
        if (_pickRenderer is null || Scene is null || Camera is null) return null;
        return _pickRenderer.Pick(x, y, Scene, Camera);
    }

    private void SetMat4(string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(_meshProgram!.Handle, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }

    private void SetMat3(string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(_meshProgram!.Handle, name);
        if (loc < 0) return;
        _gl.UniformMatrix3(loc, true, m);
    }

    private void SetVec3(string name, float x, float y, float z)
    {
        int loc = _gl!.GetUniformLocation(_meshProgram!.Handle, name);
        if (loc < 0) return;
        _gl.Uniform3(loc, x, y, z);
    }

    private void SetInt(string name, int value)
    {
        int loc = _gl!.GetUniformLocation(_meshProgram!.Handle, name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value);
    }

    /// <summary>
    /// Creates a 1x1 white R8 (single-channel) texture. Bound when SSAO is
    /// disabled so the mesh shader's AO sample returns 1.0 and the multiply
    /// is identity.
    /// </summary>
    private static unsafe uint CreateWhiteTexture(GL gl)
    {
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        byte white = 255;
        gl.TexImage2D(TextureTarget.Texture2D, 0,
            InternalFormat.R8, 1u, 1u, 0,
            PixelFormat.Red, PixelType.UnsignedByte, &white);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static string LoadEmbeddedShader(string fileName)
    {
        var asm = typeof(GlesViewportRenderer).Assembly;
        var name = "FabricationAssistant.Rendering.Gles.Shaders." + fileName;
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded shader not found: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public int Width => _width;
    public int Height => _height;
}

internal sealed class SurfaceViewGlContext : Silk.NET.Core.Contexts.INativeContext
{
    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        addr = nint.Zero;
        if (NativeLibrary.TryLoad("libGLESv3.so", out var libv3) &&
            NativeLibrary.TryGetExport(libv3, proc, out var sym))
        {
            addr = sym;
        }
        if (addr == nint.Zero && NativeLibrary.TryLoad("libGLESv2.so", out var libv2))
        {
            if (NativeLibrary.TryGetExport(libv2, proc, out var sym2)) addr = sym2;
        }
        return addr != nint.Zero;
    }

    public nint GetProcAddress(string proc, int? slot = null)
    {
        if (TryGetProcAddress(proc, out var addr, slot)) return addr;
        throw new EntryPointNotFoundException("GLES function not found: " + proc);
    }

    public void Dispose() { }
}
