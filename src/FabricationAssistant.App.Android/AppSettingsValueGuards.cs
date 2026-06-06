using FabricationAssistant.Rendering.Gles;

namespace FabricationAssistant.App.Android;

internal static class AppSettingsValueGuards
{
    public const int MinAndroidMsaaSamples = 0;
    public const int MaxAndroidMsaaSamples = 8;

    private const int ModeShadedWithEdges = (int)RenderMode.ShadedWithEdges;
    private const int ModeShaded = (int)RenderMode.Shaded;

    public static float Clamp01(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

    public static float ClampAndSnap(float value, float min, float max, float step, float fallback)
    {
        if (!float.IsFinite(value))
            value = fallback;

        float clamped = Math.Clamp(value, min, max);
        if (!float.IsFinite(step) || step <= 0.0f)
            return clamped;

        double snapped = Math.Round(clamped / (double)step, MidpointRounding.AwayFromZero) * step;
        snapped = Math.Round(snapped, DecimalPlacesForStep(step), MidpointRounding.AwayFromZero);
        return (float)Math.Clamp(snapped, min, max);
    }

    public static float ClampAndRoundToSignificantDigits(
        float value,
        float min,
        float max,
        int significantDigits,
        float fallback)
    {
        if (!float.IsFinite(value))
            value = fallback;

        float clamped = Math.Clamp(value, min, max);
        if (clamped == 0.0f || significantDigits <= 0)
            return clamped;

        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(Math.Abs(clamped))) - significantDigits + 1.0);
        double snapped = Math.Round(clamped / magnitude, MidpointRounding.AwayFromZero) * magnitude;
        return (float)Math.Clamp(snapped, min, max);
    }

    public static float ResolveLinearSliderStep(float min, float max)
    {
        float range = Math.Abs(max - min);
        if (range <= 0.003f)
            return 0.00005f;
        if (range <= 0.012f)
            return 0.0005f;
        if (range <= 0.1f)
            return 0.0005f;
        if (range <= 1.5f)
            return 0.01f;
        if (range <= 8.0f)
            return 0.05f;
        if (range <= 32.0f)
            return 0.1f;

        return 1.0f;
    }

    private static int DecimalPlacesForStep(float step)
    {
        for (int decimals = 0; decimals <= 6; decimals++)
        {
            double scaled = step * Math.Pow(10.0, decimals);
            if (Math.Abs(scaled - Math.Round(scaled)) <= 0.000001)
                return decimals;
        }

        return 6;
    }

    // Maps the persisted render_mode plus the edges toggle to the effective
    // integer mode the renderer should draw. Only the two shaded variants are
    // edge-sensitive; Wireframe/Clay/Realistic pass through unchanged. Pure so
    // it can be unit-tested without SharedPreferences (S9-F10).
    public static int EffectiveRenderMode(int renderMode, bool edgesEnabled)
        => IsShadedRenderMode(renderMode)
            ? edgesEnabled ? ModeShadedWithEdges : ModeShaded
            : renderMode;

    public static bool IsShadedRenderMode(int renderMode)
        => renderMode == ModeShadedWithEdges || renderMode == ModeShaded;

    public static int ClampAndroidMsaaSamples(int value)
    {
        if (value <= 0)
            return MinAndroidMsaaSamples;

        if (value <= 2)
            return 2;
        if (value <= 4)
            return 4;
        return MaxAndroidMsaaSamples;
    }
}
