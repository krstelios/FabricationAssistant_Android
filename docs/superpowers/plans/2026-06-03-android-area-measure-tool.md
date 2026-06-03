# Area Measurement Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "Area" measurement tool that detects the coplanar planar face under a tap (reusing existing face detection), reports its surface area + perimeter as a billboard label, with the area unit user-selectable in Settings → Measurement (default m²) and a companion length unit (default mm).

**Architecture:** Extend the shared measurement domain (`src/FabricationAssistant.Core/Measurement`) with a new `Area` tool mode, an `AreaMeasurement` type, a `SceneArea` value type, an area-aware unit formatter, a single-pick commit path in `MeasurementSession`, and a presenter case. The Android app (`Android/src/FabricationAssistant.App.Android`) adds a toolbar button, two unit pickers in settings, persisted settings, and wiring. Area/perimeter are computed from the picked face's world-space triangles (already carried on `PickResult.PickedFace.Highlight`).

**Tech Stack:** C# / .NET 8, Xamarin/.NET-Android, xUnit. Core is cross-platform and compile-linked into Android, desktop (`FabricationAssistant.App`), and OpenXR hosts.

---

## CRITICAL: Two git repositories

This feature spans **two separate git repos**:

| Path | Repo | Holds |
|---|---|---|
| `C:\Users\skritikos\Desktop\Fabrication Assistant` | **parent** (`master`) | All `src/...` Core + desktop test changes (Phase A) |
| `C:\Users\skritikos\Desktop\Fabrication Assistant\Android` | **nested** (`master`, gitignored by parent) | All `Android/src/...` app changes (Phase B) |

- **Phase A commits go to the PARENT repo.** From `C:\Users\skritikos\Desktop\Fabrication Assistant`, `git add src/...`.
- **Phase B commits go to the ANDROID repo.** From `...\Android`, `git add src/...`.
- Never `git add` a `src/FabricationAssistant.Core/...` path from inside `Android/` (it's a different repo and the path won't exist there).

New Core `.cs` files are auto-compiled into Android via the shim glob (`FabricationAssistant.Core.Android.csproj` includes `..\..\..\src\FabricationAssistant.Core\**\*.cs`) — no csproj edits needed.

### Test placement
- **Phase A logic tests** live in the desktop test project `src/FabricationAssistant.App.Tests/Measurement/` (parent repo, alongside `MeasurementPresenterTests.cs`/`MeasurementSessionTests.cs`). This keeps test + implementation in one repo per commit and runs on the net8.0 host.
- **Phase B** adds one Android source-level settings test in `Android/src/FabricationAssistant.App.Android.Tests/`.

### Commands (run from the indicated repo root)
- Build Core: `dotnet build src/FabricationAssistant.Core/FabricationAssistant.Core.csproj`
- Run a Phase A test class: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~<ClassName>"`
- Run full desktop suite (Phase C): `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj`
- Run Android logic suite (Phase C): `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`

---

## Branch setup

- [ ] **Step 1: Branch the parent repo**

Run (from `C:\Users\skritikos\Desktop\Fabrication Assistant`):
```
git checkout -b feat/area-measure-tool
```
Expected: `Switched to a new branch 'feat/area-measure-tool'`

- [ ] **Step 2: Branch the Android repo**

Run (from `C:\Users\skritikos\Desktop\Fabrication Assistant\Android`):
```
git checkout -b feat/area-measure-tool
```
Expected: `Switched to a new branch 'feat/area-measure-tool'`

---

# PHASE A — Shared Core (parent repo)

## Task A1: `MeasureToolMode.Area` + `SceneArea` value type

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Domain/MeasureToolMode.cs`
- Create: `src/FabricationAssistant.Core/Measurement/Domain/SceneArea.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/SceneAreaTests.cs`

- [ ] **Step 1: Add the enum value**

In `MeasureToolMode.cs`, add `Area` as the last member:
```csharp
public enum MeasureToolMode
{
    None,
    PointToPoint,
    FaceToPoint,
    FaceToFace,
    BoundingBox,
    Annotation,
    Area,
}
```

- [ ] **Step 2: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/SceneAreaTests.cs`:
```csharp
using FabricationAssistant.Core.Measurement.Domain;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SceneAreaTests
{
    [Fact]
    public void FromSceneUnits_SquaresTheMetersPerSceneUnitFactor()
    {
        // 1 unit² of geometry in millimetres (metersPerSceneUnit = 0.001) == 1e-6 m².
        SceneArea area = SceneArea.FromSceneUnits(1.0, 0.001);
        Assert.Equal(1e-6, area.SquareMeters, 12);
    }

    [Fact]
    public void FromSceneUnits_RejectsNonPositiveFactor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneArea.FromSceneUnits(1.0, 0.0));
    }

    [Theory]
    [InlineData(UnitSystem.Millimeters, 1_000_000.0)]
    [InlineData(UnitSystem.Centimeters, 10_000.0)]
    [InlineData(UnitSystem.Meters, 1.0)]
    public void ConvertTo_ScalesOneSquareMeterToTheUnit(UnitSystem unit, double expected)
    {
        var area = new SceneArea(1.0);
        Assert.Equal(expected, area.ConvertTo(unit), 6);
    }

    [Fact]
    public void ConvertTo_ImperialUsesInverseSquareFactors()
    {
        var area = new SceneArea(1.0);
        Assert.Equal(1.0 / (0.0254 * 0.0254), area.ConvertTo(UnitSystem.Inches), 6);
        Assert.Equal(1.0 / (0.3048 * 0.3048), area.ConvertTo(UnitSystem.Feet), 6);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SceneAreaTests"`
