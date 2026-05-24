using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Services;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// ISceneImportService decorator. Intercepts Draco-encoded files, transcodes
/// them to a non-Draco GLB via in-process libdraco, then delegates to the
/// inner (unmodified) GltfImportService. Files without Draco are passed through.
/// </summary>
public sealed class DracoDecodingGltfImportService : ISceneImportService
{
    private readonly ISceneImportService _inner;
    private readonly string _tempDir;

    public DracoDecodingGltfImportService(ISceneImportService inner, string tempDir)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tempDir = tempDir ?? throw new ArgumentNullException(nameof(tempDir));
        Directory.CreateDirectory(_tempDir);
    }

    public async Task<DocumentDto> ImportAsync(
        string filePath,
        TessellationSettings settings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        bool hasDraco = await Task.Run(() => DracoExtensionDetector.ContainsDraco(filePath), ct).ConfigureAwait(false);
        if (!hasDraco)
            return await _inner.ImportAsync(filePath, settings, progress, ct).ConfigureAwait(false);

        progress?.Report("Decoding Draco compression...");
        string decoded = Path.Combine(_tempDir, $"{Path.GetFileNameWithoutExtension(filePath)}-{Guid.NewGuid():N}.glb");
        try
        {
            await Task.Run(() => DracoGltfTranscoder.Transcode(filePath, decoded), ct).ConfigureAwait(false);
            return await _inner.ImportAsync(decoded, settings, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(decoded); } catch { /* best-effort cleanup */ }
        }
    }
}
