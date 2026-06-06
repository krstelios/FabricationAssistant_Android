using System.Runtime.InteropServices;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// Managed P/Invoke surface to libdraco_native.so. Entry points match the
/// extern "C" signatures in native/draco_native.cpp.
/// </summary>
internal static class DracoNativeDecoder
{
    private const string LibName = "libdraco_native";
    public static readonly object NativeGate = new();

    public enum AttributeType
    {
        Position = 0,
        Normal = 1,
        TexCoord = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeInfo
    {
        public int AttributeType;
        public int DataType;
        public int ComponentCount;
        public int Normalized;
        public int ByteStride;
    }

    [DllImport(LibName, EntryPoint = "draco_decode_buffer_to_mesh")]
    public static extern nint DecodeBufferToMesh(nint data, int size);

    [DllImport(LibName, EntryPoint = "draco_mesh_get_num_faces")]
    public static extern int GetNumFaces(nint handle);

    [DllImport(LibName, EntryPoint = "draco_mesh_get_num_points")]
    public static extern int GetNumPoints(nint handle);

    [DllImport(LibName, EntryPoint = "draco_mesh_copy_attribute_float")]
    public static extern int CopyAttributeFloat(
        nint handle,
        AttributeType attribute,
        int componentsExpected,
        nint outBuffer,
        int outCount);

    [DllImport(LibName, EntryPoint = "draco_mesh_get_attribute_info_by_unique_id")]
    public static extern int GetAttributeInfoByUniqueId(
        nint handle,
        int uniqueId,
        out AttributeInfo info);

    [DllImport(LibName, EntryPoint = "draco_mesh_copy_attribute_by_unique_id")]
    public static extern int CopyAttributeByUniqueId(
        nint handle,
        int uniqueId,
        nint outBuffer,
        int outCount);

    [DllImport(LibName, EntryPoint = "draco_mesh_copy_indices_uint32")]
    public static extern int CopyIndicesUint32(nint handle, nint outBuffer, int outCount);

    [DllImport(LibName, EntryPoint = "draco_mesh_destroy")]
    public static extern void DestroyMesh(nint handle);
}

/// <summary>
/// High-level managed wrapper around a single Draco-encoded buffer decode.
/// Owns the native handle returned by libdraco_native and exposes positions,
/// normals, texcoords and indices as managed arrays.
/// </summary>
public sealed class DracoMesh : IDisposable
{
    private const long MaxNativeDecodedBufferBytes = 256L * 1024L * 1024L;
    private const int MaxDecodedPoints = (int)(MaxNativeDecodedBufferBytes / (4L * 3L));
    private const int MaxDecodedFaces = (int)(MaxNativeDecodedBufferBytes / (4L * 3L));

    private nint _handle;

    public int NumPoints { get; }
    public int NumFaces { get; }

    private DracoMesh(nint handle)
    {
        _handle = handle;
        try
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                NumPoints = DracoNativeDecoder.GetNumPoints(handle);
                NumFaces = DracoNativeDecoder.GetNumFaces(handle);
            }

            if (NumPoints < 0 || NumFaces < 0)
                throw new InvalidDataException("Draco decoded mesh declares counts outside the supported int32 range.");
            if (NumPoints > MaxDecodedPoints)
                throw new InvalidDataException($"Draco decoded mesh has too many points ({NumPoints:n0}; maximum {MaxDecodedPoints:n0}).");
            if (NumFaces > MaxDecodedFaces)
                throw new InvalidDataException($"Draco decoded mesh has too many faces ({NumFaces:n0}; maximum {MaxDecodedFaces:n0}).");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static unsafe DracoMesh Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
            throw new InvalidDataException("Draco decode failed: encoded buffer is empty.");

        fixed (byte* p = encoded)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                nint handle = DracoNativeDecoder.DecodeBufferToMesh((nint)p, encoded.Length);
                if (handle == nint.Zero)
                    throw new InvalidDataException("Draco decode failed: native decoder returned a null mesh handle.");