Expected: FAIL to compile — `SceneArea` does not exist.

- [ ] **Step 4: Implement `SceneArea`**

Create `src/FabricationAssistant.Core/Measurement/Domain/SceneArea.cs`:
```csharp
namespace FabricationAssistant.Core.Measurement.Domain;

/// <summary>
/// A surface area, canonically stored in square metres. Mirror of
/// <see cref="SceneLength"/>: geometry is measured in scene units and converted
/// to the canonical square-metre value via the squared metres-per-scene-unit
/// factor, then formatted into a display unit on demand.
/// </summary>
public readonly record struct SceneArea(double SquareMeters)
{
    public static SceneArea FromSceneUnits(double sceneAreaUnits, double metersPerSceneUnit)
    {
        if (metersPerSceneUnit <= 0.0 || double.IsNaN(metersPerSceneUnit) || double.IsInfinity(metersPerSceneUnit))
            throw new ArgumentOutOfRangeException(nameof(metersPerSceneUnit),
                "metersPerSceneUnit must be a positive finite number.");
        return new SceneArea(sceneAreaUnits * metersPerSceneUnit * metersPerSceneUnit);
    }

    public double ConvertTo(UnitSystem unit) => unit switch
    {
        UnitSystem.Millimeters => SquareMeters * 1_000_000.0,
        UnitSystem.Centimeters => SquareMeters * 10_000.0,
        UnitSystem.Meters => SquareMeters,
        UnitSystem.Inches => SquareMeters / (0.0254 * 0.0254),
        UnitSystem.Feet => SquareMeters / (0.3048 * 0.3048),
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SceneAreaTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit (PARENT repo)**

Run from `C:\Users\skritikos\Desktop\Fabrication Assistant`:
```
git add src/FabricationAssistant.Core/Measurement/Domain/MeasureToolMode.cs src/FabricationAssistant.Core/Measurement/Domain/SceneArea.cs src/FabricationAssistant.App.Tests/Measurement/SceneAreaTests.cs
git commit -m "feat(measure): add MeasureToolMode.Area and SceneArea value type"
```

---

## Task A2: `AreaMeasurement` record + `MeasurementBuilders.BuildArea`

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs` (append record)
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/MeasurementBuildersAreaTests.cs`

- [ ] **Step 1: Add the `AreaMeasurement` record**

Append to `Measurement.cs` (after `AnnotationMeasurement`, before EOF). `using FabricationAssistant.Core.Math;` is already at the top of the file:
```csharp
/// <summary>
/// The surface area + perimeter of a coplanar planar face detected from a single
/// tap. <see cref="Plane"/> centroid is the label anchor. <see cref="OutlineSegments"/>
/// holds the patch's boundary edges as a flat world-space list (consecutive pairs
/// form one segment each) — used to draw the face outline and follows the body via
/// <see cref="MeasurementResult.BodyAnchor"/>.
/// </summary>
public sealed record AreaMeasurement(
    MeasurementId Id,
    ScenePlane Plane,
    SceneArea Area,
    SceneLength Perimeter,
    IReadOnlyList<Vector3d> OutlineSegments,
    bool IsVisible = true) : MeasurementResult(Id, IsVisible)
{
    public override MeasureToolMode Mode => MeasureToolMode.Area;
}
```

- [ ] **Step 2: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/MeasurementBuildersAreaTests.cs`:
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeasurementBuildersAreaTests
{
    // Unit square in the XY plane, tessellated as two triangles sharing the
    // (0,0)->(1,1) diagonal. World-space vertices, 3 per triangle.
    private static Vector3d[] UnitSquareTriangles() =>
    [
        new(0, 0, 0), new(1, 0, 0), new(1, 1, 0),   // T1
        new(0, 0, 0), new(1, 1, 0), new(0, 1, 0),   // T2
    ];

    private static ScenePlane FlatPlane() =>
        new(new Vector3d(0.5, 0.5, 0), new Vector3d(0, 0, 1), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), 0.75);

    [Fact]
    public void BuildArea_UnitSquare_AreaIsOneAndPerimeterIsFour()
    {
        AreaMeasurement m = MeasurementBuilders.BuildArea(FlatPlane(), UnitSquareTriangles(), metersPerSceneUnit: 1.0);

        Assert.Equal(1.0, m.Area.SquareMeters, 9);
        Assert.Equal(4.0, m.Perimeter.Meters, 9);
    }

    [Fact]
    public void BuildArea_ExcludesTheInteriorSharedDiagonalFromPerimeter()
    {
        AreaMeasurement m = MeasurementBuilders.BuildArea(FlatPlane(), UnitSquareTriangles(), metersPerSceneUnit: 1.0);

        // 4 boundary segments * 2 endpoints each. The shared diagonal is interior.
        Assert.Equal(8, m.OutlineSegments.Count);
    }

    [Fact]
    public void BuildArea_AppliesSquaredFactorForMillimetreSource()
    {
        // metersPerSceneUnit = 0.001 (mm). 1 unit² -> 1e-6 m²; 4 units -> 0.004 m perimeter.
        AreaMeasurement m = MeasurementBuilders.BuildArea(FlatPlane(), UnitSquareTriangles(), metersPerSceneUnit: 0.001);

        Assert.Equal(1e-6, m.Area.SquareMeters, 12);
        Assert.Equal(0.004, m.Perimeter.Meters, 9);
    }

    [Fact]
    public void BuildArea_EmptyInput_ReturnsZero()
    {
        AreaMeasurement m = MeasurementBuilders.BuildArea(FlatPlane(), [], metersPerSceneUnit: 1.0);

        Assert.Equal(0.0, m.Area.SquareMeters);
        Assert.Equal(0.0, m.Perimeter.Meters);
        Assert.Empty(m.OutlineSegments);
    }
}
```

> Note: `ScenePlane`'s constructor is `(Vector3d Origin, Vector3d Normal, Vector3d U, Vector3d V, double Radius)` (verified). The `FlatPlane()` helper matches it.

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementBuildersAreaTests"`
Expected: FAIL to compile — `MeasurementBuilders.BuildArea` does not exist.

