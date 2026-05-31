using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Numerics;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Presentation;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Sections;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Plan 2A scene renderer. Compiles the mesh + pick programs, drains a
/// thread-safe command queue at the top of every frame (so UI-thread import
/// work can enqueue GL upload commands), then draws the scene with the
/// current CameraState. Plan 2E adds the offscreen pick FBO and selection
/// highlight uniforms.
/// </summary>
public sealed class GlesViewportRenderer : IDisposable
{
    private const double CameraBasisEpsilon = 1e-8;
    private const float HiddenAlphaThreshold = 0.003f;
    private const float OpaqueAlphaThreshold = 0.999f;
    private const int SectionCapGeometryDebounceMilliseconds = 120;

    private readonly GlThreadGuard _guard = new();
    private GL? _gl;
    private ShaderProgram? _meshProgram;
    private ShaderProgram? _edgeProgram;
    private GlesPickRenderer? _pickRenderer;
    private GlesGridRenderer? _gridRenderer;
    private GlesNormalDepthRenderer? _normalDepthRenderer;
    private GlesSsaoRenderer? _ssaoRenderer;
    private ShaderProgram? _silhouetteOverlayProgram;
    private uint _silhouetteOverlayVao;
    private uint _silhouetteOverlayVbo;
    private GlesOutlineRenderer? _outlineRenderer;
    private GlesMeasurementOverlay? _measurementOverlay;
    private GlesFaceHighlightOverlay? _faceHighlightOverlay;
    private GlesSectionOverlay? _sectionOverlay;
    private GlesAxisTriadOverlay? _axisTriadOverlay;
    private SectionCapGeometryCache? _sectionCapGeometryCache;
    private readonly object _sectionCapGeometryBuildSync = new();
    private SectionCapGeometryBuildInFlight? _sectionCapGeometryBuildInFlight;
    private SectionCapGeometryBuildResult? _sectionCapGeometryBuildCompleted;
    private int _sectionCapGeometryBuildGeneration;
    private int _sectionCapDebouncePlaneHash = int.MinValue;
    private long _sectionCapDebounceStartedTicks;
    private long _lastSectionCapDebounceLogTicks;
    private long _lastSectionCapInteractionLogTicks;
    private long _lastSectionCapInFlightLogTicks;
    private long _lastSectionCapRenderTimingLogTicks;
    private long _lastSectionCapCancelLogTicks;
    private int _sectionCapDiagnosticFramesRemaining;
    private string _sectionCapDiagnosticReason = "";
    private long _lastMsaaBypassLogTicks;
    private MsaaSceneFramebuffer? _msaaFbo;
    // FXAA post-process: the resolved scene+overlays are composited into this
    // color FBO, then an FXAA pass writes to the default backbuffer. Allocated
    // lazily only when the FXAA toggle is on (default off).
    private ShaderProgram? _fxaaProgram;
    private uint _compositeFbo;
    private uint _compositeTex;
    private int _compositeWidth;
    private int _compositeHeight;
    private uint _whiteAoTexture;
    private bool _initialized;
    private int _width;
    private int _height;
    private GpuScene? _edgeSettingsScene;
    private float _edgeFeatureAngle = float.NaN;
    private float _edgeCoplanarTolerance = float.NaN;
    private float _edgeWeldTolerance = float.NaN;
    private int _failedMsaaWidth;
    private int _failedMsaaHeight;
    private int _failedMsaaSamples;
    // S6-1: an explicit "an allocation failed" flag, kept separate from
    // _failedMsaaSamples so a recorded failure at 0 samples (MSAA Off) is not
    // confused with the success/reset state (which also leaves samples at 0).
    private bool _msaaAllocationFailed;
    private int _lastLoggedMsaaRequested = int.MinValue;
    private int _lastLoggedMsaaEffective = int.MinValue;
    private int _lastLoggedMsaaMaxSamples = int.MinValue;
    private long _lastSlowFrameLogTicks;
    private readonly FrameTimingAccumulator _frameTiming = new();
    private SceneAppearance _appearance = SceneAppearance.CreateDefault();
    private readonly float[] _viewMatrixScratch = new float[16];
    private readonly float[] _projectionMatrixScratch = new float[16];
    private readonly float[] _sectionUniformScratch = new float[32];
    private readonly List<GpuMesh> _opaqueSurfaceMeshes = new();
    private readonly List<GpuMesh> _transparentSurfaceMeshes = new();
    private int _selectedMeshIndex;
    private IReadOnlyList<int> _selectedMeshIndices = Array.Empty<int>();
    private HashSet<int> _selectedMeshIndexLookup = new();
    private IReadOnlyList<int> _xrayOpaqueNodeIds = Array.Empty<int>();
    private IReadOnlyList<int> _xrayBackgroundNodeIds = Array.Empty<int>();
    private HashSet<int> _xrayOpaqueNodeIdLookup = new();
    private HashSet<int> _xrayBackgroundNodeIdLookup = new();
    private int _lastSurfaceTransparentMeshCount;
    private int _lastSurfaceHiddenMeshCount;
    private int _lastFrustumCulledMeshCount;
    private readonly float[] _frustumPlanes = new float[24]; // 6 planes x (a,b,c,d), normalized
    private readonly float[] _frustumViewProjScratch = new float[16];
    private SsaoStateKey _lastLoggedSsaoState;
#if DEBUG || FA_RENDER_DIAGNOSTICS
    private SsaoDiagnosticsKey _lastSsaoDiagnostics;
#endif
    private TransparencyStateKey _lastLoggedTransparencyState;
    private int _interactiveNavigationActive;
    private GpuScene? _scene;

    public GpuScene? Scene
    {
        get => _scene;
        set
        {
            if (ReferenceEquals(_scene, value))
                return;

            _scene = value;
            InvalidateSectionCapGeometryBuilds("scene changed");
        }
    }

    public CameraState? Camera { get; set; }
    public ConcurrentQueue<Action<GL>>? CommandQueue { get; set; }
    public event Action? FrameRendered;
    public event Action<int>? DelayedRenderRequested;

    /// <summary>
    /// 1-based mesh index to highlight in the next frame, or 0 for none.
    /// Set by the host (MainActivity) after a successful tap-pick.
    /// </summary>
    public int SelectedMeshIndex
    {
        get => _selectedMeshIndex;
        set
        {
            _selectedMeshIndex = System.Math.Max(0, value);
            SelectedMeshIndices = _selectedMeshIndex > 0
                ? new[] { _selectedMeshIndex }
                : Array.Empty<int>();
        }
    }

    /// <summary>
    /// 1-based mesh indices that belong to the current logical selection.
    /// Group and assembly selections can cover more than one rendered mesh.
    /// </summary>
    public IReadOnlyList<int> SelectedMeshIndices
    {
        get => _selectedMeshIndices;
        set
        {
            int[] snapshot = value is null
                ? Array.Empty<int>()
                : value.Where(index => index > 0).Distinct().OrderBy(index => index).ToArray();
            _selectedMeshIndices = snapshot;
            _selectedMeshIndexLookup = snapshot.ToHashSet();
            _selectedMeshIndex = snapshot.Length == 0
                ? 0
                : _selectedMeshIndex > 0 && _selectedMeshIndexLookup.Contains(_selectedMeshIndex)
                    ? _selectedMeshIndex
                    : snapshot[0];
        }
    }

    /// <summary>
    /// 1-based mesh index under a hover-capable pointer such as Samsung S Pen,
    /// or 0 when no hover target is active.
    /// </summary>
    public int HoveredMeshIndex { get; set; }

    /// <summary>
    /// True while the UI thread is actively changing the camera from a touch
    /// gesture. Expensive screen-space effects are skipped until the gesture
    /// ends so camera motion stays responsive on mobile GPUs.
    /// </summary>
    public bool InteractiveNavigationActive
    {
        get => Volatile.Read(ref _interactiveNavigationActive) != 0;
        set
        {
            int active = value ? 1 : 0;
            if (Interlocked.Exchange(ref _interactiveNavigationActive, active) == active)
                return;

            LogSectionCapInteractionState(value);
        }
    }

    /// <summary>
    /// Per-frame appearance state. Mirrors the desktop SceneAppearanceViewModel.
    /// PreferencesBottomSheet writes through AppSettings; MainActivity rebuilds
    /// this struct on every change via AppSettings.Apply.
    /// Public callers get and set defensive snapshots so array fields cannot
    /// be mutated across UI/render-thread boundaries after assignment.
    /// </summary>
    public SceneAppearance Appearance
    {
        get => _appearance.CreateRendererSnapshot();
        set
        {
            _appearance = value.CreateRendererSnapshot();
            ResetSlowFrameLogThrottle();
        }
    }

    public float XrayIsolationOpacity { get; set; } = 0.18f;

    public Vector3 DimensionHighlightColor { get; set; } = new(1.0f, 0.5019608f, 0.2509804f);

    public Vector4 MeasurementFaceSelectionColor { get; set; } = new(0.18f, 0.83f, 0.75f, 0.38f);

    public Vector4 MeasurementFaceHoverColor { get; set; } = new(1.00f, 0.58f, 0.16f, 0.30f);

    public float SectionPlaneSizeFraction { get; set; } = 0.025f;

    public Vector4 SectionFillColor { get; set; } = new(0.20f, 0.80f, 0.40f, 0.18f);

    public Vector4 SectionEdgeColor { get; set; } = new(0.20f, 0.80f, 0.40f, 0.80f);

    public float SectionEdgeWidth { get; set; } = 2.4f;

    public Vector4 SectionEdgeHighlightColor { get; set; } = new(1.00f, 0.5019608f, 0.2509804f, 1.00f);

    public Vector4 SectionCapColor { get; set; } = new(0.85f, 0.85f, 0.80f, 1.00f);

    public Vector4 SectionPlacementPreviewColor { get; set; } = new(1.00f, 0.85f, 0.20f, 1.00f);

    public Vector4 SectionPlacementHoverColor { get; set; } = new(1.00f, 0.85f, 0.20f, 0.95f);

    public Vector4 SectionGizmoAxisXColor { get; set; } = new(1.00f, 0.25f, 0.25f, 0.95f);

    public Vector4 SectionGizmoAxisYColor { get; set; } = new(0.25f, 1.00f, 0.35f, 0.95f);

    public Vector4 SectionGizmoAxisZColor { get; set; } = new(0.30f, 0.60f, 1.00f, 0.95f);

    public Vector4 SectionGizmoArcXColor { get; set; } = new(1.00f, 0.35f, 0.35f, 0.40f);

    public Vector4 SectionGizmoArcYColor { get; set; } = new(0.35f, 1.00f, 0.40f, 0.40f);

    public Vector4 SectionGizmoArcZColor { get; set; } = new(0.40f, 0.65f, 1.00f, 0.40f);

    public Vector4 SectionGizmoHoverColor { get; set; } = new(1.00f, 0.90f, 0.20f, 1.00f);

    public Vector4 SectionGizmoActiveColor { get; set; } = new(1.00f, 1.00f, 1.00f, 1.00f);

    public IReadOnlyList<int> XrayOpaqueNodeIds
    {
        get => _xrayOpaqueNodeIds;
        set
        {
            _xrayOpaqueNodeIds = value is null ? Array.Empty<int>() : value.ToArray();
            _xrayOpaqueNodeIdLookup = _xrayOpaqueNodeIds.ToHashSet();
        }
    }

    public IReadOnlyList<int> XrayBackgroundNodeIds
    {
        get => _xrayBackgroundNodeIds;
        set
        {
            _xrayBackgroundNodeIds = value is null ? Array.Empty<int>() : value.ToArray();
            _xrayBackgroundNodeIdLookup = _xrayBackgroundNodeIds.ToHashSet();
            if (_pickRenderer is not null)
                _pickRenderer.XrayBackgroundNodeIds = _xrayBackgroundNodeIds;
        }
    }

    public IReadOnlyList<PresentationSnapshot> MeasurementPresentation { get; set; }
        = Array.Empty<PresentationSnapshot>();

    public IReadOnlyList<FaceHighlight> FaceHighlights { get; set; }
        = Array.Empty<FaceHighlight>();

    private IReadOnlyList<GlesSectionPlane> _sectionPlanes = Array.Empty<GlesSectionPlane>();
    private IReadOnlyList<GlesSectionVisualPlane> _sectionVisualPlanes = Array.Empty<GlesSectionVisualPlane>();

    /// <summary>
    /// Section clip planes read by the GL thread. The setter snapshots the
    /// sequence so UI-thread gizmo edits cannot mutate data mid-frame.
    /// </summary>
    public IReadOnlyList<GlesSectionPlane> SectionPlanes
    {
        get => _sectionPlanes;
        set => _sectionPlanes = value is null ? Array.Empty<GlesSectionPlane>() : value.ToArray();
    }

    /// <summary>
    /// Visual section planes read by the GL thread. The setter snapshots the
    /// sequence for the same cross-thread safety reason as SectionPlanes.
    /// </summary>
    public IReadOnlyList<GlesSectionVisualPlane> SectionVisualPlanes
    {
        get => _sectionVisualPlanes;
        set => _sectionVisualPlanes = value is null ? Array.Empty<GlesSectionVisualPlane>() : value.ToArray();
    }

    public bool SectionFillVisible { get; set; } = true;

    public bool SectionEdgesVisible { get; set; } = true;

    public bool SectionCurvesVisible { get; set; } = true;

    public bool SectionCapsVisible { get; set; } = true;

    public void RequestSectionCapDiagnostics(string reason)
    {
        _sectionCapDiagnosticReason = string.IsNullOrWhiteSpace(reason)
            ? "unspecified"
            : reason.Trim();
        Interlocked.Exchange(ref _sectionCapDiagnosticFramesRemaining, 8);
        LogSectionCapState("diagnostic-request");
    }

    public IReadOnlyList<Vector3> SectionPlacementCommittedPicks { get; set; } = Array.Empty<Vector3>();

    public Vector3? SectionPlacementHoverPoint { get; set; }

    public Vector3 SectionGizmoAnchor { get; set; }

    public Vector3 SectionGizmoAxisX { get; set; } = Vector3.UnitX;

    public Vector3 SectionGizmoAxisY { get; set; } = Vector3.UnitY;

    public Vector3 SectionGizmoAxisZ { get; set; } = Vector3.UnitZ;

    public float SectionGizmoScale { get; set; }

    public GlesTransformGizmoHandle SectionGizmoHovered { get; set; }

    public GlesTransformGizmoHandle SectionGizmoActive { get; set; }

    public Vector3 BodyMoveGizmoAnchor { get; set; }

    public Vector3 BodyMoveGizmoAxisX { get; set; } = Vector3.UnitX;

    public Vector3 BodyMoveGizmoAxisY { get; set; } = Vector3.UnitY;

    public Vector3 BodyMoveGizmoAxisZ { get; set; } = Vector3.UnitZ;

    public float BodyMoveGizmoScale { get; set; }

    public GlesTransformGizmoHandle BodyMoveGizmoHovered { get; set; }

    public GlesTransformGizmoHandle BodyMoveGizmoActive { get; set; }

    // S6-F9: removed the dead ShowGrid/ClearColor Appearance aliases (no references;
    // the renderer reads Appearance directly). HighlightSelection is a live property
    // used across the mesh/outline passes and set from the host, so it stays.
    public bool HighlightSelection { get; set; } = true;

