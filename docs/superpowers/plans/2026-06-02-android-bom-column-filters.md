# Android BOM Consolidated Column Filters — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Excel-style per-column filter+sort dropdowns to the Android consolidated BOM table, kept in lockstep with FA part visibility and the model-explorer toggles, with a dirty-visibility "Show All" warning, and with every filter modification recorded as an undoable `BomFilterChange`.

**Architecture:** A pure, host-testable filter engine (per-column models + aggregation + sort) and a pure visibility planner (failing parts → hidden occurrence-id set) drive the consolidated BOM. Visibility is applied through the existing FA path (`AndroidScenePackageState.ApplyVisibilityState` on `_packageSession`). Undo integrates via a new, additive, optional `IBomFilterStateAccess` on Core's `SceneContext` and a `BomFilterChange : IUndoableChange` that snapshots filter-state + visibility + the dirty flag together.

**Tech Stack:** C# / .NET 8, Android (.NET for Android), xUnit. Shared `FabricationAssistant.Core` (parent repo) + `FabricationAssistant.App.Android` (nested repo).

**Spec:** `Android/docs/superpowers/specs/2026-06-02-android-bom-column-filters-design.md`

---

## Repos & commands

Two git repos (commit on `master` in both, staging only the listed files; both repos have unrelated WIP that must stay untouched — never `git add -A`):
- **Parent** `C:\Users\skritikos\Desktop\Fabrication Assistant` — Core types (Task 1) + Core tests in `src/FabricationAssistant.App.Tests`.
- **Android** `C:\Users\skritikos\Desktop\Fabrication Assistant\Android` — everything else.

