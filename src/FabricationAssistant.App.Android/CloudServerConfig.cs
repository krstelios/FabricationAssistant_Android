using Android.Content;

namespace FabricationAssistant.App.Android;

internal enum CloudServerProfile
{
    Internet,
    Local,
}

internal static class CloudServerConfig
{
    public const string FileName = "fa_cloud.ini";

    private const string CloudSection = "cloud";
    private const string ProfileKey = "profile";
    private const string InternetUrlKey = "internet_url";
    private const string LocalUrlKey = "local_url";
    private const string ServerUrlKey = "server_url";
    private static readonly object Sync = new();
    private static CloudServerProfile _profile = CloudServerProfile.Internet;
    private static string _serverUrl = "";
    private static string _internetUrl = "";
    private static string _localUrl = "";
    private static string _configPath = FileName;

    public static CloudServerProfile Profile => _profile;

    public static string ServerUrl => _serverUrl;

    public static string ConfigPath => _configPath;

    public static int ProfileSelectionIndex => _profile == CloudServerProfile.Local ? 0 : 1;

    public static string ProfileDisplayName => _profile == CloudServerProfile.Local ? "Local network" : "Internet";

    public static string SelectedUrlKey => _profile == CloudServerProfile.Local ? LocalUrlKey : InternetUrlKey;

    public static void Initialize(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context appContext = context.ApplicationContext ?? context;

        lock (Sync)
        {
            string path = ResolveConfigPath(appContext);
            EnsureConfigFile(path);
            _configPath = path;
            ApplySnapshot(ReadConfig(File.ReadAllText(path), path));
        }
    }

    public static string ReadServerUrl(string iniText)
        => ReadConfig(iniText, FileName).ServerUrl;

    public static void SetProfileSelectionIndex(int index)
        => SetProfile(index == 0 ? CloudServerProfile.Local : CloudServerProfile.Internet);

    public static void SetProfile(CloudServerProfile profile)
    {
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(_configPath))
                throw new InvalidOperationException("Cloud server config has not been initialized.");

            var snapshot = File.Exists(_configPath)
                ? ReadConfig(File.ReadAllText(_configPath), _configPath)
                : new CloudServerConfigSnapshot(CloudServerProfile.Internet, "", "", "", _configPath);

            string serverUrl = ResolveServerUrl(profile, snapshot.InternetUrl, snapshot.LocalUrl, snapshot.ServerUrl);
            var next = snapshot with
            {
                Profile = profile,
                ServerUrl = serverUrl,
            };
            WriteConfig(next);
            ApplySnapshot(next);
        }
    }

    private static CloudServerConfigSnapshot ReadConfig(string iniText, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        using var reader = new StringReader(iniText ?? "");
        while (reader.ReadLine() is { } rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            if (section.Length > 0 && !section.Equals(CloudSection, StringComparison.OrdinalIgnoreCase))
                continue;

            string key = line[..separator].Trim();
            values[key] = Unquote(line[(separator + 1)..].Trim());
        }

        string internetUrl = Normalize(values.GetValueOrDefault(InternetUrlKey));
        string localUrl = Normalize(values.GetValueOrDefault(LocalUrlKey));
        string legacyServerUrl = Normalize(values.GetValueOrDefault(ServerUrlKey));
        CloudServerProfile profile = ParseProfile(values.GetValueOrDefault(ProfileKey), legacyServerUrl);

        if (string.IsNullOrWhiteSpace(internetUrl) && profile == CloudServerProfile.Internet)
            internetUrl = legacyServerUrl;
        if (string.IsNullOrWhiteSpace(localUrl) && profile == CloudServerProfile.Local)
            localUrl = legacyServerUrl;

        string serverUrl = ResolveServerUrl(profile, internetUrl, localUrl, legacyServerUrl);
        return new CloudServerConfigSnapshot(profile, serverUrl, internetUrl, localUrl, path);
    }

    private static string ResolveConfigPath(Context context)
    {
        foreach (string? directory in EnumerateConfigDirectories(context))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            Directory.CreateDirectory(directory);
            return Path.Combine(directory, FileName);
        }

        throw new InvalidOperationException("No Android app files directory is available for FA Cloud configuration.");
    }

    private static IEnumerable<string?> EnumerateConfigDirectories(Context context)
    {
        yield return context.GetExternalFilesDir(null)?.AbsolutePath;
        yield return context.FilesDir?.AbsolutePath;
    }

    private static void EnsureConfigFile(string path)
    {
        if (File.Exists(path))
            return;

        WriteConfig(new CloudServerConfigSnapshot(CloudServerProfile.Internet, "", "", "", path));
    }

    private static void WriteConfig(CloudServerConfigSnapshot snapshot)
    {
        File.WriteAllText(snapshot.Path,
            "# Fabrication Assistant cloud configuration" + Environment.NewLine +
            "# This file is read when the Android app starts." + Environment.NewLine +
            "# Select profile from Android Settings > FA Cloud." + Environment.NewLine +
            "[cloud]" + Environment.NewLine +
            "profile=" + FormatProfile(snapshot.Profile) + Environment.NewLine +
            "internet_url=" + snapshot.InternetUrl + Environment.NewLine +
            "local_url=" + snapshot.LocalUrl + Environment.NewLine +
            "server_url=" + snapshot.ServerUrl + Environment.NewLine);
    }

    private static void ApplySnapshot(CloudServerConfigSnapshot snapshot)
    {
        _profile = snapshot.Profile;
        _serverUrl = snapshot.ServerUrl;
        _internetUrl = snapshot.InternetUrl;
        _localUrl = snapshot.LocalUrl;
        _configPath = snapshot.Path;
    }

    private static string ResolveServerUrl(
        CloudServerProfile profile,
        string internetUrl,
        string localUrl,
        string fallbackUrl)
    {
        string profileUrl = profile == CloudServerProfile.Local ? localUrl : internetUrl;
        if (!string.IsNullOrWhiteSpace(profileUrl))
            return profileUrl;

        return IsFallbackForProfile(profile, fallbackUrl) ? fallbackUrl : "";
    }

    private static bool IsFallbackForProfile(CloudServerProfile profile, string fallbackUrl)
        => profile == CloudServerProfile.Local
            ? fallbackUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            : fallbackUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static CloudServerProfile ParseProfile(string? value, string fallbackUrl)
    {
        if (string.Equals(value, "local", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "local_network", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "local network", StringComparison.OrdinalIgnoreCase))
        {
            return CloudServerProfile.Local;
        }

        if (string.Equals(value, "internet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "public", StringComparison.OrdinalIgnoreCase))
        {
            return CloudServerProfile.Internet;
        }

        return fallbackUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? CloudServerProfile.Local
            : CloudServerProfile.Internet;
    }

    private static string FormatProfile(CloudServerProfile profile)
        => profile == CloudServerProfile.Local ? "local" : "internet";

    private static string Normalize(string? value)
        => CloudServerUrls.NormalizeConfiguredUrl(value);

    private sealed record CloudServerConfigSnapshot(
        CloudServerProfile Profile,
        string ServerUrl,
        string InternetUrl,
        string LocalUrl,
        string Path);

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }
}
