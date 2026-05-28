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

    public Vector4 FillColor { get; set; }

    public Vector4 EdgeColor { get; set; }

    public Vector4 EdgeHighlightColor { get; set; }

    public Vector4 CapColor { get; set; }

    public Vector4 PlacementPreviewColor { get; set; }

    public Vector4 PlacementHoverColor { get; set; }

    public Vector4 GizmoAxisXColor { get; set; }

    public Vector4 GizmoAxisYColor { get; set; }

    public Vector4 GizmoAxisZColor { get; set; }

    public Vector4 GizmoArcXColor { get; set; }

    public Vector4 GizmoArcYColor { get; set; }

    public Vector4 GizmoArcZColor { get; set; }

    public Vector4 GizmoHoverColor { get; set; }

    public Vector4 GizmoActiveColor { get; set; }

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
            AppendPlacementPreview(_data, committedPicks, hoverPoint, PlacementPreviewColor);
            if (_data.Count > 0)
                Draw(view, projection, PrimitiveType.Lines, depthTest: false, blend: true, lineWidth: 2.0f, pointSize: 1.0f);
        }

        if (hoverPoint is { } hp)
        {
            _data.Clear();
            AppendPoint(_data, hp, PlacementHoverColor);
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

    public void RenderCapMaskTriangles(
        float[] view,
        float[] projection,
        float[] triangleVertices)
    {
        ArgumentNullException.ThrowIfNull(triangleVertices);
        if (triangleVertices.Length == 0)
            return;

        _data.Clear();
        for (int i = 0; i + 2 < triangleVertices.Length; i += 3)
        {
            AppendVertex(
                _data,
                new Vector3(triangleVertices[i], triangleVertices[i + 1], triangleVertices[i + 2]),
                Vector4.Zero);
        }

        Draw(view, projection, PrimitiveType.Triangles, depthTest: false, blend: false, lineWidth: 1.0f, pointSize: 1.0f);
    }

    public void RenderLineVertices(
        float[] view,
        float[] projection,
        float[] lineVertices,
        Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(lineVertices);
        if (lineVertices.Length == 0)
            return;

        _data.Clear();
        for (int i = 0; i + 2 < lineVertices.Length; i += 3)
        {
            AppendVertex(
                _data,
                new Vector3(lineVertices[i], lineVertices[i + 1], lineVertices[i + 2]),
                color);
        }

        Draw(view, projection, PrimitiveType.Lines, depthTest: false, blend: true, lineWidth: 2.4f, pointSize: 1.0f);
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
            GizmoColorFor(GlesTransformGizmoHandle.TranslateX, hovered, active, GizmoAxisXColor));
        AppendGizmoArrowCone(
            _data,
            anchor + axisY * scale,
            axisY,
            arrowLength,
            arrowWidth,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateY, hovered, active, GizmoAxisYColor));
        AppendGizmoArrowCone(
            _data,
            anchor + axisZ * scale,
            axisZ,
            arrowLength,
            arrowWidth,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateZ, hovered, active, GizmoAxisZColor));
        Draw(view, projection, PrimitiveType.Triangles, depthTest: false, blend: true, lineWidth: 1.0f, pointSize: 1.0f);

        _data.Clear();
        AppendGizmoAxisShaft(
            _data,
            anchor,
            axisX,
            scale,
            arrowLength,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateX, hovered, active, GizmoAxisXColor));
        AppendGizmoAxisShaft(
            _data,
            anchor,
            axisY,
            scale,
            arrowLength,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateY, hovered, active, GizmoAxisYColor));
        AppendGizmoAxisShaft(
            _data,
            anchor,
            axisZ,
            scale,
            arrowLength,
            GizmoColorFor(GlesTransformGizmoHandle.TranslateZ, hovered, active, GizmoAxisZColor));
        AppendGizmoArcOutline(
            _data,
            anchor,
            axisY,
            axisZ,
            arcRadius,
            GizmoColorFor(GlesTransformGizmoHandle.RotateX, hovered, active, WithAlpha(GizmoArcXColor, 1.0f)));
        AppendGizmoArcOutline(
            _data,
            anchor,
            axisZ,
            axisX,
            arcRadius,
            GizmoColorFor(GlesTransformGizmoHandle.RotateY, hovered, active, WithAlpha(GizmoArcYColor, 1.0f)));
        AppendGizmoArcOutline(
            _data,
            anchor,
            axisX,
            axisY,
            arcRadius,
            GizmoColorFor(GlesTransformGizmoHandle.RotateZ, hovered, active, WithAlpha(GizmoArcZColor, 1.0f)));
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
        try
        {
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
        }
        finally
        {
            _gl.BindVertexArray(0);
            _gl.LineWidth(_primitiveLimits.ClampLineWidth(1.0f));
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
        }
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

    private static void AppendPlacementPreview(
        List<float> data,
        IReadOnlyList<Vector3> committedPicks,
        Vector3? hoverPoint,
        Vector4 color)
    {
        for (int i = 0; i + 1 < committedPicks.Count; i++)
            AppendLine(data, committedPicks[i], committedPicks[i + 1], color);

        if (committedPicks.Count > 0 && hoverPoint is { } hp)
            AppendLine(data, committedPicks[^1], hp, color);
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

    private Vector4 GizmoColorFor(
        GlesTransformGizmoHandle handle,
        GlesTransformGizmoHandle hovered,
        GlesTransformGizmoHandle active,
        Vector4 baseColor)
        => handle == active
            ? GizmoActiveColor
            : handle == hovered
                ? GizmoHoverColor
                : baseColor;

    private static Vector4 WithAlpha(Vector4 color, float alpha)
        => new(color.X, color.Y, color.Z, alpha);

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
