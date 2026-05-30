using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Keeps Android's perspective depth slab tight enough for tablet close-ups.
/// </summary>
public static class AndroidCameraClipPlanes
{
    private const double GroundGridExtentScale = 8.0;
    private const double GroundGridZOffsetScale = 0.001;
    private const double MinimumNearPlane = 0.0001;
    private const double NearPlaneSceneScale = 0.00001;
    private const double NearPlaneDistanceScale = 0.00001;
    private const double NearPlaneFrontFaceScale = 0.5;
    private const double FarPlaneSceneMarginScale = 0.02;
    private const double FarPlaneSpanMarginScale = 0.05;
    private static bool s_manualPerspectiveClipPlanesEnabled;
    private static double s_manualPerspectiveNearPlane = MinimumNearPlane;
    private static double s_manualPerspectiveFarPlane = 10000.0;

    public static void ConfigureManualPerspectiveClipPlanes(bool enabled, double nearPlane, double farPlane)
    {
        if (!enabled || !IsValidPerspectiveClipPlanes(nearPlane, farPlane))
        {
            s_manualPerspectiveClipPlanesEnabled = false;
            return;
        }

        s_manualPerspectiveNearPlane = nearPlane;
        s_manualPerspectiveFarPlane = farPlane;
        s_manualPerspectiveClipPlanesEnabled = true;
    }

    public static void Update(CameraState camera, BoundingBox sceneBounds)
    {
        ArgumentNullException.ThrowIfNull(camera);

        camera.UpdateClipPlanes(sceneBounds);
        if (TryApplyManualPerspectiveClipPlanes(camera))
            return;

        if (!camera.IsPerspective || !sceneBounds.IsValid)
            return;

        if (!TryProjectBoundsToViewDepth(camera, sceneBounds, out double minDepth, out double maxDepth))
            return;

        double diagonal = sceneBounds.Diagonal;
        if (!double.IsFinite(diagonal) || diagonal <= 1e-10)
            diagonal = 1.0;

        double farFace = System.Math.Max(maxDepth, 0.0);
        if (!double.IsFinite(farFace) || farFace <= MinimumNearPlane)
            return;

        double visibleSpan = System.Math.Max(farFace - System.Math.Max(minDepth, 0.0), 0.0);
        double margin = System.Math.Max(diagonal * FarPlaneSceneMarginScale, visibleSpan * FarPlaneSpanMarginScale);
        margin = System.Math.Max(margin, MinimumNearPlane);

        double far = farFace + margin;
        double nearFloor = System.Math.Max(diagonal * NearPlaneSceneScale, camera.Distance * NearPlaneDistanceScale);
        nearFloor = System.Math.Max(nearFloor, MinimumNearPlane);

        double near = minDepth > 0.0
            ? System.Math.Max(minDepth - margin, nearFloor)
            : nearFloor;

        if (minDepth > MinimumNearPlane && near >= minDepth)
            near = System.Math.Max(minDepth * NearPlaneFrontFaceScale, MinimumNearPlane);

        if (near >= far)
            near = System.Math.Max(System.Math.Min(far - margin, far * 0.5), MinimumNearPlane);

        if (!double.IsFinite(near) || !double.IsFinite(far) || near <= 0.0 || far <= near)
            return;

        camera.NearPlane = near;
        camera.FarPlane = far;
    }

    private static bool TryApplyManualPerspectiveClipPlanes(CameraState camera)
    {
        if (!s_manualPerspectiveClipPlanesEnabled || !camera.IsPerspective)
            return false;

        double near = s_manualPerspectiveNearPlane;
        double far = s_manualPerspectiveFarPlane;
        if (!IsValidPerspectiveClipPlanes(near, far))
            return false;

        camera.NearPlane = near;
        camera.FarPlane = far;
        return true;
    }

    private static bool IsValidPerspectiveClipPlanes(double nearPlane, double farPlane)
        => double.IsFinite(nearPlane)
           && double.IsFinite(farPlane)
           && nearPlane >= MinimumNearPlane
           && farPlane > nearPlane;

    public static BoundingBox IncludeGroundGrid(
        BoundingBox sceneBounds,
        bool showGrid,
        bool shiftGridToModelMin)
    {
        if (!showGrid || !sceneBounds.IsValid)
            return sceneBounds;

        double diagonal = sceneBounds.Diagonal;
        if (!double.IsFinite(diagonal) || diagonal <= 1e-10)
            diagonal = 1.0;

        double scale = diagonal * GroundGridExtentScale;
        Vector3d center = sceneBounds.Center;
        double planeZ = (shiftGridToModelMin ? sceneBounds.Min.Z : 0.0) - diagonal * GroundGridZOffsetScale;
        if (!double.IsFinite(center.X)
            || !double.IsFinite(center.Y)
            || !double.IsFinite(scale)
            || !double.IsFinite(planeZ))
        {
            return sceneBounds;
        }

        var expanded = sceneBounds;
        expanded.Expand(new Vector3d(center.X - scale, center.Y - scale, planeZ));
        expanded.Expand(new Vector3d(center.X + scale, center.Y - scale, planeZ));
        expanded.Expand(new Vector3d(center.X + scale, center.Y + scale, planeZ));
        expanded.Expand(new Vector3d(center.X - scale, center.Y + scale, planeZ));
        return expanded;
    }

    private static bool TryProjectBoundsToViewDepth(
        CameraState camera,
        BoundingBox bounds,
        out double minDepth,
        out double maxDepth)
    {
        minDepth = double.MaxValue;
        maxDepth = double.MinValue;

        Vector3d forward = camera.Forward;
        if (!IsFinite(forward) || forward.LengthSquared <= 1e-20)
            return false;

        Vector3d position = camera.Position;
        if (!IsFinite(position))
            return false;

        for (int i = 0; i < 8; i++)
        {
            Vector3d corner = new(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);

            double depth = Vector3d.Dot(corner - position, forward);
            if (!double.IsFinite(depth))
                return false;

            minDepth = System.Math.Min(minDepth, depth);
            maxDepth = System.Math.Max(maxDepth, depth);
        }

        return minDepth <= maxDepth;
    }

    private static bool IsFinite(Vector3d value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