    public void OnSurfaceCreated()
    {
        _guard.InitializeOnCurrentThread();
        // S8#3: DisposeResources below already disposes _msaaFbo (TryDispose ->
        // Destroy + null), so the explicit Reset() here was redundant.
        DisposeResources(disposeScene: false);

        _gl = GL.GetApi(new SurfaceViewGlContext());

        // The GL context was (re)created, so any process-static GPU buffer
        // names captured under a previous context are now dead. Forget the
        // shared edge-ribbon quad names here, before any scene re-upload, so
        // the next UploadEdges rebuilds them against this fresh context
        // instead of binding stale handles (which corrupts/loses CAD edges).
        GpuMesh.ResetStaticEdgeQuad();

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

        // SSAO pipeline: normal-depth pass + SSAO + separable blur.
        var ndVs = LoadEmbeddedShader("normal_depth.gles.vert");
        var ndFs = LoadEmbeddedShader("normal_depth.gles.frag");
        _normalDepthRenderer = new GlesNormalDepthRenderer(_gl, ndVs, ndFs);

        var fsVs = LoadEmbeddedShader("fullscreen.gles.vert");
        var ssaoFs = LoadEmbeddedShader("ssao.gles.frag");
        var ssaoBlurFs = LoadEmbeddedShader("ssao_blur.gles.frag");
        _ssaoRenderer = new GlesSsaoRenderer(_gl, fsVs, ssaoFs, ssaoBlurFs);

        // Screen-space silhouette overlay driven by the normal-depth pre-pass.
        var silhouetteFs = LoadEmbeddedShader("silhouette_overlay.gles.frag");
        _silhouetteOverlayProgram = new ShaderProgram(_gl, "silhouette_overlay", fsVs, silhouetteFs);
        (_silhouetteOverlayVao, _silhouetteOverlayVbo) = GlesFullscreenTriangle.Create(_gl);

        // FXAA post-process (reuses the fullscreen vertex shader + triangle VAO).
        var fxaaFs = LoadEmbeddedShader("fxaa.gles.frag");
        _fxaaProgram = new ShaderProgram(_gl, "fxaa", fsVs, fxaaFs);

        // 1x1 white AO texture - bound when SSAO is off so the mesh shader's
        // AO multiply is identity.
        _whiteAoTexture = CreateWhiteTexture(_gl);

        // Selection outline. Reuses pick.gles.vert for the mask and
        // fullscreen.gles.vert for the Sobel composite.
        var maskFs = LoadEmbeddedShader("mask.gles.frag");
        var outlineFs = LoadEmbeddedShader("outline.gles.frag");
        _outlineRenderer = new GlesOutlineRenderer(_gl, pickVs, maskFs, fsVs, outlineFs);

        var measureVs = LoadEmbeddedShader("measure_color.gles.vert");
        var measureFs = LoadEmbeddedShader("measure_color.gles.frag");
        var measureDiskVs = LoadEmbeddedShader("measure_disk.gles.vert");
        var measureDiskFs = LoadEmbeddedShader("measure_disk.gles.frag");
        _measurementOverlay = new GlesMeasurementOverlay(_gl, measureVs, measureFs, measureDiskVs, measureDiskFs);
        _faceHighlightOverlay = new GlesFaceHighlightOverlay(_gl, measureVs, measureFs);
        _sectionOverlay = new GlesSectionOverlay(_gl, measureVs, measureFs, edgeVs, edgeFs);

        var axisTriadVs = LoadEmbeddedShader("axis_triad.gles.vert");
        var axisTriadFs = LoadEmbeddedShader("axis_triad.gles.frag");
        _axisTriadOverlay = new GlesAxisTriadOverlay(_gl, axisTriadVs, axisTriadFs);

        // Plan 3B: offscreen multisample FBO. All scene passes (grid, mesh,
        // edge) render into this FBO and the resolved color is blitted to
        // the default backbuffer before the selection outline post-process.
        _msaaFbo = new MsaaSceneFramebuffer(_gl);
        _failedMsaaWidth = 0;
        _failedMsaaHeight = 0;
        _failedMsaaSamples = 0;
        _msaaAllocationFailed = false;
        _lastLoggedMsaaRequested = int.MinValue;
        _lastLoggedMsaaEffective = int.MinValue;
        _lastLoggedMsaaMaxSamples = int.MinValue;
        _lastLoggedSsaoState = default;
#if DEBUG || FA_RENDER_DIAGNOSTICS
        _lastSsaoDiagnostics = default;
#endif
        _lastLoggedTransparencyState = default;

        bool hadSceneFromPreviousContext = Scene is not null;
        GpuScene? staleScene = Scene;
        Scene = null;
        TryDispose(staleScene);
        SelectedMeshIndex = 0;
        HoveredMeshIndex = 0;
        _edgeSettingsScene = null;
        if (hadSceneFromPreviousContext)
        {
            Android.Util.Log.Warn(
                "FA.Renderer",
                "GL context recreated; dropped stale GPU scene handles and waiting for host re-upload.");
        }

        _initialized = true;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        _guard.EnsureOnRenderThread();
        if (_gl is null)
        {
            // S6-F6: unexpected callback ordering (surface-changed before the GL
            // context exists). Silently skipping leaves a stale viewport/FBO size with
            // no diagnostic, so log it.
            Android.Util.Log.Warn(
                "FA.Renderer",
                $"OnSurfaceChanged({width}x{height}) skipped: GL context not created yet.");
            return;
        }
        _width = System.Math.Max(0, width);
        _height = System.Math.Max(0, height);
        if (_width <= 0 || _height <= 0)
            return;

        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _pickRenderer?.Resize(_width, _height);
        _normalDepthRenderer?.Resize(_width, _height);
        _ssaoRenderer?.Resize(_width, _height, _appearance.AoFullResolution);
        _outlineRenderer?.Resize(_width, _height);
        // Only allocate the MSAA FBO when MSAA is on. Mirrors the conditional
        // in OnDrawFrame so a resize while MSAA is Off does not eagerly
        // create the FBO.
        var appearance = _appearance;
        if (appearance.MsaaSamples > 1)
            TryPrepareMsaaFramebuffer(appearance);
        else
            _msaaFbo?.Destroy();
    }

    public void TrimTransientGpuResources()
    {
        _guard.EnsureOnRenderThread();
        if (_gl is null)
            return;

        _normalDepthRenderer?.TrimFramebuffers();
        _ssaoRenderer?.TrimFramebuffers();
        _outlineRenderer?.TrimFramebuffers();
        _msaaFbo?.Destroy();
#if DEBUG || FA_RENDER_DIAGNOSTICS
        _lastSsaoDiagnostics = default;
#endif
        Android.Util.Log.Info("FA.Renderer", "Trimmed transient GPU framebuffers under memory pressure.");
    }

    public void OnDrawFrame()
    {
        _guard.EnsureOnRenderThread();
        if (!_initialized || _gl is null || _meshProgram is null) return;
        long frameStart = Stopwatch.GetTimestamp();
        int queuedCommandCount = 0;

        if (CommandQueue is { } q)
        {
            while (q.TryDequeue(out var cmd))
            {
                queuedCommandCount++;
                try { cmd(_gl); }
                catch (Exception ex) { Android.Util.Log.Error("FA.Renderer", Java.Lang.Throwable.FromException(ex), $"GL command failed ({ex.GetType().Name}): {ex.Message}"); }
            }
        }
        long afterQueue = Stopwatch.GetTimestamp();

        var a = _appearance;
        CameraState? camera = SnapshotCamera();
        bool interactive = InteractiveNavigationActive;
        bool lightweightNavigationActive = interactive && a.LightweightNavigationEnabled;
        EnsureEdgesMatchAppearance(a);

        if (_width <= 0 || _height <= 0)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return;
        }

        // Supersampling (SSAA): on the settled (non-interactive) frame, render the
        // scene, AO and silhouette into an offscreen buffer at RenderScale x the
        // screen size, then linear-downsample to the backbuffer. This is done by
        // temporarily swapping _width/_height to the larger render size for the
        // offscreen passes; the final UI overlays (selection outline, axis triad)
        // and any picking run at the true screen size, restored before they draw.
        int screenWidth = _width;
        int screenHeight = _height;
        bool wantSsaa = a.RenderScale > 1.01f
            && !lightweightNavigationActive
            && Scene is not null
            && camera is not null;
        if (wantSsaa)
        {
            _width = System.Math.Max(1, (int)System.Math.Round(screenWidth * a.RenderScale));
            _height = System.Math.Max(1, (int)System.Math.Round(screenHeight * a.RenderScale));
        }

        // SSAO pre-pass: render scene normals+depth, then compute occlusion.
        // Bound back to the default FBO before the main mesh pass, which
        // samples the resulting AO texture.
        long ssaoStart = Stopwatch.GetTimestamp();
        uint aoTextureToBind = _whiteAoTexture;
        bool ssaoActive = false;
        bool normalDepthRanThisFrame = false;
        string ssaoInactiveReason = GetSsaoInactiveReason(a, camera);
        if (ssaoInactiveReason.Length == 0 && lightweightNavigationActive)
            ssaoInactiveReason = "interactive navigation";

        // SSAO is eligible to run when nothing vetoed it above.
        bool ssaoEligible = ssaoInactiveReason.Length == 0;

        // S19#1: the screen-space silhouette overlay also consumes the
        // normal-depth pre-pass, so the pre-pass must be able to run even when
        // AO is off. These are the same guards the overlay itself applies
        // below, so the pre-pass runs exactly when either consumer needs it.
        bool silhouettePrepassWanted = a.CadEdgeSilhouetteEnabled
            && a.Mode != RenderMode.Clay
            && a.Mode != RenderMode.Wireframe
            && SectionPlanes.Count == 0
            && !lightweightNavigationActive;

        if ((ssaoEligible || silhouettePrepassWanted)
            && Scene is { } ssaoScene
            && camera is { } ssaoCamera
            && _normalDepthRenderer is { } normalDepth)
        {
            bool collectSsaoDiagnostics = ShouldCollectSsaoDiagnostics(a, true, "active");
            ResetMainFramebufferState();
            normalDepth.SectionPlanes = SectionPlanes;
            normalDepth.Render(ssaoScene, ssaoCamera, _width, _height, a, collectSsaoDiagnostics);
            normalDepthRanThisFrame = true;

            // SSAO consumption stays gated on SSAO eligibility; a pre-pass run
            // only for silhouettes leaves AO off (aoTextureToBind stays white).
            if (ssaoEligible && _ssaoRenderer is { } ssao)
            {
                ssao.Resize(_width, _height, a.AoFullResolution);
                ssao.Render(
                    normalDepth.NormalTexture,
                    normalDepth.DepthTexture,
                    normalDepth.LinearDepthMin,
                    normalDepth.LinearDepthMax,
                    ssaoCamera,
                    ssaoScene.Bounds,
                    a,
                    collectSsaoDiagnostics);
                if (ssao.AoTexture != 0)
                {
                    aoTextureToBind = ssao.AoTexture;
                    ssaoActive = true;
                }
                else
                {
                    ssaoInactiveReason = "renderer produced no AO texture";
                }
            }

            ResetMainFramebufferState();
        }
        LogSsaoState(a, ssaoActive, ssaoInactiveReason, aoTextureToBind);
        long afterSsao = Stopwatch.GetTimestamp();

        bool msaaBypassedForNavigation = lightweightNavigationActive && a.MsaaSamples > 1;
        if (msaaBypassedForNavigation && ShouldLogThrottled(ref _lastMsaaBypassLogTicks, 1000.0))
        {
            Android.Util.Log.Info(
                "FA.Renderer",
                $"MSAA samples reduced during lightweight navigation: requested={a.MsaaSamples}x, depth remains D32FS8, viewport={_width}x{_height}.");
        }

        SceneAppearance renderTargetAppearance = a;
        if (msaaBypassedForNavigation)
            renderTargetAppearance.MsaaSamples = 0;
        // Under SSAA the downsample already removes most aliasing, so cap hardware
        // MSAA at 2x to keep the (now 2.25x larger) offscreen renderbuffers within
        // a sane memory budget. The MsaaSceneFramebuffer sample fallback handles
        // the rest if even that does not fit.
        if (wantSsaa && renderTargetAppearance.MsaaSamples > 2)
            renderTargetAppearance.MsaaSamples = 2;

        bool useMsaaFbo = TryPrepareMsaaFramebuffer(renderTargetAppearance);
        // SSAA needs the offscreen path so it can resolve into the composite and
        // downsample to the screen. If the (render-size) MSAA FBO could not be
        // prepared, abandon SSAA and restore the true screen size so the frame
        // falls back to the normal direct path.
        bool useSsaa = wantSsaa && useMsaaFbo && EnsureCompositeFramebuffer();
        if (wantSsaa && !useSsaa)
        {
            _width = screenWidth;
            _height = screenHeight;
        }
        // FXAA engages only on the normal MSAA path, outside lightweight
        // navigation, and not together with SSAA (which already supersamples
        // every edge). The composite FBO is shared with SSAA.
        bool useFxaa = a.FxaaEnabled
            && useMsaaFbo
            && !useSsaa
            && _fxaaProgram is not null
            && !lightweightNavigationActive
            && EnsureCompositeFramebuffer();
        // Both post-resolve modes route the scene through the offscreen composite.
        bool useComposite = useSsaa || useFxaa;
        bool drawEdgesThisFrame = false;
        long sceneStart = afterSsao;
        long beforeEdges = afterSsao;
        long afterEdges = afterSsao;
        bool retriedWithoutMsaa = false;

