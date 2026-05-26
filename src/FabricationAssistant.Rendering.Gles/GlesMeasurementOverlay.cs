using System.Runtime.InteropServices;
using System.Numerics;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Presentation;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal sealed class GlesMeasurementOverlay : IDisposable
{
    private const int FloatsPerVertex = 7;
    private const int StrideBytes = FloatsPerVertex * sizeof(float);
    private const int DiskFloatsPerVertex = 15;
    private const int DiskStrideBytes = DiskFloatsPerVertex * sizeof(float);
    private const float BallPixelSize = 15.0f;
    private const float DiskPixelRadius = 16.0f;
    private const float DiskFillAlpha = 0.30f;
    private const float DiskRingAlpha = 0.95f;
    private static readonly float[,] QuadCorners =
    {
        { -1f, -1f }, { 1f, -1f }, { 1f, 1f },
        { -1f, -1f }, { 1f, 1f }, { -1f, 1f },
    };

    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private readonly ShaderProgram _diskProgram;
    private readonly GlesPrimitiveLimits _primitiveLimits;
    private uint _vao;
    private uint _vbo;
    private uint _diskVao;
    private uint _diskVbo;
    private string _lastRenderLogKey = "";
    private readonly List<float> _lineData = new();
    private readonly List<float> _pointData = new();
    private readonly List<float> _diskData = new();
    private readonly float[] _mvpScratch = new float[16];
    private readonly float[] _diskViewScratch = new float[16];

    public GlesMeasurementOverlay(
        GL gl,
        string vertexSource,
        string fragmentSource,
        string diskVertexSource,
        string diskFragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(gl, "measure.overlay", vertexSource, fragmentSource);
        _diskProgram = new ShaderProgram(gl, "measure.disk", diskVertexSource, diskFragmentSource);
        _primitiveLimits = GlesRenderUtil.QueryPrimitiveLimits(gl);
        CreateBuffers();
        CreateDiskBuffers();
    }

    public Vector3 DimensionHighlightColor { get; set; } = new(1.0f, 0.5019608f, 0.2509804f);

    public void Render(
        float[] view,
        float[] projection,
        int viewportHeight,
        Vector3d diskOrigin,
        IReadOnlyList<PresentationSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        if (!IsFinite(diskOrigin))
            diskOrigin = Vector3d.Zero;

        _lineData.Clear();
        _pointData.Clear();
        _diskData.Clear();

        foreach (PresentationSnapshot snapshot in snapshots)
        {
            Color4 color = ColorFor(snapshot.Style);
            foreach (LinePrimitive line in snapshot.Lines)
                AddLine(_lineData, line.Start, line.End, color);

            foreach (DiskPrimitive disk in snapshot.Disks)
                AppendDisk(_diskData, disk, color, diskOrigin);

            foreach (BallPrimitive ball in snapshot.Balls)
                AddPoint(_pointData, ball.Center, color);
        }

        if (_lineData.Count == 0 && _pointData.Count == 0 && _diskData.Count == 0)
        {
            LogRender(snapshots.Count, 0, 0, 0);
            return;
        }

        int lineVertices = _lineData.Count / FloatsPerVertex;
        int pointVertices = _pointData.Count / FloatsPerVertex;
        int diskVertices = _diskData.Count / DiskFloatsPerVertex;
        LogRender(snapshots.Count, lineVertices, pointVertices, diskVertices);

        _program.Use();
        SetMat4("uView", view);
        SetMat4("uProjection", projection);

        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        if (_lineData.Count > 0)
        {
            SetFloat("uPointSize", _primitiveLimits.ClampPointSize(1.0f));
            SetInt("uRoundPoints", 0);
            _gl.LineWidth(_primitiveLimits.ClampLineWidth(2.0f));
            UploadAndDraw(_lineData, PrimitiveType.Lines);
        }

        if (_diskData.Count > 0)
        {
            _diskProgram.Use();
            SetDiskMat4("uMVP", MultiplyProjectionView(
                projection,
                ViewMatrixTranslatedByOrigin(view, diskOrigin, _diskViewScratch),
                _mvpScratch));
            float projectionY = MathF.Abs(projection.Length > 5 ? projection[5] : 0f);
            float worldPerPixelFactor = projectionY > 0.000001f && viewportHeight > 0
                ? 2.0f / (projectionY * viewportHeight)
                : 0.0f;
            worldPerPixelFactor = float.IsFinite(worldPerPixelFactor)
                ? Math.Clamp(worldPerPixelFactor, 0.0f, 1.0e6f)
                : 0.0f;
            SetDiskFloat("uWorldPerPixelFactor", worldPerPixelFactor);
            SetDiskFloat("uPixelRadius", DiskPixelRadius);
            SetDiskFloat("uFillAlpha", DiskFillAlpha);
            SetDiskFloat("uRingAlpha", DiskRingAlpha);
            UploadAndDrawDisk(_diskData);
            _program.Use();
        }

        if (_pointData.Count > 0)
        {
            SetFloat("uPointSize", _primitiveLimits.ClampPointSize(BallPixelSize));
            SetInt("uRoundPoints", 1);
            UploadAndDraw(_pointData, PrimitiveType.Points);
        }

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
    }

    private unsafe void CreateBuffers()
    {
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

    private unsafe void CreateDiskBuffers()
    {
        _diskVao = _gl.GenVertexArray();
        _diskVbo = _gl.GenBuffer();

        _gl.BindVertexArray(_diskVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _diskVbo);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, DiskStrideBytes, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, DiskStrideBytes, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, DiskStrideBytes, (void*)(7 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, DiskStrideBytes, (void*)(10 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, DiskStrideBytes, (void*)(13 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    private unsafe void UploadAndDraw(List<float> data, PrimitiveType primitive)
    {
        int vertexCount = data.Count / FloatsPerVertex;
        if (vertexCount == 0)
            return;

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        Span<float> span = CollectionsMarshal.AsSpan(data);
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
    }

    private unsafe void UploadAndDrawDisk(List<float> data)
    {
        int vertexCount = data.Count / DiskFloatsPerVertex;
        if (vertexCount == 0)
            return;

        _gl.BindVertexArray(_diskVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _diskVbo);
        Span<float> span = CollectionsMarshal.AsSpan(data);
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

    private void SetMat4(string name, float[] matrix)
    {
        int loc = _program.UniformLocation(name);
        if (loc >= 0) _gl.UniformMatrix4(loc, true, matrix);
    }

    private void SetFloat(string name, float value)
    {
        int loc = _program.UniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    private void SetInt(string name, int value)
    {
        int loc = _program.UniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    private void SetDiskMat4(string name, float[] matrix)
    {
        int loc = _diskProgram.UniformLocation(name);
        if (loc >= 0) _gl.UniformMatrix4(loc, true, matrix);
    }

    private void SetDiskFloat(string name, float value)
    {
        int loc = _diskProgram.UniformLocation(name);
        if (loc >= 0) _gl.Uniform1(loc, value);
    }

    private static void AddLine(List<float> data, Vector3d start, Vector3d end, Color4 color)
    {
        if (!IsFinite(start) || !IsFinite(end))
            return;

        AddVertex(data, start, color);
        AddVertex(data, end, color);
    }

    private static void AddPoint(List<float> data, Vector3d center, Color4 color)
    {
        if (!IsFinite(center))
            return;

        AddVertex(data, center, color);
    }

    private static void AppendDisk(List<float> data, DiskPrimitive disk, Color4 color, Vector3d origin)
    {
        if (!IsFinite(disk.Center) || !TryBuildDiskBasis(disk, out Vector3d u, out Vector3d v))
            return;

        Vector3d localCenter = disk.Center - origin;
        for (int i = 0; i < QuadCorners.GetLength(0); i++)
        {
            data.Add((float)localCenter.X);
            data.Add((float)localCenter.Y);
            data.Add((float)localCenter.Z);
            data.Add(color.R);
            data.Add(color.G);
            data.Add(color.B);
            data.Add(color.A);
            data.Add((float)u.X);
            data.Add((float)u.Y);
            data.Add((float)u.Z);
            data.Add((float)v.X);
            data.Add((float)v.Y);
            data.Add((float)v.Z);
            data.Add(QuadCorners[i, 0]);
            data.Add(QuadCorners[i, 1]);
        }
    }

    private static void AddVertex(List<float> data, Vector3d p, Color4 color)
    {
        data.Add((float)p.X);
        data.Add((float)p.Y);
        data.Add((float)p.Z);
        data.Add(color.R);
        data.Add(color.G);
        data.Add(color.B);
        data.Add(color.A);
    }

    private static bool IsFinite(Vector3d value)
        => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static bool TryBuildDiskBasis(DiskPrimitive disk, out Vector3d u, out Vector3d v)
    {
        u = default;
        v = default;
        if (!IsFinite(disk.Normal) || !IsFinite(disk.U))
            return false;

        Vector3d n = disk.Normal;
        if (n.LengthSquared < 1e-12)
            return false;
        n = n.Normalized();

        u = disk.U.LengthSquared > 1e-12 ? disk.U.Normalized() : FallbackU(n);
        u -= n * Vector3d.Dot(u, n);
        if (u.LengthSquared < 1e-12)
            u = FallbackU(n);
        u = u.Normalized();
        v = Vector3d.Cross(n, u);
        if (v.LengthSquared < 1e-12)
            return false;
        v = v.Normalized();
        return true;
    }

    private static float[] ViewMatrixTranslatedByOrigin(float[] view, Vector3d origin, float[] result)
    {
        Array.Copy(view, result, Math.Min(view.Length, result.Length));
        if (view.Length < 16 || result.Length < 16)
            return result;

        result[3] = (float)(view[0] * origin.X + view[1] * origin.Y + view[2] * origin.Z + view[3]);
        result[7] = (float)(view[4] * origin.X + view[5] * origin.Y + view[6] * origin.Z + view[7]);
        result[11] = (float)(view[8] * origin.X + view[9] * origin.Y + view[10] * origin.Z + view[11]);
        result[15] = (float)(view[12] * origin.X + view[13] * origin.Y + view[14] * origin.Z + view[15]);
        return result;
    }

    private static Vector3d FallbackU(Vector3d normal)
        => System.Math.Abs(normal.Y) < 0.9
            ? Vector3d.Cross(normal, Vector3d.UnitY).Normalized()
            : Vector3d.Cross(normal, Vector3d.UnitX).Normalized();

    private static float[] MultiplyProjectionView(float[] projection, float[] view, float[] result)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                result[row * 4 + col] =
                    projection[row * 4 + 0] * view[0 * 4 + col] +
                    projection[row * 4 + 1] * view[1 * 4 + col] +
                    projection[row * 4 + 2] * view[2 * 4 + col] +
                    projection[row * 4 + 3] * view[3 * 4 + col];
            }
        }

        return result;
    }

    private Color4 ColorFor(PresentationStyle style) => style switch
    {
        PresentationStyle.Preview => new Color4(1.00f, 0.58f, 0.16f, 0.96f),
        PresentationStyle.Selected => new Color4(DimensionHighlightColor.X, DimensionHighlightColor.Y, DimensionHighlightColor.Z, 1.00f),
        PresentationStyle.Hovered => new Color4(DimensionHighlightColor.X * 0.75f, DimensionHighlightColor.Y * 0.75f, DimensionHighlightColor.Z * 0.75f, 1.00f),
        PresentationStyle.Warning => new Color4(0.98f, 0.75f, 0.14f, 1.00f),
        PresentationStyle.SnapHint => new Color4(1.00f, 0.30f, 0.22f, 0.98f),
        _ => new Color4(0.18f, 0.83f, 0.75f, 0.96f),
    };

    private void LogRender(int snapshots, int lineVertices, int pointVertices, int diskVertices)
    {
        string key = $"{snapshots}|{lineVertices}|{pointVertices}|{diskVertices}";
        if (key == _lastRenderLogKey)
            return;

        _lastRenderLogKey = key;
        global::Android.Util.Log.Info(
            "FA.MeasureRender",
            $"Overlay draw: snapshots={snapshots}, lineVertices={lineVertices}, pointVertices={pointVertices}, diskVertices={diskVertices}.");
    }

    public void Dispose()
    {
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_diskVbo != 0) { _gl.DeleteBuffer(_diskVbo); _diskVbo = 0; }
        if (_diskVao != 0) { _gl.DeleteVertexArray(_diskVao); _diskVao = 0; }
        _program.Dispose();
        _diskProgram.Dispose();
    }

    private readonly record struct Color4(float R, float G, float B, float A);
}
