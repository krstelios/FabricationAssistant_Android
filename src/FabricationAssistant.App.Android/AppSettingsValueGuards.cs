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