- [ ] **Step 4: Implement `BuildArea`**

Add to `MeasurementBuilders.cs` (inside the static class, after `BuildFaceToFace`). The file already has `using FabricationAssistant.Core.Math;` and `using FabricationAssistant.Core.Measurement.Domain;`; `System.Collections.Generic` is available via ImplicitUsings:
```csharp
    /// <summary>
    /// Computes the area (sum of triangle areas) and perimeter (sum of boundary-edge
    /// lengths) of a planar face patch. <paramref name="worldTriangleVertices"/> holds
    /// the patch triangles in world space, 3 consecutive vertices per triangle — exactly
    /// what <c>PickResult.PickedFace.Highlight.WorldVertices</c> already carries. An edge
    /// is on the boundary iff exactly one patch triangle uses it; endpoints are welded
    /// so glTF's duplicated per-triangle vertices collapse.
    /// </summary>
    public static AreaMeasurement BuildArea(
        ScenePlane plane,
        IReadOnlyList<Vector3d> worldTriangleVertices,
        double metersPerSceneUnit)
    {
        int triCount = worldTriangleVertices.Count / 3;

        double areaScene = 0.0;
        for (int t = 0; t < triCount; t++)
        {
            Vector3d a = worldTriangleVertices[3 * t];
            Vector3d b = worldTriangleVertices[3 * t + 1];
            Vector3d c = worldTriangleVertices[3 * t + 2];
            areaScene += 0.5 * Vector3d.Cross(b - a, c - a).Length;
        }

        double weld = ComputeAreaWeldTolerance(worldTriangleVertices);
        var edges = new Dictionary<(long, long, long, long, long, long), (Vector3d A, Vector3d B, int Count)>();
        for (int t = 0; t < triCount; t++)
        {
            Vector3d a = worldTriangleVertices[3 * t];
            Vector3d b = worldTriangleVertices[3 * t + 1];
            Vector3d c = worldTriangleVertices[3 * t + 2];
            AccumulateAreaEdge(edges, a, b, weld);
            AccumulateAreaEdge(edges, b, c, weld);
            AccumulateAreaEdge(edges, c, a, weld);
        }

        double perimeterScene = 0.0;
        var outline = new List<Vector3d>();
        foreach ((Vector3d A, Vector3d B, int Count) e in edges.Values)
        {
            if (e.Count != 1) continue; // interior edge shared by two triangles
            perimeterScene += (e.B - e.A).Length;
            outline.Add(e.A);
            outline.Add(e.B);
        }

        SceneArea area = SceneArea.FromSceneUnits(areaScene, metersPerSceneUnit);
        SceneLength perimeter = SceneLength.FromSceneUnits(perimeterScene, metersPerSceneUnit);
        return new AreaMeasurement(MeasurementId.New(), plane, area, perimeter, outline);
    }

    private static double ComputeAreaWeldTolerance(IReadOnlyList<Vector3d> verts)
    {
        if (verts.Count == 0) return 1e-9;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (Vector3d v in verts)
        {
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
        }
        double diag = System.Math.Sqrt(
            (maxX - minX) * (maxX - minX) +
            (maxY - minY) * (maxY - minY) +
            (maxZ - minZ) * (maxZ - minZ));
        return System.Math.Max(diag * 1e-6, 1e-9);
    }

    private static (long, long, long) QuantizeAreaVertex(Vector3d p, double weld)
        => ((long)System.Math.Round(p.X / weld),
            (long)System.Math.Round(p.Y / weld),
            (long)System.Math.Round(p.Z / weld));

    private static void AccumulateAreaEdge(
        Dictionary<(long, long, long, long, long, long), (Vector3d A, Vector3d B, int Count)> edges,
        Vector3d a, Vector3d b, double weld)
    {
        (long x1, long y1, long z1) = QuantizeAreaVertex(a, weld);
        (long x2, long y2, long z2) = QuantizeAreaVertex(b, weld);

        // Canonical (unordered) key so edge (a,b) and (b,a) collapse to one entry.
        bool aFirst =
            x1 < x2 ||
            (x1 == x2 && (y1 < y2 ||
            (y1 == y2 && z1 <= z2)));
        var key = aFirst
            ? (x1, y1, z1, x2, y2, z2)
            : (x2, y2, z2, x1, y1, z1);

        if (edges.TryGetValue(key, out (Vector3d A, Vector3d B, int Count) existing))
            edges[key] = (existing.A, existing.B, existing.Count + 1);
        else
            edges[key] = (a, b, 1);
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementBuildersAreaTests"`
Expected: PASS (4 tests). If `ScenePlane`'s ctor differs, fix `FlatPlane()` first.

- [ ] **Step 6: Commit (PARENT repo)**

```
git add src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs src/FabricationAssistant.App.Tests/Measurement/MeasurementBuildersAreaTests.cs
git commit -m "feat(measure): AreaMeasurement record and BuildArea (area + perimeter)"
```

---

