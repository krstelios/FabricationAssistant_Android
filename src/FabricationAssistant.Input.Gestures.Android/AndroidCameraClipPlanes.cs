using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Keeps Android's perspective depth slab tight enough for tablet close-ups.
/// </summary>
public static class AndroidCameraClipPlanes
{
    private const double MinimumNearPlane = 0.0001;
    private const double NearPlaneSceneScale = 0.0005;
    private const double NearPlaneDistanceScale = 0.001;
    private const double FarPlaneSceneMarginScale = 0.02;
    private const double FarPlaneSpanMarginScale = 0.05;

    public static void Update(CameraState camera, BoundingBox sceneBounds)
    {
        ArgumentNullException.ThrowIfNull(camera);

        camera.UpdateClipPlanes(sceneBounds);
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

        if (near >= far)
            near = System.Math.Max(System.Math.Min(far - margin, far * 0.5), MinimumNearPlane);

        if (!double.IsFinite(near) || !double.IsFinite(far) || near <= 0.0 || far <= near)
            return;

        camera.NearPlane = near;
        camera.FarPlane = far;
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
