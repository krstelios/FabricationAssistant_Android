using System.Buffers;
using System.Text;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// Cheap scan that determines whether a glTF/GLB file uses
/// KHR_draco_mesh_compression. Reads up to 32 MB of the JSON chunk and
/// substring-matches; this is conservative but fast and avoids constructing
/// a full SharpGLTF reader.
/// </summary>
public static class DracoExtensionDetector
{
    private const int ScanLimitBytes = 32 * 1024 * 1024;
    private static readonly byte[] Needle = Encoding.ASCII.GetBytes("KHR_draco_mesh_compression");

    public static bool ContainsDraco(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath)) return false;

        using var fs = File.OpenRead(filePath);
        long readable = Math.Min(fs.Length, ScanLimitBytes);

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(readable, 64 * 1024));
        try
        {
            int matched = 0;
            long total = 0;
            int n;
            while (total < readable && (n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, readable - total))) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] == Needle[matched])
                    {
                        matched++;
                        if (matched == Needle.Length) return true;
                    }
                    else if (buffer[i] == Needle[0])
                    {
                        matched = 1;
                    }
                    else
                    {
                        matched = 0;
                    }
                }
                total += n;
            }
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
