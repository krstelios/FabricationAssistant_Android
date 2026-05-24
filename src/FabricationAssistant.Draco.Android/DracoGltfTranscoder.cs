using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// Transcodes a Draco-compressed GLB into a non-Draco GLB by decoding each
/// Draco primitive in-process and rewriting buffer views + accessors. Writes
/// the result to a caller-supplied destination path.
/// </summary>
public static class DracoGltfTranscoder
{
    private const uint GlbMagic = 0x46546C67;       // "glTF"
    private const uint GlbVersion = 2;
    private const uint ChunkTypeJson = 0x4E4F534A;  // "JSON"
    private const uint ChunkTypeBin  = 0x004E4942;  // "BIN\0"

    public static void Transcode(string sourceGlbPath, string destinationGlbPath)
    {
        ArgumentNullException.ThrowIfNull(sourceGlbPath);
        ArgumentNullException.ThrowIfNull(destinationGlbPath);

        byte[] bytes = File.ReadAllBytes(sourceGlbPath);
        var (json, bin) = ReadGlb(bytes);

        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("glTF JSON parse failed");

        // Preserve the original binary chunk content as the prefix of the new
        // chunk. This keeps every non-Draco bufferView (textures, animations,
        // skins, anything else the file embeds) pointing at valid bytes. We
        // append decoded Draco buffers AFTER this prefix - the existing Draco
        // bufferViews remain in the array but are no longer referenced by any
        // primitive. The overhead is small because Draco-encoded data is much
        // smaller than its decoded form, and the temp transcoded file is
        // deleted as soon as the inner GltfImportService finishes reading it.
        var newBin = new MemoryStream();
        newBin.Write(bin);
        PadTo4Bytes(newBin);

        var bufferViews = root["bufferViews"] as JsonArray ?? new JsonArray();
        var accessors = root["accessors"] as JsonArray ?? new JsonArray();
        var meshes = root["meshes"] as JsonArray;
        if (meshes is null)
        {
            File.WriteAllBytes(destinationGlbPath, bytes);
            return;
        }

        bool changed = false;
        foreach (var meshNode in meshes)
        {
            var prims = meshNode?["primitives"] as JsonArray;
            if (prims is null) continue;
            foreach (var prim in prims)
            {
                var ext = prim?["extensions"]?["KHR_draco_mesh_compression"];
                if (ext is null) continue;

                int viewIndex = ext["bufferView"]!.GetValue<int>();
                var view = bufferViews[viewIndex]!;
                int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
                int length = view["byteLength"]!.GetValue<int>();
                var encoded = new ReadOnlySpan<byte>(bin, offset, length);

                using var dm = DracoMesh.Decode(encoded)
                    ?? throw new InvalidDataException("Draco decode failed for primitive");

                var positions = dm.GetPositions();
                var normals = dm.GetNormals();
                var texCoords = dm.GetTexCoords();
                var indices = dm.GetIndices();

                if (positions is null || indices is null)
                    throw new InvalidDataException("Draco decoded mesh missing required attributes");

                int posView = AppendFloatBufferView(bufferViews, newBin, positions, byteStride: 12);
                int idxView = AppendUInt32BufferView(bufferViews, newBin, indices);
                int posAccessor = AppendVec3FloatAccessor(accessors, posView, dm.NumPoints, positions);
                int idxAccessor = AppendUIntAccessor(accessors, idxView, indices.Length);

                int? nrmAccessor = normals is null ? null
                    : AppendVec3FloatAccessor(accessors,
                        AppendFloatBufferView(bufferViews, newBin, normals, byteStride: 12),
                        dm.NumPoints, normals);
                int? uvAccessor = texCoords is null ? null
                    : AppendVec2FloatAccessor(accessors,
                        AppendFloatBufferView(bufferViews, newBin, texCoords, byteStride: 8),
                        dm.NumPoints);

                var attribs = prim!["attributes"] as JsonObject ?? new JsonObject();
                attribs["POSITION"] = posAccessor;
                if (nrmAccessor.HasValue) attribs["NORMAL"] = nrmAccessor.Value;
                if (uvAccessor.HasValue) attribs["TEXCOORD_0"] = uvAccessor.Value;
                prim["attributes"] = attribs;
                prim["indices"] = idxAccessor;

                ((JsonObject)prim["extensions"]!).Remove("KHR_draco_mesh_compression");
                if (((JsonObject)prim["extensions"]!).Count == 0)
                    ((JsonObject)prim).Remove("extensions");

                changed = true;
            }
        }

        TryRemoveExtensionRef(root, "extensionsUsed", "KHR_draco_mesh_compression");
        TryRemoveExtensionRef(root, "extensionsRequired", "KHR_draco_mesh_compression");

        root["bufferViews"] = bufferViews;
        root["accessors"] = accessors;

        var buffers = root["buffers"] as JsonArray ?? new JsonArray();
        if (buffers.Count == 0) buffers.Add(new JsonObject());
        buffers[0]!["byteLength"] = (int)newBin.Length;
        ((JsonObject)buffers[0]!).Remove("uri");
        root["buffers"] = buffers;

        if (!changed)
        {
            File.WriteAllBytes(destinationGlbPath, bytes);
            return;
        }

        WriteGlb(destinationGlbPath, root.ToJsonString(), newBin.ToArray());
    }

