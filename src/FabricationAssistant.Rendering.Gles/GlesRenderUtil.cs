using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal static class GlesRenderUtil
{
    private const double NormalMatrixDeterminantEpsilon = 1e-30;

    public static GlesPrimitiveLimits QueryPrimitiveLimits(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);

        float lineMin = 1.0f;
        float lineMax = 1.0f;
        float pointMin = 1.0f;
        float pointMax = 64.0f;

        try
        {
            if (TryReadFloatRange(gl, GLEnum.AliasedLineWidthRange, out float queriedLineMin, out float queriedLineMax))
            {
                lineMin = queriedLineMin;
                lineMax = queriedLineMax;
            }

#pragma warning disable CS0618 // GLES exposes the aliased point-size range for gl_PointSize clamping.
            if (TryReadFloatRange(gl, GLEnum.AliasedPointSizeRange, out float queriedPointMin, out float queriedPointMax))
#pragma warning restore CS0618
            {
                pointMin = queriedPointMin;
                pointMax = queriedPointMax;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("FA.Renderer", "Unable to query GLES primitive limits; using conservative defaults: " + ex.Message);
        }

        return new GlesPrimitiveLimits(lineMin, lineMax, pointMin, pointMax);
    }

    public static void ApplyMeshCulling(GL gl, GpuMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.DoubleSided)
        {
            gl.Disable(EnableCap.CullFace);
            gl.FrontFace(FrontFaceDirection.Ccw);
            return;
        }

        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(mesh.HasMirroredHandedness
            ? FrontFaceDirection.CW
            : FrontFaceDirection.Ccw);
    }

    public static void ResetMeshCulling(GL gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        gl.Enable(EnableCap.CullFace);
        gl.CullFace(TriangleFace.Back);
        gl.FrontFace(FrontFaceDirection.Ccw);
    }

    public static bool HasMirroredHandedness(float[]? rowMajorModel)
    {
        if (rowMajorModel is null || rowMajorModel.Length < 11)
            return false;

        double m00 = rowMajorModel[0];
        double m01 = rowMajorModel[1];
        double m02 = rowMajorModel[2];
        double m10 = rowMajorModel[4];
        double m11 = rowMajorModel[5];
        double m12 = rowMajorModel[6];
        double m20 = rowMajorModel[8];
        double m21 = rowMajorModel[9];
        double m22 = rowMajorModel[10];

        double det = m00 * (m11 * m22 - m12 * m21)
                   - m01 * (m10 * m22 - m12 * m20)
                   + m02 * (m10 * m21 - m11 * m20);

        return det < 0.0;
    }

    public static float[] NormalMatrixFromWorld(float[]? rowMajorModel)
    {
        if (rowMajorModel is null || rowMajorModel.Length < 11)
            return ViewportCameraMath.NormalMatrixFromIdentity();

        float[] destination = new float[9];
        if (!NormalMatrixFromWorld(rowMajorModel, destination))
            return ViewportCameraMath.NormalMatrixFromIdentity();
        return destination;
    }

    public static bool NormalMatrixFromWorld(float[]? rowMajorModel, float[] destination)
    {
        if (rowMajorModel is null || rowMajorModel.Length < 11)
            return WriteIdentityNormalMatrix(destination);

        double m00 = rowMajorModel[0];
        double m01 = rowMajorModel[1];
        double m02 = rowMajorModel[2];
        double m10 = rowMajorModel[4];
        double m11 = rowMajorModel[5];
        double m12 = rowMajorModel[6];
        double m20 = rowMajorModel[8];
        double m21 = rowMajorModel[9];
        double m22 = rowMajorModel[10];

        double det = m00 * (m11 * m22 - m12 * m21)
                   - m01 * (m10 * m22 - m12 * m20)
                   + m02 * (m10 * m21 - m11 * m20);

        if (System.Math.Abs(det) < NormalMatrixDeterminantEpsilon)
            return WriteIdentityNormalMatrix(destination);

        double invDet = 1.0 / det;

        double i00 = (m11 * m22 - m12 * m21) * invDet;
        double i01 = (m02 * m21 - m01 * m22) * invDet;
        double i02 = (m01 * m12 - m02 * m11) * invDet;
        double i10 = (m12 * m20 - m10 * m22) * invDet;
        double i11 = (m00 * m22 - m02 * m20) * invDet;
        double i12 = (m02 * m10 - m00 * m12) * invDet;
        double i20 = (m10 * m21 - m11 * m20) * invDet;
        double i21 = (m01 * m20 - m00 * m21) * invDet;
        double i22 = (m00 * m11 - m01 * m10) * invDet;

        // The GLES renderer uploads row-major arrays with transpose=true.
        // Write the row-major inverse-transpose normal matrix.
        destination[0] = (float)i00;
        destination[1] = (float)i10;
        destination[2] = (float)i20;
        destination[3] = (float)i01;
        destination[4] = (float)i11;
        destination[5] = (float)i21;
        destination[6] = (float)i02;
        destination[7] = (float)i12;
        destination[8] = (float)i22;
        return true;
    }

    private static bool WriteIdentityNormalMatrix(float[] destination)
    {
        if (destination.Length < 9)
            throw new ArgumentException("Destination must have at least 9 elements.", nameof(destination));

        destination[0] = 1f;
        destination[1] = 0f;
        destination[2] = 0f;
        destination[3] = 0f;
        destination[4] = 1f;
        destination[5] = 0f;
        destination[6] = 0f;
        destination[7] = 0f;
        destination[8] = 1f;
        return false;
    }

    private static unsafe bool TryReadFloatRange(GL gl, GLEnum pname, out float min, out float max)
    {
        min = 1.0f;
        max = 1.0f;

        float* values = stackalloc float[2];
        values[0] = 0.0f;
        values[1] = 0.0f;
        gl.GetFloat(pname, values);

        float low = values[0];
        float high = values[1];
        if (!float.IsFinite(low) || !float.IsFinite(high) || high <= 0.0f)
            return false;

        min = Math.Max(0.0f, Math.Min(low, high));
        max = Math.Max(min, Math.Max(low, high));
        return true;
    }
}

internal readonly record struct GlesPrimitiveLimits(
    float LineWidthMin,
    float LineWidthMax,
    float PointSizeMin,
    float PointSizeMax)
{
    public float ClampLineWidth(float value)
        => ClampFinite(value, LineWidthMin, LineWidthMax, fallback: 1.0f);

    public float ClampPointSize(float value)
        => ClampFinite(value, PointSizeMin, PointSizeMax, fallback: 1.0f);

    private static float ClampFinite(float value, float min, float max, float fallback)
    {
        if (!float.IsFinite(value))
            value = fallback;
        if (!float.IsFinite(min) || min < 0.0f)
            min = 0.0f;
        if (!float.IsFinite(max) || max < min)
            max = min;
        return Math.Clamp(value, min, max);
    }
}