## Task A3: Area-aware unit formatting

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/IUnitSystemService.cs`
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/SceneUnitSystemService.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/SceneUnitSystemAreaTests.cs`

> The only implementer of `IUnitSystemService` is `SceneUnitSystemService` (verified by grep). If a later grep finds another implementer, add the two members there too.

- [ ] **Step 1: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/SceneUnitSystemAreaTests.cs`:
```csharp
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SceneUnitSystemAreaTests
{
    [Fact]
    public void AreaDisplayUnit_DefaultsToSquareMeters()
    {
        var units = new SceneUnitSystemService();
        Assert.Equal(UnitSystem.Meters, units.AreaDisplayUnit);
    }

    [Fact]
    public void FormatArea_UsesAreaDisplayUnitAndSquaredSuffix()
    {
        var units = new SceneUnitSystemService { AreaDisplayUnit = UnitSystem.Meters };
        Assert.Equal("1.23 m²", units.FormatArea(new SceneArea(1.2345)));
    }

    [Fact]
    public void FormatArea_ConvertsToMillimetresSquared()
    {
        var units = new SceneUnitSystemService { AreaDisplayUnit = UnitSystem.Millimeters };
        // 1e-6 m² == 1.00 mm²
        Assert.Equal("1.00 mm²", units.FormatArea(new SceneArea(1e-6)));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SceneUnitSystemAreaTests"`
Expected: FAIL to compile — `AreaDisplayUnit`/`FormatArea` do not exist.

- [ ] **Step 3: Add the interface members**

In `IUnitSystemService.cs`, add inside the interface:
```csharp
    UnitSystem AreaDisplayUnit { get; }

    string FormatArea(SceneArea area, int decimals = 2);
```

- [ ] **Step 4: Implement in `SceneUnitSystemService`**

In `SceneUnitSystemService.cs`, after the `DisplayUnit` property add:
```csharp
    /// <summary>
    /// Unit used to display areas. Independent of <see cref="DisplayUnit"/>; defaults to
    /// square metres. Source geometry is always millimetres, so this only affects how the
    /// area label is printed, never the canonical square-metre value.
    /// </summary>
    public UnitSystem AreaDisplayUnit { get; set; } = UnitSystem.Meters;
```
And after the `Format` method add:
```csharp
    public string FormatArea(SceneArea area, int decimals = 2)
    {
        UnitSystem unit = AreaDisplayUnit;
        double value = area.ConvertTo(unit);
        string suffix = unit switch
        {
            UnitSystem.Millimeters => "mm²",
            UnitSystem.Centimeters => "cm²",
            UnitSystem.Meters => "m²",
            UnitSystem.Inches => "in²",
            UnitSystem.Feet => "ft²",
            _ => "",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{value.ToString($"F{decimals}", CultureInfo.InvariantCulture)} {suffix}");
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SceneUnitSystemAreaTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit (PARENT repo)**

```
git add src/FabricationAssistant.Core/Measurement/Engine/IUnitSystemService.cs src/FabricationAssistant.Core/Measurement/Engine/SceneUnitSystemService.cs src/FabricationAssistant.App.Tests/Measurement/SceneUnitSystemAreaTests.cs
git commit -m "feat(measure): add AreaDisplayUnit and FormatArea to unit system"
```

---

## Task A4: Presenter `Area` case (label + outline + centroid)

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/MeasurementPresenterAreaTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/MeasurementPresenterAreaTests.cs`:
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeasurementPresenterAreaTests
{
    [Fact]
    public void Build_AreaMeasurement_EmitsCentroidLabelWithAreaAndPerimeter()
    {
        var units = new SceneUnitSystemService { AreaDisplayUnit = UnitSystem.Meters, DisplayUnit = UnitSystem.Meters };
        var presenter = new MeasurementPresenter(units);

        var plane = new ScenePlane(new Vector3d(0.5, 0.5, 0), new Vector3d(0, 0, 1), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), 0.75);
        IReadOnlyList<Vector3d> outline =
        [
            new(0, 0, 0), new(1, 0, 0),
            new(1, 0, 0), new(1, 1, 0),
        ];
        var m = new AreaMeasurement(MeasurementId.New(), plane, new SceneArea(1.0), new SceneLength(4.0), outline);

        IReadOnlyList<PresentationSnapshot> snaps = presenter.Build(
            new MeasurementResult[] { m }, selectedId: null, currentDraft: null, activeTool: MeasureToolMode.Area);

        PresentationSnapshot snap = Assert.Single(snaps);
        LabelPrimitive label = Assert.Single(snap.Labels);
        Assert.Contains("m²", label.Text);
        Assert.Contains("P ", label.Text);
        Assert.Equal(plane.Origin, label.Anchor);
        Assert.Equal(2, snap.Lines.Count);      // 2 outline segments
        Assert.Single(snap.Balls);              // centroid marker
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementPresenterAreaTests"`
Expected: FAIL — area currently falls to `Empty(...)`, so `snap.Labels` is empty (`Assert.Single` throws).

- [ ] **Step 3: Add the presenter case**

In `MeasurementPresenter.cs`, in the `BuildFor` switch (currently ending with `_ => Empty(...)`), add an arm before the `_`:
```csharp
        AreaMeasurement a => Area(a, style),
```
Then add the method (next to `F2f`):
```csharp
    private PresentationSnapshot Area(AreaMeasurement m, PresentationStyle style)
    {
        Vector3d centroid = m.Plane.Origin;
        string label = $"{_units.FormatArea(m.Area)}  ·  P {_units.Format(m.Perimeter)}";

        int segCount = m.OutlineSegments.Count / 2;
        var lines = new LinePrimitive[segCount];
        for (int i = 0; i < segCount; i++)
            lines[i] = new LinePrimitive(m.OutlineSegments[2 * i], m.OutlineSegments[2 * i + 1]);

        return new PresentationSnapshot(
            m.Id, style,
            Balls: new[] { new BallPrimitive(centroid) },
            Disks: Array.Empty<DiskPrimitive>(),
            Lines: lines,
            Labels: new[] { new LabelPrimitive(label, centroid, LabelAlignment.Billboard) });
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementPresenterAreaTests"`
Expected: PASS.

- [ ] **Step 5: Commit (PARENT repo)**

```
git add src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs src/FabricationAssistant.App.Tests/Measurement/MeasurementPresenterAreaTests.cs
git commit -m "feat(measure): presenter renders Area label, outline, and centroid"
```

---

## Task A5: Pick-kind + single-pick commit in the engine

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/MeasureTool.cs`
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/MeasurementSessionAreaTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/MeasurementSessionAreaTests.cs`:
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeasurementSessionAreaTests
{
    private static PickResult.PickedFace UnitSquareFacePick()
    {
        var plane = new ScenePlane(new Vector3d(0.5, 0.5, 0), new Vector3d(0, 0, 1), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), 0.75);
        IReadOnlyList<Vector3d> tris =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0),
            new(0, 0, 0), new(1, 1, 0), new(0, 1, 0),
        ];
        var highlight = new FaceHighlight(tris, FaceHighlightKind.Selected);
        return new PickResult.PickedFace(plane, new Vector3d(0.5, 0.5, 0), highlight, NodeId: null);
    }

    [Fact]
    public void Area_SingleFacePick_CommitsAnAreaMeasurementImmediately()
    {
        var store = new MeasurementStore();
        var units = new SceneUnitSystemService();
        units.SetUnitLabel("MM"); // metersPerSceneUnit = 0.001
        var session = new MeasurementSession(store, units, MeasurementTolerances.Default);

        session.SelectTool(MeasureToolMode.Area);
        session.SubmitPick(UnitSquareFacePick());

        MeasurementResult committed = Assert.Single(store.Snapshot());
        AreaMeasurement area = Assert.IsType<AreaMeasurement>(committed);
        Assert.Equal(1e-6, area.Area.SquareMeters, 12);   // 1 mm² square
        Assert.Null(session.CurrentDraft);                 // no lingering draft
    }
}
```

> Confirm `MeasurementStore` exposes a parameterless constructor and `Snapshot()` (it does — used by `AndroidMeasureIntegration`). Confirm `FaceHighlight`/`FaceHighlightKind` are in `FabricationAssistant.Core.Measurement.Presentation`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementSessionAreaTests"`
Expected: FAIL — `AdvanceDraft` has no `Area` arm, so no measurement is committed (`Assert.Single` throws).