        while (true)
        {
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        // Bind the appropriate render target. With useMsaaFbo we render
        // into the offscreen scene FBO, which uses D32FS8 even when the
        // requested sample count is reduced to single-sample.
        if (useMsaaFbo && _msaaFbo!.FboHandle != 0)
            _msaaFbo.Bind();
        else
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        ResetMainFramebufferState();

        // Clay mode swaps to its own background so the scene reads as a
        // matte studio shot rather than the operator's main background.
        float[] bg = a.Mode == RenderMode.Clay ? a.ClayBackgroundColor : a.BackgroundColor;
        _gl.ClearColor(bg[0], bg[1], bg[2], 1.0f);
        if (useMsaaFbo)
        {
            // The MSAA FBO has a depth-stencil attachment. Clear stencil here
            // so section caps start each frame from a clean mask.
            _gl.ClearStencil(0);
            _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit));
        }
        else
        {
            // Default backbuffer path - identical to pre-Plan-3B behavior.
            _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        }

        if (Scene is null || camera is null)
        {
            if (useMsaaFbo && _msaaFbo!.FboHandle != 0)
                TryResolveMsaaFramebuffer(a, 0u);
            else
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return;
        }

        // Ground grid first so the depth pre-fill sits behind opaque meshes
        // (which will overwrite the grid where they cover it). Skipped when
        // SceneAppearance.ShowGrid is false.
        if (a.ShowGrid)
            _gridRenderer?.Draw(Scene, camera, _width, _height, a);

        // Single mesh shader for both Shaded and Clay - per-mode uniform
        // overrides happen below (mirrors the desktop's
        // SceneRenderer.ConfigureSurfaceShader clayLighting branch).
        _meshProgram.Use();
        ShaderProgram activeProgram = _meshProgram;

        var aspect = (float)_width / _height;
        var identityModel = ViewportCameraMath.IdentityModelMatrix();
        var identityNormal = ViewportCameraMath.NormalMatrixFromIdentity();
        ViewportCameraMath.FillViewMatrix(camera, _viewMatrixScratch);
        ViewportCameraMath.FillProjectionMatrix(camera, aspect, _projectionMatrixScratch);
        var view = _viewMatrixScratch;
        var proj = _projectionMatrixScratch;
        BuildFrustumPlanes(view, proj);
        _lastFrustumCulledMeshCount = 0;

        // Build a stable camera basis (forward / right / up) and derive the
        // key / fill / bounce light directions from it - matches the desktop
        // SceneRenderer.ConfigureSurfaceShader exactly.
        Vector3d worldUp = GetFallbackNormalizedAxis(camera.WorldUpDirection, camera.UpDirection);
        var (forward, right, up) = BuildCameraLightBasis(camera, worldUp);
        Vector3d keyLightDir = GetFallbackNormalizedAxis(
            forward * -0.70 + up * 0.55 + right * -0.45, forward * -1.0);
        Vector3d fillLightDir = GetFallbackNormalizedAxis(
            forward * -0.28 + up * 0.12 + right * 0.95, right);
        Vector3d bounceLightDir = GetFallbackNormalizedAxis(
            forward * -0.10 + up * -0.98 + right * 0.18, up * -1.0);

        SetMat4(activeProgram, "uView", view);
        SetMat4(activeProgram, "uProjection", proj);
        SetVec3(activeProgram, "uCameraPos",
            (float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
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
        SetFloat(activeProgram, "uSurfaceOpacity", 1.0f);
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
        SetInt(activeProgram, "uHoveredMeshIndex", HighlightSelection ? HoveredMeshIndex : 0);
        SetVec3(activeProgram, "uHighlightColor", a.OutlineColor[0], a.OutlineColor[1], a.OutlineColor[2]);
        SetVec3(activeProgram, "uHoverColor", a.HoverOutlineColor[0], a.HoverOutlineColor[1], a.HoverOutlineColor[2]);
        SetFloat(activeProgram, "uHoverTintStrength", a.HoverTintStrength);
        SetSectionUniforms(activeProgram);

        // AO binding: matches desktop's texture-unit-4 convention.
        // uAmbientOcclusionEnabled gates the sample; uViewportInvSize is the
        // reciprocal of the viewport for gl_FragCoord -> UV math.
        //
        // The AO texture is rendered at half resolution; the mesh shader
        // performs a depth-aware bilateral upsample using uAmbientOcclusionDepthTexture
        // (the normal+depth pre-pass output, sampled at the same UV).
        bool useAo = ssaoActive
                     && a.AmbientOcclusionEnabled
                     && a.Mode != RenderMode.Wireframe;
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, aoTextureToBind);
        SetInt(activeProgram, "uAmbientOcclusionTexture", 4);
        _gl.ActiveTexture(TextureUnit.Texture5);
        uint aoDepthTextureToBind = useAo && _normalDepthRenderer is not null
            ? _normalDepthRenderer.NormalTexture
            : _whiteAoTexture;
        _gl.BindTexture(TextureTarget.Texture2D, aoDepthTextureToBind);
        SetInt(activeProgram, "uAmbientOcclusionDepthTexture", 5);
        SetBool(activeProgram, "uAmbientOcclusionEnabled", useAo);
        SetVec2(activeProgram, "uViewportInvSize",
            _width > 0 ? 1f / _width : 0f,
            _height > 0 ? 1f / _height : 0f);
        _gl.ActiveTexture(TextureUnit.Texture0);

        int modelLoc = activeProgram.UniformLocation("uModel");
        int normalLoc = activeProgram.UniformLocation("uNormalMatrix");
        int meshIndexLoc = activeProgram.UniformLocation("uMeshIndex");
        int colorLoc = activeProgram.UniformLocation("uColor");
        int selectedMeshIndexLoc = activeProgram.UniformLocation("uSelectedMeshIndex");
        if (selectedMeshIndexLoc >= 0)
            _gl.Uniform1(selectedMeshIndexLoc, HighlightSelection ? SelectedMeshIndex : 0);

        bool clayEdges = a.Mode == RenderMode.Clay && a.ClayFeatureEdgesEnabled;
        bool sectionClippingActive = SectionPlanes.Count > 0;
        drawEdgesThisFrame = a.Mode == RenderMode.Wireframe
                             || (!lightweightNavigationActive
                                 && (clayEdges
                                     || (a.EdgesEnabled && a.Mode == RenderMode.ShadedWithEdges)));

        // Polygon offset pushes the surface fragments slightly back in depth
        // so edge lines drawn afterward sit cleanly above them without
        // z-fighting. Section cuts need unbiased surface depth; otherwise
        // internal B-Rep edge ribbons behind the cut can pass through the
        // visible face. This mirrors the desktop renderer's section behavior.
        bool useSurfaceDepthOffsetForEdges =
            drawEdgesThisFrame
            && a.Mode != RenderMode.Wireframe
            && !sectionClippingActive;
        bool surfaceDepthOffsetEnabled = false;
        if (useSurfaceDepthOffsetForEdges)
        {
            _gl.Enable(EnableCap.PolygonOffsetFill);
            _gl.PolygonOffset(a.SurfaceOffsetFactor, a.SurfaceOffsetUnits);
            surfaceDepthOffsetEnabled = true;
        }

        _lastSurfaceTransparentMeshCount = 0;
        _lastSurfaceHiddenMeshCount = 0;
        try
        {
            if (a.Mode != RenderMode.Wireframe)
            {
                PrepareSurfacePassMeshes(Scene.Meshes, a, clay, _opaqueSurfaceMeshes, _transparentSurfaceMeshes);

                if (_transparentSurfaceMeshes.Count == 0)
                {
                    _gl.Disable(EnableCap.Blend);
                    _gl.DepthMask(true);
                    foreach (var m in _opaqueSurfaceMeshes)
                        DrawSurfaceMesh(m, a, clay, modelLoc, normalLoc, colorLoc, meshIndexLoc, selectedMeshIndexLoc, identityModel, identityNormal);
                }
                else
                {
                    if (_opaqueSurfaceMeshes.Count > 0)
                    {
                        _gl.Disable(EnableCap.Blend);
                        _gl.DepthMask(true);
                        foreach (var m in _opaqueSurfaceMeshes)
                            DrawSurfaceMesh(m, a, clay, modelLoc, normalLoc, colorLoc, meshIndexLoc, selectedMeshIndexLoc, identityModel, identityNormal);
                    }

                    SortTransparentMeshesBackToFront(_transparentSurfaceMeshes, camera);
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.DepthMask(false);
                    foreach (var m in _transparentSurfaceMeshes)
                        DrawSurfaceMesh(m, a, clay, modelLoc, normalLoc, colorLoc, meshIndexLoc, selectedMeshIndexLoc, identityModel, identityNormal);
                }
            }
        }
        finally
        {
            GlesRenderUtil.ResetMeshCulling(_gl);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            if (surfaceDepthOffsetEnabled)
                _gl.Disable(EnableCap.PolygonOffsetFill);
        }

        // Edge pass.
        beforeEdges = Stopwatch.GetTimestamp();
        if (drawEdgesThisFrame && _edgeProgram is not null)
        {
            _edgeProgram.Use();
            SetMat4(_edgeProgram, "uView", view);
            SetMat4(_edgeProgram, "uProjection", proj);
            // In Wireframe mode the surface color drives the wire color so a
            // plain wireframe doesn't disappear against the background.
            float edgeR = a.Mode == RenderMode.Wireframe ? a.SurfaceColor[0] : clayEdges ? a.ClayFeatureEdgeColor[0] : a.EdgeColor[0];
            float edgeG = a.Mode == RenderMode.Wireframe ? a.SurfaceColor[1] : clayEdges ? a.ClayFeatureEdgeColor[1] : a.EdgeColor[1];
            float edgeB = a.Mode == RenderMode.Wireframe ? a.SurfaceColor[2] : clayEdges ? a.ClayFeatureEdgeColor[2] : a.EdgeColor[2];
            float edgeA = clayEdges && a.ClayFeatureEdgeColor.Length > 3 ? a.ClayFeatureEdgeColor[3] : 0.82f;
            SetVec2(_edgeProgram, "uViewportSize", _width, _height);
            SetFloat(_edgeProgram, "uLineWidthPixels", clayEdges
                ? System.Math.Clamp(a.ClayFeatureEdgeWidth, 0.05f, 4.0f)
                : System.Math.Clamp(a.EdgeWidth, 0.05f, 4.0f));
            float edgeDepthBias = sectionClippingActive
                ? 0.0f
                : System.Math.Max(0.0f, clayEdges ? a.ClayFeatureEdgeDepthBias : a.EdgeDepthBias);
            SetFloat(_edgeProgram, "uDepthBias", edgeDepthBias);
            SetSectionUniforms(_edgeProgram);
            int edgeModelLoc = _edgeProgram.UniformLocation("uModel");
            int edgeColorLoc = _edgeProgram.UniformLocation("uEdgeColor");
            float lastUploadedEdgeAlpha = float.NaN;

            _gl.Disable(EnableCap.CullFace);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            try
            {
                foreach (var m in Scene.Meshes)
                {
                    if (!ShouldRenderMesh(m))
                        continue;
                    if (!IsAabbInFrustum(m.WorldBounds))
                        continue;
                    if (m.EdgeSegmentCount == 0) continue;
                    float effectiveAlpha = GetEffectiveMeshAlpha(m, a, clay);
                    float meshEdgeAlpha = edgeA * effectiveAlpha;
                    if (meshEdgeAlpha <= HiddenAlphaThreshold) continue;
                    if (edgeColorLoc >= 0
                        && (float.IsNaN(lastUploadedEdgeAlpha)
                            || System.Math.Abs(meshEdgeAlpha - lastUploadedEdgeAlpha) > 0.000001f))
                    {
                        _gl.Uniform4(edgeColorLoc, edgeR, edgeG, edgeB, meshEdgeAlpha);
                        lastUploadedEdgeAlpha = meshEdgeAlpha;
                    }
                    float[] model = m.WorldTransform ?? identityModel;
                    if (edgeModelLoc >= 0) _gl.UniformMatrix4(edgeModelLoc, true, model);
                    m.DrawEdges();
                }
            }
            finally
            {
                _gl.DepthMask(true);
                _gl.DepthFunc(DepthFunction.Lequal);
                GlesRenderUtil.ResetMeshCulling(_gl);
                _gl.Disable(EnableCap.Blend);
            }
        }
        afterEdges = Stopwatch.GetTimestamp();

        if (SectionVisualPlanes.Count > 0)
        {
            ApplySectionOverlaySettings();
            RenderSectionCaps(view, proj);
            _sectionOverlay?.Render(
                view,
                proj,
                ResolveSceneDiagonal(Scene),
                SectionVisualPlanes,
                SectionFillVisible,
                SectionEdgesVisible,
                SectionPlacementCommittedPicks,
                SectionPlacementHoverPoint);
            ResetMainFramebufferState();
        }
        else if (SectionPlacementCommittedPicks.Count > 0 || SectionPlacementHoverPoint is not null)
        {
            ApplySectionOverlaySettings();
            _sectionOverlay?.Render(
                view,
                proj,
                ResolveSceneDiagonal(Scene),
                SectionVisualPlanes,
                fillVisible: false,
                edgesVisible: false,
                SectionPlacementCommittedPicks,
                SectionPlacementHoverPoint);
            ResetMainFramebufferState();
        }

        if (SectionGizmoScale > 0f)
        {
            ApplySectionOverlaySettings();
            _sectionOverlay?.RenderGizmo(
                view,
                proj,
                SectionGizmoAnchor,
                SectionGizmoAxisX,
                SectionGizmoAxisY,
                SectionGizmoAxisZ,
                SectionGizmoScale,
                SectionGizmoHovered,
                SectionGizmoActive);
            ResetMainFramebufferState();
        }

        if (BodyMoveGizmoScale > 0f)
        {
            ApplySectionOverlaySettings();
            _sectionOverlay?.RenderGizmo(
                view,
                proj,
                BodyMoveGizmoAnchor,
                BodyMoveGizmoAxisX,
                BodyMoveGizmoAxisY,
                BodyMoveGizmoAxisZ,
                BodyMoveGizmoScale,
                BodyMoveGizmoHovered,
                BodyMoveGizmoActive);
            ResetMainFramebufferState();
        }

        if (FaceHighlights.Count > 0)
            _faceHighlightOverlay?.Render(view, proj, FaceHighlights, MeasurementFaceSelectionColor, MeasurementFaceHoverColor);
        if (MeasurementPresentation.Count > 0)
        {
            if (_measurementOverlay is not null)
                _measurementOverlay.DimensionHighlightColor = DimensionHighlightColor;
            _measurementOverlay?.Render(view, proj, _height, camera.Position, MeasurementPresentation);
        }
        ResetMainFramebufferState();

        // Plan 3B: resolve the MSAA color attachment to the default
        // backbuffer. The selection outline post-process draws into the
        // default FBO over the resolved color. Skip the resolve entirely
        // when we rendered directly to the default FB.
        if (useMsaaFbo && _msaaFbo!.FboHandle != 0)
        {
            // FXAA/SSAA on: resolve into the offscreen composite FBO so the
            // overlays below draw into it and the post pass (FXAA filter or SSAA
            // downsample) can process the whole final image.
            if (!TryResolveMsaaFramebuffer(a, useComposite ? _compositeFbo : 0u))
            {
                if (retriedWithoutMsaa)
                    break;

                // S6-F3: the MSAA resolve blit failed, so re-run the loop with the
                // offscreen FBO disabled and render the scene straight to FBO 0.
                // That costs one extra full scene pass (a duplicate scene draw that frame).
                // This is an accepted one-frame fallback: the retriedWithoutMsaa flag
                // caps it at a single retry so a persistent resolve failure cannot spin
                // this loop, and the frame still shows the scene. TryResolveMsaaFramebuffer
                // has already destroyed the FBO, so the next frame re-prepares it and the
                // single redraw self-corrects once resolves succeed again.
                retriedWithoutMsaa = true;
                useMsaaFbo = false;
                // The composite path depends on the offscreen FBO; with MSAA off
                // the scene goes straight to the backbuffer, so drop SSAA/FXAA and
                // restore the true screen size before re-rendering.
                if (wantSsaa)
                {
                    _width = screenWidth;
                    _height = screenHeight;
                }
                useSsaa = false;
                useFxaa = false;
                useComposite = false;
                continue;
            }
        }

        break;
        }
        long afterScene = Stopwatch.GetTimestamp();

        // Screen-space silhouette overlay. Runs after the MSAA resolve (so
        // it draws into the resolved single-sampled buffer) and before the
        // outline post-process. Requires the normal-depth pre-pass to have run
        // THIS frame - which now happens for silhouettes independently of SSAO
        // (S19#1), so it no longer requires AO to be on. Gating on
        // normalDepthRanThisFrame (not NormalTexture != 0, which stays non-zero
        // once allocated) ensures the texture is fresh. Skipped in
        // Clay/Wireframe modes (no CAD-edge concept there). Also skip it in
        // section mode: the normal-depth pre-pass does not include cap
        // geometry, so this post-process can repaint background/cut edge pixels
        // over the section cap.
        bool silhouetteOverlayActive = normalDepthRanThisFrame
            && a.CadEdgeSilhouetteEnabled
            && a.Mode != RenderMode.Clay
            && a.Mode != RenderMode.Wireframe
            && SectionPlanes.Count == 0
            && !lightweightNavigationActive;
        if (silhouetteOverlayActive)
            RenderSilhouetteOverlay(a);

        // Bring the composited scene+silhouette (in the offscreen composite FBO)
        // to the screen backbuffer before the UI overlays draw. SSAA linear-
        // downsamples the super-sampled buffer to screen size and restores the
        // true screen dimensions; FXAA filters at native size. The selection
        // outline and axis triad then composite directly onto FBO 0 at screen
        // size (they re-bind FBO 0 themselves, so they must run after this).
        if (useSsaa && useMsaaFbo)
        {
            DownscaleCompositeToScreen(screenWidth, screenHeight);
            _width = screenWidth;
            _height = screenHeight;
        }
        else if (useFxaa && useMsaaFbo)
        {
            ApplyFxaa();
        }

        // Selection / hover outline post-process. Hover draws first so the
        // selected body's red outline wins when both targets overlap.
        long outlineStart = afterScene;
        if (!lightweightNavigationActive
            && a.OutlineEnabled
            && HighlightSelection
            && HoveredMeshIndex > 0
            && !IsSelectedMesh(HoveredMeshIndex)
            && _outlineRenderer is not null)
        {
            _outlineRenderer.SectionPlanes = SectionPlanes;
            _outlineRenderer.Render(Scene, camera, HoveredMeshIndex,
                a.HoverOutlineColor, a.HoverOutlineThicknessPx, _width, _height);
            ResetMainFramebufferState();
        }

        if (!lightweightNavigationActive
            && a.OutlineEnabled
            && HighlightSelection
            && SelectedMeshIndices.Count > 0
            && _outlineRenderer is not null)
        {
            _outlineRenderer.SectionPlanes = SectionPlanes;
            _outlineRenderer.Render(Scene, camera, SelectedMeshIndices,
                a.OutlineColor, a.OutlineThicknessPx, _width, _height);
            ResetMainFramebufferState();
        }

        if (a.ShowAxes)
        {
            _axisTriadOverlay?.Render(camera, _width, _height);
            ResetMainFramebufferState();
        }


        long afterOutline = Stopwatch.GetTimestamp();

        LogFrameTiming(
            frameStart,
            afterQueue,
            ssaoStart,
            afterSsao,
            sceneStart,
            afterScene,
            beforeEdges,
            afterEdges,
            outlineStart,
            afterOutline,
            a,
            ssaoActive,
            interactive,
            lightweightNavigationActive,
            drawEdgesThisFrame,
            queuedCommandCount);
        LogSlowFrame(frameStart, a, ssaoActive, interactive, lightweightNavigationActive, drawEdgesThisFrame, queuedCommandCount);
        FrameRendered?.Invoke();
    }

    private void DrawSurfaceMesh(
        GpuMesh mesh,
        SceneAppearance appearance,
        bool clay,
        int modelLoc,
        int normalLoc,
        int colorLoc,
        int meshIndexLoc,
        int selectedMeshIndexLoc,
        float[] identityModel,
        float[] identityNormal)
    {
        GL gl = _gl!;
        float[] model = mesh.WorldTransform ?? identityModel;
        if (modelLoc >= 0) gl.UniformMatrix4(modelLoc, true, model);

        float[] normalMatrix = identityNormal;
        if (mesh.WorldNormalMatrix is not null)
            normalMatrix = mesh.WorldNormalMatrix;
        if (normalLoc >= 0) gl.UniformMatrix3(normalLoc, true, normalMatrix);

        if (colorLoc >= 0)
        {
            // Clay mode forces every body to use the clay surface color;
            // Shaded mode uses the per-instance diffuse from upload.
            if (clay)
            {
                var c = appearance.ClaySurfaceColor;
                gl.Uniform4(colorLoc, c[0], c[1], c[2], GetEffectiveMeshAlpha(mesh, appearance, clay));
            }
            else
            {
                var c = mesh.DiffuseColor;
                gl.Uniform4(colorLoc, c[0], c[1], c[2], GetEffectiveMeshAlpha(mesh, appearance, clay));
            }
        }

        if (meshIndexLoc >= 0) gl.Uniform1(meshIndexLoc, mesh.MeshIndex);
        if (selectedMeshIndexLoc >= 0)
            gl.Uniform1(selectedMeshIndexLoc, HighlightSelection && IsSelectedMesh(mesh.MeshIndex) ? mesh.MeshIndex : 0);
        GlesRenderUtil.ApplyMeshCulling(gl, mesh);
        mesh.Draw();
    }

    private bool IsSelectedMesh(int meshIndex)
        => meshIndex > 0 && _selectedMeshIndexLookup.Contains(meshIndex);

    private void PrepareSurfacePassMeshes(
        IReadOnlyList<GpuMesh> meshes,
        SceneAppearance appearance,
        bool clay,
        List<GpuMesh> opaqueMeshes,
        List<GpuMesh> transparentMeshes)
    {
        opaqueMeshes.Clear();
        transparentMeshes.Clear();
        int hiddenMeshes = 0;
        int materialTransparentMeshes = 0;
        float minMaterialAlpha = 1.0f;
        float minEffectiveAlpha = 1.0f;

        for (int i = 0; i < meshes.Count; i++)
        {
            GpuMesh mesh = meshes[i];
            if (!ShouldRenderMesh(mesh))
            {
                hiddenMeshes++;
                continue;
            }

            if (!IsAabbInFrustum(mesh.WorldBounds))
            {
                _lastFrustumCulledMeshCount++;
                continue;
            }

            float materialAlpha = GetMeshColorAlpha(mesh);
            float alpha = GetEffectiveMeshAlpha(mesh, appearance, clay);
            if (materialAlpha < minMaterialAlpha)
                minMaterialAlpha = materialAlpha;
            if (alpha < minEffectiveAlpha)
                minEffectiveAlpha = alpha;
            if (materialAlpha < OpaqueAlphaThreshold)
                materialTransparentMeshes++;

            if (alpha <= HiddenAlphaThreshold)
            {
                hiddenMeshes++;
                continue;
            }

            if (alpha >= OpaqueAlphaThreshold)
                opaqueMeshes.Add(mesh);
            else
                transparentMeshes.Add(mesh);
        }

        _lastSurfaceTransparentMeshCount = transparentMeshes.Count;
        _lastSurfaceHiddenMeshCount = hiddenMeshes;
        LogTransparencyState(
            appearance,
            clay,
            meshes.Count,
            materialTransparentMeshes,
            transparentMeshes.Count,
            hiddenMeshes,
            minMaterialAlpha,
            minEffectiveAlpha);
    }

    private static void SortTransparentMeshesBackToFront(List<GpuMesh> meshes, CameraState camera)
    {
        if (meshes.Count <= 1)
            return;

        Vector3d forward = camera.Forward;
        if (forward.LengthSquared < CameraBasisEpsilon)
            forward = (camera.Target - camera.Position).Normalized();
        if (forward.LengthSquared < CameraBasisEpsilon)
            return;

        Vector3d cameraPosition = camera.Position;
        meshes.Sort((left, right) =>
        {
            double leftDepth = Vector3d.Dot(left.WorldCenter - cameraPosition, forward);
            double rightDepth = Vector3d.Dot(right.WorldCenter - cameraPosition, forward);
            int comparison = rightDepth.CompareTo(leftDepth);
            return comparison != 0 ? comparison : left.MeshIndex.CompareTo(right.MeshIndex);
        });
    }

    private bool ShouldRenderMesh(GpuMesh mesh)
        => mesh.Visible || IsXrayBackgroundMesh(mesh);

    /// <summary>
    /// Fullscreen pass that emits CAD silhouette edges by reading the
    /// normal-depth pre-pass texture. Constant per-pixel cost, independent
    /// of edge count.
    /// </summary>
    private void RenderSilhouetteOverlay(SceneAppearance a)
    {
        if (_gl is null
            || _silhouetteOverlayProgram is null
            || _normalDepthRenderer is null
            || _normalDepthRenderer.NormalTexture == 0
            || _width <= 0
            || _height <= 0)
        {
            return;
        }

        _silhouetteOverlayProgram.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _normalDepthRenderer.NormalTexture);
        SetInt(_silhouetteOverlayProgram, "uNormalDepthTexture", 0);
        SetVec2(_silhouetteOverlayProgram, "uViewportInvSize",
            1f / _width,
            1f / _height);

        // Match the geometric edge color so the silhouette overlay reads as
        // the same visual style as boundary/feature edges.
        float edgeAlpha = 0.82f;
        SetVec4(
            _silhouetteOverlayProgram,
            "uEdgeColor",
            a.EdgeColor[0],
            a.EdgeColor[1],
            a.EdgeColor[2],
            edgeAlpha);
        // Tuning: depth threshold is in packed normalized depth (~0.004 per
        // 8-bit step); normal threshold is 1 - cos(theta) for the crease
        // angle the silhouette test should ignore. Starting values work on
        // the test assembly; expose via AppSettings if user tuning is needed.
        SetFloat(_silhouetteOverlayProgram, "uDepthEdgeThreshold", 0.003f);
        SetFloat(_silhouetteOverlayProgram, "uNormalEdgeThreshold", 0.35f);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);

        GlesFullscreenTriangle.Draw(_gl, _silhouetteOverlayVao);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void SetVec4(ShaderProgram program, string name, float x, float y, float z, float w)
    {
        int loc = program.UniformLocation(name);
        if (loc >= 0)
            _gl!.Uniform4(loc, x, y, z, w);
    }

    /// <summary>
    /// Extracts 6 normalized world-space frustum planes from row-major
    /// view + projection matrices. Order: left, right, bottom, top, near,
    /// far. The planes face inward; a point is inside the frustum when
    /// (a*x + b*y + c*z + d) >= 0 for every plane.
    /// </summary>
    private void BuildFrustumPlanes(float[] viewMat, float[] projMat)
    {
        // viewProj = proj * view, row-major (matches how the mesh shader
        // multiplies clipPos = proj * view * worldPos via column vectors).
        float[] vp = _frustumViewProjScratch;
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                float sum = 0f;
                for (int k = 0; k < 4; k++)
                    sum += projMat[r * 4 + k] * viewMat[k * 4 + c];
                vp[r * 4 + c] = sum;
            }
        }

        // Left:   row3 + row0       Right: row3 - row0
        SetFrustumPlane(0, vp[12] + vp[0], vp[13] + vp[1], vp[14] + vp[2], vp[15] + vp[3]);
        SetFrustumPlane(1, vp[12] - vp[0], vp[13] - vp[1], vp[14] - vp[2], vp[15] - vp[3]);
        // Bottom: row3 + row1       Top:   row3 - row1
        SetFrustumPlane(2, vp[12] + vp[4], vp[13] + vp[5], vp[14] + vp[6], vp[15] + vp[7]);
        SetFrustumPlane(3, vp[12] - vp[4], vp[13] - vp[5], vp[14] - vp[6], vp[15] - vp[7]);
        // Near:   row3 + row2       Far:   row3 - row2
        SetFrustumPlane(4, vp[12] + vp[8], vp[13] + vp[9], vp[14] + vp[10], vp[15] + vp[11]);
        SetFrustumPlane(5, vp[12] - vp[8], vp[13] - vp[9], vp[14] - vp[10], vp[15] - vp[11]);
    }

    private void SetFrustumPlane(int index, float a, float b, float c, float d)
    {
        float len = MathF.Sqrt(a * a + b * b + c * c);
        int baseIdx = index * 4;
        if (len < 1e-6f)
        {
            // Degenerate plane (e.g. zero-determinant projection); treat as a
            // pass-through so the AABB test always succeeds for it.
            _frustumPlanes[baseIdx + 0] = 0f;
            _frustumPlanes[baseIdx + 1] = 0f;
            _frustumPlanes[baseIdx + 2] = 0f;
            _frustumPlanes[baseIdx + 3] = float.PositiveInfinity;
            return;
        }
        float inv = 1f / len;
        _frustumPlanes[baseIdx + 0] = a * inv;
        _frustumPlanes[baseIdx + 1] = b * inv;
        _frustumPlanes[baseIdx + 2] = c * inv;
        _frustumPlanes[baseIdx + 3] = d * inv;
    }

    /// <summary>
    /// Tests an AABB against the currently built frustum planes. Returns true
    /// if any part of the box may be visible; false if the box is fully outside
    /// at least one plane. Uses the standard center+half-extent positive-radius
    /// test (Akenine-Moller, Real-Time Rendering, sec. 22.10).
    /// </summary>
    private bool IsAabbInFrustum(in BoundingBox bounds)
    {
        if (!bounds.IsValid)
            return true; // unknown bounds -> fail open, never cull

        double cx = (bounds.Min.X + bounds.Max.X) * 0.5;
        double cy = (bounds.Min.Y + bounds.Max.Y) * 0.5;
        double cz = (bounds.Min.Z + bounds.Max.Z) * 0.5;
        double hx = (bounds.Max.X - bounds.Min.X) * 0.5;
        double hy = (bounds.Max.Y - bounds.Min.Y) * 0.5;
        double hz = (bounds.Max.Z - bounds.Min.Z) * 0.5;

        for (int i = 0; i < 6; i++)
        {
            int baseIdx = i * 4;
            float a = _frustumPlanes[baseIdx + 0];
            float b = _frustumPlanes[baseIdx + 1];
            float c = _frustumPlanes[baseIdx + 2];
            float d = _frustumPlanes[baseIdx + 3];

            double dist = a * cx + b * cy + c * cz + d;
            double radius = hx * System.Math.Abs(a) + hy * System.Math.Abs(b) + hz * System.Math.Abs(c);
            if (dist + radius < 0)
                return false;
        }
        return true;
    }

    private bool HasXrayIsolation
        => _xrayOpaqueNodeIdLookup.Count > 0 || _xrayBackgroundNodeIdLookup.Count > 0;

    private bool IsXrayOpaqueMesh(GpuMesh mesh)
        => mesh.SourceNodeId >= 0 && _xrayOpaqueNodeIdLookup.Contains(mesh.SourceNodeId);

    private bool IsXrayBackgroundMesh(GpuMesh mesh)
        => mesh.SourceNodeId >= 0 && _xrayBackgroundNodeIdLookup.Contains(mesh.SourceNodeId);

    private float GetEffectiveMeshAlpha(GpuMesh mesh, SceneAppearance appearance, bool clay)
    {
        float materialAlpha = GetMeshColorAlpha(mesh);
        if (HasXrayIsolation)
        {
            if (IsXrayOpaqueMesh(mesh))
                return 1.0f;

            if (IsXrayBackgroundMesh(mesh))
            {
                float opacity = System.Math.Clamp(XrayIsolationOpacity, 0.02f, 0.98f);
                return materialAlpha < OpaqueAlphaThreshold
                    ? System.Math.Min(materialAlpha, opacity)
                    : opacity;
            }
        }

        return GetEffectiveMeshAlpha(materialAlpha, appearance, clay);
    }

    private static float GetEffectiveMeshAlpha(float materialAlpha, SceneAppearance appearance, bool clay)
    {
        if (clay)
            return 1.0f;

        float alpha = materialAlpha * appearance.SurfaceOpacity;
        if (float.IsNaN(alpha) || float.IsInfinity(alpha))
            return 1.0f;
        return System.Math.Clamp(alpha, 0.0f, 1.0f);
    }

    private static float GetMeshColorAlpha(GpuMesh mesh)
        => mesh.MaterialAlpha;

    private void RenderSectionCaps(float[] view, float[] projection)
    {
        bool diagnostic = ConsumeSectionCapDiagnosticFrame(out string diagnosticReason);
        if (_gl is null || _sectionOverlay is null || Scene is null)
        {
            if (diagnostic)
            {
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"render skipped: gl={_gl is not null}, overlay={_sectionOverlay is not null}, scene={Scene is not null}.");
            }
            return;
        }
        if (!SectionCapsVisible && !SectionCurvesVisible)
        {
            if (diagnostic)
            {
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"render skipped: capsVisible={SectionCapsVisible}, curvesVisible={SectionCurvesVisible}.");
            }
            return;
        }
        if (SectionVisualPlanes.Count == 0 || SectionPlanes.Count == 0)
        {
            if (diagnostic)
            {
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"render skipped: visualPlanes={SectionVisualPlanes.Count}, clipPlanes={SectionPlanes.Count}.");
            }
            return;
        }

        const int capStencilBit = 0x80;
        float sceneDiagonal = ResolveSceneDiagonal(Scene);
        long capStartTicks = Stopwatch.GetTimestamp();
        SectionCapGeometry[] capGeometries = GetSectionCapGeometries(Scene, diagnostic, diagnosticReason);
        long afterGeometryTicks = Stopwatch.GetTimestamp();
        if (capGeometries.Length == 0)
        {
            if (diagnostic)
            {
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"render has no geometry yet: capsVisible={SectionCapsVisible}, curvesVisible={SectionCurvesVisible}, interactive={InteractiveNavigationActive}.");
            }
            LogSectionCapRenderTiming(capStartTicks, afterGeometryTicks, afterGeometryTicks, capGeometries);
            return;
        }

        _gl.Enable(EnableCap.StencilTest);

        try
        {
            if (SectionCapsVisible)
            {
                for (int i = 0; i < SectionVisualPlanes.Count; i++)
                {
                    SectionCapGeometry geometry = i < capGeometries.Length
                        ? capGeometries[i]
                        : SectionCapGeometry.Empty;
                    if (geometry.TriangleVertices.Length == 0)
                        continue;

                    GlesSectionVisualPlane plane = SectionVisualPlanes[i];
                    _gl.StencilMask(capStencilBit);
                    _gl.ClearStencil(0);
                    _gl.Clear((uint)ClearBufferMask.StencilBufferBit);

                    _gl.ColorMask(false, false, false, false);
                    _gl.DepthMask(false);
                    // Stencil the actual section contours. Open boundary chains
                    // are rejected by the cached geometry builder so they cannot
                    // flood cap color outside the cut footprint.
                    _gl.Disable(EnableCap.DepthTest);
                    _gl.Disable(EnableCap.CullFace);
                    _gl.Disable(EnableCap.Blend);
                    _gl.StencilFunc(StencilFunction.Always, 0, capStencilBit);
                    _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Invert);

                    _sectionOverlay.RenderCapMaskTriangles(view, projection, geometry.TriangleVertices);

                    _gl.ColorMask(true, true, true, true);
                    _gl.StencilFunc(StencilFunction.Equal, capStencilBit, capStencilBit);
                    _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
                    _gl.StencilMask(0x00);
                    _gl.DepthMask(false);
                    _gl.Enable(EnableCap.DepthTest);
                    _gl.DepthFunc(DepthFunction.Lequal);

                    _sectionOverlay.RenderCapPlane(view, projection, plane, sceneDiagonal);
                }
            }

            if (SectionCurvesVisible)
            {
                _gl.ColorMask(true, true, true, true);
                _gl.StencilMask(0xFF);
                _gl.Disable(EnableCap.StencilTest);

                int count = System.Math.Min(SectionVisualPlanes.Count, capGeometries.Length);
                for (int i = 0; i < count; i++)
                {
                    SectionCapGeometry geometry = capGeometries[i];
                    if (geometry.VectorLineVertices.Length == 0)
                        continue;

                    GlesSectionVisualPlane plane = SectionVisualPlanes[i];
                    Vector4 color = plane.Selected ? SectionEdgeHighlightColor : SectionEdgeColor;
                    _sectionOverlay.RenderLineVertices(view, projection, geometry.VectorLineVertices, color);
                }
            }
        }
        finally
        {
            _gl.ColorMask(true, true, true, true);
            _gl.StencilMask(0xFF);
            _gl.StencilFunc(StencilFunction.Always, 0, 0xFF);
            _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
            _gl.Disable(EnableCap.StencilTest);
            ResetMainFramebufferState();
            LogSectionCapRenderTiming(capStartTicks, afterGeometryTicks, Stopwatch.GetTimestamp(), capGeometries);
        }
    }

    private void ApplySectionOverlaySettings()
    {
        if (_sectionOverlay is null)
            return;

        _sectionOverlay.PlaneSizeFraction = SectionPlaneSizeFraction;
        _sectionOverlay.ViewportWidth = _width;
        _sectionOverlay.ViewportHeight = _height;
        _sectionOverlay.FillColor = SectionFillColor;
        _sectionOverlay.EdgeColor = SectionEdgeColor;
        _sectionOverlay.EdgeWidth = SectionEdgeWidth;
        _sectionOverlay.EdgeHighlightColor = SectionEdgeHighlightColor;
        _sectionOverlay.CapColor = SectionCapColor;
        _sectionOverlay.PlacementPreviewColor = SectionPlacementPreviewColor;
        _sectionOverlay.PlacementHoverColor = SectionPlacementHoverColor;
        _sectionOverlay.GizmoAxisXColor = SectionGizmoAxisXColor;
        _sectionOverlay.GizmoAxisYColor = SectionGizmoAxisYColor;
        _sectionOverlay.GizmoAxisZColor = SectionGizmoAxisZColor;
        _sectionOverlay.GizmoArcXColor = SectionGizmoArcXColor;
        _sectionOverlay.GizmoArcYColor = SectionGizmoArcYColor;
        _sectionOverlay.GizmoArcZColor = SectionGizmoArcZColor;
        _sectionOverlay.GizmoHoverColor = SectionGizmoHoverColor;
        _sectionOverlay.GizmoActiveColor = SectionGizmoActiveColor;
    }

    private SectionCapGeometry[] GetSectionCapGeometries(
        GpuScene scene,
        bool diagnostic,
        string diagnosticReason)
    {
        if (scene.Document is not { } document)
        {
            if (diagnostic)
                LogSectionCapDiagnostic(diagnosticReason, "geometry skipped: scene has no document.");
            return Array.Empty<SectionCapGeometry>();
        }

        int planeHash = ComputeSectionCapPlaneHash();
        long sceneVersion = scene.SectionCapGeometryVersion;
        SectionCapGeometryCache? cache = _sectionCapGeometryCache;
        if (cache is not null
            && ReferenceEquals(cache.Scene, scene)
            && cache.SceneVersion == sceneVersion
            && cache.PlaneHash == planeHash)
        {
            if (diagnostic)
            {
                var counts = CountSectionCapGeometry(cache.Geometries);
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"geometry cache hit: planeHash={planeHash}, sceneVersion={sceneVersion}, planes={cache.Geometries.Length}, tris={counts.Triangles}, vectors={counts.Vectors}, interactive={InteractiveNavigationActive}.");
            }
            return cache.Geometries;
        }

        if (TryConsumeCompletedSectionCapGeometryBuild(scene, sceneVersion, planeHash, out SectionCapGeometryBuildResult? completed))
        {
            SectionCapGeometryBuildResult applied = completed!;
            LogSectionCapDiagnostics(applied.Geometries, applied.SourceCount);
            Android.Util.Log.Info(
                "FA.SectionCap",
                $"Async cap build applied: generation={applied.Generation}, planeHash={applied.PlaneHash}, sceneVersion={applied.SceneVersion}, planes={applied.Geometries.Length}, sources={applied.SourceCount}, buildMs={applied.ElapsedMilliseconds:F1}.");
            _sectionCapGeometryCache = new SectionCapGeometryCache(scene, sceneVersion, planeHash, applied.Geometries);
            return applied.Geometries;
        }

        SectionCapPlane[] planes = BuildSectionCapPlanes();
        if (planes.Length == 0)
        {
            if (diagnostic)
                LogSectionCapDiagnostic(diagnosticReason, $"geometry skipped: no section cap planes; visualPlanes={SectionVisualPlanes.Count}, clipPlanes={SectionPlanes.Count}.");
            return Array.Empty<SectionCapGeometry>();
        }

        if (ShouldDeferSectionCapGeometryBuild(planeHash, out int delayMs))
        {
            if (ShouldLogThrottled(ref _lastSectionCapDebounceLogTicks, 500.0))
            {
                Android.Util.Log.Info(
                    "FA.SectionCap",
                    $"Cap build deferred by debounce: planeHash={planeHash}, sceneVersion={sceneVersion}, delayMs={delayMs}, planes={planes.Length}, interactive={InteractiveNavigationActive}.");
            }
            if (diagnostic)
            {
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"geometry deferred by debounce: planeHash={planeHash}, sceneVersion={sceneVersion}, delayMs={delayMs}, planes={planes.Length}, interactive={InteractiveNavigationActive}.");
            }

            RequestDelayedSectionCapRender(delayMs);
            return Array.Empty<SectionCapGeometry>();
        }

        if (InteractiveNavigationActive)
        {
            if (ShouldLogThrottled(ref _lastSectionCapInteractionLogTicks, 500.0))
            {
                Android.Util.Log.Info(
                    "FA.SectionCap",
                    $"Cap build blocked by camera interaction: planeHash={planeHash}, sceneVersion={sceneVersion}, planes={planes.Length}.");
            }
            if (diagnostic)
            {
                LogSectionCapDiagnostic(
                    diagnosticReason,
                    $"geometry blocked by camera interaction: planeHash={planeHash}, sceneVersion={sceneVersion}, planes={planes.Length}.");
            }

            RequestDelayedSectionCapRender(SectionCapGeometryDebounceMilliseconds);
            return Array.Empty<SectionCapGeometry>();
        }

        if (TryStartBackgroundSectionCapGeometryBuild(scene, document, sceneVersion, planeHash, planes, diagnostic, diagnosticReason))
            return Array.Empty<SectionCapGeometry>();

        if (diagnostic)
        {
            LogSectionCapDiagnostic(
                diagnosticReason,
                $"geometry build not started; delayed retry requested: planeHash={planeHash}, sceneVersion={sceneVersion}, planes={planes.Length}, interactive={InteractiveNavigationActive}.");
        }
        RequestDelayedSectionCapRender(SectionCapGeometryDebounceMilliseconds);
        return Array.Empty<SectionCapGeometry>();
    }

    private bool TryStartBackgroundSectionCapGeometryBuild(
        GpuScene scene,
        DocumentDto document,
        long sceneVersion,
        int planeHash,
        SectionCapPlane[] planes,
        bool diagnostic,
        string diagnosticReason)
    {
        lock (_sectionCapGeometryBuildSync)
        {
            if (_sectionCapGeometryBuildInFlight is not null)
            {
                if (ShouldLogThrottled(ref _lastSectionCapInFlightLogTicks, 500.0))
                {
                    Android.Util.Log.Info(
                        "FA.SectionCap",
                        $"Cap build start skipped: inFlight=true, planeHash={planeHash}, sceneVersion={sceneVersion}, generation={_sectionCapGeometryBuildInFlight.Generation}.");
                }
                if (diagnostic)
                {
                    LogSectionCapDiagnostic(
                        diagnosticReason,
                        $"build start skipped: inFlight=true, inFlightGeneration={_sectionCapGeometryBuildInFlight.Generation}, planeHash={planeHash}, sceneVersion={sceneVersion}.");
                }

                return false;
            }

            if (InteractiveNavigationActive)
            {
                if (ShouldLogThrottled(ref _lastSectionCapInteractionLogTicks, 500.0))
                {
                    Android.Util.Log.Info(
                        "FA.SectionCap",
                        $"Cap build start skipped: interactive=true, planeHash={planeHash}, sceneVersion={sceneVersion}.");
                }
                if (diagnostic)
                {
                    LogSectionCapDiagnostic(
                        diagnosticReason,
                        $"build start skipped: interactive=true, planeHash={planeHash}, sceneVersion={sceneVersion}.");
                }

                return false;
            }
        }

        long snapshotStartTicks = Stopwatch.GetTimestamp();
        var sources = new List<SectionCapMeshSource>(scene.Meshes.Count);
        foreach (GpuMesh mesh in scene.Meshes)
        {
            if (!ShouldRenderSectionCapSourceMesh(mesh))
                continue;
            if (mesh.SourceMeshId < 0 || mesh.SourceMeshId >= document.Meshes.Count)
                continue;

            MeshDto sourceMesh = document.Meshes[mesh.SourceMeshId];
            sources.Add(new SectionCapMeshSource(sourceMesh, ToMatrix4d(mesh.WorldTransform)));
        }
        double sourceSnapshotMs = TicksToMilliseconds(snapshotStartTicks, Stopwatch.GetTimestamp());
        if (sourceSnapshotMs >= 4.0)
        {
            Android.Util.Log.Warn(
                "FA.SectionCap",
                $"Cap source snapshot on GL thread: ms={sourceSnapshotMs:0.0}, sources={sources.Count}, meshes={scene.Meshes.Count}, planeHash={planeHash}, sceneVersion={sceneVersion}.");
        }
        if (diagnostic)
        {
            LogSectionCapDiagnostic(
                diagnosticReason,
                $"source snapshot: ms={sourceSnapshotMs:0.0}, sources={sources.Count}, meshes={scene.Meshes.Count}, documentMeshes={document.Meshes.Count}, planeHash={planeHash}, sceneVersion={sceneVersion}.");
        }

        double sceneDiagonal = ResolveSceneDiagonal(scene);
        var cancellation = new CancellationTokenSource();
        var request = new SectionCapGeometryBuildRequest(
            scene,
            sceneVersion,
            planeHash,
            planes,
            sources.ToArray(),
            sceneDiagonal,
            Interlocked.Increment(ref _sectionCapGeometryBuildGeneration),
            cancellation,
            Stopwatch.GetTimestamp());

        lock (_sectionCapGeometryBuildSync)
        {
            if (_sectionCapGeometryBuildInFlight is not null || InteractiveNavigationActive)
            {
                cancellation.Cancel();
                cancellation.Dispose();
                if (ShouldLogThrottled(ref _lastSectionCapInFlightLogTicks, 500.0))
                {
                    Android.Util.Log.Info(
                        "FA.SectionCap",
                        $"Cap build start aborted after snapshot: inFlight={_sectionCapGeometryBuildInFlight is not null}, interactive={InteractiveNavigationActive}, generation={request.Generation}, planeHash={planeHash}.");
                }
                if (diagnostic)
                {
                    LogSectionCapDiagnostic(
                        diagnosticReason,
                        $"build start aborted after snapshot: inFlight={_sectionCapGeometryBuildInFlight is not null}, interactive={InteractiveNavigationActive}, generation={request.Generation}, planeHash={planeHash}.");
                }

                return false;
            }

            _sectionCapGeometryBuildInFlight = new SectionCapGeometryBuildInFlight(
                request.Generation,
                scene,
                sceneVersion,
                planeHash,
                cancellation);
        }

        Android.Util.Log.Info(
            "FA.SectionCap",
            $"Async cap build queued: generation={request.Generation}, planeHash={planeHash}, sceneVersion={sceneVersion}, planes={planes.Length}, sources={request.Sources.Length}, sourceSnapshotMs={sourceSnapshotMs:0.0}, sceneDiag={sceneDiagonal:0.###}.");
        if (diagnostic)
        {
            LogSectionCapDiagnostic(
                diagnosticReason,
                $"async build queued: generation={request.Generation}, planeHash={planeHash}, sceneVersion={sceneVersion}, planes={planes.Length}, sources={request.Sources.Length}, interactive={InteractiveNavigationActive}.");
        }

        _ = Task.Factory.StartNew(
                () => BuildSectionCapGeometryOffThread(request),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .ContinueWith(
                task =>
                {
                    try
                    {
                        CompleteBackgroundSectionCapGeometryBuild(task, request);
                    }
                    finally
                    {
                        request.Cancellation.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        return true;
    }

    private bool TryConsumeCompletedSectionCapGeometryBuild(
        GpuScene scene,
        long sceneVersion,
        int planeHash,
        out SectionCapGeometryBuildResult? completed)
    {
        lock (_sectionCapGeometryBuildSync)
        {
            completed = _sectionCapGeometryBuildCompleted;
            if (completed is null)
                return false;

            if (ReferenceEquals(completed.Scene, scene)
                && completed.SceneVersion == sceneVersion
                && completed.PlaneHash == planeHash)
            {
                _sectionCapGeometryBuildCompleted = null;
                return true;
            }

            _sectionCapGeometryBuildCompleted = null;
            completed = null;
            return false;
        }
    }

    private static SectionCapGeometryBuildResult BuildSectionCapGeometryOffThread(
        SectionCapGeometryBuildRequest request)
    {
        SetSectionCapBuildThreadPriority();
        request.Cancellation.Token.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        Android.Util.Log.Info(
            "FA.SectionCap",
            $"Async cap build started: generation={request.Generation}, planeHash={request.PlaneHash}, sceneVersion={request.SceneVersion}, thread={Environment.CurrentManagedThreadId}, planes={request.Planes.Length}, sources={request.Sources.Length}.");

        var geometries = new SectionCapGeometry[request.Planes.Length];
        for (int i = 0; i < request.Planes.Length; i++)
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            geometries[i] = SectionCapGeometryBuilder.Build(
                request.Sources,
                request.Planes[i],
                request.Planes,
                i,
                request.SceneDiagonal);
        }

        double elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        var counts = CountSectionCapGeometry(geometries);
        Android.Util.Log.Info(
            "FA.SectionCap",
            $"Async cap build finished: generation={request.Generation}, planeHash={request.PlaneHash}, buildMs={elapsedMilliseconds:0.0}, queuedToFinishMs={TicksToMilliseconds(request.QueuedTicks, Stopwatch.GetTimestamp()):0.0}, tris={counts.Triangles}, vectors={counts.Vectors}.");

        return new SectionCapGeometryBuildResult(
            request.Generation,
            request.Scene,
            request.SceneVersion,
            request.PlaneHash,
            geometries,
            request.Sources.Length,
            elapsedMilliseconds);
    }

    private static void SetSectionCapBuildThreadPriority()
    {
        try
        {
            global::Android.OS.Process.SetThreadPriority((global::Android.OS.ThreadPriority)10);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FA.SectionCap", $"Could not lower cap build thread priority: {ex.Message}");
        }
    }

    private void CompleteBackgroundSectionCapGeometryBuild(Task<SectionCapGeometryBuildResult> task, SectionCapGeometryBuildRequest request)
    {
        SectionCapGeometryBuildResult? result = null;
        Exception? error = null;
        bool canceled = task.IsCanceled;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            canceled = true;
            error = ex;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        bool accepted = false;
        lock (_sectionCapGeometryBuildSync)
        {
            if (error is not null)
            {
                if (_sectionCapGeometryBuildInFlight?.Generation == request.Generation)
                    _sectionCapGeometryBuildInFlight = null;
            }
            else if (result is not null
                && _sectionCapGeometryBuildInFlight is { } inFlight
                && inFlight.Generation == result.Generation
                && ReferenceEquals(inFlight.Scene, result.Scene)
                && inFlight.SceneVersion == result.SceneVersion
                && inFlight.PlaneHash == result.PlaneHash)
            {
                _sectionCapGeometryBuildInFlight = null;
                _sectionCapGeometryBuildCompleted = result;
                accepted = true;
            }
        }

        if (canceled)
        {
            Android.Util.Log.Info(
                "FA.SectionCap",
                $"Async cap build canceled: generation={request.Generation}, planeHash={request.PlaneHash}, queuedToCancelMs={TicksToMilliseconds(request.QueuedTicks, Stopwatch.GetTimestamp()):0.0}, interactive={InteractiveNavigationActive}.");
            return;
        }

        if (error is not null)
        {
            Android.Util.Log.Warn(
                "FA.SectionCap",
                $"Async cap build failed: generation={request.Generation}, planeHash={request.PlaneHash}, queuedToFailMs={TicksToMilliseconds(request.QueuedTicks, Stopwatch.GetTimestamp()):0.0}, error={error.Message}");
            RequestDelayedSectionCapRender(SectionCapGeometryDebounceMilliseconds);
            return;
        }

        if (accepted)
        {
            var counts = result is null ? (Triangles: 0, Vectors: 0) : CountSectionCapGeometry(result.Geometries);
            Android.Util.Log.Info(
                "FA.SectionCap",
                $"Async cap build completed and pending apply: generation={request.Generation}, planeHash={request.PlaneHash}, queuedToCompleteMs={TicksToMilliseconds(request.QueuedTicks, Stopwatch.GetTimestamp()):0.0}, tris={counts.Triangles}, vectors={counts.Vectors}.");
            RequestDelayedSectionCapRender(1);
        }
        else if (result is not null)
        {
            Android.Util.Log.Info(
                "FA.SectionCap",
                $"Async cap build discarded as stale: generation={request.Generation}, planeHash={request.PlaneHash}, resultSceneVersion={result.SceneVersion}, currentGeneration={Volatile.Read(ref _sectionCapGeometryBuildGeneration)}, interactive={InteractiveNavigationActive}.");
        }
    }

    private void InvalidateSectionCapGeometryBuilds(string reason = "invalidate requested")
    {
        CancelPendingSectionCapGeometryBuilds(reason);
        _sectionCapGeometryCache = null;
        _sectionCapDebouncePlaneHash = int.MinValue;
        _sectionCapDebounceStartedTicks = 0;
    }

    private void CancelPendingSectionCapGeometryBuilds(string reason = "unspecified")
    {
        Interlocked.Increment(ref _sectionCapGeometryBuildGeneration);
        SectionCapGeometryBuildInFlight? inFlight;
        bool hadCompleted;
        lock (_sectionCapGeometryBuildSync)
        {
            inFlight = _sectionCapGeometryBuildInFlight;
            hadCompleted = _sectionCapGeometryBuildCompleted is not null;
            _sectionCapGeometryBuildInFlight = null;
            _sectionCapGeometryBuildCompleted = null;
        }

        if ((inFlight is not null || hadCompleted) && ShouldLogThrottled(ref _lastSectionCapCancelLogTicks, 250.0))
        {
            Android.Util.Log.Info(
                "FA.SectionCap",
                $"Cap build cancel requested: reason={reason}, inFlight={inFlight is not null}, completed={hadCompleted}, canceledGeneration={inFlight?.Generation.ToString() ?? "none"}, interactive={InteractiveNavigationActive}.");
        }

        try
        {
            inFlight?.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool ShouldDeferSectionCapGeometryBuild(int planeHash, out int delayMilliseconds)
    {
        long now = Stopwatch.GetTimestamp();
        if (_sectionCapDebouncePlaneHash != planeHash)
        {
            _sectionCapDebouncePlaneHash = planeHash;
            _sectionCapDebounceStartedTicks = now;
            delayMilliseconds = SectionCapGeometryDebounceMilliseconds;
            return true;
        }

        double elapsedMilliseconds = (now - _sectionCapDebounceStartedTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMilliseconds >= SectionCapGeometryDebounceMilliseconds)
        {
            delayMilliseconds = 0;
            return false;
        }

        delayMilliseconds = Math.Max(1, (int)Math.Ceiling(SectionCapGeometryDebounceMilliseconds - elapsedMilliseconds));
        return true;
    }

    private void RequestDelayedSectionCapRender(int delayMilliseconds)
    {
        Action<int>? handler = DelayedRenderRequested;
        if (handler is null)
            return;

        try
        {
            handler(Math.Clamp(delayMilliseconds, 1, SectionCapGeometryDebounceMilliseconds));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FA.SectionCap", $"Delayed cap redraw request failed: {ex.Message}");
        }
    }

    private bool ConsumeSectionCapDiagnosticFrame(out string reason)
    {
        while (true)
        {
            int remaining = Volatile.Read(ref _sectionCapDiagnosticFramesRemaining);
            if (remaining <= 0)
            {
                reason = "";
                return false;
            }

            if (Interlocked.CompareExchange(ref _sectionCapDiagnosticFramesRemaining, remaining - 1, remaining) == remaining)
            {
                reason = _sectionCapDiagnosticReason;
                return true;
            }
        }
    }

    private void LogSectionCapState(string context)
    {
        GpuScene? scene = Scene;
        int planeHash = SectionVisualPlanes.Count > 0 ? ComputeSectionCapPlaneHash() : int.MinValue;
        long sceneVersion = scene?.SectionCapGeometryVersion ?? -1;
        SectionCapGeometryCache? cache = _sectionCapGeometryCache;
        bool cacheHit = cache is not null
            && ReferenceEquals(cache.Scene, scene)
            && cache.SceneVersion == sceneVersion
            && cache.PlaneHash == planeHash;
        var cacheCounts = cache?.Geometries is { } geometries
            ? CountSectionCapGeometry(geometries)
            : (Triangles: 0, Vectors: 0);

        bool inFlight;
        int? inFlightGeneration;
        bool completed;
        int? completedGeneration;
        int generation;
        lock (_sectionCapGeometryBuildSync)
        {
            inFlight = _sectionCapGeometryBuildInFlight is not null;
            inFlightGeneration = _sectionCapGeometryBuildInFlight?.Generation;
            completed = _sectionCapGeometryBuildCompleted is not null;
            completedGeneration = _sectionCapGeometryBuildCompleted?.Generation;
            generation = Volatile.Read(ref _sectionCapGeometryBuildGeneration);
        }

        double debounceAgeMs = _sectionCapDebounceStartedTicks == 0
            ? -1.0
            : TicksToMilliseconds(_sectionCapDebounceStartedTicks, Stopwatch.GetTimestamp());

        Android.Util.Log.Info(
            "FA.SectionCap",
            $"{context}: reason={_sectionCapDiagnosticReason}, capsVisible={SectionCapsVisible}, curvesVisible={SectionCurvesVisible}, visualPlanes={SectionVisualPlanes.Count}, clipPlanes={SectionPlanes.Count}, scene={scene is not null}, meshes={scene?.Meshes.Count ?? 0}, sceneVersion={sceneVersion}, planeHash={planeHash}, interactive={InteractiveNavigationActive}, cache={cache is not null}, cacheHit={cacheHit}, cachePlanes={cache?.Geometries.Length ?? 0}, cacheTris={cacheCounts.Triangles}, cacheVectors={cacheCounts.Vectors}, inFlight={inFlight}, inFlightGeneration={inFlightGeneration?.ToString() ?? "none"}, completed={completed}, completedGeneration={completedGeneration?.ToString() ?? "none"}, generation={generation}, debounceHash={_sectionCapDebouncePlaneHash}, debounceAgeMs={debounceAgeMs:0.0}.");
    }

    private static void LogSectionCapDiagnostic(string reason, string message)
        => Android.Util.Log.Info("FA.SectionCap", $"diagnostic={reason}: {message}");

    private static (int Triangles, int Vectors) CountSectionCapGeometry(
        IReadOnlyList<SectionCapGeometry> geometries)
    {
        int triangles = 0;
        int vectors = 0;
        foreach (SectionCapGeometry geometry in geometries)
        {
            triangles += geometry.TriangleVertices.Length / 9;
            vectors += geometry.VectorLineVertices.Length / 6;
        }

        return (triangles, vectors);
    }

    private static void LogSectionCapDiagnostics(
        IReadOnlyList<SectionCapGeometry> geometries,
        int sourceCount)
    {
        for (int i = 0; i < geometries.Count; i++)
        {
            SectionCapGeometry geometry = geometries[i];
            SectionCapDiagnostics d = geometry.Diagnostics;
            bool suspicious = d.OpenPrunedSegmentCount > 0
                || d.BranchVertexCount > 0
                || d.FailedRegionCount > 0
                || (d.VectorSegmentCount > 0 && geometry.TriangleVertices.Length == 0);
            string message =
                $"plane={i}, sources={sourceCount}, intersecting={d.IntersectingSourceCount}, " +
                $"weldTol={d.WeldTolerance:G9}, maxWeldTol={d.MaxWeldTolerance:G9}, stitchTol={d.EndpointStitchTolerance:G9}, " +
                $"vectors={d.VectorSegmentCount}, candidates={d.CandidateSegmentCount}, " +
                $"weldedV={d.WeldedVertexCount}, weldedS={d.WeldedSegmentCount}, openEnds={d.OpenEndpointCount}, maxDegree={d.MaxVertexDegree}, " +
                $"weldRetried={d.AdaptiveWeldRetriedSourceCount}, weldImproved={d.AdaptiveWeldImprovedSourceCount}, stitches={d.EndpointStitchSegmentCount}, " +
                $"openPruned={d.OpenPrunedSegmentCount}, branches={d.BranchVertexCount}, " +
                $"regions={d.ClosedRegionCount}, filled={d.FilledRegionCount}, failed={d.FailedRegionCount}, " +
                $"clipped={d.ClippedAwayRegionCount}, duplicate={d.DuplicateSegmentCount}, " +
                $"degenerate={d.DegenerateSegmentCount}, edgeCandidates={d.EdgeOnPlaneCandidateCount}, " +
                $"edgeConfirmed={d.ConfirmedEdgeOnPlaneSegmentCount}, tris={geometry.TriangleVertices.Length / 9}.";

            if (suspicious)
                Android.Util.Log.Warn("FA.SectionCap", message);
            else
                Android.Util.Log.Info("FA.SectionCap", message);
        }
    }

    private void LogSectionCapRenderTiming(
        long startTicks,
        long afterGeometryTicks,
        long endTicks,
        IReadOnlyList<SectionCapGeometry> geometries)
    {
        double geometryMs = TicksToMilliseconds(startTicks, afterGeometryTicks);
        double totalMs = TicksToMilliseconds(startTicks, endTicks);
        double drawMs = TicksToMilliseconds(afterGeometryTicks, endTicks);
        if (totalMs < 8.0 && geometryMs < 4.0)
            return;
        if (!ShouldLogThrottled(ref _lastSectionCapRenderTimingLogTicks, 500.0))
            return;

        int triangles = 0;
        int vectors = 0;
        foreach (SectionCapGeometry geometry in geometries)
        {
            triangles += geometry.TriangleVertices.Length / 9;
            vectors += geometry.VectorLineVertices.Length / 6;
        }

        bool inFlight;
        bool completed;
        int generation;
        lock (_sectionCapGeometryBuildSync)
        {
            inFlight = _sectionCapGeometryBuildInFlight is not null;
            completed = _sectionCapGeometryBuildCompleted is not null;
            generation = Volatile.Read(ref _sectionCapGeometryBuildGeneration);
        }

        Android.Util.Log.Warn(
            "FA.SectionCap",
            $"Cap render timing: totalMs={totalMs:0.0}, geometryMs={geometryMs:0.0}, drawMs={drawMs:0.0}, planes={geometries.Count}, tris={triangles}, vectors={vectors}, interactive={InteractiveNavigationActive}, inFlight={inFlight}, completed={completed}, generation={generation}.");
    }

    private void LogSectionCapInteractionState(bool active)
    {
        bool inFlight;
        bool completed;
        int generation;
        lock (_sectionCapGeometryBuildSync)
        {
            inFlight = _sectionCapGeometryBuildInFlight is not null;
            completed = _sectionCapGeometryBuildCompleted is not null;
            generation = Volatile.Read(ref _sectionCapGeometryBuildGeneration);
        }

        Android.Util.Log.Info(
            "FA.SectionCap",
            $"Interactive navigation active={active}, generation={generation}, inFlight={inFlight}, completed={completed}.");
    }

    private SectionCapPlane[] BuildSectionCapPlanes()
    {
        int count = Math.Min(SectionVisualPlanes.Count, 8);
        if (count == 0)
            return Array.Empty<SectionCapPlane>();

        var planes = new SectionCapPlane[count];
        for (int i = 0; i < count; i++)
        {
            GlesSectionVisualPlane plane = SectionVisualPlanes[i];
            planes[i] = new SectionCapPlane(plane.Anchor, plane.AxisX, plane.AxisY, plane.Normal);
        }

        return planes;
    }

    private int ComputeSectionCapPlaneHash()
    {
        var hash = new HashCode();
        int count = Math.Min(SectionVisualPlanes.Count, 8);
        hash.Add(count);
        for (int i = 0; i < count; i++)
        {
            GlesSectionVisualPlane plane = SectionVisualPlanes[i];
            AddVector(ref hash, plane.Anchor);
            AddVector(ref hash, plane.AxisX);
            AddVector(ref hash, plane.AxisY);
            AddVector(ref hash, plane.Normal);
        }

        return hash.ToHashCode();
    }

    private static void AddVector(ref HashCode hash, Vector3 value)
    {
        hash.Add(value.X);
        hash.Add(value.Y);
        hash.Add(value.Z);
    }

    private bool ShouldRenderSectionCapSourceMesh(GpuMesh mesh)
    {
        if (mesh.IndexCount == 0 || !mesh.Visible)
            return false;
        if (IsXrayBackgroundMesh(mesh))
            return false;

        // Section curves and caps are geometric results. Material/global alpha
        // only controls surface rendering, so transparent-but-visible bodies
        // must still participate in section geometry.
        return true;
    }

    private static Dictionary<int, SceneNodeDto>? BuildSectionCapNodeLookup(DocumentDto document)
    {
        if (document.Nodes.Count == 0)
            return null;

        var nodesById = new Dictionary<int, SceneNodeDto>(document.Nodes.Count);
        foreach (SceneNodeDto node in document.Nodes)
            nodesById[node.Id] = node;

        return nodesById;
    }

    private static int? ResolveSectionCapSourceGroupId(
        IReadOnlyDictionary<int, SceneNodeDto>? nodesById,
        int sourceNodeId)
    {
        if (sourceNodeId < 0 || nodesById is null || !nodesById.TryGetValue(sourceNodeId, out SceneNodeDto? node))
            return null;

        if (node.ParentId >= 0
            && node.NodeType == SceneNodeType.Shape
            && nodesById.TryGetValue(node.ParentId, out SceneNodeDto? parent)
            && parent.NodeType == SceneNodeType.Part
            && parent.MeshId is null)
        {
            return parent.Id;
        }

        return node.Id;
    }

    private static Matrix4d ToMatrix4d(float[]? rowMajor)
    {
        if (rowMajor is null || rowMajor.Length < 16)
            return Matrix4d.Identity;

        return new Matrix4d(
            rowMajor[0], rowMajor[1], rowMajor[2], rowMajor[3],
            rowMajor[4], rowMajor[5], rowMajor[6], rowMajor[7],
            rowMajor[8], rowMajor[9], rowMajor[10], rowMajor[11],
            rowMajor[12], rowMajor[13], rowMajor[14], rowMajor[15]);
    }

    private static float ResolveSceneDiagonal(GpuScene scene)
    {
        BoundingBox bounds = scene.Bounds;
        if (!bounds.IsValid)
            return 100f;

        double diagonal = bounds.Diagonal;
        return double.IsFinite(diagonal) && diagonal > 0.0001
            ? (float)diagonal
            : 100f;
    }

    private void LogTransparencyState(
        SceneAppearance appearance,
        bool clay,
        int meshCount,
        int materialTransparentMeshes,
        int effectiveTransparentMeshes,
        int hiddenMeshes,
        float minMaterialAlpha,
        float minEffectiveAlpha)
    {
        bool transparentPass = !clay
            && appearance.Mode != RenderMode.Wireframe
            && effectiveTransparentMeshes > 0;
        var key = new TransparencyStateKey(
            true,
            appearance.Mode,
            appearance.SurfaceOpacity,
            Scene is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Scene),
            meshCount,
            materialTransparentMeshes,
            effectiveTransparentMeshes,
            hiddenMeshes,
            minMaterialAlpha,
            minEffectiveAlpha,
            transparentPass);
        if (key == _lastLoggedTransparencyState)
            return;

        _lastLoggedTransparencyState = key;
        Android.Util.Log.Info(
            "FA.Renderer",
            $"Transparency state: mode={appearance.Mode}, surfaceOpacity={appearance.SurfaceOpacity:0.###}, materialTransparentMeshes={materialTransparentMeshes}/{meshCount}, effectiveTransparentMeshes={effectiveTransparentMeshes}, hiddenAlphaMeshes={hiddenMeshes}, minMaterialAlpha={minMaterialAlpha:0.###}, minEffectiveAlpha={minEffectiveAlpha:0.###}, transparentPass={(transparentPass ? "on" : "off")}.");
    }

    private void LogFrameTiming(
        long frameStartTicks,
        long afterQueueTicks,
        long ssaoStartTicks,
        long afterSsaoTicks,
        long sceneStartTicks,
        long afterSceneTicks,
        long beforeEdgesTicks,
        long afterEdgesTicks,
        long outlineStartTicks,
        long afterOutlineTicks,
        SceneAppearance appearance,
        bool ssaoActive,
        bool interactive,
        bool lightweightNavigationActive,
        bool edgesDrawn,
        int queueCommandCount)
    {
        _frameTiming.Add(
            TicksToMilliseconds(frameStartTicks, afterOutlineTicks),
            TicksToMilliseconds(frameStartTicks, afterQueueTicks),
            TicksToMilliseconds(ssaoStartTicks, afterSsaoTicks),
            TicksToMilliseconds(sceneStartTicks, afterSceneTicks),
            TicksToMilliseconds(beforeEdgesTicks, afterEdgesTicks),
            TicksToMilliseconds(outlineStartTicks, afterOutlineTicks),
            appearance,
            ssaoActive,
            interactive,
            lightweightNavigationActive,
            edgesDrawn,
            queueCommandCount,
            !lightweightNavigationActive && appearance.OutlineEnabled && HighlightSelection,
            Scene?.Meshes.Count ?? 0,
            _lastSurfaceTransparentMeshCount,
            _lastSurfaceHiddenMeshCount,
            _lastFrustumCulledMeshCount,
            _width,
            _height);
    }

    private static double TicksToMilliseconds(long startTicks, long endTicks)
        => (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;

    private static bool ShouldLogThrottled(ref long lastLogTicks, double intervalMilliseconds)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref lastLogTicks);
        if (last != 0 && (now - last) * 1000.0 / Stopwatch.Frequency < intervalMilliseconds)
            return false;

        Interlocked.Exchange(ref lastLogTicks, now);
        return true;
    }

    private void LogSlowFrame(
        long frameStartTicks,
        SceneAppearance appearance,
        bool ssaoActive,
        bool interactive,
        bool lightweightNavigationActive,
        bool edgesDrawn,
        int queueCommandCount)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - frameStartTicks) * 1000.0 / Stopwatch.Frequency;
        double thresholdMs = interactive ? 34.0 : 50.0;
        if (elapsedMs < thresholdMs)
            return;

        long now = Stopwatch.GetTimestamp();
        long lastSlowFrameLogTicks = Volatile.Read(ref _lastSlowFrameLogTicks);
        if (lastSlowFrameLogTicks != 0
            && (now - lastSlowFrameLogTicks) * 1000.0 / Stopwatch.Frequency < 1000.0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSlowFrameLogTicks, now);
        string msaaState = lightweightNavigationActive && appearance.MsaaSamples > 1
            ? $"Off(interactive, requested={appearance.MsaaSamples}x)"
            : appearance.MsaaSamples <= 1 ? "Off" : appearance.MsaaSamples + "x";
        Android.Util.Log.Warn(
            "FA.Renderer",
            $"Slow frame: {elapsedMs:0.0}ms, queueCommands={queueCommandCount}, interactive={interactive}, lightweight={lightweightNavigationActive}, mode={appearance.Mode}, ssao={ssaoActive}, edges={edgesDrawn}, edgeWidth={appearance.EdgeWidth:0.###}, outline={(!lightweightNavigationActive && appearance.OutlineEnabled && HighlightSelection)}, meshes={Scene?.Meshes.Count ?? 0}, transparent={_lastSurfaceTransparentMeshCount}, hiddenAlpha={_lastSurfaceHiddenMeshCount}, msaa={msaaState}, viewport={_width}x{_height}.");
    }

    private void ResetSlowFrameLogThrottle()
        => Interlocked.Exchange(ref _lastSlowFrameLogTicks, 0);

    private void SetMat4(ShaderProgram program, string name, float[] m)
    {
        int loc = program.UniformLocation(name);
        if (loc < 0) return;
        _gl!.UniformMatrix4(loc, true, m);
    }

    private void SetVec3(ShaderProgram program, string name, float x, float y, float z)
    {
        int loc = program.UniformLocation(name);
        if (loc < 0) return;
        _gl!.Uniform3(loc, x, y, z);
    }

    private void SetInt(ShaderProgram program, string name, int value)
    {
        int loc = program.UniformLocation(name);
        if (loc < 0) return;
        _gl!.Uniform1(loc, value);
    }

    private void SetBool(ShaderProgram program, string name, bool value)
    {
        int loc = program.UniformLocation(name);
        if (loc < 0) return;
        _gl!.Uniform1(loc, value ? 1 : 0);
    }

    private void SetFloat(ShaderProgram program, string name, float value)
    {
        int loc = program.UniformLocation(name);
        if (loc < 0) return;
        _gl!.Uniform1(loc, value);
    }

    private void SetVec2(ShaderProgram program, string name, float x, float y)
    {
        int loc = program.UniformLocation(name);
        if (loc < 0) return;
        _gl!.Uniform2(loc, x, y);
    }

    private void SetSectionUniforms(ShaderProgram program)
    {
        int count = System.Math.Min(SectionPlanes.Count, 8);
        int countLoc = program.UniformLocation("uSectionPlaneCount");
        if (countLoc >= 0)
            _gl!.Uniform1(countLoc, count);

        int planesLoc = program.UniformArrayLocation("uSectionPlanes");
        if (planesLoc < 0 || count <= 0)
            return;

        Array.Clear(_sectionUniformScratch, 0, _sectionUniformScratch.Length);
        for (int i = 0; i < count; i++)
        {
            GlesSectionPlane plane = SectionPlanes[i];
            int offset = i * 4;
            _sectionUniformScratch[offset + 0] = plane.NormalX;
            _sectionUniformScratch[offset + 1] = plane.NormalY;
            _sectionUniformScratch[offset + 2] = plane.NormalZ;
            _sectionUniformScratch[offset + 3] = plane.Offset;
        }

        unsafe
        {
            fixed (float* ptr = _sectionUniformScratch)
                _gl!.Uniform4(planesLoc, (uint)count, ptr);
        }
    }

    private void EnsureEdgesMatchAppearance(SceneAppearance appearance)
    {
        if (Scene is null)
            return;

        bool clayEdges = appearance.Mode == RenderMode.Clay;
        float featureAngle = clayEdges
            ? appearance.ClayFeatureEdgeCreaseAngleDegrees
            : appearance.CadEdgeFeatureAngleDegrees;
        float coplanarTolerance = clayEdges ? 0.0f : appearance.CadEdgeCoplanarToleranceDegrees;

        bool sceneChanged = !ReferenceEquals(_edgeSettingsScene, Scene);
        bool settingsChanged =
            sceneChanged
            || System.Math.Abs(_edgeFeatureAngle - featureAngle) > 0.0001f
            || System.Math.Abs(_edgeCoplanarTolerance - coplanarTolerance) > 0.0001f
            || System.Math.Abs(_edgeWeldTolerance - appearance.CadEdgeWeldToleranceScale) > 0.000000001f;

        if (!settingsChanged)
            return;

        Scene.RebuildEdges(
            featureAngle,
            coplanarTolerance,
            appearance.CadEdgeWeldToleranceScale);

        _edgeSettingsScene = Scene;
        _edgeFeatureAngle = featureAngle;
        _edgeCoplanarTolerance = coplanarTolerance;
        _edgeWeldTolerance = appearance.CadEdgeWeldToleranceScale;
    }

    private bool TryPrepareMsaaFramebuffer(SceneAppearance appearance)
    {
        // The scene always renders into the offscreen FBO so depth precision
        // stays at D32FS8 independently of the EGL backbuffer. Sample count
        // follows the appearance setting, with 0/1 selecting single-sample
        // storage in MsaaSceneFramebuffer.
        if (_msaaFbo is null)
        {
            return false;
        }

        // S6-1: only short-circuit when an allocation actually failed for this
        // exact size + sample count. Keying on _msaaAllocationFailed (not on a 0
        // sample sentinel) lets a legitimate "MSAA Off = 0" request be retried.
        if (_msaaAllocationFailed
            && _failedMsaaWidth == _width
            && _failedMsaaHeight == _height
            && _failedMsaaSamples == appearance.MsaaSamples)
        {
            return false;
        }

        try
        {
            _msaaFbo.Ensure(_width, _height, appearance.MsaaSamples);
            _msaaAllocationFailed = false;
            _failedMsaaWidth = 0;
            _failedMsaaHeight = 0;
            _failedMsaaSamples = 0;
            LogMsaaState(appearance.MsaaSamples, _msaaFbo.Samples, _msaaFbo.MaxSamples);
            return _msaaFbo.FboHandle != 0;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(
                "FA.Renderer",
                Java.Lang.Throwable.FromException(ex),
                $"MSAA disabled for {_width}x{_height} at {appearance.MsaaSamples}x.");
            _msaaFbo.Destroy();
            _msaaAllocationFailed = true;
            _failedMsaaWidth = _width;
            _failedMsaaHeight = _height;
            _failedMsaaSamples = appearance.MsaaSamples;
            return false;
        }
    }

    private bool TryResolveMsaaFramebuffer(SceneAppearance appearance, uint targetFbo)
    {
        if (_msaaFbo is null || _msaaFbo.FboHandle == 0)
            return true;

        if (_msaaFbo.TryResolveTo(targetFbo))
            return true;

        Android.Util.Log.Warn(
            "FA.Renderer",
            $"MSAA resolve failed for {_width}x{_height} at {appearance.MsaaSamples}x, GL error=0x{(int)_msaaFbo.LastResolveError:X4}; falling back to direct rendering.");
        _msaaFbo.Destroy();
        _msaaAllocationFailed = true;
        _failedMsaaWidth = _width;
        _failedMsaaHeight = _height;
        _failedMsaaSamples = appearance.MsaaSamples;
        LogMsaaState(appearance.MsaaSamples, 1, _msaaFbo.MaxSamples);
        return false;
    }

    // Allocates (or re-allocates on size change) the full-resolution color target
    // the resolved scene + overlays are composited into before the FXAA pass.
    private unsafe bool EnsureCompositeFramebuffer()
    {
        if (_gl is null || _width <= 0 || _height <= 0)
            return false;
        if (_compositeFbo != 0 && _compositeWidth == _width && _compositeHeight == _height)
            return true;

        DestroyCompositeFramebuffer();

        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        // RGB8 to MATCH the MSAA scene color renderbuffer (also RGB8). A
        // multisample->single-sample resolve blit requires identical internal
        // formats (GLES 3.0 sec 4.3.2); an RGBA8 composite would make the resolve
        // fail with GL_INVALID_OPERATION and silently fall back to direct
        // rendering - which is why FXAA appeared to do nothing.
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8,
            (uint)_width, (uint)_height, 0, PixelFormat.Rgb, PixelType.UnsignedByte, (void*)0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (status != GLEnum.FramebufferComplete)
        {
            _gl.DeleteFramebuffer(fbo);
            _gl.DeleteTexture(tex);
            Android.Util.Log.Warn("FA.Renderer", $"FXAA composite FBO incomplete: 0x{(int)status:X4}");
            return false;
        }

        _compositeFbo = fbo;
        _compositeTex = tex;
        _compositeWidth = _width;
        _compositeHeight = _height;
        return true;
    }

    private void DestroyCompositeFramebuffer()
    {
        if (_gl is null)
            return;
        if (_compositeFbo != 0) { _gl.DeleteFramebuffer(_compositeFbo); _compositeFbo = 0; }
        if (_compositeTex != 0) { _gl.DeleteTexture(_compositeTex); _compositeTex = 0; }
        _compositeWidth = 0;
        _compositeHeight = 0;
    }

    // Linear-downsample the super-sampled composite (_compositeWidth x
    // _compositeHeight) into the screen backbuffer. The composite is a single-
    // sample texture FBO, so a scaling blit is legal; the linear filter performs
    // the box resolve that turns the larger render into anti-aliased screen
    // pixels. Leaves FBO 0 bound at the screen viewport for the UI overlays.
    private void DownscaleCompositeToScreen(int screenW, int screenH)
    {
        if (_gl is null || _compositeFbo == 0 || screenW <= 0 || screenH <= 0)
            return;

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _compositeFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.ReadBuffer(GLEnum.ColorAttachment0);
        _gl.BlitFramebuffer(
            0, 0, _compositeWidth, _compositeHeight,
            0, 0, screenW, screenH,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)screenW, (uint)screenH);
    }

    // FXAA the composited image (in _compositeTex) to the default backbuffer.
    private void ApplyFxaa()
    {
        if (_gl is null || _fxaaProgram is null || _compositeTex == 0)
            return;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);

        _fxaaProgram.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _compositeTex);
        SetInt(_fxaaProgram, "uScene", 0);
        SetVec2(_fxaaProgram, "uInvResolution", 1f / _width, 1f / _height);

        GlesFullscreenTriangle.Draw(_gl, _silhouetteOverlayVao);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void LogMsaaState(int requestedSamples, int effectiveSamples, int maxSamples)
    {
        if (_lastLoggedMsaaRequested == requestedSamples
            && _lastLoggedMsaaEffective == effectiveSamples
            && _lastLoggedMsaaMaxSamples == maxSamples)
        {
            return;
        }

        _lastLoggedMsaaRequested = requestedSamples;
        _lastLoggedMsaaEffective = effectiveSamples;
        _lastLoggedMsaaMaxSamples = maxSamples;

        string requested = requestedSamples <= 1 ? "Off" : requestedSamples + "x";
        string effective = effectiveSamples <= 1 ? "Off" : effectiveSamples + "x";
        string max = maxSamples >= 0 ? maxSamples.ToString() : "unknown";
        Android.Util.Log.Info(
            "FA.Renderer",
            $"MSAA state: requested={requested}, effective={effective}, GL_MAX_SAMPLES={max}, viewport={_width}x{_height}.");
    }

    private CameraState? SnapshotCamera()
    {
        CameraState? camera = Camera;
        if (camera is null)
            return null;

        lock (camera)
            return camera.Clone();
    }

    private string GetSsaoInactiveReason(SceneAppearance appearance, CameraState? camera)
    {
        if (!appearance.AmbientOcclusionEnabled)
            return "disabled";
        if (appearance.Mode == RenderMode.Wireframe)
            return "wireframe mode";
        if (Scene is null)
            return "no scene";
        if (camera is null)
            return "no camera";
        if (_normalDepthRenderer is null || _ssaoRenderer is null)
            return "renderer not ready";
        return "";
    }

    private bool ShouldCollectSsaoDiagnostics(SceneAppearance appearance, bool active, string reason)
    {
#if !(DEBUG || FA_RENDER_DIAGNOSTICS)
        return false;
#else
        if (!active)
            return false;

        var key = new SsaoDiagnosticsKey(
            true,
            Scene is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Scene),
            _width,
            _height,
            appearance.Mode,
            appearance.AoSampleCount,
            appearance.AoRadius,
            appearance.AoBias,
            appearance.AoIntensity,
            appearance.AoPower,
            appearance.AoContrast,
            appearance.AoMaxDistance,
            appearance.AoFadeStart,
            appearance.AoFadeEnd,
            appearance.AoBlurEnabled,
            appearance.AoBlurRadius,
            appearance.AoBlurSharpness,
            appearance.AoBlurPasses);
        if (key == _lastSsaoDiagnostics)
            return false;

        _lastSsaoDiagnostics = key;
        return true;
