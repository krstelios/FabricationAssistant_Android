using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
    private const long MaxSourceBytes = 1L * 1024L * 1024L * 1024L;
    // S5#2: capped at int.MaxValue rather than a full 2 GiB. The decoded buffer
    // byteLength and bufferView offsets are written as glTF int32 fields via
    // checked((int)...); a budget of exactly 2 GiB (2^31) is one byte past
    // int.MaxValue and would throw an uncaught OverflowException instead of the
    // catchable InvalidDataException EnsureDecodedBudget raises.
    private const long MaxDecodedBytes = int.MaxValue;

    public static void Transcode(string sourceGlbPath, string destinationGlbPath)
    {
        ArgumentNullException.ThrowIfNull(sourceGlbPath);
        ArgumentNullException.ThrowIfNull(destinationGlbPath);

        long sourceLength = new FileInfo(sourceGlbPath).Length;
        if (sourceLength > MaxSourceBytes)
            throw new InvalidDataException($"Draco source GLB is too large ({sourceLength:n0} bytes). Maximum supported size is {MaxSourceBytes:n0} bytes.");

        GlbSource glb = ReadGlb(sourceGlbPath);

        var root = JsonNode.Parse(glb.Json) ?? throw new InvalidDataException("glTF JSON parse failed");

        string tempBinPath = Path.Combine(
            Path.GetDirectoryName(destinationGlbPath) ?? Path.GetTempPath(),
            $"{Path.GetFileName(destinationGlbPath)}.{Guid.NewGuid():N}.bin");

        try
        {
            using var newBin = new FileStream(
                tempBinPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            using var sourceGlb = File.OpenRead(sourceGlbPath);

            // Preserve the original binary chunk content as the prefix of the
            // new chunk. This keeps every non-Draco bufferView (textures,
            // animations, skins, anything else the file embeds) pointing at
            // valid bytes. Decoded Draco buffers are appended after this prefix.
            CopyBinaryChunk(sourceGlb, newBin, glb.BinOffset, glb.BinLength);
            EnsureDecodedBudget(newBin.Length);
            PadTo4Bytes(newBin);

            var bufferViews = root["bufferViews"] as JsonArray ?? new JsonArray();
            var accessors = root["accessors"] as JsonArray ?? new JsonArray();
            var meshes = root["meshes"] as JsonArray;
            if (meshes is null)
            {
                File.Copy(sourceGlbPath, destinationGlbPath, overwrite: true);
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
                    if (viewIndex < 0 || viewIndex >= bufferViews.Count || bufferViews[viewIndex] is not JsonObject view)
                        throw new InvalidDataException("Draco bufferView index is invalid.");

                    int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
                    int length = view["byteLength"]!.GetValue<int>();
                    byte[] encoded = ReadBinaryChunkRange(
                        sourceGlb,
                        glb.BinOffset,
                        glb.BinLength,
                        offset,
                        length);

                    using var dm = DracoMesh.Decode(encoded);

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
            EnsureDecodedBudget(newBin.Length);
            buffers[0]!["byteLength"] = checked((int)newBin.Length);
            ((JsonObject)buffers[0]!).Remove("uri");
            root["buffers"] = buffers;

            if (!changed)
            {
                File.Copy(sourceGlbPath, destinationGlbPath, overwrite: true);
                return;
            }

            WriteGlb(destinationGlbPath, root.ToJsonString(), newBin);
        }
        finally
        {
            TryDeleteFile(tempBinPath);
        }
    }

    private static int AppendFloatBufferView(JsonArray bufferViews, Stream bin, float[] data, int byteStride)
    {
        long offset = bin.Position;
        EnsureDecodedBudget(offset + (long)data.Length * 4L + 3L);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan());
        bin.Write(bytes);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = checked((int)offset),
            ["byteLength"] = bytes.Length,
            ["byteStride"] = byteStride,
        };
        bufferViews.Add(node);
        return bufferViews.Count - 1;
    }

    private static int AppendUInt32BufferView(JsonArray bufferViews, Stream bin, uint[] data)
    {
        long offset = bin.Position;
        EnsureDecodedBudget(offset + (long)data.Length * 4L + 3L);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan());
        bin.Write(bytes);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = checked((int)offset),
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

    private static void PadTo4Bytes(Stream s)
    {
        long pad = (4 - (s.Position % 4)) % 4;
        EnsureDecodedBudget(s.Position + pad);
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    private static void EnsureDecodedBudget(long byteCount)
    {
        if (byteCount > MaxDecodedBytes)
            throw new InvalidDataException($"Decoded Draco GLB exceeds the maximum supported size of {MaxDecodedBytes:n0} bytes.");
    }

    private static GlbSource ReadGlb(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < 12) throw new InvalidDataException("GLB too short");

        Span<byte> header = stackalloc byte[12];
        fs.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != GlbMagic) throw new InvalidDataException("Not a GLB file");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != GlbVersion) throw new InvalidDataException($"Unsupported GLB version {version}.");
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (declaredLength != fs.Length) throw new InvalidDataException("GLB declared length does not match file length.");

        string? json = null;
        long binOffset = 0;
        int binLength = 0;
        int chunkIndex = 0;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (fs.Position < fs.Length)
        {
            if (fs.Length - fs.Position < 8)
                throw new InvalidDataException("GLB chunk header is truncated");

            fs.ReadExactly(chunkHeader);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            if (chunkLength > int.MaxValue || fs.Position + chunkLength > fs.Length)
                throw new InvalidDataException("GLB chunk length exceeds file bounds");

            // S4-L2: the glTF 2.0 spec requires the first chunk to be JSON, and
            // ImportFileSignatureValidator enforces that for top-level .glb imports.
            // ReadGlb also runs on inner GLBs unpacked from .fa archives, which the
            // top-level validator never sees, so enforce JSON-first here too instead
            // of relying on that gate - keeping the two GLB parsers consistent.
            if (chunkIndex == 0 && chunkType != ChunkTypeJson)
                throw new InvalidDataException("GLB first chunk is not JSON.");

            if (chunkType == ChunkTypeJson)
            {
                byte[] jsonBytes = new byte[checked((int)chunkLength)];
                fs.ReadExactly(jsonBytes);
                json = Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0', ' ');
            }
            else if (chunkType == ChunkTypeBin)
            {
                binOffset = fs.Position;
                binLength = checked((int)chunkLength);
                fs.Position += chunkLength;
            }
            else
            {
                fs.Position += chunkLength;
            }
            chunkIndex++;
        }
        if (json is null) throw new InvalidDataException("GLB missing JSON chunk");
        return new GlbSource(json, binOffset, binLength);
    }

    private static void CopyBinaryChunk(Stream source, Stream destination, long sourceOffset, int length)
    {
        if (length <= 0)
            return;

        source.Position = sourceOffset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int remaining = length;
            while (remaining > 0)
            {
                int read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw new EndOfStreamException("GLB binary chunk ended unexpectedly.");

                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] ReadBinaryChunkRange(
        Stream source,
        long binOffset,
        int binLength,
        int byteOffset,
        int byteLength)
    {
        if (byteOffset < 0 || byteLength < 0 || (long)byteOffset + byteLength > binLength)
            throw new InvalidDataException("Draco bufferView range exceeds GLB binary chunk bounds.");

        byte[] data = new byte[byteLength];
        source.Position = binOffset + byteOffset;
        source.ReadExactly(data);
        return data;
    }

    private static void WriteGlb(string path, string json, Stream bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int binLength = checked((int)bin.Length);
        int binPad = (4 - (binLength % 4)) % 4;
        int jsonLen = jsonBytes.Length + jsonPad;
        int binLen = binLength + binPad;
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
        bin.Position = 0;
        bin.CopyTo(fs);
        for (int i = 0; i < binPad; i++) fs.WriteByte(0);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private readonly record struct GlbSource(string Json, long BinOffset, int BinLength);
}
