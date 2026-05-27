using System.Runtime.CompilerServices;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class GlesShaderParityTests
{
    [Fact]
    public void MeshShader_FlipsBackFaceNormalsOutsideSectionMode()
    {
        string shader = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\Shaders\mesh.gles.frag"));

        Assert.Contains("if (!gl_FrontFacing) normal = -normal;", shader);
        Assert.DoesNotContain("uSectionPlaneCount > 0 && !gl_FrontFacing", shader);
    }

    private static string ResolveRepoPath(string relativeToTestProject, [CallerFilePath] string caller = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, relativeToTestProject));
}