#endif
    }

    private void LogSsaoState(SceneAppearance appearance, bool active, string reason, uint aoTexture)
    {
        int keyWidth = active ? _width : 0;
        int keyHeight = active ? _height : 0;
        var key = new SsaoStateKey(
            true,
            keyWidth,
            keyHeight,
            appearance.AmbientOcclusionEnabled,
            active,
            reason,
            appearance.Mode,
            appearance.AoSampleCount,
            appearance.AoRadius,
            appearance.AoBias,
            appearance.AoIntensity,
            appearance.AoPower,
            appearance.AoContrast,
            appearance.AoMaxDistance,
            appearance.AoFadeStart,
            appearance.AoFadeEnd,
            appearance.AoBlurEnabled,
            appearance.AoBlurRadius,
            appearance.AoBlurSharpness,
            appearance.AoBlurPasses);
        if (key == _lastLoggedSsaoState)
            return;

        _lastLoggedSsaoState = key;

        if (!active || _ssaoRenderer is null)
        {
            Android.Util.Log.Info(
                "FA.Renderer",
                $"SSAO state: enabled={appearance.AmbientOcclusionEnabled}, active=False, reason={reason}, mode={appearance.Mode}, viewport={_width}x{_height}.");
            return;
        }

        GlesSsaoRenderInfo info = _ssaoRenderer.LastRenderInfo;
            Android.Util.Log.Info(
                "FA.Renderer",
                $"SSAO state: enabled=True, active=True, mode={appearance.Mode}, projection={(info.IsPerspective ? "perspective" : "orthographic")}, intensity={info.Intensity:0.###}, samples={info.SampleCount}, radius={appearance.AoRadius:0.#####} effective={info.Radius:0.###}, bias={appearance.AoBias:0.#####} effective={info.Bias:0.###}, maxDistance={info.MaxDistance:0.###}, fade={info.FadeStart:0.###}-{info.FadeEnd:0.###}, sceneDiag={info.SceneDiagonal:0.###}, cameraSceneDistance={info.CameraSceneDistance:0.###}, blur={info.BlurEnabled} r={info.BlurRadius} p={info.BlurPasses}, depthRange={info.LinearDepthMin:0.###}-{info.LinearDepthMax:0.###}, texture={aoTexture}, viewport={_width}x{_height}.");

        if (info.RenderError != GLEnum.NoError)
        {
            Android.Util.Log.Warn(
                "FA.Ssao",
                $"SSAO render GL error=0x{(int)info.RenderError:X4}.");
        }

        GlesSsaoTextureStats stats = info.TextureStats;
        if (stats.Valid)
        {
            Android.Util.Log.Info(
                "FA.Ssao",
                $"SSAO texture stats: rawMin={info.RawTextureStats.Min}, rawMax={info.RawTextureStats.Max}, rawAvg={info.RawTextureStats.Average:0.0}, finalMin={stats.Min}, finalMax={stats.Max}, finalAvg={stats.Average:0.0}, center={stats.Center} (0=dark, 255=white).");
        }
        else if (stats.ReadError != GLEnum.NoError)
        {
            Android.Util.Log.Warn(
                "FA.Ssao",
                $"SSAO texture readback failed, GL error=0x{(int)stats.ReadError:X4}.");
        }

        if (_normalDepthRenderer is not null)
            LogNormalDepthStats(_normalDepthRenderer.LastRenderInfo);
    }

    private static void LogNormalDepthStats(GlesNormalDepthRenderInfo info)
    {
        GlesNormalDepthStats stats = info.Stats;
        if (!info.Rendered)
            return;

        if (stats.Valid)
        {
            Android.Util.Log.Info(
                "FA.NormalDepth",
                $"Normal/depth stats: meshes={info.MeshCount}, changedNormals={stats.ChangedNormalSamples}/{stats.SampleCount}, normalR={stats.MinNormalR}-{stats.MaxNormalR}, centerNormal=({stats.CenterNormalR},{stats.CenterNormalG}), viewDepth={stats.MinDepth:0.###}-{stats.MaxDepth:0.###}, depthAvg={stats.AverageDepth:0.###}, centerDepth={stats.CenterDepth:0.###}, depthRange={info.LinearDepthMin:0.###}-{info.LinearDepthMax:0.###}.");
            return;
        }

        if (stats.ReadError != GLEnum.NoError)
        {
            Android.Util.Log.Warn(
                "FA.NormalDepth",
                $"Normal/depth readback failed, meshes={info.MeshCount}, GL error=0x{(int)stats.ReadError:X4}.");
        }
    }

    private void ResetMainFramebufferState()
    {
        // Framebuffer is bound by the caller (OnDrawFrame). Do NOT re-bind
        // FBO 0 here - that would discard the MSAA target the caller just
        // selected. This method now only resets pipeline state.
        _gl!.Enable(EnableCap.DepthTest);
        if (_width > 0 && _height > 0)
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.ColorMask(true, true, true, true);
        _gl.Disable(EnableCap.Blend);
        // S7-F12: reset the blend func to the standard alpha-over default so a later
        // pass that enables blend without setting its own func gets a known state
        // (overlays set BlendFunc then only disable Blend, leaving the func dangling).
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.StencilTest);
        _gl.StencilMask(0xFF);
        _gl.StencilFunc(StencilFunction.Always, 0, 0xFF);
        _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
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
        CameraState? camera = SnapshotCamera();
        if (_pickRenderer is null || Scene is null || camera is null) return null;
        _pickRenderer.SectionPlanes = SectionPlanes;
        _pickRenderer.XrayBackgroundNodeIds = XrayBackgroundNodeIds;
        return _pickRenderer.Pick(x, y, Scene, camera);
    }

    /// <summary>
    /// Creates a 1x1 white R8 (single-channel) texture. Bound when SSAO is
    /// disabled so the mesh shader's AO sample returns 1.0 and the multiply
    /// is identity.
    /// </summary>
    private static unsafe uint CreateWhiteTexture(GL gl)
    {
        uint tex = gl.GenTexture();
        try
        {
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
        catch
        {
            gl.BindTexture(TextureTarget.Texture2D, 0);
            if (tex != 0) gl.DeleteTexture(tex);
            throw;
        }
    }

    private static string LoadEmbeddedShader(string fileName)
    {
        var asm = typeof(GlesViewportRenderer).Assembly;
        var name = "FabricationAssistant.Rendering.Gles.Shaders." + fileName;
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                "Embedded shader not found: " + name
                + ". Available shader resources: "
                + string.Join(", ", asm.GetManifestResourceNames()
                    .Where(resource => resource.StartsWith("FabricationAssistant.Rendering.Gles.Shaders.", StringComparison.Ordinal))
                    .OrderBy(resource => resource, StringComparer.Ordinal)));
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public int Width => _width;
    public int Height => _height;

    public void Dispose()
    {
        DisposeResources(disposeScene: true);
        DelayedRenderRequested = null;
        FrameRendered = null;
    }

    private void DisposeResources(bool disposeScene)
    {
        _initialized = false;
        InvalidateSectionCapGeometryBuilds();

        if (disposeScene)
        {
            TryDispose(Scene);
            Scene = null;
        }

        TryDispose(_meshProgram);
        TryDispose(_edgeProgram);
        TryDispose(_pickRenderer);
        TryDispose(_gridRenderer);
        TryDispose(_normalDepthRenderer);
        TryDispose(_ssaoRenderer);
        TryDispose(_silhouetteOverlayProgram);
        TryDispose(_fxaaProgram);
        if (_silhouetteOverlayVbo != 0 && _gl is not null)
        {
            try { _gl.DeleteBuffer(_silhouetteOverlayVbo); }
            catch (Exception ex) { Android.Util.Log.Warn("FA.Renderer", "Failed to delete silhouette overlay VBO: " + ex.Message); }
        }
        if (_silhouetteOverlayVao != 0 && _gl is not null)
        {
            try { _gl.DeleteVertexArray(_silhouetteOverlayVao); }
            catch (Exception ex) { Android.Util.Log.Warn("FA.Renderer", "Failed to delete silhouette overlay VAO: " + ex.Message); }
        }
        _silhouetteOverlayVbo = 0;
        _silhouetteOverlayVao = 0;
        TryDispose(_outlineRenderer);
        TryDispose(_measurementOverlay);
        TryDispose(_faceHighlightOverlay);
        TryDispose(_sectionOverlay);
        TryDispose(_axisTriadOverlay);
        TryDispose(_msaaFbo);
        DestroyCompositeFramebuffer();

        _meshProgram = null;
        _edgeProgram = null;
        _pickRenderer = null;
        _gridRenderer = null;
        _normalDepthRenderer = null;
        _ssaoRenderer = null;
        _silhouetteOverlayProgram = null;
        _fxaaProgram = null;
        _outlineRenderer = null;
        _measurementOverlay = null;
        _faceHighlightOverlay = null;
        _sectionOverlay = null;
        _axisTriadOverlay = null;
        _msaaFbo = null;

        if (_whiteAoTexture != 0 && _gl is not null)
        {
            try { _gl.DeleteTexture(_whiteAoTexture); }
            catch (Exception ex) { Android.Util.Log.Warn("FA.Renderer", "Failed to delete AO fallback texture: " + ex.Message); }
            _whiteAoTexture = 0;
        }

        _edgeSettingsScene = null;
        _opaqueSurfaceMeshes.Clear();
        _transparentSurfaceMeshes.Clear();
        _lastLoggedSsaoState = default;
#if DEBUG || FA_RENDER_DIAGNOSTICS
        _lastSsaoDiagnostics = default;
#endif
        _lastLoggedTransparencyState = default;
        ResetSlowFrameLogThrottle();
        _gl = null;
    }

    private static void TryDispose(IDisposable? disposable)
    {
        if (disposable is null)
            return;

        try { disposable.Dispose(); }
        catch (Exception ex) { Android.Util.Log.Warn("FA.Renderer", "Renderer resource disposal failed: " + ex.Message); }
    }
}

