namespace FabricationAssistant.App.Android;

internal static class ImportFileTypeResolver
{
    public static string ResolveFileNameWithMimeFallback(string? displayName, string? mimeType)
    {
        string fileName = string.IsNullOrWhiteSpace(displayName) ? "model.bin" : displayName!;
        string extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension) && !extension.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            return fileName;

        string? normalizedMimeType = string.IsNullOrWhiteSpace(mimeType)
            ? null
            : mimeType.Split(';', 2)[0].Trim().ToLowerInvariant();

        string fallbackExtension = normalizedMimeType switch
        {
            "model/gltf-binary" => ".glb",
            "model/gltf+json" => ".gltf",
            "application/gltf-buffer" => ".bin",
            "application/zip" => ".fa",
            "application/x-zip-compressed" => ".fa",
            _ => extension,
        };

        if (string.IsNullOrWhiteSpace(fallbackExtension))
            fallbackExtension = ".bin";

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "model";

        return stem + fallbackExtension;
    }
}
