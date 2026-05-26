using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal sealed class GlesSectionOverlay : IDisposable
{
    private const int FloatsPerVertex = 7;
    private const int StrideBytes = FloatsPerVertex * sizeof(float);
    private const float MinExtent = 1.0f;
    private const float MaxPlaneExtent = 10_000.0f;
    private const float MaxCapExtent = 100_000.0f;
    private const int GizmoArrowConeSegments = 12;
    private const int GizmoArcSegments = 32;
    private const float GizmoArrowLengthScale = 0.18f;
    private const float GizmoArrowWidthScale = 0.07f;
    private const float GizmoArcRadiusScale = 0.56f;
    private static readonly Vector4 PreviewColor = new(1.00f, 0.85f, 0.20f, 1.00f);
    private static readonly Vector4 HoverColor = new(1.00f, 0.85f, 0.20f, 0.95f);
    private static readonly Vector4 GizmoColorX = new(1.00f, 0.25f, 0.25f, 0.95f);
    private static readonly Vector4 GizmoColorY = new(0.25f, 1.00f, 0.35f, 0.95f);
    private static readonly Vector4 GizmoColorZ = new(0.30f, 0.60f, 1.00f, 0.95f);
    private static readonly Vector4 GizmoArcXColor = new(1.00f, 0.35f, 0.35f, 0.40f);
    private static readonly Vector4 GizmoArcYColor = new(0.35f, 1.00f, 0.40f, 0.40f);
    private static readonly Vector4 GizmoArcZColor = new(0.40f, 0.65f, 1.00f, 0.40f);
    private static readonly Vector4 GizmoHoverColor = new(1.00f, 0.90f, 0.20f, 1.00f);
    private static readonly Vector4 GizmoActiveColor = new(1.00f, 1.00f, 1.00f, 1.00f);

    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private readonly GlesPrimitiveLimits _primitiveLimits;
    private uint _vao;
    private uint _vbo;
    private readonly List<float> _data = new();

    public GlesSectionOverlay(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(gl, "section.overlay", vertexSource, fragmentSource);
        _primitiveLimits = GlesRenderUtil.QueryPrimitiveLimits(gl);
    }

    public float PlaneSizeFraction { get; set; } = 0.025f;

    public Vector4 FillColor { get; set; } = new(0.20f, 0.80f, 0.40f, 0.18f);

    public Vector4 EdgeColor { get; set; } = new(0.20f, 0.80f, 0.40f, 0.80f);

    public Vector4 EdgeHighlightColor { get; set; } = new(1.00f, 0.78f, 0.20f, 1.00f);

    public Vector4 CapColor { get; set; } = new(0.85f, 0.85f, 0.80f, 1.00f);

    public void Render(
        float[] view,
        float[] projection,
        float sceneDiagonal,
        IReadOnlyList<GlesSectionVisualPlane> planes,
        bool fillVisible,
        bool edgesVisible,
        IReadOnlyList<Vector3> committedPicks,
        Vector3? hoverPoint)
    {
        float planeHalfSide = ResolvePlaneHalfSide(sceneDiagonal);

        if (fillVisible && planes.Count > 0)
        {
            _data.Clear();
            foreach (GlesSectionVisualPlane plane in planes)
                AppendPlaneQuad(_data, plane, FillColor, planeHalfSide);
            Draw(view, projection, PrimitiveType.Triangles, depthTest: true, blend: true, lineWidth: 1.0f, pointSize: 1.0f);
        }

        if (edgesVisible && planes.Count > 0)
        {
            _data.Clear();
            foreach (GlesSectionVisualPlane plane in planes)
                AppendPlaneEdges(_data, plane, plane.Selected ? EdgeHighlightColor : EdgeColor, planeHalfSide);
            Draw(view, projection, PrimitiveType.Lines, depthTest: true, blend: true, lineWidth: 2.0f, pointSize: 1.0f);
        }

        if (committedPicks.Count > 0 || hoverPoint is not null)
        {
            _data.Clear();
            AppendPlacementPreview(_data, committedPicks, hoverPoint);
            if (_data.Count > 0)
                Draw(view, projection, PrimitiveType.Lines, depthTest: false, blend: true, lineWidth: 2.0f, pointSize: 1.0f);
        }

        if (hoverPoint is { } hp)
        {
            _data.Clear();
            AppendPoint(_data, hp, HoverColor);
            Draw(view, projection, PrimitiveType.Points, depthTest: false, blend: true, lineWidth: 1.0f, pointSize: 13.0f, roundPoints: true);
        }
    }

    public void RenderCapPlane(
        float[] view,
        float[] projection,
        GlesSectionVisualPlane plane,
        float sceneDiagonal)
    {
        _data.Clear();
        AppendPlaneQuad(_data, plane, CapColor, ResolveCapHalfSide(sceneDiagonal));
        Draw(view, projection, PrimitiveType.Triangles, depthTest: true, blend: false, lineWidth: 1.0f, pointSize: 1.0f);
    }

    public void RenderGizmo(
        float[] view,
        float[] projection,
        Vector3 anchor,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        float scale,
        GlesTransformGizmoHandle hovered,
        GlesTransformGizmoHandle active)
    {
        if (scale <= 0f
            || !TryNormalize(axisX, out axisX)
            || !TryNormalize(axisY, out axisY)
            || !TryNormalize(axisZ, out axisZ))
        {
            return;
        }

        float arrowLength = scale * GizmoArrowLengthScale;
        float arrowWidth = scale * GizmoArrowWidthScale;
        float arcRadius = scale * GizmoArcRadiusScale;

        _data.Clear();
        AppendGizmoArcFill(_data, anchor, axisY, axisZ, arcRadius, GizmoArcXColor);
        AppendGizmoArcFill(_data, anchor, axisZ, axisX, arcRadius, GizmoArcYColor);
        AppendGizmoArcFill(_data, anchor, axisX, axisY, arcRadius, GizmoArcZColor);
        AppendGizmoArrowCone(
            _data,
            anchor + axisX * scale,
            axisX,
            arrowLength,
            arrowWidth,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateX, hovered, active, GizmoColorX));
        AppendGizmoArrowCone(
            _data,
            anchor + axisY * scale,
            axisY,
            arrowLength,
            arrowWidth,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateY, hovered, active, GizmoColorY));
        AppendGizmoArrowCone(
            _data,
            anchor + axisZ * scale,
            axisZ,
            arrowLength,
            arrowWidth,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateZ, hovered, active, GizmoColorZ));
        Draw(view, projection, PrimitiveType.Triangles, depthTest: false, blend: true, lineWidth: 1.0f, pointSize: 1.0f);

        _data.Clear();
        AppendGizmoAxisShaft(
            _data,
            anchor,
            axisX,
            scale,
            arrowLength,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateX, hovered, active, GizmoColorX));
        AppendGizmoAxisShaft(
            _data,
            anchor,
            axisY,
            scale,
            arrowLength,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateY, hovered, active, GizmoColorY));
        AppendGizmoAxisShaft(
            _data,
            anchor,
            axisZ,
            scale,
            arrowLength,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateZ, hovered, active, GizmoColorZ));
        AppendGizmoArcOutline(
            _data,
            anchor,
            axisY,
            axisZ,
            arcRadius,
            GizmoColorFor(GlesTransformGizmoHandle.RotateX, hovered, active, new Vector4(GizmoArcXColor.X, GizmoArcXColor.Y, GizmoArcXColor.Z, 1.0f)));
        AppendGizmoArcOutline(
            _data,
            anchor,
            axisZ,
            axisX,
            arcRadius,
            GizmoColorFor(GlesTransformGizmoHandle.RotateY, hovered, active, new Vector4(GizmoArcYColor.X, GizmoArcYColor.Y, GizmoArcYColor.Z, 1.0f)));
        AppendGizmoArcOutline(
            _data,
            anchor,
            axisX,
            axisY,
            arcRadius,
            GizmoColorFor(GlesTransformGizmoHandle.RotateZ, hovered, active, new Vector4(GizmoArcZColor.X, GizmoArcZColor.Y, GizmoArcZColor.Z, 1.0f)));
        Draw(view, projection, PrimitiveType.Lines, depthTest: false, blend: true, lineWidth: 2.4f, pointSize: 1.0f);
    }

    private unsafe void EnsureBuffers()
    {
        if (_vao != 0 && _vbo != 0)
            return;

        DeleteBuffers();

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, StrideBytes, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    private unsafe void Draw(
        float[] view,
        float[] projection,
        PrimitiveType primitive,
        bool depthTest,
        bool blend,
        float lineWidth,
        float pointSize,
        bool roundPoints = false)
    {
        int vertexCount = _data.Count / FloatsPerVertex;
        if (vertexCount == 0)
            return;

        EnsureBuffers();

        _program.Use();
        SetMat4("uView", view);
        SetMat4("uProjection", projection);
        SetFloat("uPointSize", _primitiveLimits.ClampPointSize(pointSize));
        SetInt("uRoundPoints", roundPoints ? 1 : 0);

        if (depthTest)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
        }
        else
        {
            _gl.Disable(EnableCap.DepthTest);
        }

        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        if (blend)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        else
        {
            _gl.Disable(EnableCap.Blend);
        }

        _gl.LineWidth(_primitiveLimits.ClampLineWidth(lineWidth));
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        Span<float> span = CollectionsMarshal.AsSpan(_data);
        fixed (float* p = span)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(span.Length * sizeof(float)),
                p,
                BufferUsageARB.DynamicDraw);
        }
        _gl.DrawArrays(primitive, 0, (uint)vertexCount);
        _gl.BindVertexArray(0);

        _gl.LineWidth(_primitiveLimits.ClampLineWidth(1.0f));
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
    }

    private void SetMat4(string name, float[] matrix)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, true, matrix);
    }

    private void SetFloat(string name, float value)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value);
    }

    private void SetInt(string name, int value)
    {
        int loc = _program.UniformLocation(name);
        if (loc < 0) return;
        _gl.Uniform1(loc, value);
    }

    private float ResolvePlaneHalfSide(float sceneDiagonal)
        => Math.Clamp(sceneDiagonal * Math.Clamp(PlaneSizeFraction, 0.001f, 1.0f), MinExtent, MaxPlaneExtent);

    private static float ResolveCapHalfSide(float sceneDiagonal)
        => Math.Clamp(sceneDiagonal, MinExtent, MaxCapExtent);

    private static void AppendPlaneQuad(List<float> data, GlesSectionVisualPlane plane, Vector4 color, float extent)
    {
        Vector3 c0 = plane.Anchor - plane.AxisX * extent - plane.AxisY * extent;
        Vector3 c1 = plane.Anchor + plane.AxisX * extent - plane.AxisY * extent;
        Vector3 c2 = plane.Anchor + plane.AxisX * extent + plane.AxisY * extent;
        Vector3 c3 = plane.Anchor - plane.AxisX * extent + plane.AxisY * extent;
        AppendVertex(data, c0, color); AppendVertex(data, c1, color); AppendVertex(data, c2, color);
        AppendVertex(data, c0, color); AppendVertex(data, c2, color); AppendVertex(data, c3, color);
    }

    private static void AppendPlaneEdges(List<float> data, GlesSectionVisualPlane plane, Vector4 color, float extent)
    {
        Vector3 c0 = plane.Anchor - plane.AxisX * extent - plane.AxisY * extent;
        Vector3 c1 = plane.Anchor + plane.AxisX * extent - plane.AxisY * extent;
        Vector3 c2 = plane.Anchor + plane.AxisX * extent + plane.AxisY * extent;
        Vector3 c3 = plane.Anchor - plane.AxisX * extent + plane.AxisY * extent;
        AppendLine(data, c0, c1, color);
        AppendLine(data, c1, c2, color);
        AppendLine(data, c2, c3, color);
        AppendLine(data, c3, c0, color);
    }

    private static void AppendPlacementPreview(List<float> data, IReadOnlyList<Vector3> committedPicks, Vector3? hoverPoint)
    {
        for (int i = 0; i + 1 < committedPicks.Count; i++)
            AppendLine(data, committedPicks[i], committedPicks[i + 1], PreviewColor);

        if (committedPicks.Count > 0 && hoverPoint is { } hp)
            AppendLine(data, committedPicks[^1], hp, PreviewColor);
    }

    private static void AppendLine(List<float> data, Vector3 a, Vector3 b, Vector4 color)
    {
        AppendVertex(data, a, color);
        AppendVertex(data, b, color);
    }

    private static void AppendPoint(List<float> data, Vector3 p, Vector4 color)
        => AppendVertex(data, p, color);

    private static void AppendGizmoAxisShaft(List<float> data, Vector3 anchor, Vector3 direction, float length, float arrowLength, Vector4 color)
    {
        Vector3 tip = anchor + direction * length;
        Vector3 baseEnd = tip - direction * arrowLength;
        AppendLine(data, anchor, baseEnd, color);
    }

    private static void AppendGizmoArrowCone(List<float> data, Vector3 tip, Vector3 direction, float length, float radius, Vector4 color)
    {
        if (!TryNormalize(direction, out Vector3 axis))
            return;

        Vector3 reference = MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) < 0.95f
            ? Vector3.UnitY
            : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(axis, reference));
        Vector3 bitangent = Vector3.Cross(axis, tangent);
        Vector3 basePoint = tip - axis * length;
        float twoPi = MathF.PI * 2.0f;

        for (int i = 0; i < GizmoArrowConeSegments; i++)
        {
            float t0 = i / (float)GizmoArrowConeSegments * twoPi;
            float t1 = (i + 1) / (float)GizmoArrowConeSegments * twoPi;
            Vector3 r0 = basePoint + (tangent * MathF.Cos(t0) + bitangent * MathF.Sin(t0)) * radius;
            Vector3 r1 = basePoint + (tangent * MathF.Cos(t1) + bitangent * MathF.Sin(t1)) * radius;
            AppendVertex(data, tip, color);
            AppendVertex(data, r0, color);
            AppendVertex(data, r1, color);
        }
    }

    private static void AppendGizmoArcFill(List<float> data, Vector3 center, Vector3 right, Vector3 up, float radius, Vector4 color)
    {
        float quarter = MathF.PI * 0.5f;
        Vector3 previous = center + right * radius;
        for (int i = 1; i <= GizmoArcSegments; i++)
        {
            float a = i / (float)GizmoArcSegments * quarter;
            Vector3 current = center + (right * MathF.Cos(a) + up * MathF.Sin(a)) * radius;
            AppendVertex(data, center, color);
            AppendVertex(data, previous, color);
            AppendVertex(data, current, color);
            previous = current;
        }
    }

    private static void AppendGizmoArcOutline(List<float> data, Vector3 center, Vector3 right, Vector3 up, float radius, Vector4 color)
    {
        float quarter = MathF.PI * 0.5f;
        Vector3 previous = center + right * radius;
        for (int i = 1; i <= GizmoArcSegments; i++)
        {
            float a = i / (float)GizmoArcSegments * quarter;
            Vector3 current = center + (right * MathF.Cos(a) + up * MathF.Sin(a)) * radius;
            AppendLine(data, previous, current, color);
            previous = current;
        }
    }

    private static Vector4 GizmoColorFor(
        GlesTransformGizmoHandle handle,
        GlesTransformGizmoHandle hovered,
        GlesTransformGizmoHandle active,
        Vector4 baseColor)
        => handle == active
            ? GizmoActiveColor
            : handle == hovered
                ? GizmoHoverColor
                : baseColor;

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
        {
            normalized = default;
            return false;
        }

        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }

    private static void AppendVertex(List<float> data, Vector3 position, Vector4 color)
    {
        data.Add(position.X);
        data.Add(position.Y);
        data.Add(position.Z);
        data.Add(color.X);
        data.Add(color.Y);
        data.Add(color.Z);
        data.Add(color.W);
    }

    public void Dispose()
    {
        DeleteBuffers();
        _program.Dispose();
    }

    private void DeleteBuffers()
    {
        if (_vbo != 0)
        {
            _gl.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_vao != 0)
        {
            _gl.DeleteVertexArray(_vao);
            _vao = 0;
        }
    }
}