    private static int AppendFloatBufferView(JsonArray bufferViews, MemoryStream bin, float[] data, int byteStride)
    {
        long offset = bin.Position;
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bin.Write(bytes);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = (int)offset,
            ["byteLength"] = bytes.Length,
            ["byteStride"] = byteStride,
        };
        bufferViews.Add(node);
        return bufferViews.Count - 1;
    }

    private static int AppendUInt32BufferView(JsonArray bufferViews, MemoryStream bin, uint[] data)
    {
        long offset = bin.Position;
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bin.Write(bytes);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = (int)offset,
            ["byteLength"] = bytes.Length,
        };
        bufferViews.Add(node);
        return bufferViews.Count - 1;
    }

    private static int AppendVec3FloatAccessor(JsonArray accessors, int bufferView, int count, float[] data)
    {
        var (min, max) = ComputeVec3MinMax(data);
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = 5126,  // FLOAT
            ["count"] = count,
            ["type"] = "VEC3",
            ["min"] = new JsonArray(min[0], min[1], min[2]),
            ["max"] = new JsonArray(max[0], max[1], max[2]),
        };
        accessors.Add(node);
        return accessors.Count - 1;
    }

    private static int AppendVec2FloatAccessor(JsonArray accessors, int bufferView, int count)
    {
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = 5126,  // FLOAT
            ["count"] = count,
            ["type"] = "VEC2",
        };
        accessors.Add(node);
        return accessors.Count - 1;
    }

    private static int AppendUIntAccessor(JsonArray accessors, int bufferView, int count)
    {
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = 5125,  // UNSIGNED_INT
            ["count"] = count,
            ["type"] = "SCALAR",
        };
        accessors.Add(node);
        return accessors.Count - 1;
    }

    private static (float[] min, float[] max) ComputeVec3MinMax(float[] data)
    {
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i + 2 < data.Length; i += 3)
        {
            if (data[i]     < minX) minX = data[i];
            if (data[i + 1] < minY) minY = data[i + 1];
            if (data[i + 2] < minZ) minZ = data[i + 2];
            if (data[i]     > maxX) maxX = data[i];
            if (data[i + 1] > maxY) maxY = data[i + 1];
            if (data[i + 2] > maxZ) maxZ = data[i + 2];
        }
        return (new[] { minX, minY, minZ }, new[] { maxX, maxY, maxZ });
    }

    private static void TryRemoveExtensionRef(JsonNode root, string key, string name)
    {
        if (root[key] is JsonArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
                if (arr[i]?.GetValue<string>() == name)
                    arr.RemoveAt(i);
            if (arr.Count == 0) ((JsonObject)root).Remove(key);
        }
    }

    private static void PadTo4Bytes(MemoryStream s)
    {
        long pad = (4 - (s.Position % 4)) % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    private static (string json, byte[] bin) ReadGlb(byte[] data)
    {
        if (data.Length < 12) throw new InvalidDataException("GLB too short");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0));
        if (magic != GlbMagic) throw new InvalidDataException("Not a GLB file");

        int p = 12;
        string? json = null;
        byte[] bin = Array.Empty<byte>();
        while (p < data.Length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)); p += 4;
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)); p += 4;
            if (chunkType == ChunkTypeJson)
                json = Encoding.UTF8.GetString(data, p, (int)chunkLength).TrimEnd('\0', ' ');
            else if (chunkType == ChunkTypeBin)
            {
                bin = new byte[chunkLength];
                Buffer.BlockCopy(data, p, bin, 0, (int)chunkLength);
            }
            p += (int)chunkLength;
        }
        if (json is null) throw new InvalidDataException("GLB missing JSON chunk");
        return (json, bin);
    }

    private static void WriteGlb(string path, string json, byte[] bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int binPad = (4 - (bin.Length % 4)) % 4;
        int jsonLen = jsonBytes.Length + jsonPad;
        int binLen = bin.Length + binPad;
        int totalLen = 12 + 8 + jsonLen + 8 + binLen;

        using var fs = File.Create(path);
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], (uint)totalLen);
        fs.Write(hdr);

        Span<byte> chunkHdr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr, (uint)jsonLen);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr[4..], ChunkTypeJson);
        fs.Write(chunkHdr);
        fs.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) fs.WriteByte(0x20);  // pad JSON with spaces

        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr, (uint)binLen);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr[4..], ChunkTypeBin);
        fs.Write(chunkHdr);
        fs.Write(bin);
        for (int i = 0; i < binPad; i++) fs.WriteByte(0);
    }
}
