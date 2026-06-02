using System.Linq;
using FabricationAssistant.App.Android.Bom;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Bom;

public class BomColumnFilterModelTests
{
    [Fact]
    public void AllChecked_AllowsEverything_AndIsNotActive()
    {
        var m = new BomColumnFilterModel("name");
        m.SetValues(new[] { "Steel", "Aluminum", "" });
        Assert.True(m.Allows("Steel"));
        Assert.True(m.Allows(""));
        Assert.False(m.IsActive);
    }

    [Fact]
    public void UncheckingAValue_BlocksIt_AndMakesActive()
    {
        var m = new BomColumnFilterModel("name");
        m.SetValues(new[] { "Steel", "Aluminum" });
        m.SetChecked("Aluminum", false);
        Assert.True(m.Allows("Steel"));
        Assert.False(m.Allows("Aluminum"));
        Assert.True(m.IsActive);
    }

    [Fact]
    public void BlankValue_DisplaysAsBlankPlaceholder()
    {
        var m = new BomColumnFilterModel("name");
        m.SetValues(new[] { "" });
        Assert.Equal("(blank)", m.Values.Single().Display);
        Assert.Equal("", m.Values.Single().Value);
    }

    [Fact]
    public void SetValues_PreservesUncheckedStateForSurvivingValues()
    {
        var m = new BomColumnFilterModel("name");
        m.SetValues(new[] { "Steel", "Aluminum" });
        m.SetChecked("Aluminum", false);
        m.SetValues(new[] { "Steel", "Aluminum", "Plastic" }); // rebuild
        Assert.False(m.Allows("Aluminum")); // still unchecked
        Assert.True(m.Allows("Plastic"));    // new value defaults checked
    }

    [Fact]
    public void SelectAll_And_Clear()
    {
        var m = new BomColumnFilterModel("name");
        m.SetValues(new[] { "Steel", "Aluminum" });
        m.Clear();                  // uncheck all
        Assert.False(m.Allows("Steel"));
        Assert.True(m.IsActive);
        m.SelectAll();              // check all
        Assert.True(m.Allows("Steel"));
        Assert.False(m.IsActive);
    }

    [Fact]
    public void SetValues_IsDistinct_AndOrdinalIgnoreCaseDedup()
    {
        var m = new BomColumnFilterModel("name");
        m.SetValues(new[] { "Steel", "steel", "Steel" });
        Assert.Single(m.Values);
    }
}
