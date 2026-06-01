using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class MeshMeasurePickerLinkTests
{
    [Fact]
    public void Picker_Constructs_WithFakeRaycaster()
    {
        var raycaster = new FakeMeasureRaycaster(mesh: null, hit: null);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default);
        Assert.NotNull(picker);
    }
}
