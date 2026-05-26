using System.Numerics;
using System.Runtime.InteropServices;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal sealed class GlesAxisTriadOverlay : IDisposable
{
    private const int FloatsPerVertex = 6;
    private const int StrideBytes = FloatsPerVertex * sizeof(float);
    private const int DiskSegments = 18;
    private const float MinProjectedAxisScale = 0.28f;

    private static readonly Vector4 XColor = new(1.00f, 0.30f, 0.30f, 1.00f);
    private static readonly Vector4 YColor = new(0.30f, 1.00f, 0.30f, 1.00f);
    private static readonly Vector4 ZColor = new(0.40f, 0.62f, 1.00f, 1.00f);
    private static readonly Vector4 OriginColor = new(0.78f, 0.78f, 0.78f, 1.00f);

    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private readonly List<float> _data = new(384);
    private readonly int[] _blendSrcRgb = new int[1];
    private readonly int[] _blendSrcAlpha = new int[1];
    private readonly int[] _blendDstRgb = new int[1];
    private readonly int[] _blendDstAlpha = new int[1];
    private uint _vao;
    private uint _vbo;
    private int _renderWidth;
    private int _renderHeight;

    public GlesAxisTriadOverlay(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(gl, "axis.triad", vertexSource, fragmentSource);
        CreateBuffers();
    }

    public void Render(CameraState camera, int width, int height)
    {
        if (camera is null || width <= 0 || height <= 0)
            return;

        if (!TryBuildCameraBasis(camera, out Vector3d viewDir, out Vector3d right, out Vector3d up))
            return;

        _renderWidth = width;
        _renderHeight = height;

        float minDim = MathF.Min(width, height);
        float size = Math.Clamp(minDim * 0.12f, 96.0f, 180.0f);
        size = MathF.Min(size, MathF.Max(48.0f, minDim - 24.0f));
        float margin = Math.Clamp(size * 0.16f, 12.0f, 26.0f);
        Vector2 origin = new(margin + size * 0.5f, margin + size * 0.5f);
        float axisLength = size * 0.35f;
        float axisWidth = Math.Clamp(size * 0.026f, 2.4f, 4.8f);
        float arrowLength = size * 0.095f;
        float arrowHalfWidth = size * 0.050f;
        float labelOffset = size * 0.105f;
        float labelHalfSize = size * 0.043f;
        float labelStroke = Math.Clamp(size * 0.014f, 1.4f, 3.0f);
        float originRadius = size * 0.045f;

        Span<AxisDraw> axes =
        [
            CreateAxis(Vector3d.UnitX, XColor, AxisLabel.X, viewDir, right, up, axisLength),
            CreateAxis(Vector3d.UnitY, YColor, AxisLabel.Y, viewDir, right, up, axisLength),
            CreateAxis(Vector3d.UnitZ, ZColor, AxisLabel.Z, viewDir, right, up, axisLength),
        ];
        axes.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        _data.Clear();
        foreach (AxisDraw axis in axes)
            AppendAxis(origin, axis, axisWidth, arrowLength, arrowHalfWidth, labelOffset, labelHalfSize, labelStroke);
        AppendDisk(origin, originRadius, OriginColor);

        Draw();
    }

    private unsafe void CreateBuffers()
    {
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, StrideBytes, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, StrideBytes, (void*)(2 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    private unsafe void Draw()
    {
        int vertexCount = _data.Count / FloatsPerVertex;
        if (vertexCount == 0)
            return;

        if (_vao == 0 || _vbo == 0)
            CreateBuffers();

        _gl.GetInteger(GLEnum.BlendSrcRgb, _blendSrcRgb);
        _gl.GetInteger(GLEnum.BlendSrcAlpha, _blendSrcAlpha);
        _gl.GetInteger(GLEnum.BlendDstRgb, _blendDstRgb);
        _gl.GetInteger(GLEnum.BlendDstAlpha, _blendDstAlpha);

        try
        {
            _program.Use();

            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

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

            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertexCount);
            _gl.BindVertexArray(0);
        }
        finally
        {
            _gl.BlendFuncSeparate(
                (BlendingFactor)_blendSrcRgb[0],
                (BlendingFactor)_blendDstRgb[0],
                (BlendingFactor)_blendSrcAlpha[0],
                (BlendingFactor)_blendDstAlpha[0]);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
        }
    }

    private AxisDraw CreateAxis(
        Vector3d worldAxis,
        Vector4 color,
        AxisLabel label,
        Vector3d viewDir,
        Vector3d right,
        Vector3d up,
        float axisLength)
    {
        Vector2 screen = new(
            (float)Vector3d.Dot(worldAxis, right),
            (float)Vector3d.Dot(worldAxis, up));
        float projectedLength = screen.Length();
        Vector2 direction = projectedLength > 1e-5f
            ? screen / projectedLength
            : new Vector2(0.0f, 1.0f);
        float length = axisLength * Math.Clamp(projectedLength, MinProjectedAxisScale, 1.0f);
        float depth = (float)Vector3d.Dot(worldAxis, viewDir);
        return new AxisDraw(direction, length, depth, color, label);
    }

    private void AppendAxis(
        Vector2 origin,
        AxisDraw axis,
        float axisWidth,
        float arrowLength,
        float arrowHalfWidth,
        float labelOffset,
        float labelHalfSize,
        float labelStroke)
    {
        Vector2 tip = origin + axis.Direction * axis.Length;
        float effectiveArrowLength = MathF.Min(arrowLength, axis.Length * 0.55f);
        Vector2 arrowBase = tip - axis.Direction * effectiveArrowLength;
        AppendThickSegment(origin, arrowBase, axisWidth, axis.Color);

        Vector2 arrowNormal = new Vector2(-axis.Direction.Y, axis.Direction.X) * arrowHalfWidth;
        AppendTriangle(tip, arrowBase + arrowNormal, arrowBase - arrowNormal, axis.Color);

        Vector2 labelCenter = tip + axis.Direction * labelOffset;
        AppendLabel(axis.Label, labelCenter, labelHalfSize, labelStroke, axis.Color);
    }

    private void AppendLabel(AxisLabel label, Vector2 center, float halfSize, float strokeWidth, Vector4 color)
    {
        switch (label)
        {
            case AxisLabel.X:
                AppendThickSegment(center + new Vector2(-halfSize, -halfSize), center + new Vector2(halfSize, halfSize), strokeWidth, color);
                AppendThickSegment(center + new Vector2(-halfSize, halfSize), center + new Vector2(halfSize, -halfSize), strokeWidth, color);
                break;
            case AxisLabel.Y:
                AppendThickSegment(center + new Vector2(-halfSize, halfSize), center, strokeWidth, color);
                AppendThickSegment(center + new Vector2(halfSize, halfSize), center, strokeWidth, color);
                AppendThickSegment(center, center + new Vector2(0.0f, -halfSize), strokeWidth, color);
                break;
            case AxisLabel.Z:
                AppendThickSegment(center + new Vector2(-halfSize, halfSize), center + new Vector2(halfSize, halfSize), strokeWidth, color);
                AppendThickSegment(center + new Vector2(halfSize, halfSize), center + new Vector2(-halfSize, -halfSize), strokeWidth, color);
                AppendThickSegment(center + new Vector2(-halfSize, -halfSize), center + new Vector2(halfSize, -halfSize), strokeWidth, color);
                break;
        }
    }

    private void AppendDisk(Vector2 center, float radius, Vector4 color)
    {
        float twoPi = MathF.PI * 2.0f;
        for (int i = 0; i < DiskSegments; i++)
        {
            float a0 = i / (float)DiskSegments * twoPi;
            float a1 = (i + 1) / (float)DiskSegments * twoPi;
            AppendTriangle(
                center,
                center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius,
                center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius,
                color);
        }
    }

    private void AppendThickSegment(Vector2 a, Vector2 b, float width, Vector4 color)
    {
        Vector2 d = b - a;
        float length = d.Length();
        if (length < 0.01f)
            return;

        Vector2 normal = new Vector2(-d.Y, d.X) / length * (width * 0.5f);
        Vector2 a0 = a + normal;
        Vector2 a1 = a - normal;
        Vector2 b0 = b + normal;
        Vector2 b1 = b - normal;
        AppendTriangle(a0, b0, b1, color);
        AppendTriangle(a0, b1, a1, color);
    }

    private void AppendTriangle(Vector2 a, Vector2 b, Vector2 c, Vector4 color)
    {
        AppendVertex(a, color);
        AppendVertex(b, color);
        AppendVertex(c, color);
    }

    private void AppendVertex(Vector2 p, Vector4 color)
    {
        float x = (p.X / _renderWidth) * 2.0f - 1.0f;
        float y = (p.Y / _renderHeight) * 2.0f - 1.0f;
        _data.Add(x);
        _data.Add(y);
        _data.Add(color.X);
        _data.Add(color.Y);
        _data.Add(color.Z);
        _data.Add(color.W);
    }

    private static bool TryBuildCameraBasis(
        CameraState camera,
        out Vector3d viewDir,
        out Vector3d right,
        out Vector3d up)
    {
        viewDir = camera.Target - camera.Position;
        if (!TryNormalize(viewDir, out viewDir))
            viewDir = -Vector3d.UnitY;

        up = camera.UpDirection;
        if (!TryNormalize(up, out up))
            up = Vector3d.UnitZ;

        right = Vector3d.Cross(viewDir, up);
        if (!TryNormalize(right, out right))
        {
            up = Math.Abs(Vector3d.Dot(viewDir, Vector3d.UnitZ)) < 0.95
                ? Vector3d.UnitZ
                : Vector3d.UnitY;
            right = Vector3d.Cross(viewDir, up);
            if (!TryNormalize(right, out right))
                return false;
        }

        up = Vector3d.Cross(right, viewDir);
        return TryNormalize(up, out up);
    }

    private static bool TryNormalize(Vector3d value, out Vector3d normalized)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z))
        {
            normalized = Vector3d.Zero;
            return false;
        }

        normalized = value.Normalized();
        return normalized.LengthSquared > 1e-12;
    }

    public void Dispose()
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

        _program.Dispose();
    }

    private readonly record struct AxisDraw(Vector2 Direction, float Length, float Depth, Vector4 Color, AxisLabel Label);

    private enum AxisLabel
    {
        X,
        Y,
        Z,
    }
}
