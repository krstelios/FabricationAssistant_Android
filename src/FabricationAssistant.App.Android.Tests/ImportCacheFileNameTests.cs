using FabricationAssistant.App.Android;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class ImportCacheFileNameTests
{
    [Theory]
    [InlineData("CON.fa")]
    [InlineData("NUL.glb")]
    [InlineData("COM1.gltf")]
    [InlineData("LPT9.fa")]
    public void Create_prefixes_windows_reserved_device_names(string input)
    {
        string name = ImportCacheFileName.Create(input, 0x1234, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.StartsWith("model_", name);
        Assert.Contains("-1234-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", name);
    }

    [Fact]
    public void Create_truncates_long_stems_before_unique_suffix()
    {
        string input = new string('a', 200) + ".fa";

        string name = ImportCacheFileName.Create(input, 0x1234, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        string stem = name[..name.IndexOf("-1234-", StringComparison.Ordinal)];
        Assert.Equal(80, stem.Length);
    }
}
