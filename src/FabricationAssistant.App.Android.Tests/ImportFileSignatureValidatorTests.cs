using System.Buffers.Binary;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class ImportFileSignatureValidatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "fa-import-validator-tests-" + Guid.NewGuid().ToString("N"));

    public ImportFileSignatureValidatorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ValidateGlbAsync_accepts_minimal_valid_glb()
    {
        string path = Path.Combine(_tempDir, "valid.glb");
        WriteGlb(path, "{\"asset\":{\"version\":\"2.0\"}}");

        await ImportFileSignatureValidator.ValidateGlbAsync(path, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateGlbAsync_rejects_non_json_first_chunk()
    {
        string path = Path.Combine(_tempDir, "bad-first-chunk.glb");
        WriteGlb(path, "{\"asset\":{\"version\":\"2.0\"}}", firstChunkType: 0x004E4942);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImportFileSignatureValidator.ValidateGlbAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateGlbAsync_rejects_chunk_length_past_file_end()
    {
        string path = Path.Combine(_tempDir, "bad-length.glb");
        byte[] bytes = CreateGlb("{\"asset\":{\"version\":\"2.0\"}}");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)bytes.Length);
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImportFileSignatureValidator.ValidateGlbAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateGlbAsync_rejects_unaligned_chunk_length()
    {
        string path = Path.Combine(_tempDir, "bad-alignment.glb");
        byte[] bytes = CreateGlb("{\"asset\":{\"version\":\"2.0\"}}");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 5u);
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImportFileSignatureValidator.ValidateGlbAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateGltfJsonAsync_requires_gltf20_asset_version()
    {
        string path = Path.Combine(_tempDir, "missing-version.gltf");
        await File.WriteAllTextAsync(path, "{\"asset\":{}}");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ImportFileSignatureValidator.ValidateGltfJsonAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateGltfJsonAsync_accepts_gltf20_asset_version()
    {
        string path = Path.Combine(_tempDir, "valid.gltf");
        await File.WriteAllTextAsync(path, "{\"asset\":{\"version\":\"2.0\"}}");

        await ImportFileSignatureValidator.ValidateGltfJsonAsync(path, CancellationToken.None);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void WriteGlb(string path, string json, uint firstChunkType = 0x4E4F534A)
        => File.WriteAllBytes(path, CreateGlb(json, firstChunkType));

    private static byte[] CreateGlb(string json, uint firstChunkType = 0x4E4F534A)
    {
        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        int paddedJsonLength = Align4(jsonBytes.Length);
        int totalLength = 12 + 8 + paddedJsonLength;
        byte[] data = new byte[totalLength];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), firstChunkType);
        jsonBytes.CopyTo(data.AsSpan(20));

        for (int i = 20 + jsonBytes.Length; i < data.Length; i++)
            data[i] = (byte)' ';

        return data;
    }

    private static int Align4(int value)
        => (value + 3) & ~3;
}