- [ ] **Step 3: Add the pick kind in `MeasureTool`**

In `MeasureTool.cs`, in BOTH switch expressions that map mode → `PickKind` (the one in `HandleClick` ~line 44 and `NextPickKind` ~line 151), add an arm before `_ => PickKind.Point`:
```csharp
            MeasureToolMode.Area => PickKind.Face,
```

- [ ] **Step 4: Add `AdvanceArea` in `MeasurementSession`**

In `MeasurementSession.cs`, in `AdvanceDraft` (the `switch (ActiveTool)` ~line 262), add an arm before `_ => null`:
```csharp
            MeasureToolMode.Area => AdvanceArea(result),
```
Then add the method (next to `AdvanceF2f`):
```csharp
    private MeasurementDraft? AdvanceArea(PickResult result)
    {
        if (result is not PickResult.PickedFace pf) return CurrentDraft;

        IReadOnlyList<Vector3d> worldTris = pf.Highlight?.WorldVertices ?? Array.Empty<Vector3d>();
        MeasurementResult m = MeasurementBuilders.BuildArea(pf.Plane, worldTris, _units.MetersPerSceneUnit);
        m = WithAnchor(m, pf.NodeId);
        using var txn = _undoService.Begin($"Add {m.Mode} measurement");
        _store.Add(m);
        txn.Add(new MeasurementAddChange(m));

        // Single-pick commit (no draft existed): clear the hover fill so it does not
        // linger after the tap. SubmitPick's draft-collapse clear only fires when a
        // draft existed, which it never does for Area.
        HoverFace = null;
        return null;
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementSessionAreaTests"`
Expected: PASS.

- [ ] **Step 6: Run all measurement tests to catch regressions**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~Measurement"`
Expected: PASS (existing + new). If a desktop test enumerates all `MeasureToolMode` values or all `MeasurementResult` types and now trips on `Area`, add an `Area`/`AreaMeasurement` arm mirroring the `FaceToFace` arm.

- [ ] **Step 7: Commit (PARENT repo)**

```
git add src/FabricationAssistant.Core/Measurement/Engine/MeasureTool.cs src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs src/FabricationAssistant.App.Tests/Measurement/MeasurementSessionAreaTests.cs
git commit -m "feat(measure): Area mode picks a face and commits on a single tap"
```

---

# PHASE B — Android app (nested repo)

> All Phase B paths are under `Android/`. Commit from `C:\Users\skritikos\Desktop\Fabrication Assistant\Android` with `git add src/...`.

