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
    private nint _handle;

    public int NumPoints { get; }
    public int NumFaces { get; }

    private DracoMesh(nint handle)
    {
        _handle = handle;
        lock (DracoNativeDecoder.NativeGate)
        {
            NumPoints = DracoNativeDecoder.GetNumPoints(handle);
            NumFaces = DracoNativeDecoder.GetNumFaces(handle);
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

    private unsafe float[]? CopyFloatAttribute(DracoNativeDecoder.AttributeType type, int components)
    {
        int total = NumPoints * components;
        if (total <= 0) return null;
        var buffer = new float[total];
        fixed (float* p = buffer)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                if (DracoNativeDecoder.CopyAttributeFloat(_handle, type, components, (nint)p, total) == 0)
                    return null;
            }
        }
        return buffer;
    }

    public unsafe uint[]? GetIndices()
    {
        int total = NumFaces * 3;
        if (total <= 0) return null;
        var buffer = new uint[total];
        fixed (uint* p = buffer)
        {
            lock (DracoNativeDecoder.NativeGate)
            {
                if (DracoNativeDecoder.CopyIndicesUint32(_handle, (nint)p, total) == 0)
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

    ~DracoMesh() => Dispose();
}
