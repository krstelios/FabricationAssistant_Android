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
    private const int ImportCacheKeepCount = 10;
    private static readonly TimeSpan StaleTempFileAge = TimeSpan.FromHours(1);

    private readonly Context _context;
    private readonly IPlatformPaths _paths;

    public ImportPipeline(Context context, IPlatformPaths paths)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Task<DocumentDto> ImportAsync(AndroidUri contentUri, CancellationToken ct)
        => ImportAsync(contentUri, progress: null, ct);

    public async Task<DocumentDto> ImportAsync(
        AndroidUri contentUri,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contentUri);

        string fileName = ResolveFileName(contentUri) ?? "model.bin";
        string localPath = await CopyToLocalAsync(contentUri, fileName, progress, ct).ConfigureAwait(false);
        string ext = Path.GetExtension(localPath).ToLowerInvariant();
        if (ext == ".gltf")
        {
            progress?.Report("Normalizing glTF text...");
            await StripUtf8BomInPlaceIfPresentAsync(localPath, ct).ConfigureAwait(false);
        }
        await ValidateLocalFileSignatureAsync(localPath, ext, ct).ConfigureAwait(false);

        var settings = new TessellationSettings();

        string dracoTempDir = Path.Combine(_paths.TempDir, "draco-decode");
        var gltf = new GltfImportService();
        ISceneImportService gltfWithDraco = new DracoDecodingGltfImportService(gltf, dracoTempDir);

        if (ext is ".gltf" or ".glb")
            return await gltfWithDraco.ImportAsync(localPath, settings, progress, ct).ConfigureAwait(false);

        if (ext == ".fa")
        {
            var fa = new FaImportService(gltfWithDraco);
            return await fa.ImportAsync(localPath, settings, progress, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException(
            $"Unsupported file type: {ext}. Supported: .gltf, .glb, .fa");
    }

    private async Task<string> CopyToLocalAsync(
        AndroidUri uri,
        string fileName,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        string importCacheRoot = Path.Combine(_paths.AppDataRoot, "import-cache");
        Directory.CreateDirectory(importCacheRoot);
        string localPath = Path.Combine(importCacheRoot, CreateCacheFileName(fileName));
        string tempPath = localPath + ".part";

        progress?.Report("Copying file...");

        try
        {
            using var input = _context.ContentResolver?.OpenInputStream(uri)
                ?? throw new InvalidOperationException("ContentResolver.OpenInputStream returned null for " + uri);

            await using var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            await input.CopyToAsync(output, 128 * 1024, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            File.Move(tempPath, localPath, overwrite: true);
            progress?.Report("File copied");
            TryPruneImportCache(importCacheRoot, ImportCacheKeepCount);
            return localPath;
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static async Task StripUtf8BomInPlaceIfPresentAsync(string localPath, CancellationToken ct)
    {
        byte[] prefix = new byte[3];
        int read;
        await using (var input = File.OpenRead(localPath))
        {
            read = await input.ReadAsync(prefix.AsMemory(0, prefix.Length), ct).ConfigureAwait(false);
            if (read < 3 || prefix[0] != 0xEF || prefix[1] != 0xBB || prefix[2] != 0xBF)
                return;
        }

        string tempPath = localPath + ".nobom";
        try
        {
            await using (var input = File.OpenRead(localPath))
            await using (var output = File.Create(tempPath))
            {
                input.Position = 3;
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, localPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
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

    private static string CreateCacheFileName(string fileName)
    {
        string safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "model.bin";

        string extension = Path.GetExtension(safeName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";

        string stem = Path.GetFileNameWithoutExtension(safeName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "model";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            stem = stem.Replace(invalid, '_');

        if (stem.Length > 80)
            stem = stem[..80];

        return $"{stem}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}{extension}";
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static async Task ValidateLocalFileSignatureAsync(string localPath, string extension, CancellationToken ct)
    {
        byte[] buffer = new byte[256];
        int read;
        await using (var input = File.OpenRead(localPath))
            read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);

        if (read == 0)
            throw new InvalidDataException("The selected file is empty.");

        bool valid = extension switch
        {
            ".glb" => read >= 4 && buffer[0] == (byte)'g' && buffer[1] == (byte)'l' && buffer[2] == (byte)'T' && buffer[3] == (byte)'F',
            ".gltf" => FirstNonWhitespace(buffer, read) is (byte)'{',
            ".fa" => read >= 2 && buffer[0] == (byte)'P' && buffer[1] == (byte)'K',
            _ => true,
        };

        if (!valid)
            throw new InvalidDataException($"The selected {extension} file does not look like a valid supported model.");
    }

    private static byte FirstNonWhitespace(byte[] buffer, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte value = buffer[i];
            if (value != (byte)' ' && value != (byte)'\t' && value != (byte)'\r' && value != (byte)'\n')
                return value;
        }

        return 0;
    }

    private static void TryPruneImportCache(string importCacheRoot, int keep)
    {
        try
        {
            var root = new DirectoryInfo(importCacheRoot);
            if (!root.Exists)
                return;

            DateTime cutoffUtc = DateTime.UtcNow - StaleTempFileAge;
            FileInfo[] files = root.GetFiles();

            foreach (FileInfo temp in files.Where(IsTemporaryCacheFile))
            {
                if (temp.LastWriteTimeUtc < cutoffUtc)
                    TryDeleteFile(temp.FullName);
            }

            foreach (FileInfo file in files
                         .Where(f => !IsTemporaryCacheFile(f))
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(Math.Max(0, keep)))
            {
                TryDeleteFile(file.FullName);
            }
        }
        catch
        {
            // Cache pruning must never make an otherwise valid import fail.
        }
    }

    private static bool IsTemporaryCacheFile(FileInfo file)
        => file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
           || file.Name.EndsWith(".nobom", StringComparison.OrdinalIgnoreCase);
}
