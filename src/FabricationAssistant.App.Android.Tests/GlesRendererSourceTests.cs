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
    public void SectionCaps_WriteDepthAndCurves_RespectDepth()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesSectionOverlay.cs"));

        Assert.Contains("depthMask: true", overlay);
        Assert.Contains("DrawRibbonPositions(view, projection, lineVertices, color, depthTest: true", overlay);
        Assert.Contains("_gl.DepthMask(depthMask);", overlay);
    }

    [Fact]
    public void EdgeThicknessSliders_AreSettingsDriven()
    {
        string settings = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AppSettings.cs"));
        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesSectionOverlay.cs"));

        Assert.Contains("public static float SectionEdgeWidth", settings);
        Assert.Contains("\"Normal edge thickness\", 0.05f, 4f", preferences);
        Assert.Contains("\"Section edge thickness\", 0.5f, 8f", preferences);
        Assert.Contains("renderer.SectionEdgeWidth = AppSettings.SectionEdgeWidth;", mainActivity);
        Assert.Contains(": System.Math.Clamp(a.EdgeWidth, 0.05f, 4.0f)", renderer);
        Assert.Contains("_sectionOverlay.EdgeWidth = SectionEdgeWidth;", renderer);
        Assert.Contains("new GlesSectionOverlay(_gl, measureVs, measureFs, edgeVs, edgeFs)", renderer);
        Assert.Contains("uLineWidthPixels", overlay);
        Assert.Contains("DrawElementsInstanced", overlay);
    }

    [Fact]
    public void MouseControls_AreSettingsDriven()
    {
        string settings = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AppSettings.cs"));
        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string interaction = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Input.Gestures.Android\ViewportInteractionAdapter.cs"));
        string pointerSource = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Input.Gestures.Android\AndroidPointerSource.cs"));

        Assert.Contains("public static float MouseOrbitSpeed", settings);
        Assert.Contains("public static float MousePanSpeed", settings);
        Assert.Contains("public static float MouseWheelZoomSpeed", settings);
        Assert.Contains("public static float MouseDragThresholdDip", settings);
        Assert.Contains("public static bool MouseHoverEnabled", settings);
        Assert.Contains("public static bool MouseInvertWheelZoom", settings);
        Assert.Contains("var mouse = AddSection(ctx, root, \"Mouse\"", preferences);
        Assert.Contains("\"Right-drag orbit speed\"", preferences);
        Assert.Contains("\"Middle-drag pan speed\"", preferences);
        Assert.Contains("_interaction.MouseOrbitSpeedMultiplier = AppSettings.MouseOrbitSpeed;", mainActivity);
        Assert.Contains("_interaction.MousePanSpeedMultiplier = AppSettings.MousePanSpeed;", mainActivity);
        Assert.Contains("_interaction.MouseWheelZoomSpeedMultiplier = AppSettings.MouseWheelZoomSpeed;", mainActivity);
        Assert.Contains("_pointerSource.MouseClickDragThresholdDip = AppSettings.MouseDragThresholdDip;", mainActivity);
        Assert.Contains("isMouseHover && !AppSettings.MouseHoverEnabled", mainActivity);
        Assert.Contains("MouseOrbitSpeedMultiplier", interaction);
        Assert.Contains("MousePanSpeedMultiplier", interaction);
        Assert.Contains("MouseWheelZoomSpeedMultiplier", interaction);
        Assert.Contains("MouseClickDragThresholdDip", pointerSource);
    }

    [Fact]
    public void SectionCapBuilds_AreNotCanceledByCameraInteraction()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int propertyStart = renderer.IndexOf("public bool InteractiveNavigationActive", StringComparison.Ordinal);
        Assert.True(propertyStart >= 0, "InteractiveNavigationActive was not found.");
        int propertyEnd = renderer.IndexOf("/// <summary>", propertyStart + 1, StringComparison.Ordinal);
        Assert.True(propertyEnd > propertyStart, "InteractiveNavigationActive property end was not found.");
        string property = renderer[propertyStart..propertyEnd];

        Assert.DoesNotContain("CancelPendingSectionCapGeometryBuilds", property);

        int interactionBranch = renderer.IndexOf("if (InteractiveNavigationActive)", renderer.IndexOf("GetSectionCapGeometries", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(interactionBranch >= 0, "Section cap interaction branch was not found.");
        int branchEnd = renderer.IndexOf("if (TryStartBackgroundSectionCapGeometryBuild", interactionBranch, StringComparison.Ordinal);
        Assert.True(branchEnd > interactionBranch, "Section cap interaction branch end was not found.");
        string branch = renderer[interactionBranch..branchEnd];

        Assert.Contains("Cap build blocked by camera interaction", branch);
        Assert.DoesNotContain("CancelPendingSectionCapGeometryBuilds", branch);
    }

    [Fact]
    public void ScreenSpaceSilhouetteOverlay_IsSkippedDuringSectionClipping()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int silhouetteStart = renderer.IndexOf("bool silhouetteOverlayActive = ssaoActive", StringComparison.Ordinal);
        Assert.True(silhouetteStart >= 0, "silhouetteOverlayActive assignment was not found.");
        int silhouetteEnd = renderer.IndexOf("if (silhouetteOverlayActive)", silhouetteStart, StringComparison.Ordinal);
        Assert.True(silhouetteEnd > silhouetteStart, "silhouetteOverlayActive assignment end was not found.");
        string assignment = renderer[silhouetteStart..silhouetteEnd];

        Assert.Contains("SectionPlanes.Count == 0", assignment);
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
