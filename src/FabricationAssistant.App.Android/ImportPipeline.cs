using System.Buffers;
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
    private const long MaxImportBytes = 2L * 1024L * 1024L * 1024L;
    private const int CopyBufferBytes = 64 * 1024;
    private static readonly TimeSpan StaleTempFileAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(15);

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

        string fileName = ResolveFileNameWithMimeFallback(contentUri, ResolveFileName(contentUri));
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

    public void PruneImportCache()
        => PruneImportCache(_paths.AppDataRoot);

    public static void PruneImportCache(string appDataRoot)
    {
        if (string.IsNullOrWhiteSpace(appDataRoot))
            return;

        TryPruneImportCache(Path.Combine(appDataRoot, "import-cache"), ImportCacheKeepCount);
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
        long? declaredSize = TryResolveContentSize(uri);
        if (declaredSize is > MaxImportBytes)
            throw new InvalidDataException($"The selected file is too large ({declaredSize.Value:n0} bytes). Maximum supported import size is {MaxImportBytes:n0} bytes.");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CopyTimeout);
            await using var input = _context.ContentResolver?.OpenInputStream(uri)
                ?? throw new InvalidOperationException("ContentResolver.OpenInputStream returned null for " + uri);

            await using var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: CopyBufferBytes,
                useAsync: true);
            await CopyToAsyncWithLimit(input, output, MaxImportBytes, timeoutCts.Token).ConfigureAwait(false);
            await output.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            timeoutCts.Token.ThrowIfCancellationRequested();

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
        bool moved = false;
        try
        {
            await using (var input = File.OpenRead(localPath))
            await using (var output = File.Create(tempPath))
            {
                input.Position = 3;
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, localPath, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved)
                TryDeleteFile(tempPath);
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

    private string ResolveFileNameWithMimeFallback(AndroidUri uri, string? displayName)
        => ImportFileTypeResolver.ResolveFileNameWithMimeFallback(
            string.IsNullOrWhiteSpace(displayName) ? uri.LastPathSegment : displayName,
            _context.ContentResolver?.GetType(uri));

    private long? TryResolveContentSize(AndroidUri uri)
    {
        try
        {
            using var cursor = _context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst())
                return null;

            int sizeIndex = cursor.GetColumnIndex(IOpenableColumns.Size);
            if (sizeIndex < 0 || cursor.IsNull(sizeIndex))
                return null;

            long size = cursor.GetLong(sizeIndex);
            return size >= 0 ? size : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task CopyToAsyncWithLimit(
        Stream input,
        Stream output,
        long maxBytes,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), ct).ConfigureAwait(false);
                if (read == 0)
                    return;

                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException($"The selected file exceeds the maximum supported import size of {maxBytes:n0} bytes.");

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static async Task ValidateLocalFileSignatureAsync(string localPath, string extension, CancellationToken ct)
    {
        long fileLength = new FileInfo(localPath).Length;
        if (fileLength == 0)
            throw new InvalidDataException("The selected file is empty.");

        switch (extension)
        {
            case ".glb":
                await ImportFileSignatureValidator.ValidateGlbAsync(localPath, ct).ConfigureAwait(false);
                break;
            case ".gltf":
                await ImportFileSignatureValidator.ValidateGltfJsonAsync(localPath, ct).ConfigureAwait(false);
                break;
            case ".fa":
                byte[] buffer = new byte[2];
                int read;
                await using (var input = File.OpenRead(localPath))
                    read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read < 2 || buffer[0] != (byte)'P' || buffer[1] != (byte)'K')
                    throw new InvalidDataException($"The selected {extension} file does not look like a valid supported model.");
                await ValidateFaArchiveAsync(localPath, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task ValidateFaArchiveAsync(string localPath, CancellationToken ct)
    {
        try
        {
            using var archive = FaArchive.Open(localPath);
            string manifestJson = await archive.ReadEntryAsTextAsync(FaArchiveManifest.ManifestEntryName, ct).ConfigureAwait(false);
            FaArchiveManifest manifest = FaArchiveManifest.Parse(manifestJson);
            manifest.Validate();
            _ = archive.RequireEntry(manifest.GeometryEntryName);
            _ = archive.RequireEntry(manifest.ComponentsEntryName);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("The selected .fa file is not a valid Fabrication Assistant archive.", ex);
        }
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
