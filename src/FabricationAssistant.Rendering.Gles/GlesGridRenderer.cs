using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Draws a horizontal ground grid in the XZ plane positioned at the bottom
/// of the current scene bounds. The grid is computed in the fragment shader
/// via screen-space fwidth so it stays crisp at any zoom level, fades to
/// the background past a configurable radius, and renders major / minor
/// lines at a 10:1 ratio.
///
/// All GL calls must run on the render thread.
/// </summary>
public sealed class GlesGridRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;
    private readonly float[] _modelScratch = new float[16];
    private readonly float[] _viewScratch = new float[16];
    private readonly float[] _projectionScratch = new float[16];

    public GlesGridRenderer(GL gl, string vertSource, string fragSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(_gl, "grid", vertSource, fragSource);
        BuildQuad();
    }

    private unsafe void BuildQuad()
    {
        // 4 vertices in the XY plane (z = 0) - the rest of the app is Z-up
        // (MainActivity.FrameCameraToBounds uses Vector3d.UnitZ as
        // UpDirection), so the floor is in XY and the model's "down" is -Z.
        // Draw call scales + translates via uModel.
        float[] verts =
        {
            -1f, -1f, 0f,
             1f, -1f, 0f,
             1f,  1f, 0f,
            -1f,  1f, 0f,
        };
        uint[] indices = { 0, 1, 2, 0, 2, 3 };

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the grid for the current scene + camera. No-op when bounds are
    /// invalid (no scene loaded). Caller should invoke after the depth clear
    /// and before opaque meshes so meshes occlude the grid where they sit on
    /// or above it.
    /// </summary>
    public void Draw(GpuScene scene, CameraState camera, int viewportWidth, int viewportHeight, SceneAppearance appearance)
    {
        BoundingBox bounds = scene.Bounds;
        if (!bounds.IsValid || _vao == 0) return;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        // Place the plane at the bottom of the scene's vertical extent (Z,
        // since the app is Z-up) so it sits "under" the model. Scale to ~8x
        // the bounds diagonal so the plane extends well past the model in
        // every direction and the fade radius blends to background before
        // the user can see the edge.
        double diag = bounds.Size.Length;
        if (diag <= 0) diag = 1.0;
        double scale = diag * 8.0;
        double centerX = bounds.Center.X;
        double centerY = bounds.Center.Y;
        double planeZ = appearance.ShiftGridToModelMin ? bounds.Min.Z : 0.0;

        // Slight downward offset so the plane never z-fights with parts that
        // happen to sit exactly at bounds.Min.Z.
        double zOffset = -diag * 0.001;

        // The canonical quad lives in XY (z = 0). Scale X and Y, leave Z = 1,
        // translate the whole thing to (centerX, centerY, minZ + zOffset).
        _modelScratch[0] = (float)scale;
        _modelScratch[1] = 0f;
        _modelScratch[2] = 0f;
        _modelScratch[3] = (float)centerX;
        _modelScratch[4] = 0f;
        _modelScratch[5] = (float)scale;
        _modelScratch[6] = 0f;
        _modelScratch[7] = (float)centerY;
        _modelScratch[8] = 0f;
        _modelScratch[9] = 0f;
        _modelScratch[10] = 1f;
        _modelScratch[11] = (float)(planeZ + zOffset);
        _modelScratch[12] = 0f;
        _modelScratch[13] = 0f;
        _modelScratch[14] = 0f;
        _modelScratch[15] = 1f;

        ViewportCameraMath.FillViewMatrix(camera, _viewScratch);
        ViewportCameraMath.FillProjectionMatrix(camera, (float)viewportWidth / viewportHeight, _projectionScratch);

        _program.Use();
        SetMat4("uModel", _modelScratch);
        SetMat4("uView", _viewScratch);
        SetMat4("uProjection", _projectionScratch);
        SetVec3("uCameraPosWorld", (float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);

        // Choose a minor-line spacing that gives ~10-25 minor cells across the
        // model. Round to a nice power of 10 multiple (1, 2, 5, 10, ...) so
        // line spacings remain readable as the user zooms.
        double targetSpacing = appearance.UseAutomaticGridSpacing
            ? diag / 30.0
            : System.Math.Max(appearance.GridSpacingMm / System.Math.Max(scene.MillimetersPerSceneUnit, 1e-9), 1e-6f);
        double minorSpacing = appearance.UseAutomaticGridSpacing
            ? RoundToNiceNumber(targetSpacing)
            : targetSpacing;
        SetVec2("uGridSpacing", (float)minorSpacing, 10f);
        SetFloat("uLineThickness", System.Math.Clamp(appearance.GridLineThickness, 1.0f, 8.0f));

        SetFloat("uFadeRadius", (float)(diag * 3.5));

        float[] bg = appearance.Mode == RenderMode.Clay
            ? appearance.ClayBackgroundColor
            : appearance.BackgroundColor;
        float[] grid = appearance.GridLineColor;
        SetVec3("uBackgroundColor", bg[0], bg[1], bg[2]);
        SetVec3("uMinorColor", grid[0] * 0.75f, grid[1] * 0.75f, grid[2] * 0.75f);
        SetVec3("uMajorColor",
            System.Math.Min(grid[0] * 1.35f, 1.0f),
            System.Math.Min(grid[1] * 1.35f, 1.0f),
            System.Math.Min(grid[2] * 1.35f, 1.0f));
        SetVec3("uAxisUColor", 0.70f, 0.20f, 0.20f);
        SetVec3("uAxisVColor", 0.20f, 0.70f, 0.20f);

        // The grid renders with depth write OFF so it never occludes opaque
        // meshes that sit above it - we want them to overwrite the grid.
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace); // grid is double-sided
        try
        {
            _gl.BindVertexArray(_vao);
            unsafe
            {
                _gl.DrawElements(PrimitiveType.Triangles, 6u, DrawElementsType.UnsignedInt, (void*)0);
            }
        }
        finally
        {
            _gl.BindVertexArray(0);
            _gl.Enable(EnableCap.CullFace);
            _gl.DepthMask(true);
        }
    }

    private static double RoundToNiceNumber(double v)
    {
        if (v <= 0) return 1.0;
        double exp = System.Math.Floor(System.Math.Log10(v));
        double f = v / System.Math.Pow(10.0, exp);
        double nice = f switch
        {
            < 1.5 => 1.0,
            < 3.0 => 2.0,
            < 7.0 => 5.0,
            _ => 10.0,
        };
        return nice * System.Math.Pow(10.0, exp);
    }

    private void SetMat4(string name, float[] m)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, m);
    }

    private void SetVec3(string name, float x, float y, float z)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.Uniform3(loc, x, y, z);
    }

    private void SetVec2(string name, float x, float y)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.Uniform2(loc, x, y);
    }

    private void SetFloat(string name, float v)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.Uniform1(loc, v);
    }

    public void Dispose()
    {
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        _program.Dispose();
    }
}