                return new DracoMesh(handle);
            }
        }
    }

    public float[]? GetPositions() => CopyFloatAttribute(DracoNativeDecoder.AttributeType.Position, 3);
    public float[]? GetNormals() => CopyFloatAttribute(DracoNativeDecoder.AttributeType.Normal, 3);
    public float[]? GetTexCoords() => CopyFloatAttribute(DracoNativeDecoder.AttributeType.TexCoord, 2);

    internal unsafe DracoAttributeData? GetAttributeByUniqueId(int uniqueId)
    {
        if (uniqueId < 0)
            return null;

        DracoNativeDecoder.AttributeInfo info;
        lock (DracoNativeDecoder.NativeGate)
        {
            if (DracoNativeDecoder.GetAttributeInfoByUniqueId(_handle, uniqueId, out info) == 0)
                return null;
        }

        if (info.ComponentCount <= 0)
            throw new InvalidDataException($"Draco attribute {uniqueId} declares an invalid component count ({info.ComponentCount}).");
        if (info.ByteStride <= 0)
            throw new InvalidDataException($"Draco attribute {uniqueId} declares an invalid byte stride ({info.ByteStride}).");

        long totalBytes = (long)NumPoints * info.ByteStride;
        if (totalBytes <= 0)
            return null;
        if (totalBytes > int.MaxValue)
            throw new InvalidDataException($"Draco attribute {uniqueId} is too large to decode ({totalBytes:n0} bytes).");
        if (totalBytes > MaxNativeDecodedBufferBytes)
            throw new InvalidDataException($"Draco attribute {uniqueId} exceeds the maximum supported buffer size of {MaxNativeDecodedBufferBytes:n0} bytes.");

        byte[] buffer = new byte[(int)totalBytes];
        fixed (byte* p = buffer)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                if (DracoNativeDecoder.CopyAttributeByUniqueId(_handle, uniqueId, (nint)p, buffer.Length) == 0)
                    return null;
            }
        }

        return new DracoAttributeData(
            uniqueId,
            info.AttributeType,
            info.DataType,
            info.ComponentCount,
            info.Normalized != 0,
            info.ByteStride,
            buffer);
    }

    private unsafe float[]? CopyFloatAttribute(DracoNativeDecoder.AttributeType type, int components)
    {
        // S5-L1: NumPoints * components can overflow int32 for very large meshes,
        // wrapping negative -> a misleading "missing attributes" null. Compute in long
        // and reject anything that cannot be expressed as an int-sized array.
        long total = (long)NumPoints * components;
        if (total <= 0) return null;
        if (total > int.MaxValue)
            throw new InvalidDataException($"Draco mesh attribute is too large to decode ({total:n0} elements).");
        if (total * 4L > MaxNativeDecodedBufferBytes)
            throw new InvalidDataException($"Draco mesh attribute exceeds the maximum supported buffer size of {MaxNativeDecodedBufferBytes:n0} bytes.");
        var buffer = new float[(int)total];
        fixed (float* p = buffer)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                if (DracoNativeDecoder.CopyAttributeFloat(_handle, type, components, (nint)p, (int)total) == 0)
                    return null;
            }
        }
        return buffer;
    }

    public unsafe uint[]? GetIndices()
    {
        // S5-L1: NumFaces * 3 can overflow int32; compute in long and bound it.
        long total = (long)NumFaces * 3;
        if (total <= 0) return null;
        if (total > int.MaxValue)
            throw new InvalidDataException($"Draco mesh has too many indices to decode ({total:n0}).");
        if (total * 4L > MaxNativeDecodedBufferBytes)
            throw new InvalidDataException($"Draco mesh index buffer exceeds the maximum supported buffer size of {MaxNativeDecodedBufferBytes:n0} bytes.");
        var buffer = new uint[(int)total];
        fixed (uint* p = buffer)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                if (DracoNativeDecoder.CopyIndicesUint32(_handle, (nint)p, (int)total) == 0)
                    return null;
            }
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                if (_handle != nint.Zero)
                {
                    DracoNativeDecoder.DestroyMesh(_handle);
                    _handle = nint.Zero;
                }
            }
        }
        GC.SuppressFinalize(this);
    }

    // S5-M5: safety net only - every call site disposes deterministically (using), so
    // the finalizer should not normally run. If it does, DestroyMesh still goes through
    // NativeGate (Dispose acquires it), serializing it against any in-flight decode, so
    // freeing on the finalizer thread is safe.
    ~DracoMesh() => Dispose();
}

internal sealed record DracoAttributeData(
    int UniqueId,
    int AttributeType,
    int DataType,
    int ComponentCount,
    bool Normalized,
    int ByteStride,
    byte[] Data);
