using System.Runtime.CompilerServices;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AppSettingsAreaUnitsSourceTests
{
    [Fact]
    public void LengthUnit_DeclaredWithMillimetreDefaultAndKey()
    {
        string src = ReadAppSettingsSource();
        Assert.Contains("MeasurementLengthUnitIndex", src);
        Assert.Contains("GetIntInRange(\"measurement_length_unit\", 0, 0, 4)", src);
    }

    [Fact]
    public void AreaUnit_DeclaredWithSquareMetreDefaultAndKey()
    {
        string src = ReadAppSettingsSource();
        Assert.Contains("MeasurementAreaUnitIndex", src);
        Assert.Contains("GetIntInRange(\"measurement_area_unit\", 2, 0, 4)", src);
    }

    private static string ReadAppSettingsSource([CallerFilePath] string caller = "")
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(caller)!,
            @"..\FabricationAssistant.App.Android\AppSettings.cs")));
}
