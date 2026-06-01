using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Translates recognized gesture events into CameraState calls. The host
/// supplies accessors for the current navigation bounds, the viewport aspect,
/// and a RequestRender action. Sensitivity constants are tuned for tablet feel
/// (slightly different from the desktop's mouse / wheel values).
///
/// Tap and LongPress are consumed silently - hosts that want to log can
/// subscribe to AndroidPointerSource.GestureRecognized directly. DoubleTap
/// can call <see cref="FitToScene"/>, matching the desktop fit behavior.
/// </summary>
public sealed class ViewportInteractionAdapter
{
    private const double OrbitSensitivityRadiansPerPixel = 0.005;
    private const double MouseOrbitSensitivityRadiansPerPixel = 0.005;

    // The desktop's 0.002 value yields ~2x the world-units per DIP that a
    // finger expects on this tablet (centroid runs ahead of the finger).
    // 0.001 makes the pan track the centroid 1:1 in practice.
    private const double PanDistanceScale = 0.001;
    private const double MousePanDistanceScale = 0.002;
    private const double DefaultMousePivotSpeed = 0.8;
    private const double DefaultMousePanSpeed = 0.3;
    private const double DefaultMouseZoomSpeed = 1.0;

    private const double MinimumPanDistance = 1e-6;
    private const double PinchScaleDeadband = 0.005;
    private const double OrbitAnchorDenominatorEpsilon = 1e-8;
    private const double OrbitAnchorTranslationEpsilon = 1e-12;

    private readonly CameraState _camera;
    private readonly Func<BoundingBox?> _boundsAccessor;
    private readonly Func<BoundingBox?> _clipBoundsAccessor;
    private readonly Func<double> _aspectAccessor;
    private readonly Func<double>? _viewportWidthDipAccessor;
    private readonly Action _requestRender;
    private readonly Action<bool>? _interactionStateChanged;
    private readonly Func<Point2D, Vector3d?>? _pivotPicker;
    private readonly Func<bool>? _isFixedViewLockedAccessor;
    private readonly Func<bool>? _isNavigationSuppressedAccessor;
    private readonly Func<bool>? _isDoubleTapFitEnabledAccessor;
    private readonly GestureDeltaNormalizer _orbitDeltaNormalizer = new();
    private readonly GestureDeltaNormalizer _panDeltaNormalizer = new();
    private readonly PinchScaleNormalizer _pinchScaleNormalizer = new();

    private bool _orbitActive;
    private bool _panZoomActive;
    private bool _mouseOrbitActive;
    private bool _mousePanActive;
    private bool _hasMouseOrbitAnchor;
    private Vector3d? _activeGesturePivot;
    private Vector3d? _lastResolvedPivot;
    private Vector3d? _panZoomAnchorWorld;
    private Point2D _mouseOrbitAnchorScreenPosition;
    private double _orbitSensitivityMultiplier = 1.0;
    private double _panSensitivityMultiplier = 1.0;
    private double _zoomSensitivityMultiplier = 1.0;
    private double _mouseOrbitSpeedMultiplier = 1.0;
    private double _mousePanSpeedMultiplier = 1.1;
    private double _mouseWheelZoomSpeedMultiplier = 1.0;
    private bool _mouseWheelZoomInverted;

    /// <summary>Multiplier applied to the orbit angular velocity. 1.0 is the
    /// tuned default; the settings popup writes this from AppSettings.</summary>
    public double OrbitSensitivityMultiplier
    {
        get => _orbitSensitivityMultiplier;
        set => _orbitSensitivityMultiplier = ClampSensitivity(value);
    }

    /// <summary>Multiplier applied to the pan world-units-per-DIP scale.</summary>
    public double PanSensitivityMultiplier
    {
        get => _panSensitivityMultiplier;
        set => _panSensitivityMultiplier = ClampSensitivity(value);
    }

    /// <summary>Exponent on the pinch ratio so the user can stretch or
    /// compress the natural pinch-to-zoom mapping.</summary>
    public double ZoomSensitivityMultiplier
    {
        get => _zoomSensitivityMultiplier;
        set => _zoomSensitivityMultiplier = ClampSensitivity(value);
    }

