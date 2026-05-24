using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal static class GlesRenderUtil
{
    private const double NormalMatrixDeterminantEpsilon = 1e-30;

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
            return ViewportCameraMath.NormalMatrixFromIdentity();

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
        // Return the row-major inverse-transpose normal matrix.
        return
        [
            (float)i00, (float)i10, (float)i20,
            (float)i01, (float)i11, (float)i21,
            (float)i02, (float)i12, (float)i22,
        ];
    }
}