## Task B1: Persisted unit settings

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/AppSettings.cs`
- Test: `Android/src/FabricationAssistant.App.Android.Tests/AppSettingsAreaUnitsSourceTests.cs`

> AppSettings getters/setters need Android `SharedPreferences` and cannot run on the net8.0 host runner (see `AppSettingsMigrationSourceTests`). So the unit test asserts the source declares the two new properties with the right keys/defaults. No schema bump is needed: brand-new keys default correctly via `GetIntInRange`, and the read-side clamp guards corruption.

- [ ] **Step 1: Write the failing test**

Create `Android/src/FabricationAssistant.App.Android.Tests/AppSettingsAreaUnitsSourceTests.cs`:
```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~AppSettingsAreaUnitsSourceTests"`
Expected: FAIL — properties not present.

- [ ] **Step 3: Add the settings**

In `AppSettings.cs`, in the `// Measurement tools` region (after `MeasureModeSelectionIndex`, ~line 438), add:
```csharp
    // Measurement display units. Index maps to UnitSystem (0=mm, 1=cm, 2=m, 3=in, 4=ft).
    // Source geometry is always millimetres; these only change how labels are printed.
    public static int MeasurementLengthUnitIndex { get => GetIntInRange("measurement_length_unit", 0, 0, 4); set => Put("measurement_length_unit", System.Math.Clamp(value, 0, 4)); }
    public static int MeasurementAreaUnitIndex { get => GetIntInRange("measurement_area_unit", 2, 0, 4); set => Put("measurement_area_unit", System.Math.Clamp(value, 0, 4)); }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~AppSettingsAreaUnitsSourceTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit (ANDROID repo)**

```
git add src/FabricationAssistant.App.Android/AppSettings.cs src/FabricationAssistant.App.Android.Tests/AppSettingsAreaUnitsSourceTests.cs
git commit -m "feat(measure): persist length and area display unit settings"
```

---

## Task B2: Unit pickers in Settings → Measurement + live apply

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/PreferencesBottomSheet.cs`
- Modify: `Android/src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs`
- Modify: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`

> No unit test (UI + Android settings). Verified by build + the Phase C device smoke.

- [ ] **Step 1: Add the two pickers to the settings sheet**

In `PreferencesBottomSheet.cs`, in the Measurement section (after the "Box mode" row, ~line 283, before "Dimension text scale"):
```csharp
        AddLabeledToggleRow(ctx, measure, "Length unit", new[] { "mm", "cm", "m", "in", "ft" },
            AppSettings.MeasurementLengthUnitIndex, idx => AppSettings.MeasurementLengthUnitIndex = idx);
        AddLabeledToggleRow(ctx, measure, "Area unit", new[] { "mm²", "cm²", "m²", "in²", "ft²" },
            AppSettings.MeasurementAreaUnitIndex, idx => AppSettings.MeasurementAreaUnitIndex = idx);
```
`AddLabeledToggleRow` already stacks/shrinks for ≥4 options, and its `ToggleListener` fires `NotifySettingsChanged()` → `OnSettingsChanged` → `ApplySettingsToScene()`.

- [ ] **Step 2: Expose a unit setter on the integration**

In `AndroidMeasureIntegration.cs`, add a public method (after `ApplySettings`):
```csharp
    public void SetDisplayUnits(UnitSystem lengthUnit, UnitSystem areaUnit)
    {
        _units.DisplayUnit = lengthUnit;
        _units.AreaDisplayUnit = areaUnit;
        _invalidate();
    }
```
`UnitSystem` is already imported (`using FabricationAssistant.Core.Measurement.Domain;`).

- [ ] **Step 3: Push the units in `ApplyMeasurementSettings`**

In `MainActivity.cs`, in `ApplyMeasurementSettings()` (~line 16254), after the `_measure?.ApplySettings(...)` call and before/after `ApplySectionVisibility`:
```csharp
        _measure?.SetDisplayUnits(
            (UnitSystem)AppSettings.MeasurementLengthUnitIndex,
            (UnitSystem)AppSettings.MeasurementAreaUnitIndex);
```
`UnitSystem` resolves via the existing `using FabricationAssistant.Core.Measurement.Domain;` in MainActivity (it already uses `MeasureToolMode`). If the compiler reports `UnitSystem` ambiguous/missing, fully-qualify as `FabricationAssistant.Core.Measurement.Domain.UnitSystem`.

- [ ] **Step 4: Build the app to verify it compiles**

Run (from parent or via the project's normal Android build, e.g.): `dotnet build Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj`
Expected: Build succeeds. (If the Android workload isn't available in this environment, defer to the user's device build in Phase C; at minimum confirm `AndroidMeasureIntegration.cs` and `MainActivity.cs` compile via the Core/test build.)

- [ ] **Step 5: Commit (ANDROID repo)**

```
git add src/FabricationAssistant.App.Android/PreferencesBottomSheet.cs src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs src/FabricationAssistant.App.Android/MainActivity.cs
git commit -m "feat(measure): length/area unit pickers apply live to labels"
```

---

## Task B3: Toolbar button — icon, string, layout

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_measure_area.xml`
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/values/strings.xml`
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/layout/activity_main.xml`

- [ ] **Step 1: Create the icon**

Create `ic_measure_area.xml` (square with a diagonal hatch hint, matching the existing icon style):
```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24">
    <path
        android:fillColor="@android:color/transparent"
        android:pathData="M5,5h14v14h-14z"
        android:strokeColor="@color/fa_toolbar_icon_main"
        android:strokeLineCap="round"
        android:strokeLineJoin="round"
        android:strokeWidth="1.6" />
    <path
        android:fillColor="@android:color/transparent"
        android:pathData="M8,19l11,-11M12,19l7,-7M16,19l3,-3"
        android:strokeColor="@color/fa_toolbar_icon_accent"
        android:strokeLineCap="round"
        android:strokeLineJoin="round"
        android:strokeWidth="1.6" />
</vector>
```

