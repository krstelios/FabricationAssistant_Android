namespace FabricationAssistant.App.Android;

internal static class AppSettingsValueGuards
{
    public const int MinAndroidMsaaSamples = 0;
    public const int MaxAndroidMsaaSamples = 8;

    public static float Clamp01(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

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
