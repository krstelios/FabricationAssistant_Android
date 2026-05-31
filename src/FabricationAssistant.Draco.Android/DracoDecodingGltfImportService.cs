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
    private static readonly TimeSpan StaleDecodedFileAge = TimeSpan.FromHours(1);

    private readonly ISceneImportService _inner;
    private readonly string _tempDir;

    public DracoDecodingGltfImportService(ISceneImportService inner, string tempDir)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tempDir = tempDir ?? throw new ArgumentNullException(nameof(tempDir));
        Directory.CreateDirectory(_tempDir);
        TryPruneDecodedTempFiles(_tempDir);
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

        // S5-3: the transcoder only handles binary GLB. A Draco-compressed text .gltf
        // (now detected above) cannot be transcoded in-process, so fail with a clear
        // message instead of letting the plain importer choke on the Draco accessors.
        if (!DracoExtensionDetector.IsBinaryGlb(filePath))
            throw new NotSupportedException(
                "Draco-compressed text .gltf is not supported. Re-export the model as a binary .glb.");

        progress?.Report("Decoding Draco compression...");
        string decoded = Path.Combine(_tempDir, $"{Path.GetFileNameWithoutExtension(filePath)}-{Guid.NewGuid():N}.glb");
        try
        {
            try
            {
                await Task.Run(
                    () => DracoGltfTranscoder.Transcode(
                        filePath,
                        decoded,
                        warning => global::Android.Util.Log.Warn("FA.Draco", warning)),
                    ct).ConfigureAwait(false);
            }
            catch (DllNotFoundException ex)
            {
                global::Android.Util.Log.Warn("FA.Draco", "Native Draco library not found: " + ex);
                throw new NotSupportedException("Draco compression is not supported on this device.", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                global::Android.Util.Log.Warn("FA.Draco", "Native Draco entry point not found: " + ex);
                throw new NotSupportedException("Draco compression is not supported on this device.", ex);
            }
            catch (BadImageFormatException ex)
            {
                global::Android.Util.Log.Warn("FA.Draco", "Native Draco library ABI mismatch: " + ex);
                throw new NotSupportedException("Draco compression is not supported on this device.", ex);
            }

            return await _inner.ImportAsync(decoded, settings, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(decoded); } catch { /* best-effort cleanup */ }
        }
    }

    private static void TryPruneDecodedTempFiles(string tempDir)
    {
        try
        {
            var root = new DirectoryInfo(tempDir);
            if (!root.Exists)
                return;

            DateTime cutoffUtc = DateTime.UtcNow - StaleDecodedFileAge;
            // S5-L4: prune every stale entry, not just *.glb. DracoGltfTranscoder writes
            // a <dest>.<guid>.bin intermediate here and a hard kill skips its finally, so
            // those .bin files orphan otherwise. The directory is dedicated to Draco
            // decode temps, so removing all stale files is safe.
            foreach (FileInfo file in root.EnumerateFiles())
            {
                if (file.LastWriteTimeUtc < cutoffUtc)
                    file.Delete();
            }
        }
        catch
        {
            // Temp cleanup must never block model import.
        }
    }
}