- [ ] **Step 2: Add the content-description string**

In `strings.xml`, after `cd_measure_face_to_face` (~line 42):
```xml
    <string name="cd_measure_area">Area measurement</string>
```

- [ ] **Step 3: Add the toolbar button**

In `activity_main.xml`, insert this block immediately after the `measureFaceToFace` MaterialButton's closing tag (after the line `style="@style/FA.ViewportToolbarIconButton" />` that ends the `measureFaceToFace` button, ~line 659) and before the `measureBoundingBox` button:
```xml
        <com.google.android.material.button.MaterialButton
            android:id="@+id/measureArea"
            android:layout_width="40dp"
            android:layout_height="40dp"
            android:layout_marginStart="@dimen/fa_space_xs"
            android:visibility="gone"
            android:contentDescription="@string/cd_measure_area"
            app:icon="@drawable/ic_measure_area"
            app:iconTint="@null"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="@dimen/fa_viewport_toolbar_icon_size"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.ViewportToolbarIconButton" />
```

- [ ] **Step 4: Commit (ANDROID repo)**

```
git add src/FabricationAssistant.App.Android/Resources/drawable/ic_measure_area.xml src/FabricationAssistant.App.Android/Resources/values/strings.xml src/FabricationAssistant.App.Android/Resources/layout/activity_main.xml
git commit -m "feat(measure): add Area toolbar button icon, string, and layout"
```

---

## Task B4: Wire the Area button in MainActivity

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`

> Many small edits, all mirroring the existing `_measureFaceToFaceButton` pattern. No unit test; verified by build + device smoke.

- [ ] **Step 1: Add the field**

Near line 229 (after `_measureFaceToFaceButton`):
```csharp
    private MaterialButton? _measureAreaButton;
```

- [ ] **Step 2: Include it in the measure-button enumeration**

In the iterator near line 4691 (after `yield return _measureFaceToFaceButton;`):
```csharp
        yield return _measureAreaButton;
```

- [ ] **Step 3: FindViewById**

Near line 5136 (after the `measureFaceToFace` lookup):
```csharp
        _measureAreaButton = FindViewById<MaterialButton>(Resource.Id.measureArea);
```

- [ ] **Step 4: Attach the click handler**

Near line 5226 (after the `_measureFaceToFaceButton.Click += ...` block):
```csharp
        if (_measureAreaButton is not null)
            _measureAreaButton.Click += OnMeasureAreaClicked;
```

- [ ] **Step 5: Add the handler method**

Near line 5458 (after `OnMeasureFaceToFaceClicked`):
```csharp
    private void OnMeasureAreaClicked(object? sender, EventArgs e) => SetMeasureMode(MeasureToolMode.Area);
```

- [ ] **Step 6: Show/hide with the measure toolbar**

Near line 5831 (after `SetVisibility(_measureFaceToFaceButton, measureVisibility);`):
```csharp
        SetVisibility(_measureAreaButton, measureVisibility);
```

- [ ] **Step 7: Reflect selected state**

Near line 6364 (after `SetSelected(_measureFaceToFaceButton, activeMode == MeasureToolMode.FaceToFace);`):
```csharp
        SetSelected(_measureAreaButton, activeMode == MeasureToolMode.Area);
```

- [ ] **Step 8: Make `Area` an interactive measure mode**

In `IsInteractiveMeasureMode` (~line 10983):
```csharp
    private static bool IsInteractiveMeasureMode(MeasureToolMode mode)
        => mode == MeasureToolMode.PointToPoint
           || mode == MeasureToolMode.FaceToPoint
           || mode == MeasureToolMode.FaceToFace
           || mode == MeasureToolMode.Area;
```

- [ ] **Step 9: Don't corrupt the default-tool setting when Area activates**

In `ActivateInteractiveMeasureMode` (~line 10945), replace the unconditional persist:
```csharp
        AppSettings.MeasureModeSelectionIndex = MeasureModeToSettingsIndex(mode);
```
with a guard (the "Default measure tool" toggle only covers Point/Face-Point/Face-Face; Area must not overwrite it):
```csharp
        if (mode is MeasureToolMode.PointToPoint or MeasureToolMode.FaceToPoint or MeasureToolMode.FaceToFace)
            AppSettings.MeasureModeSelectionIndex = MeasureModeToSettingsIndex(mode);
```

- [ ] **Step 10: Add the tooltip**

In `ApplyMeasureTooltips` (~line 10993, after the `measureFaceToFace` tooltip):
```csharp
        SetTooltip(_measureAreaButton, Resource.String.cd_measure_area);
```

- [ ] **Step 11: Detach the click handler on teardown**

Near line 15895 (after `DetachClick(_measureFaceToFaceButton, OnMeasureFaceToFaceClicked);`):
```csharp
        DetachClick(_measureAreaButton, OnMeasureAreaClicked);
