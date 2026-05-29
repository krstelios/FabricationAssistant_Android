namespace FabricationAssistant.App.Android;

internal static class CloudServerUrls
{
    public static string NormalizeConfiguredUrl(string? serverUrl)
        => (serverUrl ?? "").Trim().TrimEnd('/');
}
