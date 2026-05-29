using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class CloudServerUrlsTests
{
    [Theory]
    [InlineData("https://10.0.1.159", "http://10.0.1.159")]
    [InlineData("https://10.0.1.159/", "http://10.0.1.159")]
    [InlineData(" https://10.0.1.159:443/ ", "http://10.0.1.159")]
    public void NormalizeKnownProfileUrl_maps_local_lan_https_to_http(string value, string expected)
        => Assert.Equal(expected, CloudServerUrls.NormalizeKnownProfileUrl(value));

    [Theory]
    [InlineData("http://195.97.118.165", "https://195.97.118.165")]
    [InlineData(" http://195.97.118.165/ ", "https://195.97.118.165")]
    public void NormalizeKnownProfileUrl_maps_internet_http_to_https(string value, string expected)
        => Assert.Equal(expected, CloudServerUrls.NormalizeKnownProfileUrl(value));

    [Theory]
    [InlineData("http://10.0.1.159/", "http://10.0.1.159")]
    [InlineData("https://195.97.118.165/", "https://195.97.118.165")]
    [InlineData("https://cloud.example.com/", "https://cloud.example.com")]
    public void NormalizeKnownProfileUrl_keeps_valid_or_unknown_servers(string value, string expected)
        => Assert.Equal(expected, CloudServerUrls.NormalizeKnownProfileUrl(value));
}
