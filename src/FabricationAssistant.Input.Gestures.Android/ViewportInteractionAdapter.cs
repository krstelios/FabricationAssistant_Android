using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Translates recognized gesture events into CameraState calls. The host
/// supplies accessors for the current scene bounds, the viewport aspect, and a
/// RequestRender action. Sensitivity constants are tuned for tablet feel
/// (slightly different from the desktop's mouse / wheel values).
///
/// Tap and LongPress are consumed silently - hosts that want to log can
/// subscribe to AndroidPointerSource.GestureRecognized directly. DoubleTap
/// calls <see cref="FitToScene"/>, matching the desktop fit behavior.
/// </summary>
public sealed class ViewportInteractionAdapter
{
    private const double OrbitSensitivityRadiansPerPixel = 0.005;

    // The desktop's 0.002 value yields ~2x the world-units per DIP that a
    // finger expects on this tablet (centroid runs ahead of the finger).
    // 0.001 makes the pan track the centroid 1:1 in practice.
    private const double PanDistanceScale = 0.001;

    private const double MinimumPanDistance = 1e-6;
    private const double PinchScaleDeadband = 0.005;

    private readonly CameraState _camera;
    private readonly Func<BoundingBox?> _boundsAccessor;
    private readonly Func<double> _aspectAccessor;
    private readonly Action _requestRender;
    private readonly Func<Point2D, Vector3d?>? _pivotPicker;

    private bool _orbitActive;
    private bool _panZoomActive;
    private Vector3d? _activeGesturePivot;

    /// <summary>Multiplier applied to the orbit angular velocity. 1.0 is the
    /// tuned default; the settings popup writes this from AppSettings.</summary>
    public double OrbitSensitivityMultiplier { get; set; } = 1.0;

    /// <summary>Multiplier applied to the pan world-units-per-DIP scale.</summary>
    public double PanSensitivityMultiplier { get; set; } = 1.0;

    /// <summary>Exponent on the pinch ratio so the user can stretch or
    /// compress the natural pinch-to-zoom mapping.</summary>
    public double ZoomSensitivityMultiplier { get; set; } = 1.0;

    public ViewportInteractionAdapter(
        CameraState camera,
        Func<BoundingBox?> boundsAccessor,
        Func<double> aspectAccessor,
        Action requestRender,
        Func<Point2D, Vector3d?>? pivotPicker = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _boundsAccessor = boundsAccessor ?? throw new ArgumentNullException(nameof(boundsAccessor));
        _aspectAccessor = aspectAccessor ?? throw new ArgumentNullException(nameof(aspectAccessor));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        _pivotPicker = pivotPicker;
    }

    public void OnGesture(TouchGestureEvent ev)
    {
        switch (ev.Kind)
        {
            case TouchGestureKind.OrbitBegin:
                _orbitActive = true;
                _activeGesturePivot = _pivotPicker?.Invoke(ev.Position);
                RefreshClipPlanes();
                break;

            case TouchGestureKind.OrbitDelta:
            {
                if (!_orbitActive) break;
                double sens = OrbitSensitivityRadiansPerPixel * OrbitSensitivityMultiplier;
                double yaw = ev.PixelDelta.X * sens;
                double pitch = ev.PixelDelta.Y * sens;
                Vector3d pivot = GetPivot();
                _camera.OrbitAroundPoint(pivot, yaw, pitch);
                RefreshClipPlanes();
                _requestRender();
                break;
            }

            case TouchGestureKind.OrbitEnd:
                _orbitActive = false;
                _activeGesturePivot = null;
                _requestRender();
                break;

            case TouchGestureKind.PanZoomBegin:
                _panZoomActive = true;
                _activeGesturePivot = _pivotPicker?.Invoke(ev.Position);
                RefreshClipPlanes();
                break;

            case TouchGestureKind.PanZoomDelta:
            {
                if (!_panZoomActive) break;
                Vector3d pivot = GetPivot();

                double panScale = ComputePanScale(pivot) * PanSensitivityMultiplier;
                _camera.Pan(-ev.PixelDelta.X * panScale, ev.PixelDelta.Y * panScale);

                if (System.Math.Abs(ev.PinchScale - 1.0) > PinchScaleDeadband)
                {
                    // Lifting the raw pinch ratio to a power lets the user
                    // intensify (>1) or soften (<1) the zoom response without
                    // changing the sign of the gesture.
                    double adjusted = System.Math.Pow(ev.PinchScale, ZoomSensitivityMultiplier);
                    _camera.DollyZoomAroundPivot(pivot, adjusted);
                }

                RefreshClipPlanes();
                _requestRender();
                break;
            }

            case TouchGestureKind.PanZoomEnd:
                _panZoomActive = false;
                _activeGesturePivot = null;
                _requestRender();
                break;

            case TouchGestureKind.DoubleTap:
                FitToScene();
                break;

            case TouchGestureKind.Tap:
            case TouchGestureKind.LongPress:
                // Selection is owned by the host view so it can coordinate UI state.
                break;
        }
    }

    /// <summary>
    /// Resets the camera using the shared desktop framing logic. Public so
    /// hosts can wire a Fit button to the same behavior as DoubleTap
    /// (MainActivity's nav-rail Fit button does this).
    /// </summary>
    public void FitToScene()
    {
        BoundingBox? bounds = _boundsAccessor();
        if (bounds is not { IsValid: true } valid) return;

        double diag = valid.Diagonal;
        if (!double.IsFinite(diag) || diag <= 0.0) diag = 1.0;

        _camera.MinOrthoWidth = diag * 0.001;
        _camera.MaxOrthoWidth = diag * 100.0;
        _camera.FitToBox(valid, _aspectAccessor());
        RefreshClipPlanes();
        _requestRender();
    }

    private Vector3d GetPivot()
    {
        // Tap-captured pivot wins for the duration of the gesture so orbit
        // rotates around the body the user actually grabbed, not the
        // scene-bounds center.
        if (_activeGesturePivot is { } captured) return captured;

        BoundingBox? bounds = _boundsAccessor();
        if (bounds is { IsValid: true } valid)
            return valid.Center;
        return _camera.Target;
    }

    private double ComputePanScale(Vector3d pivot)
    {
        if (_camera.IsPerspective)
        {
            double distance = Vector3d.Distance(_camera.Position, pivot);
            if (distance < MinimumPanDistance) distance = _camera.Distance;
            return distance * PanDistanceScale;
        }
        return _camera.Distance * PanDistanceScale;
    }

    private void RefreshClipPlanes()
    {
        BoundingBox? bounds = _boundsAccessor();
        if (bounds is { IsValid: true } valid)
            _camera.UpdateClipPlanes(valid);
    }
}
