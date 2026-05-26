using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Sections;

namespace FabricationAssistant.App.Android.Measurement;

internal static class AndroidSectionClipper
{
    public const int MaxSectionPlanes = 8;
    private const double PlaneTolerance = 1e-6;

    public static bool IsPointVisible(Vector3d point, IReadOnlyList<SectionPlane>? planes)
    {
        if (!IsFinite(point))
            return false;
        if (planes is null || planes.Count == 0)
            return true;

        int count = Math.Min(planes.Count, MaxSectionPlanes);
        for (int i = 0; i < count; i++)
        {
            SectionPlane plane = planes[i];
            double distance =
                (plane.Normal.X * point.X)
                + (plane.Normal.Y * point.Y)
                + (plane.Normal.Z * point.Z);
            if (distance < plane.Offset - PlaneTolerance)
                return false;
        }

        return true;
    }

    private static bool IsFinite(Vector3d point)
        => double.IsFinite(point.X)
           && double.IsFinite(point.Y)
           && double.IsFinite(point.Z);
}
