using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Sections;

namespace FabricationAssistant.App.Android.Measurement;

internal static class AndroidSectionClipper
{
    public const int MaxSectionPlanes = 8;
    private const double PlaneTolerance = 1e-6;
    private const double RelativePlaneTolerance = 1e-9;

    public static bool IsPointVisible(Vector3d point, IReadOnlyList<SectionPlane>? planes, double sceneDiagonal = 1.0)
    {
        if (!IsFinite(point))
            return false;
        if (planes is null || planes.Count == 0)
            return true;

        double tolerance = ResolvePlaneTolerance(sceneDiagonal);
        int count = Math.Min(planes.Count, MaxSectionPlanes);
        for (int i = 0; i < count; i++)
        {
            SectionPlane plane = planes[i];
            double normalLength = Math.Sqrt(
                plane.Normal.X * plane.Normal.X
                + plane.Normal.Y * plane.Normal.Y
                + plane.Normal.Z * plane.Normal.Z);
            if (!double.IsFinite(normalLength) || normalLength <= 1e-12)
                continue;

            double distance =
                (plane.Normal.X * point.X)
                + (plane.Normal.Y * point.Y)
                + (plane.Normal.Z * point.Z);
            double normalizedDistance = distance / normalLength;
            double normalizedOffset = plane.Offset / normalLength;
            if (normalizedDistance < normalizedOffset - tolerance)
                return false;
        }

        return true;
    }

    private static double ResolvePlaneTolerance(double sceneDiagonal)
        => Math.Max(
            PlaneTolerance,
            double.IsFinite(sceneDiagonal) && sceneDiagonal > 0.0
                ? sceneDiagonal * RelativePlaneTolerance
                : 0.0);

    private static bool IsFinite(Vector3d point)
        => double.IsFinite(point.X)
           && double.IsFinite(point.Y)
           && double.IsFinite(point.Z);
}
