using System.Runtime.CompilerServices;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class GlesRendererSourceTests
{
    [Fact]
    public void MeshShader_FlipsBackFaceNormalsOutsideSectionMode()
    {
        string shader = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\Shaders\mesh.gles.frag"));

        Assert.Contains("if (!gl_FrontFacing) normal = -normal;", shader);
        Assert.DoesNotContain("uSectionPlaneCount > 0 && !gl_FrontFacing", shader);
    }

    [Fact]
    public void SectionCapContourMask_UsesContourGeometryWithDepthOnlyOnCapPass()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int methodStart = renderer.IndexOf("private void RenderSectionCaps", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "RenderSectionCaps was not found.");

        int stencilDraw = renderer.IndexOf(
            "_sectionOverlay.RenderCapMaskTriangles(view, projection, geometry.TriangleVertices);",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(stencilDraw > methodStart, "Section cap contour draw was not found.");

        int capPassStart = renderer.IndexOf(
            "_gl.ColorMask(true, true, true, true);",
            stencilDraw,
            StringComparison.Ordinal);
        Assert.True(capPassStart > stencilDraw, "Section cap color pass was not found.");

        string stencilPass = renderer[methodStart..capPassStart];
        Assert.Contains("_gl.Disable(EnableCap.DepthTest);", stencilPass);
        Assert.DoesNotContain("_gl.Enable(EnableCap.DepthTest);", stencilPass);

        string capPass = renderer[capPassStart..];
        Assert.Contains("_gl.Enable(EnableCap.DepthTest);", capPass);
        Assert.Contains("_gl.DepthFunc(DepthFunction.Lequal);", capPass);
    }

    [Fact]
    public void SectionCapContourMask_DoesNotUseSceneRedrawPath()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int methodStart = renderer.IndexOf("private void RenderSectionCaps", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "RenderSectionCaps was not found.");
        string method = renderer[methodStart..renderer.IndexOf("private void ApplySectionOverlaySettings", methodStart, StringComparison.Ordinal)];

        Assert.Contains("GetSectionCapGeometries", method);
    }

    [Fact]
    public void GlesSourceAndShaders_AreAsciiOnly()
    {
        string root = ResolveRepoPath(@"..\FabricationAssistant.Rendering.Gles");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Contains(".gles.", StringComparison.OrdinalIgnoreCase)));

        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int offset = Array.FindIndex(bytes, value => value > 0x7F);
            if (offset >= 0)
                Assert.Fail($"Non-ASCII byte 0x{bytes[offset]:X2} in {Path.GetRelativePath(root, file)} at byte {offset}.");
        }
    }

    private static string ResolveRepoPath(string relativeToTestProject, [CallerFilePath] string caller = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, relativeToTestProject));
}
