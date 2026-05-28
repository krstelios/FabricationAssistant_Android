namespace FabricationAssistant.App.Android;

internal static class ImportCacheFileName
{
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    };

    public static string Create(string fileName)
        => Create(fileName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Guid.NewGuid());

    internal static string Create(string fileName, long unixTimeMs, Guid id)
    {
        string safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "model.bin";

        string extension = Path.GetExtension(safeName);
        if (string.IsNullOrWhiteSpace(extension) || extension == ".")
            extension = ".bin";

        string stem = Path.GetFileNameWithoutExtension(safeName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "model";

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
            extension = extension.Replace(invalid, '_');
        }

        stem = stem.Trim();
        if (stem.Length == 0)
            stem = "model";

        string windowsStem = stem.TrimEnd('.', ' ');
        if (WindowsReservedDeviceNames.Contains(windowsStem))
            stem = "model_" + stem;

        if (stem.Length > 80)
            stem = stem[..80];

        return $"{stem}-{unixTimeMs:x}-{id:N}{extension}";
    }
}
