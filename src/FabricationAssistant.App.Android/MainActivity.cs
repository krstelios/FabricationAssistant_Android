using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using FabricationAssistant.App.Android.Views;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Input.Gestures.Android;
using FabricationAssistant.Rendering.Gles;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using AndroidUri = Android.Net.Uri;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace FabricationAssistant.App.Android;

[Activity(
    Label = "@string/app_name",
    Theme = "@style/Theme.FabricationAssistant",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Unspecified,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode)]
public sealed class MainActivity : AppCompatActivity
{
    private IServiceProvider? _services;
    private ViewportSurfaceView? _viewport;
    private SafFilePicker? _picker;
    private ImportPipeline? _import;
    private CameraState? _camera;
    private AndroidPointerSource? _pointerSource;
    private ViewportInteractionAdapter? _interaction;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Initialize the settings store before the ViewportSurfaceView is
        // created - MultisampleConfigChooser reads AppSettings.MsaaSamples
        // at EGL config time.
        AppSettings.Initialize(ApplicationContext!);

        _services = AppServices.Build(ApplicationContext!);
        _import = _services.GetRequiredService<ImportPipeline>();
        _camera = _services.GetRequiredService<CameraState>();

        SetContentView(Resource.Layout.activity_main);
        _picker = new SafFilePicker(this);

        var container = FindViewById<FrameLayout>(Resource.Id.viewportContainer)
            ?? throw new InvalidOperationException("viewportContainer not found");

        _viewport = new ViewportSurfaceView(this);
        _viewport.Renderer.Camera = _camera;
        container.AddView(_viewport);

        // Touch input: pointer source feeds the gesture recognizer, the
        // adapter translates recognized events into CameraState calls.
        _pointerSource = new AndroidPointerSource(this);
        _interaction = new ViewportInteractionAdapter(
            _camera,
            boundsAccessor: () => _viewport?.Renderer.Scene?.Bounds,
            aspectAccessor: () => _viewport is { Width: > 0, Height: > 0 }
                ? (double)_viewport.Width / _viewport.Height
                : 1.0,
            requestRender: () => _viewport?.RequestRender(),
            pivotPicker: PickPivotAt);
        _pointerSource.GestureRecognized += _interaction.OnGesture;
        _pointerSource.GestureRecognized += OnGestureForSelection;
        _viewport.SetOnTouchListener(new TouchProxy(_pointerSource));

        var navOpen = FindViewById<MaterialButton>(Resource.Id.navOpenButton);
        if (navOpen is not null) navOpen.Click += OnOpenClicked;

        var navFit = FindViewById<MaterialButton>(Resource.Id.navFitButton);
        if (navFit is not null) navFit.Click += OnFitClicked;

        var navSettings = FindViewById<MaterialButton>(Resource.Id.navSettingsButton);
        if (navSettings is not null)
        {
            navSettings.Click += (_, _) =>
            {
                var sheet = new PreferencesBottomSheet();
                sheet.OnSettingsChanged = ApplySettingsToScene;
                sheet.Show(SupportFragmentManager, "prefs");
            };
        }

        var propertiesToggle = FindViewById<MaterialButton>(Resource.Id.propertiesToggle);
        var propertiesPanel = FindViewById<View>(Resource.Id.propertiesPanel);
        if (propertiesToggle is not null && propertiesPanel is not null)
        {
            propertiesToggle.Click += (_, _) =>
            {
                propertiesPanel.Visibility = propertiesPanel.Visibility == ViewStates.Visible
                    ? ViewStates.Gone
                    : ViewStates.Visible;
            };
        }

