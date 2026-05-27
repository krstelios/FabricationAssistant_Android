using System.Buffers;
using FabricationAssistant.Core.Math;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// One GPU mesh: VAO + VBO + EBO + draw count. Positions are float32 (x,y,z),
/// normals float32 (x,y,z). The mesh's diffuse color is bound as a uniform per
/// draw (not interleaved per vertex).
/// </summary>
public sealed class GpuMesh : IDisposable
{
    private const int EdgeEndpointFloatCount = CadEdgeBuilder.EdgeVertexFloatCount;
    private const int EdgeRibbonFloatCount = 15;
    private const int EdgeRibbonStrideBytes = EdgeRibbonFloatCount * sizeof(float);

    private readonly GL _gl;
    private float[] _diffuseColor = new[] { 0.7f, 0.7f, 0.7f, 1.0f };
    public uint Vao { get; private set; }
    public uint Vbo { get; private set; }
    public uint Ebo { get; private set; }
    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }
    public float[] DiffuseColor
    {
        get => _diffuseColor;
        set
        {
            _diffuseColor = value ?? new[] { 0.7f, 0.7f, 0.7f, 1.0f };
            MaterialAlpha = ResolveAlpha(_diffuseColor);
        }
    }

    public float MaterialAlpha { get; private set; } = 1.0f;
    public bool DoubleSided { get; set; }
    public bool HasMirroredHandedness { get; set; }

    /// <summary>
    /// 1-based identity used by the pick shader (writes this as uint into a
    /// R32UI FBO). Index 0 is reserved for "no hit", so SceneUploader assigns
    /// the position-in-list + 1.
    /// </summary>
    public int MeshIndex { get; set; }

    /// <summary>
    /// Source MeshDto index in the loaded document. Multiple scene nodes may
    /// reference the same source mesh, but each node gets its own GpuMesh so
    /// selection/picking remains per rendered body.
    /// </summary>
    public int SourceMeshId { get; set; } = -1;

    /// <summary>
    /// Source SceneNodeDto id for this rendered instance. Used by the Android
    /// app to bridge GPU picking back to the shared scene graph.
    /// </summary>
    public int SourceNodeId { get; set; } = -1;

    /// <summary>
    /// Runtime scene-graph visibility for this rendered instance. Updated from
    /// the Android runtime Scene whenever Hide/Show/Isolate changes node state.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Row-major float[16] world-space transform for this instance. Null means
    /// the identity transform. SceneUploader populates this from the scene
    /// node's column-major DTO transform (transposing during conversion).
    /// </summary>
    public float[]? WorldTransform { get; set; }

    /// <summary>
    /// Cached row-major inverse-transpose normal matrix for WorldTransform.
    /// Null means identity. This avoids recomputing the same matrix in every
    /// render pass for static imported scenes.
    /// </summary>
    public float[]? WorldNormalMatrix { get; set; }

    /// <summary>
    /// World-space center of the mesh (mesh-local bounds center transformed
    /// by WorldTransform). Used by the tap-to-pivot logic so orbit / pan /
    /// zoom rotate around the part the user actually touched.
    /// </summary>
    public Vector3d WorldCenter { get; set; }

    /// <summary>
    /// Axis-aligned bounding box in world space (8 mesh-local corners
    /// transformed by WorldTransform, then min/max). Used for the
    /// ray-vs-AABB pivot picker on tap.
    /// </summary>
    public BoundingBox WorldBounds { get; set; } = BoundingBox.Empty;

    public GpuMesh(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
    }

    private static float ResolveAlpha(float[] color)
    {
        float alpha = color.Length >= 4 ? color[3] : 1.0f;
        if (float.IsNaN(alpha) || float.IsInfinity(alpha))
            return 1.0f;
        return System.Math.Clamp(alpha, 0.0f, 1.0f);
    }

    /// <summary>
    /// Upload interleaved (position vec3, normal vec3) vertices and uint32 indices.
    /// Stride: 24 bytes (6 floats).
    /// </summary>
    public unsafe void Upload(ReadOnlySpan<float> positions, ReadOnlySpan<float> normals, ReadOnlySpan<int> indices)
    {
        if (positions.Length % 3 != 0)
            throw new ArgumentException("positions length must be a multiple of 3", nameof(positions));
        if (normals.Length != 0 && normals.Length != positions.Length)
            throw new ArgumentException("normals length must match positions length or be empty", nameof(normals));

        int vertexCount = positions.Length / 3;
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] < 0 || indices[i] >= vertexCount)
                throw new InvalidDataException($"Mesh index {indices[i]} is outside the vertex range 0..{vertexCount - 1}.");
        }

        EnsureSurfaceResources();

        int interleavedLength = checked(vertexCount * 6);
        float[] interleaved = ArrayPool<float>.Shared.Rent(interleavedLength);
        uint[] uintIndices = ArrayPool<uint>.Shared.Rent(indices.Length);
        try
        {
        bool haveNormals = normals.Length == positions.Length;
        for (int i = 0; i < vertexCount; i++)
        {
            interleaved[i * 6 + 0] = positions[i * 3 + 0];
            interleaved[i * 6 + 1] = positions[i * 3 + 1];
            interleaved[i * 6 + 2] = positions[i * 3 + 2];
            interleaved[i * 6 + 3] = haveNormals ? normals[i * 3 + 0] : 0.0f;
            interleaved[i * 6 + 4] = haveNormals ? normals[i * 3 + 1] : 0.0f;
            interleaved[i * 6 + 5] = haveNormals ? normals[i * 3 + 2] : 1.0f;
        }

        for (int i = 0; i < indices.Length; i++)
            uintIndices[i] = (uint)indices[i];

        _gl.BindVertexArray(Vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, Vbo);
        fixed (float* p = interleaved)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleavedLength * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, Ebo);
        fixed (uint* p = uintIndices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const int stride = 6 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));

        _gl.BindVertexArray(0);

        VertexCount = vertexCount;
        IndexCount = indices.Length;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(interleaved);
            ArrayPool<uint>.Shared.Return(uintIndices);
        }
    }

    public void Draw()
    {
        if (IndexCount == 0) return;
        _gl.BindVertexArray(Vao);
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        _gl.BindVertexArray(0);
    }

    // CAD edges (Plan 2I Phase D).

    public uint EdgeVao { get; private set; }
    public uint EdgeVbo { get; private set; }
    public int EdgeVertexCount { get; private set; }
    public int EdgeSegmentCount { get; private set; }

    /// <summary>
    /// Uploads desktop-compatible edge endpoint vertices as GLES ribbon
    /// triangles. GLES has no geometry shader, so each line segment expands
    /// to two screen-facing triangles in edge.ribbon.gles.vert.
    /// </summary>
    public unsafe void UploadEdges(ReadOnlySpan<float> edgeVertices)
    {
        ClearEdgeResources();

        if (edgeVertices.Length == 0)
        {
            EdgeVertexCount = 0;
            EdgeSegmentCount = 0;
            return;
        }

        int endpointPairFloatCount = EdgeEndpointFloatCount * 2;
        if (edgeVertices.Length % endpointPairFloatCount != 0)
            throw new ArgumentException("Edge endpoint buffer must contain pairs of 10-float vertices.", nameof(edgeVertices));

        int segmentCount = edgeVertices.Length / endpointPairFloatCount;
        int ribbonLength = checked(segmentCount * 6 * EdgeRibbonFloatCount);
        float[] ribbon = ArrayPool<float>.Shared.Rent(ribbonLength);
        int output = 0;

        try
        {
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int p0 = segment * endpointPairFloatCount;
            int p1 = p0 + EdgeEndpointFloatCount;

            AddRibbonVertex(edgeVertices, p0, p1, 0.0f, 1.0f, ribbon, ref output);
            AddRibbonVertex(edgeVertices, p0, p1, 0.0f, -1.0f, ribbon, ref output);
            AddRibbonVertex(edgeVertices, p0, p1, 1.0f, 1.0f, ribbon, ref output);

            AddRibbonVertex(edgeVertices, p0, p1, 1.0f, 1.0f, ribbon, ref output);
            AddRibbonVertex(edgeVertices, p0, p1, 0.0f, -1.0f, ribbon, ref output);
            AddRibbonVertex(edgeVertices, p0, p1, 1.0f, -1.0f, ribbon, ref output);
        }

        EdgeVao = _gl.GenVertexArray();
        EdgeVbo = _gl.GenBuffer();

        _gl.BindVertexArray(EdgeVao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, EdgeVbo);
        fixed (float* p = ribbon)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(output * sizeof(float)), p, BufferUsageARB.StaticDraw);

        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)(12 * sizeof(float)));
        _gl.EnableVertexAttribArray(5);
        _gl.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)(13 * sizeof(float)));
        _gl.EnableVertexAttribArray(6);
        _gl.VertexAttribPointer(6, 1, VertexAttribPointerType.Float, false, EdgeRibbonStrideBytes, (void*)(14 * sizeof(float)));

        _gl.BindVertexArray(0);
        EdgeSegmentCount = segmentCount;
        EdgeVertexCount = output / EdgeRibbonFloatCount;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ribbon);
        }
    }

    private void EnsureSurfaceResources()
    {
        if (Vao == 0)
            Vao = _gl.GenVertexArray();
        if (Vbo == 0)
            Vbo = _gl.GenBuffer();
        if (Ebo == 0)
            Ebo = _gl.GenBuffer();
    }

    public void DrawEdges()
    {
        if (EdgeVertexCount == 0 || EdgeVao == 0) return;
        _gl.BindVertexArray(EdgeVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)EdgeVertexCount);
        _gl.BindVertexArray(0);
    }

    public void RebuildEdges(
        FabricationAssistant.Core.SceneGraph.MeshDto mesh,
        float featureAngleDegrees,
        float coplanarToleranceDegrees,
        float weldToleranceScale,
        bool silhouetteEnabled)
    {
        float[] edgeVertices = mesh.EdgePositions.Length > 0
            ? CadEdgeBuilder.BuildImportedEdgeVertices(mesh.EdgePositions)
            : CadEdgeBuilder.BuildFeatureEdgeVertices(
                mesh,
                featureAngleDegrees,
                coplanarToleranceDegrees,
                weldToleranceScale,
                silhouetteEnabled);

        UploadEdges(edgeVertices);
    }

    private static void AddRibbonVertex(
        ReadOnlySpan<float> source,
        int endpoint0,
        int endpoint1,
        float segmentT,
        float side,
        float[] destination,
        ref int output)
    {
        destination[output++] = source[endpoint0 + 0];
        destination[output++] = source[endpoint0 + 1];
        destination[output++] = source[endpoint0 + 2];
        destination[output++] = source[endpoint1 + 0];
        destination[output++] = source[endpoint1 + 1];
        destination[output++] = source[endpoint1 + 2];
        destination[output++] = source[endpoint0 + 3];
        destination[output++] = source[endpoint0 + 4];
        destination[output++] = source[endpoint0 + 5];
        destination[output++] = source[endpoint0 + 6];
        destination[output++] = source[endpoint0 + 7];
        destination[output++] = source[endpoint0 + 8];
        destination[output++] = source[endpoint0 + 9];
        destination[output++] = segmentT;
        destination[output++] = side;
    }

    private void ClearEdgeResources()
    {
        if (EdgeVao != 0) { _gl.DeleteVertexArray(EdgeVao); EdgeVao = 0; }
        if (EdgeVbo != 0) { _gl.DeleteBuffer(EdgeVbo); EdgeVbo = 0; }
        EdgeVertexCount = 0;
        EdgeSegmentCount = 0;
    }

    public void Dispose()
    {
        if (Vao != 0) { _gl.DeleteVertexArray(Vao); Vao = 0; }
        if (Vbo != 0) { _gl.DeleteBuffer(Vbo); Vbo = 0; }
        if (Ebo != 0) { _gl.DeleteBuffer(Ebo); Ebo = 0; }
        ClearEdgeResources();
    }
}