internal readonly record struct TransparencyStateKey(
    bool Valid,
    RenderMode Mode,
    float SurfaceOpacity,
    int SceneHash,
    int MeshCount,
    int MaterialTransparentMeshes,
    int EffectiveTransparentMeshes,
    int HiddenMeshes,
    float MinMaterialAlpha,
    float MinEffectiveAlpha,
    bool TransparentPass);

internal readonly record struct SsaoStateKey(
    bool Valid,
    int Width,
    int Height,
    bool Enabled,
    bool Active,
    string Reason,
    RenderMode Mode,
    int SampleCount,
    float Radius,
    float Bias,
    float Intensity,
    float Power,
    float Contrast,
    float MaxDistance,
    float FadeStart,
    float FadeEnd,
    bool BlurEnabled,
    int BlurRadius,
    float BlurSharpness,
    int BlurPasses);

internal readonly record struct SsaoDiagnosticsKey(
    bool Valid,
    int SceneHash,
    int Width,
    int Height,
    RenderMode Mode,
    int SampleCount,
    float Radius,
    float Bias,
    float Intensity,
    float Power,
    float Contrast,
    float MaxDistance,
    float FadeStart,
    float FadeEnd,
    bool BlurEnabled,
    int BlurRadius,
    float BlurSharpness,
    int BlurPasses);