        // Phone-only DrawerLayout: hamburger in the AppBar opens the nav drawer.
        // Tablet layout has no DrawerLayout, so these lookups return null and the
        // wiring is a no-op.
        var drawer = FindViewById<AndroidX.DrawerLayout.Widget.DrawerLayout>(Resource.Id.drawerLayout);
        var topAppBar = FindViewById<MaterialToolbar>(Resource.Id.topAppBar);
        if (drawer is not null && topAppBar is not null)
        {
            topAppBar.NavigationClick += (_, _) =>
                drawer.OpenDrawer((int)GravityFlags.Start);
        }
    }

    private void OnFitClicked(object? sender, EventArgs e)
    {
        // Same framing as DoubleTap so the nav-rail Fit button and the
        // gesture stay in sync. Uses the initial-frame distance
        // (diag * sqrt(3)) rather than CameraState.FitToBox's tighter
        // perspective formula, which feels too close on a tablet.
        _interaction?.FitToScene();
    }

    private void OnGestureForSelection(TouchGestureEvent ev)
    {
        if (ev.Kind != TouchGestureKind.Tap || _viewport is null) return;

        // Tap positions are in DIPs (AndroidPointerSource scales MotionEvent
        // pixels by 1/density). The pick FBO is sized in physical pixels, so
        // convert back. Resources.DisplayMetrics.Density is constant for the
        // session on a Samsung tablet.
        float density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        if (density <= 0f) density = 1.0f;
        int px = (int)(ev.Position.X * density);
        int py = (int)(ev.Position.Y * density);

        _viewport.PickAsync(px, py, hit => OnPickResult(hit));
    }

    /// <summary>
    /// Tap-time pivot pick. Casts a ray from the camera through the tap
    /// position into the scene, finds the nearest mesh whose world AABB
    /// the ray crosses, and returns that mesh's world center as the pivot.
    /// Synchronous so OrbitBegin / PanZoomBegin can capture the pivot
    /// before the first delta event fires (the adapter holds it for the
    /// remainder of the gesture).
    /// </summary>
    private Vector3d? PickPivotAt(Point2D dipPos)
    {
        if (_viewport is null || _camera is null) return null;
        var scene = _viewport.Renderer.Scene;
        if (scene is null) return null;

        float density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        if (density <= 0f) density = 1.0f;
        double widthDip = _viewport.Width / density;
        double heightDip = _viewport.Height / density;
        if (widthDip <= 0 || heightDip <= 0) return null;

        // NDC: (-1,-1) bottom-left, (+1,+1) top-right. Android tap Y is
        // top-down, so flip Y for the OpenGL convention.
        double nx = (dipPos.X * 2.0 / widthDip) - 1.0;
        double ny = -((dipPos.Y * 2.0 / heightDip) - 1.0);

        Vector3d position = _camera.Position;
        Vector3d target = _camera.Target;
        Vector3d up = _camera.UpDirection.Normalized();
        Vector3d forward = (target - position).Normalized();
        if (forward.LengthSquared < 1e-12) return null;
        Vector3d right = Vector3d.Cross(forward, up).Normalized();
        if (right.LengthSquared < 1e-12) return null;
        Vector3d trueUp = Vector3d.Cross(right, forward);

        Vector3d rayOrigin;
        Vector3d rayDir;
        if (_camera.IsPerspective)
        {
            double halfFovTan = System.Math.Tan(_camera.FieldOfView * 0.5);
            double aspect = widthDip / heightDip;
            rayOrigin = position;
            rayDir = (forward + right * (nx * halfFovTan * aspect) + trueUp * (ny * halfFovTan)).Normalized();
        }
        else
        {
            double aspect = widthDip / heightDip;
            double halfW = _camera.OrthoWidth * 0.5;
            double halfH = halfW / aspect;
            rayOrigin = position + right * (nx * halfW) + trueUp * (ny * halfH);
            rayDir = forward;
        }

        // Mirror desktop ViewportPivotPicker.TryPickPivot: keep the closest
        // forward AABB hit and return the *world point at that hit*, not the
        // mesh's center. For long parts (rails, beams) this is the difference
        // between "orbit around what I touched" and "orbit around the body's
        // midpoint" which feels wrong.
        double closestT = double.MaxValue;
        Vector3d? hitPoint = null;
        foreach (var mesh in scene.Meshes)
        {
            if (!mesh.WorldBounds.IsValid) continue;
            if (TryIntersectRayAabb(rayOrigin, rayDir, mesh.WorldBounds, out double t)
                && t < closestT)
            {
                closestT = t;
                hitPoint = rayOrigin + rayDir * t;
            }
        }
        return hitPoint;
    }

    /// <summary>
    /// Slab-method ray vs AABB. Mirrors Core's RayBoxIntersection.TryHit
    /// (which we can't link because it lives in the Math namespace excluded
    /// from the Android shim per Plan 1's exclusions). tMin starts at 0 so
    /// AABBs entirely behind the camera are rejected; HitDistanceEpsilon
    /// prevents near-coincident origins from registering self-hits.
    /// </summary>
    private static bool TryIntersectRayAabb(Vector3d o, Vector3d d, BoundingBox box, out double t)
    {
        const double DirectionEpsilon = 1e-12;
        const double HitDistanceEpsilon = 1e-6;

        t = 0.0;
        if (!box.IsValid) return false;

        double tMin = 0.0;
        double tMax = double.MaxValue;

        for (int axis = 0; axis < 3; axis++)
        {
            double origin = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
            double dir = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            double mn = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z;
            double mx = axis == 0 ? box.Max.X : axis == 1 ? box.Max.Y : box.Max.Z;

            if (System.Math.Abs(dir) < DirectionEpsilon)
            {
                if (origin < mn || origin > mx) return false;
                continue;
            }

            double invD = 1.0 / dir;
            double t0 = (mn - origin) * invD;
            double t1 = (mx - origin) * invD;
            if (t0 > t1) (t0, t1) = (t1, t0);

            if (t0 > tMin) tMin = t0;
            if (t1 < tMax) tMax = t1;
            if (tMax < tMin) return false;
        }

        // tMin is the entry. If tMin is at-or-below epsilon the camera is
        // inside the box, in which case tMax (exit) is the meaningful hit.
        t = tMin > HitDistanceEpsilon ? tMin : tMax;
        return t > HitDistanceEpsilon && t < double.MaxValue;
    }

    private void OnPickResult(int? meshIndex)
    {
        if (_viewport is null) return;

        // 0 means "no hit" - clear selection.
        int selected = meshIndex ?? 0;
        _viewport.Renderer.SelectedMeshIndex = selected;
        _viewport.RequestRender();

        // Update Properties panel empty state to show the selected mesh
        // index for now. Proper node-name lookup lands when SelectionState
        // is wired (with mesh-index -> node-id resolution).
        var label = FindViewById<TextView>(Resource.Id.propertiesEmptyState);
        if (label is not null)
        {
            label.Text = selected == 0
                ? GetString(Resource.String.properties_empty)
                : $"Mesh #{selected}";
        }
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        if (_picker is null || _import is null || _viewport is null || _camera is null) return;

        AndroidUri? uri;
        try
        {
            uri = await _picker.PickAsync(new[] { "model/gltf-binary", "application/octet-stream", "*/*" });
        }
        catch (Exception ex)
        {
            ShowError("Could not open file picker", ex.Message);
            return;
        }
        if (uri is null) return;

        try
        {
            var document = await _import.ImportAsync(uri, CancellationToken.None);
            _viewport.QueueRendererCommand(gl =>
            {
                var scene = _viewport.Renderer.Scene ?? new GpuScene(gl);
                scene.Load(document);
                _viewport.Renderer.Scene = scene;
                FrameCameraToBounds(_camera, scene.Bounds, GetViewportAspect());
            });
            _viewport.RequestRender();
        }
        catch (Exception ex)
        {
            ShowError("Import failed", ex.Message);
        }
    }

    private static void FrameCameraToBounds(CameraState camera, BoundingBox bounds, double aspect)
    {
        if (!bounds.IsValid) return;
        double diagonal = bounds.Diagonal;
        if (!double.IsFinite(diagonal) || diagonal <= 0.0)
            diagonal = 1.0;
        camera.MinOrthoWidth = diagonal * 0.001;
        camera.MaxOrthoWidth = diagonal * 100.0;
        camera.FitToBox(bounds, aspect);
        camera.UpDirection = Vector3d.UnitZ;
        camera.WorldUpDirection = Vector3d.UnitZ;
    }

    private void ShowError(string title, string message)
    {
        new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetMessage(message)!
            .SetPositiveButton("OK", (_, _) => { })!
            .Show();
    }

    protected override void OnPause() { _viewport?.OnPause(); base.OnPause(); }

    protected override void OnResume()
    {
        base.OnResume();
        _viewport?.OnResume();
        ApplySettingsToScene();
    }

    /// <summary>
    /// Pull every AppSettings value the renderer + interaction adapter
    /// can consume live (everything except MSAA, which is locked to the
    /// EGL config at surface-creation time and requires a restart). Called
    /// on every OnResume so changes made in PreferencesActivity take
    /// effect the moment the user returns.
    /// </summary>
    private void ApplySettingsToScene()
    {
        if (_viewport is not null)
        {
            var appearance = _viewport.Renderer.Appearance;
            AppSettings.Apply(ref appearance);
            if (_camera is not null)
            {
                _camera.SetProjectionMode(appearance.IsPerspective, GetViewportAspect());
                if (_viewport.Renderer.Scene?.Bounds is { IsValid: true } bounds)
                    _camera.UpdateClipPlanes(bounds);
            }
            _viewport.Renderer.Appearance = appearance;
            _viewport.Renderer.HighlightSelection = AppSettings.ShowSelectionHighlight;
            _viewport.RequestRender();
        }
        if (_interaction is not null)
        {
            _interaction.OrbitSensitivityMultiplier = AppSettings.OrbitSensitivity;
            _interaction.PanSensitivityMultiplier = AppSettings.PanSensitivity;
            _interaction.ZoomSensitivityMultiplier = AppSettings.ZoomSensitivity;
        }
    }

    private double GetViewportAspect()
    {
        if (_viewport is { Width: > 0, Height: > 0 })
            return (double)_viewport.Width / _viewport.Height;
        return 1.0;
    }

    /// <summary>
    /// Wraps AndroidPointerSource.OnTouch as a View.IOnTouchListener. The
    /// listener must derive from Java.Lang.Object so it can be passed
    /// through JNI; MainActivity itself already does, but using a small
    /// dedicated object keeps the activity's class hierarchy clean.
    /// </summary>
    private sealed class TouchProxy : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly AndroidPointerSource _source;
        public TouchProxy(AndroidPointerSource source) => _source = source;
        public bool OnTouch(View? v, MotionEvent? e) => _source.OnTouch(e);
    }
}
