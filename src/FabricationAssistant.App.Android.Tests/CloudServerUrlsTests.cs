using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class CloudServerUrlsTests
{
    [Theory]
    [InlineData("https://cloud.example.com/", "https://cloud.example.com")]
    [InlineData(" https://cloud.example.com/api/ ", "https://cloud.example.com/api")]
    [InlineData("http://192.168.1.10:8080/", "http://192.168.1.10:8080")]
    public void NormalizeConfiguredUrl_trims_whitespace_and_trailing_slash(string value, string expected)
        => Assert.Equal(expected, CloudServerUrls.NormalizeConfiguredUrl(value));

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeConfiguredUrl_preserves_missing_config_as_empty(string value, string expected)
        => Assert.Equal(expected, CloudServerUrls.NormalizeConfiguredUrl(value));
}
