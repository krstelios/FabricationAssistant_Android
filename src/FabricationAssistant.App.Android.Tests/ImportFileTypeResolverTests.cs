using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class ImportFileTypeResolverTests
{
    [Theory]
    [InlineData("model.bin", "model/gltf-binary", "model.glb")]
    [InlineData("model.bin", "model/gltf+json", "model.gltf")]
    [InlineData("package.bin", "application/zip", "package.fa")]
    [InlineData(null, "application/x-zip-compressed", "model.fa")]
    [InlineData("package.bin", "APPLICATION/ZIP; charset=utf-8", "package.fa")]
    [InlineData("model", "model/gltf-binary", "model.glb")]
    public void ResolveFileNameWithMimeFallback_uses_supported_mime_extensions(
        string? displayName,
        string? mimeType,
        string expected)
        => Assert.Equal(expected, ImportFileTypeResolver.ResolveFileNameWithMimeFallback(displayName, mimeType));

    [Fact]
    public void ResolveFileNameWithMimeFallback_keeps_specific_display_extension()
        => Assert.Equal(
            "assembly.gltf",
            ImportFileTypeResolver.ResolveFileNameWithMimeFallback("assembly.gltf", "application/zip"));
}
