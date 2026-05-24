using Android.Content;
using Android.Provider;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Services;
using FabricationAssistant.Draco.Android;
using FabricationAssistant.Import.Fa;
using FabricationAssistant.Import.Gltf;
using FabricationAssistant.Platform;
using AndroidUri = Android.Net.Uri;

namespace FabricationAssistant.App.Android;

/// <summary>
/// SAF content URI -> local cached file -> Draco-aware glTF or FA importer
/// -> DocumentDto. Routes by file extension: .gltf/.glb through the
/// Draco-decoding decorator over GltfImportService; .fa through FaImportService
/// using the same decorated glTF service so packages with Draco-encoded inner
/// GLBs work identically.
/// </summary>
public sealed class ImportPipeline
{
    private readonly Context _context;
    private readonly IPlatformPaths _paths;

    public ImportPipeline(Context context, IPlatformPaths paths)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<DocumentDto> ImportAsync(AndroidUri contentUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contentUri);

        string fileName = ResolveFileName(contentUri) ?? "model.bin";
        string localPath = await CopyToLocalAsync(contentUri, fileName, ct).ConfigureAwait(false);
        string ext = Path.GetExtension(localPath).ToLowerInvariant();
        var settings = new TessellationSettings();

        string dracoTempDir = Path.Combine(_paths.TempDir, "draco-decode");
        var gltf = new GltfImportService();
        ISceneImportService gltfWithDraco = new DracoDecodingGltfImportService(gltf, dracoTempDir);

        if (ext is ".gltf" or ".glb")
            return await gltfWithDraco.ImportAsync(localPath, settings, progress: null, ct).ConfigureAwait(false);

        if (ext == ".fa")
        {
            var fa = new FaImportService(gltfWithDraco);
            return await fa.ImportAsync(localPath, settings, progress: null, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException(
            $"Unsupported file type: {ext}. Supported: .gltf, .glb, .fa");
    }

    private async Task<string> CopyToLocalAsync(AndroidUri uri, string fileName, CancellationToken ct)
    {
        string importCacheRoot = Path.Combine(_paths.AppDataRoot, "import-cache");
        Directory.CreateDirectory(importCacheRoot);
        string localPath = Path.Combine(importCacheRoot, fileName);

        using var input = _context.ContentResolver?.OpenInputStream(uri)
            ?? throw new InvalidOperationException("ContentResolver.OpenInputStream returned null for " + uri);

        await using var output = File.Create(localPath);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return localPath;
    }

    private string? ResolveFileName(AndroidUri uri)
    {
        try
        {
            using var cursor = _context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst()) return null;
            int idx = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
            if (idx < 0) return null;
            return cursor.GetString(idx);
        }
        catch
        {
            return null;
        }
    }
}