internal sealed record SectionCapGeometryCache(
    GpuScene Scene,
    long SceneVersion,
    int PlaneHash,
    SectionCapGeometry[] Geometries);

internal sealed record SectionCapGeometryBuildInFlight(
    int Generation,
    GpuScene Scene,
    long SceneVersion,
    int PlaneHash,
    CancellationTokenSource Cancellation);

internal sealed record SectionCapGeometryBuildRequest(
    GpuScene Scene,
    long SceneVersion,
    int PlaneHash,
    SectionCapPlane[] Planes,
    SectionCapMeshSource[] Sources,
    double SceneDiagonal,
    int Generation,
    CancellationTokenSource Cancellation,
    long QueuedTicks);

internal sealed record SectionCapGeometryBuildResult(
    int Generation,
    GpuScene Scene,
    long SceneVersion,
    int PlaneHash,
    SectionCapGeometry[] Geometries,
    int SourceCount,
    double ElapsedMilliseconds);

internal readonly record struct FrameTimingStateKey(
    bool Valid,
    RenderMode Mode,
    bool SsaoActive,
    bool Interactive,
    bool LightweightNavigationActive,
    bool EdgesDrawn,
    bool OutlineEnabled,
    int MeshCount,
    int TransparentMeshCount,
    int HiddenAlphaMeshCount,
    int Width,
    int Height,
    int MsaaSamples);

