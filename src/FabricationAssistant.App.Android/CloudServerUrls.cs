namespace FabricationAssistant.App.Android;

internal static class CloudServerUrls
{
    public static string NormalizeConfiguredUrl(string? serverUrl)
        => (serverUrl ?? "").Trim().TrimEnd('/');

    public static Uri BuildCloudUri(string serverUrl, string path)
    {
        string normalizedServer = NormalizeConfiguredUrl(serverUrl);
        if (!Uri.TryCreate(normalizedServer, UriKind.Absolute, out Uri? serverUri)
            || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new CloudApiException("Cloud server must be an https:// URL.");
        }

        var baseUri = new Uri(normalizedServer.TrimEnd('/') + "/");
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absolute))
        {
            if (absolute.Scheme == Uri.UriSchemeHttps
                && string.Equals(absolute.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase)
                && absolute.Port == serverUri.Port)
            {
                return absolute;
            }

            return new Uri(baseUri, absolute.PathAndQuery.TrimStart('/'));
        }

        return new Uri(baseUri, path.TrimStart('/'));
    }
}
