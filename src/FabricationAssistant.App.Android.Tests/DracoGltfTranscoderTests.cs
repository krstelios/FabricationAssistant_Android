using System.Buffers.Binary;
using System.Text;
using FabricationAssistant.Draco.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class DracoGltfTranscoderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "fa-draco-tests-" + Guid.NewGuid().ToString("N"));

    public DracoGltfTranscoderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Transcode_WithoutDracoMeshes_CopiesSourceGlb()
    {
        string source = Path.Combine(_tempDir, "source.glb");
        string destination = Path.Combine(_tempDir, "destination.glb");
        WriteGlb(
            source,
            """{"asset":{"version":"2.0"},"buffers":[{"byteLength":4}]}""",
            [1, 2, 3, 4]);

        DracoGltfTranscoder.Transcode(source, destination);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
    }

    [Fact]
    public void Transcode_RejectsDracoBufferViewOutsideBinChunk()
    {
        string source = Path.Combine(_tempDir, "bad-range.glb");
        string destination = Path.Combine(_tempDir, "destination.glb");
        WriteGlb(
            source,
            """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],"bufferViews":[{"buffer":0,"byteOffset":2,"byteLength":8}],"meshes":[{"primitives":[{"attributes":{},"extensions":{"KHR_draco_mesh_compression":{"bufferView":0,"attributes":{"POSITION":0}}}}]}]}
            """,
            [1, 2, 3, 4]);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DracoGltfTranscoder.Transcode(source, destination));
        Assert.Contains("range exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, 5120)]
    [InlineData(2, 5121)]
    [InlineData(3, 5122)]
    [InlineData(4, 5123)]
    [InlineData(6, 5125)]
    [InlineData(9, 5126)]
    public void GetGltfComponentTypeForDracoDataType_maps_supported_types(int dracoType, int expected)
        => Assert.Equal(expected, DracoGltfTranscoder.GetGltfComponentTypeForDracoDataType(dracoType));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    public void GetGltfComponentTypeForDracoDataType_rejects_non_gltf_types(int dracoType)
        => Assert.Throws<InvalidDataException>(() => DracoGltfTranscoder.GetGltfComponentTypeForDracoDataType(dracoType));

    [Theory]
    [InlineData(1, "SCALAR")]
    [InlineData(2, "VEC2")]
    [InlineData(3, "VEC3")]
    [InlineData(4, "VEC4")]
    public void GetGltfAccessorType_maps_supported_component_counts(int componentCount, string expected)
        => Assert.Equal(expected, DracoGltfTranscoder.GetGltfAccessorType(componentCount));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void GetGltfAccessorType_rejects_non_gltf_component_counts(int componentCount)
        => Assert.Throws<InvalidDataException>(() => DracoGltfTranscoder.GetGltfAccessorType(componentCount));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private static void WriteGlb(string path, string json, byte[] bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int binPad = (4 - bin.Length % 4) % 4;
        int jsonLength = jsonBytes.Length + jsonPad;
        int binLength = bin.Length + binPad;
        int totalLength = 12 + 8 + jsonLength + 8 + binLength;

        using FileStream output = File.Create(path);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)totalLength);
        output.Write(header);

        Span<byte> chunkHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader, (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], 0x4E4F534A);
        output.Write(chunkHeader);
        output.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++)
            output.WriteByte(0x20);

        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader, (uint)binLength);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..], 0x004E4942);
        output.Write(chunkHeader);
        output.Write(bin);
        for (int i = 0; i < binPad; i++)
            output.WriteByte(0);
    }
}
