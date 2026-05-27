using System.Buffers.Binary;
using System.Text.Json;

namespace FabricationAssistant.App.Android;

internal static class ImportFileSignatureValidator
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint GlbVersion = 2;
    private const uint GlbJsonChunkType = 0x4E4F534A; // "JSON"
    private const int GlbHeaderBytes = 12;
    private const int GlbChunkHeaderBytes = 8;
    private const int GlbMinimumBytes = GlbHeaderBytes + GlbChunkHeaderBytes;

    public static async Task ValidateGlbAsync(string localPath, CancellationToken ct)
    {
        long fileLength = new FileInfo(localPath).Length;
        if (fileLength < GlbMinimumBytes)
            throw new InvalidDataException("The selected .glb file is too short.");

        await using var input = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        byte[] header = new byte[GlbHeaderBytes];
        await ReadExactlyAsync(input, header, ct).ConfigureAwait(false);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        if (magic != GlbMagic || version != GlbVersion || declaredLength != fileLength)
            throw new InvalidDataException("The selected .glb file has an invalid header.");

        long offset = GlbHeaderBytes;
        int chunkIndex = 0;
        while (offset < fileLength)
        {
            if (fileLength - offset < GlbChunkHeaderBytes)
                throw new InvalidDataException("The selected .glb file has a truncated chunk header.");

            byte[] chunkHeader = new byte[GlbChunkHeaderBytes];
            await ReadExactlyAsync(input, chunkHeader, ct).ConfigureAwait(false);
            offset += GlbChunkHeaderBytes;

            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(0, 4));
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));

            if (chunkIndex == 0 && chunkType != GlbJsonChunkType)
                throw new InvalidDataException("The selected .glb file is missing the required first JSON chunk.");
            if (chunkIndex == 0 && chunkLength == 0)
                throw new InvalidDataException("The selected .glb file has an empty JSON chunk.");
            if ((chunkLength & 3) != 0)
                throw new InvalidDataException("The selected .glb file has an unaligned chunk.");
            if (chunkLength > fileLength - offset)
                throw new InvalidDataException("The selected .glb file has a chunk that exceeds file bounds.");

            input.Seek(chunkLength, SeekOrigin.Current);
            offset += chunkLength;
            chunkIndex++;
        }

        if (chunkIndex == 0)
            throw new InvalidDataException("The selected .glb file has no chunks.");
    }

    public static async Task ValidateGltfJsonAsync(string localPath, CancellationToken ct)
    {
        try
        {
            await using var input = File.OpenRead(localPath);
            using JsonDocument document = await JsonDocument.ParseAsync(input, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("asset", out JsonElement asset)
                || asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.String
                || version.GetString() != "2.0")
            {
                throw new InvalidDataException("The selected .gltf file is missing a valid glTF 2.0 asset object.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The selected .gltf file is not valid JSON.", ex);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("The selected model file ended unexpectedly.");

            total += read;
        }
    }
}
