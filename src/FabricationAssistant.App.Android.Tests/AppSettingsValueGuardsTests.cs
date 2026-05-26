using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AppSettingsValueGuardsTests
{
    [Theory]
    [InlineData(float.NaN, 0.0f)]
    [InlineData(float.NegativeInfinity, 0.0f)]
    [InlineData(-0.25f, 0.0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1.5f, 1.0f)]
    public void Clamp01_rejects_nonfinite_and_clamps_to_unit_range(float value, float expected)
        => Assert.Equal(expected, AppSettingsValueGuards.Clamp01(value));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(16, 4)]
    public void ClampAndroidMsaaSamples_maps_to_supported_android_values(int value, int expected)
        => Assert.Equal(expected, AppSettingsValueGuards.ClampAndroidMsaaSamples(value));
}
