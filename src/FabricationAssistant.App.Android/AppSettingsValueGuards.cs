namespace FabricationAssistant.App.Android;

internal static class AppSettingsValueGuards
{
    public static float Clamp01(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

    public static int ClampAndroidMsaaSamples(int value)
    {
        if (value <= 0)
            return 0;

        return value <= 2 ? 2 : 4;
    }
}
