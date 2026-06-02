using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.App.Android.Bom;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Bom;

public class BomConsolidatedFilterEngineTests
{
    private sealed record Row(string PartKey, string Name, string Source);

    private static readonly string[] Cols = { "name", "source" };

    private static BomConsolidatedFilterEngine<Row> NewEngine() =>
        new(Cols, (row, key) => key switch
        {
            "name" => row.Name,
            "source" => row.Source,
            _ => string.Empty,
        });

    private static readonly IReadOnlyList<Row> Rows = new[]
    {
        new Row("p1", "Bolt", "Purchased"),
        new Row("p2", "Nut", "Purchased"),
        new Row("p3", "Plate", "Made"),
    };

    [Fact]
    public void NoFilters_ReturnsAllRows_InOriginalOrder()
    {
        var e = NewEngine();
        e.RebuildValueLists(Rows);
        Assert.Equal(new[] { "p1", "p2", "p3" }, e.Apply(Rows).Select(r => r.PartKey));
        Assert.False(e.AnyActive);
    }

    [Fact]
    public void FilterAcrossColumns_IsAnded()
    {
        var e = NewEngine();
        e.RebuildValueLists(Rows);
        e.Column("source").SetChecked("Made", false);   // only Purchased
        e.Column("name").SetChecked("Nut", false);       // exclude Nut
        Assert.Equal(new[] { "p1" }, e.Apply(Rows).Select(r => r.PartKey));
        Assert.True(e.AnyActive);
    }

    [Fact]
    public void Sort_Ascending_And_Descending_ByColumn()
    {
        var e = NewEngine();
        e.RebuildValueLists(Rows);
        e.SetSort("name", SortDirection.Ascending);
        Assert.Equal(new[] { "Bolt", "Nut", "Plate" }, e.Apply(Rows).Select(r => r.Name));
        e.SetSort("name", SortDirection.Descending);
        Assert.Equal(new[] { "Plate", "Nut", "Bolt" }, e.Apply(Rows).Select(r => r.Name));
    }

    [Fact]
    public void ClearAll_ResetsFiltersAndSort()
    {
        var e = NewEngine();
        e.RebuildValueLists(Rows);
        e.Column("source").SetChecked("Made", false);
        e.SetSort("name", SortDirection.Descending);
        e.ClearAll();
        Assert.False(e.AnyActive);
        Assert.Null(e.SortColumnKey);
        Assert.Equal(new[] { "p1", "p2", "p3" }, e.Apply(Rows).Select(r => r.PartKey));
    }

    [Fact]
    public void RebuildValueLists_FillsEachColumnWithDistinctValues()
    {
        var e = NewEngine();
        e.RebuildValueLists(Rows);
        Assert.Equal(new[] { "Purchased", "Made" }.OrderBy(s => s),
                     e.Column("source").Values.Select(v => v.Value).OrderBy(s => s));
    }
}
