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
    // Keep the decoded GLB under a tablet-friendly byte budget. This still stays
    // below glTF int32 fields and turns hostile tiny-compressed inputs that expand
    // huge into a catchable InvalidDataException before the importer loads them.
    private const long MaxDecodedBytes = 512L * 1024L * 1024L;

    public static void Transcode(string sourceGlbPath, string destinationGlbPath, Action<string>? onWarning = null)
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
                    if (prim is not JsonObject primObj)
                        continue;

                    var ext = primObj["extensions"]?["KHR_draco_mesh_compression"];
                    if (ext is null) continue;

                    int viewIndex = ext["bufferView"]!.GetValue<int>();
                    if (viewIndex < 0 || viewIndex >= bufferViews.Count || bufferViews[viewIndex] is not JsonObject view)
                        throw new InvalidDataException("Draco bufferView index is invalid.");

                    // S5-2: the encoded Draco bytes are read from the GLB BIN chunk
                    // (buffer 0), so a bufferView pointing at any other buffer - or an
                    // external (uri) buffer - would feed the decoder the wrong bytes.
                    int bufferIndex = view["buffer"]?.GetValue<int>() ?? 0;
                    if (bufferIndex != 0)
                        throw new InvalidDataException("Draco bufferView must reference the embedded GLB buffer (buffer 0).");
                    if (root["buffers"] is JsonArray bufferList
                        && bufferIndex < bufferList.Count
                        && bufferList[bufferIndex] is JsonObject bufferObj
                        && bufferObj["uri"] is not null)
                    {
                        throw new InvalidDataException("Draco bufferView references an external buffer, which is not supported.");
                    }

                    int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
                    int length = view["byteLength"]!.GetValue<int>();
                    byte[] encoded = ReadBinaryChunkRange(
                        sourceGlb,
                        glb.BinOffset,
                        glb.BinLength,
                        offset,
                        length);

                    var dracoAttributes = ext["attributes"] as JsonObject
                        ?? throw new InvalidDataException("Draco extension is missing its attribute map.");

                    using var dm = DracoMesh.Decode(encoded);

                    uint[]? indices = dm.GetIndices();
                    if (indices is null)
                        throw new InvalidDataException("Draco decoded mesh missing triangle indices.");

                    JsonObject rebuiltAttribs = DecodeDracoAttributes(
                        dracoAttributes,
                        dm,
                        bufferViews,
                        accessors,
                        newBin);
                    int idxView = AppendUInt32BufferView(bufferViews, newBin, indices);
                    int idxAccessor = AppendUIntAccessor(accessors, idxView, indices.Length);

                    primObj["attributes"] = rebuiltAttribs;
                    primObj["indices"] = idxAccessor;

                    JsonObject extensions = (JsonObject)primObj["extensions"]!;
                    extensions.Remove("KHR_draco_mesh_compression");
                    if (extensions.Count == 0)
                        primObj.Remove("extensions");

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

    private static JsonObject DecodeDracoAttributes(
        JsonObject dracoAttributes,
        DracoMesh dm,
        JsonArray bufferViews,
        JsonArray accessors,
        Stream bin)
    {
        var rebuilt = new JsonObject();
        bool hasPosition = false;

        foreach (KeyValuePair<string, JsonNode?> entry in dracoAttributes)
        {
            if (entry.Value is null)
                throw new InvalidDataException($"Draco attribute '{entry.Key}' is missing its unique id.");

            int uniqueId = entry.Value.GetValue<int>();
            DracoAttributeData attribute = dm.GetAttributeByUniqueId(uniqueId)
                ?? throw new InvalidDataException($"Draco decoded mesh is missing attribute '{entry.Key}' with unique id {uniqueId}.");

            int componentType = GetGltfComponentTypeForDracoDataType(attribute.DataType);
            string accessorType = GetGltfAccessorType(attribute.ComponentCount);
            ValidateDecodedAttributeForGltf(entry.Key, attribute, componentType, accessorType);

            int viewIndex = AppendRawBufferView(bufferViews, bin, attribute.Data);
            int accessorIndex = AppendDecodedAttributeAccessor(
                accessors,
                viewIndex,
                dm.NumPoints,
                entry.Key,
                attribute,
                componentType,
                accessorType);

            rebuilt[entry.Key] = accessorIndex;
            if (string.Equals(entry.Key, "POSITION", StringComparison.Ordinal))
                hasPosition = true;
        }

        if (!hasPosition)
            throw new InvalidDataException("Draco decoded mesh missing required POSITION attribute.");

        return rebuilt;
    }

    private static int AppendRawBufferView(JsonArray bufferViews, Stream bin, byte[] data)
    {
        long offset = bin.Position;
        EnsureDecodedBudget(offset + data.Length + 3L);
        bin.Write(data);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = checked((int)offset),
            ["byteLength"] = data.Length,
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

    private static int AppendDecodedAttributeAccessor(
        JsonArray accessors,
        int bufferView,
        int count,
        string semantic,
        DracoAttributeData attribute,
        int componentType,
        string accessorType)
    {
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = accessorType,
        };

        if (attribute.Normalized)
            node["normalized"] = true;

        if (string.Equals(semantic, "POSITION", StringComparison.Ordinal))
        {
            var (min, max) = ComputeVec3FloatMinMax(attribute.Data);
            node["min"] = new JsonArray(min[0], min[1], min[2]);
            node["max"] = new JsonArray(max[0], max[1], max[2]);
        }

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

    private static (float[] min, float[] max) ComputeVec3FloatMinMax(byte[] data)
    {
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(data);
        if (values.Length == 0 || values.Length % 3 != 0)
            throw new InvalidDataException("POSITION attribute does not contain tightly packed VEC3 float data.");

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i + 2 < values.Length; i += 3)
        {
            if (values[i]     < minX) minX = values[i];
            if (values[i + 1] < minY) minY = values[i + 1];
            if (values[i + 2] < minZ) minZ = values[i + 2];
            if (values[i]     > maxX) maxX = values[i];
            if (values[i + 1] > maxY) maxY = values[i + 1];
            if (values[i + 2] > maxZ) maxZ = values[i + 2];
        }
        return (new[] { minX, minY, minZ }, new[] { maxX, maxY, maxZ });
    }

    internal static int GetGltfComponentTypeForDracoDataType(int dracoDataType)
        => dracoDataType switch
        {
            1 => 5120, // BYTE
            2 => 5121, // UNSIGNED_BYTE
            3 => 5122, // SHORT
            4 => 5123, // UNSIGNED_SHORT
            6 => 5125, // UNSIGNED_INT
            9 => 5126, // FLOAT
            _ => throw new InvalidDataException($"Draco attribute data type {dracoDataType} cannot be represented as a glTF accessor."),
        };

    internal static string GetGltfAccessorType(int componentCount)
        => componentCount switch
        {
            1 => "SCALAR",
            2 => "VEC2",
            3 => "VEC3",
            4 => "VEC4",
            _ => throw new InvalidDataException($"Draco attribute component count {componentCount} cannot be represented as a glTF accessor."),
        };

    private static void ValidateDecodedAttributeForGltf(
        string semantic,
        DracoAttributeData attribute,
        int componentType,
        string accessorType)
    {
        if (attribute.Data.Length % attribute.ByteStride != 0)
            throw new InvalidDataException($"Draco attribute '{semantic}' has inconsistent byte stride metadata.");

        switch (semantic)
        {
            case "POSITION":
                RequireAttributeShape(semantic, componentType, accessorType, 5126, "VEC3", normalized: false, attribute.Normalized);
                break;
            case "NORMAL":
                RequireAttributeShape(semantic, componentType, accessorType, 5126, "VEC3", normalized: false, attribute.Normalized);
                break;
            case "TANGENT":
                RequireAttributeShape(semantic, componentType, accessorType, 5126, "VEC4", normalized: false, attribute.Normalized);
                break;
            default:
                if (IsNumberedSemantic(semantic, "TEXCOORD_"))
                {
                    if (accessorType != "VEC2"
                        || componentType is not (5121 or 5123 or 5126))
                    {
                        throw new InvalidDataException($"Draco attribute '{semantic}' has an unsupported glTF texture-coordinate layout.");
                    }
                    if (componentType != 5126 && !attribute.Normalized)
                        throw new InvalidDataException($"Draco integer texture-coordinate attribute '{semantic}' must be normalized.");
                    if (componentType == 5126 && attribute.Normalized)
                        throw new InvalidDataException($"Draco float texture-coordinate attribute '{semantic}' cannot be normalized.");
                }
                else if (IsNumberedSemantic(semantic, "COLOR_"))
                {
                    if (accessorType is not ("VEC3" or "VEC4")
                        || componentType is not (5121 or 5123 or 5126))
                    {
                        throw new InvalidDataException($"Draco attribute '{semantic}' has an unsupported glTF color layout.");
                    }
                    if (componentType != 5126 && !attribute.Normalized)
                        throw new InvalidDataException($"Draco integer color attribute '{semantic}' must be normalized.");
                    if (componentType == 5126 && attribute.Normalized)
                        throw new InvalidDataException($"Draco float color attribute '{semantic}' cannot be normalized.");
                }
                else if (IsNumberedSemantic(semantic, "JOINTS_"))
                {
                    if (accessorType != "VEC4" || componentType is not (5121 or 5123) || attribute.Normalized)
                        throw new InvalidDataException($"Draco attribute '{semantic}' has an unsupported glTF joints layout.");
                }
                else if (IsNumberedSemantic(semantic, "WEIGHTS_"))
                {
                    if (accessorType != "VEC4"
                        || componentType is not (5121 or 5123 or 5126))
                    {
                        throw new InvalidDataException($"Draco attribute '{semantic}' has an unsupported glTF weights layout.");
                    }
                    if (componentType != 5126 && !attribute.Normalized)
                        throw new InvalidDataException($"Draco integer weights attribute '{semantic}' must be normalized.");
                    if (componentType == 5126 && attribute.Normalized)
                        throw new InvalidDataException($"Draco float weights attribute '{semantic}' cannot be normalized.");
                }
                else if (semantic.StartsWith("_", StringComparison.Ordinal))
                {
                    if (componentType == 5126 && attribute.Normalized)
                        throw new InvalidDataException($"Draco float custom attribute '{semantic}' cannot be normalized.");
                }
                else
                {
                    throw new InvalidDataException($"Draco attribute '{semantic}' is not a recognized glTF vertex attribute semantic.");
                }
                break;
        }
    }

    private static void RequireAttributeShape(
        string semantic,
        int actualComponentType,
        string actualAccessorType,
        int expectedComponentType,
        string expectedAccessorType,
        bool normalized,
        bool actualNormalized)
    {
        if (actualComponentType != expectedComponentType
            || actualAccessorType != expectedAccessorType
            || actualNormalized != normalized)
        {
            throw new InvalidDataException($"Draco attribute '{semantic}' has an unsupported glTF layout.");
        }
    }

    private static bool IsNumberedSemantic(string semantic, string prefix)
    {
        if (!semantic.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> suffix = semantic.AsSpan(prefix.Length);
        if (suffix.Length == 0)
            return false;

        for (int i = 0; i < suffix.Length; i++)
        {
            if (!char.IsDigit(suffix[i]))
                return false;
        }

        return true;
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