```

- [ ] **Step 12: Build to verify it compiles**

Run: `dotnet build Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj`
Expected: Build succeeds (or defer to the user's device build if the Android workload is unavailable here).

- [ ] **Step 13: Commit (ANDROID repo)**

```
git add src/FabricationAssistant.App.Android/MainActivity.cs
git commit -m "feat(measure): wire Area toolbar button into the measure toolbar"
```

---

# PHASE C — Cross-platform safety & verification

## Task C1: Full Core + desktop suite stays green

**Files:** none (verification; fix-forward only)

- [ ] **Step 1: Build the whole solution**

Run from `C:\Users\skritikos\Desktop\Fabrication Assistant`:
```
dotnet build FabricationAssistant.sln
```
Expected: Build succeeds. The new enum value/type only produce non-fatal exhaustiveness warnings (no `TreatWarningsAsErrors`). The desktop `FaViewerStateService`, Android `AndroidViewerStateSaveService`, and AI-API `ViewerMeasurement` all have graceful `default`/`_` arms, so they compile and degrade an area measurement to `"unknown"` rather than throwing.

> If the Android workload is not installed in this environment, build the non-Android projects instead: `dotnet build src/FabricationAssistant.App/FabricationAssistant.App.csproj` and `dotnet build src/FabricationAssistant.XrHost.OpenXR/FabricationAssistant.XrHost.OpenXR.csproj`, and leave the Android APK build to the user's device loop.

- [ ] **Step 2: Run the full desktop test suite**

Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj`
Expected: PASS. If any test enumerates every `MeasureToolMode` or every `MeasurementResult` subtype and now fails on `Area`/`AreaMeasurement`, add an arm mirroring `FaceToFace`/`FaceToFaceMeasurement` (area is a valid mode that commits on one face pick; the presenter renders it).

- [ ] **Step 3: Run the Android logic test suite**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
Expected: PASS (existing ~210 + the new `AppSettingsAreaUnitsSourceTests`).

- [ ] **Step 4: Commit any fix-forward changes**

Commit Core/desktop fixes to the PARENT repo and any Android fixes to the ANDROID repo, e.g.:
```
git commit -am "test(measure): handle Area in exhaustive measurement consumers"
```
(Skip if Steps 2–3 passed with no changes.)

---

## Task C2: Device smoke test (manual, user's loop)

**Files:** none

- [ ] **Step 1: Deploy and exercise the Area tool**

Build/deploy the Android app to the device (user's normal loop). Then:
1. Open a model, open the Measure toolbar — confirm the new **Area** button appears between Face-Face and Bounding-Box.
2. Tap **Area**; hover over a flat face — confirm the translucent face highlight appears (same as Face-Face hover).
3. Tap a flat face — confirm a label like `1.23 m²  ·  P 4.50 m` appears at the face centroid, with an outline around the measured face.
4. Orbit the camera — confirm the label tracks the face and stays legible.
5. Open Settings → Measurement → change **Area unit** to `mm²` and **Length unit** to `mm` — confirm the existing area label re-prints in the new units immediately (e.g. `1230000.00 mm² · P 4500.00 mm`).
6. Undo — confirm the area measurement is removed.
7. If "Multi-measure" is on, add several area measurements; if off, confirm only the latest remains.

Expected: all behaviors as described. File any deviations as new tasks.

---

## Task C3: Finish the branch

- [ ] **Step 1: Use the finishing-a-development-branch skill**

Invoke `superpowers:finishing-a-development-branch` to decide how to integrate both repos' `feat/area-measure-tool` branches (merge to `master` / PR / cleanup). Remember: there are two repos to integrate.

---

## Out of scope (deferred, documented)

- **Save/restore persistence of area measurements.** Both state serializers (`FaViewerStateService`, `AndroidViewerStateSaveService`) currently degrade an `AreaMeasurement` to a harmless `"unknown"` DTO — no crash, but the measurement is not restored. Full persistence would add an `"area"` DTO kind (area, perimeter, plane, outline) + encode/decode on both desktop and Android plus a round-trip test. Propose as a follow-up.
- **Desktop / OpenXR Area UI.** Those hosts compile against the shared Core and gain the `Area` mode, but no Area button is added to their toolbars in this plan.
- **AI/MCP API exposure** of area measurements (currently surfaces as `"unknown"` via `ViewerMeasurement`).
- **Curved-surface ("whole connected surface") area.** Only the coplanar patch under the tap is measured, consistent with the other face tools.

---

## Self-review notes (resolved)

- **Spec coverage:** Area mode (A5), face-detection reuse (A5 via `PickedFace.Highlight`), area+perimeter (A2/A4), two unit pickers default mm/m² (B1/B2), units affect printing only / source always mm (A1 squared factor + A3 `FromSceneUnits` with `MetersPerSceneUnit`), label like other tools (A4 + label layer reuse), single-tap commit (A5), hover fill (A5 pick-kind + existing hover path). All covered.
- **Type consistency:** `BuildArea(ScenePlane, IReadOnlyList<Vector3d>, double)` → `AreaMeasurement(Id, Plane, SceneArea, SceneLength, IReadOnlyList<Vector3d> OutlineSegments, bool)`; presenter reads `OutlineSegments`/`Plane`/`Area`/`Perimeter`; `FormatArea(SceneArea)`/`AreaDisplayUnit` on `IUnitSystemService`; `SetDisplayUnits(UnitSystem, UnitSystem)` on integration. Consistent across tasks.
- **Assumptions (all verified against source):** `ScenePlane(Origin, Normal, U, V, Radius)`; `Vector3d.Length` property and `Vector3d.Cross(a,b)` static; `MeasurementStore` parameterless ctor + `Snapshot()`; `MeasurementSession(store, units, MeasurementTolerances.Default)`; `IUnitSystemService`'s sole implementer is `SceneUnitSystemService`; serializers (`FaViewerStateService`, `AndroidViewerStateSaveService`) and AI API (`ViewerMeasurement`) all have graceful default arms; no `TreatWarningsAsErrors`.
