namespace FabricationAssistant.App.Android;

internal static class CloudServerUrls
{
    public const string LocalLanHost = "10.0.1.159";
    public const string InternetHost = "195.97.118.165";
    public const string LocalLanUrl = "http://" + LocalLanHost;
    public const string InternetUrl = "https://" + InternetHost;

    public static string NormalizeKnownProfileUrl(string? serverUrl)
    {
        string value = (serverUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return value;

        if (IsRootServerUri(uri, LocalLanHost, Uri.UriSchemeHttps))
            return LocalLanUrl;

        if (IsRootServerUri(uri, InternetHost, Uri.UriSchemeHttp))
            return InternetUrl;

        return value;
    }

    private static bool IsRootServerUri(Uri uri, string host, string scheme)
        => uri.IsDefaultPort
           && string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
           && string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath == "/"
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);
}
