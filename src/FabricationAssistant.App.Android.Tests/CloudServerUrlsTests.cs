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

    [Theory]
    [InlineData("/api/v1/projects", "https://fa.f-nous.com/api/v1/projects")]
    [InlineData("api/v1/projects", "https://fa.f-nous.com/api/v1/projects")]
    [InlineData("https://fa.f-nous.com/api/v1/projects", "https://fa.f-nous.com/api/v1/projects")]
    public void BuildCloudUri_builds_https_urls(string value, string expected)
        => Assert.Equal(expected, CloudServerUrls.BuildCloudUri("https://fa.f-nous.com", value).AbsoluteUri);

    [Fact]
    public void BuildCloudUri_upgrades_http_urls_to_configured_https_origin()
    {
        Uri uri = CloudServerUrls.BuildCloudUri(
            "https://fa.f-nous.com",
            "http://fa.f-nous.com/api/v1/previews/model.png?size=small");

        Assert.Equal("https://fa.f-nous.com/api/v1/previews/model.png?size=small", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildCloudUri_uses_configured_origin_for_external_http_urls()
    {
        Uri uri = CloudServerUrls.BuildCloudUri(
            "https://fa.f-nous.com",
            "http://example.com/api/v1/previews/model.png?size=small");

        Assert.Equal("https://fa.f-nous.com/api/v1/previews/model.png?size=small", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildCloudUri_uses_configured_origin_for_external_https_urls()
    {
        Uri uri = CloudServerUrls.BuildCloudUri(
            "https://fa.f-nous.com",
            "https://example.com/api/v1/previews/model.png?size=small");

        Assert.Equal("https://fa.f-nous.com/api/v1/previews/model.png?size=small", uri.AbsoluteUri);
    }

    [Fact]
    public void BuildCloudUri_uses_configured_origin_for_non_http_non_https_absolute_urls()
    {
        Uri uri = CloudServerUrls.BuildCloudUri(
            "https://fa.f-nous.com",
            "ftp://example.com/api/v1/previews/model.png");

        Assert.Equal("https://fa.f-nous.com/api/v1/previews/model.png", uri.AbsoluteUri);
    }
}
