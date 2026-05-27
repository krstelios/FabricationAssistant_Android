using System.Numerics;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Sections;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class SectionCapGeometryBuilderTests
{
    [Fact]
    public void Build_ReturnsCapTriangles_ForClosedCubeCut()
    {
        MeshDto cube = CreateCube();
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(cube, Matrix4d.Identity) },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 4.0);

        Assert.True(geometry.TriangleVertices.Length > 0);
        Assert.Equal(0, geometry.TriangleVertices.Length % 9);
        Assert.NotEmpty(geometry.VectorLineVertices);
        Assert.True(geometry.Diagnostics.VectorSegmentCount > 0);
        Assert.True(geometry.Diagnostics.ClosedRegionCount > 0);
        Assert.True(geometry.Diagnostics.FilledRegionCount > 0);
        Assert.Equal(0, geometry.Diagnostics.OpenPrunedSegmentCount);
    }

    [Fact]
    public void Build_PrunesOpenBoundaryChain()
    {
        var mesh = new MeshDto
        {
            Positions =
            [
                -1f, -1f, -1f,
                 1f, -1f,  1f,
                 0f,  1f,  1f,
            ],
            Indices = [0, 1, 2],
            TriangleCount = 1,
        };
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(mesh, Matrix4d.Identity) },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 4.0);

        Assert.Empty(geometry.TriangleVertices);
        Assert.NotEmpty(geometry.VectorLineVertices);
        Assert.Equal(1, geometry.Diagnostics.VectorSegmentCount);
        Assert.Equal(1, geometry.Diagnostics.OpenPrunedSegmentCount);
    }

    [Fact]
    public void Build_AdaptiveWeldClosesSmallEndpointGap()
    {
        MeshDto mesh = CreateCrossingSegmentLoopWithGap(gap: 1.5e-5);
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(mesh, Matrix4d.Identity) },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 4.0);

        Assert.NotEmpty(geometry.TriangleVertices);
        Assert.True(geometry.Diagnostics.MaxWeldTolerance > geometry.Diagnostics.WeldTolerance);
        Assert.Equal(1, geometry.Diagnostics.AdaptiveWeldRetriedSourceCount);
        Assert.Equal(1, geometry.Diagnostics.AdaptiveWeldImprovedSourceCount);
        Assert.Equal(0, geometry.Diagnostics.OpenEndpointCount);
        Assert.Equal(0, geometry.Diagnostics.OpenPrunedSegmentCount);
    }

    [Fact]
    public void Build_EndpointStitchClosesGapBeyondWeldTolerance()
    {
        const double gap = 1.5e-4;
        MeshDto mesh = CreateCrossingSegmentLoopWithGap(gap);
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(mesh, Matrix4d.Identity) },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 4.0);

        Assert.NotEmpty(geometry.TriangleVertices);
        Assert.True(geometry.Diagnostics.EndpointStitchSegmentCount > 0);
        Assert.True(geometry.Diagnostics.EndpointStitchTolerance > geometry.Diagnostics.MaxWeldTolerance);
        Assert.True(geometry.Diagnostics.MaxWeldTolerance < gap);
        Assert.Equal(0, geometry.Diagnostics.OpenEndpointCount);
        Assert.Equal(0, geometry.Diagnostics.OpenPrunedSegmentCount);
    }

    [Fact]
    public void Build_DoesNotBridgeSeparateLoops()
    {
        MeshDto cube = CreateCube();
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[]
            {
                new SectionCapMeshSource(cube, Matrix4d.CreateTranslation(-3.0, 0.0, 0.0)),
                new SectionCapMeshSource(cube, Matrix4d.CreateTranslation(3.0, 0.0, 0.0)),
            },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 8.0);

        bool sawLeft = false;
        bool sawRight = false;
        for (int i = 0; i + 8 < geometry.TriangleVertices.Length; i += 9)
        {
            float x0 = geometry.TriangleVertices[i];
            float x1 = geometry.TriangleVertices[i + 3];
            float x2 = geometry.TriangleVertices[i + 6];
            bool left = x0 < 0f && x1 < 0f && x2 < 0f;
            bool right = x0 > 0f && x1 > 0f && x2 > 0f;
            Assert.True(left || right, $"Triangle spans separate loops: {x0}, {x1}, {x2}");
            sawLeft |= left;
            sawRight |= right;
        }

        Assert.True(sawLeft);
        Assert.True(sawRight);
    }

    [Fact]
    public void Build_DoesNotMergeSeparateSourcesInsideSnapTolerance()
    {
        MeshDto cube = CreateCube();
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        const double halfGap = 0.00002;

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[]
            {
                new SectionCapMeshSource(cube, Matrix4d.CreateTranslation(-1.0 - halfGap, 0.0, 0.0)),
                new SectionCapMeshSource(cube, Matrix4d.CreateTranslation(1.0 + halfGap, 0.0, 0.0)),
            },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 8.0);

        bool sawLeft = false;
        bool sawRight = false;
        for (int i = 0; i + 8 < geometry.TriangleVertices.Length; i += 9)
        {
            float x0 = geometry.TriangleVertices[i];
            float x1 = geometry.TriangleVertices[i + 3];
            float x2 = geometry.TriangleVertices[i + 6];
            bool left = x0 < 0f && x1 < 0f && x2 < 0f;
            bool right = x0 > 0f && x1 > 0f && x2 > 0f;
            Assert.True(left || right, $"Triangle bridges the gap between sources: {x0}, {x1}, {x2}");
            sawLeft |= left;
            sawRight |= right;
        }

        Assert.True(sawLeft);
        Assert.True(sawRight);
    }

    [Fact]
    public void Build_IgnoresCoplanarTriangles()
    {
        var mesh = new MeshDto
        {
            Positions =
            [
                -1f, -1f, 0f,
                 1f, -1f, 0f,
                 1f,  1f, 0f,
                -1f,  1f, 0f,
            ],
            Indices = [0, 1, 2, 0, 2, 3],
            TriangleCount = 2,
        };
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(mesh, Matrix4d.Identity) },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 4.0);

        Assert.Empty(geometry.TriangleVertices);
    }

    [Fact]
    public void Build_ClipsClosedRegionAgainstOtherSectionPlanes()
    {
        MeshDto cube = CreateCube();
        var activePlane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var clipPlane = new SectionCapPlane(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(cube, Matrix4d.Identity) },
            activePlane,
            new[] { activePlane, clipPlane },
            activePlaneIndex: 0,
            sceneDiagonal: 4.0);

        Assert.NotEmpty(geometry.TriangleVertices);
        Assert.True(geometry.Diagnostics.FilledRegionCount > 0);
        for (int i = 0; i + 2 < geometry.TriangleVertices.Length; i += 3)
            Assert.True(geometry.TriangleVertices[i] >= -1.0e-5f, $"Vertex escaped clip plane: x={geometry.TriangleVertices[i]}");
    }

    [Fact]
    public void Build_PrunesOpenChainsWithoutDroppingClosedLoop()
    {
        MeshDto mesh = CreateCubeWithOpenTail();
        var plane = new SectionCapPlane(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        SectionCapGeometry geometry = SectionCapGeometryBuilder.Build(
            new[] { new SectionCapMeshSource(mesh, Matrix4d.Identity) },
            plane,
            new[] { plane },
            activePlaneIndex: 0,
            sceneDiagonal: 5.0);

        Assert.NotEmpty(geometry.TriangleVertices);
        Assert.NotEmpty(geometry.VectorLineVertices);
        Assert.True(geometry.Diagnostics.OpenPrunedSegmentCount > 0);
        for (int i = 0; i + 2 < geometry.TriangleVertices.Length; i += 3)
        {
            Assert.True(geometry.TriangleVertices[i] >= -1.00001f);
            Assert.True(geometry.TriangleVertices[i] <= 1.00001f);
        }
    }

    private static MeshDto CreateCube()
    {
        return new MeshDto
        {
            Positions =
            [
                -1f, -1f, -1f,
                 1f, -1f, -1f,
                 1f,  1f, -1f,
                -1f,  1f, -1f,
                -1f, -1f,  1f,
                 1f, -1f,  1f,
                 1f,  1f,  1f,
                -1f,  1f,  1f,
            ],
            Indices =
            [
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,
                1, 2, 6, 1, 6, 5,
                2, 3, 7, 2, 7, 6,
                3, 0, 4, 3, 4, 7,
            ],
            TriangleCount = 12,
        };
    }

    private static MeshDto CreateCubeWithOpenTail()
    {
        MeshDto cube = CreateCube();
        float[] positions =
        [
            .. cube.Positions,
             1f,  1f,  0f,
             2f,  1f, -1f,
             2f,  1f,  1f,
        ];
        int[] indices =
        [
            .. cube.Indices,
            8, 9, 10,
        ];

        return new MeshDto
        {
            Positions = positions,
            Indices = indices,
            TriangleCount = indices.Length / 3,
        };
    }

    private static MeshDto CreateCrossingSegmentLoopWithGap(double gap)
    {
        var positions = new List<float>();
        var indices = new List<int>();

        AddCrossingSegmentTriangle(positions, indices, -1.0, -1.0, 1.0, -1.0);
        AddCrossingSegmentTriangle(positions, indices, 1.0, -1.0, 1.0, 1.0);
        AddCrossingSegmentTriangle(positions, indices, 1.0, 1.0, -1.0, 1.0);
        AddCrossingSegmentTriangle(positions, indices, -1.0, 1.0, -1.0, -1.0 + gap);

        return new MeshDto
        {
            Positions = positions.ToArray(),
            Indices = indices.ToArray(),
            TriangleCount = indices.Count / 3,
        };
    }

    private static void AddCrossingSegmentTriangle(
        List<float> positions,
        List<int> indices,
        double ax,
        double ay,
        double bx,
        double by)
    {
        int start = positions.Count / 3;
        positions.Add(0f);
        positions.Add(0f);
        positions.Add(1f);
        positions.Add((float)(ax * 2.0));
        positions.Add((float)(ay * 2.0));
        positions.Add(-1f);
        positions.Add((float)(bx * 2.0));
        positions.Add((float)(by * 2.0));
        positions.Add(-1f);
        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
    }
}
