using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

internal static class GlesFullscreenTriangle
{
    public static unsafe (uint Vao, uint Vbo) Create(GL gl)
    {
        float[] vertices =
        {
            -1f,  1f, 0f,  1f,
             3f,  1f, 2f,  1f,
            -1f, -3f, 0f, -1f,
        };

        uint vao = gl.GenVertexArray();
        uint vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
        {
            gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)),
                p,
                BufferUsageARB.StaticDraw);
        }

        const uint stride = 4u * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        return (vao, vbo);
    }

    public static void Draw(GL gl, uint vao)
    {
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
    }
}
