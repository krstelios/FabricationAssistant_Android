using System.Runtime.InteropServices;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Presentation;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal sealed class GlesFaceHighlightOverlay : IDisposable
{
    private const int FloatsPerVertex = 7;
    private const int StrideBytes = FloatsPerVertex * sizeof(float);

    private readonly GL _gl;
    private readonly ShaderProgram _program;
    private uint _vao;
    private uint _vbo;
    private string _lastRenderLogKey = "";
    private readonly List<float> _vertexData = new();

    public GlesFaceHighlightOverlay(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(gl, "measure.face-highlight", vertexSource, fragmentSource);
    }

    public void Render(
        float[] view,
        float[] projection,
        IReadOnlyList<FaceHighlight> highlights)
    {
        if (highlights.Count == 0)
            return;

        _vertexData.Clear();
        foreach (FaceHighlight highlight in highlights)
        {
            Color4 color = highlight.Kind == FaceHighlightKind.Selected
                ? new Color4(0.18f, 0.83f, 0.75f, 0.38f)
                : new Color4(1.00f, 0.58f, 0.16f, 0.30f);

            foreach (Vector3d vertex in highlight.WorldVertices)
            {
                if (IsFinite(vertex))
                    AddVertex(_vertexData, vertex, color);
            }
        }

        int vertexCount = _vertexData.Count / FloatsPerVertex;
        if (vertexCount < 3)
        {
            LogRender(highlights.Count, vertexCount);
            return;
        }

        LogRender(highlights.Count, vertexCount);

        _program.Use();
        SetMat4("uView", view);
        SetMat4("uProjection", projection);
        SetFloat("uPointSize", 1.0f);
        SetInt("uRoundPoints", 0);

        try
        {
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            UploadAndDraw(_vertexData, vertexCount);
        }
        finally
        {
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
        }
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

    private unsafe void UploadAndDraw(List<float> data, int vertexCount)
    {
        EnsureBuffers();

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

    private void LogRender(int highlights, int vertexCount)
    {
        string key = $"{highlights}|{vertexCount}";
        if (key == _lastRenderLogKey)
            return;

        _lastRenderLogKey = key;
        global::Android.Util.Log.Info(
            "FA.MeasureRender",
            $"Face highlight draw: highlights={highlights}, vertices={vertexCount}.");
    }

    public void Dispose()
    {
        DeleteBuffers();
        _program.Dispose();
    }

    private void DeleteBuffers()
    {
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    private readonly record struct Color4(float R, float G, float B, float A);
}