internal sealed class FrameTimingAccumulator
{
    private const int FramesPerReport = 60;

    private FrameTimingStateKey _stateKey;
    private int _frames;
    private int _over16;
    private int _over33;
    private int _over50;
    private double _totalMs;
    private double _queueMs;
    private double _ssaoMs;
    private double _sceneMs;
    private double _edgeMs;
    private double _outlineMs;
    private double _maxMs;
    private int _queueCommands;
    private long _frustumCulledSum;

    public void Add(
        double totalMs,
        double queueMs,
        double ssaoMs,
        double sceneMs,
        double edgeMs,
        double outlineMs,
        SceneAppearance appearance,
        bool ssaoActive,
        bool interactive,
        bool lightweightNavigationActive,
        bool edgesDrawn,
        int queueCommandCount,
        bool outlineEnabled,
        int meshCount,
        int transparentMeshCount,
        int hiddenAlphaMeshCount,
        int frustumCulledMeshCount,
        int width,
        int height)
    {
        var stateKey = new FrameTimingStateKey(
            true,
            appearance.Mode,
            ssaoActive,
            interactive,
            lightweightNavigationActive,
            edgesDrawn,
            outlineEnabled,
            meshCount,
            transparentMeshCount,
            hiddenAlphaMeshCount,
            width,
            height,
            appearance.MsaaSamples);
        if (_stateKey.Valid && _stateKey != stateKey)
            Reset();
        _stateKey = stateKey;

        _frames++;
        _totalMs += totalMs;
        _queueMs += queueMs;
        _ssaoMs += ssaoMs;
        _sceneMs += sceneMs;
        _edgeMs += edgeMs;
        _outlineMs += outlineMs;
        _queueCommands += queueCommandCount;
        _frustumCulledSum += frustumCulledMeshCount;
        _maxMs = System.Math.Max(_maxMs, totalMs);
        if (totalMs > 16.67) _over16++;
        if (totalMs > 33.33) _over33++;
        if (totalMs > 50.0) _over50++;

        if (_frames < FramesPerReport)
            return;

        string msaaState = lightweightNavigationActive && appearance.MsaaSamples > 1
            ? $"Off(interactive, requested={appearance.MsaaSamples}x)"
            : appearance.MsaaSamples <= 1 ? "Off" : appearance.MsaaSamples + "x";
        Android.Util.Log.Info(
            "FA.FrameTiming",
            $"Render timing over {_frames} frames: avg={_totalMs / _frames:0.0}ms, max={_maxMs:0.0}ms, over16={_over16}, over33={_over33}, over50={_over50}, queue={_queueMs / _frames:0.0}ms, queueCommands={_queueCommands}, ssao={_ssaoMs / _frames:0.0}ms, scene={_sceneMs / _frames:0.0}ms, edges={_edgeMs / _frames:0.0}ms, outline={_outlineMs / _frames:0.0}ms, mode={appearance.Mode}, interactive={interactive}, lightweight={lightweightNavigationActive}, ssaoActive={ssaoActive}, edgesDrawn={edgesDrawn}, edgeWidth={appearance.EdgeWidth:0.###}, outline={outlineEnabled}, meshes={meshCount}, transparent={transparentMeshCount}, hiddenAlpha={hiddenAlphaMeshCount}, culledFrustum={_frustumCulledSum / _frames}/{meshCount}, msaa={msaaState}, viewport={width}x{height}.");
        Reset();
        _stateKey = stateKey;
    }

    private void Reset()
    {
        _frames = 0;
        _over16 = 0;
        _over33 = 0;
        _over50 = 0;
        _totalMs = 0;
        _queueMs = 0;
        _ssaoMs = 0;
        _sceneMs = 0;
        _edgeMs = 0;
        _outlineMs = 0;
        _maxMs = 0;
        _queueCommands = 0;
        _frustumCulledSum = 0;
    }
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