Commands:
- Core tests (from parent): `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~BomFilter"`
- Android host tests (from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~Bom"`
- Android app build (from `Android/`): `Android\tools\build.ps1 -Configuration Debug` (real compile check for view/glue code that can't load on the host).
- If a host `dotnet test` is blocked by an `XrHost.OpenXR.dll` lock (parent only), build the test csproj then run with `--no-build`. Never kill processes.

## File structure

**Core (parent repo, `src/FabricationAssistant.Core/UndoRedo/`)**
| File | Responsibility |
|------|----------------|
| `BomFilterStateSnapshot.cs` (new) | immutable snapshot: per-column unchecked sets + sort + dirty |
| `IBomFilterStateAccess.cs` (new) | `Snapshot()` / `RestoreSnapshot()` seam |
| `BomFilterChange.cs` (new) | `IUndoableChange` restoring filter-state + visibility together |
| `SceneContext.cs` (edit) | add optional `BomFilter` accessor |

**Android (nested repo, `src/FabricationAssistant.App.Android/Bom/`)** — pure unless noted
| File | Responsibility |
|------|----------------|
| `SortDirection.cs` (new) | `enum { Ascending, Descending }` |
| `BomColumnFilterValue.cs` (new) | one checklist value (raw, display, checked) |
| `BomColumnFilterModel.cs` (new) | one column's filter (Allows + unchecked cache + SetValues) |
| `BomConsolidatedFilterEngine.cs` (new, generic) | aggregate columns, Apply (filter+sort) |
| `BomFilterVisibilityPlanner.cs` (new, static) | failing parts → hidden occurrence-id set |
| `AndroidBomFilterStateAccess.cs` (new) | `IBomFilterStateAccess` bridge via lambdas |
| `BomColumnFilterPopup.cs` (new, Android view) | the dropdown UI |
| `AndroidBomPanel.cs` (edit) | header affordances, hold engine, rebuild rows, expose snapshot/restore, raise apply |
| `MainActivity.cs` (edit) | dirty flag + hooks, apply sequence + warning, occurrence resolver, ApplyBomFilterVisibility, SceneContext + access wiring, undo recording, panel ref |
| `App.Android.Tests` (link 4 pure files + add tests + source guard) |

---

## Task 1: Core undo types (additive, optional — desktop unaffected)

**Files:**
- Create: `src/FabricationAssistant.Core/UndoRedo/BomFilterStateSnapshot.cs`
- Create: `src/FabricationAssistant.Core/UndoRedo/IBomFilterStateAccess.cs`
- Create: `src/FabricationAssistant.Core/UndoRedo/BomFilterChange.cs`
- Modify: `src/FabricationAssistant.Core/UndoRedo/SceneContext.cs`
- Test: `src/FabricationAssistant.App.Tests/UndoRedo/BomFilterChangeTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `src/FabricationAssistant.App.Tests/UndoRedo/BomFilterChangeTests.cs`:

```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Selection;
using FabricationAssistant.Core.UndoRedo;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Sections;
using FabricationAssistant.Core.BodyMove;
using Xunit;

namespace FabricationAssistant.App.Tests.UndoRedo;

public class BomFilterChangeTests
{
    private sealed class FakeBomFilter : IBomFilterStateAccess
    {
        public BomFilterStateSnapshot Current = BomFilterStateSnapshot.Empty;
        public BomFilterStateSnapshot Snapshot() => Current;
        public void RestoreSnapshot(BomFilterStateSnapshot snapshot) => Current = snapshot;
    }

    private sealed class FakeVisibility : IVisibilityStateAccess
    {
        public VisibilityStateSnapshot Current = VisibilityStateSnapshot.Empty;
        public bool GetVisible(int nodeId) => true;
        public void SetVisible(int nodeId, bool visible) { }
        public void SetVisibleRange(IEnumerable<int> nodeIds, bool visible) { }
        public VisibilityStateSnapshot SnapshotFor(IEnumerable<int> nodeIds) => Current;
        public VisibilityStateSnapshot SnapshotAll() => Current;
        public void RestoreSnapshot(VisibilityStateSnapshot snapshot) => Current = snapshot;
    }

    private static BomFilterStateSnapshot Filter(bool dirty, string? sortCol) =>
        new(new Dictionary<string, IReadOnlyList<string>> { ["name"] = new[] { "A" } }, sortCol, false, dirty);

    private static VisibilityStateSnapshot Vis(params string[] hidden) =>
        new(new Dictionary<int, bool>(), new HashSet<string>(hidden), new HashSet<string>(), false, 0.5);

    [Fact]
    public void ApplyBefore_And_ApplyAfter_RestoreFilterAndVisibility()
    {
        var bom = new FakeBomFilter();
        var vis = new FakeVisibility();
        var ctx = new SceneContext(() => null, new SelectionState(), vis,
            new SectionService(), new BodyMoveService(() => null), new MeasurementStore(), bom);

        var change = new BomFilterChange(
            filterBefore: Filter(dirty: true, sortCol: null),
            filterAfter: Filter(dirty: false, sortCol: "name"),
            visBefore: Vis("occ-1"),
            visAfter: Vis(),
            description: "Filter Name");

        change.ApplyAfter(ctx);
        Assert.False(bom.Current.Dirty);
        Assert.Equal("name", bom.Current.SortColumnKey);
        Assert.Empty(vis.Current.FaHiddenOccurrenceIds);

        change.ApplyBefore(ctx);
        Assert.True(bom.Current.Dirty);
        Assert.Null(bom.Current.SortColumnKey);
        Assert.Contains("occ-1", vis.Current.FaHiddenOccurrenceIds);

        Assert.Equal("BomFilter", change.Subsystem);
        Assert.Equal("Filter Name", change.Describe());
    }
}
```

> Note: `SectionService`, `BodyMoveService`, `MeasurementStore`, `SelectionState`, `VisibilityStateSnapshot` ctor shape are existing Core types. If a ctor arg differs, adjust the test's fakes to match the real interfaces — do not change production behaviour. (`VisibilityStateSnapshot` = `(IReadOnlyDictionary<int,bool> NodeVisibility, IReadOnlySet<string> FaHiddenOccurrenceIds, IReadOnlySet<string> FaIsolatedOccurrenceIds, bool XrayActive, double XrayOpacity)` — confirm field order in `VisibilityStateSnapshot.cs` and match it.)

- [ ] **Step 2: Run test — expect FAIL (types don't exist)**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~BomFilterChangeTests"`
Expected: FAIL to compile (`BomFilterStateSnapshot`, `IBomFilterStateAccess`, `BomFilterChange`, and the 7th `SceneContext` arg don't exist).

- [ ] **Step 3: Create the snapshot**

`src/FabricationAssistant.Core/UndoRedo/BomFilterStateSnapshot.cs`:

```csharp
namespace FabricationAssistant.Core.UndoRedo;

/// <summary>
/// Immutable snapshot of the consolidated-BOM column-filter UI state for undo/redo:
/// the unchecked values per column, the active single-column sort, and the
/// visibility "dirty" flag. Visibility itself is snapshotted separately via
/// <see cref="VisibilityStateSnapshot"/>; the two are restored together by
/// <see cref="BomFilterChange"/>.
/// </summary>
public sealed record BomFilterStateSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<string>> UncheckedValuesByColumn,
    string? SortColumnKey,
    bool SortDescending,
    bool Dirty)
{
    public static readonly BomFilterStateSnapshot Empty =
        new(new Dictionary<string, IReadOnlyList<string>>(), null, false, false);
}
```

- [ ] **Step 4: Create the access seam**

`src/FabricationAssistant.Core/UndoRedo/IBomFilterStateAccess.cs`:

```csharp
namespace FabricationAssistant.Core.UndoRedo;

/// <summary>
/// Per-subsystem adapter for the consolidated-BOM column filters, mirroring
/// <see cref="IVisibilityStateAccess"/>. Implemented Android-side; the desktop
/// app passes none (the <see cref="SceneContext"/> accessor stays null).
/// </summary>
public interface IBomFilterStateAccess
{
    BomFilterStateSnapshot Snapshot();
    void RestoreSnapshot(BomFilterStateSnapshot snapshot);
}
```

- [ ] **Step 5: Create the change**

`src/FabricationAssistant.Core/UndoRedo/BomFilterChange.cs`:

```csharp
namespace FabricationAssistant.Core.UndoRedo;

/// <summary>
/// One undoable BOM-filter modification. Restores the filter UI state and the
/// visibility state together so the dropdowns, table rows, 3D view, and
/// model-explorer toggles never drift apart on undo/redo. The Show-All that a
/// dirty apply performs is folded into the same entry (it is part of the
/// before/after visibility snapshots).
/// </summary>
public sealed class BomFilterChange : IUndoableChange
{
    private readonly BomFilterStateSnapshot _filterBefore;
    private readonly BomFilterStateSnapshot _filterAfter;
    private readonly VisibilityStateSnapshot _visBefore;
    private readonly VisibilityStateSnapshot _visAfter;
    private readonly string _description;

    public BomFilterChange(
        BomFilterStateSnapshot filterBefore,
        BomFilterStateSnapshot filterAfter,
        VisibilityStateSnapshot visBefore,
        VisibilityStateSnapshot visAfter,
        string description)
    {
        _filterBefore = filterBefore;
        _filterAfter = filterAfter;
        _visBefore = visBefore;
        _visAfter = visAfter;
        _description = description;
    }

    public string Subsystem => "BomFilter";

    public void ApplyBefore(SceneContext ctx)
    {
        ctx.BomFilter?.RestoreSnapshot(_filterBefore);
        ctx.Visibility.RestoreSnapshot(_visBefore);
    }

    public void ApplyAfter(SceneContext ctx)
    {
        ctx.BomFilter?.RestoreSnapshot(_filterAfter);
        ctx.Visibility.RestoreSnapshot(_visAfter);
    }

    public string Describe() => _description;
}
```

- [ ] **Step 6: Add the optional `SceneContext` accessor**

In `SceneContext.cs`, add the trailing optional ctor param and property (the existing 6-arg call sites keep compiling):

```csharp
    public SceneContext(
        Func<Scene?> sceneAccessor,
        SelectionState selection,
        IVisibilityStateAccess visibility,
        ISectionStateAccess sections,
        IBodyMoveStateAccess bodyMove,
        IMeasurementStore measurements,
        IBomFilterStateAccess? bomFilter = null)
    {
        _sceneAccessor = sceneAccessor ?? throw new ArgumentNullException(nameof(sceneAccessor));
        Selection = selection;
        Visibility = visibility;
        Sections = sections;
        BodyMove = bodyMove;
        Measurements = measurements;
        BomFilter = bomFilter;
    }
```

and add the property next to the others:

```csharp
    /// <summary>Consolidated-BOM column-filter state. Null when no provider is wired (desktop).</summary>
    public IBomFilterStateAccess? BomFilter { get; }
```

- [ ] **Step 7: Run test — expect PASS**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~BomFilterChangeTests"`
Expected: PASS.

- [ ] **Step 8: Commit (parent repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/UndoRedo/BomFilterStateSnapshot.cs src/FabricationAssistant.Core/UndoRedo/IBomFilterStateAccess.cs src/FabricationAssistant.Core/UndoRedo/BomFilterChange.cs src/FabricationAssistant.Core/UndoRedo/SceneContext.cs src/FabricationAssistant.App.Tests/UndoRedo/BomFilterChangeTests.cs
git commit -m "feat(undo): BomFilterChange + IBomFilterStateAccess for undoable BOM filters

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `BomColumnFilterModel` + `BomColumnFilterValue` + `SortDirection` (Android, pure)

**Files:**
- Create: `src/FabricationAssistant.App.Android/Bom/SortDirection.cs`
- Create: `src/FabricationAssistant.App.Android/Bom/BomColumnFilterValue.cs`
- Create: `src/FabricationAssistant.App.Android/Bom/BomColumnFilterModel.cs`
- Modify: `src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (link the 3 files)
- Test: `src/FabricationAssistant.App.Android.Tests/Bom/BomColumnFilterModelTests.cs` (create)

- [ ] **Step 1: Link the new pure files into the test project**

In `FabricationAssistant.App.Android.Tests.csproj`, add to the `<ItemGroup>` that links app source (near the other `..\FabricationAssistant.App.Android\...` `<Compile Include>` entries):

```xml
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.App.Android\Bom\SortDirection.cs" LinkBase="Linked\Bom" />
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.App.Android\Bom\BomColumnFilterValue.cs" LinkBase="Linked\Bom" />
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.App.Android\Bom\BomColumnFilterModel.cs" LinkBase="Linked\Bom" />
```

- [ ] **Step 2: Write the failing test**

`src/FabricationAssistant.App.Android.Tests/Bom/BomColumnFilterModelTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run — expect FAIL (types missing)**

Run: `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~BomColumnFilterModelTests"`
Expected: FAIL to compile.

- [ ] **Step 4: Implement the three files**

`src/FabricationAssistant.App.Android/Bom/SortDirection.cs`:

```csharp
namespace FabricationAssistant.App.Android.Bom;

public enum SortDirection
{
    Ascending,
    Descending,
}
```

`src/FabricationAssistant.App.Android/Bom/BomColumnFilterValue.cs`:

```csharp
namespace FabricationAssistant.App.Android.Bom;

/// <summary>One distinct value in a column-filter checklist.</summary>
public sealed class BomColumnFilterValue
{
    public BomColumnFilterValue(string value, bool isChecked)
    {
        Value = value;
        IsChecked = isChecked;
    }

    /// <summary>Raw cell value (case preserved); "" for blank cells.</summary>
    public string Value { get; }

    /// <summary>UI text; blank cells render as "(blank)".</summary>
    public string Display => Value.Length == 0 ? "(blank)" : Value;

    public bool IsChecked { get; set; }
}
```

`src/FabricationAssistant.App.Android/Bom/BomColumnFilterModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// One consolidated-BOM column's filter: a checklist of the column's distinct
/// values. <see cref="Allows"/> uses an O(1) unchecked-set cache so filtering a
/// large BOM is cheap. Framework-free and host-testable.
/// </summary>
public sealed class BomColumnFilterModel
{
    private readonly List<BomColumnFilterValue> _values = new();
    private readonly HashSet<string> _unchecked = new(StringComparer.Ordinal);

    public BomColumnFilterModel(string columnKey) => ColumnKey = columnKey;

    public string ColumnKey { get; }

    public IReadOnlyList<BomColumnFilterValue> Values => _values;

    /// <summary>Any value unchecked ⇒ this column is filtering.</summary>
    public bool IsActive => _unchecked.Count > 0;

    public IReadOnlyCollection<string> UncheckedValues => _unchecked;

    /// <summary>Allows a cell value through iff its value is checked.</summary>
    public bool Allows(string? value)
        => _unchecked.Count == 0 || !_unchecked.Contains(value ?? string.Empty);

    /// <summary>
    /// Rebuilds the distinct value list from the full dataset, preserving the
    /// unchecked state of any value that still exists.
    /// </summary>
    public void SetValues(IEnumerable<string> allValues)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in allValues)
        {
            string v = raw ?? string.Empty;
            if (seen.Add(v))
                distinct.Add(v);
        }

        _unchecked.IntersectWith(distinct); // drop unchecked values that vanished
        _values.Clear();
        foreach (string v in distinct)
            _values.Add(new BomColumnFilterValue(v, isChecked: !_unchecked.Contains(v)));
    }

    public void SetChecked(string value, bool isChecked)
    {
        string v = value ?? string.Empty;
        if (isChecked) _unchecked.Remove(v);
        else _unchecked.Add(v);
        foreach (BomColumnFilterValue item in _values)
            if (string.Equals(item.Value, v, StringComparison.Ordinal))
                item.IsChecked = isChecked;
    }

    public void SelectAll()
    {
        _unchecked.Clear();
        foreach (BomColumnFilterValue item in _values) item.IsChecked = true;
    }

    public void Clear()
    {
        _unchecked.Clear();
        foreach (BomColumnFilterValue item in _values)
        {
            item.IsChecked = false;
            _unchecked.Add(item.Value);
        }
    }

    /// <summary>Restore unchecked state from a snapshot (undo/redo).</summary>
    public void RestoreUnchecked(IEnumerable<string> unchecked_)
    {
        _unchecked.Clear();
        foreach (string v in unchecked_) _unchecked.Add(v ?? string.Empty);
        foreach (BomColumnFilterValue item in _values)
            item.IsChecked = !_unchecked.Contains(item.Value);
    }
}
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~BomColumnFilterModelTests"`
Expected: PASS.

- [ ] **Step 6: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/Bom/SortDirection.cs src/FabricationAssistant.App.Android/Bom/BomColumnFilterValue.cs src/FabricationAssistant.App.Android/Bom/BomColumnFilterModel.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/Bom/BomColumnFilterModelTests.cs
git commit -m "feat(bom): per-column filter model (pure, host-tested)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `BomConsolidatedFilterEngine` (Android, pure, generic)

**Files:**
- Create: `src/FabricationAssistant.App.Android/Bom/BomConsolidatedFilterEngine.cs`
- Modify: test csproj (link it)
- Test: `src/FabricationAssistant.App.Android.Tests/Bom/BomConsolidatedFilterEngineTests.cs` (create)

- [ ] **Step 1: Link the engine into the test project**

Add to the same `<ItemGroup>` as Task 2:

```xml
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.App.Android\Bom\BomConsolidatedFilterEngine.cs" LinkBase="Linked\Bom" />
```

- [ ] **Step 2: Write the failing test**

`src/FabricationAssistant.App.Android.Tests/Bom/BomConsolidatedFilterEngineTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~BomConsolidatedFilterEngineTests"`
Expected: FAIL to compile.

- [ ] **Step 4: Implement the engine**

`src/FabricationAssistant.App.Android/Bom/BomConsolidatedFilterEngine.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// Aggregates per-column <see cref="BomColumnFilterModel"/>s for the consolidated
/// BOM and produces the filtered + sorted row list. Generic over the row type with
/// an injected cell accessor so it stays framework-free and host-testable.
/// </summary>
public sealed class BomConsolidatedFilterEngine<TRow>
{
    private readonly Func<TRow, string, string> _cell;
    private readonly Dictionary<string, BomColumnFilterModel> _columns;
    private readonly List<string> _columnKeys;

    public BomConsolidatedFilterEngine(IReadOnlyList<string> filterableColumnKeys, Func<TRow, string, string> cell)
    {
        _cell = cell ?? throw new ArgumentNullException(nameof(cell));
        _columnKeys = filterableColumnKeys.ToList();
        _columns = _columnKeys.ToDictionary(k => k, k => new BomColumnFilterModel(k), StringComparer.Ordinal);
    }

    public IReadOnlyList<string> ColumnKeys => _columnKeys;
    public BomColumnFilterModel Column(string key) => _columns[key];
    public bool AnyActive => _columns.Values.Any(c => c.IsActive);

    public string? SortColumnKey { get; private set; }
    public bool SortDescending { get; private set; }

    public void SetSort(string columnKey, SortDirection direction)
    {
        SortColumnKey = columnKey;
        SortDescending = direction == SortDirection.Descending;
    }

    public void ClearSort()
    {
        SortColumnKey = null;
        SortDescending = false;
    }

    public void SetSortRaw(string? columnKey, bool descending)
    {
        SortColumnKey = columnKey;
        SortDescending = descending;
    }

    public void RebuildValueLists(IReadOnlyList<TRow> allRows)
    {
        foreach (string key in _columnKeys)
            _columns[key].SetValues(allRows.Select(r => _cell(r, key)));
    }

    public void ClearAll()
    {
        foreach (BomColumnFilterModel c in _columns.Values) c.SelectAll();
        ClearSort();
    }

    /// <summary>Filtered (AND across columns) then stably sorted by the active sort.</summary>
    public IReadOnlyList<TRow> Apply(IReadOnlyList<TRow> allRows)
    {
        IEnumerable<TRow> filtered = allRows.Where(Passes);
        if (SortColumnKey is not { } sortKey)
            return filtered.ToList();

        IOrderedEnumerable<TRow> ordered = SortDescending
            ? filtered.OrderByDescending(r => _cell(r, sortKey), StringComparer.OrdinalIgnoreCase)
            : filtered.OrderBy(r => _cell(r, sortKey), StringComparer.OrdinalIgnoreCase);
        return ordered.ToList();
    }

    public bool Passes(TRow row)
    {
        foreach (string key in _columnKeys)
            if (!_columns[key].Allows(_cell(row, key)))
                return false;
        return true;
    }

    // -- snapshot bridge (undo) --
    public IReadOnlyDictionary<string, IReadOnlyList<string>> UncheckedByColumn()
        => _columnKeys.ToDictionary(
            k => k,
            k => (IReadOnlyList<string>)_columns[k].UncheckedValues.ToList(),
            StringComparer.Ordinal);

    public void RestoreUnchecked(IReadOnlyDictionary<string, IReadOnlyList<string>> uncheckedByColumn)
    {
        foreach (string key in _columnKeys)
            _columns[key].RestoreUnchecked(
                uncheckedByColumn.TryGetValue(key, out IReadOnlyList<string>? v) ? v : Array.Empty<string>());
    }
}
```

> `OrderBy`/`OrderByDescending` are stable in .NET, satisfying the stable-sort requirement.

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~BomConsolidatedFilterEngineTests"`
Expected: PASS.

- [ ] **Step 6: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/Bom/BomConsolidatedFilterEngine.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/Bom/BomConsolidatedFilterEngineTests.cs
git commit -m "feat(bom): consolidated filter engine (AND across columns + sort)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `BomFilterVisibilityPlanner` (Android, pure)

**Files:**
- Create: `src/FabricationAssistant.App.Android/Bom/BomFilterVisibilityPlanner.cs`
- Modify: test csproj (link it)
- Test: `src/FabricationAssistant.App.Android.Tests/Bom/BomFilterVisibilityPlannerTests.cs` (create)

- [ ] **Step 1: Link it**

```xml
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.App.Android\Bom\BomFilterVisibilityPlanner.cs" LinkBase="Linked\Bom" />
```

- [ ] **Step 2: Write the failing test**

`src/FabricationAssistant.App.Android.Tests/Bom/BomFilterVisibilityPlannerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.App.Android.Bom;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Bom;

public class BomFilterVisibilityPlannerTests
{
    private static readonly Dictionary<string, string[]> Occ = new()
    {
        ["p1"] = new[] { "o1a", "o1b" },
        ["p2"] = new[] { "o2" },
        ["p3"] = new[] { "o3" },
    };

    private static IEnumerable<string> OccOf(string partKey) =>
        Occ.TryGetValue(partKey, out string[]? v) ? v : System.Array.Empty<string>();

    [Fact]
    public void HidesOccurrencesOfFailingParts_AndKeepsPassing()
    {
        var hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            allPartKeys: new[] { "p1", "p2", "p3" },
            passingPartKeys: new HashSet<string> { "p1" },
            occurrenceIdsForPart: OccOf);
        Assert.Equal(new[] { "o2", "o3" }.OrderBy(s => s), hidden.OrderBy(s => s));
    }

    [Fact]
    public void MultiOccurrencePart_ContributesAllIds_WhenFailing()
    {
        var hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            allPartKeys: new[] { "p1", "p2" },
            passingPartKeys: new HashSet<string> { "p2" },
            occurrenceIdsForPart: OccOf);
        Assert.Equal(new[] { "o1a", "o1b" }.OrderBy(s => s), hidden.OrderBy(s => s));
    }

    [Fact]
    public void AllPassing_HidesNothing()
    {
        var hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            allPartKeys: new[] { "p1", "p2", "p3" },
            passingPartKeys: new HashSet<string> { "p1", "p2", "p3" },
            occurrenceIdsForPart: OccOf);
        Assert.Empty(hidden);
    }
}
```

- [ ] **Step 3: Run — expect FAIL**

Run: `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~BomFilterVisibilityPlannerTests"`
Expected: FAIL to compile.

- [ ] **Step 4: Implement**

`src/FabricationAssistant.App.Android/Bom/BomFilterVisibilityPlanner.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// Computes the set of FA occurrence ids to hide so the 3D view matches the
/// filter: the union of the occurrence ids of every part that does NOT pass the
/// filter. Pure — the part→occurrence resolver is injected.
/// </summary>
public static class BomFilterVisibilityPlanner
{
    public static IReadOnlySet<string> HiddenOccurrenceIds(
        IEnumerable<string> allPartKeys,
        IReadOnlySet<string> passingPartKeys,
        Func<string, IEnumerable<string>> occurrenceIdsForPart)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (string partKey in allPartKeys)
        {
            if (passingPartKeys.Contains(partKey))
                continue;
            foreach (string occ in occurrenceIdsForPart(partKey))
                if (!string.IsNullOrEmpty(occ))
                    hidden.Add(occ);
        }
        return hidden;
    }
}
```

- [ ] **Step 5: Run — expect PASS**, then **Step 6: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/Bom/BomFilterVisibilityPlanner.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/Bom/BomFilterVisibilityPlannerTests.cs
git commit -m "feat(bom): visibility planner (failing parts -> hidden occurrence ids)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `AndroidBomFilterStateAccess` + `BomColumnFilterPopup` (Android view)

These compile only in the Android app (Android view types); verify with `build.ps1`, not the host runner.

**Files:**
- Create: `src/FabricationAssistant.App.Android/Bom/AndroidBomFilterStateAccess.cs`
- Create: `src/FabricationAssistant.App.Android/Bom/BomColumnFilterPopup.cs`

- [ ] **Step 1: `AndroidBomFilterStateAccess`** (delegating bridge — no engine type coupling)

`src/FabricationAssistant.App.Android/Bom/AndroidBomFilterStateAccess.cs`:

```csharp
using System;
using FabricationAssistant.Core.UndoRedo;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// Bridges the Core undo seam to the live consolidated-BOM panel via lambdas, so
/// the stable <see cref="SceneContext"/> (created once at startup) can reach a
/// BOM panel that is created on demand. Returns <see cref="BomFilterStateSnapshot.Empty"/>
/// and no-ops when no consolidated panel is open.
/// </summary>
public sealed class AndroidBomFilterStateAccess : IBomFilterStateAccess
{
    private readonly Func<BomFilterStateSnapshot> _snapshot;
    private readonly Action<BomFilterStateSnapshot> _restore;

    public AndroidBomFilterStateAccess(Func<BomFilterStateSnapshot> snapshot, Action<BomFilterStateSnapshot> restore)
    {
        _snapshot = snapshot;
        _restore = restore;
    }

    public BomFilterStateSnapshot Snapshot() => _snapshot();
    public void RestoreSnapshot(BomFilterStateSnapshot snapshot) => _restore(snapshot);
}
```

- [ ] **Step 2: `BomColumnFilterPopup`** — the Excel-style dropdown

Create `src/FabricationAssistant.App.Android/Bom/BomColumnFilterPopup.cs`. It mirrors the existing `MaterialCardView` + `PopupWindow` pattern (see `MainActivity` ~5282) and reuses the dark-theme colors (`Resource.Color.fa_surface_background/border/text_primary/text_secondary/accent_500/highlight`). Construct it from the column's `BomColumnFilterModel`, the current sort state for that column, and callbacks. Complete code:

```csharp
using System;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Card;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>Excel-style filter+sort dropdown for one consolidated-BOM column.</summary>
internal sealed class BomColumnFilterPopup
{
    private readonly Context _ctx;
    private readonly BomColumnFilterModel _model;
    private readonly Action<SortDirection> _onSort;
    private readonly Action _onApply;      // called on Done (commit current checks)
    private readonly Action _onChanged;    // called after Select all/Clear/toggle to refresh the list
    private PopupWindow? _popup;
    private LinearLayout? _listContainer;
    private string _search = string.Empty;

    public BomColumnFilterPopup(Context ctx, BomColumnFilterModel model, Action<SortDirection> onSort, Action onApply, Action onChanged)
    {
        _ctx = ctx;
        _model = model;
        _onSort = onSort;
        _onApply = onApply;
        _onChanged = onChanged;
    }

    public void Show(View anchor)
    {
        var card = new MaterialCardView(_ctx) { Radius = Dp(12), CardElevation = Dp(12), StrokeWidth = Dp(1) };
        card.SetCardBackgroundColor(Color(Resource.Color.fa_surface_background));
        card.StrokeColor = Color(Resource.Color.fa_border);

        var col = new LinearLayout(_ctx) { Orientation = Orientation.Vertical };
        col.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));

        col.AddView(SortRow());
        col.AddView(SearchBox());
        col.AddView(SelectClearRow());

        var scroll = new ScrollView(_ctx);
        _listContainer = new LinearLayout(_ctx) { Orientation = Orientation.Vertical };
        scroll.AddView(_listContainer);
        col.AddView(scroll, new LinearLayout.LayoutParams(Dp(260), Dp(240)));
        RebuildList();

        col.AddView(DoneButton());

        card.AddView(col);
        _popup = new PopupWindow((View)card, ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, focusable: true);
        _popup.SetBackgroundDrawable(new ColorDrawable(global::Android.Graphics.Color.Transparent));
        _popup.ShowAsDropDown(anchor, 0, 0, GravityFlags.Start);
    }

    private View SortRow()
    {
        var row = new LinearLayout(_ctx) { Orientation = Orientation.Horizontal };
        row.AddView(TextButton("Sort A→Z", () => { _onSort(SortDirection.Ascending); _onApply(); _popup?.Dismiss(); }));
        row.AddView(TextButton("Sort Z→A", () => { _onSort(SortDirection.Descending); _onApply(); _popup?.Dismiss(); }));
        return row;
    }

    private EditText SearchBox()
    {
        var box = new EditText(_ctx) { Hint = "Search", ImeOptions = global::Android.Views.InputMethods.ImeAction.Done };
        box.SetSingleLine(true);
        box.SetTextColor(Color(Resource.Color.fa_text_primary));
        box.SetHintTextColor(Color(Resource.Color.fa_text_disabled));
        box.TextChanged += (_, e) => { _search = e.Text?.ToString() ?? string.Empty; RebuildList(); };
        return box;
    }

    private View SelectClearRow()
    {
        var row = new LinearLayout(_ctx) { Orientation = Orientation.Horizontal };
        row.AddView(TextButton("Select all", () => { _model.SelectAll(); RebuildList(); _onChanged(); }));
        row.AddView(TextButton("Clear", () => { _model.Clear(); RebuildList(); _onChanged(); }));
        return row;
    }

    private void RebuildList()
    {
        if (_listContainer is null) return;
        _listContainer.RemoveAllViews();
        foreach (BomColumnFilterValue value in _model.Values.Where(v =>
                     _search.Length == 0 || v.Display.Contains(_search, StringComparison.OrdinalIgnoreCase)))
        {
            var cb = new CheckBox(_ctx) { Text = value.Display, Checked = value.IsChecked };
            cb.SetTextColor(Color(Resource.Color.fa_text_primary));
            BomColumnFilterValue captured = value;
            cb.CheckedChange += (_, e) => { _model.SetChecked(captured.Value, e.IsChecked); };
            _listContainer.AddView(cb);
        }
    }

    private View DoneButton()
        => TextButton("Done", () => { _onApply(); _popup?.Dismiss(); });

    private TextView TextButton(string text, Action onClick)
    {
        var tv = new TextView(_ctx) { Text = text, Clickable = true, Focusable = true };
        tv.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        tv.SetTextColor(Color(Resource.Color.fa_text_primary));
        tv.Click += (_, _) => onClick();
        return tv;
    }

    private global::Android.Graphics.Color Color(int resId)
        => new(global::AndroidX.Core.Content.ContextCompat.GetColor(_ctx, resId));

    private int Dp(float v) => (int)MathF.Round(v * (_ctx.Resources?.DisplayMetrics?.Density ?? 1f));
}
```

> Apply timing (per spec): toggling a checkbox only updates the model; the filter is applied (and the dirty/warning sequence runs) when the user taps **Done** or a **Sort** button — these call `_onApply`, which the panel routes to MainActivity (Task 7).

- [ ] **Step 3: Build the Android app to confirm these compile**

Run (from `Android/`): `Android\tools\build.ps1 -Configuration Debug`
Expected: Build succeeded.

- [ ] **Step 4: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/Bom/AndroidBomFilterStateAccess.cs src/FabricationAssistant.App.Android/Bom/BomColumnFilterPopup.cs
git commit -m "feat(bom): filter state-access bridge + Excel-style filter popup

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Integrate the engine into `AndroidBomPanel`

Make the consolidated panel own the engine, drive `_visibleRows` from it, make headers tap-to-open the popup, and expose snapshot/restore + an apply callback for MainActivity.

**Files:**
- Modify: `src/FabricationAssistant.App.Android/AndroidBomPanel.cs`

- [ ] **Step 1: Add engine + callbacks fields and a consolidated guard**

Near the existing fields (after `private string _filterText = string.Empty;`):

```csharp
    // Consolidated column filters (consolidated kind only).
    private BomConsolidatedFilterEngine<BomPanelRow>? _filterEngine;
    private Context? _lastContext; // captured at the top of CreateView for later header rebuilds
```

Also add `_lastContext = ctx;` as the first line inside `CreateView(Context ctx)`.

Add a using at the top: `using FabricationAssistant.App.Android.Bom;` and `using FabricationAssistant.Core.UndoRedo;`.

Add public callbacks the host (MainActivity) wires:

```csharp
    /// <summary>Raised when a filter/sort/clear should be applied. Host runs the dirty/warning + visibility + undo sequence, then calls <see cref="OnFilterApplied"/>.</summary>
    public Action? ApplyFilterRequested { get; set; }

    /// <summary>Host-supplied: the part keys currently passing (host needs them for visibility); the panel computes them.</summary>
    public IReadOnlyList<string> AllPartKeys => _allRows.Select(r => r.PartKey).Distinct().ToList();
    public IReadOnlyCollection<string> PassingPartKeys()
        => _filterEngine is null
            ? AllPartKeys.ToHashSet(StringComparer.Ordinal)
            : _filterEngine.Apply(_allRows).Select(r => r.PartKey).ToHashSet(StringComparer.Ordinal);
```

- [ ] **Step 2: Build the engine when consolidated rows load**

At the end of `LoadConsolidatedRows`, after `_roots.AddRange(_allRows);`, add:

```csharp
        _filterEngine = new BomConsolidatedFilterEngine<BomPanelRow>(
            _columns.Select(c => c.Key).ToList(),
            (row, key) => CellText(row, key));
        _filterEngine.RebuildValueLists(_allRows);
```

(`CellText(row, key)` is the existing static accessor; it already maps every consolidated column key to the row's value.)

- [ ] **Step 3: Drive `_visibleRows` from the engine in the consolidated branch of `ApplyFilter`**

Replace the consolidated branch of `ApplyFilter` (the `else { foreach (BomPanelRow row in _allRows) … }` block) with:

```csharp
        else
        {
            IReadOnlyList<BomPanelRow> rows = _filterEngine?.Apply(_allRows) ?? _allRows;
            string filter = _filterText.Trim();
            foreach (BomPanelRow row in rows)
                if (filter.Length == 0 || row.Matches(filter))
                    _visibleRows.Add(row);
        }
```

(The global search box still narrows within the column-filtered+sorted set.)

- [ ] **Step 4: Make consolidated headers tappable to open the popup**

In `CreateHeaderRow` and `RebuildHeader`, after `row.AddView(cell)` / `_headerRow.AddView(cell)` for the consolidated kind, attach a click that opens the popup for that column and mark active/sort with a glyph. Replace the `CreateHeaderRow` loop body with:

```csharp
        for (int i = 0; i < _columns.Length; i++)
        {
            int columnIndex = i;
            TextView cell = CreateCell(ctx, HeaderTitle(i), GetColumnWidth(i), bold: true);
            cell.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_secondary));
            if (_kind == AndroidBomPanelKind.Consolidated)
            {
                cell.Clickable = true;
                cell.Focusable = true;
                cell.Click += (_, _) => OpenColumnFilter(ctx, columnIndex, cell);
            }
            row.AddView(cell);
        }
```

Apply the identical change inside `RebuildHeader` (using `_headerRow.AddView`). Add helpers:

```csharp
    private string HeaderTitle(int columnIndex)
    {
        ColumnSpec spec = _columns[columnIndex];
        string glyph = "";
        if (_kind == AndroidBomPanelKind.Consolidated && _filterEngine is not null)
        {
            bool active = _filterEngine.Column(spec.Key).IsActive;
            bool sorted = string.Equals(_filterEngine.SortColumnKey, spec.Key, StringComparison.Ordinal);
            string arrow = sorted ? (_filterEngine.SortDescending ? " ▼" : " ▲") : "";
            string dot = active ? " •" : "";
            glyph = arrow + dot;
        }
        return spec.Title + glyph;
    }

    private void OpenColumnFilter(Context ctx, int columnIndex, View anchor)
    {
        if (_filterEngine is null) return;
        ColumnSpec spec = _columns[columnIndex];
        var popup = new BomColumnFilterPopup(
            ctx,
            _filterEngine.Column(spec.Key),
            onSort: dir => _filterEngine.SetSort(spec.Key, dir),
            onApply: () => ApplyFilterRequested?.Invoke(),
            onChanged: () => { });
        popup.Show(anchor);
    }
```

> Replace the `CreateCell(ctx, _columns[i].Title, …)` calls in both header methods with `CreateCell(ctx, HeaderTitle(i), …)` so the glyph shows.

- [ ] **Step 5: Add a "Clear all filters" button + expose snapshot/restore**

Add to `AddActions` (after the X-Ray button) a "Clear filters" button that calls `_filterEngine?.ClearAll()` then `ApplyFilterRequested?.Invoke()`. (Mirror the existing `CreateActionButton` pattern; for the consolidated kind only.) Then expose the snapshot/restore + sort accessors the host needs:

```csharp
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotEngineUnchecked()
        => _filterEngine?.UncheckedByColumn() ?? new Dictionary<string, IReadOnlyList<string>>();
    public string? SortColumnKey => _filterEngine?.SortColumnKey;
    public bool SortDescending => _filterEngine?.SortDescending ?? false;

    public void RestoreEngineState(IReadOnlyDictionary<string, IReadOnlyList<string>> uncheckedByColumn, string? sortColumnKey, bool sortDescending)
    {
        if (_filterEngine is null) return;
        _filterEngine.RestoreUnchecked(uncheckedByColumn);
        _filterEngine.SetSortRaw(sortColumnKey, sortDescending);
        if (_lastContext is { } ctx) RebuildHeader(ctx);
        ApplyFilter();
    }

    public void RefreshAfterFilter(Context ctx) { RebuildHeader(ctx); ApplyFilter(); }
```

- [ ] **Step 6: Build to confirm panel compiles**

Run (from `Android/`): `Android\tools\build.ps1 -Configuration Debug`
Expected: Build succeeded. (No host test — `AndroidBomPanel` is Android-only.)

- [ ] **Step 7: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/AndroidBomPanel.cs
git commit -m "feat(bom): wire column-filter engine + header dropdowns into the panel

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: MainActivity wiring — dirty flag, apply sequence, visibility, undo

**Files:**
- Modify: `src/FabricationAssistant.App.Android/MainActivity.cs`

- [ ] **Step 1: Add fields**

Near the other private fields (e.g. by `_undoService`):

```csharp
    private bool _bomFilterVisibilityDirty = true;     // true until a filter establishes a consistent state
    private bool _suppressBomDirty;                     // set while filter-driven visibility mutations run
    private AndroidBomPanel? _consolidatedBomPanel;     // the open consolidated panel, if any
    private AndroidBomFilterStateAccess? _bomFilterStateAccess;
```

Add usings: `using FabricationAssistant.App.Android.Bom;`.

- [ ] **Step 2: Wire the BOM filter access into the SceneContext**

Replace the `SceneContext` construction at `~488` with one that also builds and passes the BOM filter accessor:

```csharp
        _bomFilterStateAccess = new AndroidBomFilterStateAccess(
            snapshot: () => new FabricationAssistant.Core.UndoRedo.BomFilterStateSnapshot(
                _consolidatedBomPanel?.SnapshotEngineUnchecked()
                    ?? new Dictionary<string, IReadOnlyList<string>>(),
                _consolidatedBomPanel?.SortColumnKey,
                _consolidatedBomPanel?.SortDescending ?? false,
                _bomFilterVisibilityDirty),
            restore: s =>
            {
                _bomFilterVisibilityDirty = s.Dirty;
                _consolidatedBomPanel?.RestoreEngineState(s.UncheckedValuesByColumn, s.SortColumnKey, s.SortDescending);
            });
        _undoService = new UndoService(new SceneContext(
            () => _runtimeScene,
            _undoSelectionState,
            _visibilityStateAccess,
            _sections,
            _bodyMove,
            _measurementStoreAccess,
            _bomFilterStateAccess));
```

- [ ] **Step 3: Track the consolidated panel + wire its apply callback**

In `ToggleBomPanel`, after constructing `panel` and before `ShowLeftToolPanel`, set the apply callback and track the consolidated instance:

```csharp
        if (bomKind == AndroidBomPanelKind.Consolidated)
        {
            _consolidatedBomPanel = panel;
            panel.ApplyFilterRequested = () => ApplyBomFilter(panel);
        }
```

Where the left panel is closed/replaced (in `SetLeftToolPanelExpanded(false, …)` and wherever a different panel replaces it), clear the reference: `if (!ReferenceEquals(_leftToolPanelController, _consolidatedBomPanel)) _consolidatedBomPanel = null;` — simplest: set `_consolidatedBomPanel = null;` at the top of `ToggleBomPanel`/`ToggleSettingsPanel`/panel-close paths before assigning. Keep it to: clear in the close path and re-set when opening consolidated.

- [ ] **Step 4: The apply sequence (dirty + warning + visibility + undo)**

Add these methods to MainActivity:

```csharp
    private readonly struct BomDirtySuppressor : IDisposable
    {
        private readonly MainActivity _a;
        public BomDirtySuppressor(MainActivity a) { _a = a; _a._suppressBomDirty = true; }
        public void Dispose() => _a._suppressBomDirty = false;
    }

    private BomDirtySuppressor SuppressBomDirty() => new(this);

    private void ApplyBomFilter(AndroidBomPanel panel)
    {
        Scene? scene = _runtimeScene;
        if (scene is null || _packageSession is null) return;

        if (_bomFilterVisibilityDirty)
        {
            int hiddenNow = _packageSession.HiddenOccurrenceIds.Count + _packageSession.IsolatedOccurrenceIds.Count;
            new global::Android.App.AlertDialog.Builder(this)
                .SetTitle("Show all parts?")
                ?.SetMessage($"{hiddenNow} part(s) are hidden. Applying a filter will make all parts visible first.")
                ?.SetPositiveButton("Continue", (_, _) => CommitBomFilter(panel, scene))
                ?.SetNegativeButton("Cancel", (_, _) => { })
                ?.Show();
            return;
        }
        CommitBomFilter(panel, scene);
    }

    private void CommitBomFilter(AndroidBomPanel panel, Scene scene)
    {
        var filterBefore = _bomFilterStateAccess!.Snapshot();
        VisibilityStateSnapshot visBefore = CaptureVisibilitySnapshot();

        panel.RefreshAfterFilter(this);  // recompute table rows + headers from the engine

        var passing = panel.PassingPartKeys().ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> hidden = BomFilterVisibilityPlanner.HiddenOccurrenceIds(
            panel.AllPartKeys, passing, partKey => OccurrenceIdsForPartKey(scene, partKey));

        using (SuppressBomDirty())
        {
            ClearXrayIsolationState();
            AndroidScenePackageState.ApplyVisibilityState(scene, _packageSession!, hidden, System.Array.Empty<string>());
            _bomFilterVisibilityDirty = false;
            FinalizeVisibilityMutation("bom-filter", hidden.Count);
        }

        var filterAfter = _bomFilterStateAccess!.Snapshot();
        VisibilityStateSnapshot visAfter = CaptureVisibilitySnapshot();
        using IUndoTransaction? undo = _undoService?.Begin("BOM filter");
        undo?.Add(new FabricationAssistant.Core.UndoRedo.BomFilterChange(filterBefore, filterAfter, visBefore, visAfter, "BOM filter"));
    }

    private string[] OccurrenceIdsForPartKey(Scene scene, string partKey)
    {
        int[] nodeIds = scene.NodesById.Values
            .Where(node => SameTextIgnoreCase(node.Metadata?.SourceKey, partKey)
                        || SameTextIgnoreCase(node.Metadata?.SourceFullPath, partKey))
            .Select(node => node.Id).ToArray();
        return AndroidScenePackageState.GetOccurrenceIdsForNodes(scene, nodeIds);
    }
```

> `SameTextIgnoreCase`, `CaptureVisibilitySnapshot`, `FinalizeVisibilityMutation`, `ClearXrayIsolationState`, `AndroidScenePackageState.ApplyVisibilityState/GetOccurrenceIdsForNodes` all exist. Confirm `FinalizeVisibilityMutation` arity (`(string action, int count)`) — it is `(action, count)`.

- [ ] **Step 5: Mark dirty on manual visibility changes**

At the end of every manual visibility mutation, set `if (!_suppressBomDirty) _bomFilterVisibilityDirty = true;`. Add that line just before the closing brace of each of these methods (after their `FinalizeVisibilityMutation(...)` call):
- `SetModelExplorerNodeVisibility` (~3895)
- `ShowAllNodes` (~7350, both FA and non-FA returns)
- `IsolateSelectedNodes`, `IsolateXraySelectedNodes`, and the Hide path (wherever they call `FinalizeVisibilityMutation`).

Concretely, immediately after each `FinalizeVisibilityMutation(...)` call in those methods, add:

```csharp
        if (!_suppressBomDirty) _bomFilterVisibilityDirty = true;
```

> Undo/redo of a `VisibilityChange` restores visibility outside the filter; that is covered because the BOM filter's own undo entry restores the dirty flag, and a manual VisibilityChange undo leaves `_bomFilterVisibilityDirty` as-is from the last filter apply — acceptable: the next filter apply re-checks `_packageSession` hidden count. (If stricter behaviour is wanted later, hook `OnUndoHistoryChanged`.)

- [ ] **Step 6: Clear the panel ref on close**

Wherever the consolidated panel is torn down (panel replaced or collapsed), set `_consolidatedBomPanel = null;`. At minimum, set it to null at the start of `ToggleBomPanel` and `ToggleSettingsPanel` before showing a new panel, and in the collapse branch of `ToggleBomPanel` (`SetLeftToolPanelExpanded(false …)`).

- [ ] **Step 7: Build the Android app**

Run (from `Android/`): `Android\tools\build.ps1 -Configuration Debug`
Expected: Build succeeded.

- [ ] **Step 8: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/MainActivity.cs
git commit -m "feat(bom): wire filter apply, dirty-flag warning, FA visibility, and undo

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Source guard + full verification

**Files:**
- Modify: `src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

- [ ] **Step 1: Add a wiring source guard**

Add a `[Fact]` to the existing `GlesRendererSourceGuards` class asserting the integration is present:

```csharp
    [Fact]
    public void BomConsolidatedFilters_AreWired()
    {
        string panel = File.ReadAllText(ResolveRepoPath(@"..\FabricationAssistant.App.Android\AndroidBomPanel.cs"));
        Assert.Contains("BomConsolidatedFilterEngine<BomPanelRow>", panel);
        Assert.Contains("ApplyFilterRequested", panel);

        string main = File.ReadAllText(ResolveRepoPath(@"..\FabricationAssistant.App.Android\MainActivity.cs"));
        Assert.Contains("_bomFilterStateAccess", main);
        Assert.Contains("new SceneContext(", main);
        Assert.Contains("BomFilterVisibilityPlanner.HiddenOccurrenceIds", main);
        Assert.Contains("AndroidScenePackageState.ApplyVisibilityState(scene, _packageSession!, hidden", main);
        Assert.Contains("new FabricationAssistant.Core.UndoRedo.BomFilterChange(", main);
        Assert.Contains("_bomFilterVisibilityDirty = true", main);
    }
```

- [ ] **Step 2: Run the source guard + all Bom host tests**

Run (from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~Bom|FullyQualifiedName~SourceGuards"`
Expected: PASS.

- [ ] **Step 3: Full suites + app build**

- Core (from parent): `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj"` → all pass (incl. `BomFilterChangeTests`).
- Android host (from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj"` → all pass.
- Android app (from `Android/`): `Android\tools\build.ps1 -Configuration Debug` → Build succeeded.

- [ ] **Step 4: Manual device smoke** (deploy via `build.ps1 -Configuration Release` + adb per `docs/build-and-deploy.md`)

1. Open an FA package → open the **consolidated** BOM. Tap a column header → dropdown with sort/search/checklist appears.
2. Uncheck a value → Done → table rows drop, matching parts hide in 3D, model-explorer toggles reflect it; header shows the active dot.
3. Manually hide a part in the model explorer, then apply a filter → **"Show all parts?"** warning → Continue → all show, then filter applies. Apply another filter immediately → no warning.
4. Sort A→Z / Z→A → table reorders, header shows ▲/▼.
5. Clear all filters → all parts visible + all rows back.
6. **Undo** a filter apply → previous selections + previous (manual) visibility return; **redo** re-applies.

- [ ] **Step 5: Commit the source guard (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git commit -m "test(bom): source guard for consolidated column-filter wiring

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-review notes

- **Spec coverage:** per-column Excel filter (Tasks 2,5,6); AND across columns + sort (Task 3); lockstep visibility via FA occurrence ids (Task 4 + Task 7 `ApplyVisibilityState`); dirty-flag warning + Show All (Task 7); undoable filter modifications (Task 1 `BomFilterChange` + Task 7 recording); full-dataset value lists (`RebuildValueLists`); model-explorer sync (`FinalizeVisibilityMutation` → `RefreshVisibility`); desktop unaffected (optional `SceneContext` arg). Host tests for the three pure units + the Core change; source guard + device smoke for UI/glue.
- **Type consistency:** `BomFilterStateSnapshot(UncheckedValuesByColumn, SortColumnKey, SortDescending, Dirty)`; `IBomFilterStateAccess.Snapshot/RestoreSnapshot`; `BomFilterChange(filterBefore, filterAfter, visBefore, visAfter, description)`; engine `Apply/Column/RebuildValueLists/SetSort/ClearAll/UncheckedByColumn/RestoreUnchecked/SetSortRaw/SortColumnKey/SortDescending`; model `Allows/SetValues/SetChecked/SelectAll/Clear/IsActive/UncheckedValues/RestoreUnchecked`; planner `HiddenOccurrenceIds(allPartKeys, passingPartKeys, occurrenceIdsForPart)`; panel `ApplyFilterRequested/AllPartKeys/PassingPartKeys/SnapshotEngineUnchecked/SortColumnKey/SortDescending/RestoreEngineState/RefreshAfterFilter`. Names used identically across tasks.
- **Known integration caveat (documented, not a placeholder):** undoing a filter while the consolidated panel is closed restores visibility but not the (absent) filter UI; reopening rebuilds from current engine state. Acceptable for v1.
