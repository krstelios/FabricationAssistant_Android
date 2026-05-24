using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Converts CameraState into the 4x4 / 3x3 matrices the GLES mesh shader expects.
/// Core's Matrix4d is row-major; GL is column-major. We pass `transpose=true`
/// to glUniformMatrix4 (handled by the renderer), so the float[16] we produce
/// here is the row-major dump of Matrix4d in its native field order.
/// </summary>
public static class ViewportCameraMath
{
    public static float[] ViewMatrix(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var m = Matrix4d.CreateLookAt(camera.Position, camera.Target, camera.UpDirection);
        return ToFloats(m);
    }

    public static float[] ProjectionMatrix(CameraState camera, float aspect)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Matrix4d m;
        if (camera.IsPerspective)
        {
            m = Matrix4d.CreatePerspectiveFieldOfView(camera.FieldOfView, aspect, camera.NearPlane, camera.FarPlane);
        }
        else
        {
            double height = camera.OrthoWidth / System.Math.Max(aspect, 1e-6);
            m = Matrix4d.CreateOrthographic(camera.OrthoWidth, height, camera.NearPlane, camera.FarPlane);
        }
        return ToFloats(m);
    }

    public static float[] IdentityModelMatrix() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    public static float[] NormalMatrixFromIdentity() => new float[]
    {
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
    };

    private static float[] ToFloats(Matrix4d m) => new float[]
    {
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44,
    };
}