    public double MouseOrbitSpeedMultiplier
    {
        get => _mouseOrbitSpeedMultiplier;
        set => _mouseOrbitSpeedMultiplier = ClampSensitivity(value);
    }

    public double MousePanSpeedMultiplier
    {
        get => _mousePanSpeedMultiplier;
        set => _mousePanSpeedMultiplier = ClampSensitivity(value);
    }

    public double MouseWheelZoomSpeedMultiplier
    {
        get => _mouseWheelZoomSpeedMultiplier;
        set => _mouseWheelZoomSpeedMultiplier = ClampSensitivity(value);
    }

    public bool MouseWheelZoomInverted
    {
        get => _mouseWheelZoomInverted;
        set => _mouseWheelZoomInverted = value;
    }

    public ViewportInteractionAdapter(
        CameraState camera,
        Func<BoundingBox?> boundsAccessor,
        Func<double> aspectAccessor,
        Action requestRender,
        Action<bool>? interactionStateChanged = null,
        Func<Point2D, Vector3d?>? pivotPicker = null,
        Func<double>? viewportWidthDipAccessor = null,
        Func<bool>? isFixedViewLockedAccessor = null,
        Func<bool>? isNavigationSuppressedAccessor = null,
        Func<BoundingBox?>? clipBoundsAccessor = null,
        Func<bool>? isDoubleTapFitEnabledAccessor = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _boundsAccessor = boundsAccessor ?? throw new ArgumentNullException(nameof(boundsAccessor));
        _clipBoundsAccessor = clipBoundsAccessor ?? _boundsAccessor;
        _aspectAccessor = aspectAccessor ?? throw new ArgumentNullException(nameof(aspectAccessor));
        _viewportWidthDipAccessor = viewportWidthDipAccessor;
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));
        _interactionStateChanged = interactionStateChanged;
        _pivotPicker = pivotPicker;
        _isFixedViewLockedAccessor = isFixedViewLockedAccessor;
        _isNavigationSuppressedAccessor = isNavigationSuppressedAccessor;
        _isDoubleTapFitEnabledAccessor = isDoubleTapFitEnabledAccessor;
    }

    public void OnGesture(TouchGestureEvent ev)
    {
        if (IsNavigationSuppressed() && IsCameraGesture(ev.Kind))
        {
            if (_orbitActive || _panZoomActive || _mouseOrbitActive || _mousePanActive)
            {
                _orbitActive = false;
                _panZoomActive = false;
                _mouseOrbitActive = false;
                _mousePanActive = false;
                _hasMouseOrbitAnchor = false;
                _activeGesturePivot = null;
                _panZoomAnchorWorld = null;
                _orbitDeltaNormalizer.Reset();
                _panDeltaNormalizer.Reset();
                _pinchScaleNormalizer.Reset();
                _interactionStateChanged?.Invoke(false);
            }
            return;
        }

        switch (ev.Kind)
        {
            case TouchGestureKind.OrbitBegin:
                if (IsFixedViewLocked())
                {
                    _orbitActive = false;
                    _activeGesturePivot = null;
                    _orbitDeltaNormalizer.Reset();
                    break;
                }

                _orbitActive = true;
                _interactionStateChanged?.Invoke(true);
                _orbitDeltaNormalizer.Reset();
                lock (_camera)
                {
                    CaptureGesturePivot(ev.Position);
                    RefreshClipPlanes();
                }
                break;

            case TouchGestureKind.OrbitDelta:
            {
                if (!_orbitActive || IsFixedViewLocked()) break;
                Vector2D pixelDelta = _orbitDeltaNormalizer.Normalize(ev.PixelDelta);
                if (pixelDelta.Length <= 0.0)
                    break;

                double sens = OrbitSensitivityRadiansPerPixel * OrbitSensitivityMultiplier;
                double yaw = pixelDelta.X * sens;
                double pitch = pixelDelta.Y * sens;
                lock (_camera)
                {
                    Vector3d pivot = GetPivot();
                    _camera.OrbitAroundPoint(pivot, yaw, pitch);
                    RefreshClipPlanes();
                }
                _requestRender();
                break;
            }

            case TouchGestureKind.OrbitEnd:
            {
                bool wasOrbitActive = _orbitActive;
                _orbitActive = false;
                _activeGesturePivot = null;
                _orbitDeltaNormalizer.Reset();
                if (wasOrbitActive)
                    _interactionStateChanged?.Invoke(false);
                if (!IsFixedViewLocked())
                    _requestRender();
                break;
            }

            case TouchGestureKind.PanZoomBegin:
                _panZoomActive = true;
                _interactionStateChanged?.Invoke(true);
                _panDeltaNormalizer.Reset();
                _pinchScaleNormalizer.Reset();
                lock (_camera)
                {
                    CaptureGesturePivot(ev.Position);
                    CapturePanZoomAnchor(ev.Position);
                    RefreshClipPlanes();
                }
                // S12-F4: CaptureGesturePivot may widen a narrow FOV via
                // NormalizeZoomAroundPivot. Unlike OrbitBegin (always followed by an
                // OrbitDelta that renders), PanZoomBegin has no immediate delta, so
                // request a render here to show the normalized camera at once.
                _requestRender();
                break;

            case TouchGestureKind.PanZoomDelta:
            {
                if (!_panZoomActive) break;

                bool cameraChanged = false;
                lock (_camera)
                {
                    bool usedAnchoredPanZoom = _panZoomAnchorWorld is { } anchor
                        && TryApplyAnchoredPanZoom(ev.Position, SanitizePinchScale(ev.PinchScale), anchor, out cameraChanged);

                    if (!usedAnchoredPanZoom)
                    {
                        Vector3d pivot = GetPivot();
                        Vector2D panDelta = _panDeltaNormalizer.Normalize(ev.PixelDelta);
                        double pinchScale = _pinchScaleNormalizer.Normalize(ev.PinchScale);

                        double panScale = ComputePanScale(pivot) * PanSensitivityMultiplier;
                        if (panDelta.Length > 0.0)
                        {
                            _camera.Pan(-panDelta.X * panScale, panDelta.Y * panScale);
                            cameraChanged = true;
                        }

                        if (System.Math.Abs(pinchScale - 1.0) > PinchScaleDeadband)
                        {
                            double adjusted = System.Math.Pow(pinchScale, ZoomSensitivityMultiplier);
                            _camera.DollyZoomAroundPivot(pivot, adjusted);
                            cameraChanged = true;
                        }
                    }

                    if (cameraChanged)
                        RefreshClipPlanes();
                }

                if (cameraChanged)
                {
                    _requestRender();
                }
                break;
            }

            case TouchGestureKind.PanZoomEnd:
                _panZoomActive = false;
                _activeGesturePivot = null;
                _panZoomAnchorWorld = null;
                _panDeltaNormalizer.Reset();
                _pinchScaleNormalizer.Reset();
                _interactionStateChanged?.Invoke(false);
                _requestRender();
                break;

            case TouchGestureKind.MouseOrbitBegin:
                if (IsFixedViewLocked())
                {
                    _mouseOrbitActive = false;
                    _hasMouseOrbitAnchor = false;
                    _activeGesturePivot = null;
                    break;
                }

                _mouseOrbitActive = true;
                _interactionStateChanged?.Invoke(true);
                lock (_camera)
                {
                    CaptureGesturePivot(ev.Position);
                    RefreshClipPlanes();
                    _hasMouseOrbitAnchor = TryCaptureMouseOrbitAnchor();
                }
                break;

            case TouchGestureKind.MouseOrbitDelta:
            {
                if (!_mouseOrbitActive || IsFixedViewLocked()) break;
                Vector2D pixelDelta = ev.PixelDelta;
                if (pixelDelta.Length <= 0.0)
                    break;

                double sens = MouseOrbitSensitivityRadiansPerPixel * DefaultMousePivotSpeed * MouseOrbitSpeedMultiplier;
                double yaw = pixelDelta.X * sens;
                double pitch = pixelDelta.Y * sens;
                lock (_camera)
                {
                    Vector3d pivot = GetPivot();
                    _camera.OrbitAroundPoint(pivot, yaw, pitch);
                    ApplyMouseOrbitAnchorCompensation(pivot);
                    RefreshClipPlanes();
                }
                _requestRender();
                break;
            }

            case TouchGestureKind.MouseOrbitEnd:
            {
                bool wasMouseOrbitActive = _mouseOrbitActive;
                _mouseOrbitActive = false;
                _hasMouseOrbitAnchor = false;
                _activeGesturePivot = null;
                if (wasMouseOrbitActive)
                    _interactionStateChanged?.Invoke(false);
                if (!IsFixedViewLocked())
                    _requestRender();
                break;
            }

            case TouchGestureKind.MousePanBegin:
                _mousePanActive = true;
                _activeGesturePivot = _lastResolvedPivot;
                _interactionStateChanged?.Invoke(true);
                break;

            case TouchGestureKind.MousePanDelta:
            {
                if (!_mousePanActive) break;
                Vector2D pixelDelta = ev.PixelDelta;
                if (pixelDelta.Length <= 0.0)
                    break;

                lock (_camera)
                {
                    PerformMousePan(pixelDelta);
                    RefreshClipPlanes();
                }
                _requestRender();
                break;
            }

            case TouchGestureKind.MousePanEnd:
            {
                bool wasMousePanActive = _mousePanActive;
                _mousePanActive = false;
                _activeGesturePivot = null;
                if (wasMousePanActive)
                    _interactionStateChanged?.Invoke(false);
                _requestRender();
                break;
            }

            case TouchGestureKind.MouseWheel:
            {
                double wheelNotches = ev.PixelDelta.Y;
                if (!double.IsFinite(wheelNotches) || System.Math.Abs(wheelNotches) <= 0.0)
                    break;
                if (MouseWheelZoomInverted)
                    wheelNotches = -wheelNotches;

                lock (_camera)
                {
                    Vector3d zoomPivot = ResolveCursorZoomPivot(ev.Position);
                    _camera.ZoomAroundPoint(zoomPivot, wheelNotches * DefaultMouseZoomSpeed * MouseWheelZoomSpeedMultiplier);
                    RefreshClipPlanes();
                }
                _requestRender();
                break;
            }

            case TouchGestureKind.Cancel:
                if (_mouseOrbitActive || _mousePanActive)
                    _interactionStateChanged?.Invoke(false);
                _mouseOrbitActive = false;
                _mousePanActive = false;
                _hasMouseOrbitAnchor = false;
                _activeGesturePivot = null;
                break;

            case TouchGestureKind.DoubleTap:
                if (IsDoubleTapFitEnabled())
                    FitToScene();
                break;

            case TouchGestureKind.Tap:
            case TouchGestureKind.LongPress:
            case TouchGestureKind.SecondaryTap:
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

        lock (_camera)
        {
            _camera.MinOrthoWidth = diag * 0.001;
            _camera.MaxOrthoWidth = diag * 100.0;
            _camera.FitToBox(valid, _aspectAccessor());
            RefreshClipPlanes();
        }
        _requestRender();
    }

    /// <summary>
    /// Seeds the fallback pivot used by the next orbit or pan/zoom gesture.
    /// Hosts call this after command-driven camera moves, such as Zoom Selected,
    /// so the following gesture orbits around the framed target instead of the
    /// previous touch-resolved point.
    /// </summary>
    public void SetNavigationPivot(Vector3d pivot)
    {
        if (!IsFinite(pivot))
            return;

        _lastResolvedPivot = pivot;
        _activeGesturePivot = null;
    }

    /// <summary>
    /// Clears the fallback navigation pivot so the next gesture re-resolves it
    /// (picked point, else scene-bounds center). Hosts call this on model load:
    /// without it, the first orbit/pinch over empty space after a model swap
    /// reuses the previous model's pivot (S12-F2).
    /// </summary>
    public void ResetNavigationPivot()
    {
        _lastResolvedPivot = null;
        _activeGesturePivot = null;
    }

    private Vector3d GetPivot()
    {
        // Tap-captured pivot wins for the duration of the gesture so orbit
        // rotates around the body the user actually grabbed, not the
        // scene-bounds center.
        if (_activeGesturePivot is { } captured) return captured;
        if (_lastResolvedPivot is { } resolved) return resolved;

        BoundingBox? bounds = _boundsAccessor();
        if (bounds is { IsValid: true } valid)
            return valid.Center;
        return _camera.Target;
    }

    private void CaptureGesturePivot(Point2D position)
    {
        Vector3d? pickedPivot = _pivotPicker?.Invoke(position);
        if (pickedPivot is { } resolvedPivot)
            _lastResolvedPivot = resolvedPivot;

        _activeGesturePivot = pickedPivot ?? _lastResolvedPivot;
        if (_activeGesturePivot is { } pivot)
            _camera.NormalizeZoomAroundPivot(pivot);
    }

    private void CapturePanZoomAnchor(Point2D position)
    {
        Vector3d pivot = GetPivot();
        _panZoomAnchorWorld = TryScreenPointToWorldOnPlane(position, pivot, out Vector3d anchor)
            ? anchor
            : pivot;
    }

    private bool TryApplyAnchoredPanZoom(
        Point2D centroid,
        double pinchScale,
        Vector3d anchor,
        out bool cameraChanged)
    {
        cameraChanged = false;

        if (System.Math.Abs(pinchScale - 1.0) > 1e-9)
        {
            // S12#1: apply the zoom sensitivity here - this anchored path is the
            // one taken on real devices, so it must honor the slider. The
            // exponent on the pinch ratio matches the fallback branch;
            // Pow(scale, 1.0) == scale, so the default multiplier preserves the
            // raw finger-scale, world-locked zoom.
            double adjustedPinchScale = System.Math.Pow(pinchScale, ZoomSensitivityMultiplier);
            _camera.DollyZoomAroundPivot(anchor, adjustedPinchScale);
            cameraChanged = true;
        }

        if (!TryScreenPointToWorldOnPlane(centroid, anchor, out Vector3d worldUnderCentroid))
            return cameraChanged;

        Vector3d correction = anchor - worldUnderCentroid;
        if (correction.LengthSquared <= 1e-18)
            return cameraChanged;

        // S12#1: scale the pan correction by the pan sensitivity multiplier. At
        // the default 1.0 this is identity (the world point stays locked under
        // the centroid); other values let the Pan slider speed up or slow down
        // two-finger panning, which previously did nothing because the
        // multiplier was only consulted in the effectively-never-hit fallback.
        correction *= PanSensitivityMultiplier;

        _camera.Position = _camera.Position + correction;
        _camera.Target = _camera.Target + correction;
        cameraChanged = true;
        return true;
    }

    private void PerformMousePan(Vector2D pixelDelta)
    {
        Vector3d pivot = GetPivot();
        double panScale = ComputeMousePanScale(pivot);
        Vector3d previousPosition = _camera.Position;

        _camera.Pan(-pixelDelta.X * panScale, pixelDelta.Y * panScale);

        Vector3d translation = _camera.Position - previousPosition;
        Vector3d updatedPivot = pivot + translation;
        _lastResolvedPivot = updatedPivot;
        _activeGesturePivot = updatedPivot;
    }

    private double ComputeMousePanScale(Vector3d pivot)
    {
        if (_camera.IsPerspective)
        {
            double distance = Vector3d.Distance(_camera.Position, pivot);
            if (distance < MinimumPanDistance)
                distance = _camera.Distance;
            return distance * MousePanDistanceScale * DefaultMousePanSpeed * MousePanSpeedMultiplier;
        }

        double viewportWidthDip = _viewportWidthDipAccessor?.Invoke() ?? 1.0;
        if (!double.IsFinite(viewportWidthDip) || viewportWidthDip < 1.0)
            viewportWidthDip = 1.0;
        return System.Math.Max(_camera.OrthoWidth, MinimumPanDistance) / viewportWidthDip;
    }

    /// <summary>
    /// Resolves the world-space point under the cursor to use as the mouse-wheel
    /// zoom pivot, so zoom keeps the point under the pointer fixed on screen for
    /// BOTH perspective and orthographic cameras (ZoomAroundPoint holds the pivot
    /// screen-stationary). Prefers the geometry the cursor is over; otherwise the
    /// cursor ray intersected with the plane through the current pivot (so empty
    /// space still zooms toward the cursor); finally the scene pivot. The pivot is
    /// always on the eye->cursor ray, which is what keeps the cursor point fixed.
    /// </summary>
    private Vector3d ResolveCursorZoomPivot(Point2D position)
    {
        if (_pivotPicker?.Invoke(position) is { } picked && IsFinite(picked))
            return picked;

        Vector3d pivot = GetPivot();
        if (TryScreenPointToWorldOnPlane(position, pivot, out Vector3d onPlane) && IsFinite(onPlane))
            return onPlane;

        return pivot;
    }

    private bool TryCaptureMouseOrbitAnchor()
    {
        if (TryProjectWorldToViewport(GetPivot(), out Point2D screenPosition))
        {
            _mouseOrbitAnchorScreenPosition = screenPosition;
            return true;
        }

        return false;
    }

    private bool TryProjectWorldToViewport(Vector3d world, out Point2D screenPosition)
    {
        screenPosition = default;

        double viewportWidthDip = _viewportWidthDipAccessor?.Invoke() ?? 0.0;
        double aspect = _aspectAccessor();
        if (!double.IsFinite(viewportWidthDip) || viewportWidthDip <= 1.0)
            return false;
        if (!double.IsFinite(aspect) || aspect <= 1e-6)
            return false;

        double viewportHeightDip = viewportWidthDip / aspect;
        if (!double.IsFinite(viewportHeightDip) || viewportHeightDip <= 1.0)
            return false;

        Matrix4d viewProjection = _camera.GetProjectionMatrix(aspect) * _camera.GetViewMatrix();
        if (!viewProjection.TryProjectPoint(world, out Vector3d ndc))
            return false;

        if (!IsFinite(ndc))
            return false;

        screenPosition = new Point2D(
            (ndc.X * 0.5 + 0.5) * viewportWidthDip,
            (0.5 - ndc.Y * 0.5) * viewportHeightDip);
        return double.IsFinite(screenPosition.X) && double.IsFinite(screenPosition.Y);
    }

    private void ApplyMouseOrbitAnchorCompensation(Vector3d pivot)
    {
        if (!_hasMouseOrbitAnchor)
            return;

        Vector3d forward = _camera.Forward;
        if (forward.LengthSquared < OrbitAnchorTranslationEpsilon)
            return;

        if (!TryCreateWorldRay(_mouseOrbitAnchorScreenPosition, out Vector3d rayOrigin, out Vector3d rayDirection))
            return;

        double denominator = Vector3d.Dot(rayDirection, forward);
        if (System.Math.Abs(denominator) < OrbitAnchorDenominatorEpsilon)
            return;

        double t = Vector3d.Dot(pivot - rayOrigin, forward) / denominator;
        if (_camera.IsPerspective && t <= 0.0)
            return;

        Vector3d anchorHitPoint = rayOrigin + rayDirection * t;
        Vector3d translation = pivot - anchorHitPoint;
        if (translation.LengthSquared < OrbitAnchorTranslationEpsilon)
            return;

        Vector3d right = Vector3d.Cross(forward, _camera.UpDirection).Normalized();
        Vector3d up = Vector3d.Cross(right, forward).Normalized();
        if (right.LengthSquared < OrbitAnchorTranslationEpsilon
            || up.LengthSquared < OrbitAnchorTranslationEpsilon)
        {
            return;
        }

        double panX = Vector3d.Dot(translation, right);
        double panY = Vector3d.Dot(translation, up);
        _camera.Pan(panX, panY);
    }

    private bool TryScreenPointToWorldOnPlane(Point2D position, Vector3d planePoint, out Vector3d worldPoint)
    {
        worldPoint = default;

        if (!TryCreateWorldRay(position, out Vector3d rayOrigin, out Vector3d rayDirection))
            return false;

        Vector3d planeNormal = _camera.Forward;
        if (planeNormal.LengthSquared < 1e-20)
            return false;

        double denom = Vector3d.Dot(rayDirection, planeNormal);
        if (System.Math.Abs(denom) < 1e-12)
            return false;

        double t = Vector3d.Dot(planePoint - rayOrigin, planeNormal) / denom;
        if (!double.IsFinite(t))
            return false;

        worldPoint = rayOrigin + rayDirection * t;
        return IsFinite(worldPoint);
    }

    private bool TryCreateWorldRay(Point2D position, out Vector3d rayOrigin, out Vector3d rayDirection)
    {
        rayOrigin = default;
        rayDirection = default;

        double viewportWidthDip = _viewportWidthDipAccessor?.Invoke() ?? 0.0;
        double aspect = _aspectAccessor();
        if (!double.IsFinite(viewportWidthDip) || viewportWidthDip <= 1.0)
            return false;
        if (!double.IsFinite(aspect) || aspect <= 1e-6)
            return false;

        double viewportHeightDip = viewportWidthDip / aspect;
        if (!double.IsFinite(viewportHeightDip) || viewportHeightDip <= 1.0)
            return false;

        double nx = (position.X * 2.0 / viewportWidthDip) - 1.0;
        double ny = -((position.Y * 2.0 / viewportHeightDip) - 1.0);
        if (!double.IsFinite(nx) || !double.IsFinite(ny))
            return false;

        Vector3d forward = _camera.Forward;
        Vector3d up = _camera.UpDirection.Normalized();
        if (forward.LengthSquared < 1e-20 || up.LengthSquared < 1e-20)
            return false;

        Vector3d right = Vector3d.Cross(forward, up).Normalized();
        if (right.LengthSquared < 1e-20)
            return false;

        Vector3d trueUp = Vector3d.Cross(right, forward).Normalized();
        if (trueUp.LengthSquared < 1e-20)
            return false;

        if (_camera.IsPerspective)
        {
            double tanHalfFov = System.Math.Tan(_camera.FieldOfView * 0.5);
            if (!double.IsFinite(tanHalfFov) || tanHalfFov <= 0.0)
                return false;

            rayOrigin = _camera.Position;
            rayDirection = (forward + right * (nx * tanHalfFov * aspect) + trueUp * (ny * tanHalfFov)).Normalized();
        }
        else
        {
            double halfWidth = System.Math.Max(_camera.OrthoWidth, MinimumPanDistance) * 0.5;
            double halfHeight = halfWidth / aspect;
            rayOrigin = _camera.Position + right * (nx * halfWidth) + trueUp * (ny * halfHeight);
            rayDirection = forward;
        }

        return IsFinite(rayOrigin) && IsFinite(rayDirection) && rayDirection.LengthSquared > 1e-20;
    }

    private double ComputePanScale(Vector3d pivot)
    {
        if (_camera.IsPerspective)
        {
            double distance = Vector3d.Distance(_camera.Position, pivot);
            if (distance < MinimumPanDistance) distance = _camera.Distance;
            return distance * PanDistanceScale;
        }
        double viewportWidthDip = _viewportWidthDipAccessor?.Invoke() ?? 0.0;
        if (double.IsFinite(viewportWidthDip) && viewportWidthDip > 1.0)
            return System.Math.Max(_camera.OrthoWidth, MinimumPanDistance) / viewportWidthDip;

        return _camera.Distance * PanDistanceScale;
    }

    private void RefreshClipPlanes()
    {
        BoundingBox? bounds = _clipBoundsAccessor();
        if (bounds is { IsValid: true } valid)
            AndroidCameraClipPlanes.Update(_camera, valid);
    }

    private bool IsFixedViewLocked()
        => _isFixedViewLockedAccessor?.Invoke() == true;

    private bool IsNavigationSuppressed()
        => _isNavigationSuppressedAccessor?.Invoke() == true;

    private bool IsDoubleTapFitEnabled()
        => _isDoubleTapFitEnabledAccessor?.Invoke() ?? true;

    private static bool IsCameraGesture(TouchGestureKind kind)
        => kind is TouchGestureKind.OrbitBegin
            or TouchGestureKind.OrbitDelta
            or TouchGestureKind.OrbitEnd
            or TouchGestureKind.PanZoomBegin
            or TouchGestureKind.PanZoomDelta
            or TouchGestureKind.PanZoomEnd
            or TouchGestureKind.MouseOrbitBegin
            or TouchGestureKind.MouseOrbitDelta
            or TouchGestureKind.MouseOrbitEnd
            or TouchGestureKind.MousePanBegin
            or TouchGestureKind.MousePanDelta
            or TouchGestureKind.MousePanEnd
            or TouchGestureKind.MouseWheel
            or TouchGestureKind.DoubleTap;

    private sealed class GestureDeltaNormalizer
    {
        private const double NoiseDeadbandDip = 0.45;
        private const double SmoothingAlpha = 0.58;
        private const double MaxDeltaDip = 48.0;
        private const double MaxAccelerationRatio = 2.35;
        private const double MaxAccelerationExtraDip = 8.0;

        private Vector2D _previous;
        private bool _hasPrevious;
        private int _sampleCount;

        public Vector2D Normalize(Vector2D raw)
        {
            if (!IsFinite(raw))
                return Vector2D.Zero;

            double rawLength = raw.Length;
            if (rawLength < NoiseDeadbandDip)
            {
                _hasPrevious = false;
                return Vector2D.Zero;
            }

            Vector2D limited = Limit(raw, rawLength);
            Vector2D filtered = _hasPrevious
                ? Mix(_previous, limited, SmoothingAlpha)
                : limited;

            filtered = Scale(filtered, StartupRamp(_sampleCount));
            _previous = filtered;
            _hasPrevious = true;
            _sampleCount++;
            return filtered;
        }

        public void Reset()
        {
            _previous = Vector2D.Zero;
            _hasPrevious = false;
            _sampleCount = 0;
        }

        private Vector2D Limit(Vector2D raw, double rawLength)
        {
            double maxLength = MaxDeltaDip;
            if (_hasPrevious)
            {
                double previousLength = _previous.Length;
                maxLength = System.Math.Min(
                    maxLength,
                    System.Math.Max(MaxAccelerationExtraDip, previousLength * MaxAccelerationRatio + MaxAccelerationExtraDip));
            }

            return rawLength > maxLength
                ? Scale(raw, maxLength / rawLength)
                : raw;
        }
    }

    private sealed class PinchScaleNormalizer
    {
        private const double LogDeadband = 0.004;
        private const double MaxLogDelta = 0.75;

        public double Normalize(double rawScale)
        {
            if (!double.IsFinite(rawScale) || rawScale <= 0.0)
                return 1.0;

            double logScale = System.Math.Log(rawScale);
            if (System.Math.Abs(logScale) < LogDeadband)
            {
                return 1.0;
            }

            logScale = System.Math.Clamp(logScale, -MaxLogDelta, MaxLogDelta);
            return System.Math.Exp(logScale);
        }

        public void Reset()
        {
        }
    }

    private static double StartupRamp(int sampleCount)
        => sampleCount switch
        {
            0 => 0.45,
            1 => 0.68,
            2 => 0.86,
            _ => 1.0,
        };

    private static Vector2D Mix(Vector2D previous, Vector2D current, double alpha)
        => new(
            previous.X * (1.0 - alpha) + current.X * alpha,
            previous.Y * (1.0 - alpha) + current.Y * alpha);

    private static Vector2D Scale(Vector2D value, double scale)
        => new(value.X * scale, value.Y * scale);

    private static bool IsFinite(Vector2D value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y);

    private static bool IsFinite(Vector3d value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static double SanitizePinchScale(double rawScale)
    {
        if (!double.IsFinite(rawScale) || rawScale <= 0.0)
            return 1.0;

        double logScale = System.Math.Log(rawScale);
        logScale = System.Math.Clamp(logScale, -0.75, 0.75);
        return System.Math.Exp(logScale);
    }

    private static double ClampSensitivity(double value)
        => double.IsFinite(value) ? System.Math.Clamp(value, 1e-3, 100.0) : 1.0;
}
