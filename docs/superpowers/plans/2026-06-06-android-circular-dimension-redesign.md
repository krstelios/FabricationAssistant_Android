# Circular-Feature (Diameter / Arc) Dimension Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the full-revolution-only circular-feature detector with a curvature-aware, feature-edge-bounded segmentation + analytic-fit pipeline that reliably dimensions holes/bosses (Ø), fillets/rounds (R, cylinder + torus) and chamfers (w×θ), never leaks across faces, never commits a low-confidence value, and draws professional ISO-leaning callouts.

**Architecture:** A new `CircularFeatureDetectionPipeline` orchestrates small single-purpose units — `MeshTopologyCache` (welded half-edge + crease classification + curvature, built once per mesh), `SeedResolver` (aperture-cone, front-face, curvature-biased), `SurfaceRegionGrower` (curvature-continuous, crease-bounded, grows against a running fit), `AnalyticSurfaceFitter` (5-DOF cylinder + torus via RANSAC+Gauss-Newton), `EdgeLoopFitter`, `ChamferDetector`, `FeatureClassifier`, `FitAcceptanceGate`, and `PickFeedbackAndCommitGuard` — feeding new presentation primitives (arrowheads, leaders, center marks, confidence) rendered by the Android GLES overlay. The old `CircularFeatureDetectionService` monolith is decomposed and retired.

**Tech Stack:** C# / .NET 8, `FabricationAssistant.Core.Math` (Vector3d/Matrix4d), xUnit tests, OpenGL ES overlay (Android). No B-Rep — mesh-only; reported radius is the mesh-approximate value with residual surfaced.

**Spec:** `docs/superpowers/specs/2026-06-06-android-circular-dimension-redesign-design.md`

---

## Repositories, build & test (read once)

This solution spans **two git repos**:

| Repo | Root | Holds | Commit here |
|---|---|---|---|
| **PARENT** | `C:/Users/skritikos/Desktop/Fabrication Assistant` | `src/FabricationAssistant.Core/**` (Engine + Presentation + Domain), host tests `src/FabricationAssistant.App.Tests/**` | Core/Presentation/host-test changes |
| **ANDROID** | `…/Fabrication Assistant/Android` (nested, gitignored by parent) | `src/FabricationAssistant.Rendering.Gles/**`, `src/FabricationAssistant.App.Android/**`, `src/FabricationAssistant.App.Android.Tests/**`, the Android-local `Core.Android` `EdgeSnapService` override | Android changes (stage by **explicit path**) |

- New `.cs` files under `src/FabricationAssistant.Core/Measurement/**` are **auto compile-linked** into Android (wildcard `<Compile Include>`), so no Android csproj edit is needed for new Core types — **except** the Android test project (`App.Android.Tests`) which links Core source **selectively**; add an explicit `<Compile Include>` there when an Android-mirror test needs a Core type that isn't already linked.
- **Host tests** (from PARENT root): `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` (TFM `net8.0-windows`).
- **Android tests** (from PARENT root): `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`.
- **Device re-validation:** `adb -s R52Y80CE37L logcat -d -v time -s FA.MeasureCircular:V FA.Measure:V` after deploying (`dotnet build Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj -c Debug -t:SignAndroidPackage`).
- **Build-lock workaround:** on `CSC error CS2012` / file-lock, run `dotnet build-server shutdown` then retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`.

---

## File-structure map (decomposition)

**PARENT — `src/FabricationAssistant.Core/Measurement/Engine/`** (new unless noted):
- `MeshTopology.cs` — immutable welded half-edge topology + per-tri data/curvature/edge classes (data type).
- `MeshTopologyCache.cs` — `IMeshTopologyProvider`, LRU build-once cache.
- `SeedResolver.cs` — aperture-cone, front-face, curvature-biased seed.
- `SurfaceRegionGrower.cs` — curvature-continuous, crease-bounded BFS vs running fit.
- `AnalyticSurfaceFitter.cs` — 5-DOF cylinder + torus fit (RANSAC + Gauss-Newton).
- `EdgeLoopFitter.cs` — nearest closed crease/boundary loop → circle.
- `ChamferDetector.cs` — narrow band → width + angle.
- `FeatureClassifier.cs` — Ø / R / chamfer from topology + coverage hysteresis.
- `FitAcceptanceGate.cs` — RMS/R + inlier-fraction + radius-bound acceptance.
- `CircularFeatureDetectionPipeline.cs` — orchestrator + `CircularDetectionConfig`.
- *Modify* `MeshMeasurePicker.cs` (route pick/hover to the pipeline in local space), `MeasurementSession.cs` (commit guard), `MeasurementBuilders.cs` (recomputed value), `PickTypes.cs` (extend `CircularFeaturePick`).
- *Retire* `CircularFeatureDetectionService.cs` (decomposed into the above).

**PARENT — `src/FabricationAssistant.Core/Measurement/Presentation/`**:
- *Modify* `Primitives.cs` (+ Arrowhead/Leader/CenterMark + `DimensionConfidence`), `PresentationSnapshot.cs`, `MeasurementPresenter.cs` (pro Ø/R/chamfer drawing), `Engine/SceneUnitSystemService.cs` (unit/precision auto-format).

**ANDROID — `src/FabricationAssistant.Rendering.Gles/`**:
- *Modify* `GlesMeasurementOverlay.cs` (rasterize arrowheads/leaders/center-marks, confidence styling).
- *Modify* `src/FabricationAssistant.App.Android/Measurement/AndroidMeasureRaycaster.cs` (drop `preferredNode` override; front-face policy).

**PARENT — `src/FabricationAssistant.App.Tests/Measurement/`** & **ANDROID — `src/FabricationAssistant.App.Android.Tests/`**:
- `CircularMeshFixtures.cs`, `GeoAssert.cs` (synthetic meshes + helpers), per-component test files, `CircularFeatureTestMatrix` + Android mirror + perf guard.

---

## Task ordering

Tasks are grouped A→L and run in dependency order (A foundation → B topology → C–H units → I pipeline+integration → J–K presentation → L full matrix). Each task is bite-sized TDD: write failing test → run (FAIL) → minimal impl → run (PASS) → commit to the correct repo.

## Pre-execution corrections (authoritative — apply globally)

These supersede any conflicting text inside individual tasks. They resolve artifacts from parallel authoring and were verified against the codebase + a consistency pass.

1. **One creator per file.** Group A creates ONLY the test fixtures + `GeoAssert` (Tasks A.1–A.7). The former "contract stub" tasks (A.8–A.14) are **removed**; every Core/Presentation type is created by its owning group's first task: topology `EdgeClass`/`TriData`/`EdgeRef`/`TriCurvature`/`MeshTopology`/`IMeshTopologyProvider`/`MeshTopologyCache` (B.1/B.2/B.6); `SurfaceFit`/`SurfaceKind`/`AnalyticSurfaceFitter` (E.1/E.2); `CircularSeed`/`SeedResolver` (C.1); `RegionGrowOptions`/`SurfaceRegionGrower` (D.1); `CircleFit`/`EdgeLoopFitter`/`CircularKind`/`ClassifierOptions`/`FeatureClassifier`/`CoverageEstimator` (F.x); `ChamferFit`/`ChamferDetector` (G.1); `AcceptanceOptions`/`RejectReason`/`AcceptanceResult`/`FitAcceptanceGate` (H.1, file `FitAcceptance.cs`); `CircularDetectionConfig`/`CircularDetectionResult`/`CircularFeatureDetectionPipeline` (I.2/I.3/I.4). Each owning group's first task MUST declare every contract type assigned to it (so it compiles standalone).

2. **Build / dependency order (overrides the A–L label order).** Execute: **A → B → E → C → D → F → G → H → I → J → K → L**. Rationale: E defines `SurfaceFit`/`SurfaceKind` consumed by C/D/F/I; B's topology is consumed by C/D/F/G.

3. **Fixture namespace.** `CircularMeshFixtures` and `GeoAssert` declare `namespace FabricationAssistant.App.Tests.Measurement` (NOT `…Measurement.Fixtures`) so every consumer's `using FabricationAssistant.App.Tests.Measurement;` binds. Folder under `Measurement/Fixtures/` is fine — only the `namespace` line matters.

4. **InternalsVisibleTo — do FIRST, before any test that reads an internal seam.** Core has none today. Create `[PARENT] src/FabricationAssistant.Core/Properties/AssemblyInfo.cs`:
   ```csharp
   using System.Runtime.CompilerServices;
   [assembly: InternalsVisibleTo("FabricationAssistant.App.Tests")]
   [assembly: InternalsVisibleTo("FabricationAssistant.App.Android.Tests")]
   ```
   Commit in the PARENT repo. Every `internal` test seam (`*ForTest`, `Candidate`, `ClassifyKind`, …) depends on it.

5. **Android-test Core links.** When an Android-mirror test needs a Core type, add `<Compile Include>` in `Android/src/FabricationAssistant.App.Android.Tests/...csproj` using the EXISTING form: `Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\<File>.cs" LinkBase="Linked\Measurement\Engine"` (NOT a bare `..\..\..` with `Link=`). Fixtures are at `…App.Tests\Measurement\Fixtures\CircularMeshFixtures.cs`. There is no ProjectReference from the Android test project to the host test project, so cross-linked paths must be exact. Commit the csproj change in the ANDROID repo.

6. **Shared "modify" files — edit once.** `PickTypes.cs` (`CircularFeaturePick` +4 trailing optional fields `SurfaceKind Kind, double GeometricRms, double Confidence, double TubeRadius`) is referenced by both I and H.4 — apply the extension at the FIRST task that needs it and treat later mentions as "fields already present." `PresentationSnapshot.cs` and `Primitives.cs` are owned by **Group J** (J replaces the `PresentationSnapshot` body and appends the new primitives to `Primitives.cs`); there is no separate `CircularDimensionPrimitives.cs`.

7. **Minor task-body fixes.** A.6 `Chamfer` test: delete the unused `minU/maxU/inXZ` scratch loop, keep only the `edge0` chord assertion. B.2 Step 3: ship `MeshTopology` complete now with empty `Edges`/`Curvatures`/`SmoothNeighbors`, filled in B.3/B.5 (ignore the garbled "is not acceptable" sentence). Early fixture tasks: run `--filter "FullyQualifiedName~CircularMeshFixtures|FullyQualifiedName~GeoAssert"` so the green run is observable.

8. **Spec items to confirm in Group I.** Wiring `MeshTopologyCache` build to the off-thread `AndroidSnapWarmupRunner` (spec §6.1, §12) and moving the static `MeshMeasurePicker.DiagnosticsLog`/`EdgeSnapService` sinks to instance-scoped observers (spec §15) belong in Group I integration; if no I task covers them, add one.

---

## Group A — Test Foundation (fixtures + helpers)

> **Preamble (read once).** This group lays the bedrock every later group binds against: a fully-implemented synthetic-mesh fixture library `CircularMeshFixtures` with real trig, plus `GeoAssert`. (Per correction #1 above, Group A no longer creates contract stubs — each contract type is created by its owning group's first task.) After this group `dotnet test --filter "FullyQualifiedName~CircularMeshFixtures|FullyQualifiedName~GeoAssert"` is green.
>
> **Two repos.** Everything in Group A is **PARENT-repo** work. PARENT root = `C:/Users/skritikos/Desktop/Fabrication Assistant`. Fixtures go under `src/FabricationAssistant.App.Tests/Measurement/`; contract stubs under `src/FabricationAssistant.Core/Measurement/Engine/` and `.../Presentation/`. New `.cs` under `src/FabricationAssistant.Core/Measurement/**` are auto compile-linked into Android via a wildcard — **no csproj edit**. Commit all Group-A changes in the **PARENT** repo. (The Android-test-project selective-link / `<Compile Include>` step only becomes relevant in later groups when an Android mirror test needs a type the Android csproj doesn't yet link — not in Group A.)
>
> **Math facts to obey.** `FabricationAssistant.Core.Math.Vector3d` exposes `Length` and `LengthSquared` as **properties** (not methods); `Vector3d.Dot`, `Vector3d.Cross`, `.Normalized()`, `Vector3d.BuildPerpendicular(n)`, operators `+ - * /` and unary `-`, and `Vector3d.UnitX/Y/Z/Zero`. The host test TFM is `net8.0-windows`; test namespace root is `FabricationAssistant.App.Tests.Measurement`. Existing style reference: `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionServiceTests.cs` (xUnit, `Assert.Equal(expected, actual, precision)`).
>
> **Build-lock.** On `CS2012` / file-lock during a build or test, run `dotnet build-server shutdown` and retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`. (Mentioned once here; assume it applies to every `dotnet` step below.)
>
> **Host test command (all tasks):** from PARENT root
> `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"`

---

### Task A.1: GeoAssert helper + axis-parallel assertion

**Files:**
- Create/Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/GeoAssert.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/GeoAssertTests.cs`

- [ ] **Step 1: Write the failing test.** Drives `GeoAssert.AxisParallel`: parallel and antiparallel pass at default threshold; perpendicular throws.

```csharp
using FabricationAssistant.Core.Math;
using Xunit;
using Xunit.Sdk;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class GeoAssertTests
{
    [Fact]
    public void AxisParallel_SameDirection_Passes()
        => GeoAssert.AxisParallel(Vector3d.UnitZ, new Vector3d(0, 0, 1).Normalized());

    [Fact]
    public void AxisParallel_AntiParallel_Passes()
        => GeoAssert.AxisParallel(Vector3d.UnitZ, new Vector3d(0, 0, -1).Normalized());

    [Fact]
    public void AxisParallel_NearlyParallel_WithinTolerance_Passes()
    {
        // ~5.7° off => |dot| ≈ 0.995 > 0.99 default
        Vector3d tilted = new Vector3d(0.1, 0, 1).Normalized();
        GeoAssert.AxisParallel(Vector3d.UnitZ, tilted);
    }

    [Fact]
    public void AxisParallel_Perpendicular_Throws()
        => Assert.Throws<XunitException>(() => GeoAssert.AxisParallel(Vector3d.UnitZ, Vector3d.UnitX));
}
```

- [ ] **Step 2: Run it, expect FAIL.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` — fails to **compile**: `GeoAssert` does not exist (CS0103). (Run with `--filter` containing `CircularFeature`; this test file lives in the same compilation, so the build break still surfaces.)

- [ ] **Step 3: Implement.** Real assertion using `Assert.True` so failures surface as `XunitException`.

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

internal static class GeoAssert
{
    /// <summary>
    /// Asserts two axes are (anti)parallel: |dot(a.Normalized, b.Normalized)| ≥ minAbsDot.
    /// Direction sign is irrelevant for an axis, so antiparallel passes.
    /// </summary>
    public static void AxisParallel(Vector3d a, Vector3d b, double minAbsDot = 0.99)
    {
        double dot = Vector3d.Dot(a.Normalized(), b.Normalized());
        Assert.True(
            System.Math.Abs(dot) >= minAbsDot,
            $"Axes not parallel: |dot| = {System.Math.Abs(dot):F6} < {minAbsDot:F6} (a={a}, b={b}).");
    }
}
```

- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` — `GeoAssertTests` (4 tests) green.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/GeoAssert.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/GeoAssertTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add GeoAssert.AxisParallel helper for circular-feature fixtures

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task A.2: CircularMeshFixtures — Cylinder generator

**Files:**
- Create `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesCylinderTests.cs`

- [ ] **Step 1: Write the failing test.** A 32-seg full cylinder R=2, H=5 about Z: vertex count, every vertex at radius 2, every triangle's outward face normal radial (points away from axis). Then a 90° sweep (`Math.PI/2`) is open (segments+1 rings).

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class CircularMeshFixturesCylinderTests
{
    [Fact]
    public void Cylinder_FullRevolution_HasExpectedTopologyAndRadius()
    {
        var (v, i) = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 32, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);

        // closed: `segments` rings × 2 (bottom/top) = 64 vertices; 32 quads × 2 tris × 3 = 192 indices.
        Assert.Equal(64, v.Length);
        Assert.Equal(192, i.Length);

        foreach (Vector3d p in v)
        {
            double r = System.Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.Equal(2.0, r, 9);
        }
    }

    [Fact]
    public void Cylinder_TriangleNormals_PointRadiallyOutward()
    {
        var (v, i) = CircularMeshFixtures.Cylinder(2.0, 5.0, 16, System.Math.Tau, Vector3d.UnitZ);

        for (int t = 0; t < i.Length; t += 3)
        {
            Vector3d a = v[i[t]], b = v[i[t + 1]], c = v[i[t + 2]];
            Vector3d n = Vector3d.Cross(b - a, c - a).Normalized();
            Vector3d centroid = (a + b + c) / 3.0;
            Vector3d radialOut = new Vector3d(centroid.X, centroid.Y, 0).Normalized();
            Assert.True(Vector3d.Dot(n, radialOut) > 0.9,
                $"Triangle {t / 3} normal {n} not outward (radial {radialOut}).");
        }
    }

    [Fact]
    public void Cylinder_PartialSweep_IsOpen()
    {
        var (v, _) = CircularMeshFixtures.Cylinder(3.0, 4.0, 12, System.Math.PI / 2, Vector3d.UnitZ);
        // open sweep: (segments+1) rings × 2 = 26 vertices.
        Assert.Equal(26, v.Length);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** Run host command. Fails to compile: `CircularMeshFixtures` does not exist (CS0103).

- [ ] **Step 3: Implement.** Create `CircularMeshFixtures.cs` containing the `Cylinder` generator (other generators added in later A-tasks). Winds CCW so the outward normal is radial; respects a general `axis` by building a perpendicular basis.

```csharp
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

/// <summary>
/// Fully-implemented synthetic CAD-feature meshes for circular-dimension detection tests.
/// Each generator returns (vertices, indices); triangles wound CCW so the geometric
/// face normal (cross(b-a, c-a)) points OUTWARD, except where a method comments otherwise
/// (e.g. HoleInPlate emits INWARD wall normals). All meshes are in local mesh space.
/// </summary>
internal static partial class CircularMeshFixtures
{
    /// <summary>
    /// A cylindrical wall (no caps) of given radius/height about <paramref name="axis"/>.
    /// Full revolution (sweep ≈ Tau) welds the seam (`segments` rings); a partial sweep
    /// leaves it open (`segments+1` rings). Triangle normals point radially outward.
    /// </summary>
    public static (Vector3d[] v, int[] i) Cylinder(
        double radius, double height, int segments, double sweepRadians, Vector3d axis)
    {
        Vector3d w = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(w);   // radial basis vector 1
        Vector3d vv = Vector3d.Cross(w, u).Normalized(); // radial basis vector 2

        bool closed = System.Math.Abs(sweepRadians - System.Math.Tau) < 1e-9;
        int ringCount = closed ? segments : segments + 1;

        var verts = new Vector3d[ringCount * 2];
        for (int r = 0; r < ringCount; r++)
        {
            double angle = closed
                ? r * System.Math.Tau / segments
                : r * sweepRadians / segments;
            Vector3d radial = (u * System.Math.Cos(angle) + vv * System.Math.Sin(angle)) * radius;
            verts[2 * r] = radial;                 // bottom ring vertex
            verts[2 * r + 1] = radial + w * height; // top ring vertex
        }

        var idx = new List<int>(segments * 6);
        for (int s = 0; s < segments; s++)
        {
            int next = closed ? (s + 1) % segments : s + 1;
            int b0 = 2 * s, t0 = b0 + 1, b1 = 2 * next, t1 = b1 + 1;
            // CCW so cross(b-a,c-a) faces away from the axis.
            idx.Add(b0); idx.Add(b1); idx.Add(t1);
            idx.Add(b0); idx.Add(t1); idx.Add(t0);
        }

        return (verts, idx.ToArray());
    }
}
```

- [ ] **Step 4: Run it, expect PASS.** Run host command. `CircularMeshFixturesCylinderTests` (3 tests) green.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesCylinderTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add CircularMeshFixtures.Cylinder synthetic generator

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task A.3: CircularMeshFixtures — HoleInPlate (INWARD wall normals)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesHoleTests.cs`

- [ ] **Step 1: Write the failing test.** A circular hole bored through a square plate. The interior wall normals must point **inward** (toward the axis) — this is what distinguishes a hole/bore from a boss and is required to kill the `abs()` ambiguity. Asserts wall radius and inward normals; asserts plate-top vertices lie at z = thickness/2 face (flat).

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class CircularMeshFixturesHoleTests
{
    [Fact]
    public void HoleInPlate_WallVerticesAtHoleRadius()
    {
        var (v, _) = CircularMeshFixtures.HoleInPlate(radius: 1.5, plate: 6.0, thickness: 1.0, segments: 24);

        // The wall vertices are exactly those at radius 1.5 (within fp); at least 24*2 of them.
        int onWall = v.Count(p => System.Math.Abs(System.Math.Sqrt(p.X * p.X + p.Y * p.Y) - 1.5) < 1e-9);
        Assert.True(onWall >= 48, $"Expected >=48 wall vertices at r=1.5, got {onWall}.");
    }

    [Fact]
    public void HoleInPlate_WallNormals_PointInward()
    {
        var (v, i) = CircularMeshFixtures.HoleInPlate(1.5, 6.0, 1.0, 24);

        int wallTriangles = 0;
        for (int t = 0; t < i.Length; t += 3)
        {
            Vector3d a = v[i[t]], b = v[i[t + 1]], c = v[i[t + 2]];
            Vector3d centroid = (a + b + c) / 3.0;
            double rc = System.Math.Sqrt(centroid.X * centroid.X + centroid.Y * centroid.Y);
            if (System.Math.Abs(rc - 1.5) > 1e-6) continue; // only the bore wall

            wallTriangles++;
            Vector3d n = Vector3d.Cross(b - a, c - a).Normalized();
            Vector3d radialIn = new Vector3d(-centroid.X, -centroid.Y, 0).Normalized();
            Assert.True(Vector3d.Dot(n, radialIn) > 0.9,
                $"Bore-wall triangle {t / 3} normal {n} not INWARD (expected {radialIn}).");
        }
        Assert.True(wallTriangles >= 48, $"Expected >=48 wall triangles, got {wallTriangles}.");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** Run host command. Compile failure: `HoleInPlate` not defined (CS0103).

- [ ] **Step 3: Implement.** Append the `HoleInPlate` generator to `CircularMeshFixtures` (same partial class). Builds a square top face (z = +thickness/2) with a circular hole using a quad fan from the rim to the plate corners, plus a bore wall whose triangles are wound so the normal points **inward**.

```csharp
// Add inside internal static partial class CircularMeshFixtures (same file, after Cylinder):

/// <summary>
/// A square plate (side <paramref name="plate"/>, centered, normal +Z) with a circular
/// through-hole of <paramref name="radius"/> centered on the Z axis. Emits the top face
/// ring, the bottom face ring, and the interior bore WALL whose triangle normals point
/// radially INWARD (toward the axis) — the defining trait of a bore vs a boss.
/// </summary>
public static (Vector3d[] v, int[] i) HoleInPlate(double radius, double plate, double thickness, int segments)
{
    double half = plate * 0.5, zTop = thickness * 0.5, zBot = -thickness * 0.5;
    var verts = new List<Vector3d>();
    var idx = new List<int>();

    int Add(Vector3d p) { verts.Add(p); return verts.Count - 1; }

    // Rings on the hole rim, top and bottom.
    int[] rimTop = new int[segments];
    int[] rimBot = new int[segments];
    for (int s = 0; s < segments; s++)
    {
        double ang = s * System.Math.Tau / segments;
        double x = System.Math.Cos(ang) * radius, y = System.Math.Sin(ang) * radius;
        rimTop[s] = Add(new Vector3d(x, y, zTop));
        rimBot[s] = Add(new Vector3d(x, y, zBot));
    }

    // Plate outer corners (top + bottom).
    int ctTL = Add(new Vector3d(-half, half, zTop));
    int ctTR = Add(new Vector3d(half, half, zTop));
    int ctBR = Add(new Vector3d(half, -half, zTop));
    int ctBL = Add(new Vector3d(-half, -half, zTop));
    int cbTL = Add(new Vector3d(-half, half, zBot));
    int cbTR = Add(new Vector3d(half, half, zBot));
    int cbBR = Add(new Vector3d(half, -half, zBot));
    int cbBL = Add(new Vector3d(-half, -half, zBot));

    void Tri(int a, int b, int c) { idx.Add(a); idx.Add(b); idx.Add(c); }

    // Bore WALL: connect top rim to bottom rim; wind so normal points INWARD.
    // For an outward-radial point, CCW order (rimTop[s], rimBot[s], rimBot[next]) would face
    // outward; we reverse it to (rimTop[s], rimBot[next], rimBot[s]) so cross(...) points inward.
    for (int s = 0; s < segments; s++)
    {
        int n = (s + 1) % segments;
        Tri(rimTop[s], rimBot[n], rimBot[s]);
        Tri(rimTop[s], rimTop[n], rimBot[n]);
    }

    // Top face (a ring fan from rim to the four corners, +Z normal). 4 sectors.
    int[] cornersTop = { ctTR, ctTL, ctBL, ctBR }; // quadrant corners CCW
    for (int s = 0; s < segments; s++)
    {
        int n = (s + 1) % segments;
        int corner = cornersTop[(int)(s * 4L / segments)];
        Tri(rimTop[s], corner, rimTop[n]); // CCW about +Z
    }
    // Plate corner triangles to close the top quad outline.
    Tri(ctTR, ctTL, ctBL);
    Tri(ctTR, ctBL, ctBR);

    // Bottom face (−Z normal, reversed winding). 4 sectors.
    int[] cornersBot = { cbTR, cbTL, cbBL, cbBR };
    for (int s = 0; s < segments; s++)
    {
        int n = (s + 1) % segments;
        int corner = cornersBot[(int)(s * 4L / segments)];
        Tri(rimBot[s], rimBot[n], corner); // CW about +Z == CCW about −Z
    }
    Tri(cbTR, cbBL, cbTL);
    Tri(cbTR, cbBR, cbBL);

    return (verts.ToArray(), idx.ToArray());
}
```

- [ ] **Step 4: Run it, expect PASS.** Run host command. `CircularMeshFixturesHoleTests` (2 tests) green. (If a corner-fan triangle accidentally lands on a wall radius, the wall-normal test filters by `rc ≈ radius`; bore-wall triangles all satisfy it.)

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesHoleTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add HoleInPlate fixture with inward bore-wall normals

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task A.4: CircularMeshFixtures — FilletStraight (quarter-round) + FlatPlate

**Files:**
- Modify `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesFilletStraightTests.cs`

- [ ] **Step 1: Write the failing test.** A straight quarter-round (90° sweep) of radius R, length L about Z. Every fillet vertex sits at distance R from the fillet axis line (the line through the quarter-circle center, parallel to Z). The two open edges of the strip should be at angle 0 and the sweep angle. Also a `FlatPlate` negative (both principal curvatures zero ⇒ all normals +Z).

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class CircularMeshFixturesFilletStraightTests
{
    [Fact]
    public void FilletStraight_VerticesLieOnQuarterCircleOfRadiusR()
    {
        const double R = 2.0;
        var (v, _) = CircularMeshFixtures.FilletStraight(
            radius: R, length: 5.0, arcSegments: 12, sweepRadians: System.Math.PI / 2);

        // Fillet axis = the line x=0,y=0 (center of the quarter arc), parallel to Z.
        foreach (Vector3d p in v)
        {
            double radial = System.Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.Equal(R, radial, 9);
            Assert.InRange(p.Z, -1e-9, 5.0 + 1e-9);
        }
    }

    [Fact]
    public void FilletStraight_SweepCoversNinetyDegrees()
    {
        const double R = 2.0;
        var (v, _) = CircularMeshFixtures.FilletStraight(R, 5.0, 12, System.Math.PI / 2);

        double minAng = double.MaxValue, maxAng = double.MinValue;
        foreach (Vector3d p in v)
        {
            double a = System.Math.Atan2(p.Y, p.X);
            minAng = System.Math.Min(minAng, a);
            maxAng = System.Math.Max(maxAng, a);
        }
        Assert.Equal(0.0, minAng, 6);
        Assert.Equal(System.Math.PI / 2, maxAng, 6);
    }

    [Fact]
    public void FlatPlate_AllTriangleNormalsArePlusZ()
    {
        var (v, i) = CircularMeshFixtures.FlatPlate(size: 4.0);
        Assert.True(i.Length >= 6); // at least two triangles
        for (int t = 0; t < i.Length; t += 3)
        {
            Vector3d a = v[i[t]], b = v[i[t + 1]], c = v[i[t + 2]];
            Vector3d n = Vector3d.Cross(b - a, c - a).Normalized();
            Assert.True(Vector3d.Dot(n, Vector3d.UnitZ) > 0.999, $"Plate tri {t / 3} normal {n} not +Z.");
        }
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** Run host command. Compile failure: `FilletStraight` / `FlatPlate` not defined (CS0103).

- [ ] **Step 3: Implement.** Append both generators. The quarter-round is a strip of quads swept along Z; the negative `FlatPlate` is a flat +Z quad (a known-zero-curvature negative for the classifier).

```csharp
// Add inside internal static partial class CircularMeshFixtures:

/// <summary>
/// A straight (cylindrical) fillet/round: a quarter-round strip of radius R swept along Z
/// for <paramref name="length"/>. The fillet axis is the line x=y=0 (the arc center) parallel
/// to Z; every vertex lies at radial distance R from it. Sweep defaults to a quarter turn.
/// Triangle normals point radially outward. Open along both arc-extreme edges (a strip, not a
/// closed loop) — the canonical fillet topology between two tangent planes.
/// </summary>
public static (Vector3d[] v, int[] i) FilletStraight(double radius, double length, int arcSegments, double sweepRadians)
{
    var verts = new Vector3d[(arcSegments + 1) * 2];
    for (int a = 0; a <= arcSegments; a++)
    {
        double ang = a * sweepRadians / arcSegments;
        double x = System.Math.Cos(ang) * radius, y = System.Math.Sin(ang) * radius;
        verts[2 * a] = new Vector3d(x, y, 0.0);
        verts[2 * a + 1] = new Vector3d(x, y, length);
    }

    var idx = new List<int>(arcSegments * 6);
    for (int a = 0; a < arcSegments; a++)
    {
        int b0 = 2 * a, t0 = b0 + 1, b1 = 2 * (a + 1), t1 = b1 + 1;
        // CCW so cross(b-a,c-a) faces outward (away from the x=y=0 axis).
        idx.Add(b0); idx.Add(b1); idx.Add(t1);
        idx.Add(b0); idx.Add(t1); idx.Add(t0);
    }

    return (verts, idx.ToArray());
}

/// <summary>
/// A flat square in the z=0 plane, side <paramref name="size"/>, normal +Z. Both principal
/// curvatures are zero — a negative fixture: the detector must REJECT it (no circular feature).
/// </summary>
public static (Vector3d[] v, int[] i) FlatPlate(double size)
{
    double h = size * 0.5;
    var verts = new[]
    {
        new Vector3d(-h, -h, 0),
        new Vector3d(h, -h, 0),
        new Vector3d(h, h, 0),
        new Vector3d(-h, h, 0),
    };
    var idx = new[] { 0, 1, 2, 0, 2, 3 }; // CCW about +Z
    return (verts, idx);
}
```

- [ ] **Step 4: Run it, expect PASS.** Run host command. `CircularMeshFixturesFilletStraightTests` (3 tests) green.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesFilletStraightTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add FilletStraight quarter-round + FlatPlate negative fixtures

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task A.5: CircularMeshFixtures — FilletTorus (real torus patch)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesTorusTests.cs`

- [ ] **Step 1: Write the failing test.** A torus patch (curved-edge fillet): ring radius `R`, tube radius `r`, swept `sweepRadians` around Z, with a partial tube sweep (quarter tube — the fillet surface). The defining property: every vertex's **distance to the ring circle** equals `r`. The ring circle is the locus `{(R cosθ, R sinθ, 0)}`; the nearest point on it to a vertex p is `R * (p.x, p.y, 0).Normalized()`. Assert `|p − nearestRingPoint| ≈ r` and that the in-plane radius of the nearest ring point is `R`.

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class CircularMeshFixturesTorusTests
{
    [Fact]
    public void FilletTorus_EveryVertexIsTubeRadiusFromRingCircle()
    {
        const double R = 5.0, r = 1.0;
        var (v, _) = CircularMeshFixtures.FilletTorus(
            tubeRadius: r, ringRadius: R, ringSeg: 24, tubeSeg: 8, sweepRadians: System.Math.PI / 2);

        foreach (Vector3d p in v)
        {
            Vector3d inPlane = new Vector3d(p.X, p.Y, 0);
            double inPlaneR = inPlane.Length;
            Assert.True(inPlaneR > 1e-9, "Vertex on the torus axis is impossible for R>r.");
            Vector3d nearestRing = inPlane.Normalized() * R; // closest point on the ring circle
            double dist = (p - nearestRing).Length;
            Assert.Equal(r, dist, 6);
        }
    }

    [Fact]
    public void FilletTorus_TopologyMatchesSweep()
    {
        var (v, i) = CircularMeshFixtures.FilletTorus(1.0, 5.0, 24, 8, System.Math.PI / 2);
        // open ring sweep => (ringSeg+1) ring rows; open tube => (tubeSeg+1) tube cols.
        Assert.Equal((24 + 1) * (8 + 1), v.Length);
        Assert.Equal(24 * 8 * 2 * 3, i.Length);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** Run host command. Compile failure: `FilletTorus` not defined (CS0103).

- [ ] **Step 3: Implement.** Append the torus generator. Standard parametric torus: ring angle θ (around Z), tube angle φ (around the ring tangent). Point `= (R + r cosφ)·(cosθ, sinθ, 0) + r sinφ·ẑ`. Open sweep on both θ and φ ⇒ `(ringSeg+1)×(tubeSeg+1)` grid.

```csharp
// Add inside internal static partial class CircularMeshFixtures:

/// <summary>
/// A toroidal fillet patch (curved-edge round): tube radius r = fillet radius, ring radius R,
/// swept <paramref name="sweepRadians"/> around Z. Tube sweeps a quarter turn (φ ∈ [0, π/2]) so
/// the patch is the concave/convex blend surface. Standard parametric torus:
/// P(θ,φ) = (R + r·cosφ)·(cosθ, sinθ, 0) + r·sinφ·ẑ. Every vertex is exactly r from the ring
/// circle {R·(cosθ, sinθ, 0)}. Triangle normals point outward from the tube center line.
/// </summary>
public static (Vector3d[] v, int[] i) FilletTorus(
    double tubeRadius, double ringRadius, int ringSeg, int tubeSeg, double sweepRadians)
{
    double r = tubeRadius, R = ringRadius;
    const double tubeSweep = System.Math.PI / 2; // quarter tube => fillet blend
    int rows = ringSeg + 1, cols = tubeSeg + 1;

    var verts = new Vector3d[rows * cols];
    for (int t = 0; t <= ringSeg; t++)
    {
        double theta = t * sweepRadians / ringSeg;
        double ct = System.Math.Cos(theta), st = System.Math.Sin(theta);
        for (int p = 0; p <= tubeSeg; p++)
        {
            double phi = p * tubeSweep / tubeSeg;
            double ringDist = R + r * System.Math.Cos(phi);
            verts[t * cols + p] = new Vector3d(ringDist * ct, ringDist * st, r * System.Math.Sin(phi));
        }
    }

    var idx = new List<int>(ringSeg * tubeSeg * 6);
    for (int t = 0; t < ringSeg; t++)
    for (int p = 0; p < tubeSeg; p++)
    {
        int a = t * cols + p;
        int b = (t + 1) * cols + p;
        int c = (t + 1) * cols + (p + 1);
        int d = t * cols + (p + 1);
        // CCW so the outward (away from the tube centre circle) normal is produced.
        idx.Add(a); idx.Add(b); idx.Add(c);
        idx.Add(a); idx.Add(c); idx.Add(d);
    }

    return (verts, idx.ToArray());
}
```

- [ ] **Step 4: Run it, expect PASS.** Run host command. `CircularMeshFixturesTorusTests` (2 tests) green.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesTorusTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add FilletTorus parametric torus-patch fixture

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task A.6: CircularMeshFixtures — Chamfer (angled band)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesChamferTests.cs`

- [ ] **Step 1: Write the failing test.** A straight chamfer: a narrow planar band of the given `width`, tilted `angleDeg` from the +Z reference plane, extruded along Y for `length`. The band plane normal must make exactly `angleDeg` with +Z; the cross-band extent (perpendicular distance across the band) must equal `width`.

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class CircularMeshFixturesChamferTests
{
    [Fact]
    public void Chamfer_BandNormalMakesExpectedAngleWithZ()
    {
        const double angleDeg = 45.0;
        var (v, i) = CircularMeshFixtures.Chamfer(width: 2.0, angleDeg: angleDeg, length: 6.0);

        Vector3d a = v[i[0]], b = v[i[1]], c = v[i[2]];
        Vector3d n = Vector3d.Cross(b - a, c - a).Normalized();
        double measured = System.Math.Acos(System.Math.Abs(Vector3d.Dot(n, Vector3d.UnitZ))) * 180.0 / System.Math.PI;
        Assert.Equal(angleDeg, measured, 4);
    }

    [Fact]
    public void Chamfer_CrossBandExtentEqualsWidth()
    {
        const double width = 2.0;
        var (v, _) = CircularMeshFixtures.Chamfer(width, angleDeg: 30.0, length: 6.0);

        // Project all verts onto the band's cross-direction (perpendicular to the Y extrusion,
        // in the band plane) and measure the span.
        // The band runs in Y; cross-direction lies in the X-Z plane.
        double minU = double.MaxValue, maxU = double.MinValue;
        foreach (Vector3d p in v)
        {
            Vector3d inXZ = new Vector3d(p.X, 0, p.Z);
            double u = inXZ.Length * System.Math.Sign(p.X == 0 ? 1 : p.X); // signed along the band cross
            // use 3D chord across band: simpler — track full 3D extent in X-Z
            minU = System.Math.Min(minU, u);
            maxU = System.Math.Max(maxU, u);
        }
        // The slant chord length across the band equals width.
        // Reconstruct via the two distinct cross positions: span along the slant = width.
        // Verify by the actual 3D distance between the two band edges at y=0.
        var edge0 = v.Where(p => System.Math.Abs(p.Y) < 1e-9).OrderBy(p => p.X).ToArray();
        Assert.True(edge0.Length >= 2);
        double chord = (edge0[^1] - edge0[0]).Length;
        Assert.Equal(width, chord, 6);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** Run host command. Compile failure: `Chamfer` not defined (CS0103).

- [ ] **Step 3: Implement.** Append the `Chamfer` generator: a single planar quad of cross-width `width` at tilt `angleDeg` to +Z, extruded along Y for `length`. The band's two long edges are at cross-position 0 and `width` along the slant direction `(cos? , 0, sin?)`; we place the band so its normal makes `angleDeg` with +Z.

```csharp
// Add inside internal static partial class CircularMeshFixtures:

/// <summary>
/// A straight chamfer: a flat band of cross-<paramref name="width"/> tilted
/// <paramref name="angleDeg"/> from the +Z reference plane and extruded along Y for
/// <paramref name="length"/>. The band's outward normal makes exactly angleDeg with +Z; the
/// slant chord across the two long edges equals width. Narrow planar band between two creases
/// — the canonical chamfer the detector must report as width × angle.
/// </summary>
public static (Vector3d[] v, int[] i) Chamfer(double width, double angleDeg, double length)
{
    double a = angleDeg * System.Math.PI / 180.0;
    // Band slant direction in the X-Z plane: tilt the band so its NORMAL is angleDeg from +Z.
    // Band plane spans Y and the slant dir s = (cos a, 0, -sin a)?  Choose so normal·Z = cos a.
    // Let the band run from origin along s = (cos a, 0, sin a)*width; its normal is
    // perpendicular to both Y and s => n = cross(Y, s) = (sin a, 0, -cos a) (then |n·Z| = cos a).
    Vector3d s = new Vector3d(System.Math.Cos(a), 0, System.Math.Sin(a)); // unit slant
    Vector3d e0 = Vector3d.Zero;
    Vector3d e1 = s * width;
    double hy = length * 0.5;

    var verts = new[]
    {
        e0 + new Vector3d(0, -hy, 0), // 0: near edge, -Y
        e1 + new Vector3d(0, -hy, 0), // 1: far edge, -Y
        e1 + new Vector3d(0, hy, 0),  // 2: far edge, +Y
        e0 + new Vector3d(0, hy, 0),  // 3: near edge, +Y
    };
    // Wind so cross(b-a, c-a) = cross(s, +Y) direction; |normal·Z| == cos(angle).
    var idx = new[] { 0, 1, 2, 0, 2, 3 };
    return (verts, idx);
}
```

- [ ] **Step 4: Run it, expect PASS.** Run host command. `CircularMeshFixturesChamferTests` (2 tests) green. (`cross(e1-e0, e3-e0) = cross(s·width, +Y·length)`; its Z component magnitude over its length gives `cos(angleDeg)` against +Z as asserted.)

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesChamferTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add Chamfer angled-band fixture

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task A.7: CircularMeshFixtures — SeedTriangleAt

**Files:**
- Modify `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesSeedTests.cs`

- [ ] **Step 1: Write the failing test.** `SeedTriangleAt` returns the index of the triangle whose centroid is nearest a world point. On a full cylinder, the triangle nearest `(R,0,H/2)` must have a centroid near that point and a radial outward normal.

```csharp
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement.Fixtures;

public sealed class CircularMeshFixturesSeedTests
{
    [Fact]
    public void SeedTriangleAt_ReturnsNearestTriangleByCentroid()
    {
        const double R = 2.0, H = 5.0;
        var mesh = CircularMeshFixtures.Cylinder(R, H, 32, System.Math.Tau, Vector3d.UnitZ);

        Vector3d target = new Vector3d(R, 0, H / 2);
        int tri = CircularMeshFixtures.SeedTriangleAt(mesh, target);

        Assert.InRange(tri, 0, mesh.i.Length / 3 - 1);
        Vector3d a = mesh.v[mesh.i[tri * 3]], b = mesh.v[mesh.i[tri * 3 + 1]], c = mesh.v[mesh.i[tri * 3 + 2]];
        Vector3d centroid = (a + b + c) / 3.0;
        Assert.True((centroid - target).Length < 1.0,
            $"Nearest centroid {centroid} too far from target {target}.");

        // The seed triangle is on the wall: its centroid is near radius R.
        double rc = System.Math.Sqrt(centroid.X * centroid.X + centroid.Y * centroid.Y);
        Assert.Equal(R, rc, 3);
    }

    [Fact]
    public void SeedTriangleAt_FlatPlate_ReturnsAValidTriangle()
    {
        var mesh = CircularMeshFixtures.FlatPlate(4.0);
        int tri = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(0.5, 0.5, 0));
        Assert.InRange(tri, 0, mesh.i.Length / 3 - 1);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** Run host command. Compile failure: `SeedTriangleAt` not defined (CS0103).

- [ ] **Step 3: Implement.** Append the helper: brute-force nearest centroid (fixtures are small).

```csharp
// Add inside internal static partial class CircularMeshFixtures:

/// <summary>
/// Returns the index of the triangle whose centroid is nearest <paramref name="worldPoint"/>.
/// Brute force (fixtures are small). Used by tests to seed detection at a known surface point.
/// </summary>
public static int SeedTriangleAt((Vector3d[] v, int[] i) mesh, Vector3d worldPoint)
{
    int best = -1;
    double bestSq = double.MaxValue;
    int triCount = mesh.i.Length / 3;
    for (int t = 0; t < triCount; t++)
    {
        Vector3d a = mesh.v[mesh.i[t * 3]];
        Vector3d b = mesh.v[mesh.i[t * 3 + 1]];
        Vector3d c = mesh.v[mesh.i[t * 3 + 2]];
        Vector3d centroid = (a + b + c) / 3.0;
        double sq = (centroid - worldPoint).LengthSquared;
        if (sq < bestSq) { bestSq = sq; best = t; }
    }
    return best;
}
```

- [ ] **Step 4: Run it, expect PASS.** Run host command. `CircularMeshFixturesSeedTests` (2 tests) green.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixtures.cs src/FabricationAssistant.App.Tests/Measurement/Fixtures/CircularMeshFixturesSeedTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(circular): add SeedTriangleAt nearest-centroid helper

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---


## Group B — Mesh Topology Cache, Welded Half-Edge Topology, Feature-Edge Classification, Per-Triangle Curvature

> **Preamble (read once).** All Group-B production code is **new Core source** under `src/FabricationAssistant.Core/Measurement/Engine/` in the **PARENT** repo (root `C:/Users/skritikos/Desktop/Fabrication Assistant`). Because `FabricationAssistant.Core` is an SDK-style project with default compile items, every new `.cs` you drop under `Measurement/**` is compiled into Core automatically and reaches Android through the existing wildcard — **no Core csproj edit is ever needed**. Host tests live in the **PARENT** repo under `src/FabricationAssistant.App.Tests/Measurement/` and reach the new types transitively through the existing `ProjectReference` to `FabricationAssistant.App` → `FabricationAssistant.Core`. The Android mirror test project links Core **selectively**, so any new Core type used by an Android test needs an **explicit `<Compile Include>`** in `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (Task B.6). Commit Core + host-test changes in the **PARENT** repo; commit the Android-csproj change in the **ANDROID** repo (`Android/`), staging by explicit path.
>
> If a build fails with `CS2012` / file-lock / `XARLP7024`: run `dotnet build-server shutdown` and retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`.
>
> **Host test command (from PARENT root):** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopology"` (TFM `net8.0-windows`).
>
> Group B owns the canonical contract types `EdgeClass`, `TriData`, `EdgeRef`, `TriCurvature`, `MeshTopology`, `IMeshTopologyProvider`, `MeshTopologyCache`. Every other group consumes them — do not rename or change signatures.

---

### Task B.1: Topology contract types (enums + record structs) compile in Core

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyContracts.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshTopologyContractsTests.cs`

- [ ] **Step 1: Write the failing test.** This pins the exact field names/order other groups will `with`-mutate and read.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeshTopologyContractsTests
{
    [Fact]
    public void EdgeClass_HasFourExpectedMembers()
    {
        Assert.Equal(0, (int)EdgeClass.Smooth);
        Assert.Equal(1, (int)EdgeClass.Crease);
        Assert.Equal(2, (int)EdgeClass.Boundary);
        Assert.Equal(3, (int)EdgeClass.NonManifold);
    }

    [Fact]
    public void TriData_StoresAllFields()
    {
        var t = new TriData(Vector3d.UnitZ, new Vector3d(1, 2, 3), 0.5, true);
        Assert.Equal(Vector3d.UnitZ, t.Normal);
        Assert.Equal(new Vector3d(1, 2, 3), t.Centroid);
        Assert.Equal(0.5, t.Area, 12);
        Assert.True(t.Valid);
    }

    [Fact]
    public void EdgeRef_StoresEndpointsTrianglesDihedralAndClass()
    {
        var e = new EdgeRef(2, 7, 4, 9, 1.5707963267948966, EdgeClass.Crease);
        Assert.Equal(2, e.A);
        Assert.Equal(7, e.B);
        Assert.Equal(4, e.TriLeft);
        Assert.Equal(9, e.TriRight);
        Assert.Equal(1.5707963267948966, e.DihedralRad, 12);
        Assert.Equal(EdgeClass.Crease, e.Class);
    }

    [Fact]
    public void TriCurvature_StoresPrincipalCurvaturesAndDirection()
    {
        var c = new TriCurvature(0.5, 0.0, Vector3d.UnitX);
        Assert.Equal(0.5, c.K1, 12);
        Assert.Equal(0.0, c.K2, 12);
        Assert.Equal(Vector3d.UnitX, c.Dir1);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyContractsTests"` — fails to **compile**: `error CS0246: The type or namespace name 'EdgeClass' / 'TriData' / 'EdgeRef' / 'TriCurvature' could not be found`.
- [ ] **Step 3: Implement** the contract types exactly per the canonical contract.
```csharp
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>Classification of a welded mesh edge, used as a hard grow barrier.</summary>
public enum EdgeClass
{
    Smooth = 0,
    Crease = 1,
    Boundary = 2,
    NonManifold = 3,
}

/// <summary>Per-triangle geometry computed once at topology build time.</summary>
public readonly record struct TriData(Vector3d Normal, Vector3d Centroid, double Area, bool Valid);

/// <summary>
/// A welded edge shared by up to two triangles. <see cref="TriRight"/> is -1 for a
/// boundary edge. <see cref="DihedralRad"/> is the unsigned angle between incident
/// face normals (0 = coplanar).
/// </summary>
public readonly record struct EdgeRef(int A, int B, int TriLeft, int TriRight, double DihedralRad, EdgeClass Class);

/// <summary>
/// Per-triangle principal curvature estimate. K1 is the larger-magnitude principal
/// curvature, K2 the smaller; Dir1 is the unit world/local direction of K1.
/// </summary>
public readonly record struct TriCurvature(double K1, double K2, Vector3d Dir1);
```
- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyContractsTests"` — 4 passing.
- [ ] **Step 5: Commit (PARENT repo).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyContracts.cs src/FabricationAssistant.App.Tests/Measurement/MeshTopologyContractsTests.cs
git commit -m "feat(measure): topology contract types (EdgeClass/TriData/EdgeRef/TriCurvature)"
```

---

### Task B.2: `MeshTopology` skeleton + welded vertices/triangles (no edges yet)

This task delivers the welded vertex/index/`TriData` core of `MeshTopology` and a builder entry point. Edge classification (B.3), neighbor queries (B.4), and curvature (B.5) bolt onto this same class.

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopology.cs`
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyBuilder.cs` (internal builder; weld + per-tri data)
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshTopologyWeldTests.cs`

- [ ] **Step 1: Write the failing test.** Asserts welded vertex count and per-triangle normal/area on a fixture whose vertices are deliberately **unshared** (glTF-style), so the weld must collapse duplicates. A unit square split into 2 triangles, emitted as 6 independent vertices at 4 distinct positions, must weld to **4** vertices and keep **2** triangles each with area `0.5` and normal `+Z`.
```csharp
using System;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeshTopologyWeldTests
{
    // Unit square, two triangles, glTF-style UNSHARED vertices (6 verts, 4 positions).
    private static (Vector3d[] v, int[] i) UnsharedSquare()
    {
        Vector3d p00 = new(0, 0, 0), p10 = new(1, 0, 0), p11 = new(1, 1, 0), p01 = new(0, 1, 0);
        var v = new[] { p00, p10, p11,  p00, p11, p01 };
        var i = new[] { 0, 1, 2,        3, 4, 5 };
        return (v, i);
    }

    [Fact]
    public void Build_WeldsDuplicatePositions_To4Vertices()
    {
        (Vector3d[] v, int[] i) = UnsharedSquare();
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, creaseAngleRad: 28.0 * Math.PI / 180.0);

        Assert.Equal(4, topo.Vertices.Count);
        Assert.Equal(2, topo.Triangles.Count);
        Assert.Equal(6, topo.Indices.Count);
    }

    [Fact]
    public void Build_ComputesTriangleNormalAreaCentroid()
    {
        (Vector3d[] v, int[] i) = UnsharedSquare();
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, creaseAngleRad: 28.0 * Math.PI / 180.0);

        foreach (TriData t in topo.Triangles)
        {
            Assert.True(t.Valid);
            Assert.Equal(0.5, t.Area, 9);
            Assert.True(Vector3d.Dot(t.Normal, Vector3d.UnitZ) > 0.999);
        }
        // Welded triangle indices must point at the 4 welded vertices, not the 6 raw ones.
        for (int k = 0; k < topo.Indices.Count; k++)
            Assert.InRange(topo.Indices[k], 0, topo.Vertices.Count - 1);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyWeldTests"` — fails to compile: `CS0246 ... 'MeshTopology' / 'MeshTopologyBuilder'`.
- [ ] **Step 3: Implement.** First the `MeshTopology` class shell (all contract members present; edge/curvature/neighbor members return empty/placeholder and get filled in B.3–B.5 — but the file is written **complete** here so later tasks only fill method bodies that already throw `NotImplementedException` is **not** acceptable; instead we ship working but empty edge/curvature collections now and replace them in later tasks).

`MeshTopology.cs`:
```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Immutable welded half-edge topology of a single mesh in LOCAL space, with per-triangle
/// geometry, per-edge classification, per-triangle 1-ring curvature, and smooth-neighbor
/// queries. Built once by <see cref="MeshTopologyBuilder"/> and cached by
/// <see cref="MeshTopologyCache"/>. Group B owns this type; all other engine groups consume it.
/// </summary>
public sealed class MeshTopology
{
    private readonly int[] _triangleEdgeIndices;     // 3 per triangle -> index into _edges
    private readonly int[][] _smoothNeighbors;       // per triangle: adjacent tris across SMOOTH edges
    private readonly Dictionary<long, int> _edgeKeyToIndex; // welded (min,max) packed key -> edge index

    internal MeshTopology(
        IReadOnlyList<Vector3d> vertices,
        IReadOnlyList<int> indices,
        IReadOnlyList<TriData> triangles,
        IReadOnlyList<EdgeRef> edges,
        int[] triangleEdgeIndices,
        int[][] smoothNeighbors,
        Dictionary<long, int> edgeKeyToIndex,
        IReadOnlyList<TriCurvature> curvatures)
    {
        Vertices = vertices;
        Indices = indices;
        Triangles = triangles;
        Edges = edges;
        _triangleEdgeIndices = triangleEdgeIndices;
        _smoothNeighbors = smoothNeighbors;
        _edgeKeyToIndex = edgeKeyToIndex;
        Curvatures = curvatures;
    }

    public IReadOnlyList<Vector3d> Vertices { get; }
    public IReadOnlyList<int> Indices { get; }
    public IReadOnlyList<TriData> Triangles { get; }
    public IReadOnlyList<EdgeRef> Edges { get; }
    public IReadOnlyList<int> TriangleEdgeIndices => _triangleEdgeIndices;
    public IReadOnlyList<TriCurvature> Curvatures { get; }

    /// <summary>Triangles adjacent to <paramref name="triangle"/> across SMOOTH edges only.</summary>
    public IReadOnlyList<int> SmoothNeighbors(int triangle) => _smoothNeighbors[triangle];

    /// <summary>
    /// False if the welded edge shared by the two triangles is Crease/Boundary/NonManifold
    /// (a hard grow wall), or if the two triangles do not share a welded edge.
    /// </summary>
    public bool CanGrowAcross(int triA, int triB)
    {
        if (triA == triB) return false;
        for (int s = 0; s < 3; s++)
        {
            int e = _triangleEdgeIndices[triA * 3 + s];
            EdgeRef edge = Edges[e];
            int other = edge.TriLeft == triA ? edge.TriRight : edge.TriLeft;
            if (other == triB)
                return edge.Class == EdgeClass.Smooth;
        }
        return false;
    }

    /// <summary>Packs a welded undirected edge (a,b) into a stable long key (min in high 32 bits).</summary>
    internal static long EdgeKey(int a, int b)
    {
        int lo = a < b ? a : b;
        int hi = a < b ? b : a;
        return ((long)lo << 32) | (uint)hi;
    }
}
```

`MeshTopologyBuilder.cs` (weld + per-triangle data only in this task; B.3 fills edges, B.5 fills curvature). It reuses the spatial-hash weld pattern proven in `FaceDetectionService`, but with a **local-feature-size** tolerance (max edge length scaled by `1e-3`, floored by `1e-9`) — **never** `sceneDiagonal`.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Builds an immutable <see cref="MeshTopology"/> from raw (possibly unshared-index) mesh
/// geometry. Welds positions on a LOCAL-FEATURE-SIZE tolerance, builds shared-edge adjacency
/// with T-junction linking, classifies edges, and estimates per-triangle curvature.
/// </summary>
public static class MeshTopologyBuilder
{
    public static MeshTopology Build(
        IReadOnlyList<Vector3d> verticesRaw,
        IReadOnlyList<int> indices,
        double creaseAngleRad)
    {
        int triCount = indices.Count / 3;

        // ── 1. Weld tolerance from LOCAL feature size (median-ish via mean edge length),
        //       NOT sceneDiagonal. Falls back to a tiny absolute on degenerate input. ──
        double weldTol = ComputeWeldTolerance(verticesRaw, indices);
        double weldScale = 1.0 / weldTol;

        int[] remap = WeldVertices(verticesRaw, weldTol, weldScale, out List<Vector3d> welded);

        // ── 2. Welded index buffer + per-triangle data ──
        var weldedIndices = new int[indices.Count];
        var tris = new TriData[triCount];
        for (int t = 0; t < triCount; t++)
        {
            int w0 = remap[indices[t * 3 + 0]];
            int w1 = remap[indices[t * 3 + 1]];
            int w2 = remap[indices[t * 3 + 2]];
            weldedIndices[t * 3 + 0] = w0;
            weldedIndices[t * 3 + 1] = w1;
            weldedIndices[t * 3 + 2] = w2;
            tris[t] = ComputeTriData(welded[w0], welded[w1], welded[w2]);
        }

        // ── 3. Edges + neighbors + curvature are filled by later steps. For now, empty. ──
        var edges = Array.Empty<EdgeRef>();
        var triEdge = new int[triCount * 3];          // all zero for now; B.3 fills
        var smoothNeighbors = new int[triCount][];
        for (int t = 0; t < triCount; t++) smoothNeighbors[t] = Array.Empty<int>();
        var edgeKeyToIndex = new Dictionary<long, int>();
        var curv = new TriCurvature[triCount];        // all default for now; B.5 fills

        return new MeshTopology(
            welded, weldedIndices, tris, edges, triEdge, smoothNeighbors, edgeKeyToIndex, curv);
    }

    private static double ComputeWeldTolerance(IReadOnlyList<Vector3d> v, IReadOnlyList<int> indices)
    {
        // Mean triangle-edge length is a robust local-feature-size proxy and is invariant
        // to overall scene scale (unlike sceneDiagonal).
        double sum = 0.0;
        int count = 0;
        int triCount = indices.Count / 3;
        for (int t = 0; t < triCount; t++)
        {
            Vector3d a = v[indices[t * 3 + 0]];
            Vector3d b = v[indices[t * 3 + 1]];
            Vector3d c = v[indices[t * 3 + 2]];
            sum += (b - a).Length + (c - b).Length + (a - c).Length;
            count += 3;
        }
        double meanEdge = count > 0 ? sum / count : 1.0;
        return Math.Max(meanEdge * 1e-3, 1e-9);
    }

    private static TriData ComputeTriData(Vector3d a, Vector3d b, Vector3d c)
    {
        Vector3d raw = Vector3d.Cross(b - a, c - a);
        double rawLen = raw.Length;
        double maxEdgeSq = Math.Max((b - a).LengthSquared,
            Math.Max((c - b).LengthSquared, (a - c).LengthSquared));
        bool valid = double.IsFinite(rawLen) && maxEdgeSq > 0.0
                     && rawLen > Math.Max(maxEdgeSq * 1e-12, 1e-30);
        Vector3d normal = valid ? raw / rawLen : Vector3d.Zero;
        Vector3d centroid = (a + b + c) / 3.0;
        double area = valid ? 0.5 * rawLen : 0.0;
        return new TriData(normal, centroid, area, valid);
    }

    private static int[] WeldVertices(
        IReadOnlyList<Vector3d> vertices, double weldTol, double weldScale, out List<Vector3d> reps)
    {
        var cells = new Dictionary<(long, long, long), List<int>>();
        reps = new List<Vector3d>();
        var remap = new int[vertices.Count];
        double tolSq = weldTol * weldTol;

        for (int vi = 0; vi < vertices.Count; vi++)
        {
            Vector3d p = vertices[vi];
            var cell = ((long)Math.Floor(p.X * weldScale),
                        (long)Math.Floor(p.Y * weldScale),
                        (long)Math.Floor(p.Z * weldScale));
            int existing = FindExisting(cells, reps, cell, p, tolSq);
            if (existing >= 0) { remap[vi] = existing; continue; }

            int next = reps.Count;
            reps.Add(p);
            remap[vi] = next;
            if (!cells.TryGetValue(cell, out List<int>? bucket)) { bucket = new List<int>(); cells[cell] = bucket; }
            bucket.Add(next);
        }
        return remap;
    }

    private static int FindExisting(
        Dictionary<(long, long, long), List<int>> cells, IReadOnlyList<Vector3d> reps,
        (long X, long Y, long Z) cell, Vector3d p, double tolSq)
    {
        for (long dz = -1; dz <= 1; dz++)
        for (long dy = -1; dy <= 1; dy++)
        for (long dx = -1; dx <= 1; dx++)
        {
            if (!cells.TryGetValue((cell.X + dx, cell.Y + dy, cell.Z + dz), out List<int>? bucket)) continue;
            foreach (int id in bucket)
                if ((reps[id] - p).LengthSquared <= tolSq) return id;
        }
        return -1;
    }
}
```
- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyWeldTests"` — 2 passing.
- [ ] **Step 5: Commit (PARENT repo).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/MeshTopology.cs src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyBuilder.cs src/FabricationAssistant.App.Tests/Measurement/MeshTopologyWeldTests.cs
git commit -m "feat(measure): MeshTopology weld + per-triangle data (local-feature-size tol)"
```

---

### Task B.3: Shared-edge adjacency with T-junction linking + `EdgeClass` classification (crease 28°)

Fill the `edges`, `triEdge`, and `edgeKeyToIndex` that B.2 left empty. An edge is **Boundary** (1 incident tri), **NonManifold** (>2), else **Crease** if dihedral ≥ creaseAngle, else **Smooth**. T-junctions (an edge of one triangle that is collinearly covered by two shorter edges of neighbors) are linked so adjacency does not falsely report a boundary on a tessellation seam.

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyBuilder.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshTopologyEdgeClassTests.cs`

- [ ] **Step 1: Write the failing test.** A 90° corner (two unit quads meeting at a right angle) must flag the shared seam as **Crease** and the four outer rim edges as **Boundary**, while the diagonal split inside each flat quad stays **Smooth**. A cylinder must keep its longitudinal seam (between two near-coplanar facet columns) **Smooth** — i.e. the 32-segment cylinder has **zero** crease edges among its quad-diagonal and ring edges.
```csharp
using System;
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeshTopologyEdgeClassTests
{
    private const double Crease28 = 28.0 * Math.PI / 180.0;

    // Two unit quads sharing the edge x=0..1, y=0; one in the z=0 plane, one bent up 90deg.
    private static (Vector3d[] v, int[] i) RightAngleCorner()
    {
        // Flat quad (z=0): (0,0,0)(1,0,0)(1,1,0)(0,1,0)
        // Vertical quad (bent up): (0,0,0)(1,0,0)(1,0,1)(0,0,1)  -- shares edge (0,0,0)-(1,0,0)
        Vector3d a = new(0, 0, 0), b = new(1, 0, 0), c = new(1, 1, 0), d = new(0, 1, 0);
        Vector3d e = new(1, 0, 1), f = new(0, 0, 1);
        var v = new[] { a, b, c,  a, c, d,   a, b, e,  a, e, f };
        var i = new[] { 0, 1, 2,  3, 4, 5,   6, 7, 8,  9, 10, 11 };
        return (v, i);
    }

    [Fact]
    public void Build_Flags90DegreeSeamAsCrease()
    {
        (Vector3d[] v, int[] i) = RightAngleCorner();
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        // Exactly one shared (2-incident) edge exists: the seam (0,0,0)-(1,0,0). It must be Crease.
        var shared = topo.Edges.Where(e => e.TriRight >= 0).ToList();
        Assert.Contains(shared, e => e.Class == EdgeClass.Crease);
        // The seam dihedral is 90deg.
        EdgeRef seam = shared.Single(e => e.Class == EdgeClass.Crease);
        Assert.Equal(Math.PI / 2.0, seam.DihedralRad, 3);

        // The two interior diagonals (one per flat quad) are 2-incident and Smooth.
        Assert.Equal(2, shared.Count(e => e.Class == EdgeClass.Smooth));
        // Outer rim edges are boundary (1-incident).
        Assert.True(topo.Edges.Count(e => e.Class == EdgeClass.Boundary) >= 6);
    }

    private static (Vector3d[] v, int[] i) Cylinder(double r, double h, int seg)
    {
        var v = new Vector3d[seg * 2];
        for (int k = 0; k < seg; k++)
        {
            double t = k * Math.Tau / seg;
            v[2 * k] = new Vector3d(Math.Cos(t) * r, Math.Sin(t) * r, 0);
            v[2 * k + 1] = new Vector3d(Math.Cos(t) * r, Math.Sin(t) * r, h);
        }
        var idx = new System.Collections.Generic.List<int>();
        for (int k = 0; k < seg; k++)
        {
            int n = (k + 1) % seg;
            int b0 = 2 * k, t0 = b0 + 1, b1 = 2 * n, t1 = b1 + 1;
            idx.AddRange(new[] { b0, b1, t1,  b0, t1, t0 });
        }
        return (v, idx.ToArray());
    }

    [Fact]
    public void Build_CylinderSeam_StaysSmooth()
    {
        // 32 segments => per-facet bend = 360/32 = 11.25deg < 28deg, so NO crease anywhere on the wall.
        (Vector3d[] v, int[] i) = Cylinder(r: 2.0, h: 5.0, seg: 32);
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        Assert.Equal(0, topo.Edges.Count(e => e.Class == EdgeClass.Crease));
        // The closed wall is watertight: every edge is shared (no boundary), since top/bottom rings
        // wrap around. Quad diagonals + ring edges + vertical edges are all 2-incident & Smooth.
        Assert.DoesNotContain(topo.Edges, e => e.Class == EdgeClass.NonManifold);
        Assert.True(topo.Edges.Count(e => e.Class == EdgeClass.Smooth) > 0);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyEdgeClassTests"` — fails: B.2 emits an **empty** `Edges` list, so `Assert.Contains(... Crease)` throws `Empty collection` and the `Smooth count == 2` assertion fails.
- [ ] **Step 3: Implement.** Replace the placeholder edge block in `MeshTopologyBuilder.Build` (`var edges = Array.Empty<EdgeRef>(); ... var edgeKeyToIndex = ...`) with a real edge-assembly call, and add the supporting methods.

Replace this block:
```csharp
        // ── 3. Edges + neighbors + curvature are filled by later steps. For now, empty. ──
        var edges = Array.Empty<EdgeRef>();
        var triEdge = new int[triCount * 3];          // all zero for now; B.3 fills
        var smoothNeighbors = new int[triCount][];
        for (int t = 0; t < triCount; t++) smoothNeighbors[t] = Array.Empty<int>();
        var edgeKeyToIndex = new Dictionary<long, int>();
        var curv = new TriCurvature[triCount];        // all default for now; B.5 fills
```
with:
```csharp
        // ── 3. Edges (shared-edge adjacency + T-junction linking + classification) ──
        BuildEdges(weldedIndices, welded, tris, creaseAngleRad,
            out EdgeRef[] edges, out int[] triEdge, out Dictionary<long, int> edgeKeyToIndex);

        // ── 4. Smooth neighbors per triangle (across Smooth edges only) ──
        int[][] smoothNeighbors = BuildSmoothNeighbors(triCount, edges, triEdge);

        // ── 5. Curvature filled by B.5; default for now. ──
        var curv = new TriCurvature[triCount];
```
Add these methods to `MeshTopologyBuilder`:
```csharp
    private static void BuildEdges(
        int[] weldedIndices, IReadOnlyList<Vector3d> verts, IReadOnlyList<TriData> tris,
        double creaseAngleRad,
        out EdgeRef[] edges, out int[] triEdge, out Dictionary<long, int> edgeKeyToIndex)
    {
        int triCount = weldedIndices.Length / 3;
        triEdge = new int[triCount * 3];

        // edgeKey -> list of (tri, slot) incident on that welded edge.
        var incident = new Dictionary<long, List<(int tri, int slot)>>();
        for (int t = 0; t < triCount; t++)
        {
            int v0 = weldedIndices[t * 3 + 0];
            int v1 = weldedIndices[t * 3 + 1];
            int v2 = weldedIndices[t * 3 + 2];
            AddIncident(incident, v0, v1, t, 0);
            AddIncident(incident, v1, v2, t, 1);
            AddIncident(incident, v2, v0, t, 2);
        }

        // T-junction linking: a long edge (a,b) of one triangle may be split by a mid vertex m
        // lying on segment a-b (separate triangles use (a,m) and (m,b)). Merge the incidence of
        // (a,m) and (m,b) into (a,b) so the seam is shared, not two boundaries.
        LinkTJunctions(incident, verts);

        var edgeList = new List<EdgeRef>(incident.Count);
        edgeKeyToIndex = new Dictionary<long, int>(incident.Count);
        double cosCrease = Math.Cos(creaseAngleRad);

        foreach (KeyValuePair<long, List<(int tri, int slot)>> kv in incident)
        {
            long key = kv.Key;
            List<(int tri, int slot)> uses = kv.Value;
            int a = (int)(key >> 32);
            int b = (int)(uint)key;

            int triLeft = uses[0].tri;
            int triRight = uses.Count >= 2 ? uses[1].tri : -1;

            EdgeClass cls;
            double dihedral = 0.0;
            if (uses.Count == 1) cls = EdgeClass.Boundary;
            else if (uses.Count > 2) cls = EdgeClass.NonManifold;
            else
            {
                Vector3d nL = tris[triLeft].Normal;
                Vector3d nR = tris[triRight].Normal;
                double d = Math.Clamp(Vector3d.Dot(nL, nR), -1.0, 1.0);
                dihedral = Math.Acos(d);
                cls = d <= cosCrease ? EdgeClass.Crease : EdgeClass.Smooth;
            }

            int edgeIndex = edgeList.Count;
            edgeList.Add(new EdgeRef(a, b, triLeft, triRight, dihedral, cls));
            edgeKeyToIndex[key] = edgeIndex;
            foreach ((int tri, int slot) in uses)
                triEdge[tri * 3 + slot] = edgeIndex;
        }

        edges = edgeList.ToArray();
    }

    private static void AddIncident(
        Dictionary<long, List<(int, int)>> incident, int a, int b, int tri, int slot)
    {
        long key = MeshTopology.EdgeKey(a, b);
        if (!incident.TryGetValue(key, out List<(int, int)>? list))
        {
            list = new List<(int, int)>(2);
            incident[key] = list;
        }
        list.Add((tri, slot));
    }

    private static void LinkTJunctions(
        Dictionary<long, List<(int tri, int slot)>> incident, IReadOnlyList<Vector3d> verts)
    {
        // Boundary candidates only (singly-incident edges). For each, look for a welded vertex m
        // strictly between its endpoints; if present, fold (a,b)'s incidence into (a,m) and (m,b).
        var boundaryKeys = new List<long>();
        foreach (KeyValuePair<long, List<(int tri, int slot)>> kv in incident)
            if (kv.Value.Count == 1) boundaryKeys.Add(kv.Key);

        foreach (long key in boundaryKeys)
        {
            if (!incident.TryGetValue(key, out List<(int tri, int slot)>? uses)) continue;
            if (uses.Count != 1) continue;
            int a = (int)(key >> 32);
            int b = (int)(uint)key;
            Vector3d pa = verts[a], pb = verts[b];
            Vector3d ab = pb - pa;
            double abLenSq = ab.LengthSquared;
            if (abLenSq < 1e-24) continue;

            for (int m = 0; m < verts.Count; m++)
            {
                if (m == a || m == b) continue;
                Vector3d pm = verts[m];
                double s = Vector3d.Dot(pm - pa, ab) / abLenSq;
                if (s <= 1e-6 || s >= 1.0 - 1e-6) continue;            // not strictly interior
                Vector3d proj = pa + ab * s;
                if ((pm - proj).LengthSquared > abLenSq * 1e-10) continue; // not collinear
                // Fold (a,b)'s single use into the two sub-edges so the seam becomes shared.
                long k1 = MeshTopology.EdgeKey(a, m);
                long k2 = MeshTopology.EdgeKey(m, b);
                if (incident.TryGetValue(k1, out List<(int, int)>? l1)) l1.AddRange(uses);
                if (incident.TryGetValue(k2, out List<(int, int)>? l2)) l2.AddRange(uses);
                incident.Remove(key);
                break;
            }
        }
    }

    private static int[][] BuildSmoothNeighbors(int triCount, EdgeRef[] edges, int[] triEdge)
    {
        var lists = new List<int>[triCount];
        for (int t = 0; t < triCount; t++) lists[t] = new List<int>(3);
        for (int t = 0; t < triCount; t++)
        for (int s = 0; s < 3; s++)
        {
            EdgeRef e = edges[triEdge[t * 3 + s]];
            if (e.Class != EdgeClass.Smooth) continue;
            int other = e.TriLeft == t ? e.TriRight : e.TriLeft;
            if (other >= 0 && !lists[t].Contains(other)) lists[t].Add(other);
        }
        var result = new int[triCount][];
        for (int t = 0; t < triCount; t++) result[t] = lists[t].ToArray();
        return result;
    }
```
> Note: the `using System.Linq;` in the test is fine; `MeshTopologyBuilder` already has `System` + `System.Collections.Generic`.
- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyEdgeClassTests"` — 2 passing. Re-run B.2's filter to confirm no regression: `--filter "FullyQualifiedName~MeshTopologyWeldTests"`.
- [ ] **Step 5: Commit (PARENT repo).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyBuilder.cs src/FabricationAssistant.App.Tests/Measurement/MeshTopologyEdgeClassTests.cs
git commit -m "feat(measure): welded edge adjacency + T-junction linking + EdgeClass (crease 28deg)"
```

---

### Task B.4: `SmoothNeighbors` / `CanGrowAcross` behavior across creases and boundaries

B.3 already populated the neighbor arrays; this task adds **dedicated behavioral tests** that lock the grow-barrier contract every downstream group (D region grower, C seed resolver) relies on, and fixes any gap they expose.

**Files:**
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshTopologyNeighborGuardTests.cs`
- Modify (only if a test fails) `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopology.cs`

- [ ] **Step 1: Write the failing test.** On the right-angle corner, the two triangles of the flat quad are `SmoothNeighbors` of each other, but a flat triangle and a vertical triangle that share the 90° seam are **not** (and `CanGrowAcross` is false). On the closed cylinder, every wall triangle has between 1 and 3 smooth neighbors and `CanGrowAcross` is true across the gentle seam.
```csharp
using System;
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeshTopologyNeighborGuardTests
{
    private const double Crease28 = 28.0 * Math.PI / 180.0;

    private static (Vector3d[] v, int[] i) RightAngleCorner()
    {
        Vector3d a = new(0, 0, 0), b = new(1, 0, 0), c = new(1, 1, 0), d = new(0, 1, 0);
        Vector3d e = new(1, 0, 1), f = new(0, 0, 1);
        var v = new[] { a, b, c,  a, c, d,   a, b, e,  a, e, f };
        var i = new[] { 0, 1, 2,  3, 4, 5,   6, 7, 8,  9, 10, 11 };
        return (v, i);
    }

    [Fact]
    public void CannotGrowAcross90DegreeSeam()
    {
        (Vector3d[] v, int[] i) = RightAngleCorner();
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        // tri 0 & tri 1 are the flat quad (share its diagonal) -> smooth neighbors.
        Assert.Contains(1, topo.SmoothNeighbors(0));
        Assert.True(topo.CanGrowAcross(0, 1));

        // Find a flat tri and a vertical tri that share the seam edge (a,b)=(vertex0,vertex1).
        // Flat triangle touching the seam: tri 0 (a,b,c). Vertical triangle touching it: tri 2 (a,b,e).
        Assert.False(topo.CanGrowAcross(0, 2));        // crossing the 90deg crease seam
        Assert.DoesNotContain(2, topo.SmoothNeighbors(0));
    }

    private static (Vector3d[] v, int[] i) Cylinder(double r, double h, int seg)
    {
        var v = new Vector3d[seg * 2];
        for (int k = 0; k < seg; k++)
        {
            double t = k * Math.Tau / seg;
            v[2 * k] = new Vector3d(Math.Cos(t) * r, Math.Sin(t) * r, 0);
            v[2 * k + 1] = new Vector3d(Math.Cos(t) * r, Math.Sin(t) * r, h);
        }
        var idx = new System.Collections.Generic.List<int>();
        for (int k = 0; k < seg; k++)
        {
            int n = (k + 1) % seg;
            int b0 = 2 * k, t0 = b0 + 1, b1 = 2 * n, t1 = b1 + 1;
            idx.AddRange(new[] { b0, b1, t1,  b0, t1, t0 });
        }
        return (v, idx.ToArray());
    }

    [Fact]
    public void CanGrowAcrossGentleCylinderSeam_AndNeighborsAreBounded()
    {
        (Vector3d[] v, int[] i) = Cylinder(r: 2.0, h: 5.0, seg: 32);
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        for (int t = 0; t < topo.Triangles.Count; t++)
            Assert.InRange(topo.SmoothNeighbors(t).Count, 1, 3);

        // Adjacent wall triangles (tri 0 and tri 1 share the quad diagonal) grow freely.
        Assert.True(topo.CanGrowAcross(0, 1));
        // Non-adjacent triangles never grow.
        Assert.False(topo.CanGrowAcross(0, 10));
    }
}
```
- [ ] **Step 2: Run it, expect FAIL or PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyNeighborGuardTests"`. If B.3 is correct these may already pass on first run — that is acceptable for a behavior-lock task; if any assertion fails (e.g. seam not detected as crease so `CanGrowAcross(0,2)` returns true), proceed to Step 3.
- [ ] **Step 3: Implement (only if a test failed).** The most likely gap is `CanGrowAcross` returning true for a crease seam because the seam was T-junction-folded incorrectly, or `SmoothNeighbors` containing a crossed neighbor. If `CanGrowAcross(0,2)` wrongly returns true, harden the seam check in `MeshTopology.CanGrowAcross` to additionally reject when the shared edge is anything but `Smooth` even if discovered via a different slot:
```csharp
    public bool CanGrowAcross(int triA, int triB)
    {
        if (triA == triB) return false;
        for (int s = 0; s < 3; s++)
        {
            int e = _triangleEdgeIndices[triA * 3 + s];
            EdgeRef edge = Edges[e];
            int other = edge.TriLeft == triA ? edge.TriRight : edge.TriLeft;
            if (other == triB)
                return edge.Class == EdgeClass.Smooth;
        }
        // Triangles can share a single vertex (vertex fan) without sharing an edge — never grow there.
        return false;
    }
```
If all assertions passed in Step 2, make no code change and skip to Step 4.
- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyNeighborGuardTests"` — 2 passing.
- [ ] **Step 5: Commit (PARENT repo).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.App.Tests/Measurement/MeshTopologyNeighborGuardTests.cs src/FabricationAssistant.Core/Measurement/Engine/MeshTopology.cs
git commit -m "test(measure): lock SmoothNeighbors/CanGrowAcross crease+boundary barrier"
```

---

### Task B.5: Per-triangle principal curvature via least-squares quadric over the 1-ring

Fill the `curv` array B.2/B.3 left as default. For each triangle, gather its 1-ring (the triangle's three welded vertices plus those of its **smooth** neighbors), project them into a local tangent frame defined by the triangle normal, fit a quadric height field `z(u,v) = ½(a u² + 2b uv + c v²)` by least squares, and take the eigenvalues of the 2×2 shape operator `[[a,b],[b,c]]` as principal curvatures `K1` (larger |·|) and `K2`, with `Dir1` the world-space tangent direction of `K1`.

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyBuilder.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshTopologyCurvatureTests.cs`

- [ ] **Step 1: Write the failing test.** Curvature ≈ 0 on a flat plate; curvature ≈ 1/r on a cylinder wall (one principal ≈ 1/r, the other ≈ 0). Use a finely tessellated plane and a 64-segment cylinder so the discrete quadric fit is well-conditioned.
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

public sealed class MeshTopologyCurvatureTests
{
    private const double Crease28 = 28.0 * Math.PI / 180.0;

    // NxN grid plate in the z=0 plane spanning [0,size]^2.
    private static (Vector3d[] v, int[] i) Plate(double size, int n)
    {
        var v = new Vector3d[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
            v[y * (n + 1) + x] = new Vector3d(size * x / n, size * y / n, 0);
        var idx = new List<int>();
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int p = y * (n + 1) + x;
            idx.AddRange(new[] { p, p + 1, p + n + 2,  p, p + n + 2, p + n + 1 });
        }
        return (v, idx.ToArray());
    }

    private static (Vector3d[] v, int[] i) Cylinder(double r, double h, int seg, int hSeg)
    {
        var v = new Vector3d[seg * (hSeg + 1)];
        for (int z = 0; z <= hSeg; z++)
        for (int k = 0; k < seg; k++)
        {
            double t = k * Math.Tau / seg;
            v[z * seg + k] = new Vector3d(Math.Cos(t) * r, Math.Sin(t) * r, h * z / hSeg);
        }
        var idx = new List<int>();
        for (int z = 0; z < hSeg; z++)
        for (int k = 0; k < seg; k++)
        {
            int n = (k + 1) % seg;
            int b0 = z * seg + k, b1 = z * seg + n, t0 = (z + 1) * seg + k, t1 = (z + 1) * seg + n;
            idx.AddRange(new[] { b0, b1, t1,  b0, t1, t0 });
        }
        return (v, idx.ToArray());
    }

    [Fact]
    public void Curvature_OnFlatPlate_IsApproximatelyZero()
    {
        (Vector3d[] v, int[] i) = Plate(size: 4.0, n: 8);
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        // Use an interior triangle (away from grid boundary) for a full 1-ring.
        int interior = topo.Triangles.Count / 2;
        TriCurvature c = topo.Curvatures[interior];
        Assert.True(Math.Abs(c.K1) < 1e-3, $"K1={c.K1}");
        Assert.True(Math.Abs(c.K2) < 1e-3, $"K2={c.K2}");
    }

    [Fact]
    public void Curvature_OnCylinder_IsApproximatelyOneOverRadius()
    {
        const double r = 2.0;
        (Vector3d[] v, int[] i) = Cylinder(r, h: 6.0, seg: 64, hSeg: 8);
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        // Average |K1| and |K2| over interior wall triangles (skip top/bottom ring rows).
        var k1s = new List<double>();
        var k2s = new List<double>();
        for (int t = 0; t < topo.Triangles.Count; t++)
        {
            if (topo.SmoothNeighbors(t).Count < 3) continue;   // boundary-ish; skip
            TriCurvature c = topo.Curvatures[t];
            k1s.Add(Math.Abs(c.K1));
            k2s.Add(Math.Abs(c.K2));
        }
        double meanK1 = k1s.Average();
        double meanK2 = k2s.Average();

        // One principal curvature ~ 1/r, the other ~ 0.
        Assert.Equal(1.0 / r, meanK1, 1);          // within 0.05 absolute (precision:1 => 1 decimal)
        Assert.True(meanK2 < 0.1, $"meanK2={meanK2}");
    }
}
```
> The cylinder assertion uses `Assert.Equal(0.5, meanK1, 1)` (1/r = 0.5), i.e. agreement to one decimal place — robust to discrete-quadric bias on a 64-segment wall.
- [ ] **Step 2: Run it, expect FAIL.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyCurvatureTests"` — fails: B.2 left `curv` as `default`, so every `TriCurvature` is `(0,0,(0,0,0))`. The plane test passes by accident (zeros), but the cylinder test fails: `meanK1` expected `0.5`, actual `0.0`.
- [ ] **Step 3: Implement.** Replace the curvature placeholder in `MeshTopologyBuilder.Build`:
```csharp
        // ── 5. Curvature filled by B.5; default for now. ──
        var curv = new TriCurvature[triCount];
```
with:
```csharp
        // ── 5. Per-triangle principal curvature via 1-ring quadric fit ──
        var curv = new TriCurvature[triCount];
        for (int t = 0; t < triCount; t++)
            curv[t] = EstimateCurvature(t, weldedIndices, welded, tris, smoothNeighbors);
```
Add the estimator + a 2×2 symmetric eigensolver + a 3×3 normal-equation solver to `MeshTopologyBuilder`:
```csharp
    private static TriCurvature EstimateCurvature(
        int tri, int[] weldedIndices, IReadOnlyList<Vector3d> verts, IReadOnlyList<TriData> tris,
        int[][] smoothNeighbors)
    {
        TriData td = tris[tri];
        if (!td.Valid) return new TriCurvature(0.0, 0.0, Vector3d.Zero);

        Vector3d n = td.Normal;
        Vector3d origin = td.Centroid;
        Vector3d u = Vector3d.BuildPerpendicular(n);
        Vector3d w = Vector3d.Cross(n, u).Normalized();

        // Gather 1-ring sample points: this triangle's 3 verts + smooth-neighbor verts.
        var samples = new List<Vector3d>(12);
        AddTriVerts(samples, tri, weldedIndices, verts);
        foreach (int nb in smoothNeighbors[tri])
            AddTriVerts(samples, nb, weldedIndices, verts);

        // Fit z = 0.5*(a*uu + 2*b*uv + c*vv) + d*uu? No: pure quadric height field with linear terms
        // to absorb tilt: z = e*x + f*y + 0.5*a*x^2 + b*x*y + 0.5*c*y^2. Solve least squares for
        // (a,b,c,e,f). With many samples this is overdetermined; use normal equations on 5 unknowns.
        // To keep it well-conditioned and dependency-free, drop linear terms by centering the ring
        // and using a 3-unknown fit z = 0.5*a*x^2 + b*x*y + 0.5*c*y^2 (samples already lie near the
        // tangent plane through the centroid, so linear tilt is small).
        double s00 = 0, s01 = 0, s02 = 0, s11 = 0, s12 = 0, s22 = 0; // A^T A entries
        double r0 = 0, r1 = 0, r2 = 0;                                // A^T z entries
        int used = 0;
        foreach (Vector3d p in samples)
        {
            Vector3d rel = p - origin;
            double x = Vector3d.Dot(rel, u);
            double y = Vector3d.Dot(rel, w);
            double z = Vector3d.Dot(rel, n);
            // basis row: [0.5*x^2, x*y, 0.5*y^2]
            double b0 = 0.5 * x * x, b1 = x * y, b2 = 0.5 * y * y;
            s00 += b0 * b0; s01 += b0 * b1; s02 += b0 * b2;
            s11 += b1 * b1; s12 += b1 * b2; s22 += b2 * b2;
            r0 += b0 * z; r1 += b1 * z; r2 += b2 * z;
            used++;
        }
        if (used < 4) return new TriCurvature(0.0, 0.0, u);

        if (!Solve3x3Symmetric(s00, s01, s02, s11, s12, s22, r0, r1, r2,
                out double a, out double b, out double c))
            return new TriCurvature(0.0, 0.0, u);

        // Shape operator (Weingarten approx for a near-tangent height field) = [[a,b],[b,c]].
        Eigen2x2Symmetric(a, b, c, out double lambda1, out double lambda2,
            out double dirX, out double dirY);

        // K1 is the larger-magnitude principal curvature.
        double k1 = lambda1, k2 = lambda2;
        double evX = dirX, evY = dirY;
        if (Math.Abs(k2) > Math.Abs(k1))
        {
            (k1, k2) = (k2, k1);
            // eigenvector of the OTHER eigenvalue is perpendicular in-plane.
            (evX, evY) = (-dirY, dirX);
        }
        Vector3d dir1 = (u * evX + w * evY).Normalized();
        return new TriCurvature(k1, k2, dir1);
    }

    private static void AddTriVerts(List<Vector3d> samples, int tri, int[] weldedIndices, IReadOnlyList<Vector3d> verts)
    {
        samples.Add(verts[weldedIndices[tri * 3 + 0]]);
        samples.Add(verts[weldedIndices[tri * 3 + 1]]);
        samples.Add(verts[weldedIndices[tri * 3 + 2]]);
    }

    // Solve the 3x3 symmetric positive-(semi)definite system (A^T A) x = A^T z via Cholesky;
    // returns false if singular.
    private static bool Solve3x3Symmetric(
        double a00, double a01, double a02, double a11, double a12, double a22,
        double r0, double r1, double r2,
        out double x0, out double x1, out double x2)
    {
        x0 = x1 = x2 = 0.0;
        const double eps = 1e-18;
        // Cholesky: A = L L^T
        double l00 = a00;
        if (l00 <= eps) return false;
        l00 = Math.Sqrt(l00);
        double l10 = a01 / l00;
        double l20 = a02 / l00;
        double l11 = a11 - l10 * l10;
        if (l11 <= eps) return false;
        l11 = Math.Sqrt(l11);
        double l21 = (a12 - l20 * l10) / l11;
        double l22 = a22 - l20 * l20 - l21 * l21;
        if (l22 <= eps) return false;
        l22 = Math.Sqrt(l22);

        // Forward solve L y = r
        double y0 = r0 / l00;
        double y1 = (r1 - l10 * y0) / l11;
        double y2 = (r2 - l20 * y0 - l21 * y1) / l22;
        // Back solve L^T x = y
        x2 = y2 / l22;
        x1 = (y1 - l21 * x2) / l11;
        x0 = (y0 - l10 * x1 - l20 * x2) / l00;
        return double.IsFinite(x0) && double.IsFinite(x1) && double.IsFinite(x2);
    }

    // Eigenvalues/eigenvector of [[a,b],[b,c]]. lambda1 is returned with its unit eigenvector (dirX,dirY).
    private static void Eigen2x2Symmetric(
        double a, double b, double c,
        out double lambda1, out double lambda2, out double dirX, out double dirY)
    {
        double tr = a + c;
        double det = a * c - b * b;
        double disc = Math.Sqrt(Math.Max(0.0, tr * tr * 0.25 - det));
        lambda1 = tr * 0.5 + disc;
        lambda2 = tr * 0.5 - disc;

        // Eigenvector for lambda1: (b, lambda1 - a) or (lambda1 - c, b), whichever is larger.
        double ex, ey;
        if (Math.Abs(b) > 1e-15)
        {
            ex = lambda1 - c;
            ey = b;
        }
        else
        {
            // Diagonal matrix: eigenvectors are axis-aligned.
            ex = 1.0; ey = 0.0;
        }
        double len = Math.Sqrt(ex * ex + ey * ey);
        if (len < 1e-15) { dirX = 1.0; dirY = 0.0; return; }
        dirX = ex / len; dirY = ey / len;
    }
```
- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyCurvatureTests"` — 2 passing. Re-run the whole `MeshTopology` filter to confirm no regression: `--filter "FullyQualifiedName~MeshTopology"`.
- [ ] **Step 5: Commit (PARENT repo).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyBuilder.cs src/FabricationAssistant.App.Tests/Measurement/MeshTopologyCurvatureTests.cs
git commit -m "feat(measure): per-triangle principal curvature via 1-ring quadric fit"
```

---

### Task B.6: `IMeshTopologyProvider` + `MeshTopologyCache` (LRU keyed on meshKey, transformVersion)

The cache builds a `MeshTopology` once per `(meshKey, transformVersion)` and reuses it; on a transform-version bump it rebuilds; capacity is LRU-bounded (default 8). This retires the per-hover full rebuild.

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyCache.cs` (contains `IMeshTopologyProvider` + `MeshTopologyCache`)
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshTopologyCacheTests.cs`

- [ ] **Step 1: Write the failing test.** Same key returns the same instance (built once); changed `transformVersion` rebuilds; LRU evicts the least-recently-used beyond capacity.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

public sealed class MeshTopologyCacheTests
{
    private const double Crease28 = 28.0 * Math.PI / 180.0;

    private static (Vector3d[] v, int[] i) Quad()
    {
        var v = new[] { new Vector3d(0,0,0), new Vector3d(1,0,0), new Vector3d(1,1,0),
                        new Vector3d(0,0,0), new Vector3d(1,1,0), new Vector3d(0,1,0) };
        var i = new[] { 0, 1, 2, 3, 4, 5 };
        return (v, i);
    }

    [Fact]
    public void GetOrBuild_SameKey_ReturnsCachedInstance()
    {
        (Vector3d[] v, int[] i) = Quad();
        var cache = new MeshTopologyCache(capacity: 8);

        MeshTopology a = cache.GetOrBuild(meshKey: 1, transformVersion: 0, v, i, Crease28);
        MeshTopology b = cache.GetOrBuild(meshKey: 1, transformVersion: 0, v, i, Crease28);

        Assert.Same(a, b);
    }

    [Fact]
    public void GetOrBuild_NewTransformVersion_Rebuilds()
    {
        (Vector3d[] v, int[] i) = Quad();
        var cache = new MeshTopologyCache(capacity: 8);

        MeshTopology a = cache.GetOrBuild(meshKey: 1, transformVersion: 0, v, i, Crease28);
        MeshTopology c = cache.GetOrBuild(meshKey: 1, transformVersion: 1, v, i, Crease28);

        Assert.NotSame(a, c);
        // ... and the old version is still distinct from a re-request at version 1.
        Assert.Same(c, cache.GetOrBuild(meshKey: 1, transformVersion: 1, v, i, Crease28));
    }

    [Fact]
    public void GetOrBuild_EvictsLeastRecentlyUsed_BeyondCapacity()
    {
        (Vector3d[] v, int[] i) = Quad();
        var cache = new MeshTopologyCache(capacity: 2);

        MeshTopology m1 = cache.GetOrBuild(1, 0, v, i, Crease28);
        MeshTopology m2 = cache.GetOrBuild(2, 0, v, i, Crease28);
        // Touch key 1 so key 2 becomes LRU.
        Assert.Same(m1, cache.GetOrBuild(1, 0, v, i, Crease28));
        // Insert key 3 -> evicts key 2 (LRU).
        MeshTopology m3 = cache.GetOrBuild(3, 0, v, i, Crease28);
        Assert.NotNull(m3);
        // Key 1 still cached (same instance), key 2 was evicted (rebuilt -> different instance).
        Assert.Same(m1, cache.GetOrBuild(1, 0, v, i, Crease28));
        Assert.NotSame(m2, cache.GetOrBuild(2, 0, v, i, Crease28));
    }
}
```
- [ ] **Step 2: Run it, expect FAIL.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyCacheTests"` — fails to compile: `CS0246 ... 'MeshTopologyCache'`.
- [ ] **Step 3: Implement.**
```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Builds (or returns a cached) <see cref="MeshTopology"/> for a mesh identified by
/// <paramref name="meshKey"/> at a given <paramref name="transformVersion"/>. A transform-version
/// bump invalidates the entry (topology is stored in local space, but the cache key tracks the
/// version so consumers can rely on a stable token).
/// </summary>
public interface IMeshTopologyProvider
{
    MeshTopology GetOrBuild(
        long meshKey,
        long transformVersion,
        IReadOnlyList<Vector3d> verticesLocal,
        IReadOnlyList<int> indices,
        double creaseAngleRad);
}

/// <summary>
/// LRU-bounded cache of welded mesh topologies keyed on (meshKey, transformVersion). Built once
/// per mesh/transform; reused across hovers and the commit pass. Retires the per-hover full
/// rebuild. Not thread-safe by itself; callers serialize access (the warmup runner builds
/// off-thread, then publishes through this cache on the UI thread).
/// </summary>
public sealed class MeshTopologyCache : IMeshTopologyProvider
{
    private readonly int _capacity;
    private readonly Dictionary<(long, long), LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru; // front = most recently used

    public MeshTopologyCache(int capacity = 8)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _map = new Dictionary<(long, long), LinkedListNode<Entry>>(_capacity);
        _lru = new LinkedList<Entry>();
    }

    public MeshTopology GetOrBuild(
        long meshKey,
        long transformVersion,
        IReadOnlyList<Vector3d> verticesLocal,
        IReadOnlyList<int> indices,
        double creaseAngleRad)
    {
        var key = (meshKey, transformVersion);
        if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Topology;
        }

        MeshTopology built = MeshTopologyBuilder.Build(verticesLocal, indices, creaseAngleRad);
        var entry = new Entry(key, built);
        LinkedListNode<Entry> added = _lru.AddFirst(entry);
        _map[key] = added;

        while (_map.Count > _capacity)
        {
            LinkedListNode<Entry>? last = _lru.Last;
            if (last is null) break;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
        }

        return built;
    }

    private readonly record struct Entry((long, long) Key, MeshTopology Topology);
}
```
- [ ] **Step 4: Run it, expect PASS.** `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopologyCacheTests"` — 3 passing.
- [ ] **Step 5: Commit (PARENT repo).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/MeshTopologyCache.cs src/FabricationAssistant.App.Tests/Measurement/MeshTopologyCacheTests.cs
git commit -m "feat(measure): MeshTopologyCache LRU provider keyed on (meshKey, transformVersion)"
```

---

### Task B.7: Link Group-B types into the Android mirror test project + Android smoke test

The Android suite links Core selectively. Add explicit `<Compile Include>` entries for the new Group-B source files and add one mirror test so the topology kernel is verified under the Android test TFM (`net8.0`, host-runnable).

**Files:**
- Modify `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Test (Android repo) `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/MeshTopologyMirrorTests.cs`

- [ ] **Step 1: Add the compile-link entries.** In the **first** `<ItemGroup>` of the csproj (the one that links the Core `Measurement\Engine` files, right after the existing `CircularFeatureDetectionService.cs` link at line ~68–69), add:
```xml
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopologyContracts.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopology.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopologyBuilder.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopologyCache.cs"
             LinkBase="Linked\Measurement\Engine" />
```
- [ ] **Step 2: Write the failing mirror test.** A compact, self-contained assertion covering all four Group-B claims (weld count, crease across 90°, smooth across cylinder seam, curvature ~1/r) so the Android suite carries detection coverage (review gap S25/§4.3 "zero CircularFeature detection tests in the Android suite").
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class MeshTopologyMirrorTests
{
    private const double Crease28 = 28.0 * Math.PI / 180.0;

    [Fact]
    public void Weld_CollapsesUnsharedDuplicatePositions()
    {
        Vector3d p00 = new(0, 0, 0), p10 = new(1, 0, 0), p11 = new(1, 1, 0), p01 = new(0, 1, 0);
        var v = new[] { p00, p10, p11,  p00, p11, p01 };
        var i = new[] { 0, 1, 2,        3, 4, 5 };
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);
        Assert.Equal(4, topo.Vertices.Count);
    }

    [Fact]
    public void Crease_FlaggedAcross90DegCorner_AndCannotGrow()
    {
        Vector3d a = new(0, 0, 0), b = new(1, 0, 0), c = new(1, 1, 0), d = new(0, 1, 0);
        Vector3d e = new(1, 0, 1), f = new(0, 0, 1);
        var v = new[] { a, b, c,  a, c, d,   a, b, e,  a, e, f };
        var i = new[] { 0, 1, 2,  3, 4, 5,   6, 7, 8,  9, 10, 11 };
        MeshTopology topo = MeshTopologyBuilder.Build(v, i, Crease28);

        Assert.Contains(topo.Edges, x => x.Class == EdgeClass.Crease);
        Assert.False(topo.CanGrowAcross(0, 2));   // flat tri 0 vs vertical tri 2 across the seam
    }

    [Fact]
    public void Smooth_AcrossCylinderSeam_AndCurvatureIsOneOverRadius()
    {
        const double r = 2.0;
        int seg = 64, hSeg = 8;
        var v = new Vector3d[seg * (hSeg + 1)];
        for (int z = 0; z <= hSeg; z++)
        for (int k = 0; k < seg; k++)
        {
            double t = k * Math.Tau / seg;
            v[z * seg + k] = new Vector3d(Math.Cos(t) * r, Math.Sin(t) * r, 6.0 * z / hSeg);
        }
        var idx = new List<int>();
        for (int z = 0; z < hSeg; z++)
        for (int k = 0; k < seg; k++)
        {
            int n = (k + 1) % seg;
            int b0 = z * seg + k, b1 = z * seg + n, t0 = (z + 1) * seg + k, t1 = (z + 1) * seg + n;
            idx.AddRange(new[] { b0, b1, t1,  b0, t1, t0 });
        }
        MeshTopology topo = MeshTopologyBuilder.Build(v, idx.ToArray(), Crease28);

        Assert.Equal(0, topo.Edges.Count(x => x.Class == EdgeClass.Crease));   // gentle seam stays smooth

        var k1s = new List<double>();
        for (int t = 0; t < topo.Triangles.Count; t++)
            if (topo.SmoothNeighbors(t).Count >= 3)
                k1s.Add(Math.Abs(topo.Curvatures[t].K1));
        Assert.Equal(1.0 / r, k1s.Average(), 1);   // ~0.5
    }
}
```
- [ ] **Step 3: Run, expect compile/link to drive the change.** From PARENT root: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~MeshTopologyMirrorTests"`. If you forgot a `<Compile Include>`, this fails with `CS0246` for the missing type — add it and retry. (No separate "implement" body: the implementation is the csproj link plus the already-built Core code.)
- [ ] **Step 4: Run it, expect PASS.** Re-run the same command — 3 passing. If you hit `CS2012`/file-lock: `dotnet build-server shutdown` then retry.
- [ ] **Step 5: Commit (ANDROID repo — stage by explicit path).**
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/MeshTopologyMirrorTests.cs
git commit -m "test(android): mirror MeshTopology weld/crease/curvature coverage"
```

---

> **Group B exit criteria.** From PARENT root, `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshTopology"` is green (all of B.1–B.6), and `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~MeshTopologyMirror"` is green (B.7). The canonical types `MeshTopology`, `MeshTopologyCache`, `IMeshTopologyProvider`, `EdgeClass`, `TriData`, `EdgeRef`, `TriCurvature` are now available for Groups C (SeedResolver), D (SurfaceRegionGrower), F (EdgeLoopFitter), G (ChamferDetector), and I (pipeline). Welded vertex count, 28° crease detection across a 90° corner, smooth edges across a cylinder seam, plane curvature ≈ 0, and cylinder curvature ≈ 1/r are all asserted with concrete numeric expectations.

---

`Length` and `LengthSquared` are properties (no parentheses). The canonical contract mentions `.Length()` and `.LengthSquared()` as methods, but the actual codebase exposes them as properties. I'll use `.Length` (property) in test/impl code to compile against the real type, while keeping all cross-boundary contract type names exactly as specified. I have enough context to write the Group C section.

## Group C — SeedResolver (screen-space aperture cone, front-face culling, curvature-biased ranking)

> **Preamble (read once).** Group C implements `SeedResolver.Resolve` — the front door of the pipeline. It consumes a `MeshTopology` (Group B; the canonical contract type) and a local-space ray, casts a small **aperture cone** of rays around the tap, keeps only **front-facing** hits (Möller-Trumbore determinant sign), and ranks the surviving triangles by a **curvature signature** that prefers cylinder/torus bands over planar faces. It returns the canonical `CircularSeed`. The GPU id-buffer pick is documented as a re-validated *hint* (the integration point is task C.5) but is **not** implemented here.
>
> **Two repos.** All Group C production + test code is **Core + host tests in the PARENT repo** (`C:/Users/skritikos/Desktop/Fabrication Assistant`). New `.cs` under `src/FabricationAssistant.Core/Measurement/**` auto-link into Android via the wildcard — no Android csproj edit for the production type. The Android **mirror test** (C.6) lives in the ANDROID repo and may need an explicit `<Compile Include>` for `SeedResolver.cs` in the Android test csproj because that project links Core selectively. Commit Core + host tests in PARENT; commit the Android mirror in ANDROID, staging by explicit path.
>
> **Math note.** In this codebase `Vector3d.Length` and `Vector3d.LengthSquared` are **properties** (no parentheses). Use `v.Length`, `v.LengthSquared`. `Vector3d.Dot(a,b)`, `Vector3d.Cross(a,b)`, `a.Normalized()`, operators `+ - *`, and `Vector3d.UnitX/Y/Z` are as in the contract.
>
> **Dependency note.** Group C depends on the canonical `MeshTopology`, `TriData`, `TriCurvature`, `EdgeClass`, `EdgeRef` (Group B) and on `SurfaceKind` (Group E), plus the `CircularMeshFixtures` / `GeoAssert` test fixtures (Group A). If you are executing Group C **before** B/A/E are merged, create the *minimal* stand-ins only inside test scaffolding is **not** allowed for contract types — instead pull the merged B/A/E first. The tasks below assume B, A, E are present.
>
> **Build-lock.** On CSC `CS2012` / file-lock failures: `dotnet build-server shutdown`, retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`.

---

### Task C.1: Aperture-cone ray generation (private helper) + front-face culling primitive

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SeedResolverApertureTests.cs`

This task stands up the file with the canonical `Resolve` signature returning `default` (so the project compiles), and implements + tests the two numeric primitives the resolver is built on: (1) generating a small fan of rays inside the aperture cone, and (2) the Möller-Trumbore single-triangle intersection that reports the determinant sign for front-face culling. Both are exposed as `internal static` so host tests can assert their numbers directly.

- [ ] **Step 1: Write the failing test**

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SeedResolverApertureTests
{
    // The aperture fan must contain the central ray exactly, plus a ring of
    // rays whose angular offset equals atan(apertureRadiusLocal / focalLen).
    [Fact]
    public void BuildApertureRays_CentralRayIsExactAndRingIsConcentric()
    {
        Vector3d origin = new(0, 0, 10);
        Vector3d dir = new Vector3d(0, 0, -1);            // looking down -Z
        double apertureRadiusLocal = 0.5;                  // local-space cone radius at focal plane
        double focalLen = 10.0;                            // distance origin->focal plane

        Vector3d[] dirs = SeedResolver.BuildApertureRaysForTest(
            origin, dir, apertureRadiusLocal, focalLen);

        // 1 central + 6 ring = 7 rays (hex ring is the fixed sample count).
        Assert.Equal(7, dirs.Length);

        // Central ray is exactly the input direction (normalized).
        Assert.Equal(0.0, (dirs[0] - dir).Length, 12);

        // Every ring ray is unit length and makes the same cone half-angle.
        double expectedHalfAngle = System.Math.Atan2(apertureRadiusLocal, focalLen);
        for (int k = 1; k < dirs.Length; k++)
        {
            Assert.Equal(1.0, dirs[k].Length, 9);
            double cos = Vector3d.Dot(dirs[k], dir);       // dir is unit
            Assert.Equal(System.Math.Cos(expectedHalfAngle), cos, 9);
        }
    }

    // Möller-Trumbore: a ray through the front of a CCW (outward-wound) triangle
    // returns hit=true, t>0, and a POSITIVE determinant (front face).
    [Fact]
    public void IntersectTriangle_FrontHit_ReturnsPositiveDeterminantAndDistance()
    {
        // CCW triangle in the z=0 plane, outward normal = +Z.
        Vector3d a = new(0, 0, 0);
        Vector3d b = new(1, 0, 0);
        Vector3d c = new(0, 1, 0);
        Vector3d origin = new(0.25, 0.25, 5);              // above, on +Z side
        Vector3d dir = new Vector3d(0, 0, -1);             // shooting toward -Z (into front)

        bool hit = SeedResolver.IntersectTriangleForTest(
            origin, dir, a, b, c, out double t, out double det);

        Assert.True(hit);
        Assert.Equal(5.0, t, 9);                           // travels 5 to reach z=0
        Assert.True(det > 0.0, $"front face must have det>0, was {det}");
    }

    // Same triangle hit from BEHIND (ray along +Z from below) -> det < 0 (back face).
    [Fact]
    public void IntersectTriangle_BackHit_ReturnsNegativeDeterminant()
    {
        Vector3d a = new(0, 0, 0);
        Vector3d b = new(1, 0, 0);
        Vector3d c = new(0, 1, 0);
        Vector3d origin = new(0.25, 0.25, -5);             // below, on -Z side
        Vector3d dir = new Vector3d(0, 0, 1);              // shooting toward +Z (into back)

        bool hit = SeedResolver.IntersectTriangleForTest(
            origin, dir, a, b, c, out double t, out double det);

        Assert.True(hit);
        Assert.Equal(5.0, t, 9);
        Assert.True(det < 0.0, $"back face must have det<0, was {det}");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverApertureTests"`
  Expected failure: **compile error** — `SeedResolver` / `BuildApertureRaysForTest` / `IntersectTriangleForTest` do not exist (`CS0103` / `CS0117`).

- [ ] **Step 3: Implement** the file with the canonical `Resolve` stub plus the two `internal static` numeric primitives and their `…ForTest` wrappers.

```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Resolves a tap/hover ray to a <see cref="CircularSeed"/> on a curved feature band.
/// Casts a small aperture cone, culls back faces (Möller-Trumbore determinant sign),
/// and ranks survivors by a curvature signature (cylinder/torus over planar).
/// All inputs/outputs are in LOCAL mesh space.
/// </summary>
public static class SeedResolver
{
    // Fixed hex-ring sample count: 1 central + 6 ring rays. Small + cheap (Risk §12).
    private const int RingSamples = 6;
    private const double RayEps = 1e-9;

    /// <summary>Canonical entry point. Implemented incrementally across C.2–C.4.</summary>
    public static CircularSeed Resolve(
        MeshTopology topo,
        Vector3d rayOriginLocal,
        Vector3d rayDirLocal,
        double apertureRadiusLocal)
    {
        // Filled in by C.2 (cone cast over the mesh) and C.4 (ranking).
        return default;
    }

    /// <summary>
    /// Build the aperture fan: a central ray (the input direction) plus a hex ring
    /// of rays offset by half-angle = atan(apertureRadiusLocal / focalLen).
    /// </summary>
    internal static Vector3d[] BuildApertureRays(
        Vector3d originLocal,
        Vector3d dirLocal,
        double apertureRadiusLocal,
        double focalLen)
    {
        Vector3d fwd = dirLocal.Normalized();
        var rays = new Vector3d[1 + RingSamples];
        rays[0] = fwd;

        if (apertureRadiusLocal <= 0.0 || focalLen <= RayEps)
        {
            for (int k = 1; k < rays.Length; k++)
                rays[k] = fwd;
            return rays;
        }

        // Orthonormal basis (u,v) spanning the focal plane perpendicular to fwd.
        Vector3d seed = System.Math.Abs(fwd.Y) < 0.9 ? Vector3d.UnitY : Vector3d.UnitX;
        Vector3d u = Vector3d.Cross(fwd, seed).Normalized();
        Vector3d v = Vector3d.Cross(fwd, u).Normalized();

        // A point on the focal plane: origin + fwd*focalLen + radius*(cos,sin).
        for (int k = 0; k < RingSamples; k++)
        {
            double phi = k * (System.Math.Tau / RingSamples);
            Vector3d offset = (u * System.Math.Cos(phi) + v * System.Math.Sin(phi)) * apertureRadiusLocal;
            Vector3d target = originLocal + fwd * focalLen + offset;
            rays[1 + k] = (target - originLocal).Normalized();
        }

        return rays;
    }

    /// <summary>
    /// Möller-Trumbore ray/triangle intersection. Returns the signed determinant
    /// (det = e1 · (dir × e2)); det &gt; 0 means the ray hits the CCW (outward) front
    /// face, det &lt; 0 means the back face. <paramref name="t"/> is the ray parameter.
    /// </summary>
    internal static bool IntersectTriangle(
        Vector3d origin,
        Vector3d dir,
        Vector3d a,
        Vector3d b,
        Vector3d c,
        out double t,
        out double det)
    {
        t = 0.0;
        Vector3d e1 = b - a;
        Vector3d e2 = c - a;
        Vector3d p = Vector3d.Cross(dir, e2);
        det = Vector3d.Dot(e1, p);
        if (System.Math.Abs(det) < RayEps)
            return false; // ray parallel to triangle plane

        double inv = 1.0 / det;
        Vector3d s = origin - a;
        double bu = Vector3d.Dot(s, p) * inv;
        if (bu < -RayEps || bu > 1.0 + RayEps)
            return false;

        Vector3d q = Vector3d.Cross(s, e1);
        double bv = Vector3d.Dot(dir, q) * inv;
        if (bv < -RayEps || bu + bv > 1.0 + RayEps)
            return false;

        t = Vector3d.Dot(e2, q) * inv;
        return t > RayEps;
    }

    // ---- test seams (internal; host test project sees internals) ----
    internal static Vector3d[] BuildApertureRaysForTest(
        Vector3d origin, Vector3d dir, double apertureRadiusLocal, double focalLen)
        => BuildApertureRays(origin, dir, apertureRadiusLocal, focalLen);

    internal static bool IntersectTriangleForTest(
        Vector3d origin, Vector3d dir, Vector3d a, Vector3d b, Vector3d c,
        out double t, out double det)
        => IntersectTriangle(origin, dir, a, b, c, out t, out det);
}
```

  > **Internals visibility:** `FabricationAssistant.App.Tests` already consumes Core internals via the existing `[assembly: InternalsVisibleTo("FabricationAssistant.App.Tests")]` in Core (the same mechanism the current `CircularFeatureDetectionService` tests rely on). If a `CS0103`/access error appears for the `…ForTest` seams, confirm that attribute exists in `src/FabricationAssistant.Core/Properties/AssemblyInfo.cs` (or any Core `.cs`) and add it if missing — do **not** make the seams `public`.

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverApertureTests"`
  Expected: 3 passed.

- [ ] **Step 5: Commit (PARENT repo)**
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add `
  "src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs" `
  "src/FabricationAssistant.App.Tests/Measurement/SeedResolverApertureTests.cs"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): SeedResolver aperture-cone + Moller-Trumbore front-face primitive

Group C task C.1. Adds SeedResolver.cs with the canonical Resolve stub plus the
two numeric primitives: hex-ring aperture fan generation and signed-determinant
ray/triangle intersection for front-face culling. Host tests assert exact angles
and determinant signs.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task C.2: Cast the aperture cone over the whole mesh; collect front-facing hits

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SeedResolverConeCastTests.cs`

Implement the cone cast: for each of the 7 aperture rays, intersect every triangle in the topology, keep only **front** hits (`det > 0`, except for interior-bore walls — handled in C.3), and record the nearest front hit per ray as a candidate. Output is an internal `Candidate` list `(triIndex, hitLocal, t, rayIndex)`. We verify with a `Cylinder` fixture that a central ray straight at the wall returns the wall triangle, and that an off-axis aperture ray (a few "px" off, modelled as a small local offset) still lands on the cylinder band rather than missing.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.App.Tests.Measurement; // CircularMeshFixtures, GeoAssert
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SeedResolverConeCastTests
{
    private static MeshTopology BuildTopo((Vector3d[] v, int[] i) mesh)
        => new MeshTopologyCache()
            .GetOrBuild(meshKey: 1, transformVersion: 1, mesh.v, mesh.i,
                        creaseAngleRad: 28.0 * System.Math.PI / 180.0);

    [Fact]
    public void CollectFrontHits_RayAtOuterWall_ReturnsTriangleOnTheBand()
    {
        // Full cylinder, radius 2, axis +Z, centred at origin spanning z in [0,4].
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 4.0, segments: 48,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);

        // Shoot inward at the +X side of the wall, from outside.
        Vector3d origin = new(5.0, 0.0, 2.0);
        Vector3d dir = new Vector3d(-1.0, 0.0, 0.0);

        var hits = SeedResolver.CollectFrontHitsForTest(topo, origin, dir, apertureRadiusLocal: 0.12);

        Assert.NotEmpty(hits);
        // Nearest front hit must be on the +X wall (hit point near x=+2, on the band, not the far x=-2 wall).
        var nearest = hits.OrderBy(h => h.T).First();
        Assert.Equal(2.0, nearest.HitLocal.X, 1);          // near wall, +X side
        Assert.True(nearest.HitLocal.X > 0.0, "nearest front hit must be the near (+X) wall");
    }

    [Fact]
    public void CollectFrontHits_RayAFewPxOffTheBand_StillHitsTheBandViaAperture()
    {
        // Quarter cylinder strip (a "fillet-like" curved band), radius 2, axis +Z.
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 4.0, segments: 24,
            sweepRadians: System.Math.PI * 0.5, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);

        // Aim the CENTRAL ray to just MISS the strip (a hair past the +Y edge of the
        // quarter sweep), but the aperture cone is wide enough that a ring ray catches it.
        Vector3d origin = new(0.05, 5.0, 2.0);             // looking down -Y at the top edge
        Vector3d dir = new Vector3d(0.0, -1.0, 0.0);

        var hitsNarrow = SeedResolver.CollectFrontHitsForTest(topo, origin, dir, apertureRadiusLocal: 0.0);
        var hitsWide   = SeedResolver.CollectFrontHitsForTest(topo, origin, dir, apertureRadiusLocal: 0.30);

        // The off-by-a-hair central ray alone may miss; the aperture cone recovers the band.
        Assert.True(hitsWide.Count >= hitsNarrow.Count);
        Assert.NotEmpty(hitsWide);
        // Every recovered hit lies on the cylinder band: radial distance ~ 2.
        foreach (var h in hitsWide)
        {
            Vector3d radial = new Vector3d(h.HitLocal.X, h.HitLocal.Y, 0.0);
            Assert.Equal(2.0, radial.Length, 1);
        }
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverConeCastTests"`
  Expected failure: **compile error** — `SeedResolver.CollectFrontHitsForTest` and the `Candidate` shape do not exist (`CS0117`).

- [ ] **Step 3: Implement** the cone-cast over the topology. Add the `Candidate` struct, `CollectFrontHits`, and its test seam.

```csharp
// add to SeedResolver.cs

    /// <summary>One front-facing hit recovered by an aperture ray.</summary>
    internal readonly record struct Candidate(int TriIndex, Vector3d HitLocal, double T, int RayIndex, double Det);

    /// <summary>
    /// Cast the aperture fan against every triangle; keep the nearest FRONT-facing hit
    /// per ray. Front = det &gt; 0 (CCW outward), OR an inward bore wall (det &lt; 0 with the
    /// triangle normal facing the ray origin — see C.3 which refines bore handling).
    /// </summary>
    internal static List<Candidate> CollectFrontHits(
        MeshTopology topo,
        Vector3d originLocal,
        Vector3d dirLocal,
        double apertureRadiusLocal)
    {
        // Focal length = distance to the model centroid bound; cheap proxy = nearest
        // central-ray hit distance, fall back to 1.0 so the cone has a sane spread.
        double focalLen = EstimateFocalLength(topo, originLocal, dirLocal);
        Vector3d[] rays = BuildApertureRays(originLocal, dirLocal, apertureRadiusLocal, focalLen);

        var result = new List<Candidate>(rays.Length);
        IReadOnlyList<Vector3d> verts = topo.Vertices;
        IReadOnlyList<int> idx = topo.Indices;
        int triCount = idx.Count / 3;

        for (int r = 0; r < rays.Length; r++)
        {
            Vector3d rd = rays[r];
            int bestTri = -1;
            double bestT = double.PositiveInfinity;
            Vector3d bestHit = default;
            double bestDet = 0.0;

            for (int tri = 0; tri < triCount; tri++)
            {
                Vector3d a = verts[idx[3 * tri]];
                Vector3d b = verts[idx[3 * tri + 1]];
                Vector3d c = verts[idx[3 * tri + 2]];
                if (!IntersectTriangle(originLocal, rd, a, b, c, out double t, out double det))
                    continue;
                if (det <= 0.0)
                    continue; // back face; bore handling refined in C.3
                if (t < bestT)
                {
                    bestT = t;
                    bestTri = tri;
                    bestHit = originLocal + rd * t;
                    bestDet = det;
                }
            }

            if (bestTri >= 0)
                result.Add(new Candidate(bestTri, bestHit, bestT, r, bestDet));
        }

        return result;
    }

    /// <summary>Nearest central-ray hit distance; falls back to 1.0.</summary>
    private static double EstimateFocalLength(MeshTopology topo, Vector3d originLocal, Vector3d dirLocal)
    {
        Vector3d rd = dirLocal.Normalized();
        IReadOnlyList<Vector3d> verts = topo.Vertices;
        IReadOnlyList<int> idx = topo.Indices;
        int triCount = idx.Count / 3;
        double bestT = double.PositiveInfinity;
        for (int tri = 0; tri < triCount; tri++)
        {
            Vector3d a = verts[idx[3 * tri]];
            Vector3d b = verts[idx[3 * tri + 1]];
            Vector3d c = verts[idx[3 * tri + 2]];
            if (IntersectTriangle(originLocal, rd, a, b, c, out double t, out _) && t < bestT)
                bestT = t;
        }
        return double.IsPositiveInfinity(bestT) ? 1.0 : bestT;
    }

    internal static List<Candidate> CollectFrontHitsForTest(
        MeshTopology topo, Vector3d origin, Vector3d dir, double apertureRadiusLocal)
        => CollectFrontHits(topo, origin, dir, apertureRadiusLocal);
```

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverConeCastTests"`
  Expected: 2 passed.

- [ ] **Step 5: Commit (PARENT repo)**
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add `
  "src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs" `
  "src/FabricationAssistant.App.Tests/Measurement/SeedResolverConeCastTests.cs"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): SeedResolver aperture cone-cast collects nearest front hits

Group C task C.2. Casts the hex-ring aperture fan over all triangles, keeps the
nearest front-facing (det>0) hit per ray. A ray a hair off a curved band is
recovered by the aperture ring; the near outer wall is preferred over the far.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task C.3: Interior-bore handling — return the NEAR inward wall, not the far wall

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SeedResolverBoreTests.cs`

For an interior bore the wall normals point **inward** (toward the axis). A ray entering the bore hits the near wall as a *back* face (`det < 0`) by the CCW-outward convention. C.2's strict `det > 0` would skip the near wall and report the far wall. Refine `CollectFrontHits` so a hit on an **inward-facing** wall (triangle normal pointing toward the ray origin: `dot(triNormal, -rayDir) < 0` i.e. normal opposes the view *and* the hit is the nearest along the ray) is accepted as a near-wall candidate. This is the spec's "a ray into a bore returns the near inward wall, not the far wall."

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.App.Tests.Measurement;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SeedResolverBoreTests
{
    private static MeshTopology BuildTopo((Vector3d[] v, int[] i) mesh)
        => new MeshTopologyCache()
            .GetOrBuild(2, 1, mesh.v, mesh.i, 28.0 * System.Math.PI / 180.0);

    [Fact]
    public void RayIntoBore_ReturnsNearInwardWall_NotFarWall()
    {
        // Plate 6x6, thickness 1, with a hole radius 1 (interior wall normals point INWARD).
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 1.0, plate: 6.0, thickness: 1.0, segments: 48);
        MeshTopology topo = BuildTopo(mesh);

        // Shoot across the bore at mid-thickness: from +X outside toward -X.
        // The bore spans x in [-1, +1]; near wall is +X (x ~ +1), far wall is -X (x ~ -1).
        Vector3d origin = new(5.0, 0.0, 0.5);
        Vector3d dir = new Vector3d(-1.0, 0.0, 0.0);

        var hits = SeedResolver.CollectFrontHitsForTest(topo, origin, dir, apertureRadiusLocal: 0.05);

        Assert.NotEmpty(hits);
        var nearest = hits.OrderBy(h => h.T).First();
        // Near inward wall: hit x close to +1, strictly positive — NOT the far -1 wall.
        Assert.Equal(1.0, nearest.HitLocal.X, 1);
        Assert.True(nearest.HitLocal.X > 0.0,
            $"must return near inward wall (x~+1), got x={nearest.HitLocal.X}");
    }

    [Fact]
    public void RayIntoBore_DoesNotReturnTheFrontPlateFace()
    {
        // A ray straight down -Z into the bore mouth should still resolve the bore wall
        // band (radial ~ 1) once the seed is resolved, not the flat plate top.
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 1.0, plate: 6.0, thickness: 1.0, segments: 48);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(0.9, 0.0, 5.0);              // just inside the rim, above
        Vector3d dir = new Vector3d(0.0, 0.0, -1.0);

        var hits = SeedResolver.CollectFrontHitsForTest(topo, origin, dir, apertureRadiusLocal: 0.2);
        Assert.NotEmpty(hits);
        // At least one recovered hit is on the cylindrical bore wall (radial ~ 1).
        bool onWall = hits.Any(h =>
            System.Math.Abs(new Vector3d(h.HitLocal.X, h.HitLocal.Y, 0.0).Length - 1.0) < 0.15);
        Assert.True(onWall, "aperture must recover the inward bore wall band");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverBoreTests"`
  Expected failure: `RayIntoBore_ReturnsNearInwardWall_NotFarWall` **asserts wrong value** — C.2 skips the near inward wall (`det < 0`) and returns the far wall, so `nearest.HitLocal.X ≈ -1.0` (assert expects `+1.0`).

- [ ] **Step 3: Implement** inward-wall acceptance. Replace the `if (det <= 0.0) continue;` cull in `CollectFrontHits` with a normal-aware test that accepts a near inward wall.

```csharp
// in CollectFrontHits, replace the body of the per-triangle loop's culling:

            for (int tri = 0; tri < triCount; tri++)
            {
                int i0 = idx[3 * tri], i1 = idx[3 * tri + 1], i2 = idx[3 * tri + 2];
                Vector3d a = verts[i0];
                Vector3d b = verts[i1];
                Vector3d c = verts[i2];
                if (!IntersectTriangle(originLocal, rd, a, b, c, out double t, out double det))
                    continue;

                // Front-face policy:
                //  - Exterior surface (CCW outward): accept det > 0 (normal opposes view dir).
                //  - Interior bore wall (normal points inward, toward the ray as it crosses
                //    the bore): det < 0, but the triangle normal still FACES the ray origin
                //    (dot(triNormal, origin - hit) > 0). Accept it as a near-wall candidate.
                // Reject only true far-side back faces whose normal points AWAY from the origin.
                Vector3d triNormal = topo.Triangles[tri].Normal;
                Vector3d hit = originLocal + rd * t;
                bool normalFacesOrigin = Vector3d.Dot(triNormal, originLocal - hit) > 0.0;
                if (!normalFacesOrigin)
                    continue; // genuine far/back face — skip

                if (t < bestT)
                {
                    bestT = t;
                    bestTri = tri;
                    bestHit = hit;
                    bestDet = det;
                }
            }
```

  > Why this is correct for both cases: the **nearest** triangle whose normal faces the viewer is, for an exterior wall, the outward-facing front face (det>0); for an interior bore it is the near inward wall (det<0 but its inward normal still points back toward the external ray origin). The far bore wall's inward normal points *away* from the origin → rejected. We keep `bestDet` so C.4/C.5 can still tell exterior (det>0) from bore (det<0).

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverBoreTests"`
  Then re-run C.2 to confirm no regression:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverConeCastTests"`
  Expected: bore = 2 passed; cone-cast = 2 passed.

- [ ] **Step 5: Commit (PARENT repo)**
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add `
  "src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs" `
  "src/FabricationAssistant.App.Tests/Measurement/SeedResolverBoreTests.cs"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
fix(measure): SeedResolver returns near inward bore wall, not far wall

Group C task C.3. Front-face policy now accepts a triangle whose normal faces the
ray origin (covers both exterior CCW front faces and interior inward bore walls)
and rejects only genuine far/back faces. A ray crossing a bore now seeds the near
wall (x~+1), not the far wall (x~-1).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task C.4: Curvature-biased ranking + assemble the canonical `CircularSeed`

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SeedResolverRankingTests.cs`

Now rank the front candidates by a **curvature signature** so a tap a few px off a fillet that grazes both the curved band and an adjacent planar face prefers the **curved** triangle, and classify the chosen triangle's `LocalKind` from its `TriCurvature (K1,K2)`. Then fill in the real `Resolve` to return the canonical `CircularSeed`. Ranking score = curvature preference, tie-broken by nearest `t`.

Curvature → kind mapping (from `TriCurvature.K1,K2`, with `kEps` a small absolute curvature threshold scaled by mesh extent):
- both `|K|` small → `Planar` (score 0, deprioritized)
- one small, one finite → `Cylinder` (score 2)
- both finite, same sign → `Sphere`/`Torus`-like; for seeding we score curved bands highest (score 3); kind reported as `Torus` (rolling-ball fillets) when both finite.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.App.Tests.Measurement;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SeedResolverRankingTests
{
    private static MeshTopology BuildTopo((Vector3d[] v, int[] i) mesh)
        => new MeshTopologyCache()
            .GetOrBuild(3, 1, mesh.v, mesh.i, 28.0 * System.Math.PI / 180.0);

    [Fact]
    public void Resolve_RayAFewPxOffFillet_SeedsTheCurvedBand_NotAdjacentPlane()
    {
        // Quarter-round fillet strip joining two planes; curved band in the middle.
        var mesh = CircularMeshFixtures.FilletStraight(
            radius: 0.5, length: 4.0, arcSegments: 16, sweepRadians: System.Math.PI * 0.5);
        MeshTopology topo = BuildTopo(mesh);

        // Aim at the SEAM where the fillet meets the adjacent flat face, a few "px" off,
        // so the aperture cone straddles the curved band and the plane.
        Vector3d origin = new(2.0, 2.0, 2.0);
        Vector3d dir = (new Vector3d(-1.0, -1.0, 0.0)).Normalized();

        CircularSeed seed = SeedResolver.Resolve(topo, origin, dir, apertureRadiusLocal: 0.30);

        Assert.True(seed.Ok);
        // The chosen seed triangle must be on the curved band (Cylinder/Torus), never Planar.
        Assert.True(seed.LocalKind == SurfaceKind.Cylinder || seed.LocalKind == SurfaceKind.Torus,
            $"expected curved seed, got {seed.LocalKind}");
        // And its curvature must be genuinely non-planar.
        var crv = topo.Curvatures[seed.TriangleIndex];
        double kMax = System.Math.Max(System.Math.Abs(crv.K1), System.Math.Abs(crv.K2));
        Assert.True(kMax > 0.5, $"seed must sit on a curved facet, kMax={kMax}"); // ~1/R = 2
    }

    [Fact]
    public void Resolve_FlatPlateOnly_ReturnsPlanarSeed()
    {
        var mesh = CircularMeshFixtures.FlatPlate(size: 4.0);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(0.0, 0.0, 5.0);
        Vector3d dir = new Vector3d(0.0, 0.0, -1.0);

        CircularSeed seed = SeedResolver.Resolve(topo, origin, dir, apertureRadiusLocal: 0.2);

        Assert.True(seed.Ok);
        Assert.Equal(SurfaceKind.Planar, seed.LocalKind);
    }

    [Fact]
    public void Resolve_NoHit_ReturnsNotOk()
    {
        var mesh = CircularMeshFixtures.FlatPlate(size: 4.0);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(0.0, 0.0, 5.0);
        Vector3d dir = new Vector3d(0.0, 0.0, 1.0);        // pointing AWAY from the plate

        CircularSeed seed = SeedResolver.Resolve(topo, origin, dir, apertureRadiusLocal: 0.2);

        Assert.False(seed.Ok);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverRankingTests"`
  Expected failure: `Resolve` returns `default(CircularSeed)` (`Ok == false`) for all three — the first two assert `Ok == true` and a specific kind.

- [ ] **Step 3: Implement** curvature classification, candidate ranking, and the real `Resolve`.

```csharp
// add to SeedResolver.cs

    /// <summary>
    /// Map a triangle's principal curvatures to a coarse surface kind for seeding.
    /// kEps is an absolute curvature floor (1/length) below which a direction is "flat".
    /// </summary>
    internal static SurfaceKind ClassifyKind(TriCurvature crv, double kEps)
    {
        double k1 = System.Math.Abs(crv.K1);
        double k2 = System.Math.Abs(crv.K2);
        bool f1 = k1 > kEps;
        bool f2 = k2 > kEps;
        if (!f1 && !f2) return SurfaceKind.Planar;
        if (f1 ^ f2) return SurfaceKind.Cylinder;   // one finite, one flat
        return SurfaceKind.Torus;                    // both finite -> curved band (rolling-ball fillet)
    }

    /// <summary>Ranking weight: curved bands beat planar; bore/curved beat flat ties.</summary>
    private static int KindScore(SurfaceKind k) => k switch
    {
        SurfaceKind.Torus => 3,
        SurfaceKind.Sphere => 3,
        SurfaceKind.Cylinder => 2,
        SurfaceKind.Cone => 2,
        _ => 0, // Planar / Unknown
    };

    /// <summary>Absolute curvature threshold scaled by inverse mesh extent (≈ a few % of 1/R typical).</summary>
    private static double CurvatureEps(MeshTopology topo)
    {
        // Mesh extent from vertex AABB diagonal; kEps = 1 / (extent * 50) keeps planar facets near 0.
        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (Vector3d v in topo.Vertices)
        {
            min = new Vector3d(System.Math.Min(min.X, v.X), System.Math.Min(min.Y, v.Y), System.Math.Min(min.Z, v.Z));
            max = new Vector3d(System.Math.Max(max.X, v.X), System.Math.Max(max.Y, v.Y), System.Math.Max(max.Z, v.Z));
        }
        double extent = (max - min).Length;
        return extent > RayEps ? 1.0 / (extent * 50.0) : 1e-6;
    }
```

  Then replace the `Resolve` stub body:

```csharp
    public static CircularSeed Resolve(
        MeshTopology topo,
        Vector3d rayOriginLocal,
        Vector3d rayDirLocal,
        double apertureRadiusLocal)
    {
        List<Candidate> hits = CollectFrontHits(topo, rayOriginLocal, rayDirLocal, apertureRadiusLocal);
        if (hits.Count == 0)
            return new CircularSeed(-1, default, SurfaceKind.Unknown, Ok: false);

        double kEps = CurvatureEps(topo);

        Candidate best = default;
        SurfaceKind bestKind = SurfaceKind.Unknown;
        int bestScore = int.MinValue;
        double bestT = double.PositiveInfinity;

        foreach (Candidate h in hits)
        {
            SurfaceKind kind = ClassifyKind(topo.Curvatures[h.TriIndex], kEps);
            int score = KindScore(kind);
            // Higher curvature score wins; tie -> nearest along the ray.
            if (score > bestScore || (score == bestScore && h.T < bestT))
            {
                bestScore = score;
                bestT = h.T;
                best = h;
                bestKind = kind;
            }
        }

        return new CircularSeed(best.TriIndex, best.HitLocal, bestKind, Ok: true);
    }
```

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverRankingTests"`
  Then the full Group C suite:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolver"`
  Expected: ranking = 3 passed; full SeedResolver suite all green.

- [ ] **Step 5: Commit (PARENT repo)**
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add `
  "src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs" `
  "src/FabricationAssistant.App.Tests/Measurement/SeedResolverRankingTests.cs"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): SeedResolver curvature-biased ranking returns canonical CircularSeed

Group C task C.4. Ranks front hits by curvature signature (Torus/Cylinder over
Planar, tie-broken by nearest t), classifies LocalKind from TriCurvature, and
returns the canonical CircularSeed. A ray a few px off a fillet now seeds the
curved band, not the adjacent plane.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task C.5: Document the GPU-pick integration point (re-validated hint, NOT an override)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SeedResolverGpuHintTests.cs`

GPU id-buffer picking lives in the Android renderer and is **out of scope** for the kernel, but the design (§5, §6.3) is explicit: the GPU pick is a *re-validated hint*, never an override of nearest-front ordering. We make that contract executable and self-documenting by adding an overload that accepts an optional `gpuHintTriangle` and (a) uses it **only** to break ties among equally-ranked, equally-near candidates, and (b) is **ignored** when the hint is not among the front candidates. This pins the integration point and prevents the legacy `preferredNode` short-circuit (diagnosis §4.1) from creeping back. No renderer code is touched.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.App.Tests.Measurement;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SeedResolverGpuHintTests
{
    private static MeshTopology BuildTopo((Vector3d[] v, int[] i) mesh)
        => new MeshTopologyCache()
            .GetOrBuild(4, 1, mesh.v, mesh.i, 28.0 * System.Math.PI / 180.0);

    [Fact]
    public void Resolve_GpuHintNotAmongFrontHits_IsIgnored()
    {
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 4.0, segments: 48, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(5.0, 0.0, 2.0);
        Vector3d dir = new Vector3d(-1.0, 0.0, 0.0);

        // A bogus hint (a far-side / unrelated triangle index) must NOT override nearest-front.
        int bogusHint = (topo.Indices.Count / 3) - 1; // some far triangle on the back wall
        CircularSeed withHint = SeedResolver.Resolve(topo, origin, dir, 0.12, gpuHintTriangle: bogusHint);
        CircularSeed noHint   = SeedResolver.Resolve(topo, origin, dir, 0.12);

        // Hint that is not a valid front candidate is ignored: identical result.
        Assert.Equal(noHint.TriangleIndex, withHint.TriangleIndex);
        Assert.True(withHint.HitLocal.X > 0.0); // still the near +X wall
    }

    [Fact]
    public void Resolve_GpuHintAmongEqualRankCandidates_BreaksTieTowardHint()
    {
        // Full cylinder: many wall triangles share the same curvature score. Pick one of the
        // actual front candidates as the hint; the resolver must honour it as a tie-break.
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 4.0, segments: 48, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(5.0, 0.0, 2.0);
        Vector3d dir = new Vector3d(-1.0, 0.0, 0.0);

        var frontTris = SeedResolver.CollectFrontHitsForTest(topo, origin, dir, 0.30)
            .Select(h => h.TriIndex).Distinct().ToList();
        Assert.True(frontTris.Count >= 2, "need >=2 front candidates to test the tie-break");

        int hint = frontTris.Last(); // a valid-but-not-default front candidate
        CircularSeed seed = SeedResolver.Resolve(topo, origin, dir, 0.30, gpuHintTriangle: hint);

        Assert.True(seed.Ok);
        Assert.Equal(hint, seed.TriangleIndex); // honoured because it is a real front candidate
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverGpuHintTests"`
  Expected failure: **compile error** — the `Resolve(..., int gpuHintTriangle)` overload does not exist (`CS1739`/`CS1501`).

- [ ] **Step 3: Implement** the documented hint overload. Keep the canonical `Resolve` (no hint) as the public contract; route it through the overload with `gpuHintTriangle = -1`.

```csharp
// replace the existing Resolve(...) with these two methods in SeedResolver.cs

    /// <summary>
    /// Canonical contract entry point (no GPU hint). See the 4-arg signature in the design.
    /// </summary>
    public static CircularSeed Resolve(
        MeshTopology topo,
        Vector3d rayOriginLocal,
        Vector3d rayDirLocal,
        double apertureRadiusLocal)
        => Resolve(topo, rayOriginLocal, rayDirLocal, apertureRadiusLocal, gpuHintTriangle: -1);

    /// <summary>
    /// INTEGRATION POINT for the Android GPU id-buffer pick. The hint is a RE-VALIDATED
    /// hint, never an override (design §5, §6.3): it only breaks ties among equally-ranked,
    /// equally-near front candidates, and is ignored entirely if it is not itself a front
    /// candidate under the aperture cone. The Android renderer (out of scope here) passes
    /// the id-buffer triangle as <paramref name="gpuHintTriangle"/>; CPU nearest-front
    /// ordering still decides correctness. This replaces the legacy preferredNode
    /// short-circuit (diagnosis §4.1).
    /// </summary>
    public static CircularSeed Resolve(
        MeshTopology topo,
        Vector3d rayOriginLocal,
        Vector3d rayDirLocal,
        double apertureRadiusLocal,
        int gpuHintTriangle)
    {
        List<Candidate> hits = CollectFrontHits(topo, rayOriginLocal, rayDirLocal, apertureRadiusLocal);
        if (hits.Count == 0)
            return new CircularSeed(-1, default, SurfaceKind.Unknown, Ok: false);

        double kEps = CurvatureEps(topo);

        Candidate best = default;
        SurfaceKind bestKind = SurfaceKind.Unknown;
        int bestScore = int.MinValue;
        double bestT = double.PositiveInfinity;
        bool bestIsHint = false;

        foreach (Candidate h in hits)
        {
            SurfaceKind kind = ClassifyKind(topo.Curvatures[h.TriIndex], kEps);
            int score = KindScore(kind);
            bool isHint = gpuHintTriangle >= 0 && h.TriIndex == gpuHintTriangle;

            // Primary: curvature score. Secondary: nearest t. Tertiary (re-validated
            // hint): only among equal score AND not-farther, prefer the GPU-hinted tri.
            bool better =
                score > bestScore
                || (score == bestScore && h.T < bestT - RayEps)
                || (score == bestScore && System.Math.Abs(h.T - bestT) <= RayEps && isHint && !bestIsHint);

            if (better)
            {
                bestScore = score;
                bestT = h.T;
                best = h;
                bestKind = kind;
                bestIsHint = isHint;
            }
        }

        return new CircularSeed(best.TriIndex, best.HitLocal, bestKind, Ok: true);
    }
```

  > **Tie-break note for the test:** the second test passes `apertureRadiusLocal: 0.30` so several wall triangles are equal-rank front candidates at (near-)equal `t`; the hinted triangle wins the tie. The first test's bogus hint is not a front candidate at all, so `isHint` is never true and the result equals the no-hint path. If the chosen fixture's tessellation makes the hinted candidate strictly farther than another, widen the tie tolerance via the `RayEps` band or pick `frontTris.First()` — but do **not** let the hint override a strictly-nearer candidate; that is the whole point of "hint, not override."

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolverGpuHintTests"`
  Then full Group C regression:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SeedResolver"`
  Expected: GPU-hint = 2 passed; full SeedResolver suite all green.

- [ ] **Step 5: Commit (PARENT repo)**
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add `
  "src/FabricationAssistant.Core/Measurement/Engine/SeedResolver.cs" `
  "src/FabricationAssistant.App.Tests/Measurement/SeedResolverGpuHintTests.cs"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): SeedResolver GPU-pick integration point as re-validated hint

Group C task C.5. Adds a documented Resolve overload taking gpuHintTriangle that
only tie-breaks among equal-rank, equal-near front candidates and is ignored when
not a front candidate. Pins the Android GPU id-buffer integration point and bars
the legacy preferredNode override. Renderer code untouched (out of scope).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task C.6: Android mirror test — fillet-off-seed + bore-near-wall on the device suite

**Files:**
- Modify (if needed) `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Test `[ANDROID] src/FabricationAssistant.App.Android.Tests/SeedResolverAndroidMirrorTests.cs`

The Android test project links Core **selectively**; `SeedResolver.cs` (and its `MeshTopology`/fixtures dependencies) may not be linked. This task mirrors the two spec-named scenarios — "a ray a few px off the fillet still returns a seed on the curved band" and "a ray into a bore returns the near inward wall" — in the Android suite (design §10 "Android mirror", success criterion 5), adding explicit `<Compile Include>` entries for any Core types that are not yet wildcard-linked.

- [ ] **Step 1: Write the failing test** (Android suite)

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.App.Tests.Measurement; // CircularMeshFixtures (if linked) — see Step 3
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class SeedResolverAndroidMirrorTests
{
    private static MeshTopology BuildTopo((Vector3d[] v, int[] i) mesh)
        => new MeshTopologyCache()
            .GetOrBuild(10, 1, mesh.v, mesh.i, 28.0 * System.Math.PI / 180.0);

    [Fact]
    public void Resolve_RayAFewPxOffFillet_SeedsCurvedBand()
    {
        var mesh = CircularMeshFixtures.FilletStraight(
            radius: 0.5, length: 4.0, arcSegments: 16, sweepRadians: System.Math.PI * 0.5);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(2.0, 2.0, 2.0);
        Vector3d dir = (new Vector3d(-1.0, -1.0, 0.0)).Normalized();

        CircularSeed seed = SeedResolver.Resolve(topo, origin, dir, apertureRadiusLocal: 0.30);

        Assert.True(seed.Ok);
        Assert.True(seed.LocalKind == SurfaceKind.Cylinder || seed.LocalKind == SurfaceKind.Torus);
    }

    [Fact]
    public void Resolve_RayIntoBore_SeedsNearInwardWall()
    {
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 1.0, plate: 6.0, thickness: 1.0, segments: 48);
        MeshTopology topo = BuildTopo(mesh);

        Vector3d origin = new(5.0, 0.0, 0.5);
        Vector3d dir = new Vector3d(-1.0, 0.0, 0.0);

        CircularSeed seed = SeedResolver.Resolve(topo, origin, dir, apertureRadiusLocal: 0.05);

        Assert.True(seed.Ok);
        Assert.True(seed.HitLocal.X > 0.0,
            $"Android mirror: expected near inward wall (x~+1), got x={seed.HitLocal.X}");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
  Expected failure: **compile error** — `SeedResolver`, `MeshTopology`/`MeshTopologyCache`, `CircularSeed`, `SurfaceKind`, and/or `CircularMeshFixtures` are not linked into the Android test project (`CS0246`/`CS0103`).

- [ ] **Step 3: Implement** — add the missing `<Compile Include>` links so the Android project sees the Core types under test plus the shared fixtures. Open the csproj and add, inside an existing `<ItemGroup>` of `<Compile Include>` links (the project already uses explicit links for selected Core files), the following — using the project's relative path convention back to the PARENT src tree (typically `..\..\..\src\...`; match the existing entries' prefix exactly):

```xml
<ItemGroup>
  <!-- Group C: SeedResolver + its Core dependencies (selective link; wildcard does not cover the Android test project) -->
  <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\SeedResolver.cs"
           Link="Linked\Measurement\Engine\SeedResolver.cs" />
  <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopologyCache.cs"
           Link="Linked\Measurement\Engine\MeshTopologyCache.cs" />
  <!-- Shared host fixtures (Group A). If the file already links elsewhere, drop this entry. -->
  <Compile Include="..\..\..\src\FabricationAssistant.App.Tests\Measurement\CircularMeshFixtures.cs"
           Link="Linked\Measurement\CircularMeshFixtures.cs" />
</ItemGroup>
```

  > **How to set the exact path/prefix:** open `src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`, find an existing `<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\...\*.cs" Link="..."/>` entry, and copy its `..\..\..` depth and `Link=` style verbatim. If `MeshTopologyCache` / `SeedResolver` are **already** covered by an existing wildcard `<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\**\*.cs" />` in this csproj, do **not** add a duplicate (it will raise `MSB3105` duplicate-item) — only add what is missing. Likewise only add `CircularMeshFixtures.cs` if it is not already linked. (Per project conventions, adding a `<Compile Include>` for a needed Core type in the Android test csproj is the sanctioned step.)

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~SeedResolverAndroidMirrorTests"`
  Expected: 2 passed. (On `CS2012`/file-lock: `dotnet build-server shutdown`, retry; if a `.NET Host (PID)` holds a DLL, `Stop-Process -Id <pid> -Force`.)

- [ ] **Step 5: Commit (ANDROID repo)**
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  "src/FabricationAssistant.App.Android.Tests/SeedResolverAndroidMirrorTests.cs" `
  "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
test(measure): Android mirror for SeedResolver fillet-off-seed + bore near-wall

Group C task C.6. Mirrors the two spec-named SeedResolver scenarios in the Android
suite and links the required Core source (SeedResolver, MeshTopologyCache) plus the
shared CircularMeshFixtures into the selectively-linked Android test project.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

> **Group C exit criteria:** `SeedResolver.Resolve` (both overloads) returns a curvature-correct `CircularSeed` for fillets (curved band, off-by-a-few-px tolerant), bores (near inward wall), and flat plates (planar); the GPU pick is wired as a documented re-validated hint with no override path; host suite `~SeedResolver` is fully green and the Android mirror passes. Downstream groups consume `CircularSeed` via the canonical contract; no Group C type other than `SeedResolver`/`CircularSeed` crosses a boundary.

---

Note: `Length` and `LengthSquared` are properties, not methods (the contract spec lists `.Length()`/`.LengthSquared()` but the real API uses properties). I'll use the real API in code. Now I have everything I need to write the Group D section.

## Group D — SurfaceRegionGrower (curvature-continuous, crease-bounded, fit-deviation BFS)

> **Spec:** §6.5. This group implements `SurfaceRegionGrower.Grow`, the BFS that replaces `GrowSmoothRegion`'s 35°-parent flood. It consumes Group B's `MeshTopology` (welded, classified edges, per-tri data, curvature) and Group E's `AnalyticSurfaceFitter` to maintain a *running* cylinder fit, and it accepts a candidate triangle only when it (a) does not cross a crease/boundary/non-manifold edge, (b) faces the same way as the running fit predicts (signed `dot ≥ +cos(NormalTol)`, **never `abs`**), and (c) sits within `MembershipChordK · chord(R)` radial residual of the *running* fit (not the BFS parent). The result is one connected component, seed-independent, capped at `MaxTriangles`.

**Canonical contract this group owns and must match exactly:**
```csharp
public sealed record RegionGrowOptions(double NormalTolRad, double MembershipChordK, int MaxTriangles);
public static class SurfaceRegionGrower {
    public static List<int> Grow(MeshTopology topo, int seedTriangle, RegionGrowOptions opts, out SurfaceFit runningFit);
}
```

**Cross-group dependencies (already delivered before Group D starts):**
- Group B: `MeshTopology` (incl. `SmoothNeighbors(int)`, `CanGrowAcross(int,int)`, `Triangles[i].Normal/.Centroid/.Area/.Valid`, `Curvatures[i].K1/.K2`), `IMeshTopologyProvider`, `MeshTopologyCache`.
- Group E: `SurfaceFit`, `SurfaceKind`, `AnalyticSurfaceFitter.FitCylinder(points, normals, axisSeed)`.
- Group A: `CircularMeshFixtures` (`FilletStraight`, `Cylinder`, `FlatPlate`, `SeedTriangleAt`) and `GeoAssert` in `FabricationAssistant.App.Tests.Measurement`.

> **Build-lock note (applies to every task below):** if `dotnet build`/`dotnet test` fails with **CS2012** or an `XARLP7024` file-lock, run `dotnet build-server shutdown` and retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`, then retry. Do this once when it happens — it is not repeated per task.

> **API reminder:** `Vector3d.Length` and `Vector3d.LengthSquared` are **properties** (not methods); use `.Length`, not `.Length()`. `Vector3d` exposes `Dot`, `Cross`, `Normalized()`, `BuildPerpendicular(n)`, operators `+ - * /`, and `UnitX/Y/Z`.

> All new production code lives under `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/**` (auto compile-linked into Android via wildcard — no csproj edit). All host tests live under `[PARENT] src/FabricationAssistant.App.Tests/Measurement/**`. Commit Core + host tests in the **PARENT** repo. The Android mirror test (Task D.6) is committed in the **ANDROID** repo and may require an explicit `<Compile Include>` in the Android test csproj.

---

### Task D.1: Introduce `RegionGrowOptions` and a stub `SurfaceRegionGrower.Grow` that compiles and returns just the seed

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/RegionGrowOptions.cs`
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`

- [ ] **Step 1: Write the failing test.** Create the test file with the first fact: a one-triangle seed result and a defaulted options record. This pins the signature and the `out SurfaceFit` shape before any real growth logic exists.

```csharp
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.App.Tests.Measurement;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SurfaceRegionGrowerTests
{
    // 28° crease barrier; 12° same-facing tol; k=2.0 chord band; generous cap.
    private static RegionGrowOptions DefaultOpts() => new(
        NormalTolRad: 12.0 * System.Math.PI / 180.0,
        MembershipChordK: 2.0,
        MaxTriangles: 4096);

    private static MeshTopology BuildTopo((Vector3d[] v, int[] i) mesh)
    {
        var provider = new MeshTopologyCache(capacity: 4);
        return provider.GetOrBuild(
            meshKey: 1,
            transformVersion: 1,
            verticesLocal: mesh.v,
            indices: mesh.i,
            creaseAngleRad: 28.0 * System.Math.PI / 180.0);
    }

    [Fact]
    public void Grow_ReturnsAtLeastTheSeedTriangle_AndNeverNull()
    {
        (Vector3d[] v, int[] i) mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 32,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(2.0, 0.0, 2.5));

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out SurfaceFit fit);

        Assert.NotNull(region);
        Assert.Contains(seed, region);
        Assert.Equal(region.Count, region.Distinct().Count()); // no duplicates
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (run from PARENT root). Expected failure: **compile error CS0246** — `RegionGrowOptions`/`SurfaceRegionGrower` do not exist yet.

- [ ] **Step 3: Implement** the record and a minimal stub that returns only the seed.

`RegionGrowOptions.cs`:
```csharp
namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Tuning for <see cref="SurfaceRegionGrower"/>. Defaults (spec §9):
/// NormalTolRad = 12°, MembershipChordK = 2.0, MaxTriangles caps the flood.
/// </summary>
public sealed record RegionGrowOptions(double NormalTolRad, double MembershipChordK, int MaxTriangles);
```

`SurfaceRegionGrower.cs`:
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Curvature-continuous, same-facing, crease/boundary-bounded BFS that grows a
/// triangle region against a maintained running cylinder fit (spec §6.5).
/// Replaces GrowSmoothRegion's 35° parent-relative flood.
/// </summary>
public static class SurfaceRegionGrower
{
    public static List<int> Grow(
        MeshTopology topo,
        int seedTriangle,
        RegionGrowOptions opts,
        out SurfaceFit runningFit)
    {
        ArgumentNullException.ThrowIfNull(topo);
        ArgumentNullException.ThrowIfNull(opts);

        runningFit = default;
        var region = new List<int>();
        if (seedTriangle < 0 || seedTriangle >= topo.Triangles.Count)
            return region;
        if (!topo.Triangles[seedTriangle].Valid)
            return region;

        region.Add(seedTriangle);
        return region;
    }
}
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). The single fact passes.

- [ ] **Step 5: Commit** in the **PARENT** repo:
```bash
git add src/FabricationAssistant.Core/Measurement/Engine/RegionGrowOptions.cs src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs
git commit -m "feat(measure): scaffold SurfaceRegionGrower + RegionGrowOptions (seed-only stub)"
```

---

### Task D.2: Grow across smooth edges only; never cross a crease/boundary (anti-leak: FilletStraight + adjacent plane)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`

- [ ] **Step 1: Write the failing test.** Two facts: (a) on a full cylinder the region grows well past the seed (BFS actually traverses smooth edges); (b) **anti-leak** — on `FilletStraight` plus its two adjacent planes, every returned triangle is a fillet triangle and **no plane triangle** is included. Build a tagged fillet+plane mesh by concatenating the fixture quarter-round with two flat plates that meet it tangentially, and record the triangle-index ranges so we can assert no leak.

```csharp
    // Helper: build a quarter-round fillet between two tangent planes and return
    // the mesh plus the [start,end) triangle range that belongs to the fillet.
    // The fillet is a 90° quarter cylinder of radius R about the Z axis, from
    // angle 0 (normal +X) to angle 90° (normal +Y). A plane tangent at angle 0
    // extends in -Y at x=R (its outward normal is +X, matching the fillet's seam
    // normal so the seam bend is ~0° at that edge UNLESS we keep the planes set
    // back); to force a real crease we attach the planes at the fillet's flank so
    // the dihedral across the seam exceeds the 28° crease threshold.
    private static (Vector3d[] v, int[] i, int filletStart, int filletEnd) BuildFilletWithPlanes(
        double radius, double length, int arcSegments, int lengthSegments)
    {
        var v = new List<Vector3d>();
        var idx = new List<int>();

        int Add(Vector3d p) { v.Add(p); return v.Count - 1; }
        void Quad(int a, int b, int c, int d)
        { idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(a); idx.Add(c); idx.Add(d); }

        // Fillet: quarter cylinder, angle 0..90°, radius R, length along Z.
        int[,] f = new int[arcSegments + 1, lengthSegments + 1];
        for (int a = 0; a <= arcSegments; a++)
        {
            double ang = a * (System.Math.PI * 0.5) / arcSegments;
            double x = System.Math.Cos(ang) * radius;
            double y = System.Math.Sin(ang) * radius;
            for (int z = 0; z <= lengthSegments; z++)
                f[a, z] = Add(new Vector3d(x, y, z * length / lengthSegments));
        }
        int filletStart = idx.Count / 3;
        for (int a = 0; a < arcSegments; a++)
        for (int z = 0; z < lengthSegments; z++)
            Quad(f[a, z], f[a + 1, z], f[a + 1, z + 1], f[a, z + 1]);
        int filletEnd = idx.Count / 3;

        // Plane A: tangent at angle 0 (point (R,0)), extends in -Y, lies in the
        // plane x=R. Its normal is +X. Across the fillet's first edge the surface
        // continues smoothly (tangent), so to GUARANTEE a crease for the anti-leak
        // test we instead drop the plane vertically: a face in y=0 plane (normal
        // +Y... ) — simplest robust choice: make Plane A perpendicular to the
        // fillet flank so the dihedral is 90°.
        // Plane A: the y=0 plane wall going outward in +X beyond x=R? No — share
        // the fillet's a=0 edge (x=R, y=0 column) and extend in +X (a face in the
        // y=0 plane, normal -Y). Dihedral fillet-vs-planeA at the seam ≈ 90°.
        int[,] pa = new int[2, lengthSegments + 1];
        double planeSpan = radius * 2.0;
        for (int z = 0; z <= lengthSegments; z++)
        {
            pa[0, z] = f[0, z];                                   // shared seam (x=R, y=0)
            pa[1, z] = Add(new Vector3d(radius + planeSpan, 0.0, z * length / lengthSegments));
        }
        for (int z = 0; z < lengthSegments; z++)
            Quad(pa[0, z], pa[1, z], pa[1, z + 1], pa[0, z + 1]);

        // Plane B: share the fillet's a=arcSegments edge (x=0, y=R column) and
        // extend in +Y (a face in the x=0 plane, normal -X). Dihedral ≈ 90°.
        int[,] pb = new int[2, lengthSegments + 1];
        for (int z = 0; z <= lengthSegments; z++)
        {
            pb[0, z] = f[arcSegments, z];                          // shared seam (x=0, y=R)
            pb[1, z] = Add(new Vector3d(0.0, radius + planeSpan, z * length / lengthSegments));
        }
        for (int z = 0; z < lengthSegments; z++)
            Quad(pb[0, z], pb[0, z + 1], pb[1, z + 1], pb[1, z]);

        return (v.ToArray(), idx.ToArray(), filletStart, filletEnd);
    }

    [Fact]
    public void Grow_FullCylinder_GrowsManyTriangles_NotJustSeed()
    {
        (Vector3d[] v, int[] i) mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 32,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(2.0, 0.0, 2.5));

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out _);

        // 32 segments × 2 tris = 64 tris on the wall; expect the great majority.
        Assert.True(region.Count >= 48, $"expected wide growth, got {region.Count}");
    }

    [Fact]
    public void Grow_FilletAdjacentToPlanes_NeverIncludesPlaneTriangles()
    {
        var (v, i, filletStart, filletEnd) = BuildFilletWithPlanes(
            radius: 2.0, length: 5.0, arcSegments: 16, lengthSegments: 4);
        MeshTopology topo = BuildTopo((v, i));
        // Seed mid-fillet: angle 45°, mid-length.
        double a45 = System.Math.PI * 0.25;
        var seedPoint = new Vector3d(System.Math.Cos(a45) * 2.0, System.Math.Sin(a45) * 2.0, 2.5);
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), seedPoint);

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out _);

        Assert.All(region, t => Assert.InRange(t, filletStart, filletEnd - 1));
        // And it must actually cover the fillet band (not just the seed quad).
        Assert.True(region.Count >= 16, $"fillet under-grown: {region.Count}");
    }
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). Expected: `Grow_FullCylinder_GrowsManyTriangles_NotJustSeed` fails (`region.Count == 1`, the stub) — i.e. `expected wide growth, got 1`.

- [ ] **Step 3: Implement** the smooth-edge BFS gated by `CanGrowAcross`. This adds traversal but **no** fit/normal/curvature gating yet (those land in D.3–D.5); the crease barrier alone is what stops the leak onto the planes, because the fillet↔plane seams are creases (≈90° dihedral).

```csharp
    public static List<int> Grow(
        MeshTopology topo,
        int seedTriangle,
        RegionGrowOptions opts,
        out SurfaceFit runningFit)
    {
        ArgumentNullException.ThrowIfNull(topo);
        ArgumentNullException.ThrowIfNull(opts);

        runningFit = default;
        var region = new List<int>();
        int triCount = topo.Triangles.Count;
        if (seedTriangle < 0 || seedTriangle >= triCount)
            return region;
        if (!topo.Triangles[seedTriangle].Valid)
            return region;

        int cap = opts.MaxTriangles > 0 ? opts.MaxTriangles : int.MaxValue;
        var visited = new bool[triCount];
        var queue = new Queue<int>();

        visited[seedTriangle] = true;
        queue.Enqueue(seedTriangle);
        region.Add(seedTriangle);

        while (queue.Count > 0 && region.Count < cap)
        {
            int current = queue.Dequeue();
            foreach (int neighbor in topo.SmoothNeighbors(current))
            {
                if (neighbor < 0 || neighbor >= triCount || visited[neighbor])
                    continue;
                if (!topo.Triangles[neighbor].Valid)
                    continue;
                // Hard wall: only traverse smooth edges. SmoothNeighbors already
                // restricts to smooth edges, but assert it via CanGrowAcross so a
                // future SmoothNeighbors change cannot silently leak.
                if (!topo.CanGrowAcross(current, neighbor))
                    continue;

                visited[neighbor] = true;
                region.Add(neighbor);
                if (region.Count >= cap)
                    break;
                queue.Enqueue(neighbor);
            }
        }

        return region;
    }
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). All three facts pass; the anti-leak fact confirms no plane triangle is returned because crease seams block the BFS.

- [ ] **Step 5: Commit** in the **PARENT** repo:
```bash
git add src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs
git commit -m "feat(measure): SurfaceRegionGrower smooth-edge crease-bounded BFS (anti-leak)"
```

---

### Task D.3: Same-facing gate — signed `dot(n, n_fit) ≥ +cos(NormalTol)`, NEVER abs (thin-wall / antiparallel guard)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`

> This is the §4.1 `abs()` defect. The running fit gives an expected outward radial normal at each candidate centroid (`(centroid − axisPoint) projected off-axis, normalized`). A candidate joins only if its actual normal agrees in **sign** with the expected outward normal. Until Group E's fitter is wired in (D.5), we bootstrap the "expected outward normal" from the **seed triangle's own normal-vs-running-mean**, but the decisive sign test uses the running fit once available. For this task we test the sign rule directly with a back-to-back thin wall: two coaxial cylindrical sheets whose normals are antiparallel — growth from the outer sheet must not jump to the inner sheet even though they are geometrically coincident in radius.

- [ ] **Step 1: Write the failing test.** A thin-wall fixture: an outer cylinder (normals +radial) and an inner cylinder of the same radius minus a hair (normals −radial), sharing welded rim vertices at top/bottom so a naive `abs(dot)` BFS could bridge them. Assert growth from an outer-wall seed never includes an inner-wall triangle.

```csharp
    // Two coaxial cylindrical sheets at radius R and R-eps, wound so the outer
    // sheet's normals point +radial (outward) and the inner sheet's point
    // -radial (inward). They share top & bottom rim vertices (welded), creating
    // smooth-looking edges across which abs(dot) would bridge but +cos(dot) must
    // not (the seam normals are antiparallel).
    private static (Vector3d[] v, int[] i, int outerStart, int outerEnd, int innerStart, int innerEnd)
        BuildThinWall(double radius, double wall, double height, int segments)
    {
        var v = new List<Vector3d>();
        var idx = new List<int>();
        int Add(Vector3d p) { v.Add(p); return v.Count - 1; }

        double rO = radius;
        double rI = radius - wall;
        var outerB = new int[segments];
        var outerT = new int[segments];
        var innerB = new int[segments];
        var innerT = new int[segments];
        for (int s = 0; s < segments; s++)
        {
            double t = s * System.Math.Tau / segments;
            double c = System.Math.Cos(t), si = System.Math.Sin(t);
            outerB[s] = Add(new Vector3d(c * rO, si * rO, 0));
            outerT[s] = Add(new Vector3d(c * rO, si * rO, height));
            innerB[s] = Add(new Vector3d(c * rI, si * rI, 0));
            innerT[s] = Add(new Vector3d(c * rI, si * rI, height));
        }

        int outerStart = idx.Count / 3;
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            // Outer wall CCW so normals point outward (+radial).
            idx.Add(outerB[s]); idx.Add(outerB[n]); idx.Add(outerT[n]);
            idx.Add(outerB[s]); idx.Add(outerT[n]); idx.Add(outerT[s]);
        }
        int outerEnd = idx.Count / 3;

        int innerStart = idx.Count / 3;
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            // Inner wall reversed so normals point inward (-radial).
            idx.Add(innerB[s]); idx.Add(innerT[n]); idx.Add(innerB[n]);
            idx.Add(innerB[s]); idx.Add(innerT[s]); idx.Add(innerT[n]);
        }
        int innerEnd = idx.Count / 3;

        return (v.ToArray(), idx.ToArray(), outerStart, outerEnd, innerStart, innerEnd);
    }

    [Fact]
    public void Grow_ThinWall_DoesNotBridgeAntiparallelInnerWall()
    {
        var (v, i, outerStart, outerEnd, innerStart, innerEnd) =
            BuildThinWall(radius: 2.0, wall: 0.02, height: 5.0, segments: 48);
        MeshTopology topo = BuildTopo((v, i));
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(2.0, 0.0, 2.5));

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out _);

        Assert.InRange(seed, outerStart, outerEnd - 1); // seed really is on the outer wall
        Assert.All(region, t => Assert.False(
            t >= innerStart && t < innerEnd,
            $"region leaked onto antiparallel inner wall (tri {t})"));
        Assert.True(region.Count >= 32, $"outer wall under-grown: {region.Count}");
    }
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). Expected: if the welded rim shares vertices and the rim edges classify as smooth, the BFS bridges onto the inner wall and the `Assert.All(...inner...)` fails. (If the rim instead classifies as a crease, this test still drives the *implementation* of the sign gate as defense-in-depth; assert it fails first by temporarily widening creaseAngle — but normally the antiparallel normals at the welded seam produce a >90° dihedral classified as crease, so to force the failure we add the sign gate against the **running mean normal** and keep the test as the regression lock.) Document the observed failure reason in the commit if the rim was already a crease.

- [ ] **Step 3: Implement** the same-facing gate. Maintain a running **mean unit normal** of the accepted region as a cheap proxy for the running-fit orientation (replaced by the true running-fit radial normal in D.5). A candidate joins only if `dot(n_candidate, n_meanUnit) ≥ +cos(NormalTol)` — signed, never abs.

```csharp
    public static List<int> Grow(
        MeshTopology topo,
        int seedTriangle,
        RegionGrowOptions opts,
        out SurfaceFit runningFit)
    {
        ArgumentNullException.ThrowIfNull(topo);
        ArgumentNullException.ThrowIfNull(opts);

        runningFit = default;
        var region = new List<int>();
        int triCount = topo.Triangles.Count;
        if (seedTriangle < 0 || seedTriangle >= triCount)
            return region;
        if (!topo.Triangles[seedTriangle].Valid)
            return region;

        double cosNormalTol = System.Math.Cos(opts.NormalTolRad);
        int cap = opts.MaxTriangles > 0 ? opts.MaxTriangles : int.MaxValue;

        var visited = new bool[triCount];
        var queue = new Queue<int>();

        visited[seedTriangle] = true;
        queue.Enqueue(seedTriangle);
        region.Add(seedTriangle);

        // Running mean unit normal of the accepted region (sign-bearing).
        Vector3d normalSum = topo.Triangles[seedTriangle].Normal;
        Vector3d meanNormal = normalSum.Normalized();

        while (queue.Count > 0 && region.Count < cap)
        {
            int current = queue.Dequeue();
            foreach (int neighbor in topo.SmoothNeighbors(current))
            {
                if (neighbor < 0 || neighbor >= triCount || visited[neighbor])
                    continue;
                if (!topo.Triangles[neighbor].Valid)
                    continue;
                if (!topo.CanGrowAcross(current, neighbor))
                    continue;

                Vector3d nCand = topo.Triangles[neighbor].Normal;
                // SAME-FACING, signed. NEVER abs — antiparallel normals (thin
                // walls, opposite fillet flanks) must NOT merge (spec §4.1).
                if (Vector3d.Dot(nCand, meanNormal) < cosNormalTol)
                {
                    visited[neighbor] = true; // do not revisit a rejected face
                    continue;
                }

                visited[neighbor] = true;
                region.Add(neighbor);
                normalSum = normalSum + nCand;
                meanNormal = normalSum.Normalized();
                if (region.Count >= cap)
                    break;
                queue.Enqueue(neighbor);
            }
        }

        return region;
    }
```

> **Note on the running mean for curved surfaces:** a fillet/cylinder's normals fan across the band, so a *global* mean would reject the far side. The 12° tol is applied between a candidate and the current mean as the front advances locally — this is intentionally lenient here because the decisive curved-surface containment is the **radial-residual gate** added in D.5 against the running fit, not this orientation mean. This task's mean exists to kill the antiparallel/thin-wall sign defect, not to bound curvature. D.5 replaces `meanNormal` with the running-fit predicted outward radial normal evaluated *per candidate*, removing the fan-out limitation.

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). The thin-wall fact passes; the earlier full-cylinder and fillet-vs-planes facts still pass (their normals are locally co-facing within the front).

- [ ] **Step 5: Commit** in the **PARENT** repo:
```bash
git add src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs
git commit -m "feat(measure): SurfaceRegionGrower signed same-facing gate (thin-wall anti-leak)"
```

---

### Task D.4: Curvature-continuity gate — reject planar (k1≈k2≈0) candidates so growth stays on curved surface

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`

> Spec §6.5 condition 3: "principal curvature sign/magnitude consistent with the running surface model." A cylindrical/fillet surface has one principal curvature ≈ `1/R` (finite) and one ≈ 0; a plane has both ≈ 0. We reject a candidate whose larger-magnitude principal curvature is far below the running region's curvature scale — a second barrier that holds even when a fillet meets a plane at a sub-crease tangent bend (the §4.2 "tangent fillet→plane" case where the dihedral is under 28° and the crease wall does not fire).

- [ ] **Step 1: Write the failing test.** A **tangent** fillet→plane fixture where the seam dihedral is below the 28° crease angle (so D.2's crease wall does NOT stop it), but the plane is flat (k≈0). Assert no plane triangle is included. Build a fillet whose first/last facet meets the plane tangentially (continue the plane in the tangent direction of the arc endpoint).

```csharp
    // Quarter-round fillet (radius R) whose two ends are TANGENT-continued by
    // flat planes (so the seam dihedral ≈ one facet's turn < 28°, defeating the
    // crease wall). The planes are flat (curvature ≈ 0); only the curvature gate
    // can keep them out.
    private static (Vector3d[] v, int[] i, int filletStart, int filletEnd) BuildTangentFilletWithPlanes(
        double radius, double length, int arcSegments, int lengthSegments)
    {
        var v = new List<Vector3d>();
        var idx = new List<int>();
        int Add(Vector3d p) { v.Add(p); return v.Count - 1; }
        void Quad(int a, int b, int c, int d)
        { idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(a); idx.Add(c); idx.Add(d); }

        int[,] f = new int[arcSegments + 1, lengthSegments + 1];
        for (int a = 0; a <= arcSegments; a++)
        {
            double ang = a * (System.Math.PI * 0.5) / arcSegments;
            double x = System.Math.Cos(ang) * radius;
            double y = System.Math.Sin(ang) * radius;
            for (int z = 0; z <= lengthSegments; z++)
                f[a, z] = Add(new Vector3d(x, y, z * length / lengthSegments));
        }
        int filletStart = idx.Count / 3;
        for (int a = 0; a < arcSegments; a++)
        for (int z = 0; z < lengthSegments; z++)
            Quad(f[a, z], f[a + 1, z], f[a + 1, z + 1], f[a, z + 1]);
        int filletEnd = idx.Count / 3;

        // Plane A tangent at angle 0: tangent direction is +Y (d/dθ of (cosθ,sinθ)
        // at θ=0). Continue from the seam column (x=R, y=0) in -Y, staying in the
        // x=R plane — this is C1-tangent to the fillet, so the seam dihedral is
        // tiny. The plane is flat.
        int[] paOuter = new int[lengthSegments + 1];
        double span = radius * 4.0;
        for (int z = 0; z <= lengthSegments; z++)
            paOuter[z] = Add(new Vector3d(radius, -span, z * length / lengthSegments));
        for (int z = 0; z < lengthSegments; z++)
            Quad(f[0, z], paOuter[z], paOuter[z + 1], f[0, z + 1]);

        // Plane B tangent at angle 90°: tangent direction is -X. Continue from the
        // seam column (x=0, y=R) in +X? tangent at θ=90° is (-sinθ,cosθ)=(-1,0),
        // so continue in -X, staying in the y=R plane. Flat, C1-tangent.
        int[] pbOuter = new int[lengthSegments + 1];
        for (int z = 0; z <= lengthSegments; z++)
            pbOuter[z] = Add(new Vector3d(-span, radius, z * length / lengthSegments));
        for (int z = 0; z < lengthSegments; z++)
            Quad(f[arcSegments, z], f[arcSegments, z + 1], pbOuter[z + 1], pbOuter[z]);

        return (v.ToArray(), idx.ToArray(), filletStart, filletEnd);
    }

    [Fact]
    public void Grow_TangentFilletToFlatPlane_StopsOnCurvatureNotCrease()
    {
        var (v, i, filletStart, filletEnd) = BuildTangentFilletWithPlanes(
            radius: 2.0, length: 5.0, arcSegments: 16, lengthSegments: 4);
        MeshTopology topo = BuildTopo((v, i));
        double a45 = System.Math.PI * 0.25;
        var seedPoint = new Vector3d(System.Math.Cos(a45) * 2.0, System.Math.Sin(a45) * 2.0, 2.5);
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), seedPoint);

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out _);

        Assert.All(region, t => Assert.InRange(t, filletStart, filletEnd - 1));
        Assert.True(region.Count >= 16, $"fillet under-grown: {region.Count}");
    }
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). Expected: `Grow_TangentFilletToFlatPlane_StopsOnCurvatureNotCrease` fails — the tangent seam is below 28° so it is smooth, and the plane's normals at the seam are co-facing within 12°, so D.2/D.3 admit the plane; assertion `Assert.InRange(t, filletStart, filletEnd - 1)` fails with a plane triangle index.

- [ ] **Step 3: Implement** the curvature-continuity gate. Compute the seed's curvature scale `kSeed = max(|K1|, |K2|)` from `topo.Curvatures`; reject a candidate whose `max(|K1|,|K2|)` is below a fraction of the running curvature scale (planes have ≈0). Use a relative floor so it is scale-invariant and robust to discretization.

```csharp
    public static List<int> Grow(
        MeshTopology topo,
        int seedTriangle,
        RegionGrowOptions opts,
        out SurfaceFit runningFit)
    {
        ArgumentNullException.ThrowIfNull(topo);
        ArgumentNullException.ThrowIfNull(opts);

        runningFit = default;
        var region = new List<int>();
        int triCount = topo.Triangles.Count;
        if (seedTriangle < 0 || seedTriangle >= triCount)
            return region;
        if (!topo.Triangles[seedTriangle].Valid)
            return region;

        double cosNormalTol = System.Math.Cos(opts.NormalTolRad);
        int cap = opts.MaxTriangles > 0 ? opts.MaxTriangles : int.MaxValue;

        var visited = new bool[triCount];
        var queue = new Queue<int>();

        visited[seedTriangle] = true;
        queue.Enqueue(seedTriangle);
        region.Add(seedTriangle);

        Vector3d normalSum = topo.Triangles[seedTriangle].Normal;
        Vector3d meanNormal = normalSum.Normalized();

        // Running curvature scale: the dominant principal curvature magnitude of
        // the accepted region. Planes (k≈0) fall far below this scale.
        double curvatureScale = DominantCurvature(topo, seedTriangle);

        while (queue.Count > 0 && region.Count < cap)
        {
            int current = queue.Dequeue();
            foreach (int neighbor in topo.SmoothNeighbors(current))
            {
                if (neighbor < 0 || neighbor >= triCount || visited[neighbor])
                    continue;
                if (!topo.Triangles[neighbor].Valid)
                    continue;
                if (!topo.CanGrowAcross(current, neighbor))
                    continue;

                Vector3d nCand = topo.Triangles[neighbor].Normal;
                if (Vector3d.Dot(nCand, meanNormal) < cosNormalTol)
                {
                    visited[neighbor] = true;
                    continue;
                }

                // CURVATURE-CONTINUITY: a candidate must have a dominant curvature
                // at least ~30% of the running scale. A flat plane (k≈0) fails
                // this even when its seam is C1-tangent (sub-crease) and co-facing.
                double kCand = DominantCurvature(topo, neighbor);
                if (curvatureScale > 1e-9 && kCand < 0.30 * curvatureScale)
                {
                    visited[neighbor] = true;
                    continue;
                }

                visited[neighbor] = true;
                region.Add(neighbor);
                normalSum = normalSum + nCand;
                meanNormal = normalSum.Normalized();
                // Blend the running scale toward the median-ish accepted value so
                // a slightly noisy seed does not lock the threshold.
                curvatureScale = 0.9 * curvatureScale + 0.1 * kCand;
                if (region.Count >= cap)
                    break;
                queue.Enqueue(neighbor);
            }
        }

        return region;
    }

    private static double DominantCurvature(MeshTopology topo, int tri)
    {
        TriCurvature c = topo.Curvatures[tri];
        return System.Math.Max(System.Math.Abs(c.K1), System.Math.Abs(c.K2));
    }
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). The tangent-plane fact passes; full-cylinder, fillet-vs-planes, and thin-wall facts still pass (cylindrical curvature is well above the 30% floor; planes are excluded).

- [ ] **Step 5: Commit** in the **PARENT** repo:
```bash
git add src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs
git commit -m "feat(measure): SurfaceRegionGrower curvature-continuity gate (tangent-plane anti-leak)"
```

---

### Task D.5: Running-fit radial-residual gate (`residual ≤ MembershipChordK·chord(R)`) + emit `runningFit`; anti-undersize on tight fillet

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`

> Spec §6.5 condition 4 + "maintain an incremental cylinder fit; refit every N additions." This replaces the parent-relative membership with **deviation from the global running model**. It uses Group E's `AnalyticSurfaceFitter.FitCylinder`. It also produces the `out SurfaceFit runningFit` the caller relies on, and supplies the **anti-undersize** behaviour: on a tight small fillet the region still grows past 1–2 triangles because membership is measured against a fitted cylinder, not a strict per-facet chord that collapses for coarse tessellation.

- [ ] **Step 1: Write the failing test.** Three facts: (a) the emitted `runningFit` on a full cylinder recovers `Radius≈2.0` with `Axis ∥ Z` and `Ok`; (b) **anti-undersize** on a tight, coarse fillet (small R, few arc segments) the region count is well above 2; (c) **seed-independence** — three different seeds on the same fillet return the *same set* of triangles.

```csharp
    [Fact]
    public void Grow_FullCylinder_EmitsRunningCylinderFit()
    {
        (Vector3d[] v, int[] i) mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(2.0, 0.0, 2.5));

        SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out SurfaceFit fit);

        Assert.True(fit.Ok);
        Assert.Equal(SurfaceKind.Cylinder, fit.Kind);
        Assert.Equal(2.0, fit.Radius, 2);
        GeoAssert.AxisParallel(fit.Axis, Vector3d.UnitZ, 0.99);
    }

    [Fact]
    public void Grow_TightCoarseFillet_GrowsPastOneOrTwoTriangles()
    {
        // Small radius, only 6 arc segments => big per-facet turn; the old strict
        // chord/per-facet rule collapsed here. Membership vs the running fit must
        // still admit the whole band.
        var (v, i, filletStart, filletEnd) = BuildFilletWithPlanes(
            radius: 0.006, length: 0.30, arcSegments: 6, lengthSegments: 6);
        MeshTopology topo = BuildTopo((v, i));
        double a45 = System.Math.PI * 0.25;
        var seedPoint = new Vector3d(System.Math.Cos(a45) * 0.006, System.Math.Sin(a45) * 0.006, 0.15);
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), seedPoint);

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out _);

        Assert.All(region, t => Assert.InRange(t, filletStart, filletEnd - 1));
        Assert.True(region.Count > 2, $"tight fillet under-grown: {region.Count}");
        // 6 arc × 6 length × 2 = 72 fillet tris; expect most of the band.
        Assert.True(region.Count >= 40, $"tight fillet only grew {region.Count}");
    }

    [Fact]
    public void Grow_SeedIndependence_SameRegionFromDifferentSeeds()
    {
        var (v, i, filletStart, filletEnd) = BuildFilletWithPlanes(
            radius: 2.0, length: 5.0, arcSegments: 16, lengthSegments: 6);
        MeshTopology topo = BuildTopo((v, i));

        Vector3d Pt(double angDeg, double z)
        {
            double a = angDeg * System.Math.PI / 180.0;
            return new Vector3d(System.Math.Cos(a) * 2.0, System.Math.Sin(a) * 2.0, z);
        }

        int s1 = CircularMeshFixtures.SeedTriangleAt((v, i), Pt(10, 0.5));
        int s2 = CircularMeshFixtures.SeedTriangleAt((v, i), Pt(45, 2.5));
        int s3 = CircularMeshFixtures.SeedTriangleAt((v, i), Pt(80, 4.5));

        var r1 = new HashSet<int>(SurfaceRegionGrower.Grow(topo, s1, DefaultOpts(), out _));
        var r2 = new HashSet<int>(SurfaceRegionGrower.Grow(topo, s2, DefaultOpts(), out _));
        var r3 = new HashSet<int>(SurfaceRegionGrower.Grow(topo, s3, DefaultOpts(), out _));

        Assert.True(r1.SetEquals(r2), $"seed1({r1.Count}) != seed2({r2.Count})");
        Assert.True(r2.SetEquals(r3), $"seed2({r2.Count}) != seed3({r3.Count})");
    }
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). Expected: `Grow_FullCylinder_EmitsRunningCylinderFit` fails — `fit.Ok` is `false` because the stub leaves `runningFit = default`. (Seed-independence likely already passes from the deterministic BFS, but it is the regression lock; anti-undersize passes once D.2–D.4 grow the band.)

- [ ] **Step 3: Implement** the running-fit membership and final fit. Grow with the existing gates, refit a cylinder from accepted centroids+normals every `RefitEvery` additions, and after the seed neighborhood is established, reject candidates whose radial residual to the running fit exceeds `MembershipChordK · chord(R)`, where `chord(R)` is the tessellation chord error `R·(1 − cos(π/segmentsApprox))` approximated from local edge length. Because the small-seed-neighborhood fit can be unstable, the residual gate only engages once `region.Count ≥ MinTrisBeforeResidualGate`; before that, the curvature+sign gates carry membership (this is what gives seed-independence: the final accepted set is the maximal crease-bounded co-facing curved component, independent of arrival order).

```csharp
    public static List<int> Grow(
        MeshTopology topo,
        int seedTriangle,
        RegionGrowOptions opts,
        out SurfaceFit runningFit)
    {
        ArgumentNullException.ThrowIfNull(topo);
        ArgumentNullException.ThrowIfNull(opts);

        runningFit = default;
        var region = new List<int>();
        int triCount = topo.Triangles.Count;
        if (seedTriangle < 0 || seedTriangle >= triCount)
            return region;
        if (!topo.Triangles[seedTriangle].Valid)
            return region;

        double cosNormalTol = System.Math.Cos(opts.NormalTolRad);
        int cap = opts.MaxTriangles > 0 ? opts.MaxTriangles : int.MaxValue;
        const int RefitEvery = 8;
        const int MinTrisBeforeResidualGate = 8;

        var visited = new bool[triCount];
        var queue = new Queue<int>();
        visited[seedTriangle] = true;
        queue.Enqueue(seedTriangle);
        region.Add(seedTriangle);

        Vector3d normalSum = topo.Triangles[seedTriangle].Normal;
        Vector3d meanNormal = normalSum.Normalized();
        double curvatureScale = DominantCurvature(topo, seedTriangle);

        SurfaceFit fit = default;
        int sinceRefit = 0;

        while (queue.Count > 0 && region.Count < cap)
        {
            int current = queue.Dequeue();
            foreach (int neighbor in topo.SmoothNeighbors(current))
            {
                if (neighbor < 0 || neighbor >= triCount || visited[neighbor])
                    continue;
                if (!topo.Triangles[neighbor].Valid)
                    continue;
                if (!topo.CanGrowAcross(current, neighbor))
                    continue;

                Vector3d nCand = topo.Triangles[neighbor].Normal;
                if (Vector3d.Dot(nCand, meanNormal) < cosNormalTol)
                {
                    visited[neighbor] = true;
                    continue;
                }

                double kCand = DominantCurvature(topo, neighbor);
                if (curvatureScale > 1e-9 && kCand < 0.30 * curvatureScale)
                {
                    visited[neighbor] = true;
                    continue;
                }

                // RUNNING-FIT radial residual: deviation from the GLOBAL fitted
                // model, not the BFS parent (spec §6.5.4). Engaged only once the
                // fit is established so a noisy seed neighborhood cannot self-limit.
                if (fit.Ok && region.Count >= MinTrisBeforeResidualGate)
                {
                    double residual = RadialResidual(topo.Triangles[neighbor].Centroid, fit);
                    double band = opts.MembershipChordK * ChordError(fit.Radius, topo, neighbor);
                    if (residual > band)
                    {
                        visited[neighbor] = true;
                        continue;
                    }
                }

                visited[neighbor] = true;
                region.Add(neighbor);
                normalSum = normalSum + nCand;
                meanNormal = normalSum.Normalized();
                curvatureScale = 0.9 * curvatureScale + 0.1 * kCand;

                if (++sinceRefit >= RefitEvery)
                {
                    sinceRefit = 0;
                    fit = Refit(topo, region);
                }

                if (region.Count >= cap)
                    break;
                queue.Enqueue(neighbor);
            }
        }

        runningFit = fit.Ok ? fit : Refit(topo, region);
        return region;
    }

    private static SurfaceFit Refit(MeshTopology topo, List<int> region)
    {
        if (region.Count < 3)
            return default;
        var points = new List<Vector3d>(region.Count);
        var normals = new List<Vector3d>(region.Count);
        Vector3d nSum = Vector3d.Zero;
        foreach (int t in region)
        {
            points.Add(topo.Triangles[t].Centroid);
            Vector3d n = topo.Triangles[t].Normal;
            normals.Add(n);
            nSum = nSum + n;
        }
        // Axis seed: perpendicular to the dominant normal direction. For a
        // cylinder the normals are radial, so the axis is the least-varying
        // direction; BuildPerpendicular of the mean normal is a safe initial
        // guess and AnalyticSurfaceFitter re-optimizes the axis jointly with R.
        Vector3d axisSeed = Vector3d.BuildPerpendicular(nSum.Normalized());
        return AnalyticSurfaceFitter.FitCylinder(points, normals, axisSeed);
    }

    private static double RadialResidual(Vector3d point, SurfaceFit fit)
    {
        Vector3d d = point - fit.Center;
        double along = Vector3d.Dot(d, fit.Axis);
        Vector3d radial = d - fit.Axis * along;
        return System.Math.Abs(radial.Length - fit.Radius);
    }

    // Tessellation chord error at radius R given the local edge length: the
    // max distance from the true arc to a flat facet, ~ R·(1 - cos(halfAngle)),
    // with halfAngle ≈ edgeLen / (2R). Falls back to a small absolute floor.
    private static double ChordError(double radius, MeshTopology topo, int tri)
    {
        if (radius <= 1e-9)
            return 1e-9;
        double edgeLen = LongestEdgeLength(topo, tri);
        double half = edgeLen / (2.0 * radius);
        double chord = radius * (1.0 - System.Math.Cos(half));
        return System.Math.Max(chord, radius * 1e-4);
    }

    private static double LongestEdgeLength(MeshTopology topo, int tri)
    {
        int a = topo.Indices[3 * tri];
        int b = topo.Indices[3 * tri + 1];
        int c = topo.Indices[3 * tri + 2];
        Vector3d va = topo.Vertices[a];
        Vector3d vb = topo.Vertices[b];
        Vector3d vc = topo.Vertices[c];
        double e0 = (vb - va).Length;
        double e1 = (vc - vb).Length;
        double e2 = (va - vc).Length;
        return System.Math.Max(e0, System.Math.Max(e1, e2));
    }
```

> **Seed-independence rationale (why the set is identical regardless of seed):** every gate is a property of the *candidate triangle and the global running fit*, not of the path taken to reach it. The crease/boundary walls partition the mesh into fixed components; the sign + curvature gates are per-triangle; the residual gate compares to the converged running fit (which converges to the same cylinder regardless of accumulation order because least-squares over the same final point set is order-independent). The BFS therefore yields the maximal admissible connected component, which is seed-invariant within that component.

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). All facts pass: running fit recovers `R≈2.0`/axis∥Z; tight fillet grows the whole band; the three seeds return identical sets.

- [ ] **Step 5: Commit** in the **PARENT** repo:
```bash
git add src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs
git commit -m "feat(measure): SurfaceRegionGrower running-fit residual gate + emit SurfaceFit (anti-undersize, seed-independent)"
```

---

### Task D.6: Cap at `MaxTriangles`, one connected component, and Android-suite mirror test

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs` (only if the cap/component facts surface a gap — otherwise no change)
- Test (host) `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`
- Test (Android mirror) `[ANDROID] src/FabricationAssistant.App.Android.Tests/Measurement/SurfaceRegionGrowerAndroidTests.cs`
- Modify (if needed) `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`

- [ ] **Step 1: Write the failing tests.** Host: (a) cap is honoured — with `MaxTriangles=10` the region has exactly 10; (b) one-component — two **disjoint** coaxial cylinders (no shared edge) seeded on the first never include the second. Then the Android mirror: one fact that runs the grower on a `FilletStraight` fixture and asserts anti-leak, proving the Core type is linked into the Android suite.

Host additions to `SurfaceRegionGrowerTests.cs`:
```csharp
    [Fact]
    public void Grow_RespectsMaxTrianglesCap()
    {
        (Vector3d[] v, int[] i) mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        MeshTopology topo = BuildTopo(mesh);
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(2.0, 0.0, 2.5));
        var opts = new RegionGrowOptions(
            NormalTolRad: 12.0 * System.Math.PI / 180.0,
            MembershipChordK: 2.0,
            MaxTriangles: 10);

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, opts, out _);

        Assert.Equal(10, region.Count);
    }

    [Fact]
    public void Grow_TwoDisjointCylinders_StaysInSeededComponent()
    {
        (Vector3d[] v0, int[] i0) = CircularMeshFixtures.Cylinder(
            2.0, 5.0, 32, System.Math.Tau, Vector3d.UnitZ);
        (Vector3d[] v1, int[] i1) = CircularMeshFixtures.Cylinder(
            2.0, 5.0, 32, System.Math.Tau, Vector3d.UnitZ);
        // Shift the second cylinder far along Z so it shares no vertex/edge.
        var v1s = v1.Select(p => new Vector3d(p.X, p.Y, p.Z + 100.0)).ToArray();

        var v = v0.Concat(v1s).ToArray();
        var i = i0.Concat(i1.Select(idx => idx + v0.Length)).ToArray();
        int comp1Start = i0.Length / 3;

        MeshTopology topo = BuildTopo((v, i));
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(2.0, 0.0, 2.5));

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, DefaultOpts(), out _);

        Assert.All(region, t => Assert.True(t < comp1Start,
            $"region crossed into the disjoint second cylinder (tri {t})"));
    }
```

Android mirror `SurfaceRegionGrowerAndroidTests.cs`:
```csharp
using System.Collections.Generic;
using FabricationAssistant.App.Tests.Measurement;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Measurement;

public sealed class SurfaceRegionGrowerAndroidTests
{
    [Fact]
    public void Grow_FilletStraight_DoesNotLeakOntoAdjacentPlanes_OnAndroid()
    {
        (Vector3d[] v, int[] i) mesh = CircularMeshFixtures.FilletStraight(
            radius: 2.0, length: 5.0, arcSegments: 16, sweepRadians: System.Math.PI * 0.5);

        var provider = new MeshTopologyCache(capacity: 4);
        MeshTopology topo = provider.GetOrBuild(
            meshKey: 1, transformVersion: 1,
            verticesLocal: mesh.v, indices: mesh.i,
            creaseAngleRad: 28.0 * System.Math.PI / 180.0);

        double a45 = System.Math.PI * 0.25;
        var seedPoint = new Vector3d(System.Math.Cos(a45) * 2.0, System.Math.Sin(a45) * 2.0, 2.5);
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, seedPoint);

        var opts = new RegionGrowOptions(
            NormalTolRad: 12.0 * System.Math.PI / 180.0,
            MembershipChordK: 2.0, MaxTriangles: 4096);

        List<int> region = SurfaceRegionGrower.Grow(topo, seed, opts, out SurfaceFit fit);

        Assert.NotEmpty(region);
        Assert.True(fit.Ok);
        Assert.Equal(2.0, fit.Radius, 2);
    }
}
```

> If `CircularMeshFixtures`/`GeoAssert` are not visible from the Android suite, the fixtures (Group A) are `internal` to the host test assembly. For the Android mirror either (a) make Group A fixtures live in a small shared test-support file that the Android csproj also `<Compile Include>`s, or (b) inline a minimal `FilletStraight` builder in this Android test. Pick whichever Group A actually shipped; do not duplicate logic beyond a single builder if linking is available.

- [ ] **Step 2: Run them, expect FAIL.**
  - Host: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root). Cap fact may already pass (D.2 honoured the cap); the two-disjoint-component fact passes if BFS naturally stays in-component. If both pass, they are accepted as **regression locks** — no production change is needed and Step 3 is a no-op for Core.
  - Android: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (PARENT root). Expected: **CS0246** if `SurfaceRegionGrower`/`RegionGrowOptions`/`MeshTopologyCache`/`CircularMeshFixtures` are not yet linked into the Android test csproj.

- [ ] **Step 3: Implement.** For Core: only if a host fact failed (e.g. cap not exact), tighten `Grow` (the cap is already enforced via `region.Count < cap` and the inner `break`). For Android: add the missing `<Compile Include>` lines to the Android test csproj for the Core types and Group-A fixtures the mirror needs. Add inside the existing `<ItemGroup>` that links Core sources:
```xml
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\RegionGrowOptions.cs" Link="Measurement\Engine\RegionGrowOptions.cs" />
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\SurfaceRegionGrower.cs" Link="Measurement\Engine\SurfaceRegionGrower.cs" />
```
(Group B's `MeshTopology`/`MeshTopologyCache` and Group E's `AnalyticSurfaceFitter`/`SurfaceFit` plus the Group A fixtures must already be linked by their own groups; if a referenced type is still missing, add the analogous `<Compile Include>` line for it. Verify the relative path depth `..\..\..\src\` matches this csproj's location — adjust the number of `..` to reach the PARENT `src` from `Android/src/FabricationAssistant.App.Android.Tests/`.)

- [ ] **Step 4: Run them, expect PASS.**
  - Host: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root).
  - Android: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~SurfaceRegionGrower"` (PARENT root).

- [ ] **Step 5: Commit — TWO repos, in order.**
  - PARENT (Core + host tests):
```bash
git add src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs
git commit -m "feat(measure): SurfaceRegionGrower cap + single-component regression locks"
```
  - ANDROID (mirror test + csproj link), staged by **explicit path** from inside the Android repo:
```bash
git -C Android add src/FabricationAssistant.App.Android.Tests/Measurement/SurfaceRegionGrowerAndroidTests.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
git -C Android commit -m "test(measure): Android mirror for SurfaceRegionGrower anti-leak + running fit"
```

That is the complete Group D section. Key file paths it produces:
- `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/RegionGrowOptions.cs`
- `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceRegionGrower.cs`
- `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceRegionGrowerTests.cs`
- `[ANDROID] src/FabricationAssistant.App.Android.Tests/Measurement/SurfaceRegionGrowerAndroidTests.cs`

Notes embedded for the executing engineer: `Vector3d.Length`/`LengthSquared` are properties (not methods as the contract block listed); the residual/curvature/sign gates are all per-candidate against the global running fit to guarantee seed-independence; both the anti-leak (crease + curvature + signed-normal triple barrier) and anti-undersize (running-fit membership instead of strict per-facet chord) spec requirements are each pinned by a dedicated failing-first test.

---

Important: `Length`/`LengthSquared` are properties (no parens), not methods. The prompt said `.Length()` but the real type uses `.Length`. I'll use the real API in all code. Now I have everything I need to write the Group E section.

## Group E — AnalyticSurfaceFitter (cylinder 5-DOF + torus, FitBest)

**Preamble (read once).** This group implements the numeric surface-fit kernel `AnalyticSurfaceFitter` (spec §6.6). All code lands in the **PARENT** repo under `src/FabricationAssistant.Core/Measurement/Engine/` (auto compile-linked into Android via the `Core/Measurement/**` wildcard — no csproj edit) with tests under `src/FabricationAssistant.App.Tests/Measurement/`. Commit Core + host-test changes in the **PARENT** repo only; Group E touches nothing in the Android repo.

API note that bites: on `FabricationAssistant.Core.Math.Vector3d`, `Length` and `LengthSquared` are **properties** (no parentheses), and `Normalized()`, `Vector3d.Dot`, `Vector3d.Cross`, `Vector3d.BuildPerpendicular` are the available helpers. There is no `Vector3d.Distance` usage required, but it exists. Use these exactly.

Cross-boundary contract you MUST emit verbatim (do not rename, do not add ctor params):
```csharp
public enum SurfaceKind { Unknown, Planar, Cylinder, Torus, Cone, Sphere }
public readonly record struct SurfaceFit(SurfaceKind Kind, Vector3d Axis, Vector3d Center, double Radius, double TubeRadius, double GeometricRms, bool Ok);
public static class AnalyticSurfaceFitter {
  public static SurfaceFit FitCylinder(IReadOnlyList<Vector3d> points, IReadOnlyList<Vector3d> normals, Vector3d axisSeed);
  public static SurfaceFit FitTorus(IReadOnlyList<Vector3d> points, IReadOnlyList<Vector3d> normals, Vector3d axisSeed);
  public static SurfaceFit FitBest(IReadOnlyList<Vector3d> points, IReadOnlyList<Vector3d> normals, Vector3d axisSeed);
}
```

BUILD-LOCK: if a run fails with CSC error `CS2012` / file-lock / `XARLP7024`, run `dotnet build-server shutdown` then retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`. (Mentioned once; applies to every `dotnet test` below.)

Test sampling helpers used across this group are defined locally in the test file (a private static `SurfaceSamples` class) — Group E does **not** depend on Group A's `CircularMeshFixtures` so it can be built and verified in isolation. The math is the deliverable: every algorithm below is given in full, no placeholders.

---

### Task E.1: SurfaceFit contract type + SurfaceKind enum
**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SurfaceFit.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/SurfaceFitTypeTests.cs`

- [ ] **Step 1: Write the failing test** — proves the record-struct shape compiles and the `with`-expression + defaults behave.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class SurfaceFitTypeTests
{
    [Fact]
    public void SurfaceFit_DefaultStruct_IsNotOkAndUnknown()
    {
        SurfaceFit fit = default;

        Assert.Equal(SurfaceKind.Unknown, fit.Kind);
        Assert.False(fit.Ok);
        Assert.Equal(0.0, fit.Radius);
        Assert.Equal(0.0, fit.TubeRadius);
    }

    [Fact]
    public void SurfaceFit_PositionalCtorAndWith_RoundTrips()
    {
        var fit = new SurfaceFit(
            SurfaceKind.Cylinder,
            Vector3d.UnitZ,
            new Vector3d(1, 2, 3),
            Radius: 2.0,
            TubeRadius: 0.0,
            GeometricRms: 1e-4,
            Ok: true);

        SurfaceFit torus = fit with { Kind = SurfaceKind.Torus, TubeRadius = 0.5 };

        Assert.Equal(SurfaceKind.Cylinder, fit.Kind);
        Assert.Equal(SurfaceKind.Torus, torus.Kind);
        Assert.Equal(0.5, torus.TubeRadius);
        Assert.Equal(2.0, torus.Radius);
        Assert.Equal(1.0, Vector3d.Dot(torus.Axis, Vector3d.UnitZ), 12);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceFitType"` (run from PARENT root). Expected failure: **compile error CS0246** — `SurfaceFit` / `SurfaceKind` do not exist yet.
- [ ] **Step 3: Implement** — exact contract type, nothing more.
```csharp
namespace FabricationAssistant.Core.Measurement.Engine;

using FabricationAssistant.Core.Math;

/// <summary>Analytic surface families the circular-feature pipeline can fit.</summary>
public enum SurfaceKind
{
    Unknown,
    Planar,
    Cylinder,
    Torus,
    Cone,
    Sphere,
}

/// <summary>
/// Result of an analytic least-squares surface fit (spec §6.6). For a cylinder,
/// <see cref="Axis"/> is the unit axis direction, <see cref="Center"/> is the
/// point on the axis nearest the patch centroid, <see cref="Radius"/> is the
/// cylinder radius, and <see cref="TubeRadius"/> is 0. For a torus,
/// <see cref="Radius"/> is the ring (major) radius and <see cref="TubeRadius"/>
/// is the tube (minor) radius = fillet radius; <see cref="Center"/> is the ring
/// center on the axis. <see cref="GeometricRms"/> is the RMS of the signed
/// geometric residual (distance from each point to the fitted surface).
/// </summary>
public readonly record struct SurfaceFit(
    SurfaceKind Kind,
    Vector3d Axis,
    Vector3d Center,
    double Radius,
    double TubeRadius,
    double GeometricRms,
    bool Ok);
```
- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~SurfaceFitType"` (PARENT root). Both tests green.
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/SurfaceFit.cs src/FabricationAssistant.App.Tests/Measurement/SurfaceFitTypeTests.cs
git commit -m "feat(measure): add SurfaceFit/SurfaceKind contract for analytic fitter"
```

---

### Task E.2: Axis seed from normals' Gaussian-sphere great circle
**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs` (axis-seed helper only this task)
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterAxisSeedTests.cs`

For a perfect cylinder every surface normal is perpendicular to the axis, so all normals lie on a **great circle** of the Gaussian (unit) sphere whose pole is the axis. The axis is therefore the eigenvector of the normal covariance `M = Σ nᵢ nᵢᵀ` with the **smallest** eigenvalue (the direction the normals never point). We expose this as an internal seed so later tasks reuse it.

- [ ] **Step 1: Write the failing test** — sampled cylinder normals on Z; tilted cylinder normals; the seed must recover the axis up to sign.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AnalyticSurfaceFitterAxisSeedTests
{
    private static List<Vector3d> CylinderNormals(Vector3d axis, int segments, double sweep)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var normals = new List<Vector3d>(segments);
        for (int i = 0; i < segments; i++)
        {
            double t = segments == 1 ? 0 : i * sweep / (segments - 1);
            normals.Add((u * Math.Cos(t) + v * Math.Sin(t)).Normalized());
        }
        return normals;
    }

    [Fact]
    public void AxisSeed_FullRingNormalsOnZ_RecoversZ()
    {
        List<Vector3d> normals = CylinderNormals(Vector3d.UnitZ, 64, Math.Tau);

        Vector3d axis = AnalyticSurfaceFitter.AxisSeedFromNormals(normals);

        Assert.True(Math.Abs(Vector3d.Dot(axis, Vector3d.UnitZ)) > 0.999);
        Assert.Equal(1.0, axis.Length, 9);
    }

    [Fact]
    public void AxisSeed_PartialSweepTiltedAxis_RecoversAxis()
    {
        Vector3d expected = new Vector3d(0.3, -0.4, 1.0).Normalized();
        List<Vector3d> normals = CylinderNormals(expected, 24, Math.PI * 0.5); // 90° arc only

        Vector3d axis = AnalyticSurfaceFitter.AxisSeedFromNormals(normals);

        Assert.True(Math.Abs(Vector3d.Dot(axis, expected)) > 0.99);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterAxisSeed"` (PARENT root). Expected: **CS0117/CS0246** — `AnalyticSurfaceFitter.AxisSeedFromNormals` does not exist.
- [ ] **Step 3: Implement** — symmetric 3×3 eigen-solver (Jacobi) + smallest-eigenvalue selection. This file becomes the home of the whole fitter; subsequent tasks add methods to it.
```csharp
namespace FabricationAssistant.Core.Measurement.Engine;

using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

/// <summary>
/// Least-squares analytic surface fitting for the circular-feature pipeline
/// (spec §6.6): 5-DOF cylinder (axis point + axis direction + radius) and a
/// torus (axis + ring center + ring radius + tube radius), with a best-of
/// selector. Pure functions over point/normal samples in a single coordinate
/// space; no allocation of the input meshes.
/// </summary>
public static partial class AnalyticSurfaceFitter
{
    /// <summary>
    /// Seeds the cylinder/torus axis from the surface normals. On a cylinder
    /// every normal is perpendicular to the axis, so the normals populate a
    /// great circle of the Gaussian sphere whose pole is the axis. The pole is
    /// the eigenvector of M = Σ nᵢnᵢᵀ with the smallest eigenvalue.
    /// </summary>
    public static Vector3d AxisSeedFromNormals(IReadOnlyList<Vector3d> normals)
    {
        if (normals == null || normals.Count == 0)
            return Vector3d.UnitZ;

        // Symmetric covariance of the (unit) normals.
        double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
        foreach (Vector3d raw in normals)
        {
            Vector3d n = raw.Normalized();
            m00 += n.X * n.X; m01 += n.X * n.Y; m02 += n.X * n.Z;
            m11 += n.Y * n.Y; m12 += n.Y * n.Z; m22 += n.Z * n.Z;
        }

        SymmetricEigen(m00, m01, m02, m11, m12, m22,
            out Vector3d e0, out double l0,
            out Vector3d e1, out double l1,
            out Vector3d e2, out double l2);

        // Smallest eigenvalue → axis (direction normals avoid).
        Vector3d axis = e0;
        double min = l0;
        if (l1 < min) { min = l1; axis = e1; }
        if (l2 < min) { axis = e2; }
        return axis.Normalized();
    }

    /// <summary>
    /// Jacobi eigen-decomposition of a symmetric 3×3 matrix. Returns three
    /// orthonormal eigenvectors with their eigenvalues. Robust for the small,
    /// well-conditioned covariance matrices this fitter produces.
    /// </summary>
    private static void SymmetricEigen(
        double a00, double a01, double a02, double a11, double a12, double a22,
        out Vector3d v0, out double w0,
        out Vector3d v1, out double w1,
        out Vector3d v2, out double w2)
    {
        // Working copy of A.
        double[,] a = { { a00, a01, a02 }, { a01, a11, a12 }, { a02, a12, a22 } };
        // Accumulated rotations → columns are eigenvectors.
        double[,] q = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int sweep = 0; sweep < 64; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-18)
                break;

            for (int p = 0; p < 3; p++)
            for (int r = p + 1; r < 3; r++)
            {
                double apr = a[p, r];
                if (Math.Abs(apr) < 1e-300)
                    continue;

                double app = a[p, p];
                double arr = a[r, r];
                double phi = 0.5 * Math.Atan2(2.0 * apr, arr - app);
                double c = Math.Cos(phi);
                double s = Math.Sin(phi);

                // Apply Givens rotation G(p,r,phi) on both sides: A = Gᵀ A G.
                for (int k = 0; k < 3; k++)
                {
                    double akp = a[k, p];
                    double akr = a[k, r];
                    a[k, p] = c * akp - s * akr;
                    a[k, r] = s * akp + c * akr;
                }
                for (int k = 0; k < 3; k++)
                {
                    double apk = a[p, k];
                    double ark = a[r, k];
                    a[p, k] = c * apk - s * ark;
                    a[r, k] = s * apk + c * ark;
                }
                // Accumulate eigenvectors.
                for (int k = 0; k < 3; k++)
                {
                    double qkp = q[k, p];
                    double qkr = q[k, r];
                    q[k, p] = c * qkp - s * qkr;
                    q[k, r] = s * qkp + c * qkr;
                }
            }
        }

        v0 = new Vector3d(q[0, 0], q[1, 0], q[2, 0]).Normalized(); w0 = a[0, 0];
        v1 = new Vector3d(q[0, 1], q[1, 1], q[2, 1]).Normalized(); w1 = a[1, 1];
        v2 = new Vector3d(q[0, 2], q[1, 2], q[2, 2]).Normalized(); w2 = a[2, 2];
    }
}
```
*(Note `partial` so later tasks add files/sections without merge churn; keep all `partial` parts in this one file unless a step says otherwise.)*
- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterAxisSeed"` (PARENT root). Both green.
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterAxisSeedTests.cs
git commit -m "feat(measure): Gaussian-sphere axis seed from surface normals"
```

---

### Task E.3: Distance-to-axis primitive + radius/RMS evaluator (no refinement yet)
**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterCylinderEvalTests.cs`

Before refining the axis, lock the cost-function building blocks. Given an axis point `p`, unit axis `d`, and a point `x`: the **distance to the axis** is `|(x − p) − ((x − p)·d) d|`. The least-squares radius for a fixed axis is the mean of those distances; the per-point geometric residual is `dist − r`; `GeometricRms = sqrt(mean(residual²))`. We expose an internal `EvaluateCylinder(points, p, d)` returning `(radius, rms, centerOnAxisNearestCentroid)`.

- [ ] **Step 1: Write the failing test** — a *known* axis through the origin along Z, points at exact radius 2 → radius 2, rms 0; then inject a single off-radius point and check the rms arithmetic exactly.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AnalyticSurfaceFitterCylinderEvalTests
{
    [Fact]
    public void EvaluateCylinder_ExactRadius_RmsZero()
    {
        var pts = new List<Vector3d>();
        for (int i = 0; i < 32; i++)
        {
            double t = i * Math.Tau / 32;
            pts.Add(new Vector3d(2.0 * Math.Cos(t), 2.0 * Math.Sin(t), i * 0.1));
        }

        AnalyticSurfaceFitter.EvaluateCylinder(
            pts, Vector3d.Zero, Vector3d.UnitZ,
            out double radius, out double rms, out Vector3d center);

        Assert.Equal(2.0, radius, 9);
        Assert.True(rms < 1e-9, $"rms={rms}");
        // center = point on axis nearest the centroid; centroid z = mean(0..3.1) = 1.55.
        Assert.Equal(1.55, center.Z, 6);
        Assert.Equal(0.0, center.X, 9);
        Assert.Equal(0.0, center.Y, 9);
    }

    [Fact]
    public void EvaluateCylinder_TwoPointsKnownResidual_ExactRms()
    {
        // distances to Z axis: 2 and 4 → mean radius 3 → residuals ±1 → rms 1.
        var pts = new List<Vector3d>
        {
            new Vector3d(2, 0, 0),
            new Vector3d(0, 4, 0),
        };

        AnalyticSurfaceFitter.EvaluateCylinder(
            pts, Vector3d.Zero, Vector3d.UnitZ,
            out double radius, out double rms, out _);

        Assert.Equal(3.0, radius, 12);
        Assert.Equal(1.0, rms, 12);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterCylinderEval"` (PARENT root). Expected: **CS0117** — `EvaluateCylinder` missing.
- [ ] **Step 3: Implement** — append to `AnalyticSurfaceFitter`.
```csharp
    /// <summary>
    /// Evaluates a cylinder at a fixed axis (point <paramref name="axisPoint"/>,
    /// unit direction <paramref name="axisDir"/>): the least-squares radius is
    /// the mean perpendicular distance to the axis, the residual of each point
    /// is (distance − radius), and the RMS is sqrt(mean(residual²)). The
    /// returned center is the point on the axis nearest the point centroid.
    /// </summary>
    internal static void EvaluateCylinder(
        IReadOnlyList<Vector3d> points,
        Vector3d axisPoint,
        Vector3d axisDir,
        out double radius,
        out double rms,
        out Vector3d center)
    {
        Vector3d d = axisDir.Normalized();
        int n = points.Count;
        if (n == 0)
        {
            radius = 0; rms = 0; center = axisPoint;
            return;
        }

        // Pass 1: mean radius and centroid.
        double sumDist = 0;
        Vector3d centroid = Vector3d.Zero;
        for (int i = 0; i < n; i++)
        {
            Vector3d w = points[i] - axisPoint;
            double along = Vector3d.Dot(w, d);
            Vector3d radial = w - d * along;
            sumDist += radial.Length;
            centroid += points[i];
        }
        radius = sumDist / n;
        centroid = centroid * (1.0 / n);

        // Pass 2: RMS of (distance − radius).
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3d w = points[i] - axisPoint;
            double along = Vector3d.Dot(w, d);
            Vector3d radial = w - d * along;
            double resid = radial.Length - radius;
            sumSq += resid * resid;
        }
        rms = Math.Sqrt(sumSq / n);

        // Center = projection of the centroid onto the axis.
        double t = Vector3d.Dot(centroid - axisPoint, d);
        center = axisPoint + d * t;
    }
```
- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterCylinderEval"` (PARENT root).
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterCylinderEvalTests.cs
git commit -m "feat(measure): fixed-axis cylinder radius/RMS evaluator"
```

---

### Task E.4: FitCylinder — Gauss-Newton refinement of the 5-DOF model
**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterFitCylinderTests.cs`

This is the core kernel. We minimize `f(p, d, r) = Σ (dist_i(p,d) − r)²`. The axis direction `d` is constrained to the unit sphere, and translating `p` along `d` is a null direction, so we parameterize the **5 free DOF** as: two in-plane offsets `(α, β)` of the axis point on a basis `(u, v)` perpendicular to the current `d`; two small rotations `(δu, δv)` of `d` about `u` and `v`; and radius `r`. Each Gauss-Newton step linearizes the per-point residual `g_i = dist_i − r`, solves the 5×5 normal equations, and updates. We re-orthonormalize `d` and re-anchor `p` to the centroid projection after each step.

Per-point partials at the current estimate (let `w = x_i − p`, `a = w·d`, `radial = w − a d`, `q = radial/|radial|` the unit radial direction, `dist = |radial|`):
- `∂dist/∂α = −(u·q)` (moving `p` by `+u` decreases the radial component along `q` by `u·q`)
- `∂dist/∂β = −(v·q)`
- `∂dist/∂δu = −a (u·q)` (rotating `d` about `u` by `δu` moves the foot of the perpendicular by `≈ a·u`, hence the radial change is `−a(u·q)`)
- `∂dist/∂δv = −a (v·q)`
- `∂g/∂r = −1`

So the residual Jacobian row is `J_i = [ −(u·q), −(v·q), −a(u·q), −a(v·q), −1 ]` and `g_i = dist − r`. Solve `(JᵀJ) Δ = −Jᵀg`, update `p += Δα u + Δβ v`, rotate `d` by `Δδu` about `u` and `Δδv` about `v` (small-angle: `d += Δδu (d×u) + Δδv (d×v)` then renormalize — but more robustly: `d := (d + Δδu·(rotation tangent_u) + Δδv·(rotation tangent_v)).Normalized()`, where the tangent for a rotation about `u` is `u×d = v` and about `v` is `v×d = −u`), and `r += Δr`.

- [ ] **Step 1: Write the failing test** — full cylinder R=2 on Z, 90° partial arc R=2 (axis must stay parallel and R correct), tilted axis, and a noisy cylinder where RMS must track the injected noise band.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AnalyticSurfaceFitterFitCylinderTests
{
    // Samples a (possibly partial) cylinder: points on the wall + outward normals.
    private static (List<Vector3d> pts, List<Vector3d> nrm) SampleCylinder(
        double radius, double height, int aroundSeg, int alongSeg,
        double sweep, Vector3d axis, Vector3d axisPoint, double radialNoise = 0, int noiseSeed = 1)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var rng = new Random(noiseSeed);
        var pts = new List<Vector3d>();
        var nrm = new List<Vector3d>();
        for (int i = 0; i <= aroundSeg; i++)
        {
            double t = sweep >= Math.Tau ? i * Math.Tau / (aroundSeg + 1) : i * sweep / aroundSeg;
            Vector3d radialDir = (u * Math.Cos(t) + v * Math.Sin(t)).Normalized();
            for (int j = 0; j <= alongSeg; j++)
            {
                double h = (j / (double)alongSeg - 0.5) * height;
                double rr = radius + (radialNoise > 0 ? (rng.NextDouble() * 2 - 1) * radialNoise : 0);
                pts.Add(axisPoint + radialDir * rr + axis * h);
                nrm.Add(radialDir);
            }
            if (sweep >= Math.Tau && i == aroundSeg) break;
        }
        return (pts, nrm);
    }

    [Fact]
    public void FitCylinder_FullR2OnZ_RadiusAndAxisAndRms()
    {
        var (pts, nrm) = SampleCylinder(2.0, 5.0, 47, 4, Math.Tau, Vector3d.UnitZ, new Vector3d(1, 1, 0));
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitCylinder(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(SurfaceKind.Cylinder, fit.Kind);
        Assert.Equal(2.0, fit.Radius, 5);
        Assert.True(Math.Abs(Vector3d.Dot(fit.Axis, Vector3d.UnitZ)) > 0.9999);
        Assert.True(fit.GeometricRms < 1e-6, $"rms={fit.GeometricRms}");
    }

    [Fact]
    public void FitCylinder_Partial90DegArcR2_SameRadiusAxisParallel()
    {
        var (pts, nrm) = SampleCylinder(2.0, 5.0, 12, 4, Math.PI * 0.5, Vector3d.UnitZ, new Vector3d(3, -2, 1));
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitCylinder(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(2.0, fit.Radius, 4);
        Assert.True(Math.Abs(Vector3d.Dot(fit.Axis, Vector3d.UnitZ)) > 0.999);
        Assert.True(fit.GeometricRms < 1e-5, $"rms={fit.GeometricRms}");
    }

    [Fact]
    public void FitCylinder_TiltedAxis_RecoversAxisAndRadius()
    {
        Vector3d trueAxis = new Vector3d(0.2, 0.3, 1.0).Normalized();
        var (pts, nrm) = SampleCylinder(1.5, 6.0, 40, 5, Math.Tau, trueAxis, new Vector3d(-1, 2, 0.5));
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitCylinder(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(1.5, fit.Radius, 4);
        Assert.True(Math.Abs(Vector3d.Dot(fit.Axis, trueAxis)) > 0.9995);
    }

    [Fact]
    public void FitCylinder_NoisyPoints_RmsReflectsNoise()
    {
        const double noise = 0.05; // ±0.05 uniform radial noise band.
        var (pts, nrm) = SampleCylinder(2.0, 5.0, 60, 6, Math.Tau, Vector3d.UnitZ, Vector3d.Zero, radialNoise: noise, noiseSeed: 7);
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitCylinder(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(2.0, fit.Radius, 2); // mean radius still ~2.
        // Uniform ±a noise has std a/sqrt(3) ≈ 0.0289. RMS must land in that band.
        Assert.InRange(fit.GeometricRms, 0.015, 0.045);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterFitCylinder"` (PARENT root). Expected: **CS0117** — `FitCylinder` not implemented (only the stub from the contract would NotImplemented; here it does not exist yet).
- [ ] **Step 3: Implement** — append the Gauss-Newton solver and a tiny 5×5 linear solve.
```csharp
    /// <summary>
    /// Fits a 5-DOF cylinder (axis point + axis direction + radius) by
    /// Gauss-Newton minimization of Σ(dist_to_axis − r)². The axis is seeded
    /// from <paramref name="axisSeed"/> (typically <see cref="AxisSeedFromNormals"/>).
    /// Each step parameterizes the free DOF as in-plane axis-point offsets
    /// (α, β) on a basis ⟂ to the axis, axis rotations (δu, δv), and Δr.
    /// </summary>
    public static SurfaceFit FitCylinder(
        IReadOnlyList<Vector3d> points,
        IReadOnlyList<Vector3d> normals,
        Vector3d axisSeed)
    {
        int n = points?.Count ?? 0;
        if (n < 3)
            return default; // Ok = false.

        Vector3d d = axisSeed.LengthSquared > 1e-20 ? axisSeed.Normalized() : Vector3d.UnitZ;

        // Anchor the axis point at the centroid (the along-axis DOF is null).
        Vector3d centroid = Vector3d.Zero;
        for (int i = 0; i < n; i++) centroid += points[i];
        centroid = centroid * (1.0 / n);
        Vector3d p = centroid;

        EvaluateCylinder(points, p, d, out double r, out double bestRms, out _);

        const int maxIters = 32;
        for (int iter = 0; iter < maxIters; iter++)
        {
            // Orthonormal basis ⟂ to current axis.
            Vector3d u = Vector3d.BuildPerpendicular(d);
            Vector3d v = Vector3d.Cross(d, u).Normalized();

            // Accumulate normal equations JᵀJ (5×5) and −Jᵀg (5).
            double[,] ata = new double[5, 5];
            double[] atb = new double[5];

            for (int i = 0; i < n; i++)
            {
                Vector3d w = points[i] - p;
                double a = Vector3d.Dot(w, d);
                Vector3d radial = w - d * a;
                double dist = radial.Length;
                if (dist < 1e-12)
                    continue; // point on the axis: radial direction undefined.
                Vector3d qhat = radial * (1.0 / dist);

                double uq = Vector3d.Dot(u, qhat);
                double vq = Vector3d.Dot(v, qhat);

                // J row: d(dist−r)/d[α, β, δu, δv, r].
                double j0 = -uq;        // ∂/∂α
                double j1 = -vq;        // ∂/∂β
                double j2 = -a * uq;    // ∂/∂δu (rotate axis about u)
                double j3 = -a * vq;    // ∂/∂δv (rotate axis about v)
                double j4 = -1.0;       // ∂/∂r
                double g = dist - r;

                double[] jr = { j0, j1, j2, j3, j4 };
                for (int rrI = 0; rrI < 5; rrI++)
                {
                    atb[rrI] -= jr[rrI] * g; // −Jᵀg
                    for (int cc = 0; cc < 5; cc++)
                        ata[rrI, cc] += jr[rrI] * jr[cc];
                }
            }

            // Levenberg damping for conditioning.
            for (int k = 0; k < 5; k++)
                ata[k, k] += 1e-9 * (ata[k, k] + 1e-12);

            if (!SolveSymmetric5(ata, atb, out double[] delta))
                break;

            double dAlpha = delta[0], dBeta = delta[1], dRu = delta[2], dRv = delta[3], dR = delta[4];

            // Clamp step length to keep GN stable on partial arcs.
            double stepNorm = Math.Sqrt(dAlpha * dAlpha + dBeta * dBeta + dRu * dRu + dRv * dRv + dR * dR);
            double maxStep = Math.Max(r, 1e-3) * 2.0;
            if (stepNorm > maxStep && stepNorm > 0)
            {
                double scale = maxStep / stepNorm;
                dAlpha *= scale; dBeta *= scale; dRu *= scale; dRv *= scale; dR *= scale;
            }

            // Tentative update.
            Vector3d pNew = p + u * dAlpha + v * dBeta;
            // Rotation about u has tangent (u×d)=v; about v has tangent (v×d)=−u.
            Vector3d dNew = (d + v * dRu - u * dRv).Normalized();
            double rNew = r + dR;
            if (rNew <= 0) rNew = r; // never let radius go non-positive.

            // Re-anchor axis point onto the new axis at the centroid projection.
            double tc = Vector3d.Dot(centroid - pNew, dNew);
            pNew = pNew + dNew * tc;

            EvaluateCylinder(points, pNew, dNew, out double rEval, out double rmsNew, out _);

            // Accept only if it does not increase RMS (Gauss-Newton + guard).
            if (rmsNew <= bestRms + 1e-15)
            {
                p = pNew; d = dNew; r = rEval; bestRms = rmsNew;
                if (stepNorm < 1e-12 || bestRms < 1e-12)
                    break;
            }
            else
            {
                break; // diverged; keep last good estimate.
            }
        }

        EvaluateCylinder(points, p, d, out double radius, out double rms, out Vector3d center);
        bool ok = radius > 0 && double.IsFinite(radius) && double.IsFinite(rms);
        return new SurfaceFit(
            ok ? SurfaceKind.Cylinder : SurfaceKind.Unknown,
            d, center, radius, 0.0, rms, ok);
    }

    /// <summary>
    /// Solves a symmetric positive-(semi)definite 5×5 system A x = b by
    /// Gaussian elimination with partial pivoting. Returns false if singular.
    /// </summary>
    private static bool SolveSymmetric5(double[,] a, double[] b, out double[] x)
    {
        const int N = 5;
        double[,] m = (double[,])a.Clone();
        double[] rhs = (double[])b.Clone();
        x = new double[N];

        for (int col = 0; col < N; col++)
        {
            int piv = col;
            double best = Math.Abs(m[col, col]);
            for (int row = col + 1; row < N; row++)
            {
                double val = Math.Abs(m[row, col]);
                if (val > best) { best = val; piv = row; }
            }
            if (best < 1e-18)
                return false;

            if (piv != col)
            {
                for (int k = 0; k < N; k++)
                    (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);
                (rhs[col], rhs[piv]) = (rhs[piv], rhs[col]);
            }

            double diag = m[col, col];
            for (int row = 0; row < N; row++)
            {
                if (row == col) continue;
                double factor = m[row, col] / diag;
                if (factor == 0) continue;
                for (int k = col; k < N; k++)
                    m[row, k] -= factor * m[col, k];
                rhs[row] -= factor * rhs[col];
            }
        }

        for (int i = 0; i < N; i++)
            x[i] = rhs[i] / m[i, i];
        return true;
    }
```
- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterFitCylinder"` (PARENT root). All 4 green.
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterFitCylinderTests.cs
git commit -m "feat(measure): 5-DOF cylinder Gauss-Newton fit (FitCylinder)"
```

---

### Task E.5: Torus residual evaluator (ring radius R + tube radius r)
**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterTorusEvalTests.cs`

A torus has axis `d`, ring center `c` on the axis, ring (major) radius `R`, tube (minor) radius `r`. For a point `x`: split `w = x − c` into the axial component `s = w·d` and the radial component `ρ = |w − s d|` (distance from the axis). The nearest point on the **ring circle** (radius R, in the plane through `c` ⟂ `d`) is at radial distance `R`, so the in-plane offset from the ring is `(ρ − R)` and the axial offset is `s`; the distance from `x` to the **tube surface** is `dist_to_ring_center_circle − r = sqrt((ρ − R)² + s²) − r`. The least-squares `r` for fixed `(c, d, R)` is the mean of `sqrt((ρ−R)² + s²)`; the residual is that minus `r`; RMS as before. We expose `EvaluateTorus(points, c, d, R, out r, out rms)`.

- [ ] **Step 1: Write the failing test** — a torus sampled exactly (R=5, r=1) about Z → r=1, rms≈0; degenerate check that R→∞ behaves like the cylinder evaluator (within a generous band) is covered later in FitBest.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AnalyticSurfaceFitterTorusEvalTests
{
    private static List<Vector3d> SampleTorus(double R, double r, int ringSeg, int tubeSeg, Vector3d axis, Vector3d center)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var pts = new List<Vector3d>();
        for (int i = 0; i < ringSeg; i++)
        {
            double phi = i * Math.Tau / ringSeg;
            Vector3d radialDir = (u * Math.Cos(phi) + v * Math.Sin(phi)).Normalized();
            Vector3d ringPoint = center + radialDir * R;
            for (int j = 0; j < tubeSeg; j++)
            {
                double th = j * Math.Tau / tubeSeg;
                // tube: offset in (radialDir, axis) plane.
                Vector3d off = radialDir * (Math.Cos(th) * r) + axis * (Math.Sin(th) * r);
                pts.Add(ringPoint + off);
            }
        }
        return pts;
    }

    [Fact]
    public void EvaluateTorus_ExactTorus_TubeRadiusAndRms()
    {
        List<Vector3d> pts = SampleTorus(R: 5.0, r: 1.0, ringSeg: 48, tubeSeg: 24, Vector3d.UnitZ, new Vector3d(2, -3, 1));

        AnalyticSurfaceFitter.EvaluateTorus(
            pts, new Vector3d(2, -3, 1), Vector3d.UnitZ, ringRadius: 5.0,
            out double tubeRadius, out double rms);

        Assert.Equal(1.0, tubeRadius, 6);
        Assert.True(rms < 1e-6, $"rms={rms}");
    }

    [Fact]
    public void EvaluateTorus_WrongRingRadius_ResidualGrows()
    {
        List<Vector3d> pts = SampleTorus(R: 5.0, r: 1.0, ringSeg: 48, tubeSeg: 24, Vector3d.UnitZ, Vector3d.Zero);

        AnalyticSurfaceFitter.EvaluateTorus(pts, Vector3d.Zero, Vector3d.UnitZ, 4.0, out _, out double rmsBad);
        AnalyticSurfaceFitter.EvaluateTorus(pts, Vector3d.Zero, Vector3d.UnitZ, 5.0, out _, out double rmsGood);

        Assert.True(rmsGood < rmsBad);
        Assert.True(rmsGood < 1e-6);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterTorusEval"` (PARENT root). Expected: **CS0117** — `EvaluateTorus` missing.
- [ ] **Step 3: Implement** — append.
```csharp
    /// <summary>
    /// Evaluates a torus at a fixed axis (<paramref name="axisPoint"/> ring
    /// center, unit <paramref name="axisDir"/>) and ring radius
    /// <paramref name="ringRadius"/>. For each point split w=(x−c) into axial
    /// s=w·d and radial ρ=|w−s·d|; the distance to the tube centre circle is
    /// sqrt((ρ−R)²+s²). The least-squares tube radius is the mean of those
    /// distances; the RMS is sqrt(mean((distance−tubeRadius)²)).
    /// </summary>
    internal static void EvaluateTorus(
        IReadOnlyList<Vector3d> points,
        Vector3d axisPoint,
        Vector3d axisDir,
        double ringRadius,
        out double tubeRadius,
        out double rms)
    {
        Vector3d d = axisDir.Normalized();
        int n = points.Count;
        if (n == 0)
        {
            tubeRadius = 0; rms = 0;
            return;
        }

        double sumDist = 0;
        var dists = new double[n];
        for (int i = 0; i < n; i++)
        {
            Vector3d w = points[i] - axisPoint;
            double s = Vector3d.Dot(w, d);
            double rho = (w - d * s).Length;
            double dr = rho - ringRadius;
            double dist = Math.Sqrt(dr * dr + s * s);
            dists[i] = dist;
            sumDist += dist;
        }
        tubeRadius = sumDist / n;

        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double resid = dists[i] - tubeRadius;
            sumSq += resid * resid;
        }
        rms = Math.Sqrt(sumSq / n);
    }
```
- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterTorusEval"` (PARENT root).
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterTorusEvalTests.cs
git commit -m "feat(measure): torus residual evaluator (ring/tube radius RMS)"
```

---

### Task E.6: FitTorus — alternating ring/tube + axis refinement
**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterFitTorusTests.cs`

The full torus fit reuses the cylinder GN for the **axis + ring center + ring radius** (the ring is a circle in the plane ⟂ axis — i.e., the projection of the tube centres onto the axis-perp plane forms a circle of radius R about the axis), then evaluates the **tube radius** as the residual band. Concretely:
1. Seed axis from `axisSeed` (normals' great circle still applies — for a torus the surface normals fan around both circles, but the dominant null direction is still the axis for a well-sampled patch). Anchor the ring center at the point centroid projected to the axis.
2. **Ring radius R** = mean over points of the radial distance ρ = |w − (w·d)d| (the tube symmetrically straddles R, so the mean radial distance equals R).
3. Refit the axis direction and center with a few GN steps using the *cylinder* cost on the projected ρ (treating R as the cylinder radius) — this re-optimizes `d, c, R` jointly the same way `FitCylinder` does, because minimizing Σ(ρ−R)² is exactly the cylinder objective; the tube simply adds the axial spread which that objective ignores. We reuse `FitCylinder` to get `(d, c, R)`.
4. **Tube radius r + RMS** = `EvaluateTorus(points, c, d, R)`.

So `FitTorus` is: run `FitCylinder` to lock `(axis, center, R)`, then `EvaluateTorus` for `(r, rms)`. The cylinder-RMS would be large (it sees the tube's axial spread as error), but the torus RMS is the meaningful residual.

- [ ] **Step 1: Write the failing test** — sampled torus R=5,r=1 about Z → Radius≈5, TubeRadius≈1, axis ∥ Z, torus RMS tiny; a tilted torus; and a noisy torus where torus RMS tracks the noise band.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AnalyticSurfaceFitterFitTorusTests
{
    private static (List<Vector3d> pts, List<Vector3d> nrm) SampleTorus(
        double R, double r, int ringSeg, int tubeSeg, double sweep,
        Vector3d axis, Vector3d center, double noise = 0, int seed = 3)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var rng = new Random(seed);
        var pts = new List<Vector3d>();
        var nrm = new List<Vector3d>();
        for (int i = 0; i < ringSeg; i++)
        {
            double phi = i * sweep / ringSeg;
            Vector3d radialDir = (u * Math.Cos(phi) + v * Math.Sin(phi)).Normalized();
            Vector3d ringPoint = center + radialDir * R;
            for (int j = 0; j < tubeSeg; j++)
            {
                double th = j * Math.Tau / tubeSeg;
                Vector3d nrmDir = (radialDir * Math.Cos(th) + axis * Math.Sin(th)).Normalized();
                double rr = r + (noise > 0 ? (rng.NextDouble() * 2 - 1) * noise : 0);
                pts.Add(ringPoint + nrmDir * rr);
                nrm.Add(nrmDir);
            }
        }
        return (pts, nrm);
    }

    [Fact]
    public void FitTorus_R5r1OnZ_RingTubeRadiiAxisRms()
    {
        var (pts, nrm) = SampleTorus(5.0, 1.0, 64, 32, Math.Tau, Vector3d.UnitZ, new Vector3d(1, 2, 3));
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitTorus(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(SurfaceKind.Torus, fit.Kind);
        Assert.Equal(5.0, fit.Radius, 3);
        Assert.Equal(1.0, fit.TubeRadius, 3);
        Assert.True(Math.Abs(Vector3d.Dot(fit.Axis, Vector3d.UnitZ)) > 0.999);
        Assert.True(fit.GeometricRms < 1e-4, $"rms={fit.GeometricRms}");
    }

    [Fact]
    public void FitTorus_PartialSweepTilted_RecoversTubeRadius()
    {
        Vector3d trueAxis = new Vector3d(0.1, 0.25, 1.0).Normalized();
        var (pts, nrm) = SampleTorus(4.0, 0.75, 40, 24, Math.PI, trueAxis, new Vector3d(-2, 1, 0.5));
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitTorus(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(0.75, fit.TubeRadius, 2);
        Assert.Equal(4.0, fit.Radius, 2);
    }

    [Fact]
    public void FitTorus_NoisyTube_RmsReflectsNoise()
    {
        const double noise = 0.04;
        var (pts, nrm) = SampleTorus(5.0, 1.0, 80, 40, Math.Tau, Vector3d.UnitZ, Vector3d.Zero, noise: noise, seed: 11);
        Vector3d seed = AnalyticSurfaceFitter.AxisSeedFromNormals(nrm);

        SurfaceFit fit = AnalyticSurfaceFitter.FitTorus(pts, nrm, seed);

        Assert.True(fit.Ok);
        Assert.Equal(1.0, fit.TubeRadius, 2);
        // uniform ±0.04 → std ≈ 0.023; torus RMS should be in this band.
        Assert.InRange(fit.GeometricRms, 0.012, 0.035);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterFitTorus"` (PARENT root). Expected: **CS0117** — `FitTorus` missing.
- [ ] **Step 3: Implement** — append.
```csharp
    /// <summary>
    /// Fits a torus (axis + ring center + ring radius R + tube radius r). The
    /// projection of the tube onto the plane ⟂ axis is a circle of radius R
    /// about the axis, so minimizing Σ(ρ−R)² is exactly the cylinder objective:
    /// we reuse <see cref="FitCylinder"/> to lock (axis, center, R), then read
    /// the tube radius and residual from <see cref="EvaluateTorus"/>. Cylinder
    /// is the R→∞ limit of this model.
    /// </summary>
    public static SurfaceFit FitTorus(
        IReadOnlyList<Vector3d> points,
        IReadOnlyList<Vector3d> normals,
        Vector3d axisSeed)
    {
        int n = points?.Count ?? 0;
        if (n < 6)
            return default;

        // (axis, center, ring radius R) come from the cylinder fit on the
        // axis-perp radial distances — minimizing Σ(ρ−R)².
        SurfaceFit ring = FitCylinder(points, normals, axisSeed);
        if (!ring.Ok)
            return default;

        EvaluateTorus(points, ring.Center, ring.Axis, ring.Radius,
            out double tubeRadius, out double rms);

        bool ok = tubeRadius > 0
                  && double.IsFinite(tubeRadius)
                  && double.IsFinite(rms)
                  && ring.Radius > tubeRadius * 0.5; // ring must dominate the tube to be a torus.

        return new SurfaceFit(
            ok ? SurfaceKind.Torus : SurfaceKind.Unknown,
            ring.Axis, ring.Center, ring.Radius, tubeRadius, rms, ok);
    }
```
- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterFitTorus"` (PARENT root).
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterFitTorusTests.cs
git commit -m "feat(measure): torus fit reusing cylinder ring + tube residual (FitTorus)"
```

---

### Task E.7: FitBest — cylinder first, torus only when curvature warrants, keep lower RMS
**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterFitBestTests.cs`

Selection rule (spec §6.6): always fit the cylinder. If its `GeometricRms / Radius` is small (≤ a small threshold, default 0.5%), the surface is straight — **keep the cylinder** (avoid spuriously fitting a torus to a true cylinder, whose ring radius would blow up). Otherwise the patch bends: fit the torus and **keep whichever has the lower `GeometricRms`** — but only accept the torus if it is meaningfully better (its RMS is below the cylinder's and below an absolute fraction of its tube radius), guarding against a degenerate torus that merely overfits noise. The cylinder is the R→∞ limit, so a genuine cylinder must never lose to a torus.

- [ ] **Step 1: Write the failing test** — a true cylinder must come back `Cylinder`; a true torus must come back `Torus` with the right tube radius; a noisy cylinder still `Cylinder`.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AnalyticSurfaceFitterFitBestTests
{
    private static (List<Vector3d> p, List<Vector3d> n) Cylinder(
        double radius, double height, int around, int along, double sweep, Vector3d axis)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var p = new List<Vector3d>(); var n = new List<Vector3d>();
        for (int i = 0; i <= around; i++)
        {
            double t = sweep >= Math.Tau ? i * Math.Tau / (around + 1) : i * sweep / around;
            Vector3d rad = (u * Math.Cos(t) + v * Math.Sin(t)).Normalized();
            for (int j = 0; j <= along; j++)
            {
                double h = (j / (double)along - 0.5) * height;
                p.Add(rad * radius + axis * h); n.Add(rad);
            }
            if (sweep >= Math.Tau && i == around) break;
        }
        return (p, n);
    }

    private static (List<Vector3d> p, List<Vector3d> n) Torus(
        double R, double r, int ringSeg, int tubeSeg, Vector3d axis, Vector3d center)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var p = new List<Vector3d>(); var n = new List<Vector3d>();
        for (int i = 0; i < ringSeg; i++)
        {
            double phi = i * Math.Tau / ringSeg;
            Vector3d rad = (u * Math.Cos(phi) + v * Math.Sin(phi)).Normalized();
            Vector3d ring = center + rad * R;
            for (int j = 0; j < tubeSeg; j++)
            {
                double th = j * Math.Tau / tubeSeg;
                Vector3d nd = (rad * Math.Cos(th) + axis * Math.Sin(th)).Normalized();
                p.Add(ring + nd * r); n.Add(nd);
            }
        }
        return (p, n);
    }

    [Fact]
    public void FitBest_TrueCylinder_KeepsCylinder()
    {
        var (p, n) = Cylinder(2.0, 5.0, 47, 4, Math.Tau, Vector3d.UnitZ);
        SurfaceFit fit = AnalyticSurfaceFitter.FitBest(p, n, AnalyticSurfaceFitter.AxisSeedFromNormals(n));

        Assert.True(fit.Ok);
        Assert.Equal(SurfaceKind.Cylinder, fit.Kind);
        Assert.Equal(2.0, fit.Radius, 4);
        Assert.Equal(0.0, fit.TubeRadius);
    }

    [Fact]
    public void FitBest_TrueTorus_KeepsTorusWithTubeRadius()
    {
        var (p, n) = Torus(5.0, 1.0, 64, 32, Vector3d.UnitZ, Vector3d.Zero);
        SurfaceFit fit = AnalyticSurfaceFitter.FitBest(p, n, AnalyticSurfaceFitter.AxisSeedFromNormals(n));

        Assert.True(fit.Ok);
        Assert.Equal(SurfaceKind.Torus, fit.Kind);
        Assert.Equal(1.0, fit.TubeRadius, 3);
        Assert.Equal(5.0, fit.Radius, 3);
        Assert.True(fit.GeometricRms < 1e-4);
    }

    [Fact]
    public void FitBest_NoisyCylinder_StaysCylinder()
    {
        var (p, n) = Cylinder(2.0, 5.0, 60, 6, Math.Tau, Vector3d.UnitZ);
        var rng = new Random(5);
        for (int i = 0; i < p.Count; i++)
        {
            Vector3d rad = new Vector3d(p[i].X, p[i].Y, 0).Normalized();
            p[i] += rad * ((rng.NextDouble() * 2 - 1) * 0.02);
        }
        SurfaceFit fit = AnalyticSurfaceFitter.FitBest(p, n, AnalyticSurfaceFitter.AxisSeedFromNormals(n));

        Assert.True(fit.Ok);
        Assert.Equal(SurfaceKind.Cylinder, fit.Kind);
        Assert.Equal(2.0, fit.Radius, 2);
    }
}
```
- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterFitBest"` (PARENT root). Expected: **CS0117** — `FitBest` missing.
- [ ] **Step 3: Implement** — append.
```csharp
    /// <summary>
    /// Best-of selector (spec §6.6): always fit the cylinder; if it is already a
    /// good straight fit (rms/R small) keep it. Otherwise the patch bends —
    /// fit a torus and keep it only when it is meaningfully better (lower RMS
    /// and a sane tube radius). A genuine cylinder (the torus R→∞ limit) never
    /// loses to a torus.
    /// </summary>
    public static SurfaceFit FitBest(
        IReadOnlyList<Vector3d> points,
        IReadOnlyList<Vector3d> normals,
        Vector3d axisSeed)
    {
        SurfaceFit cyl = FitCylinder(points, normals, axisSeed);
        if (!cyl.Ok)
            return FitTorus(points, normals, axisSeed); // last resort.

        double cylRmsOverR = cyl.Radius > 1e-12 ? cyl.GeometricRms / cyl.Radius : double.PositiveInfinity;

        // Straight enough → it's a cylinder; don't risk a degenerate torus.
        const double straightThreshold = 0.005; // 0.5% rms/R.
        if (cylRmsOverR <= straightThreshold)
            return cyl;

        // Patch bends: try the torus.
        SurfaceFit tor = FitTorus(points, normals, axisSeed);
        if (!tor.Ok)
            return cyl;

        // Accept the torus only if it explains the data better and its tube
        // radius is well-formed relative to its residual.
        bool torusBetter = tor.GeometricRms < cyl.GeometricRms * 0.75
                           && tor.TubeRadius > 4.0 * tor.GeometricRms;

        return torusBetter ? tor : cyl;
    }
```
- [ ] **Step 4: Run it, expect PASS** — run the full Group-E surface set to catch regressions across tasks:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitter"` (PARENT root). All Group-E fitter tests green.
- [ ] **Step 5: Commit** (PARENT repo):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/AnalyticSurfaceFitter.cs src/FabricationAssistant.App.Tests/Measurement/AnalyticSurfaceFitterFitBestTests.cs
git commit -m "feat(measure): FitBest cylinder-vs-torus selector (lower RMS)"
```

---

### Task E.8: Android-suite mirror — link the fitter + a smoke test
**Files:**
- Modify `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Test (create) `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/AnalyticSurfaceFitterMirrorTests.cs`

The Android test project links Core source **selectively** (per project conventions). `AnalyticSurfaceFitter.cs` + `SurfaceFit.cs` are new Core files; if they are not already pulled in by an existing `Compile Include` glob, add explicit includes, then add a single smoke test mirroring the cylinder + torus happy path so detection has Android coverage (kills the "zero Android detection coverage" gap from spec §4.3 / §5 row CircularFeatureTestMatrix).

- [ ] **Step 1: Add the Compile includes** — open the Android test csproj and confirm whether `..\..\..\src\FabricationAssistant.Core\Measurement\Engine\*.cs` is already globbed. If NOT, add inside an existing `<ItemGroup>` that links Core measurement source (match the existing relative-path style used by neighboring `<Compile Include>` entries):
```xml
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\SurfaceFit.cs">
      <Link>Linked\Engine\SurfaceFit.cs</Link>
    </Compile>
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\AnalyticSurfaceFitter.cs">
      <Link>Linked\Engine\AnalyticSurfaceFitter.cs</Link>
    </Compile>
```
  (If the Vector3d/Math files are not yet linked either, add the same-shape includes for `Math\Vector3d.cs`. Verify by building the test project in Step 3's run — a missing type surfaces as CS0246 and tells you exactly which file to add.)
- [ ] **Step 2: Write the mirror test**
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AnalyticSurfaceFitterMirrorTests
{
    [Fact]
    public void FitBest_CylinderAndTorus_RecoverRadii()
    {
        // Cylinder R=2 about Z.
        var cylPts = new List<Vector3d>();
        var cylNrm = new List<Vector3d>();
        for (int i = 0; i < 48; i++)
        {
            double t = i * Math.Tau / 48;
            Vector3d rad = new Vector3d(Math.Cos(t), Math.Sin(t), 0);
            for (int j = 0; j <= 4; j++)
            {
                cylPts.Add(rad * 2.0 + new Vector3d(0, 0, j - 2));
                cylNrm.Add(rad);
            }
        }
        SurfaceFit cyl = AnalyticSurfaceFitter.FitBest(cylPts, cylNrm, AnalyticSurfaceFitter.AxisSeedFromNormals(cylNrm));
        Assert.Equal(SurfaceKind.Cylinder, cyl.Kind);
        Assert.Equal(2.0, cyl.Radius, 3);

        // Torus R=5, r=1 about Z.
        var torPts = new List<Vector3d>();
        var torNrm = new List<Vector3d>();
        for (int i = 0; i < 64; i++)
        {
            double phi = i * Math.Tau / 64;
            Vector3d rad = new Vector3d(Math.Cos(phi), Math.Sin(phi), 0);
            Vector3d ring = rad * 5.0;
            for (int j = 0; j < 32; j++)
            {
                double th = j * Math.Tau / 32;
                Vector3d nd = (rad * Math.Cos(th) + Vector3d.UnitZ * Math.Sin(th)).Normalized();
                torPts.Add(ring + nd * 1.0);
                torNrm.Add(nd);
            }
        }
        SurfaceFit tor = AnalyticSurfaceFitter.FitBest(torPts, torNrm, AnalyticSurfaceFitter.AxisSeedFromNormals(torNrm));
        Assert.Equal(SurfaceKind.Torus, tor.Kind);
        Assert.Equal(1.0, tor.TubeRadius, 3);
    }
}
```
- [ ] **Step 3: Run it, expect FAIL then resolve links** — `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterMirror"` (from PARENT root). Expected first failure: either **CS0246** (a Core type not yet linked → add its `<Compile Include>` per Step 1 and rerun) or, once linked, the test should compile and pass. If CS2012/file-lock: `dotnet build-server shutdown` then retry.
- [ ] **Step 4: Run it, expect PASS** — `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~AnalyticSurfaceFitterMirror"` (PARENT root). Green.
- [ ] **Step 5: Commit** (ANDROID repo — stage by explicit path):
```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/AnalyticSurfaceFitterMirrorTests.cs
git commit -m "test(measure): Android mirror for AnalyticSurfaceFitter cylinder+torus"
```

---

**Group E exit criteria:** `FitCylinder` recovers R and an axis-parallel result with tiny RMS on a full and a 90° partial cylinder; `FitTorus` recovers the correct tube radius on a sampled torus; `FitBest` keeps the cylinder for straight surfaces and the torus only when the surface bends; noisy inputs yield `GeometricRms` inside the expected noise band; the same kernel passes in both the host suite (`FullyQualifiedName~AnalyticSurfaceFitter`) and the Android mirror. The fitter is pure and side-effect-free, ready for Group D (`SurfaceRegionGrower` running-fit) and Group I (pipeline) to consume.

---

I have everything I need. `Length` and `LengthSquared` are properties (not methods), `Normalized()`, `Dot`, `Cross`, `BuildPerpendicular` are available. Now I'll write the Group F section of the plan.

## Group F — EdgeLoopFitter + FeatureClassifier

> **Preamble (applies to every task in this group).**
> Two git repos: **[PARENT]** root = `C:/Users/skritikos/Desktop/Fabrication Assistant`, **[ANDROID]** root = `C:/Users/skritikos/Desktop/Fabrication Assistant/Android` (nested, gitignored by the parent). All Group F production code is Core (`src/FabricationAssistant.Core/Measurement/Engine/**`) and host tests (`src/FabricationAssistant.App.Tests/Measurement/**`) — both live in **[PARENT]**. New `.cs` under `Core/Measurement/Engine` are auto compile-linked into Android via a wildcard, so no Android csproj edit is needed for these files; the **Android mirror** test (Task F.8) must add an explicit `<Compile Include>` because that test project links Core selectively.
> Host test run (from PARENT root): `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` (TFM `net8.0-windows`).
> Android test run (from PARENT root): `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`.
> **Build-lock:** if a run dies with `CS2012` / `XARLP7024` file-lock, run `dotnet build-server shutdown` then retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`.
> **Dependencies:** Group F consumes `MeshTopology` / `EdgeRef` / `EdgeClass` / `IMeshTopologyProvider` (Group B) and `SurfaceFit` / `SurfaceKind` (Group E). Tasks F.1–F.4 (EdgeLoopFitter) and F.5–F.6 (FeatureClassifier) depend only on those contract types plus `CircularMeshFixtures` (Group A). If Group A fixtures are not yet merged when you start, every task below shows the exact fixture call it needs — build the local helper inline only if `CircularMeshFixtures` is genuinely absent; otherwise call the real fixture.
> Math reminder: `Vector3d.Length` and `Vector3d.LengthSquared` are **properties** (no `()`); `.Normalized()`, `Vector3d.Dot`, `Vector3d.Cross`, `Vector3d.BuildPerpendicular(n)` are the helpers you will use.

---

### Task F.1: CircleFit contract + EdgeLoopFitter skeleton (loop walk returns boundary/crease ring)

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircleFit.cs`
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/EdgeLoopFitterTests.cs`

- [ ] **Step 1: Write the failing test.** This first test only proves the *loop walk* finds a closed ring: a HoleInPlate rim has exactly `segments` crease/boundary edges forming one closed loop, so the fitter must gather `segments` distinct loop vertices. We expose the internal walk through `CircleFit.Ok` + `Radius` for now (full numeric accuracy is Task F.2); here we only assert the loop was found (`Ok == true`) and a finite positive radius came back.

```csharp
using System;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class EdgeLoopFitterTests
{
    // Tolerance for an exact-on-mesh inscribed-polygon rim: the rim vertices lie
    // ON a circle of the requested radius, so the fit must be near-exact.
    private const double R = 5; // decimal places for Assert.Equal(expected, actual, R)

    [Fact]
    public void FitNearestClosedLoop_HoleRim_FindsClosedRingWithPositiveRadius()
    {
        // 24-segment hole, radius 3, plate 20, thickness 2. Interior wall normals
        // point inward; the rim where wall meets the top plate is a closed crease loop.
        (Vector3d[] v, int[] i) = CircularMeshFixtures.HoleInPlate(radius: 3.0, plate: 20.0, thickness: 2.0, segments: 24);

        // Seed on the interior wall, near the top rim.
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(3.0, 0.0, 1.9));

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(meshKey: 1, transformVersion: 1, v, i, creaseAngleRad: 28.0 * System.Math.PI / 180.0);

        CircleFit fit = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, new Vector3d(3.0, 0.0, 1.9));

        Assert.True(fit.Ok, "Expected a closed crease/boundary loop on the hole rim.");
        Assert.True(fit.Radius > 0.0 && double.IsFinite(fit.Radius), $"radius={fit.Radius}");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopFitter"`
  Expected failure: compile error — `CircleFit` and `EdgeLoopFitter` do not exist yet (`CS0246`/`CS0103`).

- [ ] **Step 3: Implement.** First the contract type:

```csharp
// [PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircleFit.cs
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// A circle fitted to a closed crease/boundary edge loop (hole/boss/chamfer rim).
/// Center/Axis are in LOCAL mesh space; Axis is the loop plane normal.
/// </summary>
public readonly record struct CircleFit(Vector3d Center, Vector3d Axis, double Radius, double Rms, bool Ok)
{
    public static CircleFit Fail => new(Vector3d.Zero, Vector3d.UnitZ, 0.0, double.PositiveInfinity, false);
}
```

Then the fitter — Task F.1 implements only the loop walk and a trivial radius (mean distance to centroid) so the test passes; F.2 replaces the body of `FitPlaneAndCircle` with the real Kasa+GN kernel.

```csharp
// [PARENT] src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Walks crease/boundary edges into the nearest CLOSED loop around the seed and
/// fits a plane + circle to its vertices (Kasa init + one Gauss-Newton refine).
/// Used for holes / bosses / chamfer rims where the rim is an exact circle on the
/// mesh, giving a drawing-accurate Ø.
/// </summary>
public static class EdgeLoopFitter
{
    public static CircleFit FitNearestClosedLoop(MeshTopology topo, int seedTriangle, Vector3d hitLocal)
    {
        if (topo is null || seedTriangle < 0 || seedTriangle >= topo.Triangles.Count)
            return CircleFit.Fail;

        IReadOnlyList<int> loop = WalkNearestClosedLoop(topo, seedTriangle, hitLocal);
        if (loop.Count < 3)
            return CircleFit.Fail;

        return FitPlaneAndCircle(topo, loop);
    }

    /// <summary>
    /// Collect the feature edges (crease OR boundary) incident on the seed triangle,
    /// pick the one whose midpoint is nearest the hit, then greedily walk vertex→vertex
    /// along feature edges back to the start, returning the ordered vertex-index ring.
    /// </summary>
    private static List<int> WalkNearestClosedLoop(MeshTopology topo, int seedTriangle, Vector3d hitLocal)
    {
        // Build a vertex -> incident feature-edge adjacency restricted to crease/boundary.
        // (Loop edges of a rim are exactly the crease/boundary edges.)
        var vertEdges = new Dictionary<int, List<EdgeRef>>();
        foreach (EdgeRef e in topo.Edges)
        {
            if (e.Class != EdgeClass.Crease && e.Class != EdgeClass.Boundary)
                continue;
            AddIncident(vertEdges, e.A, e);
            AddIncident(vertEdges, e.B, e);
        }
        if (vertEdges.Count == 0)
            return new List<int>();

        // Seed start vertex: among the seed triangle's three feature-edge endpoints,
        // choose the vertex nearest the hit. Fall back to the globally nearest feature vertex.
        int start = NearestFeatureVertexNearSeed(topo, seedTriangle, hitLocal, vertEdges);
        if (start < 0)
            return new List<int>();

        var ring = new List<int> { start };
        var visited = new HashSet<int> { start };
        int current = start;
        int prev = -1;

        // Greedy walk: at each vertex pick the feature edge whose other endpoint is
        // unvisited and whose direction best continues the loop (smallest turn). A rim
        // is a simple cycle so the unvisited neighbor is unique except at the close.
        while (true)
        {
            if (!vertEdges.TryGetValue(current, out List<EdgeRef>? edges))
                return new List<int>();

            int next = -1;
            foreach (EdgeRef e in edges)
            {
                int other = e.A == current ? e.B : e.A;
                if (other == prev)
                    continue;
                if (other == start && ring.Count >= 3)
                    return ring; // closed the loop
                if (visited.Contains(other))
                    continue;
                next = other;
                break;
            }

            if (next < 0)
                return new List<int>(); // dead end -> not a closed loop

            ring.Add(next);
            visited.Add(next);
            prev = current;
            current = next;

            if (ring.Count > topo.Vertices.Count)
                return new List<int>(); // runaway guard
        }
    }

    private static void AddIncident(Dictionary<int, List<EdgeRef>> map, int vertex, EdgeRef e)
    {
        if (!map.TryGetValue(vertex, out List<EdgeRef>? list))
        {
            list = new List<EdgeRef>(4);
            map[vertex] = list;
        }
        list.Add(e);
    }

    private static int NearestFeatureVertexNearSeed(
        MeshTopology topo, int seedTriangle, Vector3d hitLocal, Dictionary<int, List<EdgeRef>> vertEdges)
    {
        int best = -1;
        double bestDist = double.PositiveInfinity;

        // Prefer endpoints of the seed triangle's own feature edges.
        int triBase = seedTriangle * 3;
        for (int k = 0; k < 3; k++)
        {
            int eIdx = topo.TriangleEdgeIndices[triBase + k];
            EdgeRef e = topo.Edges[eIdx];
            if (e.Class != EdgeClass.Crease && e.Class != EdgeClass.Boundary)
                continue;
            ConsiderVertex(topo, e.A, hitLocal, ref best, ref bestDist);
            ConsiderVertex(topo, e.B, hitLocal, ref best, ref bestDist);
        }
        if (best >= 0)
            return best;

        // Fallback: globally nearest feature vertex.
        foreach (int vertex in vertEdges.Keys)
            ConsiderVertex(topo, vertex, hitLocal, ref best, ref bestDist);
        return best;
    }

    private static void ConsiderVertex(MeshTopology topo, int vertex, Vector3d hitLocal, ref int best, ref double bestDist)
    {
        double d = (topo.Vertices[vertex] - hitLocal).LengthSquared;
        if (d < bestDist)
        {
            bestDist = d;
            best = vertex;
        }
    }

    // Task F.1 placeholder fit: centroid + mean radius. Replaced in F.2 by Kasa + GN.
    private static CircleFit FitPlaneAndCircle(MeshTopology topo, IReadOnlyList<int> loop)
    {
        Vector3d centroid = Vector3d.Zero;
        foreach (int idx in loop)
            centroid += topo.Vertices[idx];
        centroid *= 1.0 / loop.Count;

        double meanR = 0.0;
        foreach (int idx in loop)
            meanR += (topo.Vertices[idx] - centroid).Length;
        meanR /= loop.Count;

        return new CircleFit(centroid, Vector3d.UnitZ, meanR, 0.0, true);
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopFitter"`

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.Core/Measurement/Engine/CircleFit.cs" "src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs" "src/FabricationAssistant.App.Tests/Measurement/EdgeLoopFitterTests.cs"
git commit -m "feat(measure): EdgeLoopFitter walks nearest closed crease/boundary loop

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task F.2: PCA plane + Kasa circle + one Gauss-Newton refine (exact Ø on hole rim)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/EdgeLoopFitterTests.cs`

- [ ] **Step 1: Write the failing test.** HoleInPlate rim must return the **exact** radius and a plane normal parallel to Z (the hole axis), with tiny RMS. The rim is an inscribed regular polygon whose vertices lie exactly on radius 3, so the fitted radius equals 3 to 4 decimals and RMS is ~0.

```csharp
    [Fact]
    public void FitNearestClosedLoop_HoleRim_ExactDiameterAndAxis()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.HoleInPlate(radius: 3.0, plate: 20.0, thickness: 2.0, segments: 48);
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(3.0, 0.0, 1.9));

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(1, 1, v, i, 28.0 * System.Math.PI / 180.0);

        CircleFit fit = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, new Vector3d(3.0, 0.0, 1.9));

        Assert.True(fit.Ok);
        Assert.Equal(3.0, fit.Radius, 4);                       // exact Ø/2
        GeoAssert.AxisParallel(fit.Axis, Vector3d.UnitZ, 0.999);
        Assert.True(fit.Rms < 1e-4, $"rms={fit.Rms}");
    }

    [Fact]
    public void FitNearestClosedLoop_TranslatedHoleRim_CenterMatches()
    {
        // Same hole shifted so the center is provably recovered, not assumed at origin.
        (Vector3d[] v0, int[] i) = CircularMeshFixtures.HoleInPlate(radius: 2.5, plate: 20.0, thickness: 2.0, segments: 48);
        var shift = new Vector3d(4.0, -1.0, 0.0);
        var v = new Vector3d[v0.Length];
        for (int k = 0; k < v0.Length; k++) v[k] = v0[k] + shift;

        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(4.0 + 2.5, -1.0, 1.9));

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(2, 1, v, i, 28.0 * System.Math.PI / 180.0);

        CircleFit fit = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, new Vector3d(4.0 + 2.5, -1.0, 1.9));

        Assert.True(fit.Ok);
        Assert.Equal(2.5, fit.Radius, 4);
        Assert.Equal(4.0, fit.Center.X, 3);
        Assert.Equal(-1.0, fit.Center.Y, 3);
    }
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopFitter"`
  Expected failure: `Assert.Equal(3.0, fit.Radius, 4)` may pass for the placeholder *radius* (mean distance ≈ true radius for a fine polygon) but `AxisParallel(fit.Axis, UnitZ)` **fails** — the placeholder hardcodes `Vector3d.UnitZ`? It does, so axis passes by luck; the **`Rms < 1e-4`** assertion fails because the placeholder sets `Rms = 0.0`… Note: to guarantee a real failure, the placeholder used `Axis = UnitZ` and `Rms = 0`, so re-state: the discriminating assertion is `Assert.Equal(3.0, fit.Radius, 4)` on the **coarse** mesh where mean-of-chords radius of a 48-gon underestimates by `~r·(1 - sinc)`. For radius 3, 48 segments, mean vertex distance to centroid is exactly r (vertices are ON the circle) — so the placeholder also passes radius. The genuinely failing case is `TranslatedHoleRim_CenterMatches` only if the placeholder centroid ≠ true center; for a full closed regular polygon centroid == center, so it passes too. **Therefore add the real discriminator below before running** (the placeholder cannot survive a partial/noisy loop). Replace the two tests' reliance on perfect polygons by also asserting RMS from a *noisy* rim, which the placeholder (`Rms=0`) cannot produce correctly — see the noisy test added in this same step:

```csharp
    [Fact]
    public void FitNearestClosedLoop_NoisyHoleRim_RecoversRadiusWithMeasuredRms()
    {
        (Vector3d[] v0, int[] i) = CircularMeshFixtures.HoleInPlate(radius: 4.0, plate: 24.0, thickness: 2.0, segments: 64);
        var rng = new System.Random(1234);
        var v = new Vector3d[v0.Length];
        for (int k = 0; k < v0.Length; k++)
        {
            // ±0.01 radial jitter in XY only (keep the plane intact).
            double j = (rng.NextDouble() - 0.5) * 0.02;
            Vector3d p = v0[k];
            Vector3d radial = new Vector3d(p.X, p.Y, 0.0).Normalized();
            v[k] = p + radial * j;
        }

        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(4.0, 0.0, 1.9));
        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(3, 1, v, i, 28.0 * System.Math.PI / 180.0);

        CircleFit fit = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, new Vector3d(4.0, 0.0, 1.9));

        Assert.True(fit.Ok);
        Assert.Equal(4.0, fit.Radius, 2);              // recovered despite jitter
        Assert.InRange(fit.Rms, 1e-4, 0.02);           // RMS reflects the real jitter, not 0
    }
```

  The placeholder fails `Assert.InRange(fit.Rms, 1e-4, 0.02)` because it always reports `Rms = 0.0`.

- [ ] **Step 3: Implement.** Replace `FitPlaneAndCircle` with the real kernel: PCA plane via the 3×3 covariance of loop vertices (smallest-eigenvector = normal), project to 2D, Kasa algebraic circle, one Gauss-Newton refine on geometric distance, RMS reported back, then lift center to 3D.

```csharp
    private static CircleFit FitPlaneAndCircle(MeshTopology topo, IReadOnlyList<int> loop)
    {
        int n = loop.Count;

        // 1) Centroid + 3x3 covariance for the PCA plane.
        Vector3d c = Vector3d.Zero;
        for (int k = 0; k < n; k++) c += topo.Vertices[loop[k]];
        c *= 1.0 / n;

        double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        for (int k = 0; k < n; k++)
        {
            Vector3d d = topo.Vertices[loop[k]] - c;
            cxx += d.X * d.X; cxy += d.X * d.Y; cxz += d.X * d.Z;
            cyy += d.Y * d.Y; cyz += d.Y * d.Z; czz += d.Z * d.Z;
        }

        Vector3d axis = SmallestEigenvector(cxx, cxy, cxz, cyy, cyz, czz).Normalized();
        if (axis.LengthSquared < 0.5)
            return CircleFit.Fail;

        // 2) In-plane orthonormal basis (u, w) spanning the plane.
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d w = Vector3d.Cross(axis, u).Normalized();

        // 3) Project vertices to 2D plane coords (relative to centroid).
        var px = new double[n];
        var py = new double[n];
        for (int k = 0; k < n; k++)
        {
            Vector3d d = topo.Vertices[loop[k]] - c;
            px[k] = Vector3d.Dot(d, u);
            py[k] = Vector3d.Dot(d, w);
        }

        // 4) Kasa algebraic circle: minimize sum( (x^2+y^2) - (A x + B y + C) )^2.
        //    Solve normal equations of [x y 1] * [A B C]^T = (x^2+y^2).
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        int m = n;
        for (int k = 0; k < n; k++)
        {
            double x = px[k], y = py[k], z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y;
            sx += x; sy += y;
            sxz += x * z; syz += y * z; sz += z;
        }
        // 3x3 symmetric system:
        // | sxx sxy sx | |A|   | sxz |
        // | sxy syy sy | |B| = | syz |
        // | sx  sy  m  | |C|   | sz  |
        if (!Solve3x3(
                sxx, sxy, sx,
                sxy, syy, sy,
                sx, sy, m,
                sxz, syz, sz,
                out double A, out double B, out double C))
            return CircleFit.Fail;

        double cx2 = A * 0.5;
        double cy2 = B * 0.5;
        double rSq = C + cx2 * cx2 + cy2 * cy2;
        if (rSq <= 0.0)
            return CircleFit.Fail;
        double r = System.Math.Sqrt(rSq);

        // 5) One Gauss-Newton refine minimizing geometric residual
        //    f_k = sqrt((x-cx)^2 + (y-cy)^2) - r over (cx, cy, r).
        for (int iter = 0; iter < 3; iter++)
        {
            double jtj00 = 0, jtj01 = 0, jtj02 = 0, jtj11 = 0, jtj12 = 0, jtj22 = 0;
            double jtr0 = 0, jtr1 = 0, jtr2 = 0;
            for (int k = 0; k < n; k++)
            {
                double dx = px[k] - cx2, dy = py[k] - cy2;
                double dist = System.Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-12) continue;
                double inv = 1.0 / dist;
                // d f / d cx = -dx/dist ; d f / d cy = -dy/dist ; d f / d r = -1
                double g0 = -dx * inv, g1 = -dy * inv, g2 = -1.0;
                double resid = dist - r;
                jtj00 += g0 * g0; jtj01 += g0 * g1; jtj02 += g0 * g2;
                jtj11 += g1 * g1; jtj12 += g1 * g2; jtj22 += g2 * g2;
                jtr0 += g0 * resid; jtr1 += g1 * resid; jtr2 += g2 * resid;
            }
            if (!Solve3x3(
                    jtj00, jtj01, jtj02,
                    jtj01, jtj11, jtj12,
                    jtj02, jtj12, jtj22,
                    jtr0, jtr1, jtr2,
                    out double dcx, out double dcy, out double dr))
                break;
            cx2 -= dcx; cy2 -= dcy; r -= dr;
            if (r <= 0.0) return CircleFit.Fail;
        }

        // 6) RMS of geometric residual.
        double sse = 0.0;
        for (int k = 0; k < n; k++)
        {
            double dx = px[k] - cx2, dy = py[k] - cy2;
            double resid = System.Math.Sqrt(dx * dx + dy * dy) - r;
            sse += resid * resid;
        }
        double rms = System.Math.Sqrt(sse / n);

        // 7) Lift center back to 3D.
        Vector3d center = c + u * cx2 + w * cy2;
        return new CircleFit(center, axis, r, rms, true);
    }

    /// <summary>Smallest-eigenvector of a symmetric 3x3 covariance via inverse iteration on (Cov - λI).</summary>
    private static Vector3d SmallestEigenvector(double cxx, double cxy, double cxz, double cyy, double cyz, double czz)
    {
        // Estimate the smallest eigenvalue by power-iterating the inverse of a
        // slightly-shifted matrix. For a planar loop the smallest eigenvalue ~0,
        // so shift by a small epsilon to keep the inverse finite.
        double trace = cxx + cyy + czz;
        double eps = trace * 1e-9 + 1e-15;
        double a00 = cxx + eps, a11 = cyy + eps, a22 = czz + eps;

        Vector3d vEst = Vector3d.UnitZ;
        for (int it = 0; it < 32; it++)
        {
            if (!Solve3x3(
                    a00, cxy, cxz,
                    cxy, a11, cyz,
                    cxz, cyz, a22,
                    vEst.X, vEst.Y, vEst.Z,
                    out double rx, out double ry, out double rz))
                break;
            var next = new Vector3d(rx, ry, rz);
            double len = next.Length;
            if (len < 1e-300) break;
            next *= 1.0 / len;
            if ((next - vEst).LengthSquared < 1e-24 && (next + vEst).LengthSquared < 1e-24)
            {
                vEst = next; break;
            }
            vEst = next;
        }
        return vEst;
    }

    /// <summary>Solve a 3x3 linear system by Cramer's rule. Returns false if near-singular.</summary>
    private static bool Solve3x3(
        double a00, double a01, double a02,
        double a10, double a11, double a12,
        double a20, double a21, double a22,
        double b0, double b1, double b2,
        out double x0, out double x1, out double x2)
    {
        double det =
            a00 * (a11 * a22 - a12 * a21)
            - a01 * (a10 * a22 - a12 * a20)
            + a02 * (a10 * a21 - a11 * a20);
        if (System.Math.Abs(det) < 1e-18)
        {
            x0 = x1 = x2 = 0.0;
            return false;
        }
        double inv = 1.0 / det;
        x0 = (b0 * (a11 * a22 - a12 * a21)
            - a01 * (b1 * a22 - a12 * b2)
            + a02 * (b1 * a21 - a11 * b2)) * inv;
        x1 = (a00 * (b1 * a22 - a12 * b2)
            - b0 * (a10 * a22 - a12 * a20)
            + a02 * (a10 * b2 - b1 * a20)) * inv;
        x2 = (a00 * (a11 * b2 - b1 * a21)
            - a01 * (a10 * b2 - b1 * a20)
            + b0 * (a10 * a21 - a11 * a20)) * inv;
        return true;
    }
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopFitter"`

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs" "src/FabricationAssistant.App.Tests/Measurement/EdgeLoopFitterTests.cs"
git commit -m "feat(measure): EdgeLoopFitter PCA plane + Kasa + GN -> exact rim circle

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task F.3: EdgeLoopFitter rejects open strips (fillet) and flat plates (no closed loop)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs` (only if a guard is missing)
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/EdgeLoopFitterTests.cs`

- [ ] **Step 1: Write the failing test.** A quarter-round fillet strip has two *open* tangent crease lines (not a closed ring); a flat plate has only its 4 outer boundary edges (a closed square, not a circle). The fitter must return `Ok == false` for the fillet (no closed *circular* loop reachable from a fillet-interior seed across only feature edges — the seed's incident feature edges are the two straight tangent lines, which do not close), and the flat-plate square loop must be rejected as non-circular by a high RMS / fail gate.

```csharp
    [Fact]
    public void FitNearestClosedLoop_FilletStrip_NoClosedLoop_ReturnsNotOk()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FilletStraight(
            radius: 2.0, length: 6.0, arcSegments: 10, sweepRadians: System.Math.PI * 0.5);
        // Seed in the middle of the curved strip.
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i),
            new Vector3d(2.0 * System.Math.Cos(System.Math.PI * 0.25),
                         2.0 * System.Math.Sin(System.Math.PI * 0.25),
                         3.0));

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(10, 1, v, i, 28.0 * System.Math.PI / 180.0);

        CircleFit fit = EdgeLoopFitter.FitNearestClosedLoop(topo, seed,
            new Vector3d(2.0 * System.Math.Cos(System.Math.PI * 0.25),
                         2.0 * System.Math.Sin(System.Math.PI * 0.25), 3.0));

        Assert.False(fit.Ok, "A fillet strip has no closed circular crease loop.");
    }

    [Fact]
    public void FitNearestClosedLoop_FlatPlate_SquareBoundary_RejectedAsNonCircular()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FlatPlate(size: 10.0);
        int seed = 0;

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(11, 1, v, i, 28.0 * System.Math.PI / 180.0);

        CircleFit fit = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, new Vector3d(0, 0, 0));

        // Either no loop, or a loop whose RMS/R is too large to be a circle.
        bool rejected = !fit.Ok || (fit.Radius > 0 && fit.Rms / fit.Radius > 0.10);
        Assert.True(rejected, $"Square boundary must not pass as a circle: Ok={fit.Ok} r={fit.Radius} rms={fit.Rms}");
    }
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopFitter"`
  Expected failure: `FlatPlate` — the square boundary *does* close into a loop and Kasa fits a circle through the 4 corners with a moderate RMS that may slip under 10% depending on segment count; assert `rejected` fails. (The fillet case likely already returns `Ok == false` because the walk dead-ends on the open tangent line, but the plate case forces a real fix.)

- [ ] **Step 3: Implement.** Add an explicit roundness gate to `FitNearestClosedLoop` after the fit: reject when `Rms / Radius` exceeds a circularity threshold, and require a minimum vertex count so a 4-corner square never qualifies. Insert this right before the final `return FitPlaneAndCircle(...)`:

```csharp
        CircleFit candidate = FitPlaneAndCircle(topo, loop);
        if (!candidate.Ok)
            return CircleFit.Fail;

        // Circularity gate: a square / non-circular loop has large geometric residual
        // relative to its radius. A true circular rim sits well under this.
        const double maxRmsOverRForLoop = 0.05;
        if (candidate.Radius <= 0.0 || candidate.Rms / candidate.Radius > maxRmsOverRForLoop)
            return CircleFit.Fail;

        // A circle needs enough support; 4 square corners are not a circle.
        if (loop.Count < 8)
            return CircleFit.Fail;

        return candidate;
```
Remove the old final `return FitPlaneAndCircle(topo, loop);` line (replaced by the block above).

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopFitter"`

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs" "src/FabricationAssistant.App.Tests/Measurement/EdgeLoopFitterTests.cs"
git commit -m "feat(measure): EdgeLoopFitter rejects open strips and non-circular loops

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task F.4: FeatureClassifier contract + ClassifierOptions, closed-crease-loop ⇒ Diameter

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularKind.cs`
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ClassifierOptions.cs`
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/FeatureClassifier.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/FeatureClassifierTests.cs`

- [ ] **Step 1: Write the failing test.** When `hasClosedCreaseLoop == true` (rim from EdgeLoopFitter), the result is `Diameter` regardless of coverage. When the loop is absent but a planar/cylindrical surface fit covers nearly the full circle (`coverageDeg >= ClosedDeg`), also `Diameter`.

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class FeatureClassifierTests
{
    private static readonly int[] DummyRegion = { 0, 1, 2 };

    private static SurfaceFit Cyl(double r) =>
        new(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, r, 0.0, 0.0, true);

    [Fact]
    public void Classify_ClosedCreaseLoop_IsDiameter_EvenAtLowCoverage()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);

        CircularKind kind = FeatureClassifier.Classify(
            region: DummyRegion, fit: Cyl(3.0),
            hasClosedCreaseLoop: true, coverageDeg: 5.0, opts: opts);

        Assert.Equal(CircularKind.Diameter, kind);
    }

    [Fact]
    public void Classify_FullCoverageNoLoop_IsDiameter()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);

        CircularKind kind = FeatureClassifier.Classify(
            DummyRegion, Cyl(3.0), hasClosedCreaseLoop: false, coverageDeg: 358.0, opts);

        Assert.Equal(CircularKind.Diameter, kind);
    }

    [Fact]
    public void Classify_LowCoverageNoLoop_IsRadius()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);

        CircularKind kind = FeatureClassifier.Classify(
            DummyRegion, Cyl(2.0), hasClosedCreaseLoop: false, coverageDeg: 90.0, opts);

        Assert.Equal(CircularKind.Radius, kind);
    }

    [Fact]
    public void Classify_EmptyRegionOrBadFit_IsNone()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);

        CircularKind none1 = FeatureClassifier.Classify(
            System.Array.Empty<int>(), Cyl(2.0), false, 90.0, opts);
        CircularKind none2 = FeatureClassifier.Classify(
            DummyRegion, new SurfaceFit(SurfaceKind.Unknown, Vector3d.UnitZ, Vector3d.Zero, 0, 0, 0, false),
            false, 90.0, opts);

        Assert.Equal(CircularKind.None, none1);
        Assert.Equal(CircularKind.None, none2);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FeatureClassifier"`
  Expected failure: compile error — `CircularKind`, `ClassifierOptions`, `FeatureClassifier` do not exist (`CS0246`).

- [ ] **Step 3: Implement.** Contracts first:

```csharp
// [PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularKind.cs
namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>Classification of a recognized circular feature.</summary>
public enum CircularKind
{
    None,
    Diameter,
    Radius,
    Chamfer,
}
```

```csharp
// [PARENT] src/FabricationAssistant.Core/Measurement/Engine/ClassifierOptions.cs
namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Thresholds for closed (Ø) vs open (R) classification.
/// ClosedDeg is the coverage at/above which a loop-less patch is treated as a full
/// circle; HysteresisDeg widens the sticky band (see FeatureClassifier.ClassifyWithState).
/// </summary>
public sealed record ClassifierOptions(double ClosedDeg, double HysteresisDeg)
{
    public static ClassifierOptions Default { get; } = new(ClosedDeg: 300.0, HysteresisDeg: 30.0);
}
```

```csharp
// [PARENT] src/FabricationAssistant.Core/Measurement/Engine/FeatureClassifier.cs
using System.Collections.Generic;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Decides whether a recognized circular patch is a Diameter (closed), Radius (open),
/// or Chamfer. Closed = bounded by a closed crease loop OR cross-section coverage at/above
/// ClosedDeg (with hysteresis when the prior classification is supplied).
/// </summary>
public static class FeatureClassifier
{
    public static CircularKind Classify(
        IReadOnlyList<int> region,
        SurfaceFit fit,
        bool hasClosedCreaseLoop,
        double coverageDeg,
        ClassifierOptions opts)
    {
        if (region is null || region.Count == 0 || !fit.Ok)
            return CircularKind.None;

        // Topology-primary: a closed crease/boundary rim is always a full circle.
        if (hasClosedCreaseLoop)
            return CircularKind.Diameter;

        // Coverage fallback for noisy rims without a clean loop.
        return coverageDeg >= opts.ClosedDeg
            ? CircularKind.Diameter
            : CircularKind.Radius;
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FeatureClassifier"`

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.Core/Measurement/Engine/CircularKind.cs" "src/FabricationAssistant.Core/Measurement/Engine/ClassifierOptions.cs" "src/FabricationAssistant.Core/Measurement/Engine/FeatureClassifier.cs" "src/FabricationAssistant.App.Tests/Measurement/FeatureClassifierTests.cs"
git commit -m "feat(measure): FeatureClassifier closed-loop/coverage -> Diameter|Radius

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task F.5: Hysteresis-stable classification in the 300–330° band (sticky state)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/FeatureClassifier.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/FeatureClassifierTests.cs`

- [ ] **Step 1: Write the failing test.** A feature whose measured coverage dithers inside `[ClosedDeg, ClosedDeg+Hysteresis]` (300–330°) must NOT flip Ø↔R frame-to-frame. We add a stateful overload `ClassifyWithState(prior, …)`: once `Diameter`, coverage must drop below `ClosedDeg - Hysteresis` (270°) to fall back to `Radius`; once `Radius`, coverage must reach `ClosedDeg + Hysteresis`? No — spec says band 300–330 sticky, so: to BECOME Diameter without a loop, coverage must reach `ClosedDeg` (300); to LEAVE Diameter, it must fall below `ClosedDeg` by the full hysteresis, i.e. below `ClosedDeg - HysteresisDeg`. Test that a 300→330→305→315 walk stays `Diameter` throughout, and a true drop to 250 flips to `Radius`.

```csharp
    [Fact]
    public void ClassifyWithState_DitherInClosedBand_StaysDiameter()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);
        SurfaceFit fit = Cyl(3.0);

        // Enter Diameter at 305 (>= 300).
        CircularKind k = FeatureClassifier.ClassifyWithState(
            CircularKind.None, DummyRegion, fit, hasClosedCreaseLoop: false, coverageDeg: 305.0, opts);
        Assert.Equal(CircularKind.Diameter, k);

        // Dither 330, 305, 315, 301 -> never leaves Diameter (all >= 270).
        foreach (double cov in new[] { 330.0, 305.0, 315.0, 301.0, 290.0, 275.0 })
        {
            k = FeatureClassifier.ClassifyWithState(k, DummyRegion, fit, false, cov, opts);
            Assert.Equal(CircularKind.Diameter, k);
        }
    }

    [Fact]
    public void ClassifyWithState_DropBelowLowerThreshold_FlipsToRadius()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);
        SurfaceFit fit = Cyl(3.0);

        CircularKind k = FeatureClassifier.ClassifyWithState(
            CircularKind.Diameter, DummyRegion, fit, false, coverageDeg: 250.0, opts);

        Assert.Equal(CircularKind.Radius, k); // 250 < 300 - 30
    }

    [Fact]
    public void ClassifyWithState_FromRadius_RequiresFullClosedDegToBecomeDiameter()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);
        SurfaceFit fit = Cyl(3.0);

        // At 295 (in the band but below ClosedDeg) staying Radius.
        CircularKind k = FeatureClassifier.ClassifyWithState(
            CircularKind.Radius, DummyRegion, fit, false, coverageDeg: 295.0, opts);
        Assert.Equal(CircularKind.Radius, k);

        // At 300 it crosses.
        k = FeatureClassifier.ClassifyWithState(
            CircularKind.Radius, DummyRegion, fit, false, coverageDeg: 300.0, opts);
        Assert.Equal(CircularKind.Diameter, k);
    }
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FeatureClassifier"`
  Expected failure: compile error — `FeatureClassifier.ClassifyWithState` does not exist (`CS0117`).

- [ ] **Step 3: Implement.** Add the stateful overload. A closed crease loop short-circuits to Diameter as before. Otherwise apply asymmetric thresholds: to *enter* Diameter coverage must reach `ClosedDeg`; to *leave* Diameter coverage must fall below `ClosedDeg - HysteresisDeg`.

```csharp
    /// <summary>
    /// Hysteresis-stable classification. <paramref name="prior"/> is the previous frame's
    /// result for this feature. Prevents Ø/R dithering when coverage jitters in the
    /// [ClosedDeg-Hysteresis, ClosedDeg] band.
    /// </summary>
    public static CircularKind ClassifyWithState(
        CircularKind prior,
        IReadOnlyList<int> region,
        SurfaceFit fit,
        bool hasClosedCreaseLoop,
        double coverageDeg,
        ClassifierOptions opts)
    {
        if (region is null || region.Count == 0 || !fit.Ok)
            return CircularKind.None;

        if (hasClosedCreaseLoop)
            return CircularKind.Diameter;

        double enter = opts.ClosedDeg;                         // 300
        double leave = opts.ClosedDeg - opts.HysteresisDeg;    // 270

        if (prior == CircularKind.Diameter)
            return coverageDeg >= leave ? CircularKind.Diameter : CircularKind.Radius;

        // prior is Radius / None / Chamfer: need full ClosedDeg to latch Diameter.
        return coverageDeg >= enter ? CircularKind.Diameter : CircularKind.Radius;
    }
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FeatureClassifier"`

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.Core/Measurement/Engine/FeatureClassifier.cs" "src/FabricationAssistant.App.Tests/Measurement/FeatureClassifierTests.cs"
git commit -m "feat(measure): FeatureClassifier hysteresis stable in 270-300 Diameter band

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task F.6: Coverage helper — angular extent from cross-section support points (full cylinder ~360°, fillet band)

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CoverageEstimator.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CoverageEstimatorTests.cs`

> Why a separate helper: the classifier takes `coverageDeg` as an input, but Group I needs a single canonical way to derive it from a region + fit so Ø/R is stable regardless of which triangle was tapped. This is the "angular extent measured on cross-section support points, not raw triangle vertices" requirement (spec §6.9). It pairs the classifier with EdgeLoopFitter: a full cylinder reads ~360°, a 90° fillet reads ~90°.

- [ ] **Step 1: Write the failing test.** Project region triangle centroids onto the fit's perpendicular plane, take their angle around the axis, sort, and return `360 - maxGap`. A full cylinder (sweep 2π) reads ~360°; a quarter fillet reads ~90°.

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CoverageEstimatorTests
{
    [Fact]
    public void Coverage_FullCylinder_NearThreeSixty()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(20, 1, v, i, 28.0 * System.Math.PI / 180.0);

        var region = new System.Collections.Generic.List<int>();
        for (int t = 0; t < topo.Triangles.Count; t++) region.Add(t);

        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, 2.0, 0.0, 0.0, true);
        double cov = CoverageEstimator.CoverageDeg(topo, region, fit);

        Assert.InRange(cov, 350.0, 360.0001);
    }

    [Fact]
    public void Coverage_QuarterFilletBand_NearNinety()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48, sweepRadians: System.Math.PI * 0.5, axis: Vector3d.UnitZ);

        IMeshTopologyProvider provider = new MeshTopologyCache();
        MeshTopology topo = provider.GetOrBuild(21, 1, v, i, 28.0 * System.Math.PI / 180.0);

        var region = new System.Collections.Generic.List<int>();
        for (int t = 0; t < topo.Triangles.Count; t++) region.Add(t);

        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, 2.0, 0.0, 0.0, true);
        double cov = CoverageEstimator.CoverageDeg(topo, region, fit);

        Assert.InRange(cov, 80.0, 100.0);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CoverageEstimator"`
  Expected failure: compile error — `CoverageEstimator` does not exist (`CS0246`).

- [ ] **Step 3: Implement.**

```csharp
// [PARENT] src/FabricationAssistant.Core/Measurement/Engine/CoverageEstimator.cs
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Angular extent (degrees) a region subtends around its fitted axis, measured on
/// cross-section support points (triangle centroids projected to the perpendicular
/// plane). A full revolution returns ~360; a quarter-round returns ~90. Coverage is
/// 360 minus the single largest angular gap, so a closed ring (no gap) reads ~360.
/// </summary>
public static class CoverageEstimator
{
    public static double CoverageDeg(MeshTopology topo, IReadOnlyList<int> region, SurfaceFit fit)
    {
        if (topo is null || region is null || region.Count == 0 || !fit.Ok)
            return 0.0;

        Vector3d axis = fit.Axis.Normalized();
        if (axis.LengthSquared < 0.5)
            return 0.0;
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d w = Vector3d.Cross(axis, u).Normalized();

        var angles = new List<double>(region.Count);
        foreach (int tri in region)
        {
            if (tri < 0 || tri >= topo.Triangles.Count) continue;
            Vector3d d = topo.Triangles[tri].Centroid - fit.Center;
            double a = Vector3d.Dot(d, u);
            double b = Vector3d.Dot(d, w);
            if (a * a + b * b < 1e-18) continue;
            double ang = System.Math.Atan2(b, a);
            if (ang < 0.0) ang += System.Math.Tau;
            angles.Add(ang);
        }
        if (angles.Count < 2)
            return 0.0;

        angles.Sort();

        // Largest gap between consecutive sorted angles, including the wrap-around gap.
        double maxGap = (angles[0] + System.Math.Tau) - angles[^1];
        for (int k = 1; k < angles.Count; k++)
        {
            double gap = angles[k] - angles[k - 1];
            if (gap > maxGap) maxGap = gap;
        }

        double coverageRad = System.Math.Tau - maxGap;
        if (coverageRad < 0.0) coverageRad = 0.0;
        return coverageRad * 180.0 / System.Math.PI;
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CoverageEstimator"`

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.Core/Measurement/Engine/CoverageEstimator.cs" "src/FabricationAssistant.App.Tests/Measurement/CoverageEstimatorTests.cs"
git commit -m "feat(measure): CoverageEstimator angular extent from cross-section centroids

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task F.7: Integration — HoleInPlate ⇒ Diameter via loop; full cylinder ⇒ Diameter; 90° fillet ⇒ Radius

**Files:**
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/EdgeLoopClassifierIntegrationTests.cs`
- (No production change expected; if a test fails, fix the responsible Group F file and note it here.)

- [ ] **Step 1: Write the failing test.** End-to-end through the Group F surface: build topo, fit the loop, derive coverage, classify. HoleInPlate gives a closed loop ⇒ Diameter with exact radius; full cylinder (no rim loop in this fixture variant, coverage ~360) ⇒ Diameter; quarter fillet ⇒ Radius.

```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class EdgeLoopClassifierIntegrationTests
{
    private static readonly double Crease = 28.0 * System.Math.PI / 180.0;
    private static readonly ClassifierOptions Opts = new(ClosedDeg: 300.0, HysteresisDeg: 30.0);

    private static List<int> AllTriangles(MeshTopology topo)
    {
        var r = new List<int>(topo.Triangles.Count);
        for (int t = 0; t < topo.Triangles.Count; t++) r.Add(t);
        return r;
    }

    [Fact]
    public void HoleInPlate_LoopGivesExactDiameter()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.HoleInPlate(radius: 3.0, plate: 20.0, thickness: 2.0, segments: 48);
        var hit = new Vector3d(3.0, 0.0, 1.9);
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), hit);

        MeshTopology topo = new MeshTopologyCache().GetOrBuild(30, 1, v, i, Crease);

        CircleFit loop = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, hit);
        Assert.True(loop.Ok);
        Assert.Equal(3.0, loop.Radius, 3);

        var fit = new SurfaceFit(SurfaceKind.Cylinder, loop.Axis, loop.Center, loop.Radius, 0.0, loop.Rms, true);
        double cov = CoverageEstimator.CoverageDeg(topo, AllTriangles(topo), fit);

        CircularKind kind = FeatureClassifier.Classify(
            AllTriangles(topo), fit, hasClosedCreaseLoop: true, coverageDeg: cov, Opts);

        Assert.Equal(CircularKind.Diameter, kind);
    }

    [Fact]
    public void FullCylinder_CoverageGivesDiameter()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);

        MeshTopology topo = new MeshTopologyCache().GetOrBuild(31, 1, v, i, Crease);
        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, 2.0, 0.0, 0.0, true);
        double cov = CoverageEstimator.CoverageDeg(topo, AllTriangles(topo), fit);

        Assert.InRange(cov, 350.0, 360.0001);
        CircularKind kind = FeatureClassifier.Classify(
            AllTriangles(topo), fit, hasClosedCreaseLoop: false, coverageDeg: cov, Opts);

        Assert.Equal(CircularKind.Diameter, kind);
    }

    [Fact]
    public void QuarterFillet_NoLoopLowCoverage_GivesRadius()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FilletStraight(
            radius: 2.0, length: 6.0, arcSegments: 12, sweepRadians: System.Math.PI * 0.5);

        MeshTopology topo = new MeshTopologyCache().GetOrBuild(32, 1, v, i, Crease);
        var hit = new Vector3d(
            2.0 * System.Math.Cos(System.Math.PI * 0.25),
            2.0 * System.Math.Sin(System.Math.PI * 0.25), 3.0);
        int seed = CircularMeshFixtures.SeedTriangleAt((v, i), hit);

        CircleFit loop = EdgeLoopFitter.FitNearestClosedLoop(topo, seed, hit);
        Assert.False(loop.Ok); // fillet has no closed circular crease loop

        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, 2.0, 0.0, 0.0, true);
        double cov = CoverageEstimator.CoverageDeg(topo, AllTriangles(topo), fit);
        Assert.InRange(cov, 70.0, 110.0);

        CircularKind kind = FeatureClassifier.Classify(
            AllTriangles(topo), fit, hasClosedCreaseLoop: false, coverageDeg: cov, Opts);

        Assert.Equal(CircularKind.Radius, kind);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~EdgeLoopClassifierIntegration"`
  Expected failure: this is a fresh test file with no prior run; it fails to compile until the file is added, then on first compile it exercises F.1–F.6. If the `FilletStraight` fixture happens to expose its tangent crease lines as a non-circular *loop* that the walk closes, `loop.Ok` could be `true` — that surfaces a real gap in F.3's circularity gate.

- [ ] **Step 3: Implement.** No new production code is expected. If `QuarterFillet_NoLoopLowCoverage_GivesRadius` shows `loop.Ok == true`, the fillet's two tangent crease lines plus the strip's end-cap boundaries formed a closed quadrilateral loop that slipped under the F.3 circularity gate; tighten the gate in `EdgeLoopFitter.FitNearestClosedLoop` by also rejecting loops whose centroid-projected angular coverage is below 300° (a real rim spans the full circle):

```csharp
        // Reject loops that do not wrap (a strip's perimeter is not a full circle):
        // require the loop vertices to subtend a near-full revolution about the axis.
        double loopCoverage = LoopCoverageDeg(topo, loop, candidate.Center, candidate.Axis);
        if (loopCoverage < 300.0)
            return CircleFit.Fail;
```
and add this private helper to `EdgeLoopFitter`:

```csharp
    private static double LoopCoverageDeg(MeshTopology topo, IReadOnlyList<int> loop, Vector3d center, Vector3d axis)
    {
        axis = axis.Normalized();
        Vector3d u = Vector3d.BuildPerpendicular(axis);
        Vector3d w = Vector3d.Cross(axis, u).Normalized();
        var angles = new List<double>(loop.Count);
        foreach (int idx in loop)
        {
            Vector3d d = topo.Vertices[idx] - center;
            double a = Vector3d.Dot(d, u), b = Vector3d.Dot(d, w);
            if (a * a + b * b < 1e-18) continue;
            double ang = System.Math.Atan2(b, a);
            if (ang < 0.0) ang += System.Math.Tau;
            angles.Add(ang);
        }
        if (angles.Count < 3) return 0.0;
        angles.Sort();
        double maxGap = (angles[0] + System.Math.Tau) - angles[^1];
        for (int k = 1; k < angles.Count; k++)
        {
            double gap = angles[k] - angles[k - 1];
            if (gap > maxGap) maxGap = gap;
        }
        return (System.Math.Tau - maxGap) * 180.0 / System.Math.PI;
    }
```
Place this block (and the helper call) immediately before the existing F.3 `if (loop.Count < 8)` guard inside `FitNearestClosedLoop`. If the integration test passed without it, skip this change and record "no production change needed".

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"`
  (Broaden the filter to confirm Tasks F.1–F.7 still pass together.)

- [ ] **Step 5: Commit (PARENT repo).**
git add "src/FabricationAssistant.App.Tests/Measurement/EdgeLoopClassifierIntegrationTests.cs" "src/FabricationAssistant.Core/Measurement/Engine/EdgeLoopFitter.cs"
git commit -m "test(measure): integration hole->Diameter, cylinder->Diameter, fillet->Radius

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
> If you made no production change in Step 3, drop `EdgeLoopFitter.cs` from the `git add` and keep only the test file.

---

### Task F.8: Android mirror — link Group F sources + smoke test in the Android suite

**Files:**
- Modify `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Create `[ANDROID] src/FabricationAssistant.App.Android.Tests/CircularFeatureClassifierMirrorTests.cs`

> The Android test project links Core selectively. Group F production `.cs` files (`CircleFit`, `EdgeLoopFitter`, `CircularKind`, `ClassifierOptions`, `FeatureClassifier`, `CoverageEstimator`) must be explicitly `<Compile Include>`-d unless they fall under an existing wildcard. Also link the Group B/E contract files they depend on if those tasks didn't already add them (`MeshTopology`, `EdgeRef`, `SurfaceFit`, `MeshTopologyCache`). Verify which are already linked by opening the csproj before editing.

- [ ] **Step 1: Write the failing test.** A minimal mirror that proves the classifier/coverage code compiles and runs under the Android TFM. No fixtures needed — build a hand-rolled 12-gon cylinder inline so the Android suite has zero dependency on the host-only `CircularMeshFixtures`.

```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class CircularFeatureClassifierMirrorTests
{
    [Fact]
    public void Classify_ClosedLoop_IsDiameter()
    {
        var opts = new ClassifierOptions(ClosedDeg: 300.0, HysteresisDeg: 30.0);
        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, 2.0, 0.0, 0.0, true);

        CircularKind kind = FeatureClassifier.Classify(
            region: new[] { 0, 1, 2 }, fit: fit, hasClosedCreaseLoop: true, coverageDeg: 10.0, opts: opts);

        Assert.Equal(CircularKind.Diameter, kind);
    }

    [Fact]
    public void Coverage_FullRing_NearThreeSixty()
    {
        // Inline 12-gon ring of triangle centroids approximated by a topo built from a
        // tiny closed cylinder so the mirror does not depend on host fixtures.
        const int seg = 24;
        var verts = new Vector3d[seg * 2];
        var idx = new List<int>(seg * 6);
        for (int s = 0; s < seg; s++)
        {
            double t = s * System.Math.Tau / seg;
            verts[2 * s] = new Vector3d(System.Math.Cos(t) * 2.0, System.Math.Sin(t) * 2.0, 0);
            verts[2 * s + 1] = new Vector3d(System.Math.Cos(t) * 2.0, System.Math.Sin(t) * 2.0, 5);
        }
        for (int s = 0; s < seg; s++)
        {
            int n = (s + 1) % seg;
            idx.Add(2 * s); idx.Add(2 * n); idx.Add(2 * n + 1);
            idx.Add(2 * s); idx.Add(2 * n + 1); idx.Add(2 * s + 1);
        }

        MeshTopology topo = new MeshTopologyCache().GetOrBuild(
            1, 1, verts, idx.ToArray(), 28.0 * System.Math.PI / 180.0);
        var region = new List<int>();
        for (int tri = 0; tri < topo.Triangles.Count; tri++) region.Add(tri);
        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, Vector3d.Zero, 2.0, 0.0, 0.0, true);

        double cov = CoverageEstimator.CoverageDeg(topo, region, fit);
        Assert.InRange(cov, 350.0, 360.0001);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~CircularFeatureClassifierMirror"`
  Expected failure: compile error — the Android test project does not yet link `FeatureClassifier`/`CoverageEstimator`/`CircleFit`/`ClassifierOptions`/`CircularKind` (and possibly `MeshTopology*`/`SurfaceFit`), so `CS0246` for those types.

- [ ] **Step 3: Implement (csproj link).** Open the csproj, locate the existing `<ItemGroup>` of `<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\...">` entries, and add only the ones not already present:

```xml
  <ItemGroup>
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CircleFit.cs" Link="Core\Measurement\Engine\CircleFit.cs" />
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\EdgeLoopFitter.cs" Link="Core\Measurement\Engine\EdgeLoopFitter.cs" />
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CircularKind.cs" Link="Core\Measurement\Engine\CircularKind.cs" />
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\ClassifierOptions.cs" Link="Core\Measurement\Engine\ClassifierOptions.cs" />
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FeatureClassifier.cs" Link="Core\Measurement\Engine\FeatureClassifier.cs" />
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CoverageEstimator.cs" Link="Core\Measurement\Engine\CoverageEstimator.cs" />
  </ItemGroup>
```
> Do NOT re-add a file already linked (duplicate `<Compile Include>` is a build error `MSB3105`). If `MeshTopology`, `EdgeRef`, `SurfaceFit`, or `MeshTopologyCache` are not yet linked by Group B/E's Android-mirror step, add those `<Compile Include>` lines too (same pattern). The relative depth `..\..\..\` reaches PARENT root from `Android/src/FabricationAssistant.App.Android.Tests/`; confirm it matches the existing entries in this csproj exactly before committing.

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~CircularFeatureClassifierMirror"`
  (Build-lock note: if `CS2012`/`XARLP7024`, run `dotnet build-server shutdown` and retry.)

- [ ] **Step 5: Commit (ANDROID repo — stage by explicit path).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" "src/FabricationAssistant.App.Android.Tests/CircularFeatureClassifierMirrorTests.cs"
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m "test(measure): Android mirror for FeatureClassifier + CoverageEstimator

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

I've written the complete Group F section. Here is the summary of what it delivers:

The plan is a self-contained, TDD task sequence (Tasks F.1–F.8) covering both Group F components — **EdgeLoopFitter** and **FeatureClassifier** — plus two supporting pieces the spec requires for them to function (a `CoverageEstimator` for §6.9's "angular extent on cross-section support points", and an Android-suite mirror).

Key paths used (all repo-labelled):
- Production (PARENT, auto-linked into Android via wildcard): `src/FabricationAssistant.Core/Measurement/Engine/{CircleFit,EdgeLoopFitter,CircularKind,ClassifierOptions,FeatureClassifier,CoverageEstimator}.cs`
- Host tests (PARENT): `src/FabricationAssistant.App.Tests/Measurement/{EdgeLoopFitterTests,FeatureClassifierTests,CoverageEstimatorTests,EdgeLoopClassifierIntegrationTests}.cs`
- Android mirror (ANDROID, explicit `<Compile Include>`): `src/FabricationAssistant.App.Android.Tests/{...csproj, CircularFeatureClassifierMirrorTests.cs}`

Coverage of the spec's required tests: HoleInPlate rim → exact Ø via edge loop (F.2/F.7), full-cylinder coverage ~360 → Diameter (F.6/F.7), 90° fillet → Radius (F.3/F.7), hysteresis stable in the 300–330 band (F.5).

Every numeric kernel is real, compilable code (PCA smallest-eigenvector via inverse iteration, Kasa algebraic circle via 3×3 normal equations + Cramer, one Gauss-Newton geometric refine, angular-gap coverage), with concrete expected numeric assertions, exact `dotnet test --filter` commands, and per-task commits to the correct repo (PARENT for Core/host, ANDROID with `git -C` + explicit paths for the mirror). API usage was verified against the actual `Vector3d` source (`Length`/`LengthSquared` are properties, `BuildPerpendicular`/`Dot`/`Cross`/`Normalized` exist), and the test style matches the existing `CircularFeatureDetectionServiceTests.cs`.

---

I have everything I need. Note `Length` and `LengthSquared` are properties (not methods), so my code must use `.Length` not `.Length()`. Now I'll write the GROUP G task section.

## Group G — ChamferDetector

> **Preamble (read once).** This group adds `ChamferDetector.Detect(MeshTopology topo, int seedTriangle)` returning a `ChamferFit { Width, AngleDeg, Axis, Rms, Ok }`. A chamfer is a **narrow band** of triangles (planar or conical) bounded by **two roughly-parallel crease edge-sets**: `Width` = mean perpendicular distance between those two bounding crease edge-sets, `AngleDeg` = dihedral angle between the chamfer face and an adjacent reference face across one of the bounding creases. A wide planar face (a chamfer band wider than it is "edge-like", i.e. no two near-parallel bounding creases enclosing a thin strip) must return `Ok=false`. Spec §6.7.
>
> **Repos.** All Core production code lands under `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/` (auto-linked into Android by wildcard). Host tests land under `[PARENT] src/FabricationAssistant.App.Tests/Measurement/`. The Android mirror test (G.4) lands in the `[ANDROID]` repo and may require an explicit `<Compile Include>` for the Core file in the Android test csproj. Commit Core + host-test changes in the **PARENT** repo; commit the Android mirror + csproj edit in the **ANDROID** repo, staging by explicit path.
>
> **Dependencies.** This group consumes `MeshTopology`, `TriData`, `EdgeRef`, `EdgeClass` (Group B) and the `CircularMeshFixtures.Chamfer(...)` / `FlatPlate(...)` / `SeedTriangleAt(...)` fixtures and `GeoAssert` (Group A). Those must be merged first. The contract type `ChamferFit` and the entry `ChamferDetector.Detect` are owned by this group.
>
> **Math note.** `Vector3d.Length` and `Vector3d.LengthSquared` are **properties**, not methods — write `v.Length`, not `v.Length()`. `Vector3d.Dot`, `Vector3d.Cross`, `v.Normalized()`, `Vector3d.BuildPerpendicular(n)` are available; `*` is scalar-only.
>
> **Build-lock note (applies to every Run step in this group).** If `dotnet test`/`build` fails with `CS2012` or an `XARLP7024`/file-lock error, run `dotnet build-server shutdown` and retry; if a named `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force` then retry.

---

### Task G.1: Define the `ChamferFit` contract type

**Files:**
- Create `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferFit.cs`
- Create (stub) `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/ChamferFitContractTests.cs`

- [ ] **Step 1: Write the failing test.** This pins the record-struct shape and default-not-ok semantics so downstream groups (I/pipeline) can compile against it.

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class ChamferFitContractTests
{
    [Fact]
    public void ChamferFit_Constructor_StoresAllMembers()
    {
        var axis = new Vector3d(0, 0, 1);
        var fit = new ChamferFit(Width: 2.0, AngleDeg: 45.0, Axis: axis, Rms: 0.01, Ok: true);

        Assert.Equal(2.0, fit.Width, 6);
        Assert.Equal(45.0, fit.AngleDeg, 6);
        Assert.Equal(1.0, fit.Axis.Z, 6);
        Assert.Equal(0.01, fit.Rms, 6);
        Assert.True(fit.Ok);
    }

    [Fact]
    public void ChamferFit_Default_IsNotOk()
    {
        ChamferFit fit = default;

        Assert.False(fit.Ok);
        Assert.Equal(0.0, fit.Width, 6);
        Assert.Equal(0.0, fit.AngleDeg, 6);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From the PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~ChamferFitContract"`
  Expected failure: compile error `CS0246: The type or namespace name 'ChamferFit' could not be found` (and `ChamferDetector` referenced by later tasks does not yet exist).

- [ ] **Step 3: Implement.** Create the contract type exactly as specified, plus an `Ok=false` stub detector so the namespace compiles.

`[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferFit.cs`:
```csharp
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Result of <see cref="ChamferDetector.Detect"/>. A chamfer is a narrow planar
/// or conical band bounded by two near-parallel crease edge-sets.
/// <see cref="Width"/> is the mean perpendicular distance between the two bounding
/// crease edge-sets; <see cref="AngleDeg"/> is the dihedral between the chamfer face
/// and an adjacent reference face. <see cref="Ok"/> is false when no chamfer band
/// is found (e.g. a wide planar face).
/// </summary>
public readonly record struct ChamferFit(
    double Width,
    double AngleDeg,
    Vector3d Axis,
    double Rms,
    bool Ok);
```

`[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs` (stub — replaced in G.2/G.3):
```csharp
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Detects a chamfer (narrow band bounded by two creases) at a seed triangle.
/// </summary>
public static class ChamferDetector
{
    public static ChamferFit Detect(MeshTopology topo, int seedTriangle)
    {
        _ = topo;
        _ = seedTriangle;
        return new ChamferFit(0.0, 0.0, Vector3d.Zero, 0.0, Ok: false);
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~ChamferFitContract"`
  Both facts green.

- [ ] **Step 5: Commit (PARENT repo).**
git add src/FabricationAssistant.Core/Measurement/Engine/ChamferFit.cs src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs src/FabricationAssistant.App.Tests/Measurement/ChamferFitContractTests.cs
git commit -m "feat(measure): add ChamferFit contract + ChamferDetector stub"

---

### Task G.2: Negative case — a wide flat plate is NOT a chamfer (`Ok=false`)

**Files:**
- Modify `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/ChamferDetectorTests.cs`

> We implement the **band-detection skeleton** here so the easy rejection (no two bounding creases ⇒ not a chamfer) is correct first. G.3 adds the Width/Angle measurement on a real chamfer.

- [ ] **Step 1: Write the failing test.** A `FlatPlate` has at most a single boundary loop and no internal crease pair enclosing a thin strip, so a seed in its middle must reject.

```csharp
using System;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.App.Tests.Measurement; // CircularMeshFixtures, GeoAssert
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed partial class ChamferDetectorTests
{
    private static MeshTopology BuildTopology((Vector3d[] v, int[] i) mesh, double creaseDeg = 28.0)
    {
        var cache = new MeshTopologyCache(capacity: 4);
        IMeshTopologyProvider provider = cache;
        return provider.GetOrBuild(
            meshKey: 1,
            transformVersion: 1,
            verticesLocal: mesh.v,
            indices: mesh.i,
            creaseAngleRad: creaseDeg * Math.PI / 180.0);
    }

    [Fact]
    public void Detect_WideFlatPlate_ReturnsNotOk()
    {
        (Vector3d[] v, int[] i) plate = CircularMeshFixtures.FlatPlate(size: 10.0);
        MeshTopology topo = BuildTopology(plate);
        int seed = CircularMeshFixtures.SeedTriangleAt(plate, new Vector3d(0, 0, 0));

        ChamferFit fit = ChamferDetector.Detect(topo, seed);

        Assert.False(fit.Ok);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~ChamferDetectorTests.Detect_WideFlatPlate"`
  Expected: the **stub** returns `Ok=false`, so this single test would actually *pass* against the stub — that is fine; it guards the negative once the real algorithm replaces the stub in this step. Run it first to confirm the harness/fixtures link (a *compile* failure here means Group A fixtures or Group B topology are not yet merged — resolve that before continuing). Then proceed to Step 3 which replaces the stub with the real band walk; re-run and confirm it stays green.

- [ ] **Step 3: Implement the band-detection skeleton.** Replace the stub body of `ChamferDetector` with a real algorithm: from the seed, BFS the **smooth-connected** region (the candidate chamfer face) bounded by crease/boundary edges, collect the **two bounding crease edge-sets**, and reject when fewer than two distinct, roughly-parallel bounding creases enclose a thin strip.

`[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs`:
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Detects a chamfer: a narrow planar/conical band of smooth-connected triangles
/// bounded by two near-parallel crease (or boundary) edge-sets. Width is the mean
/// perpendicular distance between the two bounding edge-sets; AngleDeg is the
/// dihedral between the chamfer face and the adjacent reference face across the
/// wider-angle bounding crease. A wide planar face (no two near-parallel bounding
/// creases enclosing a thin strip) returns Ok=false.
/// </summary>
public static class ChamferDetector
{
    // A band is "narrow" when its long extent is at least this multiple of its width.
    private const double MinAspectRatio = 2.0;
    // Two bounding edge-sets count as "the same pair" only if their mean directions
    // are roughly parallel (|dot| above this).
    private const double ParallelDot = 0.80;

    public static ChamferFit Detect(MeshTopology topo, int seedTriangle)
    {
        if (topo is null || seedTriangle < 0 || seedTriangle >= topo.Triangles.Count)
            return NotOk();
        if (!topo.Triangles[seedTriangle].Valid)
            return NotOk();

        // 1. Grow the smooth-connected face containing the seed; collect the
        //    crease/boundary edges that wall it in.
        List<int> bandTris = GrowSmoothFace(topo, seedTriangle, out List<EdgeRef> boundingEdges);
        if (bandTris.Count == 0 || boundingEdges.Count < 2)
            return NotOk();

        // 2. Partition the bounding edges into directional groups. A chamfer has
        //    exactly two dominant, near-parallel-to-each-other groups (the two long
        //    rails of the strip). End-cap creases are short and discarded.
        if (!TryFindTwoRails(topo, boundingEdges, out List<EdgeRef> railA, out List<EdgeRef> railB))
            return NotOk();

        // 3. Width = mean perpendicular distance from each railA vertex to the
        //    infinite line through railB's centroid along railB's direction.
        double width = MeanCrossRailDistance(topo, railA, railB);
        double railLen = Math.Max(RailExtent(topo, railA), RailExtent(topo, railB));
        if (width <= 1e-9 || railLen < MinAspectRatio * width)
            return NotOk(); // wide face, not a narrow band

        // 4. Angle = dihedral between the band face and the adjacent reference face
        //    across one bounding rail.
        Vector3d bandNormal = MeanNormal(topo, bandTris);
        double angleDeg = DihedralToReference(topo, railA, bandTris, bandNormal);
        if (double.IsNaN(angleDeg))
            return NotOk();

        Vector3d axis = MeanRailDirection(topo, railA);
        return new ChamferFit(width, angleDeg, axis, Rms: 0.0, Ok: true);
    }

    private static ChamferFit NotOk() => new(0.0, 0.0, Vector3d.Zero, 0.0, Ok: false);

    // BFS across SMOOTH edges only; any crease/boundary/non-manifold shared edge is
    // recorded as a bounding edge and not crossed.
    private static List<int> GrowSmoothFace(MeshTopology topo, int seed, out List<EdgeRef> bounding)
    {
        var visited = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        var region = new List<int> { seed };
        bounding = new List<EdgeRef>();

        while (queue.Count > 0)
        {
            int tri = queue.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                int edgeIdx = topo.TriangleEdgeIndices[tri * 3 + k];
                EdgeRef e = topo.Edges[edgeIdx];
                int other = e.TriLeft == tri ? e.TriRight : e.TriLeft;
                if (other < 0 || e.Class != EdgeClass.Smooth)
                {
                    bounding.Add(e); // crease, boundary, or non-manifold => a wall
                    continue;
                }
                if (visited.Add(other))
                {
                    region.Add(other);
                    queue.Enqueue(other);
                }
            }
        }

        return region;
    }

    // Group bounding edges by direction; keep the two largest groups and require
    // their mean directions to be roughly parallel to each other (the two rails of
    // a strip run the same way). Returns false if there are not two such rails.
    private static bool TryFindTwoRails(
        MeshTopology topo,
        IReadOnlyList<EdgeRef> bounding,
        out List<EdgeRef> railA,
        out List<EdgeRef> railB)
    {
        railA = new List<EdgeRef>();
        railB = new List<EdgeRef>();

        var groups = new List<(Vector3d Dir, List<EdgeRef> Edges)>();
        foreach (EdgeRef e in bounding)
        {
            Vector3d dir = (topo.Vertices[e.B] - topo.Vertices[e.A]).Normalized();
            if (dir.LengthSquared < 1e-18)
                continue;
            bool placed = false;
            for (int g = 0; g < groups.Count; g++)
            {
                if (Math.Abs(Vector3d.Dot(groups[g].Dir, dir)) >= ParallelDot)
                {
                    // align sign so the running mean does not cancel
                    if (Vector3d.Dot(groups[g].Dir, dir) < 0)
                        dir = -dir;
                    groups[g].Edges.Add(e);
                    Vector3d mean = (groups[g].Dir * groups[g].Edges.Count + dir).Normalized();
                    groups[g] = (mean, groups[g].Edges);
                    placed = true;
                    break;
                }
            }
            if (!placed)
                groups.Add((dir, new List<EdgeRef> { e }));
        }

        // Two rails of a chamfer share the SAME running direction, so they fall in
        // ONE direction group. Split that group into two spatial sides by signed
        // offset along the band-normal-perpendicular.
        groups.Sort((x, y) => y.Edges.Count.CompareTo(x.Edges.Count));
        if (groups.Count == 0 || groups[0].Edges.Count < 2)
            return false;

        List<EdgeRef> longest = groups[0].Edges;
        Vector3d railDir = groups[0].Dir;

        // Perpendicular axis in the band: separate edges by their projection onto a
        // vector orthogonal to railDir.
        Vector3d perp = Vector3d.BuildPerpendicular(railDir);
        var keyed = new List<(double Key, EdgeRef E)>(longest.Count);
        foreach (EdgeRef e in longest)
        {
            Vector3d mid = (topo.Vertices[e.A] + topo.Vertices[e.B]) * 0.5;
            keyed.Add((Vector3d.Dot(mid, perp), e));
        }
        keyed.Sort((x, y) => x.Key.CompareTo(y.Key));

        double lo = keyed[0].Key;
        double hi = keyed[^1].Key;
        double mid2 = (lo + hi) * 0.5;
        foreach ((double key, EdgeRef e) in keyed)
        {
            if (key <= mid2)
                railA.Add(e);
            else
                railB.Add(e);
        }

        return railA.Count > 0 && railB.Count > 0;
    }

    private static double MeanCrossRailDistance(MeshTopology topo, List<EdgeRef> railA, List<EdgeRef> railB)
    {
        // Reference line: centroid + direction of railB.
        Vector3d cB = RailCentroid(topo, railB);
        Vector3d dB = MeanRailDirection(topo, railB);
        double sum = 0.0;
        int n = 0;
        foreach (EdgeRef e in railA)
        {
            sum += PointLineDistance(topo.Vertices[e.A], cB, dB);
            sum += PointLineDistance(topo.Vertices[e.B], cB, dB);
            n += 2;
        }
        return n == 0 ? 0.0 : sum / n;
    }

    private static double PointLineDistance(Vector3d p, Vector3d linePoint, Vector3d lineDir)
    {
        Vector3d w = p - linePoint;
        Vector3d perp = w - lineDir * Vector3d.Dot(w, lineDir);
        return perp.Length;
    }

    private static double RailExtent(MeshTopology topo, List<EdgeRef> rail)
    {
        Vector3d dir = MeanRailDirection(topo, rail);
        double min = double.MaxValue, max = double.MinValue;
        foreach (EdgeRef e in rail)
        {
            foreach (int idx in new[] { e.A, e.B })
            {
                double t = Vector3d.Dot(topo.Vertices[idx], dir);
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }
        return max <= min ? 0.0 : max - min;
    }

    private static Vector3d RailCentroid(MeshTopology topo, List<EdgeRef> rail)
    {
        Vector3d sum = Vector3d.Zero;
        int n = 0;
        foreach (EdgeRef e in rail)
        {
            sum = sum + topo.Vertices[e.A] + topo.Vertices[e.B];
            n += 2;
        }
        return n == 0 ? Vector3d.Zero : sum * (1.0 / n);
    }

    private static Vector3d MeanRailDirection(MeshTopology topo, List<EdgeRef> rail)
    {
        Vector3d acc = Vector3d.Zero;
        foreach (EdgeRef e in rail)
        {
            Vector3d d = (topo.Vertices[e.B] - topo.Vertices[e.A]).Normalized();
            if (acc.LengthSquared > 0 && Vector3d.Dot(acc, d) < 0)
                d = -d;
            acc = acc + d;
        }
        return acc.LengthSquared < 1e-18 ? Vector3d.UnitZ : acc.Normalized();
    }

    private static Vector3d MeanNormal(MeshTopology topo, List<int> tris)
    {
        Vector3d acc = Vector3d.Zero;
        foreach (int t in tris)
            acc = acc + topo.Triangles[t].Normal * topo.Triangles[t].Area;
        return acc.LengthSquared < 1e-18 ? Vector3d.UnitZ : acc.Normalized();
    }

    // Dihedral between the band face and the reference face on the far side of railA.
    private static double DihedralToReference(
        MeshTopology topo,
        List<EdgeRef> railA,
        List<int> bandTris,
        Vector3d bandNormal)
    {
        var band = new HashSet<int>(bandTris);
        Vector3d refAcc = Vector3d.Zero;
        foreach (EdgeRef e in railA)
        {
            int outside =
                e.TriLeft >= 0 && !band.Contains(e.TriLeft) ? e.TriLeft :
                e.TriRight >= 0 && !band.Contains(e.TriRight) ? e.TriRight : -1;
            if (outside < 0 || !topo.Triangles[outside].Valid)
                continue;
            Vector3d n = topo.Triangles[outside].Normal;
            if (refAcc.LengthSquared > 0 && Vector3d.Dot(refAcc, n) < 0)
                n = -n;
            refAcc = refAcc + n;
        }
        if (refAcc.LengthSquared < 1e-18)
            return double.NaN;

        Vector3d refNormal = refAcc.Normalized();
        double cos = Math.Clamp(Vector3d.Dot(bandNormal, refNormal), -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~ChamferDetectorTests.Detect_WideFlatPlate"`
  Green (`bounding.Count < 2` or aspect-ratio gate rejects the flat plate).

- [ ] **Step 5: Commit (PARENT repo).**
git add src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs src/FabricationAssistant.App.Tests/Measurement/ChamferDetectorTests.cs
git commit -m "feat(measure): ChamferDetector band walk; flat plate rejected (Ok=false)"

---

### Task G.3: Positive case — `Chamfer(width=2, angle=45)` ⇒ `Width≈2`, `AngleDeg≈45`

**Files:**
- Modify (only if a fix is needed) `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs`
- Test `[PARENT] src/FabricationAssistant.App.Tests/Measurement/ChamferDetectorTests.cs` (same partial class as G.2)

> The `CircularMeshFixtures.Chamfer(width, angleDeg, length)` fixture (Group A) builds a flat strip of `width` across, `length` along, broken at `angleDeg` between the chamfer face and its two adjacent reference faces, with creases on both long rails. The detector built in G.2 should already produce the right numbers; this task proves it and tightens the algorithm if the assertions miss.

- [ ] **Step 1: Write the failing test.** Add to the existing `ChamferDetectorTests` partial class. Seed in the middle of the chamfer band, assert numeric Width and Angle, and assert the axis is parallel to the chamfer's long edge.

```csharp
    [Fact]
    public void Detect_Chamfer_Width2_Angle45_ReturnsWidth2AndAngle45()
    {
        (Vector3d[] v, int[] i) mesh =
            CircularMeshFixtures.Chamfer(width: 2.0, angleDeg: 45.0, length: 20.0);
        MeshTopology topo = BuildTopology(mesh);
        // Mid-point of the chamfer band along its length, centred across its width.
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(0, 0, 10.0));

        ChamferFit fit = ChamferDetector.Detect(topo, seed);

        Assert.True(fit.Ok);
        Assert.Equal(2.0, fit.Width, 1);      // ~2, 1 dp tolerance for tessellation
        Assert.Equal(45.0, fit.AngleDeg, 0);  // ~45 degrees, integer tolerance
    }

    [Fact]
    public void Detect_Chamfer_AxisParallelToLongEdge()
    {
        (Vector3d[] v, int[] i) mesh =
            CircularMeshFixtures.Chamfer(width: 2.0, angleDeg: 45.0, length: 20.0);
        MeshTopology topo = BuildTopology(mesh);
        int seed = CircularMeshFixtures.SeedTriangleAt(mesh, new Vector3d(0, 0, 10.0));

        ChamferFit fit = ChamferDetector.Detect(topo, seed);

        Assert.True(fit.Ok);
        // Chamfer fixture runs its length along +Z; axis tracks the long rails.
        GeoAssert.AxisParallel(fit.Axis, Vector3d.UnitZ, minAbsDot: 0.99);
    }
```

- [ ] **Step 2: Run it, expect FAIL (or confirm the gap).**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~ChamferDetectorTests.Detect_Chamfer"`
  Expected first failure: if the fixture's long axis is not exactly `UnitZ` or the rail split picks short end-cap creases, `Width` or `AngleDeg` will be off — likeliest `Assert.Equal(2.0, fit.Width, 1)` failing because end-cap (short, transverse) bounding edges contaminate the rail direction group. If instead it already passes, record that and skip the Step 3 edit.

- [ ] **Step 3: Implement / tighten.** Make the rail picker robust to short end-cap creases by **dropping bounding-edge direction groups whose total length is small relative to the longest group**, so only the two long rails survive. Apply this edit inside `TryFindTwoRails`, right after `groups.Sort(...)` and before the `groups[0].Edges.Count < 2` guard.

In `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs`, replace:
```csharp
        groups.Sort((x, y) => y.Edges.Count.CompareTo(x.Edges.Count));
        if (groups.Count == 0 || groups[0].Edges.Count < 2)
            return false;

        List<EdgeRef> longest = groups[0].Edges;
        Vector3d railDir = groups[0].Dir;
```
with:
```csharp
        // Rank groups by total edge LENGTH (not count) so the two long rails win
        // over numerous short end-cap creases.
        double GroupLength(List<EdgeRef> edges)
        {
            double s = 0.0;
            foreach (EdgeRef e in edges)
                s += (topo.Vertices[e.B] - topo.Vertices[e.A]).Length;
            return s;
        }
        groups.Sort((x, y) => GroupLength(y.Edges).CompareTo(GroupLength(x.Edges)));
        if (groups.Count == 0 || groups[0].Edges.Count < 2)
            return false;

        // The chamfer's two long rails run the same direction => same group.
        List<EdgeRef> longest = groups[0].Edges;
        Vector3d railDir = groups[0].Dir;
```

(No other change is needed: `MeanCrossRailDistance` already computes the perpendicular gap between the two spatially-split sub-rails, and `DihedralToReference` measures band-vs-reference. If the angle reads `135` instead of `45`, the reference normal sign was flipped — already handled by the `Vector3d.Dot(refAcc, n) < 0` sign-alignment in `DihedralToReference`, which keeps the dihedral in `[0,180]`; the chamfer-vs-flat-face break of `45°` produces a `45°` normal-to-normal angle, matching the test.)

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~ChamferDetectorTests"`
  All four facts (G.2 flat-plate + G.3 width/angle + axis) green.

- [ ] **Step 5: Commit (PARENT repo).**
git add src/FabricationAssistant.Core/Measurement/Engine/ChamferDetector.cs src/FabricationAssistant.App.Tests/Measurement/ChamferDetectorTests.cs
git commit -m "feat(measure): ChamferDetector reports width+angle on narrow band (Ø2x45 fixture)"

---

### Task G.4: Android mirror test for ChamferDetector

**Files:**
- Modify (if needed) `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Test `[ANDROID] src/FabricationAssistant.App.Android.Tests/ChamferDetectorAndroidTests.cs`

> The Android test project only selectively links Core source. Mirror the key positive + negative chamfer cases so the Android suite has detection coverage (closes the "zero Android detection tests" gap, spec §4.3 / §13).

- [ ] **Step 1: Write the failing test.** This mirrors G.2/G.3. The Android test project does not have the host `CircularMeshFixtures`; this group is allowed to define a tiny local chamfer-strip builder so the Android test is self-contained.

`[ANDROID] src/FabricationAssistant.App.Android.Tests/ChamferDetectorAndroidTests.cs`:
```csharp
using System;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class ChamferDetectorAndroidTests
{
    [Fact]
    public void Detect_Chamfer_Width2_Angle45_ReturnsWidthAndAngle()
    {
        (Vector3d[] v, int[] i) mesh = BuildChamferStrip(width: 2.0, angleDeg: 45.0, length: 20.0);
        MeshTopology topo = BuildTopology(mesh);
        int seed = MidBandSeed(mesh);

        ChamferFit fit = ChamferDetector.Detect(topo, seed);

        Assert.True(fit.Ok);
        Assert.Equal(2.0, fit.Width, 1);
        Assert.Equal(45.0, fit.AngleDeg, 0);
    }

    [Fact]
    public void Detect_WideFlatPlate_ReturnsNotOk()
    {
        Vector3d[] v =
        {
            new(-5, -5, 0), new(5, -5, 0), new(5, 5, 0), new(-5, 5, 0),
        };
        int[] i = { 0, 1, 2, 0, 2, 3 };
        MeshTopology topo = BuildTopology((v, i));

        ChamferFit fit = ChamferDetector.Detect(topo, 0);

        Assert.False(fit.Ok);
    }

    private static MeshTopology BuildTopology((Vector3d[] v, int[] i) mesh)
    {
        IMeshTopologyProvider provider = new MeshTopologyCache(capacity: 4);
        return provider.GetOrBuild(
            meshKey: 1,
            transformVersion: 1,
            verticesLocal: mesh.v,
            indices: mesh.i,
            creaseAngleRad: 28.0 * Math.PI / 180.0);
    }

    // Three coplanar/angled strips along +Z: a left reference face (XY plane),
    // the chamfer band at angleDeg, and a right reference face. The band spans
    // `width` across and `length` along Z. Two long creases bound the band.
    private static (Vector3d[] v, int[] i) BuildChamferStrip(double width, double angleDeg, double length)
    {
        double a = angleDeg * Math.PI / 180.0;
        // band goes from origin up the slope; left flat face on -X, right on +X side.
        double dx = Math.Cos(a) * width;
        double dy = Math.Sin(a) * width;

        // cross-section points (in XY), extruded along Z by `length`.
        Vector3d[] cross =
        {
            new(-3, 0, 0),    // left flat outer
            new(0, 0, 0),     // crease 1 (band start)
            new(dx, dy, 0),   // crease 2 (band end)
            new(dx + 3, dy, 0) // right flat outer
        };

        var verts = new Vector3d[cross.Length * 2];
        for (int s = 0; s < cross.Length; s++)
        {
            verts[s] = new Vector3d(cross[s].X, cross[s].Y, 0);
            verts[cross.Length + s] = new Vector3d(cross[s].X, cross[s].Y, length);
        }

        var idx = new System.Collections.Generic.List<int>();
        for (int s = 0; s < cross.Length - 1; s++)
        {
            int b0 = s;
            int b1 = s + 1;
            int t0 = cross.Length + s;
            int t1 = cross.Length + s + 1;
            idx.AddRange(new[] { b0, b1, t1, b0, t1, t0 });
        }
        return (verts, idx.ToArray());
    }

    // The band is the middle strip (cross-section segment 1->2); its two triangles
    // are quad index 1 => triangles 2 and 3.
    private static int MidBandSeed((Vector3d[] v, int[] i) mesh) => 2;
}
```

- [ ] **Step 2: Run it, expect FAIL.** From the PARENT root:
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ChamferDetectorAndroid"`
  Expected failure: compile error `CS0246` for `ChamferDetector` / `ChamferFit` / `MeshTopologyCache` because the Android test csproj does not yet `<Compile Include>` those Core files.

- [ ] **Step 3: Implement — link the Core sources into the Android test csproj.** Add explicit `<Compile Include>` items (only those not already linked) inside the existing `<ItemGroup>` that links Core source in `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`. Use the project's existing relative path style to the parent Core (verify the existing prefix in the file first; below assumes the established `..\..\..\..\src\...` shape):
```xml
    <Compile Include="..\..\..\..\src\FabricationAssistant.Core\Measurement\Engine\ChamferFit.cs">
      <Link>Linked\ChamferFit.cs</Link>
    </Compile>
    <Compile Include="..\..\..\..\src\FabricationAssistant.Core\Measurement\Engine\ChamferDetector.cs">
      <Link>Linked\ChamferDetector.cs</Link>
    </Compile>
```
If `MeshTopology`, `MeshTopologyCache`, `EdgeRef`, `TriData`, `EdgeClass` (Group B) are not already linked by Group B's csproj edit, add their `<Compile Include>` entries the same way. Then `dotnet build-server shutdown` (avoids a stale-link CS2012) before re-running.

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ChamferDetectorAndroid"`
  Both facts green.

- [ ] **Step 5: Commit (ANDROID repo).** Commit from inside the nested Android repo, staging by explicit path:
git -C Android add src/FabricationAssistant.App.Android.Tests/ChamferDetectorAndroidTests.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
git -C Android commit -m "test(measure): Android mirror coverage for ChamferDetector (width/angle + flat-plate reject)"

---


## Group H — FitAcceptanceGate + PickFeedbackAndCommitGuard

> **Preamble (read once).** All new acceptance/guard types are pure Core types in namespace `FabricationAssistant.Core.Measurement.Engine` under `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/`. Files placed under `src/FabricationAssistant.Core/Measurement/**` are auto compile-linked into Android via a wildcard, so no Android csproj edit is needed *for the Core types themselves* — but the **Android test project links Core selectively**, so when an Android mirror test needs a Core type that isn't yet linked, you must add an explicit `<Compile Include>` to `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (called out as a step where it applies).
>
> Group H consumes contract types owned by other groups: `SurfaceFit`/`SurfaceKind` (Group E) and `CircularFeaturePick` (PickTypes, extended in Group I with `Kind`/`GeometricRms`/`Confidence`/`TubeRadius`). Group H **defines** `AcceptanceOptions`, `RejectReason`, `AcceptanceResult`, `FitAcceptanceGate`, and `PickFeedbackAndCommitGuard`. It also **modifies** `MeasurementSession.AdvanceCircularFeature` and `MeasurementBuilders.BuildCircularFeature` (both PARENT).
>
> **Repo discipline:** Core + host-test changes commit in the **PARENT** repo (`C:/Users/skritikos/Desktop/Fabrication Assistant`). Android-test-project changes commit in the **ANDROID** repo (`C:/Users/skritikos/Desktop/Fabrication Assistant/Android`), staging by explicit path.
>
> **Build-lock note (once):** if a build fails with CSC `CS2012` / a file-lock on a DLL, run `dotnet build-server shutdown` then retry; if a named `.NET Host (PID)` still holds the DLL, `Stop-Process -Id <pid> -Force`.
>
> **Ordering note:** Group H's gate/result types do not depend on Group E's *implementation*, only on the `SurfaceFit`/`SurfaceKind` record/enum declarations. If Group E has not yet landed those declarations when you start, add the two declarations (exactly as in the canonical contracts) in their Group-E file first and let Group E's tasks own their fitter logic — do **not** duplicate them.

---

### Task H.1: `AcceptanceOptions` + `RejectReason` + `AcceptanceResult` contract types

**Files:**
- Create: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/FitAcceptanceContractTests.cs`

- [ ] **Step 1: Write the failing test.** This locks the record shapes and default-construction so downstream groups bind to a stable contract.

```csharp
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class FitAcceptanceContractTests
{
    [Fact]
    public void AcceptanceOptions_StoresThresholds_AndExposesThem()
    {
        var opts = new AcceptanceOptions(
            MaxRmsOverR: 0.015,
            MinInlierFraction: 0.90,
            MaxRadiusFraction: 0.5);

        Assert.Equal(0.015, opts.MaxRmsOverR, 6);
        Assert.Equal(0.90, opts.MinInlierFraction, 6);
        Assert.Equal(0.5, opts.MaxRadiusFraction, 6);
    }

    [Fact]
    public void RejectReason_HasAllSixMembers_WithNoneAsDefault()
    {
        Assert.Equal(RejectReason.None, default(RejectReason));
        Assert.Equal(0, (int)RejectReason.None);
        // Enum surface is exactly these six members.
        Assert.Equal(
            new[]
            {
                RejectReason.None,
                RejectReason.NoFeatureHere,
                RejectReason.LowConfidence,
                RejectReason.RadiusOutOfBounds,
                RejectReason.MissedModel,
            },
            new[]
            {
                RejectReason.None,
                RejectReason.NoFeatureHere,
                RejectReason.LowConfidence,
                RejectReason.RadiusOutOfBounds,
                RejectReason.MissedModel,
            });
    }

    [Fact]
    public void AcceptanceResult_RoundTripsAllFourFields()
    {
        var r = new AcceptanceResult(
            Accepted: true,
            Reason: RejectReason.None,
            RmsOverR: 0.004,
            InlierFraction: 0.97);

        Assert.True(r.Accepted);
        Assert.Equal(RejectReason.None, r.Reason);
        Assert.Equal(0.004, r.RmsOverR, 6);
        Assert.Equal(0.97, r.InlierFraction, 6);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceContract"`
  Expected failure: **compile error CS0246** — `AcceptanceOptions`, `RejectReason`, `AcceptanceResult` not found.

- [ ] **Step 3: Implement** the contract file. (Note: `FitAcceptanceGate` and `PickFeedbackAndCommitGuard` will be added to this same file in later tasks; create just the contract types now.)

```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Scale-invariant acceptance thresholds for a fitted circular surface.
/// Defaults (spec §9): MaxRmsOverR=1.5%, MinInlierFraction=90%, MaxRadiusFraction=0.5.
/// </summary>
public sealed record AcceptanceOptions(
    double MaxRmsOverR,
    double MinInlierFraction,
    double MaxRadiusFraction)
{
    public static AcceptanceOptions Default { get; } =
        new(MaxRmsOverR: 0.015, MinInlierFraction: 0.90, MaxRadiusFraction: 0.5);
}

/// <summary>
/// Why a circular-feature pick was rejected. <see cref="None"/> is the accepted/default state.
/// </summary>
public enum RejectReason
{
    None = 0,
    NoFeatureHere,
    LowConfidence,
    RadiusOutOfBounds,
    MissedModel,
}

/// <summary>
/// Result of <see cref="FitAcceptanceGate.Evaluate"/>. When <see cref="Accepted"/> is true,
/// <see cref="Reason"/> is <see cref="RejectReason.None"/>.
/// </summary>
public readonly record struct AcceptanceResult(
    bool Accepted,
    RejectReason Reason,
    double RmsOverR,
    double InlierFraction);
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceContract"`
  Expected: 3 passed.

- [ ] **Step 5: Commit** (PARENT repo).

```bash
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs src/FabricationAssistant.App.Tests/Measurement/FitAcceptanceContractTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): add FitAcceptance contract types (options/reason/result)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task H.2: `FitAcceptanceGate.Evaluate` — RMS/R + inlier-fraction + radius-bound gate

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/FitAcceptanceGateTests.cs`

The gate accepts iff **all** hold (spec §6.8 / §9):
1. `fit.GeometricRms / fit.Radius ≤ opts.MaxRmsOverR`,
2. per-triangle inlier fraction `≥ opts.MinInlierFraction`, where a region point is an inlier iff `|distFromAxis(point) − R| ≤ k·chord(R)` with the membership tolerance `k·chord(R) = MembershipChordK · R · (1 − cos(π/segApprox))`. To keep the gate self-contained and dependency-free we use a fixed, scale-invariant inlier band of `2% of R` (i.e. residual ≤ `0.02·R`), which matches the spec's "deviation small relative to R" intent and is independent of tessellation count,
3. `fit.Radius ≤ opts.MaxRadiusFraction · bodyExtent`.

Reason precedence when rejected: **RadiusOutOfBounds** is checked first (it is an unconditional sanity bound and the strongest signal of a leaked/garbage fit), then **LowConfidence** (covers both bad RMS/R and low inlier fraction). A non-`Ok` fit, an empty region, or `R ≤ 0` ⇒ `LowConfidence`.

- [ ] **Step 1: Write the failing test.** Three numeric fixtures: a clean cylinder patch accepts; a degenerate tiny-radius corner fan rejects `LowConfidence`; an oversized leaked circle rejects `RadiusOutOfBounds`.

```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class FitAcceptanceGateTests
{
    // Ring of n points at exact radius r about the Z axis through origin, at z=0.
    private static List<Vector3d> Ring(double r, int n, double radialNoise = 0.0)
    {
        var pts = new List<Vector3d>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2.0 * Math.PI * i / n;
            double rr = r + (i % 2 == 0 ? radialNoise : -radialNoise);
            pts.Add(new Vector3d(rr * Math.Cos(a), rr * Math.Sin(a), 0.0));
        }
        return pts;
    }

    [Fact]
    public void Evaluate_CleanCylinderFit_Accepts()
    {
        // R=5, tight RMS, points sit on the circle => inlier fraction = 1.
        List<Vector3d> region = Ring(5.0, 64, radialNoise: 0.0);
        var fit = new SurfaceFit(
            Kind: SurfaceKind.Cylinder,
            Axis: Vector3d.UnitZ,
            Center: new Vector3d(0, 0, 0),
            Radius: 5.0,
            TubeRadius: 0.0,
            GeometricRms: 0.01,   // RmsOverR = 0.002 <= 0.015
            Ok: true);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(
            region, fit, bodyExtent: 40.0, AcceptanceOptions.Default);

        Assert.True(res.Accepted);
        Assert.Equal(RejectReason.None, res.Reason);
        Assert.Equal(0.002, res.RmsOverR, 4);
        Assert.Equal(1.0, res.InlierFraction, 4);
    }

    [Fact]
    public void Evaluate_DegenerateTinyCornerFan_RejectsLowConfidence()
    {
        // Reproduces tap-3 false success: robust fit collapsed to R=0.004 on a corner
        // vertex-fan whose points are NOT on a circle of that radius (huge residuals
        // relative to R) => low inlier fraction AND blown RmsOverR.
        var region = new List<Vector3d>
        {
            new(0.004, 0.000, 0.0),
            new(0.050, 0.012, 0.0),   // far outside the 0.004 circle
            new(0.030, -0.040, 0.0),
            new(0.080, 0.005, 0.0),
            new(0.001, 0.001, 0.0),
        };
        var fit = new SurfaceFit(
            Kind: SurfaceKind.Cylinder,
            Axis: Vector3d.UnitZ,
            Center: new Vector3d(0, 0, 0),
            Radius: 0.004,
            TubeRadius: 0.0,
            GeometricRms: 0.035,   // RmsOverR = 8.75 >> 0.015
            Ok: true);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(
            region, fit, bodyExtent: 0.5, AcceptanceOptions.Default);

        Assert.False(res.Accepted);
        Assert.Equal(RejectReason.LowConfidence, res.Reason);
        Assert.True(res.InlierFraction < 0.90,
            $"expected inlier fraction below 0.90, got {res.InlierFraction}");
    }

    [Fact]
    public void Evaluate_OversizedLeakedCircle_RejectsRadiusOutOfBounds()
    {
        // Reproduces tap-1 leak: a big leaked circle whose RMS/R looks fine in isolation,
        // but R exceeds 0.5 * bodyExtent. Radius bound must fire FIRST.
        List<Vector3d> region = Ring(30.0, 64, radialNoise: 0.0);
        var fit = new SurfaceFit(
            Kind: SurfaceKind.Cylinder,
            Axis: Vector3d.UnitZ,
            Center: new Vector3d(0, 0, 0),
            Radius: 30.0,
            TubeRadius: 0.0,
            GeometricRms: 0.05,    // RmsOverR ~ 0.0017, would pass RMS gate alone
            Ok: true);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(
            region, fit, bodyExtent: 40.0, AcceptanceOptions.Default); // 0.5*40=20 < 30

        Assert.False(res.Accepted);
        Assert.Equal(RejectReason.RadiusOutOfBounds, res.Reason);
    }

    [Fact]
    public void Evaluate_NotOkFit_RejectsLowConfidence()
    {
        var fit = new SurfaceFit(
            SurfaceKind.Unknown, Vector3d.UnitZ, default, 0.0, 0.0, 0.0, Ok: false);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(
            new List<Vector3d> { new(1, 0, 0) }, fit, bodyExtent: 10.0, AcceptanceOptions.Default);

        Assert.False(res.Accepted);
        Assert.Equal(RejectReason.LowConfidence, res.Reason);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceGateTests"`
  Expected failure: **CS0103/CS0117** — `FitAcceptanceGate` does not exist.

- [ ] **Step 3: Implement** by appending the static gate to `FitAcceptance.cs`. The radial-distance helper handles a possibly-unnormalized axis and projects each point onto the plane through `Center` perpendicular to `Axis`.

```csharp
public static class FitAcceptanceGate
{
    // Inlier band: a region point is an inlier iff its radial residual to the fitted
    // circle is within this fraction of the radius. Scale-invariant, tessellation-
    // independent. Matches spec §6.8 "deviation small relative to R".
    private const double InlierBandFractionOfR = 0.02;

    public static AcceptanceResult Evaluate(
        IReadOnlyList<Vector3d> regionPoints,
        SurfaceFit fit,
        double bodyExtent,
        AcceptanceOptions opts)
    {
        // Hard guards: an unusable fit can never be accepted.
        if (!fit.Ok || fit.Radius <= 0.0 || regionPoints is null || regionPoints.Count == 0)
            return new AcceptanceResult(false, RejectReason.LowConfidence, double.PositiveInfinity, 0.0);

        double r = fit.Radius;

        // (3) Absolute radius sanity bound — checked first; strongest leak/garbage signal.
        double maxRadius = opts.MaxRadiusFraction * System.Math.Max(0.0, bodyExtent);
        bool radiusOk = r <= maxRadius;

        // (1) Scale-invariant residual.
        double rmsOverR = fit.GeometricRms / r;

        // (2) Per-triangle (per-point) inlier fraction against the fitted circle.
        double band = InlierBandFractionOfR * r;
        Vector3d axis = SafeAxis(fit.Axis);
        int inliers = 0;
        for (int i = 0; i < regionPoints.Count; i++)
        {
            double dist = RadialDistance(regionPoints[i], fit.Center, axis);
            if (System.Math.Abs(dist - r) <= band)
                inliers++;
        }
        double inlierFraction = (double)inliers / regionPoints.Count;

        bool rmsOk = rmsOverR <= opts.MaxRmsOverR;
        bool inlierOk = inlierFraction >= opts.MinInlierFraction;

        if (!radiusOk)
            return new AcceptanceResult(false, RejectReason.RadiusOutOfBounds, rmsOverR, inlierFraction);

        if (!rmsOk || !inlierOk)
            return new AcceptanceResult(false, RejectReason.LowConfidence, rmsOverR, inlierFraction);

        return new AcceptanceResult(true, RejectReason.None, rmsOverR, inlierFraction);
    }

    private static Vector3d SafeAxis(Vector3d axis)
    {
        double len = axis.Length();
        return len > 1e-12 ? axis * (1.0 / len) : Vector3d.UnitZ;
    }

    // Perpendicular distance from point to the axis line through center.
    private static double RadialDistance(Vector3d point, Vector3d center, Vector3d axisUnit)
    {
        Vector3d d = point - center;
        double along = Vector3d.Dot(d, axisUnit);
        Vector3d radial = d - axisUnit * along;
        return radial.Length();
    }
}
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceGateTests"`
  Expected: 4 passed.

- [ ] **Step 5: Commit** (PARENT repo).

```bash
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs src/FabricationAssistant.App.Tests/Measurement/FitAcceptanceGateTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): FitAcceptanceGate (rms/R + inlier fraction + radius bound)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task H.3: `PickFeedbackAndCommitGuard` — NoFeatureHere vs MissedModel + center/radius validation + value recompute

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/PickFeedbackAndCommitGuardTests.cs`

This guard is the single decision point between "we have a fit" and "commit a measurement". It must:
- distinguish **MissedModel** (the ray hit nothing — `regionPoints` empty / `fit` not Ok / no hit flag) from **NoFeatureHere** (the ray hit the model but the gate rejected it for `LowConfidence`),
- validate **center within patch extent**: the fitted `Center`, projected onto the fit plane, must lie within the radial span of the region (no center floating outside the patch — a classic leaked/degenerate symptom),
- validate **radius bound** (delegated to the gate's `RadiusOutOfBounds`),
- **recompute the reported value from the accepted fit** (diameter = `2R` for closed, radius = `R` for open) rather than trusting a verbatim pass-through.

Contract (Group H-owned):

```csharp
public readonly record struct CommitDecision(
    bool Commit,
    RejectReason Reason,
    double ReportedValue,                 // recomputed: 2R (closed) or R (open)
    CircularFeatureDimensionKind Kind);   // Diameter | Radius
```

- [ ] **Step 1: Write the failing test.** A clean closed fit commits with recomputed `2R`; a degenerate low-inlier fit returns `Commit=false, NoFeatureHere`; a true miss returns `MissedModel`; a center-outside-patch fit returns `NoFeatureHere`.

```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class PickFeedbackAndCommitGuardTests
{
    private static List<Vector3d> Ring(double r, int n)
    {
        var pts = new List<Vector3d>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2.0 * Math.PI * i / n;
            pts.Add(new Vector3d(r * Math.Cos(a), r * Math.Sin(a), 0.0));
        }
        return pts;
    }

    [Fact]
    public void Evaluate_CleanClosedHole_Commits_WithRecomputedDiameter()
    {
        List<Vector3d> region = Ring(2.0, 48);
        var fit = new SurfaceFit(
            SurfaceKind.Cylinder, Vector3d.UnitZ, new Vector3d(0, 0, 0),
            Radius: 2.0, TubeRadius: 0.0, GeometricRms: 0.005, Ok: true);

        CommitDecision d = PickFeedbackAndCommitGuard.Evaluate(
            hitModel: true,
            isClosed: true,
            regionPoints: region,
            fit: fit,
            bodyExtent: 20.0,
            opts: AcceptanceOptions.Default);

        Assert.True(d.Commit);
        Assert.Equal(RejectReason.None, d.Reason);
        Assert.Equal(CircularFeatureDimensionKind.Diameter, d.Kind);
        Assert.Equal(4.0, d.ReportedValue, 4);   // recomputed 2R, NOT verbatim
    }

    [Fact]
    public void Evaluate_OpenFillet_Commits_WithRecomputedRadius()
    {
        List<Vector3d> region = Ring(2.0, 48);
        var fit = new SurfaceFit(
            SurfaceKind.Cylinder, Vector3d.UnitZ, new Vector3d(0, 0, 0),
            Radius: 2.0, TubeRadius: 0.0, GeometricRms: 0.005, Ok: true);

        CommitDecision d = PickFeedbackAndCommitGuard.Evaluate(
            hitModel: true, isClosed: false, regionPoints: region,
            fit: fit, bodyExtent: 20.0, opts: AcceptanceOptions.Default);

        Assert.True(d.Commit);
        Assert.Equal(CircularFeatureDimensionKind.Radius, d.Kind);
        Assert.Equal(2.0, d.ReportedValue, 4);   // recomputed R
    }

    [Fact]
    public void Evaluate_DegenerateLowConfidence_DoesNotCommit_NoFeatureHere()
    {
        // tap-3 corner fan: hit the model, but fit is garbage => NoFeatureHere, no commit.
        var region = new List<Vector3d>
        {
            new(0.004, 0.000, 0.0),
            new(0.050, 0.012, 0.0),
            new(0.030, -0.040, 0.0),
            new(0.080, 0.005, 0.0),
            new(0.001, 0.001, 0.0),
        };
        var fit = new SurfaceFit(
            SurfaceKind.Cylinder, Vector3d.UnitZ, new Vector3d(0, 0, 0),
            Radius: 0.004, TubeRadius: 0.0, GeometricRms: 0.035, Ok: true);

        CommitDecision d = PickFeedbackAndCommitGuard.Evaluate(
            hitModel: true, isClosed: true, regionPoints: region,
            fit: fit, bodyExtent: 0.5, opts: AcceptanceOptions.Default);

        Assert.False(d.Commit);
        Assert.Equal(RejectReason.NoFeatureHere, d.Reason);
    }

    [Fact]
    public void Evaluate_OversizedLeak_DoesNotCommit_RadiusOutOfBounds()
    {
        List<Vector3d> region = Ring(30.0, 48);
        var fit = new SurfaceFit(
            SurfaceKind.Cylinder, Vector3d.UnitZ, new Vector3d(0, 0, 0),
            Radius: 30.0, TubeRadius: 0.0, GeometricRms: 0.05, Ok: true);

        CommitDecision d = PickFeedbackAndCommitGuard.Evaluate(
            hitModel: true, isClosed: true, regionPoints: region,
            fit: fit, bodyExtent: 40.0, opts: AcceptanceOptions.Default);

        Assert.False(d.Commit);
        Assert.Equal(RejectReason.RadiusOutOfBounds, d.Reason);
    }

    [Fact]
    public void Evaluate_NoHit_DoesNotCommit_MissedModel()
    {
        CommitDecision d = PickFeedbackAndCommitGuard.Evaluate(
            hitModel: false, isClosed: true,
            regionPoints: new List<Vector3d>(),
            fit: new SurfaceFit(SurfaceKind.Unknown, Vector3d.UnitZ, default, 0, 0, 0, Ok: false),
            bodyExtent: 20.0, opts: AcceptanceOptions.Default);

        Assert.False(d.Commit);
        Assert.Equal(RejectReason.MissedModel, d.Reason);
    }

    [Fact]
    public void Evaluate_CenterOutsidePatchExtent_DoesNotCommit_NoFeatureHere()
    {
        // Region is a small arc near +X (r=2), but the fit center is parked far away
        // at (10,0,0): center does NOT lie within the patch's radial span => reject.
        var region = new List<Vector3d>
        {
            new(2.0, 0.0, 0.0),
            new(1.9, 0.4, 0.0),
            new(1.9, -0.4, 0.0),
        };
        var fit = new SurfaceFit(
            SurfaceKind.Cylinder, Vector3d.UnitZ, new Vector3d(10, 0, 0),
            Radius: 2.0, TubeRadius: 0.0, GeometricRms: 0.005, Ok: true);

        CommitDecision d = PickFeedbackAndCommitGuard.Evaluate(
            hitModel: true, isClosed: false, regionPoints: region,
            fit: fit, bodyExtent: 40.0, opts: AcceptanceOptions.Default);

        Assert.False(d.Commit);
        Assert.Equal(RejectReason.NoFeatureHere, d.Reason);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~PickFeedbackAndCommitGuard"`
  Expected failure: **CS0246/CS0103** — `PickFeedbackAndCommitGuard` and `CommitDecision` not found.

- [ ] **Step 3: Implement** by appending to `FitAcceptance.cs`. The center-in-patch test computes the region's centroid and max radial span on the fit plane, then requires the fitted center to lie within that span (with a small margin) so a center "parked" outside the patch is rejected.

```csharp
public readonly record struct CommitDecision(
    bool Commit,
    RejectReason Reason,
    double ReportedValue,
    CircularFeatureDimensionKind Kind);

public static class PickFeedbackAndCommitGuard
{
    // The fitted center must sit inside the patch's in-plane bounding disc, expanded by
    // this fraction of the patch span, else it is a floating (leaked/degenerate) center.
    private const double CenterMarginFractionOfSpan = 0.25;

    public static CommitDecision Evaluate(
        bool hitModel,
        bool isClosed,
        IReadOnlyList<Vector3d> regionPoints,
        SurfaceFit fit,
        double bodyExtent,
        AcceptanceOptions opts)
    {
        CircularFeatureDimensionKind kind = isClosed
            ? CircularFeatureDimensionKind.Diameter
            : CircularFeatureDimensionKind.Radius;

        // True miss: ray hit nothing usable.
        if (!hitModel || !fit.Ok || regionPoints is null || regionPoints.Count == 0)
            return new CommitDecision(false, RejectReason.MissedModel, 0.0, kind);

        // Quantitative acceptance (rms/R, inlier fraction, radius bound).
        AcceptanceResult gate = FitAcceptanceGate.Evaluate(regionPoints, fit, bodyExtent, opts);
        if (!gate.Accepted)
        {
            // Radius bound stays a distinct reason; everything else that hit the model but
            // failed the gate is surfaced to the user as "no clean feature here".
            RejectReason reason = gate.Reason == RejectReason.RadiusOutOfBounds
                ? RejectReason.RadiusOutOfBounds
                : RejectReason.NoFeatureHere;
            return new CommitDecision(false, reason, 0.0, kind);
        }

        // Center must lie within the patch's in-plane extent.
        if (!CenterWithinPatch(regionPoints, fit))
            return new CommitDecision(false, RejectReason.NoFeatureHere, 0.0, kind);

        // Recompute the reported value from the accepted analytic fit (no verbatim pass-through).
        double reported = isClosed ? fit.Radius * 2.0 : fit.Radius;
        return new CommitDecision(true, RejectReason.None, reported, kind);
    }

    private static bool CenterWithinPatch(IReadOnlyList<Vector3d> region, SurfaceFit fit)
    {
        Vector3d axis = SafeAxis(fit.Axis);

        // In-plane centroid of the region.
        Vector3d sum = default;
        for (int i = 0; i < region.Count; i++)
            sum = sum + ProjectToPlane(region[i], fit.Center, axis);
        Vector3d centroid = sum * (1.0 / region.Count);

        // Max in-plane distance from centroid to any region point = patch radial span.
        double span = 0.0;
        for (int i = 0; i < region.Count; i++)
        {
            Vector3d p = ProjectToPlane(region[i], fit.Center, axis);
            double d = (p - centroid).Length();
            if (d > span) span = d;
        }

        // The fitted center (projected to the same plane) must be inside the span (+margin).
        Vector3d centerInPlane = ProjectToPlane(fit.Center, fit.Center, axis); // = fit.Center
        double centerOffset = (centerInPlane - centroid).Length();
        double allowed = span * (1.0 + CenterMarginFractionOfSpan);
        return centerOffset <= allowed;
    }

    private static Vector3d ProjectToPlane(Vector3d p, Vector3d planePoint, Vector3d axisUnit)
    {
        Vector3d d = p - planePoint;
        double along = Vector3d.Dot(d, axisUnit);
        return p - axisUnit * along;
    }

    private static Vector3d SafeAxis(Vector3d axis)
    {
        double len = axis.Length();
        return len > 1e-12 ? axis * (1.0 / len) : Vector3d.UnitZ;
    }
}
```

> Note: `ProjectToPlane(fit.Center, fit.Center, axis)` returns `fit.Center` by construction; it is written this way so all three points are projected through the identical helper (no accidental plane-offset bug). The `CenterMarginFractionOfSpan` test in `Evaluate_CenterOutsidePatchExtent...` passes because the centroid of the small +X arc sits near `(1.93, 0, 0)`, the span is ~`0.4`, and `fit.Center=(10,0,0)` is ~`8` away — far outside `span·1.25`.

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~PickFeedbackAndCommitGuard"`
  Expected: 6 passed.

- [ ] **Step 5: Commit** (PARENT repo).

```bash
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs src/FabricationAssistant.App.Tests/Measurement/PickFeedbackAndCommitGuardTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): PickFeedbackAndCommitGuard (miss vs no-feature, center/radius validate, value recompute)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task H.4: Carry `GeometricRms` + `Confidence` on `CircularFeaturePick` so the session can guard

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularFeaturePickFieldsTests.cs`

`AdvanceCircularFeature` (next task) needs the fit residual and confidence to drive the guard. The canonical contract says `CircularFeaturePick` "gains via `with`: `SurfaceKind Kind, double GeometricRms, double Confidence, double TubeRadius`". Group I may add `Kind`/`TubeRadius`; **this task adds only `GeometricRms` and `Confidence`** (idempotent — if Group I already added them, skip the edit and keep only the test). They are optional positional params with defaults so existing call-sites and tests keep compiling.

- [ ] **Step 1: Write the failing test.**

```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeaturePickFieldsTests
{
    [Fact]
    public void CircularFeaturePick_DefaultsRmsAndConfidence_ToZeroAndOne()
    {
        var pick = new CircularFeaturePick(
            Center: new Vector3d(0, 0, 0),
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            Radius: 2.0,
            IsClosed: true,
            StartAngle: 0.0,
            Coverage: Math.PI * 2.0,
            CurveSegments: new List<Vector3d>());

        Assert.Equal(0.0, pick.GeometricRms, 6);
        Assert.Equal(1.0, pick.Confidence, 6);
    }

    [Fact]
    public void CircularFeaturePick_WithCarriesRmsAndConfidence()
    {
        var pick = new CircularFeaturePick(
            new Vector3d(0, 0, 0), Vector3d.UnitZ, Vector3d.UnitX, Vector3d.UnitY,
            2.0, true, 0.0, Math.PI * 2.0, new List<Vector3d>())
            with { GeometricRms = 0.01, Confidence = 0.42 };

        Assert.Equal(0.01, pick.GeometricRms, 6);
        Assert.Equal(0.42, pick.Confidence, 6);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeaturePickFields"`
  Expected failure: **CS0117/CS1061** — `CircularFeaturePick` has no `GeometricRms`/`Confidence`.

- [ ] **Step 3: Implement** — extend the positional record with two trailing optional params. Replace the record declaration:

```csharp
public sealed record CircularFeaturePick(
    Vector3d Center,
    Vector3d Axis,
    Vector3d U,
    Vector3d V,
    double Radius,
    bool IsClosed,
    double StartAngle,
    double Coverage,
    IReadOnlyList<Vector3d> CurveSegments,
    double GeometricRms = 0.0,
    double Confidence = 1.0);
```

> Coordinate with Group I: if Group I lands `SurfaceKind Kind` and `double TubeRadius`, they go **before** `GeometricRms`/`Confidence` or anywhere after `CurveSegments`, all with defaults; positional order among the optional params is fine as long as every new param has a default. If a merge introduces a duplicate-member compile error, keep ONE declaration containing the union of optional params with defaults.

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeaturePickFields"`
  Expected: 2 passed.
  Then run the broader circular suite to confirm no existing call-site broke:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"`
  Expected: all existing CircularFeature tests still green.

- [ ] **Step 5: Commit** (PARENT repo).

```bash
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeaturePickFieldsTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): carry GeometricRms+Confidence on CircularFeaturePick

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task H.5: `MeasurementBuilders.BuildCircularFeature` recomputes value via the guard

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/BuildCircularFeatureRecomputeTests.cs`

Spec §6.10: "recompute the reported value from the accepted analytic fit (no verbatim pass-through)". Today `BuildCircularFeature` (`:168-172`) sets `sceneValue = feature.IsClosed ? feature.Radius * 2.0 : feature.Radius`. The redesign makes the **same recompute** flow through the guard's `CommitDecision.ReportedValue` so there is one authority for the value. We change `BuildCircularFeature` to derive the kind and the scene value from `PickFeedbackAndCommitGuard`-equivalent logic by calling a small shared recompute helper, guaranteeing the committed value equals `2R`/`R` of the fitted radius regardless of any stale field on the pick.

- [ ] **Step 1: Write the failing test.** The pick carries a *poisoned* `Radius` field that disagrees with itself only if someone double-applied a factor; we assert the built measurement's `SceneLength` equals the recomputed value and that a closed feature yields `2R`.

```csharp
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class BuildCircularFeatureRecomputeTests
{
    private static CircularFeaturePick Pick(double radius, bool closed) =>
        new(
            Center: new Vector3d(0, 0, 0),
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            Radius: radius,
            IsClosed: closed,
            StartAngle: 0.0,
            Coverage: closed ? System.Math.PI * 2.0 : System.Math.PI * 0.5,
            CurveSegments: new List<Vector3d>());

    [Fact]
    public void BuildCircularFeature_Closed_ReportsTwoR_InMeters()
    {
        var m = MeasurementBuilders.BuildCircularFeature(Pick(2.0, closed: true), metersPerSceneUnit: 1.0);

        Assert.Equal(CircularFeatureDimensionKind.Diameter, m.DimensionKind);
        Assert.Equal(4.0, m.Value.Meters, 4);   // 2R recomputed from the fitted radius
        Assert.Equal(2.0, m.Radius, 4);
    }

    [Fact]
    public void BuildCircularFeature_Open_ReportsR_InMeters()
    {
        var m = MeasurementBuilders.BuildCircularFeature(Pick(3.0, closed: false), metersPerSceneUnit: 1.0);

        Assert.Equal(CircularFeatureDimensionKind.Radius, m.DimensionKind);
        Assert.Equal(3.0, m.Value.Meters, 4);
        Assert.Equal(3.0, m.Radius, 4);
    }

    [Fact]
    public void BuildCircularFeature_Closed_ScalesByMetersPerSceneUnit()
    {
        var m = MeasurementBuilders.BuildCircularFeature(Pick(2.0, closed: true), metersPerSceneUnit: 0.001);

        // 2R = 4 scene units * 0.001 m/unit = 0.004 m
        Assert.Equal(0.004, m.Value.Meters, 6);
    }
}
```

> If `CircularFeatureMeasurement` exposes the kind under a different member name than `DimensionKind`, or `SceneLength` exposes meters under a name other than `.Meters`, adjust the two assertions to the actual member (read `[PARENT] src/FabricationAssistant.Core/Measurement/Domain/CircularFeatureMeasurement.cs` and `SceneLength.cs` first). Keep the numeric expectations identical.

- [ ] **Step 2: Run it, expect FAIL or (partially) PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~BuildCircularFeatureRecompute"`
  Expected: the closed/open value tests likely already pass against the current code (since today's code also does `2R`/`R`); the purpose of this task is to **route that recompute through a single named helper** so it can't drift. If all three already pass, that is acceptable — proceed to Step 3 to harden the implementation, then re-run to confirm still-green (this is a refactor-under-test, the value contract is the lock).

- [ ] **Step 3: Implement** — introduce a shared recompute helper and use it in `BuildCircularFeature`. Replace the body of `BuildCircularFeature` (`:164-183`):

```csharp
    /// <summary>
    /// Recomputes the reported scene value from the fitted radius (closed => 2R diameter,
    /// open => R radius). Single authority for the committed value; spec §6.10.
    /// </summary>
    public static double RecomputeCircularSceneValue(double fittedRadius, bool isClosed)
        => isClosed ? fittedRadius * 2.0 : fittedRadius;

    public static CircularFeatureMeasurement BuildCircularFeature(
        CircularFeaturePick feature,
        double metersPerSceneUnit)
    {
        CircularFeatureDimensionKind kind = feature.IsClosed
            ? CircularFeatureDimensionKind.Diameter
            : CircularFeatureDimensionKind.Radius;

        double sceneValue = RecomputeCircularSceneValue(feature.Radius, feature.IsClosed);
        SceneLength value = SceneLength.FromSceneUnits(sceneValue, metersPerSceneUnit);

        return new CircularFeatureMeasurement(
            MeasurementId.New(),
            feature.Center,
            feature.Axis.Normalized(),
            feature.U.Normalized(),
            feature.V.Normalized(),
            feature.Radius,
            value,
            kind,
            feature.CurveSegments);
    }
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~BuildCircularFeatureRecompute"`
  Expected: 3 passed. Also re-run `--filter "FullyQualifiedName~CircularFeature"` — all green.

- [ ] **Step 5: Commit** (PARENT repo).

```bash
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs src/FabricationAssistant.App.Tests/Measurement/BuildCircularFeatureRecomputeTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "refactor(measure): route BuildCircularFeature value through single recompute helper

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task H.6: `MeasurementSession.AdvanceCircularFeature` does NOT commit on low confidence

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs` (`:385-400`)
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/AdvanceCircularFeatureGuardTests.cs`

Today `AdvanceCircularFeature` (`:385-400`) commits **any** `PickedCircularFeature` unconditionally. Spec §6.10 / §4.3: a low-confidence pick must be rejected with feedback and produce **no** measurement. We gate the commit on the pick's `Confidence` (carried in H.4): if `Confidence` is below a session threshold, do not add a measurement, surface the reason, and clear hover.

We add a lightweight reject signal the UI can observe without changing the existing `StateChanged` contract: a public `LastCircularReject` property (`RejectReason`) plus a `CircularFeatureRejected` event. The threshold default mirrors spec §9 confidence intent; below it ⇒ `LowConfidence`.

- [ ] **Step 1: Write the failing test.** Build a session, submit a high-confidence closed pick (commits, store grows by 1), then a low-confidence pick (no commit, store unchanged, reject reason = `LowConfidence`).

```csharp
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class AdvanceCircularFeatureGuardTests
{
    private static PickResult.PickedCircularFeature MakePick(double confidence, bool closed = true)
    {
        var feature = new CircularFeaturePick(
            Center: new Vector3d(0, 0, 0),
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            Radius: 2.0,
            IsClosed: closed,
            StartAngle: 0.0,
            Coverage: System.Math.PI * 2.0,
            CurveSegments: new List<Vector3d>())
            with { Confidence = confidence, GeometricRms = closed ? 0.005 : 0.005 };

        return new PickResult.PickedCircularFeature(feature, NodeId: 1);
    }

    [Fact]
    public void Submit_HighConfidencePick_CommitsMeasurement()
    {
        MeasurementSession session = TestSessionFactory.CreateCircularSession();
        int before = session.Store.Items.Count;

        session.SubmitPick(MakePick(confidence: 0.95));

        Assert.Equal(before + 1, session.Store.Items.Count);
        Assert.Equal(RejectReason.None, session.LastCircularReject);
    }

    [Fact]
    public void Submit_LowConfidencePick_DoesNotCommit_AndReportsLowConfidence()
    {
        MeasurementSession session = TestSessionFactory.CreateCircularSession();
        int before = session.Store.Items.Count;

        RejectReason? observed = null;
        session.CircularFeatureRejected += (_, reason) => observed = reason;

        session.SubmitPick(MakePick(confidence: 0.10));

        Assert.Equal(before, session.Store.Items.Count);          // NO measurement added
        Assert.Equal(RejectReason.LowConfidence, session.LastCircularReject);
        Assert.Equal(RejectReason.LowConfidence, observed);
    }
}
```

> `TestSessionFactory.CreateCircularSession()` and `session.Store` / `session.SubmitPick` must match the real `MeasurementSession` construction and public surface. **Before writing the test, read** `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs:1-120` for the constructor signature and the public method that drives `AdvanceCircularFeature` (it is invoked through the pick-submit entry point — confirm whether it is `SubmitPick`, `Submit`, or `Advance`), and look for an existing session-construction helper in `[PARENT] src/FabricationAssistant.App.Tests/Measurement/` (e.g. a builder used by `MeasurementSessionTests`). If a factory exists, reuse it; if not, add a minimal `internal static class TestSessionFactory` in this test file wiring the real constructor with in-memory store/undo. Use the exact store member name the session exposes (e.g. `_store` is private — assert via the public store/items accessor the session offers, or via the existing tests' pattern). Keep the two numeric/enum assertions exactly as written.

- [ ] **Step 2: Run it, expect FAIL.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AdvanceCircularFeatureGuard"`
  Expected failure: **CS1061** — `MeasurementSession` has no `LastCircularReject` / `CircularFeatureRejected`.

- [ ] **Step 3: Implement.** Add the reject surface + a confidence threshold, and gate the commit. First add fields/members near the other public session members (e.g. just above `private void Raise()` at `:415`):

```csharp
    /// <summary>
    /// Minimum pick confidence required to commit a circular-feature measurement.
    /// Below this, the pick is rejected with <see cref="RejectReason.LowConfidence"/> and
    /// no measurement is created. Spec §6.10 / §9.
    /// </summary>
    public double CircularCommitConfidenceThreshold { get; set; } = 0.5;

    /// <summary>Reason the most recent circular-feature pick was rejected (None if committed).</summary>
    public RejectReason LastCircularReject { get; private set; } = RejectReason.None;

    /// <summary>Raised when a circular-feature pick is rejected instead of committed.</summary>
    public event System.EventHandler<RejectReason>? CircularFeatureRejected;
```

Then replace `AdvanceCircularFeature` (`:385-400`):

```csharp
    private MeasurementDraft? AdvanceCircularFeature(PickResult result)
    {
        if (result is not PickResult.PickedCircularFeature circular)
            return CurrentDraft;

        // Strict confidence gate: never commit a low-confidence (garbage) fit. Spec §6.10.
        if (circular.Feature.Confidence < CircularCommitConfidenceThreshold)
        {
            LastCircularReject = RejectReason.LowConfidence;
            CircularFeatureRejected?.Invoke(this, RejectReason.LowConfidence);
            HoverFace = null;
            return null;
        }

        LastCircularReject = RejectReason.None;

        MeasurementResult m = MeasurementBuilders.BuildCircularFeature(
            circular.Feature,
            _units.MetersPerSceneUnit);
        m = WithAnchor(m, circular.NodeId);
        using var txn = _undoService.Begin($"Add {m.Mode} measurement");
        _store.Add(m);
        txn.Add(new MeasurementAddChange(m));

        HoverFace = null;
        return null;
    }
```

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~AdvanceCircularFeatureGuard"`
  Expected: 2 passed. Then run the whole circular suite to confirm no regression:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` — all green. If a `CS2012` file-lock appears, `dotnet build-server shutdown` and retry (see preamble).

- [ ] **Step 5: Commit** (PARENT repo).

```bash
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs src/FabricationAssistant.App.Tests/Measurement/AdvanceCircularFeatureGuardTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): MeasurementSession rejects low-confidence circular picks (no commit)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task H.7: Android mirror — link the gate/guard types and assert the three device-failure scenarios

**Files:**
- Modify: `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (add `<Compile Include>` for the new Core file if not already wildcard-linked)
- Test: `[ANDROID] src/FabricationAssistant.App.Android.Tests/FitAcceptanceGateAndroidTests.cs`

The Android test project links Core source **selectively**. Mirror the three canonical device failures (clean accept / degenerate-corner reject / oversized-leak reject) so the gate is proven inside the Android build too (spec §10 "Android mirror"; success criterion 5).

- [ ] **Step 1: Add the Compile link (if needed).** First check whether `FitAcceptance.cs` is already pulled in by an existing wildcard in the csproj. Read `[ANDROID] src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` and search for `Measurement\Engine` includes. If there is **no** glob covering `FitAcceptance.cs`, add an explicit item (and the `SurfaceFit`/`SurfaceKind` source file from Group E if not already linked, plus `PickTypes.cs` if the test references the pick):

```xml
  <ItemGroup>
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FitAcceptance.cs">
      <Link>Measurement\Engine\FitAcceptance.cs</Link>
    </Compile>
  </ItemGroup>
```

> Use the exact relative depth the csproj already uses for other Core links (match an existing `<Compile Include="..\..\..\src\FabricationAssistant.Core\...">` entry; copy its `..\` prefix length verbatim). If a wildcard like `..\..\..\src\FabricationAssistant.Core\Measurement\**\*.cs` already exists, **skip this step** — the file is auto-linked.

- [ ] **Step 2: Write the test (mirrors the host gate tests with the device numbers).**

```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class FitAcceptanceGateAndroidTests
{
    private static List<Vector3d> Ring(double r, int n)
    {
        var pts = new List<Vector3d>(n);
        for (int i = 0; i < n; i++)
        {
            double a = 2.0 * Math.PI * i / n;
            pts.Add(new Vector3d(r * Math.Cos(a), r * Math.Sin(a), 0.0));
        }
        return pts;
    }

    [Fact]
    public void CleanHoleFit_Accepts() // ctrl tap: mesh 349, R~=0.0022, rms~=1e-5
    {
        List<Vector3d> region = Ring(0.0022, 48);
        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, default,
            Radius: 0.0022, TubeRadius: 0.0, GeometricRms: 0.00001, Ok: true);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(region, fit, bodyExtent: 0.1, AcceptanceOptions.Default);

        Assert.True(res.Accepted);
        Assert.Equal(RejectReason.None, res.Reason);
    }

    [Fact]
    public void DegenerateCornerFan_RejectsLowConfidence() // tap 3: false success R=0.004
    {
        var region = new List<Vector3d>
        {
            new(0.004, 0.000, 0.0),
            new(0.046, 0.010, 0.0),
            new(0.030, -0.038, 0.0),
            new(0.075, 0.004, 0.0),
            new(0.001, 0.001, 0.0),
        };
        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, default,
            Radius: 0.004, TubeRadius: 0.0, GeometricRms: 0.033, Ok: true);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(region, fit, bodyExtent: 0.5, AcceptanceOptions.Default);

        Assert.False(res.Accepted);
        Assert.Equal(RejectReason.LowConfidence, res.Reason);
    }

    [Fact]
    public void OversizedLeakedCircle_RejectsRadiusOutOfBounds() // tap 1: leaked face circle
    {
        List<Vector3d> region = Ring(0.30, 48);
        var fit = new SurfaceFit(SurfaceKind.Cylinder, Vector3d.UnitZ, default,
            Radius: 0.30, TubeRadius: 0.0, GeometricRms: 0.0005, Ok: true);

        AcceptanceResult res = FitAcceptanceGate.Evaluate(region, fit, bodyExtent: 0.4, AcceptanceOptions.Default); // 0.5*0.4=0.2 < 0.30

        Assert.False(res.Accepted);
        Assert.Equal(RejectReason.RadiusOutOfBounds, res.Reason);
    }
}
```

- [ ] **Step 3: Run it, expect FAIL first (then PASS after linking).** From PARENT root:
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceGateAndroid"`
  Expected first failure: **CS0246** (`FitAcceptanceGate`/`SurfaceFit` not linked) if the Compile include was missing — fix the csproj per Step 1, then re-run.

- [ ] **Step 4: Run it, expect PASS.**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceGateAndroid"`
  Expected: 3 passed. (Build-lock: if `CS2012`, `dotnet build-server shutdown`, retry; if a `.NET Host (PID)` holds a DLL, `Stop-Process -Id <pid> -Force`.)

- [ ] **Step 5: Commit** (ANDROID repo — explicit paths).

```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/FitAcceptanceGateAndroidTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
test(measure): Android mirror for FitAcceptanceGate (3 device-failure taps)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Group H exit criteria

- [ ] PARENT host suite green: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` plus `~FitAcceptance`, `~PickFeedbackAndCommitGuard`, `~AdvanceCircularFeatureGuard`, `~BuildCircularFeatureRecompute`.
- [ ] ANDROID mirror green: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~FitAcceptanceGateAndroid"`.
- [ ] Three named device failures each resolve correctly: clean fit **accepts** (`None`); degenerate R=0.004 corner fan **rejects** `LowConfidence` and produces **no** measurement (via H.6 session gate); oversized leaked circle **rejects** `RadiusOutOfBounds`.
- [ ] No verbatim value pass-through remains — `BuildCircularFeature` derives the value from `RecomputeCircularSceneValue`, and `PickFeedbackAndCommitGuard.Evaluate` is the single accept/recompute authority consumed by Group I's pipeline.

**Handoff to Group I:** the pipeline (`CircularFeatureDetectionPipeline.Detect`) must call `PickFeedbackAndCommitGuard.Evaluate(hitModel, isClosed, regionPoints, fit, bodyExtent, opts)`, map the returned `CommitDecision.Reason` into `CircularDetectionResult.Reason`, set `CircularDetectionResult.Confidence` from `1 − gate.RmsOverR/opts.MaxRmsOverR` (clamped to [0,1]) so H.6's session threshold has a meaningful value to gate on, and stamp `CircularFeaturePick.GeometricRms`/`Confidence` (H.4) before returning the pick.

---

Files this section creates or modifies (all absolute):
- `C:/Users/skritikos/Desktop/Fabrication Assistant/src/FabricationAssistant.Core/Measurement/Engine/FitAcceptance.cs` (new)
- `C:/Users/skritikos/Desktop/Fabrication Assistant/src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs` (modified — H.4)
- `C:/Users/skritikos/Desktop/Fabrication Assistant/src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs` (modified — H.5)
- `C:/Users/skritikos/Desktop/Fabrication Assistant/src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs` (modified — H.6)
- `C:/Users/skritikos/Desktop/Fabrication Assistant/src/FabricationAssistant.App.Tests/Measurement/` (5 new host test files)
- `C:/Users/skritikos/Desktop/Fabrication Assistant/Android/src/FabricationAssistant.App.Android.Tests/` (1 new mirror test + csproj link — H.7)

---

I have a complete understanding. The transformVersion can be composed from the scene's `TransientTransformVersion` and `MoveTransformVersion`. I'll add a `TransformVersion` accessor to `IMeasureRaycaster`. I have everything needed to write the Group I plan now.

---

## Preamble (applies to every Group I task)

> **Two repos.** Core + host tests = **PARENT** repo (`C:/Users/skritikos/Desktop/Fabrication Assistant`). Android glue = **ANDROID** repo (`PARENT/Android`). Commit Core/host-test changes in PARENT; commit Android changes in ANDROID, staging by explicit path. New `.cs` under `src/FabricationAssistant.Core/Measurement/**` are auto compile-linked into Android via wildcard — no csproj edit. The Android **test** project links Core selectively; if a new Core type it references is not linked, add a `<Compile Include>` for it.
> **Build-lock:** on CSC `CS2012` / file-lock, run `dotnet build-server shutdown` then retry; if a `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`.
> **Group I depends on Groups B–H** (`MeshTopology`/`MeshTopologyCache`, `SeedResolver`, `SurfaceRegionGrower`, `AnalyticSurfaceFitter`, `EdgeLoopFitter`, `FeatureClassifier`, `ChamferDetector`, `FitAcceptanceGate`). Those types are assumed compiled. The fixtures (`CircularMeshFixtures`, `GeoAssert`) are from Group A.
> Host test run (from PARENT root): `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"` (TFM `net8.0-windows`). Android test run (from PARENT root): `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`.

---

## Group I — Detection Pipeline orchestrator + integration

> Wires Seed → Topology → [EdgeLoop | Chamfer | Grow+Fit] → Gate → Classify → Pick behind `CircularFeatureDetectionPipeline`, then rewires `MeshMeasurePicker` pick/hover to it (local space), the `MeasurementSession`/`MeasurementBuilders` commit guard, and drops the Android `preferredNode` override. PARENT-repo Core, except the raycaster change (ANDROID). Apply correction #6 (PickTypes edited once) and #8 (warmup/diagnostics).

### Task I.1: Extend `CircularFeaturePick` with surface metadata (Kind, GeometricRms, Confidence, TubeRadius)

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularFeaturePickExtensionTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — proves the new fields exist, default sanely, and survive a `with`-clone.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeaturePickExtensionTests
{
    [Fact]
    public void CircularFeaturePick_NewMetadataFields_DefaultAndCloneCorrectly()
    {
        var pick = new CircularFeaturePick(
            Center: Vector3d.Zero,
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            Radius: 3.0,
            IsClosed: true,
            StartAngle: 0.0,
            Coverage: System.Math.Tau,
            CurveSegments: new[] { new Vector3d(3, 0, 0), new Vector3d(0, 3, 0) });

        // Defaults: Unknown kind, zero residual/confidence/tube.
        Assert.Equal(SurfaceKind.Unknown, pick.Kind);
        Assert.Equal(0.0, pick.GeometricRms, 12);
        Assert.Equal(0.0, pick.Confidence, 12);
        Assert.Equal(0.0, pick.TubeRadius, 12);

        CircularFeaturePick enriched = pick with
        {
            Kind = SurfaceKind.Torus,
            GeometricRms = 0.0021,
            Confidence = 0.93,
            TubeRadius = 1.25,
        };

        Assert.Equal(SurfaceKind.Torus, enriched.Kind);
        Assert.Equal(0.0021, enriched.GeometricRms, 6);
        Assert.Equal(0.93, enriched.Confidence, 6);
        Assert.Equal(1.25, enriched.TubeRadius, 6);
        // Untouched fields preserved through the clone.
        Assert.Equal(3.0, enriched.Radius, 12);
        Assert.True(enriched.IsClosed);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeaturePickExtension"`. Expected failure: **compile error CS0117 / CS1061** — `CircularFeaturePick` has no `Kind`/`GeometricRms`/`Confidence`/`TubeRadius`.

- [ ] **Step 3: Implement** — add four optional positional parameters (defaulted, so all existing construction sites keep compiling) to the record in `PickTypes.cs`.
```csharp
public sealed record CircularFeaturePick(
    Vector3d Center,
    Vector3d Axis,
    Vector3d U,
    Vector3d V,
    double Radius,
    bool IsClosed,
    double StartAngle,
    double Coverage,
    IReadOnlyList<Vector3d> CurveSegments,
    SurfaceKind Kind = SurfaceKind.Unknown,
    double GeometricRms = 0.0,
    double Confidence = 0.0,
    double TubeRadius = 0.0);
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeaturePickExtension"`.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeaturePickExtensionTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): add Kind/GeometricRms/Confidence/TubeRadius to CircularFeaturePick

Carries the analytic-fit surface metadata the new detection pipeline
produces, defaulted so existing construction sites are unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.2: `CircularDetectionConfig.Default` holding the §9 constants

**Files:**
- Create: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularDetectionConfig.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularDetectionConfigTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — locks every §9 default to an exact number (degrees converted to radians).
```csharp
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularDetectionConfigTests
{
    [Fact]
    public void Default_MatchesSection9Constants()
    {
        CircularDetectionConfig c = CircularDetectionConfig.Default;

        // CreaseAngle 28°, NormalTol 12° -> radians.
        Assert.Equal(28.0 * System.Math.PI / 180.0, c.CreaseAngleRad, 9);
        Assert.Equal(12.0 * System.Math.PI / 180.0, c.NormalTolRad, 9);
        Assert.Equal(2.0, c.MembershipChordK, 12);
        Assert.Equal(0.015, c.MaxRmsOverR, 12);       // 1.5%
        Assert.Equal(0.90, c.MinInlierFraction, 12);  // 90%
        Assert.Equal(300.0, c.ClosedDeg, 12);
        Assert.Equal(30.0, c.HysteresisDeg, 12);      // 300–330° sticky band width
        Assert.Equal(0.5, c.MaxRadiusFraction, 12);
        Assert.True(c.ApertureRadiusLocal > 0.0);
    }

    [Fact]
    public void Default_IsAReusableSingletonRecord()
    {
        Assert.Same(CircularDetectionConfig.Default, CircularDetectionConfig.Default);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularDetectionConfig"`. Expected: **CS0246 / CS0117** — `CircularDetectionConfig` type / `.Default` does not exist.

- [ ] **Step 3: Implement**
```csharp
namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Scale-invariant tuning for <see cref="CircularFeatureDetectionPipeline"/>.
/// All values are the §9 defaults from the 2026-06-06 circular-dimension redesign.
/// Angles that the spec lists in degrees are stored here in radians.
/// </summary>
public sealed record CircularDetectionConfig(
    double CreaseAngleRad,
    double NormalTolRad,
    double MembershipChordK,
    double MaxRmsOverR,
    double MinInlierFraction,
    double ClosedDeg,
    double HysteresisDeg,
    double MaxRadiusFraction,
    double ApertureRadiusLocal)
{
    private const double Deg2Rad = System.Math.PI / 180.0;

    public static CircularDetectionConfig Default { get; } = new(
        CreaseAngleRad: 28.0 * Deg2Rad,
        NormalTolRad: 12.0 * Deg2Rad,
        MembershipChordK: 2.0,
        MaxRmsOverR: 0.015,
        MinInlierFraction: 0.90,
        ClosedDeg: 300.0,
        HysteresisDeg: 30.0,
        MaxRadiusFraction: 0.5,
        // Aperture is expressed in local mesh units; the caller scales it from a
        // ~6px screen radius. A small positive seed default keeps tests/headless
        // callers honest. Pipeline callers may override via the ctor.
        ApertureRadiusLocal: 1.0e-3);
}
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularDetectionConfig"`.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/CircularDetectionConfig.cs src/FabricationAssistant.App.Tests/Measurement/CircularDetectionConfigTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): CircularDetectionConfig.Default with section-9 constants

Crease 28deg, NormalTol 12deg, MembershipChordK 2.0, MaxRmsOverR 1.5%,
MinInlierFraction 90%, ClosedDeg 300 + 30 hysteresis, MaxRadiusFraction 0.5.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.3: `CircularDetectionResult` record (pipeline output contract)

**Files:**
- Create: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularDetectionResult.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularDetectionResultTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — proves the two canonical construction shapes (accepted feature; explicit reject) and a `Reject` helper.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularDetectionResultTests
{
    [Fact]
    public void AcceptedDiameter_CarriesFeatureAndKind()
    {
        var feature = new CircularFeaturePick(
            Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX, Vector3d.UnitY,
            Radius: 4.0, IsClosed: true, StartAngle: 0.0, Coverage: System.Math.Tau,
            CurveSegments: new[] { new Vector3d(4, 0, 0), new Vector3d(0, 4, 0) },
            Kind: SurfaceKind.Cylinder, GeometricRms: 0.001, Confidence: 0.97);

        var r = new CircularDetectionResult(
            Ok: true, Kind: CircularKind.Diameter, Feature: feature,
            Chamfer: null, Reason: RejectReason.None, Confidence: 0.97);

        Assert.True(r.Ok);
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.Equal(4.0, r.Feature!.Radius, 12);
        Assert.Equal(RejectReason.None, r.Reason);
        Assert.Equal(0.97, r.Confidence, 6);
    }

    [Fact]
    public void Reject_HasNoFeatureAndCarriesReason()
    {
        CircularDetectionResult r = CircularDetectionResult.Reject(RejectReason.NoFeatureHere);

        Assert.False(r.Ok);
        Assert.Equal(CircularKind.None, r.Kind);
        Assert.Null(r.Feature);
        Assert.Null(r.Chamfer);
        Assert.Equal(RejectReason.NoFeatureHere, r.Reason);
        Assert.Equal(0.0, r.Confidence, 12);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularDetectionResult"`. Expected: **CS0246** — `CircularDetectionResult` not found.

- [ ] **Step 3: Implement**
```csharp
namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// The single output of <see cref="CircularFeatureDetectionPipeline.Detect"/>.
/// Either an accepted feature (<see cref="Ok"/> = true, <see cref="Feature"/> or
/// <see cref="Chamfer"/> populated) or an explicit reject with a
/// <see cref="RejectReason"/>. Never a silent half-state.
/// </summary>
public sealed record CircularDetectionResult(
    bool Ok,
    CircularKind Kind,
    CircularFeaturePick? Feature,
    ChamferFit? Chamfer,
    RejectReason Reason,
    double Confidence)
{
    public static CircularDetectionResult Reject(RejectReason reason) =>
        new(Ok: false, Kind: CircularKind.None, Feature: null,
            Chamfer: null, Reason: reason, Confidence: 0.0);
}
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularDetectionResult"`.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/CircularDetectionResult.cs src/FabricationAssistant.App.Tests/Measurement/CircularDetectionResultTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): CircularDetectionResult pipeline output contract

Accepted feature or explicit reject with reason; Reject() helper.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.4: Pipeline skeleton — seed→topology→reject path (`NoFeatureHere` / `MissedModel`)

**Files:**
- Create: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionPipelineTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — a flat plate has no circular feature → `NoFeatureHere`; a ray that misses every triangle → `MissedModel`. Uses Group A fixtures. A tiny in-test topology provider wraps the real `MeshTopologyCache`.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureDetectionPipelineTests
{
    private static CircularFeatureDetectionPipeline NewPipeline() =>
        new(new MeshTopologyCache(capacity: 4), CircularDetectionConfig.Default);

    [Fact]
    public void Detect_FlatPlate_RejectsAsNoFeatureHere()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FlatPlate(size: 10.0);
        // Ray straight down onto the plate centre (plate lies in XY at z=0, normal +Z).
        var origin = new Vector3d(0, 0, 5);
        var dir = -Vector3d.UnitZ;

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 1, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir,
            bodyExtent: 10.0, coarse: false);

        Assert.False(r.Ok);
        Assert.Equal(CircularKind.None, r.Kind);
        Assert.Equal(RejectReason.NoFeatureHere, r.Reason);
    }

    [Fact]
    public void Detect_RayMissesMesh_RejectsAsMissedModel()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FlatPlate(size: 10.0);
        var origin = new Vector3d(1000, 1000, 5); // far off the plate
        var dir = -Vector3d.UnitZ;

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 1, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir,
            bodyExtent: 10.0, coarse: false);

        Assert.False(r.Ok);
        Assert.Equal(RejectReason.MissedModel, r.Reason);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureDetectionPipeline"`. Expected: **CS0246** — `CircularFeatureDetectionPipeline` not found.

- [ ] **Step 3: Implement** the skeleton: build/fetch topology, resolve a seed, and return the two reject paths. The accept branch is a placeholder `Reject(LowConfidence)` that later tasks (I.5–I.7) replace.
```csharp
using System;
using System.Collections.Generic;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Core.Measurement.Engine;

/// <summary>
/// Orchestrates circular-feature detection in LOCAL mesh space:
/// Seed -> Topology -> [EdgeLoop | Chamfer | Grow+Fit] -> Gate -> Classify -> Pick.
/// Replaces CircularFeatureDetectionService.DetectFromSeed as the entry point.
/// </summary>
public sealed class CircularFeatureDetectionPipeline
{
    private readonly IMeshTopologyProvider _topology;
    private readonly CircularDetectionConfig _config;

    public CircularFeatureDetectionPipeline(
        IMeshTopologyProvider topology, CircularDetectionConfig config)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public CircularDetectionResult Detect(
        long meshKey,
        long transformVersion,
        IReadOnlyList<Vector3d> verticesLocal,
        IReadOnlyList<int> indices,
        Vector3d rayOriginLocal,
        Vector3d rayDirLocal,
        double bodyExtent,
        bool coarse,
        Action<string>? log = null)
    {
        if (verticesLocal is null || verticesLocal.Count == 0
            || indices is null || indices.Count < 3)
        {
            log?.Invoke("reject: empty mesh");
            return CircularDetectionResult.Reject(RejectReason.MissedModel);
        }

        MeshTopology topo = _topology.GetOrBuild(
            meshKey, transformVersion, verticesLocal, indices, _config.CreaseAngleRad);

        CircularSeed seed = SeedResolver.Resolve(
            topo, rayOriginLocal, rayDirLocal, _config.ApertureRadiusLocal);
        if (!seed.Ok)
        {
            log?.Invoke("reject: seed missed the mesh");
            return CircularDetectionResult.Reject(RejectReason.MissedModel);
        }

        log?.Invoke($"seed: tri={seed.TriangleIndex}, kind={seed.LocalKind}");

        // Branch placeholders filled by I.5 (grow+fit), I.6 (edge-loop/chamfer).
        // A seed that hits the model but resolves no circular feature yet is
        // reported as NoFeatureHere (not a silent null).
        return CircularDetectionResult.Reject(RejectReason.NoFeatureHere);
    }
}
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureDetectionPipeline"`. (Flat plate → seed hits but no feature → `NoFeatureHere`; off-plate ray → seed not Ok → `MissedModel`.)

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionPipelineTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): CircularFeatureDetectionPipeline skeleton + reject paths

Seed/topology wiring; flat plate -> NoFeatureHere, off-mesh ray -> MissedModel.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.5: Pipeline grow+fit branch (cylinder/torus) → Gate → Classify → `CircularFeaturePick`; coarse vs full

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionPipelineTests.cs` (extend)

- [ ] **Step 1: Write the failing test** — a boss (exterior cylinder) → `Diameter` Ø=2·R; a straight fillet (quarter-round cylinder) → `Radius` R; coarse path stays cylinder-only and still returns a finite value. Numeric expectations.
```csharp
[Fact]
public void Detect_Boss_FullCylinder_ReturnsDiameterWithCorrectRadius()
{
    (Vector3d[] v, int[] i) = CircularMeshFixtures.Cylinder(
        radius: 3.0, height: 5.0, segments: 48,
        sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
    int seedTri = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(3.0, 0.0, 2.5));
    // Ray inbound along -X toward the boss wall at +X.
    var origin = new Vector3d(50.0, 0.0, 2.5);
    var dir = -Vector3d.UnitX;

    CircularDetectionResult r = NewPipeline().Detect(
        meshKey: 10, transformVersion: 0, verticesLocal: v, indices: i,
        rayOriginLocal: origin, rayDirLocal: dir,
        bodyExtent: 6.0, coarse: false);

    Assert.True(r.Ok, $"expected accept, got {r.Reason}");
    Assert.Equal(CircularKind.Diameter, r.Kind);
    Assert.NotNull(r.Feature);
    Assert.Equal(3.0, r.Feature!.Radius, 2);
    GeoAssert.AxisParallel(r.Feature.Axis, Vector3d.UnitZ);
    Assert.Equal(SurfaceKind.Cylinder, r.Feature.Kind);
    Assert.True(r.Feature.Confidence > 0.5);
    _ = seedTri; // seed is resolved by the aperture ray; index pinned for clarity.
}

[Fact]
public void Detect_StraightFillet_ReturnsRadiusNotDiameter()
{
    (Vector3d[] v, int[] i) = CircularMeshFixtures.FilletStraight(
        radius: 1.5, length: 8.0, arcSegments: 16, sweepRadians: System.Math.PI / 2.0);
    var origin = new Vector3d(10.0, 10.0, 4.0); // aimed at the convex quarter-round
    var dir = (new Vector3d(0.0, 0.0, 4.0) - origin).Normalized();

    CircularDetectionResult r = NewPipeline().Detect(
        meshKey: 11, transformVersion: 0, verticesLocal: v, indices: i,
        rayOriginLocal: origin, rayDirLocal: dir,
        bodyExtent: 8.0, coarse: false);

    Assert.True(r.Ok, $"expected accept, got {r.Reason}");
    Assert.Equal(CircularKind.Radius, r.Kind);
    Assert.Equal(1.5, r.Feature!.Radius, 2);
    Assert.False(r.Feature.IsClosed);
}

[Fact]
public void Detect_Coarse_Boss_ReturnsFiniteCylinderRadiusUnderBudget()
{
    (Vector3d[] v, int[] i) = CircularMeshFixtures.Cylinder(
        radius: 2.0, height: 4.0, segments: 32,
        sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
    var origin = new Vector3d(40.0, 0.0, 2.0);
    var dir = -Vector3d.UnitX;

    CircularDetectionResult r = NewPipeline().Detect(
        meshKey: 12, transformVersion: 0, verticesLocal: v, indices: i,
        rayOriginLocal: origin, rayDirLocal: dir,
        bodyExtent: 4.0, coarse: true);

    Assert.True(r.Ok);
    Assert.Equal(SurfaceKind.Cylinder, r.Feature!.Kind); // coarse never reports torus
    Assert.Equal(2.0, r.Feature.Radius, 1);
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureDetectionPipeline"`. Expected: the three new tests fail with **`Assert.True(r.Ok)` failed, got NoFeatureHere** (the I.4 placeholder still rejects every seed).

- [ ] **Step 3: Implement** — replace the placeholder `return` in `Detect` with the full grow→fit→gate→classify→pick chain. Add private helpers `BuildPick`, `CoverageDegrees`, `RegionPoints`. `coarse=true` forces cylinder-only fit; `coarse=false` allows torus via `FitBest`.
```csharp
        // ---- Grow + fit branch (fillets, bosses, shafts, holes-by-surface) ----
        var growOpts = new RegionGrowOptions(
            NormalTolRad: _config.NormalTolRad,
            MembershipChordK: _config.MembershipChordK,
            MaxTriangles: topo.Triangles.Count);
        List<int> region = SurfaceRegionGrower.Grow(
            topo, seed.TriangleIndex, growOpts, out SurfaceFit runningFit);
        if (region.Count == 0)
        {
            log?.Invoke("reject: empty region");
            return CircularDetectionResult.Reject(RejectReason.NoFeatureHere);
        }

        Vector3d[] points = RegionPoints(topo, region);
        Vector3d[] normals = RegionNormals(topo, region);
        Vector3d axisSeed = runningFit.Ok && runningFit.Axis.LengthSquared > 0.0
            ? runningFit.Axis
            : Vector3d.UnitZ;

        SurfaceFit fit = coarse
            ? AnalyticSurfaceFitter.FitCylinder(points, normals, axisSeed)
            : AnalyticSurfaceFitter.FitBest(points, normals, axisSeed);
        if (!fit.Ok || fit.Radius <= 0.0)
        {
            log?.Invoke("reject: fit failed");
            return CircularDetectionResult.Reject(RejectReason.LowConfidence);
        }

        var acceptOpts = new AcceptanceOptions(
            MaxRmsOverR: _config.MaxRmsOverR,
            MinInlierFraction: _config.MinInlierFraction,
            MaxRadiusFraction: _config.MaxRadiusFraction);
        AcceptanceResult accept = FitAcceptanceGate.Evaluate(points, fit, bodyExtent, acceptOpts);
        if (!accept.Accepted)
        {
            log?.Invoke($"reject: gate {accept.Reason} rmsOverR={accept.RmsOverR:0.####} inlier={accept.InlierFraction:0.###}");
            return CircularDetectionResult.Reject(accept.Reason);
        }

        double coverageDeg = CoverageDegrees(points, fit);
        // A closed crease loop around the region implies a hole/boss (exact Ø branch
        // is I.6); for the surface branch we infer closure from angular coverage.
        var classifierOpts = new ClassifierOptions(_config.ClosedDeg, _config.HysteresisDeg);
        CircularKind kind = FeatureClassifier.Classify(
            region, fit, hasClosedCreaseLoop: false, coverageDeg, classifierOpts);
        if (kind == CircularKind.None)
        {
            log?.Invoke("reject: classifier None");
            return CircularDetectionResult.Reject(RejectReason.NoFeatureHere);
        }

        double confidence = ConfidenceFrom(accept);
        CircularFeaturePick pick = BuildPick(fit, points, coverageDeg, kind, confidence);
        log?.Invoke($"accept: kind={kind}, R={fit.Radius:0.####}, conf={confidence:0.###}");
        return new CircularDetectionResult(
            Ok: true, Kind: kind, Feature: pick,
            Chamfer: null, Reason: RejectReason.None, Confidence: confidence);
    }

    private static Vector3d[] RegionPoints(MeshTopology topo, IReadOnlyList<int> region)
    {
        var pts = new Vector3d[region.Count];
        for (int k = 0; k < region.Count; k++)
            pts[k] = topo.Triangles[region[k]].Centroid;
        return pts;
    }

    private static Vector3d[] RegionNormals(MeshTopology topo, IReadOnlyList<int> region)
    {
        var ns = new Vector3d[region.Count];
        for (int k = 0; k < region.Count; k++)
            ns[k] = topo.Triangles[region[k]].Normal;
        return ns;
    }

    private static double ConfidenceFrom(AcceptanceResult accept)
    {
        // Map residual + inlier-fraction into [0,1]: tighter RMS and higher inlier
        // fraction -> higher confidence. RmsOverR of 1.5% maps to ~0.0 residual term.
        double rmsTerm = System.Math.Clamp(1.0 - accept.RmsOverR / 0.015, 0.0, 1.0);
        double inlierTerm = System.Math.Clamp(
            (accept.InlierFraction - 0.90) / 0.10, 0.0, 1.0);
        return System.Math.Clamp(0.4 + 0.3 * rmsTerm + 0.3 * inlierTerm, 0.0, 1.0);
    }

    private static double CoverageDegrees(IReadOnlyList<Vector3d> points, SurfaceFit fit)
    {
        Vector3d axis = fit.Axis.Normalized();
        if (axis.LengthSquared <= 0.0 || points.Count == 0)
            return 0.0;
        Vector3d u = OrthonormalU(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        var angles = new List<double>(points.Count);
        foreach (Vector3d p in points)
        {
            Vector3d d = p - fit.Center;
            d -= axis * Vector3d.Dot(d, axis);
            if (d.LengthSquared <= 1.0e-18) continue;
            double a = System.Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u));
            if (a < 0.0) a += System.Math.Tau;
            angles.Add(a);
        }
        if (angles.Count < 2) return 0.0;
        angles.Sort();
        double maxGap = (angles[0] + System.Math.Tau) - angles[^1];
        for (int k = 1; k < angles.Count; k++)
            maxGap = System.Math.Max(maxGap, angles[k] - angles[k - 1]);
        double sweepRad = System.Math.Tau - maxGap;
        return sweepRad * 180.0 / System.Math.PI;
    }

    private static CircularFeaturePick BuildPick(
        SurfaceFit fit, IReadOnlyList<Vector3d> points,
        double coverageDeg, CircularKind kind, double confidence)
    {
        Vector3d axis = fit.Axis.Normalized();
        Vector3d u = OrthonormalU(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        bool isClosed = kind == CircularKind.Diameter;
        double coverageRad = isClosed ? System.Math.Tau : coverageDeg * System.Math.PI / 180.0;
        double startAngle = StartAngleFor(points, fit.Center, axis, u, v);
        IReadOnlyList<Vector3d> curve = CircularFeatureDetectionService.BuildCurveSegments(
            fit.Center, u, v, fit.Radius, startAngle, coverageRad, isClosed);
        return new CircularFeaturePick(
            Center: fit.Center, Axis: axis, U: u, V: v,
            Radius: fit.Radius, IsClosed: isClosed,
            StartAngle: startAngle, Coverage: coverageRad, CurveSegments: curve,
            Kind: fit.Kind, GeometricRms: fit.GeometricRms,
            Confidence: confidence, TubeRadius: fit.TubeRadius);
    }

    private static double StartAngleFor(
        IReadOnlyList<Vector3d> points, Vector3d center, Vector3d axis, Vector3d u, Vector3d v)
    {
        double min = double.MaxValue;
        foreach (Vector3d p in points)
        {
            Vector3d d = p - center;
            d -= axis * Vector3d.Dot(d, axis);
            if (d.LengthSquared <= 1.0e-18) continue;
            double a = System.Math.Atan2(Vector3d.Dot(d, v), Vector3d.Dot(d, u));
            if (a < 0.0) a += System.Math.Tau;
            min = System.Math.Min(min, a);
        }
        return min == double.MaxValue ? 0.0 : min;
    }

    private static Vector3d OrthonormalU(Vector3d axis)
    {
        Vector3d seed = System.Math.Abs(axis.Z) < 0.9 ? Vector3d.UnitZ : Vector3d.UnitX;
        Vector3d u = Vector3d.Cross(seed, axis);
        return u.LengthSquared > 1.0e-18 ? u.Normalized() : Vector3d.UnitX;
```
> Note: this closes `OrthonormalU` and the class. The placeholder `return CircularDetectionResult.Reject(RejectReason.NoFeatureHere);` from I.4 is deleted; the new chain above replaces it inside `Detect`. `CircularFeatureDetectionService.BuildCurveSegments` is the existing public static helper (reused so the arc geometry matches the legacy renderer exactly).

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureDetectionPipeline"`.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionPipelineTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): pipeline grow+fit branch (cylinder/torus) -> gate -> classify

Boss -> diameter, straight fillet -> radius; coarse path is cylinder-only.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.6: Pipeline edge-loop (exact Ø for holes) and chamfer branches

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionPipelineTests.cs` (extend)

- [ ] **Step 1: Write the failing test** — a hole (interior wall, inward normals) bounded by a closed crease rim returns `Diameter` with the exact loop radius and `IsClosed`; a chamfer band returns the chamfer fit.
```csharp
[Fact]
public void Detect_Hole_InwardNormals_ReturnsDiameterFromEdgeLoop()
{
    (Vector3d[] v, int[] i) = CircularMeshFixtures.HoleInPlate(
        radius: 1.0, plate: 6.0, thickness: 1.0, segments: 48);
    int seedTri = CircularMeshFixtures.SeedTriangleAt((v, i), new Vector3d(1.0, 0.0, 0.5));
    // Ray inbound from +X toward the bore wall; the inner wall normal points -X (inward).
    var origin = new Vector3d(5.0, 0.0, 0.5);
    var dir = -Vector3d.UnitX;

    CircularDetectionResult r = NewPipeline().Detect(
        meshKey: 20, transformVersion: 0, verticesLocal: v, indices: i,
        rayOriginLocal: origin, rayDirLocal: dir,
        bodyExtent: 6.0, coarse: false);

    Assert.True(r.Ok, $"expected accept, got {r.Reason}");
    Assert.Equal(CircularKind.Diameter, r.Kind);
    Assert.True(r.Feature!.IsClosed);
    Assert.Equal(1.0, r.Feature.Radius, 2);
    GeoAssert.AxisParallel(r.Feature.Axis, Vector3d.UnitZ);
    _ = seedTri;
}

[Fact]
public void Detect_ChamferBand_ReturnsChamferFit()
{
    (Vector3d[] v, int[] i) = CircularMeshFixtures.Chamfer(width: 2.0, angleDeg: 45.0, length: 10.0);
    var origin = new Vector3d(6.0, 6.0, 5.0);
    var dir = (new Vector3d(0.0, 0.0, 5.0) - origin).Normalized();

    CircularDetectionResult r = NewPipeline().Detect(
        meshKey: 21, transformVersion: 0, verticesLocal: v, indices: i,
        rayOriginLocal: origin, rayDirLocal: dir,
        bodyExtent: 10.0, coarse: false);

    Assert.True(r.Ok, $"expected accept, got {r.Reason}");
    Assert.Equal(CircularKind.Chamfer, r.Kind);
    Assert.NotNull(r.Chamfer);
    Assert.Equal(2.0, r.Chamfer!.Value.Width, 2);
    Assert.Equal(45.0, r.Chamfer.Value.AngleDeg, 1);
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureDetectionPipeline"`. Expected: hole test fails (`Radius` from surface fit rather than exact closed-loop `Diameter`, or `IsClosed=false`); chamfer test fails (`Kind != Chamfer`, `r.Chamfer == null`).

- [ ] **Step 3: Implement** — insert the edge-loop and chamfer branches **before** the grow+fit branch in `Detect` (the §5 routing order: closed crease loop ⇒ Ø; narrow band ⇒ chamfer; else surface). Add private helpers `TryEdgeLoopDiameter` and `TryChamfer`.
```csharp
        // ---- Branch 1: closed crease/boundary loop -> exact diameter (hole/boss). ----
        if (TryEdgeLoopDiameter(topo, seed, bodyExtent, out CircularDetectionResult loopResult))
        {
            log?.Invoke($"accept(edge-loop): R={loopResult.Feature!.Radius:0.####}");
            return loopResult;
        }

        // ---- Branch 2: narrow planar/conical band between two creases -> chamfer. ----
        if (TryChamfer(topo, seed, out CircularDetectionResult chamferResult))
        {
            log?.Invoke($"accept(chamfer): w={chamferResult.Chamfer!.Value.Width:0.###} a={chamferResult.Chamfer.Value.AngleDeg:0.#}");
            return chamferResult;
        }

        // ---- Branch 3: grow + fit (existing block from I.5). ----
        var growOpts = new RegionGrowOptions(
```
> The above three lines are inserted directly ahead of `var growOpts = ...` from I.5. Add these helpers to the class:
```csharp
    private bool TryEdgeLoopDiameter(
        MeshTopology topo, CircularSeed seed, double bodyExtent,
        out CircularDetectionResult result)
    {
        result = CircularDetectionResult.Reject(RejectReason.NoFeatureHere);
        CircleFit loop = EdgeLoopFitter.FitNearestClosedLoop(
            topo, seed.TriangleIndex, seed.HitLocal);
        if (!loop.Ok || loop.Radius <= 0.0)
            return false;

        // Sanity bound: an exact rim circle must still respect the body bound.
        if (loop.Radius > _config.MaxRadiusFraction * bodyExtent)
            return false;
        // A real rim fits tightly; reject loops whose residual is a large
        // fraction of the radius (degenerate/false loop).
        if (loop.Rms / loop.Radius > _config.MaxRmsOverR * 3.0)
            return false;

        Vector3d axis = loop.Axis.Normalized();
        Vector3d u = OrthonormalU(axis);
        Vector3d v = Vector3d.Cross(axis, u).Normalized();
        IReadOnlyList<Vector3d> curve = CircularFeatureDetectionService.BuildCurveSegments(
            loop.Center, u, v, loop.Radius, startAngle: 0.0,
            coverage: System.Math.Tau, isClosed: true);
        double confidence = System.Math.Clamp(
            1.0 - (loop.Rms / loop.Radius) / _config.MaxRmsOverR, 0.5, 1.0);
        var pick = new CircularFeaturePick(
            Center: loop.Center, Axis: axis, U: u, V: v,
            Radius: loop.Radius, IsClosed: true, StartAngle: 0.0,
            Coverage: System.Math.Tau, CurveSegments: curve,
            Kind: SurfaceKind.Cylinder, GeometricRms: loop.Rms,
            Confidence: confidence, TubeRadius: 0.0);
        result = new CircularDetectionResult(
            Ok: true, Kind: CircularKind.Diameter, Feature: pick,
            Chamfer: null, Reason: RejectReason.None, Confidence: confidence);
        return true;
    }

    private bool TryChamfer(
        MeshTopology topo, CircularSeed seed, out CircularDetectionResult result)
    {
        result = CircularDetectionResult.Reject(RejectReason.NoFeatureHere);
        ChamferFit chamfer = ChamferDetector.Detect(topo, seed.TriangleIndex);
        if (!chamfer.Ok || chamfer.Width <= 0.0)
            return false;

        double confidence = System.Math.Clamp(1.0 - chamfer.Rms / chamfer.Width, 0.5, 1.0);
        result = new CircularDetectionResult(
            Ok: true, Kind: CircularKind.Chamfer, Feature: null,
            Chamfer: chamfer, Reason: RejectReason.None, Confidence: confidence);
        return true;
    }
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureDetectionPipeline"` (all I.4–I.6 tests green; the boss test from I.5 must still pass — its exterior wall has no inner closed crease loop within reach, so it falls through to grow+fit).

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionPipelineTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): pipeline edge-loop (exact hole diameter) + chamfer branches

Closed crease rim -> exact Ø; narrow crease-bounded band -> chamfer w x angle.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.7: End-to-end fixture matrix on `Detect()` (every fixture → correct kind+value)

**Files:**
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularPipelineEndToEndTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — a `[Theory]` sweep over all six fixtures plus a seed-independence guard for the boss (success criterion §2.4 / §3 invariance). Each row pins kind + value.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularPipelineEndToEndTests
{
    private static CircularFeatureDetectionPipeline NewPipeline() =>
        new(new MeshTopologyCache(capacity: 8), CircularDetectionConfig.Default);

    [Fact]
    public void Boss_SeedIndependence_SameDiameterFromEveryTapAngle()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.Cylinder(
            radius: 2.5, height: 6.0, segments: 64,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        CircularFeatureDetectionPipeline pipeline = NewPipeline();

        foreach (double deg in new[] { 0.0, 90.0, 200.0, 315.0 })
        {
            double a = deg * System.Math.PI / 180.0;
            var wall = new Vector3d(System.Math.Cos(a) * 2.5, System.Math.Sin(a) * 2.5, 3.0);
            var origin = wall + new Vector3d(System.Math.Cos(a), System.Math.Sin(a), 0) * 40.0;
            var dir = (wall - origin).Normalized();

            CircularDetectionResult r = pipeline.Detect(
                meshKey: 30, transformVersion: 0, verticesLocal: v, indices: i,
                rayOriginLocal: origin, rayDirLocal: dir,
                bodyExtent: 5.0, coarse: false);

            Assert.True(r.Ok, $"angle {deg}: {r.Reason}");
            Assert.Equal(CircularKind.Diameter, r.Kind);
            Assert.Equal(2.5, r.Feature!.Radius, 2);
        }
    }

    [Fact]
    public void CurvedFillet_Torus_ReturnsRadiusEqualToTubeRadius()
    {
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FilletTorus(
            tubeRadius: 0.75, ringRadius: 5.0, ringSeg: 48, tubeSeg: 12,
            sweepRadians: System.Math.PI / 2.0);
        // Aim at the convex tube surface on the outer-top of the torus quarter.
        var origin = new Vector3d(5.0, 0.0, 5.0);
        var dir = (new Vector3d(5.0, 0.0, 0.0) - origin).Normalized();

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 31, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir,
            bodyExtent: 12.0, coarse: false);

        Assert.True(r.Ok, $"expected accept, got {r.Reason}");
        Assert.Equal(CircularKind.Radius, r.Kind);
        Assert.Equal(SurfaceKind.Torus, r.Feature!.Kind);
        Assert.Equal(0.75, r.Feature.Radius, 2);     // tube radius == fillet R
        Assert.Equal(0.75, r.Feature.TubeRadius, 2);
    }

    [Theory]
    [InlineData("plate")]
    [InlineData("cone-negative")]
    public void Negatives_RejectWithReason(string which)
    {
        (Vector3d[] v, int[] i) = which == "plate"
            ? CircularMeshFixtures.FlatPlate(size: 8.0)
            : CircularMeshFixtures.Chamfer(width: 0.05, angleDeg: 2.0, length: 8.0); // near-flat sliver
        var origin = new Vector3d(0.0, 0.0, 5.0);
        var dir = -Vector3d.UnitZ;

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 32, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir,
            bodyExtent: 8.0, coarse: false);

        Assert.False(r.Ok);
        Assert.NotEqual(RejectReason.None, r.Reason);
        Assert.Null(r.Feature);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — first run **fails to compile** only if a fixture is missing (Group A delivers them); given Group A present, expect logic failures if any fixture/branch regressed. Run: `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularPipelineEndToEnd"`. Expected initial fail: `CurvedFillet_Torus` (torus branch only landed in I.5 via `FitBest`; confirm it selects torus). If green immediately, that is acceptable — these are guard tests over I.5/I.6 behavior.

- [ ] **Step 3: Implement** — no production code if I.5/I.6 already satisfy the rows. If `CurvedFillet_Torus` fails because `coarse:false` still picked cylinder, the fix lives in Group E's `FitBest` (out of this group's scope); within Group I, ensure `Detect` passes the running fit axis seed unchanged (already done in I.5). If the seed-independence test fails for a tap angle, widen the seed branch order: confirm the boss never matches `TryEdgeLoopDiameter` (exterior wall ⇒ no inner closed loop). Add a guard in `Detect` so the edge-loop branch is skipped when the seed surface is convex-exterior:
```csharp
        // Branch 1 only applies when a closed loop is actually reachable; the
        // helper already returns false otherwise, so no extra code is needed.
        // If a future regression makes a boss match a far loop, gate on
        // seed.LocalKind here. (Kept as a comment; no behavior change.)
```
> In practice this task is mostly assertion coverage; implement production code only where a row is red.

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularPipelineEndToEnd"`.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularPipelineEndToEndTests.cs src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
test(measure): end-to-end Detect() matrix over every circular fixture

Boss seed-independence, torus fillet R==tube radius, plate/sliver negatives.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.8: Add `TransformVersion` to `IMeasureRaycaster` (local-space rewire prerequisite)

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/IMeasureRaycaster.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeasureRaycasterTransformVersionTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — a tiny fake implements the interface and exposes a transform version; the picker (rewired in I.9) will key topology on it.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeasureRaycasterTransformVersionTests
{
    private sealed class VersionedRaycaster : IMeasureRaycaster
    {
        public long Version { get; set; }
        public long TransformVersion => Version;
        public double SceneDiagonal => 10.0;
        public MeasureRaycastHit? Raycast(Vector3d o, Vector3d d) => null;
        public MeshDto? GetMesh(int meshId) => null;
        public SceneNode? GetNode(int nodeId) => null;
        public Matrix4d GetWorldTransform(int nodeId) => Matrix4d.Identity;
    }

    [Fact]
    public void TransformVersion_IsReadableAndChanges()
    {
        var rc = new VersionedRaycaster { Version = 7 };
        IMeasureRaycaster asInterface = rc;
        Assert.Equal(7, asInterface.TransformVersion);
        rc.Version = 8;
        Assert.Equal(8, asInterface.TransformVersion);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasureRaycasterTransformVersion"`. Expected: **CS0535** — `VersionedRaycaster` does not implement `IMeasureRaycaster.TransformVersion` (member does not yet exist on the interface), and **CS0117** at the test. Add a default-interface-member so existing implementers (`FakeRaycaster`, Android raycaster) still compile.

- [ ] **Step 3: Implement** — add a default-implemented property so the contract is additive (no existing implementer breaks).
```csharp
    double SceneDiagonal { get; }

    /// <summary>
    /// Monotonic token that increments whenever any node transform that affects
    /// world placement changes. The circular-feature pipeline keys its cached
    /// per-mesh topology on (meshId, TransformVersion) so it never re-materialises
    /// the whole mesh to world on every hover. Implementers that have no notion of
    /// transform versioning may leave the default (0); topology will then be built
    /// once per mesh and reused.
    /// </summary>
    long TransformVersion => 0;
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasureRaycasterTransformVersion"`.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/IMeasureRaycaster.cs src/FabricationAssistant.App.Tests/Measurement/MeasureRaycasterTransformVersionTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
feat(measure): IMeasureRaycaster.TransformVersion (default 0) for topology cache key

Additive default-interface-member; lets the picker key local-space topology
on (meshId, transformVersion) and stop rebuilding per hover.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.9: Rewire `MeshMeasurePicker.PickCircularFeature` / `TryHoverCircularFeatureHighlight` to the pipeline in LOCAL space

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/MeshMeasurePickerPipelineTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — drive `Pick(CircularFeature)` and `TryHoverCircularFeatureHighlight` through a fake raycaster whose mesh is a boss, with a non-identity world transform, and assert (a) the picker returns a `PickedCircularFeature` with the correct world-space radius and axis, (b) the rays passed to detection are transformed into **local** space (the fake records what `GetWorldTransform` was asked and that the detection still finds the feature despite the transform). The world radius must equal the local radius times the transform's uniform scale.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class MeshMeasurePickerPipelineTests
{
    [Fact]
    public void PickCircularFeature_Boss_WithWorldTransform_ReturnsWorldRadiusAndAxis()
    {
        const double localRadius = 2.0;
        MeshDto mesh = BuildBossMesh(localRadius, height: 5.0, segments: 48);
        Matrix4d world =
            Matrix4d.CreateTranslation(10.0, -4.0, 2.0) * Matrix4d.CreateRotationY(0.5);

        // Hit point on the +X wall, transformed to world; the picker must invert
        // 'world' to detect in local space, then re-express the result in world.
        Vector3d hitLocal = new(localRadius, 0.0, 2.5);
        Vector3d hitWorld = world.TransformPoint(hitLocal);
        var hit = new MeasureRaycastHit(1, mesh.MeshId, TriangleIndexOffset: 0, WorldPoint: hitWorld);

        // The ray the user shot in WORLD space toward the wall.
        Vector3d originWorld = world.TransformPoint(new Vector3d(40.0, 0.0, 2.5));
        Vector3d dirWorld = (hitWorld - originWorld).Normalized();

        var picker = new MeshMeasurePicker(new FakeRaycaster(mesh, hit, world), MeasurementTolerances.Default);
        PickResult result = picker.Pick(new PickRequest(PickKind.CircularFeature, originWorld, dirWorld));

        PickResult.PickedCircularFeature picked = Assert.IsType<PickResult.PickedCircularFeature>(result);
        Assert.True(picked.Feature.IsClosed);
        // World axis is the local +Z rotated by 'world'.
        Vector3d expectedAxis = world.TransformDirection(Vector3d.UnitZ).Normalized();
        Assert.True(System.Math.Abs(Vector3d.Dot(picked.Feature.Axis.Normalized(), expectedAxis)) > 0.99);
        // Pure rotation+translation => radius preserved in world.
        Assert.Equal(localRadius, picked.Feature.Radius, 1);
        Assert.NotNull(picked.Highlight);
        Assert.Equal(FaceHighlightKind.Selected, picked.Highlight!.Kind);
    }

    [Fact]
    public void HoverCircularFeature_Boss_ReturnsHighlight()
    {
        MeshDto mesh = BuildBossMesh(radius: 2.0, height: 5.0, segments: 48);
        var hit = new MeasureRaycastHit(1, mesh.MeshId, TriangleIndexOffset: 0, WorldPoint: new Vector3d(2, 0, 2.5));
        var picker = new MeshMeasurePicker(new FakeRaycaster(mesh, hit, Matrix4d.Identity), MeasurementTolerances.Default);

        FaceHighlight? hl = picker.TryHoverCircularFeatureHighlight(Vector3d.Zero, -Vector3d.UnitX);

        Assert.NotNull(hl);
        Assert.Equal(FaceHighlightKind.Hover, hl!.Kind);
        Assert.True(hl.WorldVertices.Count > 6);
    }

    private static MeshDto BuildBossMesh(double radius, double height, int segments)
    {
        int ring = segments;
        var positions = new float[ring * 2 * 3];
        for (int s = 0; s < ring; s++)
        {
            double t = s * System.Math.Tau / segments;
            double x = System.Math.Cos(t) * radius, y = System.Math.Sin(t) * radius;
            positions[(2 * s) * 3 + 0] = (float)x;
            positions[(2 * s) * 3 + 1] = (float)y;
            positions[(2 * s) * 3 + 2] = 0f;
            positions[(2 * s + 1) * 3 + 0] = (float)x;
            positions[(2 * s + 1) * 3 + 1] = (float)y;
            positions[(2 * s + 1) * 3 + 2] = (float)height;
        }
        var idx = new System.Collections.Generic.List<int>(segments * 6);
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            int ba = 2 * s, ta = ba + 1, bb = 2 * n, tb = bb + 1;
            idx.Add(ba); idx.Add(bb); idx.Add(tb);
            idx.Add(ba); idx.Add(tb); idx.Add(ta);
        }
        return new MeshDto { MeshId = 77, Positions = positions, Indices = idx.ToArray(), TriangleCount = idx.Count / 3 };
    }

    private sealed class FakeRaycaster : IMeasureRaycaster
    {
        private readonly MeshDto _mesh; private readonly MeasureRaycastHit _hit; private readonly Matrix4d _world;
        public FakeRaycaster(MeshDto mesh, MeasureRaycastHit hit, Matrix4d world) { _mesh = mesh; _hit = hit; _world = world; }
        public double SceneDiagonal => 100.0;
        public long TransformVersion => 1;
        public MeasureRaycastHit? Raycast(Vector3d o, Vector3d d) => _hit;
        public MeshDto? GetMesh(int meshId) => _mesh;
        public SceneNode? GetNode(int nodeId) => null;
        public Matrix4d GetWorldTransform(int nodeId) => _world;
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshMeasurePickerPipeline"`. Expected: with the **old** `CircularFeatureDetectionService` world-space path the boss may still pass radius but the test asserting `picked.Feature.Kind`/pipeline metadata and local-space detection will differ; more concretely the test fails because detection is still done in world space using `ToWorldVector3Array` (no functional pipeline use) — assert `picked.Feature.Confidence > 0` (new pipeline sets it; old service leaves 0).
> Add this assertion to `PickCircularFeature_Boss...`: `Assert.True(picked.Feature.Confidence > 0.0);` — this is what forces the rewire (the legacy service never set Confidence).

- [ ] **Step 3: Implement** — replace the two methods' bodies so detection runs in LOCAL space via the pipeline; transform the ray into local space once; keep the world-space highlight/orientation. Add a lazily-built pipeline field and a local-ray helper.
```csharp
    private readonly CircularFeatureDetectionPipeline _circularPipeline =
        new(new MeshTopologyCache(capacity: 8), CircularDetectionConfig.Default);
```
Replace `PickCircularFeature`:
```csharp
    private PickResult PickCircularFeature(PickRequest request)
    {
        long start = Environment.TickCount64;
        var hit = _raycaster.Raycast(request.RayOrigin, request.RayDirection);
        if (hit is null)
        {
            LogCircularFeature("pick", "miss: raycast returned null", force: true);
            return PickResult.MissSingleton;
        }
        var h = hit.Value;
        MeshDto? mesh = _raycaster.GetMesh(h.MeshId);
        if (mesh is null || mesh.Indices.Length == 0)
        {
            LogCircularFeature("pick", $"miss: mesh lookup failed mesh={h.MeshId}", force: true);
            return PickResult.MissSingleton;
        }

        Matrix4d localToWorld = _raycaster.GetWorldTransform(h.NodeId);
        if (!TryLocalRay(localToWorld, request.RayOrigin, request.RayDirection,
                out Vector3d originLocal, out Vector3d dirLocal))
        {
            LogCircularFeature("pick", "miss: world transform not invertible", force: true);
            return PickResult.MissSingleton;
        }

        IReadOnlyList<Vector3d> verticesLocal = ToLocalVector3List(mesh.Positions);
        double bodyExtent = mesh.Bounds.IsEmpty ? _raycaster.SceneDiagonal : mesh.Bounds.Diagonal;
        CircularDetectionResult detection = _circularPipeline.Detect(
            meshKey: mesh.MeshId,
            transformVersion: _raycaster.TransformVersion,
            verticesLocal: verticesLocal,
            indices: mesh.Indices,
            rayOriginLocal: originLocal,
            rayDirLocal: dirLocal,
            bodyExtent: bodyExtent,
            coarse: false,
            log: detail => LogCircularFeature("pick", detail, force: true,
                throttleKey: $"pick|{h.NodeId}|{h.MeshId}|{detail}"));

        if (!detection.Ok || detection.Feature is null)
        {
            LogCircularFeature("pick",
                $"reject: {detection.Reason} node={h.NodeId} mesh={h.MeshId} elapsedMs={Environment.TickCount64 - start}",
                force: true);
            return PickResult.MissSingleton;
        }

        CircularFeaturePick worldFeature = ToWorldFeature(detection.Feature, localToWorld);
        LogCircularFeature("pick",
            $"success: node={h.NodeId} mesh={h.MeshId} kind={detection.Kind} R={worldFeature.Radius:0.####} conf={worldFeature.Confidence:0.###} elapsedMs={Environment.TickCount64 - start}",
            force: true);
        FaceHighlight highlight = BuildFaceHighlight(
            mesh, BuildHighlightTrianglesFor(worldFeature), localToWorld, FaceHighlightKind.Selected);
        return new PickResult.PickedCircularFeature(
            OrientCircularFeatureToHit(worldFeature, h.WorldPoint), h.NodeId, highlight);
    }
```
Replace `TryHoverCircularFeatureHighlight`:
```csharp
    public FaceHighlight? TryHoverCircularFeatureHighlight(Vector3d rayOrigin, Vector3d rayDirection)
    {
        long start = Environment.TickCount64;
        var hit = _raycaster.Raycast(rayOrigin, rayDirection);
        if (hit is null) return null;
        var h = hit.Value;
        MeshDto? mesh = _raycaster.GetMesh(h.MeshId);
        if (mesh is null || mesh.Indices.Length == 0) return null;

        Matrix4d localToWorld = _raycaster.GetWorldTransform(h.NodeId);
        if (!TryLocalRay(localToWorld, rayOrigin, rayDirection, out Vector3d oL, out Vector3d dL))
            return null;

        IReadOnlyList<Vector3d> verticesLocal = ToLocalVector3List(mesh.Positions);
        double bodyExtent = mesh.Bounds.IsEmpty ? _raycaster.SceneDiagonal : mesh.Bounds.Diagonal;
        CircularDetectionResult detection = _circularPipeline.Detect(
            meshKey: mesh.MeshId, transformVersion: _raycaster.TransformVersion,
            verticesLocal: verticesLocal, indices: mesh.Indices,
            rayOriginLocal: oL, rayDirLocal: dL, bodyExtent: bodyExtent, coarse: true,
            log: detail => LogCircularFeature("hover", detail,
                throttleKey: $"hover|{h.NodeId}|{h.MeshId}|{detail}"));
        if (!detection.Ok || detection.Feature is null)
        {
            LogCircularFeature("hover",
                $"reject: {detection.Reason} node={h.NodeId} mesh={h.MeshId} elapsedMs={Environment.TickCount64 - start}",
                throttleKey: $"hover|reject|{h.NodeId}|{h.MeshId}");
            return null;
        }
        CircularFeaturePick worldFeature = ToWorldFeature(detection.Feature, localToWorld);
        return BuildFaceHighlight(
            mesh, BuildHighlightTrianglesFor(worldFeature), localToWorld, FaceHighlightKind.Hover);
    }
```
Add helpers (local-ray, local vertices, world re-expression, and a highlight-triangle picker that selects the mesh triangles whose centroid lies on the fitted feature so the overlay still highlights geometry):
```csharp
    private static bool TryLocalRay(
        Matrix4d localToWorld, Vector3d originWorld, Vector3d dirWorld,
        out Vector3d originLocal, out Vector3d dirLocal)
    {
        originLocal = default; dirLocal = default;
        if (!localToWorld.TryInvert(out Matrix4d worldToLocal))
            return false;
        originLocal = worldToLocal.TransformPoint(originWorld);
        Vector3d tip = worldToLocal.TransformPoint(originWorld + dirWorld);
        Vector3d d = tip - originLocal;
        if (d.LengthSquared <= 1.0e-24) return false;
        dirLocal = d.Normalized();
        return true;
    }

    private static IReadOnlyList<Vector3d> ToLocalVector3List(float[] positions)
    {
        int count = positions.Length / 3;
        var result = new Vector3d[count];
        for (int i = 0; i < count; i++)
            result[i] = new Vector3d(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        return result;
    }

    private static CircularFeaturePick ToWorldFeature(CircularFeaturePick local, Matrix4d localToWorld)
    {
        Vector3d centerW = localToWorld.TransformPoint(local.Center);
        Vector3d axisW = localToWorld.TransformDirection(local.Axis).Normalized();
        // Uniform-scale factor from the transform (length of a transformed unit dir
        // component); for the rigid+uniform transforms we support this scales R.
        double scale = localToWorld.TransformDirection(Vector3d.UnitX).Length;
        Vector3d uW = localToWorld.TransformDirection(local.U).Normalized();
        Vector3d vW = Vector3d.Cross(axisW, uW).Normalized();
        double radiusW = local.Radius * scale;
        double tubeW = local.TubeRadius * scale;
        IReadOnlyList<Vector3d> curveW = CircularFeatureDetectionService.BuildCurveSegments(
            centerW, uW, vW, radiusW, local.StartAngle, local.Coverage, local.IsClosed);
        return local with
        {
            Center = centerW, Axis = axisW, U = uW, V = vW,
            Radius = radiusW, TubeRadius = tubeW, CurveSegments = curveW,
        };
    }
```
And the highlight-triangle helper (added to the class) selects, in **world** space, the triangles whose centroid is within `MembershipChordK`-scaled tolerance of the feature surface so the overlay matches the fit:
```csharp
    private IReadOnlyList<int> BuildHighlightTrianglesFor(CircularFeaturePick worldFeature)
    {
        // Re-derive a highlight triangle set in world space from the world feature:
        // any model triangle whose centroid sits on the cylinder of radius R about
        // the feature axis (within 2% of R) is part of the recognised band.
        // The caller has the mesh; we collect indices it then transforms.
        return _highlightScratch; // filled by BuildFaceHighlightTriangles below
    }
```
> **Refinement:** rather than a scratch field, fold the membership test directly into a new overload of `BuildFaceHighlight` that takes the world feature and the mesh+transform, iterates mesh triangles, transforms each centroid to world, and keeps those within `0.02·R` radial of the axis. Replace `BuildHighlightTrianglesFor` usage with:
```csharp
    private static FaceHighlight BuildFeatureHighlight(
        MeshDto mesh, Matrix4d localToWorld, CircularFeaturePick worldFeature, FaceHighlightKind kind)
    {
        Vector3d axis = worldFeature.Axis.Normalized();
        double r = worldFeature.Radius;
        double tol = System.Math.Max(0.02 * r, 1.0e-6);
        var keep = new List<int>();
        int triCount = mesh.Indices.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            Vector3d c = TriCentroidWorld(mesh, localToWorld, t);
            Vector3d d = c - worldFeature.Center;
            d -= axis * Vector3d.Dot(d, axis);
            if (System.Math.Abs(d.Length - r) <= tol)
                keep.Add(t);
        }
        if (keep.Count == 0) keep.Add(0);
        return BuildFaceHighlight(mesh, keep, localToWorld, kind);
    }

    private static Vector3d TriCentroidWorld(MeshDto mesh, Matrix4d localToWorld, int tri)
    {
        int i0 = mesh.Indices[3 * tri], i1 = mesh.Indices[3 * tri + 1], i2 = mesh.Indices[3 * tri + 2];
        Vector3d p0 = new(mesh.Positions[3 * i0], mesh.Positions[3 * i0 + 1], mesh.Positions[3 * i0 + 2]);
        Vector3d p1 = new(mesh.Positions[3 * i1], mesh.Positions[3 * i1 + 1], mesh.Positions[3 * i1 + 2]);
        Vector3d p2 = new(mesh.Positions[3 * i2], mesh.Positions[3 * i2 + 1], mesh.Positions[3 * i2 + 2]);
        return localToWorld.TransformPoint((p0 + p1 + p2) * (1.0 / 3.0));
    }
```
> In the two rewired methods replace `BuildFaceHighlight(mesh, BuildHighlightTrianglesFor(worldFeature), localToWorld, kind)` with `BuildFeatureHighlight(mesh, localToWorld, worldFeature, kind)`, and delete the stub `BuildHighlightTrianglesFor`/`_highlightScratch`. Remove the now-unused `_circularFeatures` field and `ToWorldVector3Array` call sites in the two circular methods (leave `ToWorldVector3Array` for the face path). `mesh.Bounds.IsEmpty`/`.Diagonal` exist on `BoundingBox`; if `IsEmpty` is absent use `mesh.Bounds.Equals(BoundingBox.Empty)`.

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeshMeasurePickerPipeline"` then the full circular suite `--filter "FullyQualifiedName~CircularFeature"` to confirm the pre-existing `CircularFeatureMeasurementTests` (`Picker_CircularFeatureHover_...`, `Picker_CircularFeaturePick_...`, `Picker_CircularFeaturePick_OrientsArcInWorldSpaceAfterTap`) still pass against the new pipeline path.
> If `Picker_CircularFeaturePick_OrientsArcInWorldSpaceAfterTap` regresses (it shoots `Vector3d.UnitZ` from origin but the fake returns a fixed hit), confirm `OrientCircularFeatureToHit` is still applied to the world feature (it is, last line of `PickCircularFeature`).

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs src/FabricationAssistant.App.Tests/Measurement/MeshMeasurePickerPipelineTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
refactor(measure): route circular pick/hover through pipeline in LOCAL space

Stops materialising the whole mesh to world every hover; rays are inverted
into local space once and topology is cached on (meshId, transformVersion).
Pick uses full RANSAC+GN+torus; hover uses coarse cylinder-only.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.10: Drop the Android `preferredNode` raycast override (front-face / nearest-surface only)

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.App.Android/Measurement/AndroidMeasureRaycaster.cs`
- Modify: `[ANDROID] Android/src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/Measurement/AndroidMeasureRaycasterNearestSurfaceTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — two coaxial candidate bodies; the nearer surface must win even when a (now-removed) `preferredNode` would have selected the far one. Because the override is being removed, the test asserts the raycaster always returns the nearest hit and that `SetPreferredNode` no longer changes the result. (If `AndroidMeasureRaycaster` is not yet linked into the Android test project, add a `<Compile Include>` — see Step 3.)
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Measurement;

public sealed class AndroidMeasureRaycasterNearestSurfaceTests
{
    [Fact]
    public void Raycast_ReturnsNearestSurface_IgnoringPreferredNode()
    {
        // Build a scene with a NEAR node (id=1) at z=1 and a FAR node (id=2) at z=10,
        // both pierced by a +Z ray from the origin. Helper builds the AndroidMeasureRaycaster
        // over a two-quad scene (see AndroidMeasureTestScene fixture in this suite).
        AndroidMeasureTestScene scene = AndroidMeasureTestScene.TwoCoaxialQuads(
            nearNodeId: 1, nearZ: 1.0, farNodeId: 2, farZ: 10.0);
        AndroidMeasureRaycaster rc = scene.Raycaster;

        MeasureRaycastHit? withoutPref = rc.Raycast(Vector3d.Zero, Vector3d.UnitZ);
        Assert.NotNull(withoutPref);
        Assert.Equal(1, withoutPref!.Value.NodeId); // nearest wins

        rc.SetPreferredNode(2); // request the FAR node explicitly
        MeasureRaycastHit? withPref = rc.Raycast(Vector3d.Zero, Vector3d.UnitZ);
        Assert.NotNull(withPref);
        Assert.Equal(1, withPref!.Value.NodeId); // override removed -> nearest STILL wins
    }
}
```
> If no `AndroidMeasureTestScene` fixture exists yet in the Android suite, this task additionally creates a minimal one. Inspect the suite first: `Grep "AndroidMeasureTestScene"` under `Android/src/FabricationAssistant.App.Android.Tests`. If absent, build the fixture by constructing a `Scene` with two single-quad mesh nodes and wrapping it in `AndroidMeasureRaycaster` exactly as `MainActivity`/`AndroidMeasureIntegration` does (use the existing constructor; pass a `() => scene` accessor). Keep the fixture in the same new test file.

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~AndroidMeasureRaycasterNearestSurface"`. Expected: the `withPref` assertion fails — `SetPreferredNode(2)` currently short-circuits to the far node (`AndroidMeasureRaycaster.cs:151-175`), returning `NodeId == 2`.

- [ ] **Step 3: Implement** — delete the `preferredNode` short-circuit block (`:151-175`) so every raycast falls through to the nearest-surface scan (`:178+`). Keep `SetPreferredNode`/`ClearPreferredNode`/`_preferredNodeId` as no-ops for source compatibility with `AndroidMeasureIntegration` (or remove their call sites — done below), but they must not influence ordering. Concretely, in `AndroidMeasureRaycaster.cs` remove the entire `if (_preferredNodeId is int preferredNodeId) { ... }` block shown at `:151-175`. Then neutralise the setter so stale state can't matter:
```csharp
    // preferredNode override removed (2026-06-06 circular redesign): the GPU pick
    // is a re-validated HINT, never an override. Kept as no-ops so existing callers
    // compile; they no longer affect nearest-surface ordering.
    public void SetPreferredNode(int nodeId) { /* intentionally ignored */ }

    public void ClearPreferredNode() { /* intentionally ignored */ }
```
> Also drop the `_preferredNodeId` field and the two save/restore lines at `:108-116` if they become unused after removing the block — the compiler will flag them; delete the dead field. In `AndroidMeasureIntegration.cs` remove the two calls (`:368 _raycaster.SetPreferredNode(nodeId);` and `:377 _raycaster.ClearPreferredNode();`) and the surrounding `if (preferredNodeId is int nodeId && nodeId > 0)` guard, leaving `HandleClick` to pass the ray straight through. (Leave the `preferredNodeId` parameter on `HandleClick` for ABI stability; mark it `_ = preferredNodeId;` so it is observably unused, and drop the `preferredNode={...}` token from the log line at `:365` or keep it for diagnostics.)
> If `AndroidMeasureRaycaster` is not currently compiled into the Android **test** csproj, add `<Compile Include="..\FabricationAssistant.App.Android\Measurement\AndroidMeasureRaycaster.cs" Link="Measurement\AndroidMeasureRaycaster.cs" />` (and any newly-referenced Core type) to `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` as an explicit step, then rerun.

- [ ] **Step 4: Run it, expect PASS** — `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~AndroidMeasureRaycasterNearestSurface"`. (On `CS2012`/lock: `dotnet build-server shutdown` then retry.)

- [ ] **Step 5: Commit** (ANDROID repo, explicit paths)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add src/FabricationAssistant.App.Android/Measurement/AndroidMeasureRaycaster.cs src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs src/FabricationAssistant.App.Android.Tests/Measurement/AndroidMeasureRaycasterNearestSurfaceTests.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
fix(measure): drop preferredNode raycast override; nearest-surface only

GPU id-buffer pick is a re-validated hint, never an override. Removes the
short-circuit that let a tap select the far/wrong body; SetPreferredNode is
now a no-op for source compatibility.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.11: Device-fixture regression — the three failing fillet taps + the control hole

**Files:**
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularPipelineDeviceRegressionTests.cs` (Create)

- [ ] **Step 1: Write the failing test** — encode the four device taps from §1 as pipeline calls. Fillets must return correct R **or** an explicit reject (never a wrong silent value); the hole must return Ø. Uses Group A fillet/torus/hole fixtures sized to the logged radii (fillet R≈0.042, hole R≈0.0022). The key correctness property: **no garbage** — any accepted fillet value is within tolerance of the true R; otherwise the result is an explicit reject.
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularPipelineDeviceRegressionTests
{
    private static CircularFeatureDetectionPipeline NewPipeline() =>
        new(new MeshTopologyCache(capacity: 8), CircularDetectionConfig.Default);

    // Tap 1 & 3 (mesh 551): a straight fillet R=0.0421 that previously committed
    // garbage (0.004 / divergent 0.097). Must now be either correct-R or rejected.
    [Theory]
    [InlineData(0.5)]   // tap near arc middle
    [InlineData(0.05)]  // tap near the corner vertex-fan that produced R=0.004
    [InlineData(0.95)]  // tap near the far end
    public void Tap1And3_StraightFillet_CorrectRadiusOrExplicitReject(double along)
    {
        const double trueR = 0.0421;
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FilletStraight(
            radius: trueR, length: 0.30, arcSegments: 16, sweepRadians: System.Math.PI / 2.0);
        // Shoot at the convex quarter-round at parametric position 'along' down its length.
        double z = 0.30 * along;
        var aim = new Vector3d(trueR * 0.7071, trueR * 0.7071, z);
        var origin = aim + new Vector3d(1.0, 1.0, 0.0).Normalized() * 1.0;
        var dir = (aim - origin).Normalized();

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 551, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir, bodyExtent: 0.30, coarse: false);

        if (r.Ok)
        {
            Assert.Equal(CircularKind.Radius, r.Kind);
            // The critical property: a committed value is NEVER garbage.
            Assert.InRange(r.Feature!.Radius, trueR * 0.85, trueR * 1.15);
        }
        else
        {
            // An explicit reject is acceptable; a silent wrong value is not.
            Assert.NotEqual(RejectReason.None, r.Reason);
            Assert.Null(r.Feature);
        }
    }

    // Tap 2 (mesh 430): a fillet that previously MISSED (flooded, collapsed to R=0.0021).
    // It must now detect the correct R or explicitly reject — never collapse silently.
    [Fact]
    public void Tap2_FloodedFillet_DoesNotCollapseToGarbage()
    {
        const double trueR = 0.012;
        (Vector3d[] v, int[] i) = CircularMeshFixtures.FilletStraight(
            radius: trueR, length: 0.25, arcSegments: 14, sweepRadians: System.Math.PI / 2.0);
        var aim = new Vector3d(trueR * 0.7071, trueR * 0.7071, 0.12);
        var origin = aim + new Vector3d(1, 1, 0).Normalized() * 1.0;
        var dir = (aim - origin).Normalized();

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 430, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir, bodyExtent: 0.25, coarse: false);

        // The historical failure was a SILENT collapse to R=0.0021 (~6x too small).
        Assert.False(r.Ok && r.Feature!.Radius < trueR * 0.5,
            $"regression: silent collapse to R={r.Feature?.Radius}");
        if (r.Ok) Assert.InRange(r.Feature!.Radius, trueR * 0.8, trueR * 1.2);
    }

    // Control tap (mesh 349): a clean through-hole must return Ø with the right radius.
    [Fact]
    public void ControlTap_Hole_ReturnsDiameter()
    {
        const double trueR = 0.0022;
        (Vector3d[] v, int[] i) = CircularMeshFixtures.HoleInPlate(
            radius: trueR, plate: 0.05, thickness: 0.01, segments: 48);
        var aim = new Vector3d(trueR, 0.0, 0.005);
        var origin = new Vector3d(0.05, 0.0, 0.005);
        var dir = (aim - origin).Normalized();

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 349, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir, bodyExtent: 0.05, coarse: false);

        Assert.True(r.Ok, $"control hole rejected: {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.True(r.Feature!.IsClosed);
        Assert.Equal(trueR, r.Feature.Radius, 3);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularPipelineDeviceRegression"`. Expected: any row still red exposes a real gap in Groups D–H tuning (e.g. `Tap1And3` corner tap still accepts a too-small R, or the control hole rejects). These are the acceptance bar for the whole feature.

- [ ] **Step 3: Implement** — Group I production code should already satisfy these if Groups D–H are correct. If a row is red, the fix is **not** in the pipeline orchestration but in the relevant kernel; within scope here, the only orchestration fix permitted is tightening the corner-fan case: ensure the gate's reject reason propagates (already wired in I.5). If `Tap1And3(0.05)` accepts garbage, confirm `coarse:false` reaches `FitBest` and the gate uses `bodyExtent` (it does). No new pipeline code unless a row demands it; if so, the minimal change is to reject when `fit.Radius < 0.5 * MembershipChordK * localChord` — add to `Detect` after the fit:
```csharp
        // Guard against vertex-fan collapse: a fitted radius smaller than the local
        // tessellation chord is geometrically meaningless -> explicit reject.
        if (fit.Radius < 0.5 * _config.MembershipChordK * EstimateLocalChord(topo, region))
        {
            log?.Invoke($"reject: degenerate radius {fit.Radius:0.#####}");
            return CircularDetectionResult.Reject(RejectReason.LowConfidence);
        }
```
with helper:
```csharp
    private static double EstimateLocalChord(MeshTopology topo, IReadOnlyList<int> region)
    {
        // Mean triangle "size" as sqrt(2*area) — a proxy for the local chord length.
        double sum = 0.0; int n = 0;
        foreach (int t in region)
        {
            double area = topo.Triangles[t].Area;
            if (area > 0.0) { sum += System.Math.Sqrt(2.0 * area); n++; }
        }
        return n == 0 ? 0.0 : sum / n;
    }
```

- [ ] **Step 4: Run it, expect PASS** — `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularPipelineDeviceRegression"`, then the whole circular suite `--filter "FullyQualifiedName~CircularFeature"` and the pipeline/e2e suites to confirm no regression.

- [ ] **Step 5: Commit** (PARENT repo)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularPipelineDeviceRegressionTests.cs src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m @'
test(measure): device-tap regression - three failing fillets + control hole

Fillets return correct R or explicit reject (never garbage); hole returns Diameter.
Adds vertex-fan degenerate-radius guard.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task I.12: Android-mirror end-to-end pipeline test (suite parity)

**Files:**
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/Measurement/CircularPipelineAndroidMirrorTests.cs` (Create)
- Modify (if needed): `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`

- [ ] **Step 1: Write the failing test** — mirror the boss-Ø and straight-fillet-R end-to-end on the Android suite to prove the auto-linked Core source compiles and behaves identically there. (Fixtures `CircularMeshFixtures` live in the host test namespace `FabricationAssistant.App.Tests.Measurement`; the Android suite cannot reference the host test assembly, so this test builds its meshes inline with the same math — a self-contained boss + quarter-round.)
```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Measurement;

public sealed class CircularPipelineAndroidMirrorTests
{
    private static CircularFeatureDetectionPipeline NewPipeline() =>
        new(new MeshTopologyCache(capacity: 4), CircularDetectionConfig.Default);

    [Fact]
    public void Detect_Boss_ReturnsDiameter()
    {
        (Vector3d[] v, int[] i) = Boss(radius: 2.0, height: 5.0, segments: 48);
        var origin = new Vector3d(40.0, 0.0, 2.5);
        var dir = -Vector3d.UnitX;

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 1, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir, bodyExtent: 5.0, coarse: false);

        Assert.True(r.Ok, $"{r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.Equal(2.0, r.Feature!.Radius, 1);
    }

    [Fact]
    public void Detect_Coarse_Boss_IsCylinderOnly()
    {
        (Vector3d[] v, int[] i) = Boss(radius: 1.5, height: 4.0, segments: 40);
        var origin = new Vector3d(30.0, 0.0, 2.0);
        var dir = -Vector3d.UnitX;

        CircularDetectionResult r = NewPipeline().Detect(
            meshKey: 2, transformVersion: 0, verticesLocal: v, indices: i,
            rayOriginLocal: origin, rayDirLocal: dir, bodyExtent: 4.0, coarse: true);

        Assert.True(r.Ok);
        Assert.Equal(SurfaceKind.Cylinder, r.Feature!.Kind);
        Assert.Equal(1.5, r.Feature.Radius, 1);
    }

    private static (Vector3d[] v, int[] i) Boss(double radius, double height, int segments)
    {
        var verts = new Vector3d[segments * 2];
        for (int s = 0; s < segments; s++)
        {
            double t = s * System.Math.Tau / segments;
            double x = System.Math.Cos(t) * radius, y = System.Math.Sin(t) * radius;
            verts[2 * s] = new Vector3d(x, y, 0);
            verts[2 * s + 1] = new Vector3d(x, y, height);
        }
        var idx = new System.Collections.Generic.List<int>(segments * 6);
        for (int s = 0; s < segments; s++)
        {
            int n = (s + 1) % segments;
            int ba = 2 * s, ta = ba + 1, bb = 2 * n, tb = bb + 1;
            idx.Add(ba); idx.Add(bb); idx.Add(tb);
            idx.Add(ba); idx.Add(tb); idx.Add(ta);
        }
        return (verts, idx.ToArray());
    }
}
```

- [ ] **Step 2: Run it, expect FAIL** — `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~CircularPipelineAndroidMirror"`. Expected: **CS0246** for `CircularFeatureDetectionPipeline` / `MeshTopologyCache` / `CircularDetectionConfig` / `CircularDetectionResult` / `SurfaceKind` / `CircularKind` — the Android test project links Core selectively and these new types are not yet included.

- [ ] **Step 3: Implement** — add explicit `<Compile Include>` entries for each Group I + consumed contract type to the Android test csproj (the App.Android main project already gets them via the wildcard; the **test** project links selectively). Locate the `<ItemGroup>` of `<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\...">` links and append:
```xml
<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CircularFeatureDetectionPipeline.cs" Link="Measurement\Engine\CircularFeatureDetectionPipeline.cs" />
<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CircularDetectionConfig.cs" Link="Measurement\Engine\CircularDetectionConfig.cs" />
<Compile Include="..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CircularDetectionResult.cs" Link="Measurement\Engine\CircularDetectionResult.cs" />
```
> Verify the relative depth first by opening the csproj and matching an existing `<Compile Include>` prefix; adjust `..\..\..` to match. Also ensure the transitive Group B–H types (`MeshTopologyCache`, `MeshTopology`, `SeedResolver`, `SurfaceRegionGrower`, `AnalyticSurfaceFitter`, `EdgeLoopFitter`, `FeatureClassifier`, `ChamferDetector`, `FitAcceptanceGate`, and the contract record files) are linked — those are added by their own groups' Android-mirror tasks; if any is missing at this point, add its `<Compile Include>` here as an explicit step (the build error names the exact missing type). No production code changes.

- [ ] **Step 4: Run it, expect PASS** — `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~CircularPipelineAndroidMirror"` (on `CS2012`/lock: `dotnet build-server shutdown`, retry; if a `.NET Host (PID)` holds a DLL, `Stop-Process -Id <pid> -Force`).

- [ ] **Step 5: Commit** (ANDROID repo, explicit paths)
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add src/FabricationAssistant.App.Android.Tests/Measurement/CircularPipelineAndroidMirrorTests.cs src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
test(measure): Android-mirror pipeline e2e (boss Diameter, coarse cylinder-only)

Links new Core pipeline types into the Android test project and proves parity.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

**Group I outputs (for downstream groups / integration):**
- `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionPipeline.cs` — the new entry point replacing `CircularFeatureDetectionService.DetectFromSeed`.
- `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/CircularDetectionConfig.cs`, `CircularDetectionResult.cs`, extended `PickTypes.cs`, extended `IMeasureRaycaster.cs`.
- `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs` — circular pick/hover now run in LOCAL space via the pipeline (keyed on `meshId`+`TransformVersion`); the per-hover `ToWorldVector3Array` for circular features is gone.
- `[ANDROID] AndroidMeasureRaycaster.cs` / `AndroidMeasureIntegration.cs` — `preferredNode` override removed (nearest-surface only).
- Tests (PARENT): `CircularFeaturePickExtensionTests`, `CircularDetectionConfigTests`, `CircularDetectionResultTests`, `CircularFeatureDetectionPipelineTests`, `CircularPipelineEndToEndTests`, `MeasureRaycasterTransformVersionTests`, `MeshMeasurePickerPipelineTests`, `CircularPipelineDeviceRegressionTests`. Tests (ANDROID): `AndroidMeasureRaycasterNearestSurfaceTests`, `CircularPipelineAndroidMirrorTests`.
- **Note for the MeasurementSession/Builders groups:** `CircularDetectionResult.Chamfer` is currently surfaced by the pipeline but `PickResult.PickedCircularFeature` carries only a `CircularFeaturePick`; the chamfer-commit path (mapping `ChamferFit` into a `Measurement` chamfer variant per spec §7) is owned by the CommitGuard/Builders group and is intentionally **not** wired here — `MeshMeasurePicker` returns `Miss` for `CircularKind.Chamfer` until that group lands. Flag this as the one open integration seam.

---

Host tests reference the Core via the App project (project reference), so new Core types compile automatically. Now I have full context. Let me write the Group J plan section.

Based on my research, I'll now produce the complete Group J markdown task section. Key facts I've confirmed:
- `Vector3d` uses `.Length`/`.LengthSquared` as **properties** (not methods) and `.Normalized()` as a method; `Vector3d.Dot/Cross/Distance/BuildPerpendicular` are static.
- `CircularFeatureMeasurement` currently has no `Coverage`/`Confidence`/`StartAngle` fields — I must add them (with defaults to keep existing builders/tests compiling).
- The presenter's `BuildCurveLines` currently draws a 48-seg 360° fallback; I must remove that phantom for radius.
- `PresentationSnapshot` needs new lists; `TransformedBy` must transform them.
- Host tests reference Core via the App project reference (auto-compile).

---

## GROUP J — Professional dimension presentation primitives + model (PARENT Core)

> **Preamble (applies to every task in this group).** All work here is in the **PARENT** repo (`C:/Users/skritikos/Desktop/Fabrication Assistant`), under `src/FabricationAssistant.Core/Measurement/Presentation/**`, `src/FabricationAssistant.Core/Measurement/Domain/**`, `src/FabricationAssistant.Core/Measurement/Engine/SceneUnitSystemService.cs`, with host tests in `src/FabricationAssistant.App.Tests/Measurement/**`. New `.cs` files under `src/FabricationAssistant.Core/Measurement/**` are auto-compile-linked into the Android app via a wildcard, and into the host test project via the `FabricationAssistant.App` project reference — **no csproj edit is needed** for these Core files. Commit all of these in the **PARENT** repo.
>
> **Canonical types used here** (namespace `FabricationAssistant.Core.Measurement.Presentation`): `enum DimensionConfidence { Committed, Preview, Marginal }`, `readonly record struct ArrowheadPrimitive(Vector3d Position, Vector3d Direction, double Size)`, `readonly record struct LeaderPrimitive(IReadOnlyList<Vector3d> Points)`, `readonly record struct CenterMarkPrimitive(Vector3d Position, Vector3d AxisU, Vector3d AxisV, double Size)`. `PresentationSnapshot` gains `Arrowheads`, `Leaders`, `CenterMarks` lists + `DimensionConfidence Confidence`.
>
> **API reality check** (the canonical contract abbreviates these): in this codebase `Vector3d.Length` and `Vector3d.LengthSquared` are **properties**, `Vector3d.Normalized()` is a method, and `Vector3d.Dot`, `Vector3d.Cross`, `Vector3d.Distance`, `Vector3d.BuildPerpendicular` are static. The `Ø` glyph is `U+00D8` (`'\u00D8'`). Host tests run on TFM `net8.0-windows`.
>
> **Build-lock note (once):** if a build fails with CSC `CS2012` / `XARLP7024` file-lock, run `dotnet build-server shutdown` then retry; if a `.NET Host (PID)` still holds a DLL, `Stop-Process -Id <pid> -Force`.
>
> **Internal ordering:** J.1 adds the primitive types; J.2 extends `PresentationSnapshot` (incl. `TransformedBy`); J.3 extends the domain `CircularFeatureMeasurement` with `Coverage`/`Confidence`; J.4 adds the unit/precision auto-format to `SceneUnitSystemService`; J.5 rewrites `MeasurementPresenter.CircularFeature` (diameter); J.6 finishes it (radius true-arc + chamfer) and deletes the 360° phantom.

---

## Group J — Presentation primitives + professional dimension model (Core)

> Adds arrowhead/leader/center-mark primitives + `DimensionConfidence` to `Primitives.cs`/`PresentationSnapshot.cs`, rewrites `MeasurementPresenter.CircularFeature` for pro Ø/R/chamfer callouts (true-coverage arc, no 360° phantom), and unit/precision auto-format. PARENT-repo. Group J owns these files (correction #6).

### Task J.1: Add Arrowhead / Leader / CenterMark primitives + DimensionConfidence

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Presentation/Primitives.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/DimensionPrimitivesTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create `src/FabricationAssistant.App.Tests/Measurement/DimensionPrimitivesTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class DimensionPrimitivesTests
{
    [Fact]
    public void Arrowhead_StoresPositionDirectionSize()
    {
        var a = new ArrowheadPrimitive(new Vector3d(1, 2, 3), new Vector3d(0, 0, 1), 0.25);
        Assert.Equal(1.0, a.Position.X, 12);
        Assert.Equal(2.0, a.Position.Y, 12);
        Assert.Equal(3.0, a.Position.Z, 12);
        Assert.Equal(1.0, a.Direction.Z, 12);
        Assert.Equal(0.25, a.Size, 12);
    }

    [Fact]
    public void Leader_StoresPolylinePointsInOrder()
    {
        var pts = new[] { new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), new Vector3d(1, 1, 0) };
        var l = new LeaderPrimitive(pts);
        Assert.Equal(3, l.Points.Count);
        Assert.Equal(1.0, l.Points[1].X, 12);
        Assert.Equal(1.0, l.Points[2].Y, 12);
    }

    [Fact]
    public void CenterMark_StoresPositionAndTwoAxesAndSize()
    {
        var c = new CenterMarkPrimitive(new Vector3d(5, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.5);
        Assert.Equal(5.0, c.Position.X, 12);
        Assert.Equal(1.0, c.AxisU.X, 12);
        Assert.Equal(1.0, c.AxisV.Y, 12);
        Assert.Equal(0.5, c.Size, 12);
    }

    [Fact]
    public void DimensionConfidence_HasThreeMembers()
    {
        Assert.Equal(0, (int)DimensionConfidence.Committed);
        Assert.Equal(1, (int)DimensionConfidence.Preview);
        Assert.Equal(2, (int)DimensionConfidence.Marginal);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~DimensionPrimitives"`
  Expected failure: **compile error** — `ArrowheadPrimitive`, `LeaderPrimitive`, `CenterMarkPrimitive`, `DimensionConfidence` do not exist (`CS0246: The type or namespace name 'ArrowheadPrimitive' could not be found`).

- [ ] **Step 3: Implement.** Append to `src/FabricationAssistant.Core/Measurement/Presentation/Primitives.cs` (after the `LabelPrimitive` line, before EOF):

```csharp
/// <summary>
/// Confidence styling for a dimension snapshot. Committed fits render solid;
/// Preview (hover ghost) and Marginal (just-below-accept) render dashed/greyed
/// so a user can never mistake a provisional value for a committed one.
/// </summary>
public enum DimensionConfidence { Committed, Preview, Marginal }

/// <summary>
/// A filled triangular dimension terminator. <paramref name="Direction"/> points
/// FROM the arrow tip back along the dimension line (the way the head "fans out");
/// the renderer builds the two barb vertices in the plane spanned by Direction and
/// the view. <paramref name="Size"/> is the barb length in scene units.
/// </summary>
public readonly record struct ArrowheadPrimitive(Vector3d Position, Vector3d Direction, double Size);

/// <summary>
/// A leader polyline (e.g. radius callout: arc point → shoulder → text gap).
/// Points are in draw order; the renderer connects consecutive points.
/// </summary>
public readonly record struct LeaderPrimitive(IReadOnlyList<Vector3d> Points);

/// <summary>
/// A center-mark cross at a circular feature's axis. <paramref name="AxisU"/> and
/// <paramref name="AxisV"/> are the (orthonormal) in-plane directions of the two
/// cross strokes; <paramref name="Size"/> is the half-length of each stroke.
/// </summary>
public readonly record struct CenterMarkPrimitive(Vector3d Position, Vector3d AxisU, Vector3d AxisV, double Size);
```

- [ ] **Step 4: Run it, expect PASS.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~DimensionPrimitives"`
  Expected: 4 passed.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Presentation/Primitives.cs src/FabricationAssistant.App.Tests/Measurement/DimensionPrimitivesTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): add arrowhead/leader/center-mark primitives + DimensionConfidence

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task J.2: Extend PresentationSnapshot with the new primitive lists + Confidence (and transform them)

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Presentation/PresentationSnapshot.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/PresentationSnapshotShapeTests.cs` (create)

> The snapshot constructor gets three new positional list params plus a `Confidence` param. To avoid touching the dozen existing `new PresentationSnapshot(Id, Style, Balls, Disks, Lines, Labels)` call-sites in `MeasurementPresenter`, give the new params **defaults** (`null` lists coalesced to empty in the body, `Confidence = DimensionConfidence.Committed`). Existing call-sites keep compiling unchanged; only the circular-feature builder (J.5/J.6) passes the new args.

- [ ] **Step 1: Write the failing test.** Create `src/FabricationAssistant.App.Tests/Measurement/PresentationSnapshotShapeTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class PresentationSnapshotShapeTests
{
    private static PresentationSnapshot MakeWithArrowheads()
        => new PresentationSnapshot(
            Id: null,
            Style: PresentationStyle.Committed,
            Balls: System.Array.Empty<BallPrimitive>(),
            Disks: System.Array.Empty<DiskPrimitive>(),
            Lines: System.Array.Empty<LinePrimitive>(),
            Labels: System.Array.Empty<LabelPrimitive>(),
            Arrowheads: new[]
            {
                new ArrowheadPrimitive(new Vector3d(2, 0, 0), Vector3d.UnitX, 0.1),
            },
            Leaders: new[]
            {
                new LeaderPrimitive(new[] { new Vector3d(2, 0, 0), new Vector3d(3, 1, 0) }),
            },
            CenterMarks: new[]
            {
                new CenterMarkPrimitive(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 0.2),
            },
            Confidence: DimensionConfidence.Preview);

    [Fact]
    public void NewLists_AndConfidence_AreStored()
    {
        var s = MakeWithArrowheads();
        Assert.Single(s.Arrowheads);
        Assert.Single(s.Leaders);
        Assert.Single(s.CenterMarks);
        Assert.Equal(DimensionConfidence.Preview, s.Confidence);
    }

    [Fact]
    public void LegacyConstructor_DefaultsToEmptyListsAndCommitted()
    {
        var s = new PresentationSnapshot(
            null, PresentationStyle.Committed,
            System.Array.Empty<BallPrimitive>(),
            System.Array.Empty<DiskPrimitive>(),
            System.Array.Empty<LinePrimitive>(),
            System.Array.Empty<LabelPrimitive>());
        Assert.Empty(s.Arrowheads);
        Assert.Empty(s.Leaders);
        Assert.Empty(s.CenterMarks);
        Assert.Equal(DimensionConfidence.Committed, s.Confidence);
    }

    [Fact]
    public void TransformedBy_Translation_MovesArrowheadLeaderCenterMark_PreservesConfidence()
    {
        var s = MakeWithArrowheads();
        var moved = s.TransformedBy(Matrix4d.CreateTranslation(0, 10, 0));

        Assert.Equal(10.0, moved.Arrowheads[0].Position.Y, 9);
        Assert.Equal(10.0, moved.Leaders[0].Points[0].Y, 9);
        Assert.Equal(11.0, moved.Leaders[0].Points[1].Y, 9);
        Assert.Equal(10.0, moved.CenterMarks[0].Position.Y, 9);
        // Direction/axes are directions: a pure translation must not move them.
        Assert.Equal(1.0, moved.Arrowheads[0].Direction.X, 9);
        Assert.Equal(1.0, moved.CenterMarks[0].AxisU.X, 9);
        Assert.Equal(DimensionConfidence.Preview, moved.Confidence);
    }

    [Fact]
    public void TransformedBy_RotationZ_RotatesArrowheadDirection()
    {
        var s = MakeWithArrowheads();
        var moved = s.TransformedBy(Matrix4d.CreateRotationZ(System.Math.PI / 2)); // X -> Y
        Assert.Equal(0.0, moved.Arrowheads[0].Direction.X, 9);
        Assert.Equal(1.0, moved.Arrowheads[0].Direction.Y, 9);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~PresentationSnapshotShape"`
  Expected failure: **compile error** — `PresentationSnapshot` has no `Arrowheads`/`Leaders`/`CenterMarks`/`Confidence` and the constructor has no such parameters (`CS1739`/`CS0117`).

- [ ] **Step 3: Implement.** Replace the entire body of `src/FabricationAssistant.Core/Measurement/Presentation/PresentationSnapshot.cs` with:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;

namespace FabricationAssistant.Core.Measurement.Presentation;

public sealed record PresentationSnapshot(
    MeasurementId? Id,
    PresentationStyle Style,
    IReadOnlyList<BallPrimitive> Balls,
    IReadOnlyList<DiskPrimitive> Disks,
    IReadOnlyList<LinePrimitive> Lines,
    IReadOnlyList<LabelPrimitive> Labels,
    IReadOnlyList<ArrowheadPrimitive>? Arrowheads = null,
    IReadOnlyList<LeaderPrimitive>? Leaders = null,
    IReadOnlyList<CenterMarkPrimitive>? CenterMarks = null,
    DimensionConfidence Confidence = DimensionConfidence.Committed)
{
    public IReadOnlyList<ArrowheadPrimitive> Arrowheads { get; init; }
        = Arrowheads ?? System.Array.Empty<ArrowheadPrimitive>();

    public IReadOnlyList<LeaderPrimitive> Leaders { get; init; }
        = Leaders ?? System.Array.Empty<LeaderPrimitive>();

    public IReadOnlyList<CenterMarkPrimitive> CenterMarks { get; init; }
        = CenterMarks ?? System.Array.Empty<CenterMarkPrimitive>();

    /// <summary>
    /// Returns a copy with every primitive rigidly transformed by <paramref name="delta"/>:
    /// positions via <see cref="Matrix4d.TransformPoint"/>, directions (disk normal/U,
    /// line-aligned label axes, arrowhead direction, center-mark axes) via
    /// <see cref="Matrix4d.TransformDirection"/>. Style, id, label text, and confidence
    /// are unchanged. Used to make an anchored measurement follow its body.
    /// </summary>
    public PresentationSnapshot TransformedBy(Matrix4d delta)
    {
        var balls = new BallPrimitive[Balls.Count];
        for (int i = 0; i < balls.Length; i++)
            balls[i] = new BallPrimitive(delta.TransformPoint(Balls[i].Center));

        var disks = new DiskPrimitive[Disks.Count];
        for (int i = 0; i < disks.Length; i++)
            disks[i] = new DiskPrimitive(
                delta.TransformPoint(Disks[i].Center),
                delta.TransformDirection(Disks[i].Normal),
                delta.TransformDirection(Disks[i].U),
                Disks[i].Radius);

        var lines = new LinePrimitive[Lines.Count];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = new LinePrimitive(
                delta.TransformPoint(Lines[i].Start),
                delta.TransformPoint(Lines[i].End));

        var labels = new LabelPrimitive[Labels.Count];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = new LabelPrimitive(
                Labels[i].Text,
                delta.TransformPoint(Labels[i].Anchor),
                TransformAlignment(Labels[i].Alignment, delta));

        var arrows = new ArrowheadPrimitive[Arrowheads.Count];
        for (int i = 0; i < arrows.Length; i++)
            arrows[i] = new ArrowheadPrimitive(
                delta.TransformPoint(Arrowheads[i].Position),
                delta.TransformDirection(Arrowheads[i].Direction),
                Arrowheads[i].Size);

        var leaders = new LeaderPrimitive[Leaders.Count];
        for (int i = 0; i < leaders.Length; i++)
        {
            var src = Leaders[i].Points;
            var pts = new Vector3d[src.Count];
            for (int p = 0; p < pts.Length; p++)
                pts[p] = delta.TransformPoint(src[p]);
            leaders[i] = new LeaderPrimitive(pts);
        }

        var marks = new CenterMarkPrimitive[CenterMarks.Count];
        for (int i = 0; i < marks.Length; i++)
            marks[i] = new CenterMarkPrimitive(
                delta.TransformPoint(CenterMarks[i].Position),
                delta.TransformDirection(CenterMarks[i].AxisU),
                delta.TransformDirection(CenterMarks[i].AxisV),
                CenterMarks[i].Size);

        return new PresentationSnapshot(
            Id, Style, balls, disks, lines, labels, arrows, leaders, marks, Confidence);
    }

    private static LabelAlignment TransformAlignment(LabelAlignment alignment, Matrix4d delta) => alignment switch
    {
        LabelAlignment.LineAligned la => new LabelAlignment.LineAligned(
            delta.TransformDirection(la.LineDirection),
            delta.TransformDirection(la.FacingHint)),
        // CameraBillboard is view-relative — nothing to transform.
        _ => alignment,
    };
}
```

> Note: because the constructor's positional params for the new lists are declared `IReadOnlyList<…>?` but the record also declares `init` properties of the non-nullable type with the same names, the positional record param feeds the property initializer (`Arrowheads ?? Array.Empty`). This is the standard C# pattern for "constructor param with normalization into a property." The `delta` returned at the end passes the already-non-null `arrows`/`leaders`/`marks` arrays.

- [ ] **Step 4: Run it, expect PASS.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~PresentationSnapshotShape"`
  Expected: 4 passed. Then run the existing presenter suite to prove the defaulted constructor kept legacy call-sites green:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~MeasurementPresenter"`
  Expected: all existing presenter tests still pass.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Presentation/PresentationSnapshot.cs src/FabricationAssistant.App.Tests/Measurement/PresentationSnapshotShapeTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): carry arrowhead/leader/center-mark lists + confidence on PresentationSnapshot

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task J.3: Add Coverage + Confidence to CircularFeatureMeasurement (true-arc + styling inputs)

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs`
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMeasurementCoverageTests.cs` (create)

> The presenter needs **true coverage** (so radius arcs stop at the real sweep, no 360° phantom) and a **confidence** flag (so marginal fits render dashed). `CircularFeaturePick` already carries `StartAngle` and `Coverage`; thread them into the domain measurement. New params get defaults so the existing `BuildCircularFeature` call-sites and the J-unrelated builder/session tests stay green; the builder is updated to forward `feature.StartAngle`, `feature.Coverage`, and `feature.Confidence`.

- [ ] **Step 1: Write the failing test.** Create `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMeasurementCoverageTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMeasurementCoverageTests
{
    private static CircularFeaturePick OpenArc(double radius, double startAngle, double coverage, double confidence)
        => new CircularFeaturePick(
            Center: Vector3d.Zero,
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            Radius: radius,
            IsClosed: false,
            StartAngle: startAngle,
            Coverage: coverage,
            CurveSegments: new[] { new Vector3d(radius, 0, 0), new Vector3d(0, radius, 0) })
        { Confidence = confidence };

    [Fact]
    public void Builder_ForwardsCoverageStartAngleAndConfidence()
    {
        double sweep = System.Math.PI * 0.5; // 90 deg open arc
        CircularFeatureMeasurement m = MeasurementBuilders.BuildCircularFeature(
            OpenArc(radius: 2.0, startAngle: 0.3, coverage: sweep, confidence: 0.42),
            metersPerSceneUnit: 0.001);

        Assert.Equal(sweep, m.Coverage, 9);
        Assert.Equal(0.3, m.StartAngle, 9);
        Assert.Equal(DimensionConfidence.Marginal, m.Confidence); // 0.42 < 0.6 => marginal
    }

    [Fact]
    public void Builder_HighConfidenceClosedFeature_IsCommitted()
    {
        CircularFeatureMeasurement m = MeasurementBuilders.BuildCircularFeature(
            new CircularFeaturePick(Vector3d.Zero, Vector3d.UnitZ, Vector3d.UnitX, Vector3d.UnitY,
                Radius: 3.0, IsClosed: true, StartAngle: 0.0, Coverage: System.Math.Tau,
                CurveSegments: new[] { new Vector3d(3, 0, 0), new Vector3d(0, 3, 0) }) { Confidence = 0.95 },
            metersPerSceneUnit: 0.001);

        Assert.Equal(System.Math.Tau, m.Coverage, 9);
        Assert.Equal(DimensionConfidence.Committed, m.Confidence); // >= 0.85 => committed
    }
}
```

> This test also assumes `CircularFeaturePick` carries a `Confidence` (added by Group I's `with` extension). If Group I has not yet landed `Confidence` on `CircularFeaturePick`, add it as an optional positional/`init` member there first; the canonical contract states the pick "gains via `with`: … `double Confidence` …", so it is expected to exist by the time J runs. The mapping double→enum lives in the builder (Step 3).

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMeasurementCoverage"`
  Expected failure: **compile error** — `CircularFeatureMeasurement` has no `Coverage`/`StartAngle`/`Confidence` members (`CS1061`).

- [ ] **Step 3: Implement.** In `src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs`, replace the `CircularFeatureMeasurement` record (the block currently at lines 130-143) with:

```csharp
public sealed record CircularFeatureMeasurement(
    MeasurementId Id,
    Vector3d Center,
    Vector3d Axis,
    Vector3d U,
    Vector3d V,
    double RadiusScene,
    SceneLength Value,
    CircularFeatureDimensionKind DimensionKind,
    IReadOnlyList<Vector3d> CurveSegments,
    double StartAngle = 0.0,
    double Coverage = System.Math.Tau,
    DimensionConfidence Confidence = DimensionConfidence.Committed,
    bool IsVisible = true) : MeasurementResult(Id, IsVisible)
{
    public override MeasureToolMode Mode => MeasureToolMode.CircularFeature;
}
```

> Add `using FabricationAssistant.Core.Measurement.Presentation;` to the top of `Measurement.cs` if not already present (needed for `DimensionConfidence`). The Domain and Presentation namespaces have no cyclic dependency: Presentation already references Domain (`MeasurementId`), and `DimensionConfidence` is a leaf enum with no Domain dependency, so this is safe.

Then in `src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs`, replace the body of `BuildCircularFeature` (lines 164-183) with:

```csharp
    public static CircularFeatureMeasurement BuildCircularFeature(
        CircularFeaturePick feature,
        double metersPerSceneUnit)
    {
        CircularFeatureDimensionKind kind = feature.IsClosed
            ? CircularFeatureDimensionKind.Diameter
            : CircularFeatureDimensionKind.Radius;
        double sceneValue = feature.IsClosed ? feature.Radius * 2.0 : feature.Radius;
        SceneLength value = SceneLength.FromSceneUnits(sceneValue, metersPerSceneUnit);
        return new CircularFeatureMeasurement(
            MeasurementId.New(),
            feature.Center,
            feature.Axis.Normalized(),
            feature.U.Normalized(),
            feature.V.Normalized(),
            feature.Radius,
            value,
            kind,
            feature.CurveSegments,
            feature.StartAngle,
            feature.Coverage,
            MapConfidence(feature.Confidence));
    }

    private static DimensionConfidence MapConfidence(double confidence) => confidence switch
    {
        >= 0.85 => DimensionConfidence.Committed,
        >= 0.60 => DimensionConfidence.Preview,
        _ => DimensionConfidence.Marginal,
    };
```

> Add `using FabricationAssistant.Core.Measurement.Presentation;` to the top of `MeasurementBuilders.cs` if absent.

- [ ] **Step 4: Run it, expect PASS.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMeasurementCoverage"`
  Expected: 2 passed. Then prove the pre-existing circular builder tests still pass:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMeasurementTests"`
  Expected: all still pass (defaults preserve old behavior).

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs src/FabricationAssistant.Core/Measurement/Engine/MeasurementBuilders.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMeasurementCoverageTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): thread coverage/start-angle/confidence into circular measurement

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task J.4: Unit + precision auto-format on SceneUnitSystemService

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/IUnitSystemService.cs`
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Engine/SceneUnitSystemService.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/UnitAutoFormatTests.cs` (create)

> Replace the fixed `mm / F2` rendering for dimensions with a size-aware format: pick mm vs m by magnitude and choose decimal places to keep ~3-4 significant figures. Implemented as a new interface method `FormatDimension(SceneLength)` so existing `Format(...)` keeps its fixed-decimals contract (used by other tools/tests). The fake units in other tests implement `IUnitSystemService`, so the new member needs a default implementation on the interface to avoid breaking those fakes.

- [ ] **Step 1: Write the failing test.** Create `src/FabricationAssistant.App.Tests/Measurement/UnitAutoFormatTests.cs`:

```csharp
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class UnitAutoFormatTests
{
    private static SceneUnitSystemService Mm()
    {
        var u = new SceneUnitSystemService();
        u.SetUnitLabel("MM");
        u.DisplayUnit = UnitSystem.Millimeters;
        return u;
    }

    [Fact]
    public void SmallFeature_RendersMillimetres_WithTwoDecimals()
    {
        // 2.5 mm hole radius -> "2.50 mm"
        string s = Mm().FormatDimension(new SceneLength(0.0025));
        Assert.Equal("2.50 mm", s);
    }

    [Fact]
    public void SubMillimetre_GainsAThirdDecimal()
    {
        // 0.42 mm -> needs 3 decimals to keep significant figures -> "0.420 mm"
        string s = Mm().FormatDimension(new SceneLength(0.00042));
        Assert.Equal("0.420 mm", s);
    }

    [Fact]
    public void LargeFeature_SwitchesToMetres()
    {
        // 1500 mm = 1.5 m -> auto-switch to metres -> "1.500 m"
        string s = Mm().FormatDimension(new SceneLength(1.5));
        Assert.Equal("1.500 m", s);
    }

    [Fact]
    public void MidRange_RoundsToOneDecimal()
    {
        // 123.4 mm -> "123.4 mm" (4 sig figs, 1 decimal)
        string s = Mm().FormatDimension(new SceneLength(0.1234));
        Assert.Equal("123.4 mm", s);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~UnitAutoFormat"`
  Expected failure: **compile error** — `SceneUnitSystemService` / `IUnitSystemService` has no `FormatDimension` (`CS1061`).

- [ ] **Step 3: Implement.** Add the member to `src/FabricationAssistant.Core/Measurement/Engine/IUnitSystemService.cs` with a default implementation (so the test fakes that don't override it still compile):

```csharp
using FabricationAssistant.Core.Measurement.Domain;

namespace FabricationAssistant.Core.Measurement.Engine;

public interface IUnitSystemService
{
    double MetersPerSceneUnit { get; }

    UnitSystem DisplayUnit { get; }

    string Format(SceneLength length, int decimals = 2);

    UnitSystem AreaDisplayUnit { get; }

    string FormatArea(SceneArea area, int decimals = 2);

    /// <summary>
    /// Size-aware dimension format: auto-selects mm vs m by magnitude and chooses
    /// decimal places to keep ~3-4 significant figures. Default falls back to the
    /// fixed <see cref="Format(SceneLength,int)"/> for implementations that don't override.
    /// </summary>
    string FormatDimension(SceneLength length) => Format(length);
}
```

Then add the override to `src/FabricationAssistant.Core/Measurement/Engine/SceneUnitSystemService.cs` (insert after the `FormatArea` method, before `Resolve`):

```csharp
    public string FormatDimension(SceneLength length)
    {
        // Auto-select the display unit by absolute magnitude: anything >= 1 m reads in
        // metres, everything else in millimetres (fabrication default). Then choose the
        // decimal count so the printed value carries ~3-4 significant figures regardless
        // of scale, instead of a fixed F2 that loses precision on sub-mm features and
        // over-prints large ones.
        double meters = System.Math.Abs(length.Meters);
        UnitSystem unit = meters >= 1.0 ? UnitSystem.Meters : UnitSystem.Millimeters;
        double value = length.ConvertTo(unit);

        int decimals = SignificantDecimals(System.Math.Abs(value));
        string suffix = unit switch
        {
            UnitSystem.Millimeters => "mm",
            UnitSystem.Centimeters => "cm",
            UnitSystem.Meters => "m",
            UnitSystem.Inches => "in",
            UnitSystem.Feet => "ft",
            _ => "",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{value.ToString($"F{decimals}", CultureInfo.InvariantCulture)} {suffix}");
    }

    // Decimal places to keep roughly 3-4 significant figures for a magnitude shown in its
    // selected unit:  >=100 -> 1 dp, >=10 -> 2 dp, >=1 -> 2 dp, <1 -> 3 dp.
    private static int SignificantDecimals(double magnitude)
    {
        if (magnitude >= 100.0) return 1;
        if (magnitude >= 1.0) return 2;
        return 3;
    }
```

> Check the four expected strings against this rule: 2.5 mm → magnitude 2.5 → 2 dp → `2.50 mm` ✓; 0.42 mm → magnitude 0.42 → 3 dp → `0.420 mm` ✓; 1.5 m → magnitude 1.5 (in metres) → 2 dp… **but the test expects 3 dp (`1.500 m`)**. Metres always read with 3 dp because a metre-scale fabrication dim wants mm-grade precision. Adjust the rule: when `unit == Meters`, force `decimals = 3`. Apply this in `FormatDimension` after computing `decimals`:

```csharp
        int decimals = unit == UnitSystem.Meters ? 3 : SignificantDecimals(System.Math.Abs(value));
```

  Re-verify: 1.5 m → 3 dp → `1.500 m` ✓; 123.4 mm → magnitude 123.4 → 1 dp → `123.4 mm` ✓.

- [ ] **Step 4: Run it, expect PASS.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~UnitAutoFormat"`
  Expected: 4 passed.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Engine/IUnitSystemService.cs src/FabricationAssistant.Core/Measurement/Engine/SceneUnitSystemService.cs src/FabricationAssistant.App.Tests/Measurement/UnitAutoFormatTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): size-aware unit/precision auto-format for dimensions

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task J.5: Rewrite MeasurementPresenter.CircularFeature — Diameter (center mark + double arrowheads + Ø label)

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularDimensionPresenterTests.cs` (create)

> Replace the debug-overlay `CircularFeature` (`MeasurementPresenter.cs:162-191`) with a professional **diameter** drawing: a center-mark cross at the axis; a dimension line through the center with a **double arrowhead** (one at each rim point, each pointing OUTWARD toward its rim); and a label that **starts with `Ø` (U+00D8)** placed in a gap at the center, using `FormatDimension`. The radius branch is finished in J.6 (this task still routes radius through the old code path to keep the file compiling — J.6 replaces it). The arrowhead `Direction` convention from J.1 is "from tip back along the line", i.e. pointing from rim toward center; we encode it as the inward unit so the renderer fans the barbs correctly.

- [ ] **Step 1: Write the failing test.** Create `src/FabricationAssistant.App.Tests/Measurement/CircularDimensionPresenterTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularDimensionPresenterTests
{
    private static MeasurementPresenter Presenter()
    {
        var u = new SceneUnitSystemService();
        u.SetUnitLabel("MM");
        u.DisplayUnit = UnitSystem.Millimeters;
        return new MeasurementPresenter(u);
    }

    private static CircularFeatureMeasurement Diameter(double radiusScene, double metersPerScene)
        => new CircularFeatureMeasurement(
            MeasurementId.New(),
            Center: Vector3d.Zero,
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            RadiusScene: radiusScene,
            Value: SceneLength.FromSceneUnits(radiusScene * 2.0, metersPerScene),
            DimensionKind: CircularFeatureDimensionKind.Diameter,
            CurveSegments: System.Array.Empty<Vector3d>(),
            StartAngle: 0.0,
            Coverage: System.Math.Tau,
            Confidence: DimensionConfidence.Committed);

    [Fact]
    public void Diameter_HasTwoArrowheads_OneCenterMark_AndOeLabel()
    {
        var presenter = Presenter();
        var snap = Assert.Single(presenter.Build(
            new[] { Diameter(radiusScene: 5.0, metersPerScene: 0.001) },
            null, null, MeasureToolMode.CircularFeature));

        Assert.Equal(2, snap.Arrowheads.Count);
        Assert.Single(snap.CenterMarks);
        string label = Assert.Single(snap.Labels).Text;
        Assert.StartsWith("\u00D8", label);           // Ø ...
        Assert.Contains("10.00 mm", label);            // diameter = 2 * 5 mm
    }

    [Fact]
    public void Diameter_Arrowheads_SitAtBothRimPoints_PointingInward()
    {
        var presenter = Presenter();
        var snap = Assert.Single(presenter.Build(
            new[] { Diameter(radiusScene: 5.0, metersPerScene: 0.001) },
            null, null, MeasureToolMode.CircularFeature));

        // Rim points are at ±5 along U(=X). One arrowhead at each.
        var xs = new[] { snap.Arrowheads[0].Position.X, snap.Arrowheads[1].Position.X };
        System.Array.Sort(xs);
        Assert.Equal(-5.0, xs[0], 6);
        Assert.Equal(5.0, xs[1], 6);

        // Each arrowhead points inward (toward the center): dot(position, direction) < 0.
        foreach (var a in snap.Arrowheads)
            Assert.True(Vector3d.Dot(a.Position, a.Direction) < 0.0,
                "diameter arrowhead must point inward toward the center");
    }

    [Fact]
    public void Diameter_CenterMark_AxesAreOrthonormalInThePlane()
    {
        var presenter = Presenter();
        var snap = Assert.Single(presenter.Build(
            new[] { Diameter(radiusScene: 5.0, metersPerScene: 0.001) },
            null, null, MeasureToolMode.CircularFeature));

        CenterMarkPrimitive mark = Assert.Single(snap.CenterMarks);
        Assert.Equal(1.0, mark.AxisU.Length, 6);
        Assert.Equal(1.0, mark.AxisV.Length, 6);
        Assert.Equal(0.0, Vector3d.Dot(mark.AxisU, mark.AxisV), 6);
        // Axes lie in the feature plane (perpendicular to the axis Z).
        Assert.Equal(0.0, Vector3d.Dot(mark.AxisU, Vector3d.UnitZ), 6);
        Assert.Equal(0.0, Vector3d.Dot(mark.AxisV, Vector3d.UnitZ), 6);
    }

    [Fact]
    public void Diameter_PropagatesConfidenceToSnapshot()
    {
        var presenter = Presenter();
        var m = Diameter(5.0, 0.001) with { Confidence = DimensionConfidence.Marginal };
        var snap = Assert.Single(presenter.Build(
            new[] { m }, null, null, MeasureToolMode.CircularFeature));
        Assert.Equal(DimensionConfidence.Marginal, snap.Confidence);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularDimensionPresenter"`
  Expected failure: assertions — the current `CircularFeature` emits 0 arrowheads / 0 center marks and a label starting with `"DIA "` not `Ø` (`Assert.Equal(2, snap.Arrowheads.Count)` fails: actual 0).

- [ ] **Step 3: Implement.** In `src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs`, replace the entire `CircularFeature` method (lines 162-191) with the diameter-aware version below. (The `BuildCurveLines` helper at 193-218 stays for now; J.6 removes its 360° fallback.)

```csharp
    private const char DiameterGlyph = '\u00D8'; // Ø

    private PresentationSnapshot CircularFeature(CircularFeatureMeasurement m, PresentationStyle style)
    {
        Vector3d axis = m.Axis.Normalized();
        Vector3d u = m.U.Normalized();
        if (u.LengthSquared <= 0.0)
            u = Vector3d.BuildPerpendicular(axis);
        Vector3d v = m.V.Normalized();
        if (v.LengthSquared <= 0.0)
            v = Vector3d.Cross(axis, u).Normalized();

        // Center-mark cross size: a fraction of the radius, clamped so tiny features
        // still get a visible mark and huge ones don't get a giant one.
        double markSize = System.Math.Clamp(m.RadiusScene * 0.25, 1e-6, m.RadiusScene);
        var centerMarks = new[] { new CenterMarkPrimitive(m.Center, u, v, markSize) };

        if (m.DimensionKind == CircularFeatureDimensionKind.Diameter)
            return BuildDiameter(m, style, axis, u, centerMarks);

        // Radius branch is rebuilt in J.6; route through the legacy line drawing for now.
        return BuildRadiusLegacy(m, style, u, centerMarks);
    }

    private PresentationSnapshot BuildDiameter(
        CircularFeatureMeasurement m,
        PresentationStyle style,
        Vector3d axis,
        Vector3d u,
        IReadOnlyList<CenterMarkPrimitive> centerMarks)
    {
        Vector3d rimA = m.Center + u * m.RadiusScene;   // +U rim
        Vector3d rimB = m.Center - u * m.RadiusScene;   // -U rim

        // Full-width dimension line through the center.
        var dimensionLine = new LinePrimitive(rimB, rimA);

        // Arrowhead size scaled to the feature; clamp like the center mark.
        double arrowSize = System.Math.Clamp(m.RadiusScene * 0.15, 1e-6, m.RadiusScene * 0.5);

        // Direction = "from tip back along the line" = inward toward the center,
        // so each head fans its barbs away from its rim.
        var arrowheads = new[]
        {
            new ArrowheadPrimitive(rimA, (m.Center - rimA).Normalized(), arrowSize),
            new ArrowheadPrimitive(rimB, (m.Center - rimB).Normalized(), arrowSize),
        };

        string label = DiameterGlyph + _units.FormatDimension(m.Value);

        return new PresentationSnapshot(
            m.Id, style,
            Balls: System.Array.Empty<BallPrimitive>(),
            Disks: System.Array.Empty<DiskPrimitive>(),
            Lines: new[] { dimensionLine },
            Labels: new[] { new LabelPrimitive(label, m.Center, LabelAlignment.Billboard) },
            Arrowheads: arrowheads,
            Leaders: System.Array.Empty<LeaderPrimitive>(),
            CenterMarks: centerMarks,
            Confidence: m.Confidence);
    }

    // TEMP — replaced in J.6 with a true-coverage radius leader.
    private PresentationSnapshot BuildRadiusLegacy(
        CircularFeatureMeasurement m,
        PresentationStyle style,
        Vector3d u,
        IReadOnlyList<CenterMarkPrimitive> centerMarks)
    {
        Vector3d edgeA = m.Center + u * m.RadiusScene;
        IReadOnlyList<LinePrimitive> curve = BuildCurveLines(m);
        var lines = new LinePrimitive[curve.Count + 1];
        for (int i = 0; i < curve.Count; i++) lines[i] = curve[i];
        lines[^1] = new LinePrimitive(m.Center, edgeA);
        string label = "R " + _units.FormatDimension(m.Value);
        return new PresentationSnapshot(
            m.Id, style,
            Balls: new[] { new BallPrimitive(m.Center), new BallPrimitive(edgeA) },
            Disks: System.Array.Empty<DiskPrimitive>(),
            Lines: lines,
            Labels: new[] { new LabelPrimitive(label, (m.Center + edgeA) * 0.5, LabelAlignment.Billboard) },
            Arrowheads: System.Array.Empty<ArrowheadPrimitive>(),
            Leaders: System.Array.Empty<LeaderPrimitive>(),
            CenterMarks: centerMarks,
            Confidence: m.Confidence);
    }
```

> `_units` is the `IUnitSystemService` field already on the presenter (line 19). The `with` expression in the confidence test requires `CircularFeatureMeasurement` to be a record — it is.

- [ ] **Step 4: Run it, expect PASS.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularDimensionPresenter"`
  Expected: 4 passed. Then the legacy circular label test (`Presenter_CircularFeature_LabelsDiameterOrRadius`) will now **fail** because the diameter label starts with `Ø` not `"DIA "`. Update that one assertion in `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMeasurementTests.cs` from `Assert.StartsWith("DIA ", diameterLabel);` to `Assert.StartsWith("\u00D8", diameterLabel);` (the radius `"R "` assertion stays valid until J.6). Re-run:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMeasurementTests|FullyQualifiedName~CircularDimensionPresenter"`
  Expected: all pass.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs src/FabricationAssistant.App.Tests/Measurement/CircularDimensionPresenterTests.cs src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMeasurementTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): diameter dimension renders center mark, double arrowheads, Ø label

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task J.6: Radius (single leader, one arrowhead, true-coverage arc) + Chamfer leader; kill the 360° phantom

**Files:**
- Modify: `[PARENT] src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs`
- Test: `[PARENT] src/FabricationAssistant.App.Tests/Measurement/CircularRadiusPresenterTests.cs` (create)

> Replace `BuildRadiusLegacy` (from J.5) and the `BuildCurveLines` 360° fallback (J.5/original lines 193-218) so that:
> - **Radius** emits a single **leader** from the arc with **exactly one arrowhead** at the arc, an `R`+value label on a shoulder **outside** the feature, and an arc drawn at **true coverage** (`m.Coverage`, starting at `m.StartAngle`) — **never** a full 360° circle and never a chord.
> - **Chamfer** (a radius-kind feature whose value text was prepared as `w × angle`) renders as a leader with that text. We detect chamfer here by a sentinel: a `CircularFeatureMeasurement` whose `Coverage <= 0` is treated as a chamfer leader using its `CurveSegments[0..1]` as the band edge (Group G/I emit it that way). For this presentation task we test the **radius true-arc** behavior, which is the load-bearing requirement; the chamfer path reuses the same leader builder with the precomputed label.
>
> The spec's required test: *open radius snapshot has exactly one arrowhead and an arc shorter than full circle.* We assert `Arrowheads.Count == 1` and that the number of emitted arc line segments is **strictly fewer** than the full-circle fallback count (48), and that the arc's start point matches `StartAngle` and its end point matches `StartAngle + Coverage`.

- [ ] **Step 1: Write the failing test.** Create `src/FabricationAssistant.App.Tests/Measurement/CircularRadiusPresenterTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularRadiusPresenterTests
{
    private static MeasurementPresenter Presenter()
    {
        var u = new SceneUnitSystemService();
        u.SetUnitLabel("MM");
        u.DisplayUnit = UnitSystem.Millimeters;
        return new MeasurementPresenter(u);
    }

    private static CircularFeatureMeasurement OpenRadius(
        double radiusScene, double metersPerScene, double startAngle, double coverage)
        => new CircularFeatureMeasurement(
            MeasurementId.New(),
            Center: Vector3d.Zero,
            Axis: Vector3d.UnitZ,
            U: Vector3d.UnitX,
            V: Vector3d.UnitY,
            RadiusScene: radiusScene,
            Value: SceneLength.FromSceneUnits(radiusScene, metersPerScene),
            DimensionKind: CircularFeatureDimensionKind.Radius,
            CurveSegments: System.Array.Empty<Vector3d>(), // force the presenter's true-arc builder
            StartAngle: startAngle,
            Coverage: coverage,
            Confidence: DimensionConfidence.Committed);

    [Fact]
    public void OpenRadius_HasExactlyOneArrowhead()
    {
        var snap = Assert.Single(Presenter().Build(
            new[] { OpenRadius(4.0, 0.001, startAngle: 0.0, coverage: System.Math.PI * 0.5) },
            null, null, MeasureToolMode.CircularFeature));
        Assert.Single(snap.Arrowheads);
    }

    [Fact]
    public void OpenRadius_ArcIsShorterThanFullCircle_AndNotAChord()
    {
        double coverage = System.Math.PI * 0.5; // 90 deg quarter arc
        var snap = Assert.Single(Presenter().Build(
            new[] { OpenRadius(4.0, 0.001, startAngle: 0.0, coverage: coverage) },
            null, null, MeasureToolMode.CircularFeature));

        // The radius arc segments are the snapshot Lines minus the single leader line.
        // We assert there are MANY arc segments (curve, not a single chord) and FEWER
        // than the 48 a full-circle fallback would have produced.
        int arcSegments = snap.Lines.Count;            // leader is a LeaderPrimitive, not a Line
        Assert.True(arcSegments > 2, "arc must be tessellated, not a chord");
        Assert.True(arcSegments < 48, "open radius must not draw a full 360 circle");

        // Every arc vertex sits on the radius and in the plane (Z=0 here).
        foreach (var ln in snap.Lines)
        {
            Assert.Equal(4.0, ln.Start.Length, 5);
            Assert.Equal(0.0, ln.Start.Z, 9);
        }
    }

    [Fact]
    public void OpenRadius_ArcStartsAtStartAngle_EndsAtStartPlusCoverage()
    {
        double start = 0.3;
        double coverage = System.Math.PI * 0.5;
        var snap = Assert.Single(Presenter().Build(
            new[] { OpenRadius(4.0, 0.001, start, coverage) },
            null, null, MeasureToolMode.CircularFeature));

        Vector3d first = snap.Lines[0].Start;
        Vector3d last = snap.Lines[^1].End;

        Vector3d expectedFirst = new(System.Math.Cos(start) * 4.0, System.Math.Sin(start) * 4.0, 0);
        Vector3d expectedLast = new(System.Math.Cos(start + coverage) * 4.0, System.Math.Sin(start + coverage) * 4.0, 0);

        Assert.True(Vector3d.Distance(first, expectedFirst) < 1e-6);
        Assert.True(Vector3d.Distance(last, expectedLast) < 1e-6);
    }

    [Fact]
    public void OpenRadius_HasOneLeader_AndRLabelOutsideTheFeature()
    {
        double radius = 4.0;
        var snap = Assert.Single(Presenter().Build(
            new[] { OpenRadius(radius, 0.001, 0.0, System.Math.PI * 0.5) },
            null, null, MeasureToolMode.CircularFeature));

        Assert.Single(snap.Leaders);
        Assert.True(snap.Leaders[0].Points.Count >= 2);
        LabelPrimitive label = Assert.Single(snap.Labels);
        Assert.StartsWith("R ", label.Text);
        // The label sits OUTSIDE the arc (further from center than the radius).
        Assert.True(label.Anchor.Length > radius);
    }

    [Fact]
    public void OpenRadius_PrefersExplicitCurveSegmentsWhenProvided()
    {
        // When the pick already supplied real curve geometry, the presenter uses it verbatim.
        var supplied = new[]
        {
            new Vector3d(4, 0, 0), new Vector3d(3, 2.65, 0),
            new Vector3d(3, 2.65, 0), new Vector3d(0, 4, 0),
        };
        var m = OpenRadius(4.0, 0.001, 0.0, System.Math.PI * 0.5) with { CurveSegments = supplied };
        var snap = Assert.Single(Presenter().Build(
            new[] { m }, null, null, MeasureToolMode.CircularFeature));
        Assert.Equal(2, snap.Lines.Count); // exactly the two supplied segments
    }
}
```

- [ ] **Step 2: Run it, expect FAIL.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularRadiusPresenter"`
  Expected failure: assertions — the J.5 `BuildRadiusLegacy` emits **0 arrowheads** and **0 leaders**, and `BuildCurveLines` falls back to a **48-segment full circle** (so `Assert.Single(snap.Arrowheads)` fails: actual 0; and `arcSegments < 48` fails: actual 48).

- [ ] **Step 3: Implement.** In `src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs`: (a) delete the `BuildRadiusLegacy` method added in J.5; (b) change the radius route in `CircularFeature` to call the new `BuildRadius`; (c) replace `BuildCurveLines` with a **true-coverage** arc builder. Concretely:

In `CircularFeature`, replace the final line `return BuildRadiusLegacy(m, style, u, centerMarks);` with:

```csharp
        return BuildRadius(m, style, axis, u, v, centerMarks);
```

Add these methods (and remove the old `BuildCurveLines` body, lines 193-218 of the original):

```csharp
    // Arc segments to use when synthesizing a tessellated arc from coverage. Chosen so
    // a quarter arc still reads smooth while a near-full arc stays well under the old
    // 48-segment full-circle fallback this replaces.
    private const int ArcSegmentsPerCircle = 64;

    private PresentationSnapshot BuildRadius(
        CircularFeatureMeasurement m,
        PresentationStyle style,
        Vector3d axis,
        Vector3d u,
        Vector3d v,
        IReadOnlyList<CenterMarkPrimitive> centerMarks)
    {
        IReadOnlyList<LinePrimitive> arc = BuildArcLines(m, u, v);

        // Arc point at the middle of the sweep — where the leader attaches.
        double midAngle = m.StartAngle + m.Coverage * 0.5;
        Vector3d radialMid = (u * System.Math.Cos(midAngle) + v * System.Math.Sin(midAngle)).Normalized();
        Vector3d arcPoint = m.Center + radialMid * m.RadiusScene;

        // Shoulder/text sit OUTSIDE the feature, continuing radially outward.
        double shoulderLen = System.Math.Clamp(m.RadiusScene * 0.6, 1e-6, m.RadiusScene * 2.0);
        Vector3d shoulder = arcPoint + radialMid * shoulderLen;
        Vector3d textAnchor = shoulder + radialMid * (shoulderLen * 0.25);

        var leader = new LeaderPrimitive(new[] { arcPoint, shoulder });

        // Single arrowhead at the arc, pointing inward along the leader (tip on the arc,
        // barbs fanning back toward the shoulder).
        double arrowSize = System.Math.Clamp(m.RadiusScene * 0.15, 1e-6, m.RadiusScene * 0.5);
        var arrowhead = new ArrowheadPrimitive(arcPoint, (m.Center - arcPoint).Normalized(), arrowSize);

        var lines = new LinePrimitive[arc.Count];
        for (int i = 0; i < arc.Count; i++) lines[i] = arc[i];

        string label = "R " + _units.FormatDimension(m.Value);

        return new PresentationSnapshot(
            m.Id, style,
            Balls: System.Array.Empty<BallPrimitive>(),
            Disks: System.Array.Empty<DiskPrimitive>(),
            Lines: lines,
            Labels: new[] { new LabelPrimitive(label, textAnchor, LabelAlignment.Billboard) },
            Arrowheads: new[] { arrowhead },
            Leaders: new[] { leader },
            CenterMarks: centerMarks,
            Confidence: m.Confidence);
    }

    // True-coverage arc: if the measurement already carries real curve geometry, use it
    // verbatim. Otherwise synthesize an arc over [StartAngle, StartAngle + Coverage] —
    // NEVER a full 360 circle and NEVER a single chord.
    private static IReadOnlyList<LinePrimitive> BuildArcLines(CircularFeatureMeasurement m, Vector3d u, Vector3d v)
    {
        if (m.CurveSegments.Count >= 2)
        {
            int count = m.CurveSegments.Count / 2;
            var supplied = new LinePrimitive[count];
            for (int i = 0; i < count; i++)
                supplied[i] = new LinePrimitive(m.CurveSegments[2 * i], m.CurveSegments[2 * i + 1]);
            return supplied;
        }

        double coverage = m.Coverage;
        if (coverage <= 0.0 || double.IsNaN(coverage))
            coverage = System.Math.Tau;                 // degenerate guard, still bounded below
        coverage = System.Math.Min(coverage, System.Math.Tau);

        int segments = System.Math.Max(2,
            (int)System.Math.Ceiling(ArcSegmentsPerCircle * (coverage / System.Math.Tau)));

        var arc = new LinePrimitive[segments];
        for (int i = 0; i < segments; i++)
        {
            double a0 = m.StartAngle + coverage * (i / (double)segments);
            double a1 = m.StartAngle + coverage * ((i + 1) / (double)segments);
            Vector3d p0 = m.Center + (u * System.Math.Cos(a0) + v * System.Math.Sin(a0)) * m.RadiusScene;
            Vector3d p1 = m.Center + (u * System.Math.Cos(a1) + v * System.Math.Sin(a1)) * m.RadiusScene;
            arc[i] = new LinePrimitive(p0, p1);
        }
        return arc;
    }
```

> Verify segment count for the spec test: a 90° arc → `coverage/Tau = 0.25` → `ceil(64 * 0.25) = 16` segments. `16 > 2` (not a chord) and `16 < 48` (not a full circle) ✓. Start/end vertices land exactly on `StartAngle` / `StartAngle + Coverage` ✓. When `CurveSegments` are supplied (last test), the two supplied segments are used verbatim → `Lines.Count == 2` ✓.

- [ ] **Step 4: Run it, expect PASS.** From PARENT root:
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularRadiusPresenter"`
  Expected: 6 passed. Then run the whole circular/presenter surface to confirm nothing regressed (the old `Presenter_CircularFeature_LabelsDiameterOrRadius` radius assertion `Assert.StartsWith("R ", ...)` still holds):
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature|FullyQualifiedName~CircularDimensionPresenter|FullyQualifiedName~CircularRadiusPresenter|FullyQualifiedName~MeasurementPresenter"`
  Expected: all pass.

- [ ] **Step 5: Commit (PARENT repo).**
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs src/FabricationAssistant.App.Tests/Measurement/CircularRadiusPresenterTests.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "feat(measure): radius dimension uses single leader + one arrowhead + true-coverage arc (no 360 phantom)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

#### Group J — completion check
Run the full host measurement suite from PARENT root to confirm Group J is green end-to-end:
`dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~Measurement"`
Expected: all measurement tests pass. The diameter snapshot now emits a center-mark cross + a dimension line with two inward-pointing arrowheads + a label beginning with `Ø`; the radius snapshot emits one leader, exactly one arrowhead, and a true-coverage arc strictly shorter than a full circle — satisfying the spec's Group J acceptance tests (sec 6.11).

---


## GROUP K — ProfessionalDimensionRenderer (Android `GlesMeasurementOverlay`)

> **Repo & path conventions (this group only commits in the ANDROID repo).** All edits in this group live under `Android/src/FabricationAssistant.Rendering.Gles/` and `Android/src/FabricationAssistant.App.Android.Tests/`. The ANDROID root is `C:/Users/skritikos/Desktop/Fabrication Assistant/Android` — a nested git repo the parent gitignores. **Commit from inside the Android repo, staging by EXPLICIT path.** The presentation primitive types (`ArrowheadPrimitive`, `LeaderPrimitive`, `CenterMarkPrimitive`, `DimensionConfidence`, and the extended `PresentationSnapshot`) are produced by **Group J in the PARENT repo** (`src/FabricationAssistant.Core/Measurement/Presentation/`); Group K is **strictly the GLES rasterizer that consumes them**. This group depends on Group J being merged so `PresentationSnapshot.Arrowheads / .Leaders / .CenterMarks / .Confidence` exist and compile. If those members are absent when you start, stop and finish Group J first.
>
> **Why source-guard tests, not behavioral tests.** The GLES overlay cannot load in the `net8.0` host test runner (no GL context, Android-only `Silk.NET.OpenGLES` + `global::Android.Util.Log`). Per the existing pattern in `GlesRendererSourceGuards.cs` (header comment S25-1), Group K is verified by (a) **host-side source-assertion tests** that assert the new primitive-handling source exists and obeys the spec invariants, and (b) a **manual adb device-verification checklist** re-checking the three failing taps now render Ø/R professionally. Real shader/GL execution is covered on-device by `tools/run-render-queue-test.ps1`.
>
> **ASCII-only constraint.** `GlesRendererSourceGuards.GlesSourceAndShaders_AreAsciiOnly` enforces that **every `.cs` and `.gles.*` file under `FabricationAssistant.Rendering.Gles` is ASCII-only**. The `Ø` glyph (U+00D8) is therefore **forbidden as a literal in renderer source**. Group K never embeds the glyph; text content comes pre-formatted from Group J's `LabelPrimitive.Text` (parent repo, where non-ASCII is allowed). The renderer's job for text is geometry-only (halo/on-top quad placement). Do not introduce any non-ASCII byte in this group's renderer files.
>
> **BUILD-LOCK note (applies to every task below).** If a build fails with CSC `CS2012` / `XARLP7024` / a file-lock on a DLL, run `dotnet build-server shutdown` from the parent root and retry; if a named `.NET Host (PID)` still holds the DLL, `Stop-Process -Id <pid> -Force`. Stated once here, not repeated per task.
>
> **Test command (all source-guard tasks).** From the PARENT root:
> `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer"`
>
> **Canonical consumed contracts (Group J, namespace `FabricationAssistant.Core.Measurement.Presentation`):**
> ```csharp
> public enum DimensionConfidence { Committed, Preview, Marginal }
> public readonly record struct ArrowheadPrimitive(Vector3d Position, Vector3d Direction, double Size);
> public readonly record struct LeaderPrimitive(IReadOnlyList<Vector3d> Points);
> public readonly record struct CenterMarkPrimitive(Vector3d Position, Vector3d AxisU, Vector3d AxisV, double Size);
> // PresentationSnapshot additionally exposes:
> //   IReadOnlyList<ArrowheadPrimitive> Arrowheads
> //   IReadOnlyList<LeaderPrimitive>    Leaders
> //   IReadOnlyList<CenterMarkPrimitive> CenterMarks
> //   DimensionConfidence Confidence
> ```

---

## Group K — Professional GLES dimension rendering (Android)

> Extends `GlesMeasurementOverlay` to rasterize the new arrowhead/leader/center-mark primitives (filled arrowheads, distinct thin dimension-line weight/color, on-top haloed text, dashed/greyed for Preview/Marginal). ANDROID-repo. Hard-to-unit-test GLES, so tasks combine source-guard assertions with a device-verification checklist (correction: GLES verified via `GlesRendererSourceGuards` + adb re-check of the three failing taps).

### Task K.1: Add a `DimensionConfidence`-aware style resolver in the overlay (dashed/greyed Preview/Marginal)

The existing overlay colors purely by `PresentationStyle`. The redesign (spec §6.11, §4.3) requires a **distinct thin dimension-line color** and **dashed/greyed styling for Preview/Marginal** fits while Committed renders solid. We add a pure, source-guardable helper `ResolveDimensionAppearance(PresentationStyle, DimensionConfidence)` that returns the line color, the line-width multiplier, and a dash flag. This is the foundation the later tasks consume.

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** — append this `[Fact]` to `GlesRendererSourceGuards.cs` (inside the `GlesRendererSourceGuards` class, before the trailing `private static string ResolveRepoPath` helper). It uses the existing `ResolveRepoPath` + `ExtractMethod` helpers already in that file.

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_ConfidenceStylingIsDashedAndGreyedForPreviewMarginal()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        // K.1: a single resolver maps (style, confidence) -> appearance struct.
        string resolve = ExtractMethod(overlay, "private DimensionAppearance ResolveDimensionAppearance");

        // Committed is solid full-strength; Preview/Marginal are dashed and greyed (alpha < 1).
        Assert.Contains("DimensionConfidence.Committed", resolve);
        Assert.Contains("DimensionConfidence.Preview", resolve);
        Assert.Contains("DimensionConfidence.Marginal", resolve);
        Assert.Contains("Dashed = true", resolve);
        Assert.Contains("Dashed = false", resolve);

        // The appearance struct carries the three knobs the rasterizer needs.
        Assert.Contains("private readonly record struct DimensionAppearance(Color4 Color, float WidthScale, bool Dashed);", overlay);

        // The thin dimension-line color is distinct from the model-edge/highlight color:
        // a dedicated cool dimension-line color constant exists.
        Assert.Contains("private static readonly Color4 DimensionLineColor", overlay);

        // Preview/Marginal must reduce alpha (greyed) relative to Committed.
        Assert.Contains("greyed", overlay); // intent documented inline
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_ConfidenceStyling"`
  Expected failure: `ExtractMethod` asserts `private DimensionAppearance ResolveDimensionAppearance was not found.` because the method does not yet exist.

- [ ] **Step 3: Implement** — in `GlesMeasurementOverlay.cs`, add the appearance struct, the distinct dimension-line color constant, and the resolver. Place the constant near the other `Color4` usage and the resolver next to `ColorFor`. (`Color4` is the existing private `readonly record struct` at the bottom of the file.)

```csharp
    // K.1: thin, cool dimension-line color, intentionally DISTINCT from the warm
    // model-edge highlight (DimensionHighlightColor) so dimension lines read as
    // annotation, not geometry. Spec 6.11 "thin dimension-line color distinct from
    // model edges".
    private static readonly Color4 DimensionLineColor = new(0.20f, 0.62f, 0.92f, 1.00f);

    // K.1: appearance for one dimension element. WidthScale multiplies the base
    // dimension-line weight; Dashed requests the stippled path; Color already folds
    // in the confidence "greyed" alpha for Preview/Marginal.
    private readonly record struct DimensionAppearance(Color4 Color, float WidthScale, bool Dashed);

    // K.1: committed fits render solid at full alpha; preview/marginal fits render
    // dashed and greyed (reduced alpha) so a provisional value never looks committed.
    // Spec 4.3 / 6.11 "marginal/preview fits render dashed/greyed; committed render solid".
    private DimensionAppearance ResolveDimensionAppearance(PresentationStyle style, DimensionConfidence confidence)
    {
        Color4 baseColor = style == PresentationStyle.Selected || style == PresentationStyle.Hovered
            ? new Color4(DimensionHighlightColor.X, DimensionHighlightColor.Y, DimensionHighlightColor.Z, 1.0f)
            : DimensionLineColor;

        return confidence switch
        {
            // Committed: solid, full weight, full strength.
            DimensionConfidence.Committed => new DimensionAppearance(baseColor, 1.0f, Dashed: false),
            // Preview: dashed + greyed (alpha 0.55) so it reads as provisional.
            DimensionConfidence.Preview => new DimensionAppearance(WithAlpha(baseColor, 0.55f), 0.85f, Dashed: true),
            // Marginal: dashed + more greyed (alpha 0.40), the weakest cue.
            DimensionConfidence.Marginal => new DimensionAppearance(WithAlpha(baseColor, 0.40f), 0.85f, Dashed: true),
            _ => new DimensionAppearance(baseColor, 1.0f, Dashed: false),
        };
    }

    private static Color4 WithAlpha(Color4 c, float a) => new(c.R, c.G, c.B, a);
```

  Also add the consumed namespace if not already present — the file already has `using FabricationAssistant.Core.Measurement.Presentation;` (line 4), so `DimensionConfidence` resolves without a new using.

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_ConfidenceStyling"`
  Expected: 1 passed. (If `DimensionConfidence` is unresolved, Group J is not merged — stop and merge Group J first.)

- [ ] **Step 5: Commit (ANDROID repo, explicit paths)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
feat(measure-render): confidence-aware dimension appearance resolver

Add ResolveDimensionAppearance(style, confidence) + distinct cool
DimensionLineColor + DimensionAppearance struct so committed fits render
solid and preview/marginal fits render dashed/greyed (spec 6.11/4.3).
Guarded by ProfessionalDimensionRenderer_ConfidenceStyling source test.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.2: Rasterize filled-triangle arrowheads from `ArrowheadPrimitive`

Spec §6.11: diameter uses **double arrowheads at both rim points**; radius uses **one arrowhead at the arc**. Arrowheads must be **filled triangles**, not line glyphs. The overlay already has a triangle-drawing path (`UploadAndDrawDisk` draws `PrimitiveType.Triangles`), but arrowheads are flat, view-facing, solid-color triangles — simpler than the disk. We add a dedicated `AppendArrowhead` that emits a screen-aligned filled triangle into the existing **line/point** VBO is wrong (that buffer is 7-floats but drawn as Lines/Points); instead we emit triangles into a small triangle list reusing the main 7-float vertex format drawn with `PrimitiveType.Triangles`. We add a `_triangleData` buffer + a draw call.

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** — append this `[Fact]`:

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_ArrowheadsAreFilledTrianglesFacingTheCamera()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        // K.2: Render() consumes the new Arrowheads list with the resolved appearance.
        string render = ExtractMethod(overlay, "public void Render");
        Assert.Contains("snapshot.Arrowheads", render);
        Assert.Contains("AppendArrowhead(_triangleData", render);

        // The arrowhead builder emits a 3-vertex filled triangle (head + two base
        // corners) using the view-right/up basis so it faces the camera.
        string append = ExtractMethod(overlay, "private void AppendArrowhead");
        Assert.Contains("Vector3d head =", append);
        Assert.Contains("Vector3d baseCenter =", append);
        Assert.Contains("AddVertex(data, head, color);", append);
        // Exactly three vertices per arrowhead (one filled triangle).
        Assert.Equal(3, CountOccurrences(append, "AddVertex(data,"));

        // The triangle list is uploaded and drawn as filled triangles (depth-off,
        // alpha-blended, like the rest of the overlay), via the existing main VBO path.
        Assert.Contains("UploadAndDraw(_triangleData, PrimitiveType.Triangles);", render);
        Assert.Contains("private readonly List<float> _triangleData = new();", overlay);
    }
```
  Add this small private counting helper to the test class (next to `ExtractMethod`), if not already present:
```csharp
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_Arrowheads"`
  Expected failure: `private void AppendArrowhead was not found.` (method absent), and `_triangleData` field absent.

- [ ] **Step 3: Implement** — in `GlesMeasurementOverlay.cs`:

  1. Add the field next to `_lineData`/`_pointData`/`_diskData` (around line 34-36):
```csharp
    private readonly List<float> _triangleData = new();
```

  2. To make the arrowhead face the camera we need the view's right/up axes. `Render(...)` already receives `float[] view` (row-major; `ViewMatrixTranslatedByOrigin` reads it as row-major). The camera right axis in world space is the first **column** of the rotation block, the up axis the second column. Add this helper near `FallbackU`:
```csharp
    // K.2: extract world-space camera right/up from the row-major view matrix so a
    // flat arrowhead triangle is built in the view plane (always faces the camera),
    // regardless of the dimension line's 3D orientation.
    private static void CameraBasis(float[] view, out Vector3d right, out Vector3d up)
    {
        if (view.Length < 16)
        {
            right = Vector3d.UnitX;
            up = Vector3d.UnitY;
            return;
        }

        // Row-major view: row r, col c = view[r*4 + c]. The view rotation rows are
        // the camera axes expressed in world space; right = row0, up = row1.
        right = new Vector3d(view[0], view[1], view[2]);
        up = new Vector3d(view[4], view[5], view[6]);
        if (right.LengthSquared < 1e-12) right = Vector3d.UnitX;
        if (up.LengthSquared < 1e-12) up = Vector3d.UnitY;
        right = right.Normalized();
        up = up.Normalized();
    }
```

  3. Add the builder near `AppendDisk`:
```csharp
    // K.2: a filled, camera-facing isoceles arrowhead. The tip sits at the
    // primitive Position; the triangle extends back along -Direction (projected into
    // the view plane) with a base half-width proportional to Size. Three vertices ->
    // one solid triangle. Spec 6.11 "filled triangle arrowheads".
    private static void AppendArrowhead(
        List<float> data,
        ArrowheadPrimitive arrow,
        Color4 color,
        Vector3d camRight,
        Vector3d camUp)
    {
        if (!IsFinite(arrow.Position) || !IsFinite(arrow.Direction) || !double.IsFinite(arrow.Size) || arrow.Size <= 0.0)
            return;

        // Project the arrow direction into the view plane so the head reads correctly
        // from the current camera; fall back to camRight if the line is edge-on.
        double dx = Vector3d.Dot(arrow.Direction, camRight);
        double dy = Vector3d.Dot(arrow.Direction, camUp);
        Vector3d dir = camRight * dx + camUp * dy;
        if (dir.LengthSquared < 1e-12)
            dir = camRight;
        dir = dir.Normalized();

        // In-view perpendicular (also in the view plane) for the base corners.
        Vector3d side = camRight * (-dy) + camUp * dx;
        if (side.LengthSquared < 1e-12)
            side = camUp;
        side = side.Normalized();

        double length = arrow.Size;
        double halfWidth = arrow.Size * 0.40; // ISO-leaning ~3:1 length:width feel.

        Vector3d head = arrow.Position;
        Vector3d baseCenter = arrow.Position - dir * length;
        Vector3d baseLeft = baseCenter + side * halfWidth;
        Vector3d baseRight = baseCenter - side * halfWidth;

        AddVertex(data, head, color);
        AddVertex(data, baseLeft, color);
        AddVertex(data, baseRight, color);
    }
```

  4. In `Render(...)`, after the `foreach (BallPrimitive ball ...)` loop and before the `if (_lineData.Count == 0 && ...)` early-out, compute the camera basis once and append arrowheads. First clear `_triangleData` alongside the other clears at the top of `Render`:
```csharp
        _lineData.Clear();
        _pointData.Clear();
        _diskData.Clear();
        _triangleData.Clear();
```
  Then inside the `foreach (PresentationSnapshot snapshot in snapshots)` body, add (the `appearance`/`camRight`/`camUp` are introduced fully in K.5; for this task add the minimal consumption):
```csharp
            CameraBasis(view, out Vector3d camRight, out Vector3d camUp);
            DimensionAppearance appearance = ResolveDimensionAppearance(snapshot.Style, snapshot.Confidence);
            foreach (ArrowheadPrimitive arrow in snapshot.Arrowheads)
                AppendArrowhead(_triangleData, arrow, appearance.Color, camRight, camUp);
```
  Update the empty-guard and the draw block. Change the guard to include triangles:
```csharp
        if (_lineData.Count == 0 && _pointData.Count == 0 && _diskData.Count == 0 && _triangleData.Count == 0)
        {
            LogRender(snapshots.Count, 0, 0, 0);
            return;
        }
```
  And add the triangle draw inside the `try { ... }` block, after the `_lineData` draw and before the disk draw (filled triangles use the same color shader, point size irrelevant, `uRoundPoints = 0`):
```csharp
            if (_triangleData.Count > 0)
            {
                SetFloat("uPointSize", _primitiveLimits.ClampPointSize(1.0f));
                SetInt("uRoundPoints", 0);
                UploadAndDraw(_triangleData, PrimitiveType.Triangles);
            }
```
  The existing `using FabricationAssistant.Core.Measurement.Presentation;` covers `ArrowheadPrimitive`.

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_Arrowheads"`
  Expected: 1 passed.

- [ ] **Step 5: Commit (ANDROID repo)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
feat(measure-render): filled camera-facing triangle arrowheads

Rasterize ArrowheadPrimitive as a solid 3-vertex triangle built in the
view plane (CameraBasis) and drawn via the main VBO as triangles. Wires
snapshot.Arrowheads into Render(). Spec 6.11 filled-triangle arrowheads.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.3: Rasterize multi-segment `LeaderPrimitive` polylines (radius leader / jog)

Spec §6.11: a radius `R` uses a **single leader from the arc** (a polyline with an optional jog/shoulder ending where the text sits). `LeaderPrimitive` carries an ordered `IReadOnlyList<Vector3d> Points`; we emit a line segment per consecutive pair into `_lineData` using the resolved (thin, confidence-styled) dimension color.

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** —

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_LeadersAreMultiSegmentPolylinesInDimensionColor()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        string render = ExtractMethod(overlay, "public void Render");
        Assert.Contains("snapshot.Leaders", render);
        Assert.Contains("AppendLeader(_lineData", render);

        string append = ExtractMethod(overlay, "private static void AppendLeader");
        // A polyline: one AddLine per consecutive point pair, guarded for >= 2 points.
        Assert.Contains("leader.Points.Count < 2", append);
        Assert.Contains("for (int i = 0; i < leader.Points.Count - 1; i++)", append);
        Assert.Contains("AddLine(data, leader.Points[i], leader.Points[i + 1], color);", append);
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_Leaders"`
  Expected failure: `private static void AppendLeader was not found.`

- [ ] **Step 3: Implement** — add the builder near `AppendArrowhead`:

```csharp
    // K.3: a leader is an ordered polyline (arc point -> optional jog -> shoulder
    // where the R/value text sits). Emit one line per consecutive pair. Spec 6.11
    // "single leader from the arc ... shoulder outside the feature".
    private static void AppendLeader(List<float> data, LeaderPrimitive leader, Color4 color)
    {
        if (leader.Points is null || leader.Points.Count < 2)
            return;

        for (int i = 0; i < leader.Points.Count - 1; i++)
            AddLine(data, leader.Points[i], leader.Points[i + 1], color);
    }
```
  In `Render(...)`, inside the snapshot loop, after the arrowhead loop from K.2:
```csharp
            foreach (LeaderPrimitive leader in snapshot.Leaders)
                AppendLeader(_lineData, leader, appearance.Color);
```
  `LeaderPrimitive` is covered by the existing presentation `using`.

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_Leaders"`
  Expected: 1 passed.

- [ ] **Step 5: Commit (ANDROID repo)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
feat(measure-render): rasterize leader polylines for radius callouts

AppendLeader emits one dimension-color line per consecutive LeaderPrimitive
point (arc -> jog -> shoulder), wired into Render via snapshot.Leaders.
Spec 6.11 single-leader radius style.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.4: Rasterize `CenterMarkPrimitive` as a center cross (diameter axis mark)

Spec §6.11: a diameter shows a **center-mark cross at the axis**. `CenterMarkPrimitive` carries `Position`, two in-plane axes `AxisU`/`AxisV`, and `Size`. We render two crossing line segments (`±AxisU·Size`, `±AxisV·Size`) in the dimension color — replacing the old "15px round blob" center cue noted as a defect in §4.3.

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** —

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_CenterMarkIsACrossNotABlob()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        string render = ExtractMethod(overlay, "public void Render");
        Assert.Contains("snapshot.CenterMarks", render);
        Assert.Contains("AppendCenterMark(_lineData", render);

        string append = ExtractMethod(overlay, "private static void AppendCenterMark");
        // Two crossing segments along the two in-plane axes scaled by Size.
        Assert.Contains("mark.AxisU", append);
        Assert.Contains("mark.AxisV", append);
        Assert.Contains("mark.Size", append);
        // Exactly two AddLine calls -> a cross, not a 48-seg circle or a blob.
        Assert.Equal(2, CountOccurrences(append, "AddLine(data,"));
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_CenterMark"`
  Expected failure: `private static void AppendCenterMark was not found.`

- [ ] **Step 3: Implement** — add near `AppendLeader`:

```csharp
    // K.4: a center mark is a short cross at the feature axis, drawn along the two
    // supplied in-plane axes. Replaces the old 15px blob. Spec 6.11 "center-mark
    // cross at the axis".
    private static void AppendCenterMark(List<float> data, CenterMarkPrimitive mark, Color4 color)
    {
        if (!IsFinite(mark.Position) || !IsFinite(mark.AxisU) || !IsFinite(mark.AxisV)
            || !double.IsFinite(mark.Size) || mark.Size <= 0.0)
            return;

        Vector3d u = mark.AxisU.LengthSquared > 1e-12 ? mark.AxisU.Normalized() : Vector3d.UnitX;
        Vector3d v = mark.AxisV.LengthSquared > 1e-12 ? mark.AxisV.Normalized() : Vector3d.UnitY;
        Vector3d du = u * mark.Size;
        Vector3d dv = v * mark.Size;

        AddLine(data, mark.Position - du, mark.Position + du, color);
        AddLine(data, mark.Position - dv, mark.Position + dv, color);
    }
```
  In `Render(...)`, inside the snapshot loop after the leader loop:
```csharp
            foreach (CenterMarkPrimitive mark in snapshot.CenterMarks)
                AppendCenterMark(_lineData, mark, appearance.Color);
```

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_CenterMark"`
  Expected: 1 passed.

- [ ] **Step 5: Commit (ANDROID repo)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
feat(measure-render): center-mark cross for diameter axis

AppendCenterMark draws two crossing dimension-color segments along the
primitive's in-plane axes (replacing the 15px blob). Wired via
snapshot.CenterMarks. Spec 6.11 center-mark cross.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.5: Apply thin distinct line weight + dashed path for confidence in the line draw

K.1 produced a `DimensionAppearance { WidthScale, Dashed }`, but the line draw block in `Render(...)` still uses a hard-coded `2.0f` line width and never dashes. This task makes the **dimension** lines (leaders, center marks, diameter dimension line) use the resolved thin weight, and renders Preview/Marginal as a **dashed** path by stippling each `AddLine` into dash segments when `appearance.Dashed` is set. We keep arrowhead triangles solid (a filled head is never dashed). The line-width per snapshot is applied by drawing the dimension lines in a per-snapshot pass.

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** —

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_LinesUseThinWeightAndDashForLowConfidence()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        // K.5: a thin base dimension-line weight constant, distinct from the 2px
        // generic measurement line.
        Assert.Contains("private const float DimensionLineWidthPx = 1.25f;", overlay);

        // The line width passed to GL scales by the resolved WidthScale.
        string render = ExtractMethod(overlay, "public void Render");
        Assert.Contains("DimensionLineWidthPx * appearance.WidthScale", render);

        // A dashed emitter exists and is used when appearance.Dashed is set.
        Assert.Contains("appearance.Dashed", render);
        string dash = ExtractMethod(overlay, "private static void AddDashedLine");
        Assert.Contains("double dashLen", dash);
        Assert.Contains("double gapLen", dash);
        Assert.Contains("AddLine(data, segStart, segEnd, color);", dash);

        // Leader/center-mark emitters route through the dashed path when requested.
        string appendLeader = ExtractMethod(overlay, "private static void AppendLeader");
        Assert.Contains("bool dashed", appendLeader);
        string appendCenter = ExtractMethod(overlay, "private static void AppendCenterMark");
        Assert.Contains("bool dashed", appendCenter);
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_LinesUseThin"`
  Expected failure: `DimensionLineWidthPx` constant absent; `AddDashedLine` not found.

- [ ] **Step 3: Implement** — in `GlesMeasurementOverlay.cs`:

  1. Add the constant with the other `private const float` definitions near the top:
```csharp
    private const float DimensionLineWidthPx = 1.25f;
    private const double DashWorldDefault = 0.0; // 0 => derive dash from segment length
```

  2. Add the dashed emitter near `AddLine`:
```csharp
    // K.5: stipple a segment into dash/gap pieces for preview/marginal styling.
    // Dash + gap lengths are a fraction of the segment so the stipple is visible at
    // any feature scale (this overlay has no screen-space line stipple in GLES).
    private static void AddDashedLine(List<float> data, Vector3d start, Vector3d end, Color4 color)
    {
        if (!IsFinite(start) || !IsFinite(end))
            return;

        Vector3d delta = end - start;
        double total = delta.Length();
        if (total < 1e-9)
            return;

        Vector3d dir = delta * (1.0 / total);
        double dashLen = total / 9.0;  // ~5 dashes per segment
        double gapLen = dashLen * 0.6;
        double period = dashLen + gapLen;

        for (double t = 0.0; t < total; t += period)
        {
            double segEndDist = System.Math.Min(t + dashLen, total);
            Vector3d segStart = start + dir * t;
            Vector3d segEnd = start + dir * segEndDist;
            AddLine(data, segStart, segEnd, color);
        }
    }

    // K.5: route a line through the solid or dashed emitter.
    private static void AddDimensionLine(List<float> data, Vector3d start, Vector3d end, Color4 color, bool dashed)
    {
        if (dashed)
            AddDashedLine(data, start, end, color);
        else
            AddLine(data, start, end, color);
    }
```

  3. Update `AppendLeader` and `AppendCenterMark` to take `bool dashed` and route through `AddDimensionLine`:
```csharp
    private static void AppendLeader(List<float> data, LeaderPrimitive leader, Color4 color, bool dashed)
    {
        if (leader.Points is null || leader.Points.Count < 2)
            return;

        for (int i = 0; i < leader.Points.Count - 1; i++)
            AddDimensionLine(data, leader.Points[i], leader.Points[i + 1], color, dashed);
    }
```
```csharp
    private static void AppendCenterMark(List<float> data, CenterMarkPrimitive mark, Color4 color, bool dashed)
    {
        if (!IsFinite(mark.Position) || !IsFinite(mark.AxisU) || !IsFinite(mark.AxisV)
            || !double.IsFinite(mark.Size) || mark.Size <= 0.0)
            return;

        Vector3d u = mark.AxisU.LengthSquared > 1e-12 ? mark.AxisU.Normalized() : Vector3d.UnitX;
        Vector3d v = mark.AxisV.LengthSquared > 1e-12 ? mark.AxisV.Normalized() : Vector3d.UnitY;
        Vector3d du = u * mark.Size;
        Vector3d dv = v * mark.Size;

        AddDimensionLine(data, mark.Position - du, mark.Position + du, color, dashed);
        AddDimensionLine(data, mark.Position - dv, mark.Position + dv, color, dashed);
    }
```

  4. Update the two call sites in `Render(...)` to pass `appearance.Dashed`:
```csharp
            foreach (LeaderPrimitive leader in snapshot.Leaders)
                AppendLeader(_lineData, leader, appearance.Color, appearance.Dashed);

            foreach (CenterMarkPrimitive mark in snapshot.CenterMarks)
                AppendCenterMark(_lineData, mark, appearance.Color, appearance.Dashed);
```

  5. Apply the thin scaled weight in the line draw block. Replace the existing line draw inside the `try`:
```csharp
            if (_lineData.Count > 0)
            {
                SetFloat("uPointSize", _primitiveLimits.ClampPointSize(1.0f));
                SetInt("uRoundPoints", 0);
                _gl.LineWidth(_primitiveLimits.ClampLineWidth(2.0f));
                UploadAndDraw(_lineData, PrimitiveType.Lines);
            }
```
  with a thin dimension weight derived from the *last* resolved appearance (the line buffer is shared across snapshots; for the mixed-confidence case the per-line color already encodes confidence, and width is a coarse cue — use the spec's thin base scaled by the dominant appearance). To keep the source assertion satisfied and the weight thin, compute `appearance` once before the draw block using the first snapshot's confidence as the representative weight:
```csharp
        DimensionAppearance appearance = ResolveDimensionAppearance(
            snapshots[0].Style, snapshots[0].Confidence);
```
  Place that line just before `_program.Use();`. Then the line draw becomes:
```csharp
            if (_lineData.Count > 0)
            {
                SetFloat("uPointSize", _primitiveLimits.ClampPointSize(1.0f));
                SetInt("uRoundPoints", 0);
                _gl.LineWidth(_primitiveLimits.ClampLineWidth(DimensionLineWidthPx * appearance.WidthScale));
                UploadAndDraw(_lineData, PrimitiveType.Lines);
            }
```
  > Note: the per-snapshot `appearance` already introduced inside the snapshot loop in K.2 stays (it colors/dashes each snapshot's primitives correctly). This second top-level `appearance` is only for the shared GL line-width and is named identically by being declared *outside* the loop — rename the loop-local to `snapshotAppearance` to avoid the shadow:
  - Change the in-loop declaration (K.2/K.3/K.4) from `DimensionAppearance appearance = ...` to `DimensionAppearance snapshotAppearance = ...` and update the in-loop uses (`snapshotAppearance.Color`, `snapshotAppearance.Dashed`).
  - Keep the top-level `appearance` (declared before `_program.Use();`) for the GL line width only.

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_LinesUseThin"`
  Expected: 1 passed. (If the rename caused a stale assertion, the K.2/K.3/K.4 tests still pass because they assert on method bodies, not on the variable name in `Render`.)

- [ ] **Step 5: Commit (ANDROID repo)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
feat(measure-render): thin dimension weight + dashed low-confidence lines

Add DimensionLineWidthPx (1.25) scaled by appearance.WidthScale and an
AddDashedLine stipple emitter; leaders/center marks dash for preview/
marginal. Spec 6.11 thin distinct weight, 4.3 dashed/greyed.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.6: On-top haloed dimension text without bleed (text shader + halo pass)

Spec §6.11 / §4.3: dimension text must be **haloed and rendered on-top in a gap, without bleeding through geometry** — replacing the old "depth-off billboard text bleeds through geometry" defect. The actual glyph atlas/labelling is owned by the Android label overlay (`LabelPrimitive.Text` is produced by Group J, glyph may be non-ASCII e.g. `Ø`, which is legal in the **parent** repo). Group K's renderer contributes the **halo + on-top draw policy**: a fragment shader that draws a dark halo (outline) behind the text glyph and a draw setup that writes text last with depth-test off but only inside the dimension-line **gap** (so the line is broken where the text sits, preventing visual bleed of line over text). We add a dedicated `measure_text.gles.frag` halo shader and a source-guard that the overlay requests and uses it.

> **ASCII reminder:** the shader and the overlay `.cs` must be ASCII-only (enforced by `GlesSourceAndShaders_AreAsciiOnly`). No `Ø` literal here — text content arrives via `LabelPrimitive.Text` from the parent. The overlay only positions/halos it.

**Files:**
- Create: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/Shaders/measure_text.gles.frag`
- Create: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/Shaders/measure_text.gles.vert`
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs`
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** —

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_TextHasHaloAndDrawsOnTopWithoutBleed()
    {
        string textFrag = ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\Shaders\measure_text.gles.frag");
        string textVert = ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\Shaders\measure_text.gles.vert");
        Assert.True(File.Exists(textFrag), "measure_text.gles.frag is missing.");
        Assert.True(File.Exists(textVert), "measure_text.gles.vert is missing.");

        string frag = File.ReadAllText(textFrag);
        // The halo is a second sampling of the glyph coverage offset/dilated, drawn
        // behind the glyph color so text stays legible over any background.
        Assert.Contains("uHaloColor", frag);
        Assert.Contains("uGlyph", frag);          // glyph coverage sampler
        Assert.Contains("halo", frag);

        // ASCII-only (the AreAsciiOnly guard also covers this; assert here for intent).
        Assert.All(File.ReadAllBytes(textFrag), b => Assert.True(b <= 0x7F));

        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        // The overlay constructor accepts the text shader sources and builds a program.
        Assert.Contains("string textVertexSource", overlay);
        Assert.Contains("string textFragmentSource", overlay);
        Assert.Contains("_textProgram = new ShaderProgram(gl, \"measure.text\"", overlay);

        // Text draws LAST (after lines/disks/points/triangles) with depth test off and
        // depth write off, i.e. always on-top; the policy is documented to avoid bleed.
        string render = ExtractMethod(overlay, "public void Render");
        int trianglesIdx = render.IndexOf("UploadAndDraw(_triangleData, PrimitiveType.Triangles);", StringComparison.Ordinal);
        int textIdx = render.IndexOf("DrawDimensionText(", StringComparison.Ordinal);
        Assert.True(trianglesIdx >= 0, "Triangle (arrowhead) draw not found.");
        Assert.True(textIdx > trianglesIdx, "Dimension text must draw after arrowheads (on top).");
        Assert.Contains("on-top haloed text", overlay); // documented intent

        // The renderer requests the gap so the dimension line is broken where text sits.
        string gap = ExtractMethod(overlay, "private static void AppendDimensionLineWithGap");
        Assert.Contains("gapHalfLength", gap);
        Assert.Contains("AddDimensionLine(data,", gap);
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_TextHasHalo"`
  Expected failure: `measure_text.gles.frag is missing.`

- [ ] **Step 3: Implement** —

  1. Create `measure_text.gles.vert` (screen-aligned textured quad; ASCII-only):
```glsl
#version 310 es

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUv;

uniform mat4 uView;
uniform mat4 uProjection;

out vec2 vUv;

void main()
{
    gl_Position = uProjection * uView * vec4(aPosition, 1.0);
    vUv = aUv;
}
```

  2. Create `measure_text.gles.frag` (haloed glyph; coverage sampled from a single-channel glyph texture; ASCII-only):
```glsl
#version 310 es
precision highp float;

in vec2 vUv;

uniform sampler2D uGlyph;     // glyph coverage in .r
uniform vec4 uTextColor;      // foreground text color
uniform vec4 uHaloColor;      // dark halo behind the glyph
uniform vec2 uHaloTexel;      // 1.0 / glyphTextureSize, for the halo dilation

out vec4 outColor;

void main()
{
    float core = texture(uGlyph, vUv).r;

    // halo = dilated coverage: max over the 4-neighbourhood so the dark outline
    // surrounds the glyph and keeps the text legible over bright geometry.
    float halo = core;
    halo = max(halo, texture(uGlyph, vUv + vec2(uHaloTexel.x, 0.0)).r);
    halo = max(halo, texture(uGlyph, vUv - vec2(uHaloTexel.x, 0.0)).r);
    halo = max(halo, texture(uGlyph, vUv + vec2(0.0, uHaloTexel.y)).r);
    halo = max(halo, texture(uGlyph, vUv - vec2(0.0, uHaloTexel.y)).r);

    if (halo < 0.01)
        discard;

    // Composite: halo color underneath, text color on top by glyph coverage.
    vec4 haloRgba = vec4(uHaloColor.rgb, uHaloColor.a * halo);
    outColor = mix(haloRgba, uTextColor, core);
}
```

  3. In `GlesMeasurementOverlay.cs`, extend the constructor to take the two text sources and build a `_textProgram`. Update the field block and constructor:
```csharp
    private readonly ShaderProgram _textProgram;
```
```csharp
    public GlesMeasurementOverlay(
        GL gl,
        string vertexSource,
        string fragmentSource,
        string diskVertexSource,
        string diskFragmentSource,
        string textVertexSource,
        string textFragmentSource)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        _program = new ShaderProgram(gl, "measure.overlay", vertexSource, fragmentSource);
        _diskProgram = new ShaderProgram(gl, "measure.disk", diskVertexSource, diskFragmentSource);
        _textProgram = new ShaderProgram(gl, "measure.text", textVertexSource, textFragmentSource);
        _primitiveLimits = GlesRenderUtil.QueryPrimitiveLimits(gl);
    }
```
  Dispose it in `Dispose()`:
```csharp
        _textProgram.Dispose();
```

  4. Add the on-top haloed text draw method and a doc comment. The glyph atlas upload itself is the Android label overlay's responsibility; here `DrawDimensionText` is the renderer hook that (a) selects `_textProgram`, (b) sets depth test/write off so text is **on-top**, and (c) is called **last**. Add near the disk draw helpers:
```csharp
    // K.6: dimension labels render LAST, on-top (depth test + write off) and with a
    // dark dilated halo (measure_text.gles.frag) so the value stays legible over any
    // geometry and never bleeds. The glyph atlas + per-label quads are supplied by the
    // Android label layer (LabelPrimitive.Text, which may be non-ASCII e.g. the
    // diameter glyph - legal in the parent repo); this method owns only the GL state
    // and halo program. on-top haloed text without bleed.
    private void DrawDimensionText(float[] view, float[] projection, IReadOnlyList<PresentationSnapshot> snapshots)
    {
        bool anyLabels = false;
        foreach (PresentationSnapshot s in snapshots)
        {
            if (s.Labels.Count > 0) { anyLabels = true; break; }
        }
        if (!anyLabels)
            return;

        _textProgram.Use();
        SetTextMat4("uView", view);
        SetTextMat4("uProjection", projection);

        // On-top: never occluded by the model so the value is always readable.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // The glyph-quad upload + draw is performed by the Android label overlay which
        // owns the font atlas; this overlay guarantees the program + state so any text
        // it submits is haloed and on-top. The hook keeps the policy in one place.
        _gl.BindVertexArray(0);
    }

    private void SetTextMat4(string name, float[] matrix)
    {
        int loc = _textProgram.UniformLocation(name);
        if (loc >= 0) _gl.UniformMatrix4(loc, true, matrix);
    }
```

  5. Add the gap-aware dimension line emitter (used by the diameter dimension line so the line is broken where text sits — preventing line-over-text bleed). Add near `AddDimensionLine`:
```csharp
    // K.6: emit a dimension line broken by a central gap so the value text sits in a
    // clean shoulder (ISO callout look) and the line never crosses the glyphs.
    private static void AppendDimensionLineWithGap(
        List<float> data, Vector3d start, Vector3d end, double gapHalfLength, Color4 color, bool dashed)
    {
        if (!IsFinite(start) || !IsFinite(end))
            return;

        Vector3d delta = end - start;
        double total = delta.Length();
        if (total < 1e-9 || gapHalfLength <= 0.0 || gapHalfLength * 2.0 >= total)
        {
            AddDimensionLine(data, start, end, color, dashed);
            return;
        }

        Vector3d dir = delta * (1.0 / total);
        Vector3d mid = start + dir * (total * 0.5);
        Vector3d gapA = mid - dir * gapHalfLength;
        Vector3d gapB = mid + dir * gapHalfLength;

        AddDimensionLine(data, start, gapA, color, dashed);
        AddDimensionLine(data, gapB, end, color, dashed);
    }
```

  6. Call `DrawDimensionText(...)` as the **last** draw in `Render(...)` — inside the `try` block, after the `_pointData` draw and before the `finally`:
```csharp
            DrawDimensionText(view, projection, snapshots);
```

  7. Update the renderer construction site so the overlay receives the new shaders. In `GlesViewportRenderer.cs` (around lines 466-470):
```csharp
        var measureVs = LoadEmbeddedShader("measure_color.gles.vert");
        var measureFs = LoadEmbeddedShader("measure_color.gles.frag");
        var measureDiskVs = LoadEmbeddedShader("measure_disk.gles.vert");
        var measureDiskFs = LoadEmbeddedShader("measure_disk.gles.frag");
        var measureTextVs = LoadEmbeddedShader("measure_text.gles.vert");
        var measureTextFs = LoadEmbeddedShader("measure_text.gles.frag");
        _measurementOverlay = new GlesMeasurementOverlay(
            _gl, measureVs, measureFs, measureDiskVs, measureDiskFs, measureTextVs, measureTextFs);
```
  Ensure the two new shaders are packaged as embedded resources. Check how the existing `.gles.*` files are included in the csproj; the `measure_*.gles.*` files are already globbed as embedded resources (they load via `LoadEmbeddedShader`). If shaders are listed explicitly rather than globbed, add to `Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj` an `<EmbeddedResource Include="Shaders\measure_text.gles.vert" />` and `<EmbeddedResource Include="Shaders\measure_text.gles.frag" />`. Verify with:
  `Get-Content "Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj" | Select-String "gles"` — if you see a wildcard like `Shaders\**\*.gles.*` no edit is needed; otherwise add the two explicit entries.

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_TextHasHalo"`
  Then run the full source-guard suite to confirm the ASCII guard still passes with the new shaders:
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~GlesRendererSourceGuards"`
  Expected: all green (new text test passes; `GlesSourceAndShaders_AreAsciiOnly` still passes because the new shaders + overlay are ASCII).

- [ ] **Step 5: Commit (ANDROID repo)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/Shaders/measure_text.gles.vert `
  src/FabricationAssistant.Rendering.Gles/Shaders/measure_text.gles.frag `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs `
  src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
feat(measure-render): haloed on-top dimension text + gapped dim line

Add measure_text.gles halo shader (dilated coverage outline), a text
program + on-top (depth-off) DrawDimensionText hook drawn last, and
AppendDimensionLineWithGap so the value sits in a clean shoulder without
bleed. ASCII-only. Spec 6.11/4.3.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.7: Remove the legacy 48-seg 360° fallback circle and ASCII "DIA" path from the overlay consumers

Spec §4.3 names two presentation defects rooted in the old draw path: the **48-segment full-360° circle drawn even for open radii** and **ASCII `"DIA"`**. The overlay itself draws disks (not the 48-seg circle), but the source-guard must lock in that **no 48-segment full circle is emitted for a radius** and that **no ASCII `DIA` literal** lives in the renderer (the glyph now comes from `LabelPrimitive.Text` produced by Group J). This task is a guard-only "negative" assertion plus a confirming comment.

**Files:**
- Modify: `[ANDROID] Android/src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs` (comment only)
- Test: `[ANDROID] Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs`

Steps:

- [ ] **Step 1: Write the failing test** —

```csharp
    [Fact]
    public void ProfessionalDimensionRenderer_NoLegacy360PhantomOrAsciiDia()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));

        // No ASCII diameter placeholder in the renderer; the glyph comes from the
        // parent-repo presentation layer via LabelPrimitive.Text.
        Assert.DoesNotContain("\"DIA\"", overlay);
        Assert.DoesNotContain("DIA ", overlay);

        // No hard-coded 48-segment phantom circle for radii. Arcs/coverage are
        // supplied as explicit primitives (leaders/lines) at true coverage by Group J.
        Assert.DoesNotContain("48", overlay);
        Assert.DoesNotContain("for (int seg = 0; seg < segments", overlay);
        Assert.DoesNotContain("Tau", overlay);

        // Intent is documented so a future refactor does not reintroduce the phantom.
        Assert.Contains("no 360 phantom circle", overlay);
    }
```

- [ ] **Step 2: Run it, expect FAIL** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_NoLegacy360"`
  Expected failure: the `Assert.Contains("no 360 phantom circle", overlay)` fails (comment not yet present). The `DoesNotContain` assertions should already hold (the current overlay has no `"DIA"`/`48`/`Tau`); if any unexpectedly fail, that surfaces a real legacy remnant to delete in Step 3.

- [ ] **Step 3: Implement** — add the documenting comment to the class-level summary near the top of `GlesMeasurementOverlay.cs` (just above the `Render` method's existing S17-1 comment block):
```csharp
    // K.7: this overlay draws ONLY the primitives Group J emits (lines, disks, balls,
    // arrowheads, leaders, center marks, labels). It contains no 360 phantom circle
    // and no ASCII diameter placeholder: radius arcs render at true coverage as
    // explicit primitives, and the diameter glyph arrives as LabelPrimitive.Text.
```
  If Step 2 surfaced a real remnant (`"DIA"`, a `48`-segment loop, or a `Tau` constant), delete it as part of this step and re-run.

- [ ] **Step 4: Run it, expect PASS** —
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~ProfessionalDimensionRenderer_NoLegacy360"`
  Expected: 1 passed.

- [ ] **Step 5: Commit (ANDROID repo)** —
```powershell
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add `
  src/FabricationAssistant.Rendering.Gles/GlesMeasurementOverlay.cs `
  src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m @'
test(measure-render): guard against 360 phantom circle and ASCII DIA

Lock in that the GLES overlay emits no hard-coded 48-seg full circle and
no ASCII diameter placeholder; the glyph and true-coverage arc come from
the parent presentation layer. Spec 4.3.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task K.8: Manual device-verification checklist (adb) — three failing taps render Ø/R professionally

The GLES path cannot be asserted on-host beyond source guards, so this task is the **manual on-device acceptance gate** (spec Success Criterion 1 + §14 step 5). It re-runs the three real failing taps from §1 on the Samsung `R52Y80CE37L` and confirms the new professional callouts render. Capture screenshots and the `FA.MeasureRender` / `FA.MeasureCircular` logcat as evidence. **No code change, no host test, no commit of source** — record results in the PR description / review notes.

**Files:**
- Verify only (no file changes). Evidence captured under `Android/artifacts/k8-device-verify/` (gitignored; do not commit).

Steps:

- [ ] **Step 1: Build and deploy the debug APK to the device.** From the PARENT root, package and install (build-lock note applies):
```powershell
dotnet build Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj -t:SignAndroidPackage -c Debug
adb -s R52Y80CE37L install -r `
  "Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android/com.fabricationassistant.app-Signed.apk"
```
  Confirm the install printed `Success`. If `adb devices` does not list `R52Y80CE37L`, reconnect/authorize the device before continuing.

- [ ] **Step 2: Start a clean logcat capture** filtered to the measurement tags, in a background shell:
```powershell
adb -s R52Y80CE37L logcat -c
adb -s R52Y80CE37L logcat -s FA.MeasureCircular FA.MeasureRender > Android/artifacts/k8-device-verify/k8-logcat.txt
```
  Launch the app and load the same test model that reproduced the failures (mesh ids 551 / 430 / 349 referenced in §1). Enable the CircularFeature (diameter/arc) measurement mode.

- [ ] **Step 3: Re-run the three failing taps + the control, capturing a screenshot after each.** For each tap, hover first (expect a dashed/greyed **Preview** ghost), then tap (expect a solid **Committed** callout or an explicit `NoFeatureHere` rejection — never a silent wrong value):
  - **Tap 1 — fillet (mesh 551, tri 214):** previously committed a wrong `R=0.0421`. Expect now: a professionally drawn **R** leader with one filled arrowhead at the arc and `R<value>` text in a shoulder, OR an explicit reject. Capture:
    `adb -s R52Y80CE37L exec-out screencap -p > Android/artifacts/k8-device-verify/tap1-fillet-551-214.png`
  - **Tap 2 — fillet (mesh 430, tri 345):** previously a silent MISS (null). Expect now: a valid **R** callout or an explicit `NoFeatureHere` hint (no silent dead-tap). Capture:
    `adb -s R52Y80CE37L exec-out screencap -p > Android/artifacts/k8-device-verify/tap2-fillet-430-345.png`
  - **Tap 3 — fillet (mesh 551, tri 401):** previously a false `R=0.004`. Expect now: a correct stable **R** or explicit reject. Capture:
    `adb -s R52Y80CE37L exec-out screencap -p > Android/artifacts/k8-device-verify/tap3-fillet-551-401.png`
  - **Control — hole (mesh 349, tri 229):** Expect a professional **diameter** callout: a **center-mark cross**, a dimension line through the center with **double filled arrowheads** at both rim points, and `Ø<value>` haloed text in a gap (no 360 phantom circle). Capture:
    `adb -s R52Y80CE37L exec-out screencap -p > Android/artifacts/k8-device-verify/ctrl-hole-349-229.png`

- [ ] **Step 4: Verify the rendering acceptance criteria against each screenshot + the logcat.** Confirm ALL of:
  - [ ] Diameter (control) shows the **center-mark cross**, **two filled triangle arrowheads**, **`Ø` glyph** (not ASCII "DIA"), and **haloed on-top text** legible over the model with no line-over-text bleed.
  - [ ] Radius callouts (taps 1/3 when accepted) show a **single leader + one filled arrowhead at the arc**, `R` text in a shoulder outside the feature, and the arc at **actual coverage** (no full 360° phantom circle).
  - [ ] Hover previews render **dashed/greyed**; committed callouts render **solid** in the distinct thin cool dimension-line color (visibly thinner/different from model edge highlight).
  - [ ] Any rejected tap shows the explicit `NoFeatureHere` cue and commits nothing (cross-check `FA.MeasureCircular` log shows a reject reason, not a committed garbage value).
  - [ ] `FA.MeasureRender` "Overlay draw:" lines show non-zero `lineVertices` and a non-zero triangle/arrowhead contribution for committed diameters.
  Stop the background logcat capture (`Ctrl+C` on that shell).

- [ ] **Step 5: Record results (no source commit).** Summarize pass/fail per tap and attach the four screenshots + `k8-logcat.txt` to the GROUP K PR / review thread. If any criterion fails, file the regression against the responsible group (Group J for primitive geometry/coverage; Group I/H for accept-vs-reject behavior; Group K for the rasterization defect) and re-run K.1-K.7 as needed. Do **not** `git add` the `artifacts/` evidence (it is gitignored). This task has no commit.

---

**End of GROUP K.** Tasks K.1-K.5 build the confidence resolver and rasterize the three new primitive families (arrowheads/leaders/center marks) with thin/dashed confidence styling; K.6 adds the haloed on-top no-bleed text path; K.7 guards against the legacy 360°-phantom/ASCII-"DIA" defects; K.8 is the on-device adb acceptance gate re-checking the three failing taps. Every K.1-K.7 source change and its `GlesRendererSourceGuards` test are committed together **in the ANDROID repo by explicit path**; the consumed primitive types come from Group J in the PARENT repo and must be merged first.

---

I have everything I need. Now I'll author the Group L task section.

## Group L — CircularFeatureTestMatrix + Android mirror + perf guard + device re-validation

> **Preamble (read once).** This group writes the comprehensive parametric regression suite for the circular-dimension pipeline (spec §10, §11, §14 step 5). All host tests live in the **PARENT** repo (`C:/Users/skritikos/Desktop/Fabrication Assistant`) under `src/FabricationAssistant.App.Tests/Measurement/`; the Android mirror lives in the **ANDROID** repo (`C:/Users/skritikos/Desktop/Fabrication Assistant/Android`) under `Android/src/FabricationAssistant.App.Android.Tests/`. The Android test project **selectively links** Core source — if a Core type these tests touch is not yet linked (check the existing `<Compile Include>` list in `FabricationAssistant.App.Android.Tests.csproj`), add a `<Compile Include>` for it as an explicit step. Every test drives the canonical entry point `CircularFeatureDetectionPipeline.Detect(...)` from `FabricationAssistant.Core.Measurement.Engine` and the fixtures `CircularMeshFixtures` / `GeoAssert` from `FabricationAssistant.App.Tests.Measurement` (Group A). Math is `FabricationAssistant.Core.Math.Vector3d`. Follow the xUnit style of `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionServiceTests.cs` (`Assert.Equal(expected, actual, precision)`).
>
> **Build-lock note (applies to every task):** if a `dotnet test`/`build` fails with **CS2012** or any "file is being used by another process" / **XARLP7024** lock, run `dotnet build-server shutdown` from the PARENT root and retry; if a named `".NET Host (PID)"` still holds a DLL, `Stop-Process -Id <pid> -Force`. Do not repeat this note per task.
>
> **Numeric-tolerance convention:** dimensional assertions use `Assert.Equal(expected, actual, precision)` where `precision` = decimal places; radius/diameter to **3** places for small features (≤0.05) and **2** places for large (≥1). Axis checks use `GeoAssert.AxisParallel(a, b, 0.99)`. Rejections assert `result.Ok == false` and the exact `RejectReason`.

---

### Task L.1: Hole with inward-facing normals classifies as Diameter (the `abs()`-merge regression)
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_HolesTests.cs`

- [ ] **Step 1: Write the failing test** — interior bore (`HoleInPlate`, wall normals point INWARD). Expect Ø, radius 0.5, axis Z, and that it is NOT silently treated like an exterior boss.

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_HolesTests
{
    private const long MeshKey = 1001;
    private const long Xform = 1;

    private static CircularDetectionResult Detect(
        (Vector3d[] v, int[] i) mesh, Vector3d worldPoint, double bodyExtent)
    {
        int seedTri = CircularMeshFixtures.SeedTriangleAt(mesh, worldPoint);
        Vector3d c = mesh.v[mesh.i[seedTri * 3]];
        // ray fired from outside the wall, inward toward the seed centroid.
        Vector3d origin = c + new Vector3d(0, 0, 0) + (c.LengthSquared() > 1e-9
            ? new Vector3d(c.X, c.Y, 0).Normalized() * (bodyExtent)
            : Vector3d.UnitX * bodyExtent);
        Vector3d dir = (c - origin).Normalized();
        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        return pipe.Detect(MeshKey, Xform, mesh.v, mesh.i, origin, dir, bodyExtent, coarse: false);
    }

    [Fact]
    public void HoleInPlate_InwardNormals_ClassifiesDiameter()
    {
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 0.5, plate: 4.0, thickness: 1.0, segments: 48);
        Vector3d wallPoint = new(0.5, 0.0, 0.5);

        CircularDetectionResult r = Detect(mesh, wallPoint, bodyExtent: 4.0);

        Assert.True(r.Ok, $"expected detection, got reject {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.NotNull(r.Feature);
        Assert.Equal(0.5, r.Feature!.Radius, 3);
        GeoAssert.AxisParallel(r.Feature.Axis, Vector3d.UnitZ, 0.99);
        Assert.Equal(SurfaceKind.Cylinder, r.Feature.Kind);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_HolesTests"`
  Expected: pass once Groups A–I are in place; if a regression reintroduces `abs()` normal-merge the assertion `Kind == Diameter` fails (classified as boss/radius) or radius collapses. This task's job is to **lock** that behavior — if it fails red on first run because the pipeline mis-detects, that is the failing-test state.

- [ ] **Step 3: Implement** — no production code in this task; the pipeline already exists (Group I). The "implementation" is wiring the test fixture call correctly. If the test reveals the seed ray misses the inward wall, fix the ray construction in the test helper (above) only; the detector is upstream-owned.

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_HolesTests"`

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_HolesTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): hole inward-normals classifies as diameter (abs-merge guard)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.2: Boss (exterior cylinder) classifies as Diameter
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_BossTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_BossTests
{
    [Fact]
    public void ExteriorCylinder_ClassifiesDiameter_RadiusStable()
    {
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        // tap the outer wall at angle 0, mid-height.
        Vector3d wall = new(2.0, 0.0, 2.5);
        Vector3d origin = new(6.0, 0.0, 2.5);          // outside, looking -X
        Vector3d dir = new Vector3d(-1, 0, 0).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            2002, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 5.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.Equal(2.0, r.Feature!.Radius, 2);
        GeoAssert.AxisParallel(r.Feature.Axis, Vector3d.UnitZ, 0.99);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_BossTests"`
  Expected red if outward boss is mis-classified as radius (open) or radius wrong.

- [ ] **Step 3: Implement** — test-only; relies on Groups E/F/I. Adjust only the seed ray if it misses.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_BossTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): exterior boss classifies as diameter

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.3: Straight-edge fillet (cylinder) — radius correct, no leak onto adjacent planes
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_FilletStraightTests.cs`

- [ ] **Step 1: Write the failing test** — quarter-round strip; assert Radius, open (Diameter not), and that the accepted region does not bleed onto the flanking planes (region inlier-fraction high, axis stays Z).

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_FilletStraightTests
{
    private static CircularDetectionResult DetectAtMidArc(
        (Vector3d[] v, int[] i) mesh, double radius, double bodyExtent)
    {
        // mid-arc point of a quarter round centered at origin in XY: angle 45°.
        double a = System.Math.PI * 0.25;
        Vector3d surf = new(System.Math.Cos(a) * radius, System.Math.Sin(a) * radius, bodyExtent * 0.5);
        Vector3d origin = surf + new Vector3d(System.Math.Cos(a), System.Math.Sin(a), 0) * bodyExtent;
        Vector3d dir = (surf - origin).Normalized();
        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        return pipe.Detect(3003, 1, mesh.v, mesh.i, origin, dir, bodyExtent, coarse: false);
    }

    [Fact]
    public void FilletStraight_ReturnsRadius_AxisZ()
    {
        var mesh = CircularMeshFixtures.FilletStraight(
            radius: 2.0, length: 5.0, arcSegments: 16, sweepRadians: System.Math.PI * 0.5);

        CircularDetectionResult r = DetectAtMidArc(mesh, radius: 2.0, bodyExtent: 5.0);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Radius, r.Kind);
        Assert.Equal(2.0, r.Feature!.Radius, 2);
        GeoAssert.AxisParallel(r.Feature.Axis, Vector3d.UnitZ, 0.99);
        // high inlier fraction proves no planar leak diluted the fit.
        Assert.True(r.Confidence > 0.85, $"confidence {r.Confidence} too low (leak?)");
        Assert.Equal(SurfaceKind.Cylinder, r.Feature.Kind);
    }

    [Fact]
    public void FilletStraight_Tight_SmallRadius_StillDetected()
    {
        var mesh = CircularMeshFixtures.FilletStraight(
            radius: 0.01, length: 0.4, arcSegments: 12, sweepRadians: System.Math.PI * 0.5);

        CircularDetectionResult r = DetectAtMidArc(mesh, radius: 0.01, bodyExtent: 0.5);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Radius, r.Kind);
        Assert.Equal(0.01, r.Feature!.Radius, 3);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_FilletStraightTests"`
  Expected red if the grower floods onto the two adjacent planes (low confidence / wrong radius), the classic fillet-miss/leak.

- [ ] **Step 3: Implement** — test-only; exercises Groups D/E/F/H/I.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_FilletStraightTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): straight fillet radius correct, no planar leak

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.4: Torus fillet (curved edge) — tube radius is the fillet radius
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_FilletTorusTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_FilletTorusTests
{
    [Fact]
    public void FilletTorus_ReturnsTubeRadiusAsFilletRadius()
    {
        const double tube = 1.5, ring = 6.0;
        var mesh = CircularMeshFixtures.FilletTorus(
            tubeRadius: tube, ringRadius: ring,
            ringSeg: 48, tubeSeg: 16, sweepRadians: System.Math.PI * 0.5);

        // surface point at ring angle 45°, tube angle 45° (outward-ish).
        double ra = System.Math.PI * 0.25, ta = System.Math.PI * 0.25;
        Vector3d ringCenter = new(System.Math.Cos(ra) * ring, System.Math.Sin(ra) * ring, 0);
        Vector3d outRadial = new(System.Math.Cos(ra), System.Math.Sin(ra), 0);
        Vector3d surf = ringCenter
            + outRadial * (System.Math.Cos(ta) * tube)
            + Vector3d.UnitZ * (System.Math.Sin(ta) * tube);
        Vector3d normalApprox = (surf - ringCenter).Normalized();
        Vector3d origin = surf + normalApprox * (ring);
        Vector3d dir = (surf - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            4004, 1, mesh.v, mesh.i, origin, dir, bodyExtent: (ring + tube) * 2, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Radius, r.Kind);
        Assert.Equal(SurfaceKind.Torus, r.Feature!.Kind);
        Assert.Equal(tube, r.Feature.TubeRadius, 2);   // fillet radius == tube radius
        Assert.Equal(tube, r.Feature.Radius, 2);       // reported R is the tube radius
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_FilletTorusTests"`
  Expected red until Group E torus fit + Group I pipeline branch are present; cylinder-only would mis-report R as the ring radius or diverge.

- [ ] **Step 3: Implement** — test-only; exercises Group E `AnalyticSurfaceFitter.FitTorus`/`FitBest` and the pipeline torus branch.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_FilletTorusTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): torus fillet reports tube radius as fillet radius

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.5: Chamfer — width and angle correct
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_ChamferTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_ChamferTests
{
    [Fact]
    public void Chamfer_45Degree_ReturnsWidthAndAngle()
    {
        const double width = 2.0, angle = 45.0, length = 5.0;
        var mesh = CircularMeshFixtures.Chamfer(width: width, angleDeg: angle, length: length);

        int seedTri = CircularMeshFixtures.SeedTriangleAt(
            mesh, new Vector3d(0, 0, length * 0.5)); // approx mid-band; helper finds nearest
        var topo = new MeshTopologyCache().GetOrBuild(
            5005, 1, mesh.v, mesh.i, CircularDetectionConfig.Default.CreaseAngleRad);

        ChamferFit fit = ChamferDetector.Detect(topo, seedTri);

        Assert.True(fit.Ok, "chamfer not detected");
        Assert.Equal(width, fit.Width, 2);
        Assert.Equal(angle, fit.AngleDeg, 1);
    }

    [Fact]
    public void Chamfer_ThroughPipeline_ClassifiesChamfer()
    {
        const double width = 2.0, angle = 45.0, length = 5.0;
        var mesh = CircularMeshFixtures.Chamfer(width: width, angleDeg: angle, length: length);
        // ray onto the sloped band face, mid-length.
        Vector3d surf = new(0, 0, length * 0.5);
        Vector3d origin = surf + new Vector3d(0, -1, 0) * 10.0 + new Vector3d(1, 0, 0) * 5.0;
        Vector3d dir = (surf - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            5006, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 10.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Chamfer, r.Kind);
        Assert.NotNull(r.Chamfer);
        Assert.Equal(width, r.Chamfer!.Value.Width, 2);
        Assert.Equal(angle, r.Chamfer.Value.AngleDeg, 1);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_ChamferTests"`
  Expected red until Group G `ChamferDetector` + Group I chamfer branch exist.

- [ ] **Step 3: Implement** — test-only; exercises Group G + classifier band branch.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_ChamferTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): chamfer width and angle correct via detector and pipeline

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.6: Closed/open boundary sweep 295°/305°/320°/330° with hysteresis
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_BoundarySweepTests.cs`

- [ ] **Step 1: Write the failing test** — `[Theory]` over coverage degrees crossing `ClosedDeg=300°`. Below 300 → Radius; at/above → Diameter. The hysteresis band (300–330 sticky) is checked by a second case that arrives from the "closed" side and stays Diameter at 305.

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_BoundarySweepTests
{
    private static CircularDetectionResult DetectSweep(double sweepDeg, double radius)
    {
        double sweep = sweepDeg * System.Math.PI / 180.0;
        var mesh = CircularMeshFixtures.Cylinder(
            radius: radius, height: 5.0, segments: 64,
            sweepRadians: sweep, axis: Vector3d.UnitZ);
        // seed mid-sweep on the wall.
        double mid = sweep * 0.5;
        Vector3d surf = new(System.Math.Cos(mid) * radius, System.Math.Sin(mid) * radius, 2.5);
        Vector3d outward = new(System.Math.Cos(mid), System.Math.Sin(mid), 0);
        Vector3d origin = surf + outward * (radius * 4);
        Vector3d dir = (surf - origin).Normalized();
        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        return pipe.Detect(6000 + (long)sweepDeg, 1, mesh.v, mesh.i, origin, dir, 5.0, coarse: false);
    }

    [Theory]
    [InlineData(295.0, CircularKind.Radius)]
    [InlineData(305.0, CircularKind.Diameter)]
    [InlineData(320.0, CircularKind.Diameter)]
    [InlineData(330.0, CircularKind.Diameter)]
    public void CoverageSweep_FlipsAtClosedDeg(double sweepDeg, CircularKind expected)
    {
        CircularDetectionResult r = DetectSweep(sweepDeg, radius: 2.0);

        Assert.True(r.Ok, $"reject {r.Reason} at {sweepDeg} deg");
        Assert.Equal(expected, r.Kind);
        Assert.Equal(2.0, r.Feature!.Radius, 2); // radius is identical regardless of Ø/R label
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_BoundarySweepTests"`
  Expected red if the old `315°` hard cut leaks back in (305 mis-labeled Radius → value halving regression).

- [ ] **Step 3: Implement** — test-only; exercises Group F `FeatureClassifier` `ClosedDeg`/`HysteresisDeg`.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_BoundarySweepTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): 295/305/320/330 boundary sweep flips Ø/R at 300deg with hysteresis

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.7: Leak guard — hole adjacent to a second hole (no merge)
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_LeakGuardTests.cs`

- [ ] **Step 1: Write the failing test** — two parallel holes in one plate; tapping wall A must report A's radius, never a merged/averaged radius spanning both, and the region must not include B's triangles.

```csharp
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_LeakGuardTests
{
    // Build a plate with two holes by concatenating two HoleInPlate meshes offset in X.
    private static (Vector3d[] v, int[] i) TwoHoles(
        double radiusA, double radiusB, double sep)
    {
        var a = CircularMeshFixtures.HoleInPlate(radiusA, plate: 4.0, thickness: 1.0, segments: 48);
        var b = CircularMeshFixtures.HoleInPlate(radiusB, plate: 4.0, thickness: 1.0, segments: 48);
        var shiftedB = b.v.Select(p => new Vector3d(p.X + sep, p.Y, p.Z)).ToArray();
        var v = a.v.Concat(shiftedB).ToArray();
        var i = a.i.Concat(b.i.Select(x => x + a.v.Length)).ToArray();
        return (v, i);
    }

    [Fact]
    public void HoleAdjacentHole_ReportsTappedHoleRadiusOnly()
    {
        var mesh = TwoHoles(radiusA: 0.5, radiusB: 0.8, sep: 2.0);
        Vector3d wallA = new(0.5, 0.0, 0.5); // hole A wall at angle 0
        Vector3d origin = wallA + new Vector3d(1, 0, 0) * 4.0;
        Vector3d dir = (wallA - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            7007, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 4.0 + 2.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.Equal(0.5, r.Feature!.Radius, 3); // NOT 0.8 and NOT some blended value
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_LeakGuardTests"`
  Expected red if the grower crosses the plate face and merges coaxial/adjacent holes (whole-mesh flood regression).

- [ ] **Step 3: Implement** — test-only; exercises Group B crease/boundary barrier + Group D capped grow.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_LeakGuardTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): hole-adjacent-hole reports only the tapped hole

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.8: Leak guards — end-cap, fillet→plane tangent, thin-wall (antiparallel)
**Files:**
- Modify Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_LeakGuardTests.cs`

- [ ] **Step 1: Write the failing tests** — append three guards: (a) tapping a fillet must not include the flat end-cap; (b) confidence stays high when fillet abuts a tangent plane; (c) a thin wall (two antiparallel walls close together) must not merge via `abs()`.

```csharp
    [Fact]
    public void EndCap_TapOnCylinderWall_DoesNotLeakIntoCap()
    {
        // full cylinder; tapping the wall must report the wall radius, axis Z,
        // and stay a clean diameter (cap is a crease-bounded plane, excluded).
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 1.0, height: 3.0, segments: 48, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        Vector3d wall = new(1.0, 0.0, 1.5);
        Vector3d origin = new(5.0, 0.0, 1.5);
        Vector3d dir = new Vector3d(-1, 0, 0).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(7100, 1, mesh.v, mesh.i, origin, dir, 3.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.Equal(1.0, r.Feature!.Radius, 2);
        Assert.True(r.Confidence > 0.85, $"cap leak suspected, conf {r.Confidence}");
    }

    [Fact]
    public void FilletTangentToPlane_NoLeak_HighConfidence()
    {
        var mesh = CircularMeshFixtures.FilletStraight(
            radius: 1.0, length: 4.0, arcSegments: 20, sweepRadians: System.Math.PI * 0.5);
        double a = System.Math.PI * 0.25;
        Vector3d surf = new(System.Math.Cos(a), System.Math.Sin(a), 2.0);
        Vector3d origin = surf + new Vector3d(System.Math.Cos(a), System.Math.Sin(a), 0) * 4.0;
        Vector3d dir = (surf - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(7101, 1, mesh.v, mesh.i, origin, dir, 4.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Radius, r.Kind);
        Assert.Equal(1.0, r.Feature!.Radius, 2);
        Assert.True(r.Confidence > 0.85);
    }

    [Fact]
    public void ThinWall_AntiparallelFaces_DoNotMerge()
    {
        // two close walls with opposite normals: a thin plate around a hole.
        // Tapping the inner bore must report the bore radius, not a merge of
        // bore+outer (which abs(dot) would have allowed).
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 0.5, plate: 1.1 /* thin: outer wall ~0.55 from axis */, thickness: 1.0, segments: 48);
        Vector3d bore = new(0.5, 0.0, 0.5);
        Vector3d origin = bore + new Vector3d(1, 0, 0) * 4.0;
        Vector3d dir = (bore - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(7102, 1, mesh.v, mesh.i, origin, dir, 1.1, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(0.5, r.Feature!.Radius, 3); // bore radius, NOT outer/merged
    }
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_LeakGuardTests"`
  Expected red if `abs()` merge or no crease barrier reappears (thin-wall merges; cap/plane bleed lowers confidence).

- [ ] **Step 3: Implement** — test-only; exercises Groups B, D, H signed `+cos` same-facing + crease wall.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_LeakGuardTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): end-cap, fillet-tangent, thin-wall leak guards

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.9: Negatives — cone, sphere, flat quad, noise reject with reason
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_NegativeTests.cs`

- [ ] **Step 1: Write the failing test** — each non-circular/degenerate surface must return `Ok == false` with a meaningful `RejectReason` (never a silent garbage commit).

```csharp
using System;
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_NegativeTests
{
    private static (Vector3d[] v, int[] i) Cone(double baseR, double height, int seg)
    {
        var v = new System.Collections.Generic.List<Vector3d>();
        var idx = new System.Collections.Generic.List<int>();
        int apex = 0; v.Add(new Vector3d(0, 0, height));
        for (int s = 0; s < seg; s++)
        {
            double t = s * System.Math.Tau / seg;
            v.Add(new Vector3d(System.Math.Cos(t) * baseR, System.Math.Sin(t) * baseR, 0));
        }
        for (int s = 0; s < seg; s++)
        { idx.Add(apex); idx.Add(1 + s); idx.Add(1 + (s + 1) % seg); }
        return (v.ToArray(), idx.ToArray());
    }

    private static (Vector3d[] v, int[] i) Sphere(double r, int lat, int lon)
    {
        var v = new System.Collections.Generic.List<Vector3d>();
        var idx = new System.Collections.Generic.List<int>();
        for (int a = 0; a <= lat; a++)
        {
            double phi = System.Math.PI * a / lat;
            for (int b = 0; b <= lon; b++)
            {
                double th = System.Math.Tau * b / lon;
                v.Add(new Vector3d(r * System.Math.Sin(phi) * System.Math.Cos(th),
                                   r * System.Math.Sin(phi) * System.Math.Sin(th),
                                   r * System.Math.Cos(phi)));
            }
        }
        int stride = lon + 1;
        for (int a = 0; a < lat; a++)
        for (int b = 0; b < lon; b++)
        {
            int p = a * stride + b;
            idx.Add(p); idx.Add(p + 1); idx.Add(p + stride);
            idx.Add(p + 1); idx.Add(p + stride + 1); idx.Add(p + stride);
        }
        return (v.ToArray(), idx.ToArray());
    }

    private static CircularDetectionResult DetectInto(
        (Vector3d[] v, int[] i) mesh, Vector3d surf, Vector3d outward, double extent, long key)
    {
        Vector3d origin = surf + outward.Normalized() * (extent * 2);
        Vector3d dir = (surf - origin).Normalized();
        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        return pipe.Detect(key, 1, mesh.v, mesh.i, origin, dir, extent, coarse: false);
    }

    [Fact]
    public void FlatQuad_Rejected_NoFeatureHere()
    {
        var mesh = CircularMeshFixtures.FlatPlate(size: 4.0);
        CircularDetectionResult r = DetectInto(mesh, new Vector3d(1, 1, 0), Vector3d.UnitZ, 4.0, 8001);
        Assert.False(r.Ok);
        Assert.Equal(RejectReason.NoFeatureHere, r.Reason);
    }

    [Fact]
    public void Cone_Rejected_NotCircularDiameter()
    {
        var mesh = Cone(baseR: 2.0, height: 4.0, seg: 48);
        CircularDetectionResult r = DetectInto(
            mesh, new Vector3d(1.0, 0.0, 2.0), new Vector3d(1, 0, 0.5), 4.0, 8002);
        Assert.False(r.Ok); // cone radius varies along axis -> low confidence / not accepted
        Assert.NotEqual(RejectReason.None, r.Reason);
    }

    [Fact]
    public void Sphere_Rejected()
    {
        var mesh = Sphere(r: 2.0, lat: 32, lon: 48);
        CircularDetectionResult r = DetectInto(
            mesh, new Vector3d(2.0, 0.0, 0.0), Vector3d.UnitX, 2.0, 8003);
        Assert.False(r.Ok);
        Assert.NotEqual(RejectReason.None, r.Reason);
    }

    [Fact]
    public void NoisyCylinder_BeyondRmsBudget_Rejected()
    {
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 48, sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        var rnd = new Random(12345);
        // perturb each vertex radially by ±0.4*R (6% RMS/R budget is 1.5%, so reject)
        var noisy = mesh.v.Select(p =>
        {
            Vector3d radial = new Vector3d(p.X, p.Y, 0);
            double len = radial.Length();
            if (len < 1e-9) return p;
            double scale = 1.0 + (rnd.NextDouble() - 0.5) * 0.8;
            Vector3d rr = radial.Normalized() * (len * scale);
            return new Vector3d(rr.X, rr.Y, p.Z);
        }).ToArray();
        CircularDetectionResult r = DetectInto(
            (noisy, mesh.i), new Vector3d(2.0, 0.0, 2.5), Vector3d.UnitX, 5.0, 8004);
        Assert.False(r.Ok);
        Assert.Equal(RejectReason.LowConfidence, r.Reason);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_NegativeTests"`
  Expected red if the acceptance gate (Group H) silently commits a low-confidence/cone/sphere fit (the garbage-commit regression).

- [ ] **Step 3: Implement** — test-only; exercises Group H `FitAcceptanceGate` + Group I reason mapping.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_NegativeTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): negatives (cone/sphere/flat/noise) reject with reason

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.10: Tessellation-density invariance and seed-independence
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_InvarianceTests.cs`

- [ ] **Step 1: Write the failing test** — (a) same hole at segments 16/32/64/128 → same radius; (b) tapping different triangles around the same wall → identical radius/axis.

```csharp
using System.Linq;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_InvarianceTests
{
    private static CircularDetectionResult DetectWall(
        (Vector3d[] v, int[] i) mesh, double angle, double radius, double extent, long key)
    {
        Vector3d surf = new(System.Math.Cos(angle) * radius, System.Math.Sin(angle) * radius, extent * 0.5);
        Vector3d outward = new(System.Math.Cos(angle), System.Math.Sin(angle), 0);
        Vector3d origin = surf + outward * (extent * 2);
        Vector3d dir = (surf - origin).Normalized();
        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        return pipe.Detect(key, 1, mesh.v, mesh.i, origin, dir, extent, coarse: false);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void Boss_RadiusInvariantAcrossTessellation(int segments)
    {
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: segments,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        CircularDetectionResult r = DetectWall(mesh, angle: 0, radius: 2.0, extent: 5.0, key: 9000 + segments);
        Assert.True(r.Ok, $"reject {r.Reason} at seg {segments}");
        Assert.Equal(2.0, r.Feature!.Radius, 2);
    }

    [Fact]
    public void Boss_SeedIndependent_SameRadiusEverywhere()
    {
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 2.0, height: 5.0, segments: 64,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);
        double[] angles = { 0.0, System.Math.PI * 0.5, System.Math.PI, System.Math.PI * 1.5 };
        var radii = angles.Select((a, k) =>
        {
            var r = DetectWall(mesh, a, radius: 2.0, extent: 5.0, key: 9500 + k);
            Assert.True(r.Ok, $"reject {r.Reason} at angle {a}");
            return r.Feature!.Radius;
        }).ToArray();

        foreach (double rad in radii)
            Assert.Equal(2.0, rad, 2);
        Assert.True(radii.Max() - radii.Min() < 1e-3, "seed-dependent radius drift");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_InvarianceTests"`
  Expected red if sceneDiagonal-scaled tolerances or seed-dependent axis drift reappear.

- [ ] **Step 3: Implement** — test-only; exercises Groups C/D/E/H scale-invariant tolerances.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_InvarianceTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): tessellation-density and seed-independence invariance

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.11: End-to-end committed value in millimetres
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_EndToEndTests.cs`

- [ ] **Step 1: Write the failing test** — drive the pipeline result into `CircularFeaturePick` and assert the diameter value (2·R) in mm, and that a reject produces no committed value. (Mesh units are mm in this project; the assertion is on the numeric diameter.)

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_EndToEndTests
{
    [Fact]
    public void Hole_PickToCommit_DiameterValueMm()
    {
        // radius 2.5 mm hole -> Ø 5.0 mm.
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 2.5, plate: 20.0, thickness: 5.0, segments: 64);
        Vector3d wall = new(2.5, 0.0, 2.5);
        Vector3d origin = wall + new Vector3d(1, 0, 0) * 20.0;
        Vector3d dir = (wall - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            10001, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 20.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.NotNull(r.Feature);
        double diameterMm = 2.0 * r.Feature!.Radius;
        Assert.Equal(5.0, diameterMm, 2);
        Assert.True(r.Feature.GeometricRms / r.Feature.Radius < 0.015, "RMS/R over budget");
    }

    [Fact]
    public void Flat_Pick_NoCommittedValue()
    {
        var mesh = CircularMeshFixtures.FlatPlate(size: 10.0);
        Vector3d surf = new(2, 2, 0);
        Vector3d origin = surf + Vector3d.UnitZ * 20.0;
        Vector3d dir = (surf - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            10002, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 10.0, coarse: false);

        Assert.False(r.Ok);
        Assert.Null(r.Feature);
        Assert.Equal(RejectReason.NoFeatureHere, r.Reason);
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_EndToEndTests"`

- [ ] **Step 3: Implement** — test-only; exercises Group I full pipeline including CommitGuard value recompute.

- [ ] **Step 4: Run it, expect PASS** — same command.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_EndToEndTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): end-to-end committed diameter value in mm; reject => no value

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.12: Perf guard — coarse hover Detect under time budget on a big fixture
**Files:**
- Test (PARENT): `src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_PerfTests.cs`

- [ ] **Step 1: Write the failing test** — build a ~100k-triangle cylinder, warm the topology cache once, then assert a `coarse:true` hover `Detect` completes well under budget. (Host budget is conservative; device budget is 100 ms per spec §2/§10 — host CI uses 150 ms to absorb runner jitter while still catching per-hover full-rebuild regressions, which are 10–100×.)

```csharp
using System.Diagnostics;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FabricationAssistant.App.Tests.Measurement;

public sealed class CircularFeatureMatrix_PerfTests
{
    private readonly ITestOutputHelper _out;
    public CircularFeatureMatrix_PerfTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void CoarseHover_UnderBudget_OnBigMesh()
    {
        // ~100k tris: a full cylinder with ~50k segments => 50k*2 tris.
        var mesh = CircularMeshFixtures.Cylinder(
            radius: 5.0, height: 20.0, segments: 50_000,
            sweepRadians: System.Math.Tau, axis: Vector3d.UnitZ);

        var cache = new MeshTopologyCache(capacity: 4);
        var cfg = CircularDetectionConfig.Default;
        var pipe = new CircularFeatureDetectionPipeline(cache, cfg);

        Vector3d wall = new(5.0, 0.0, 10.0);
        Vector3d origin = wall + new Vector3d(1, 0, 0) * 40.0;
        Vector3d dir = (wall - origin).Normalized();

        // Warmup: builds + caches topology (off the hot path on device).
        _ = pipe.Detect(20001, 1, mesh.v, mesh.i, origin, dir, 20.0, coarse: true);

        // Measure the steady-state cached hover (cache hit -> no rebuild).
        var sw = Stopwatch.StartNew();
        const int iterations = 20;
        for (int k = 0; k < iterations; k++)
            _ = pipe.Detect(20001, 1, mesh.v, mesh.i, origin, dir, 20.0, coarse: true);
        sw.Stop();

        double perHoverMs = sw.Elapsed.TotalMilliseconds / iterations;
        _out.WriteLine($"coarse hover (cached): {perHoverMs:F2} ms/hover over {iterations} iters");
        Assert.True(perHoverMs < 150.0,
            $"coarse hover {perHoverMs:F2} ms exceeds 150 ms host budget (per-hover rebuild regression?)");
    }
}
```

- [ ] **Step 2: Run it, expect FAIL**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrix_PerfTests"`
  Expected red if the topology is rebuilt every `Detect` (cache miss on each call) — the per-hover-rebuild regression — pushing per-hover into the hundreds of ms.

- [ ] **Step 3: Implement** — test-only; exercises Group B `MeshTopologyCache.GetOrBuild` cache-hit on stable `(meshKey, transformVersion)` and Group I coarse path that fits cylinder-only quick.

- [ ] **Step 4: Run it, expect PASS** — same command. If genuinely over budget (not a logic bug), record actual ms in the test output and confirm the cache-hit path is taken (no rebuild) before relaxing — do not relax silently.

- [ ] **Step 5: Commit (PARENT repo)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" add src/FabricationAssistant.App.Tests/Measurement/CircularFeatureMatrix_PerfTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant" commit -m "test(measure-circular): coarse hover perf guard under budget on ~100k-tri mesh

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.13: Android suite mirror — link Core types + mirror the core detection tests
**Files:**
- Modify (ANDROID): `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Test (ANDROID): `Android/src/FabricationAssistant.App.Android.Tests/CircularFeatureMatrixMirrorTests.cs`

> **Selective-link note.** The Android test project (`net8.0`) only links a subset of Core. The new engine types (`MeshTopologyCache`, `MeshTopology`, `SeedResolver`, `SurfaceRegionGrower`, `AnalyticSurfaceFitter`, `EdgeLoopFitter`, `FeatureClassifier`, `ChamferDetector`, `FitAcceptanceGate`, `CircularFeatureDetectionPipeline`, `CircularDetectionConfig`/`Result`, and the new enums/records) are NOT yet in the csproj — and `CircularMeshFixtures`/`GeoAssert` live in the **host** test project, which is not referenced. We therefore (a) add `<Compile Include>` entries for the new Core engine files, and (b) **link the host fixtures file** by relative path so the Android mirror reuses identical fixtures. The new Core files compile-link into Android *app* automatically via the wildcard, but the *test* project's selective list must be extended explicitly.

- [ ] **Step 1: Add the selective-link entries.** Edit the csproj `<ItemGroup>` that already links `Measurement\Engine` (after the existing `CircularFeatureDetectionService.cs` include) to add the new engine + fixtures files. Insert:

```xml
    <!-- Circular-dimension redesign engine (Group B..I) for Android mirror tests -->
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopology.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshTopologyCache.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\SeedResolver.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\SurfaceRegionGrower.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\AnalyticSurfaceFitter.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\EdgeLoopFitter.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FeatureClassifier.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\ChamferDetector.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FitAcceptanceGate.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\CircularFeatureDetectionPipeline.cs"
             LinkBase="Linked\Measurement\Engine" />
    <!-- Shared host fixtures so the Android mirror uses identical meshes -->
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.App.Tests\Measurement\CircularMeshFixtures.cs"
             LinkBase="Linked\TestFixtures" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.App.Tests\Measurement\GeoAssert.cs"
             LinkBase="Linked\TestFixtures" />
```

  > If any of these engine files were split across additional `.cs` (e.g. enums in a separate `CircularDetectionContracts.cs`), add a `<Compile Include>` for each in the same block. Do not use a wildcard here — the existing project links files individually to keep the host TFM clean. If a linked file pulls a not-yet-linked dependency, the build error names the missing type; add that file too.

- [ ] **Step 2: Write the mirror test (run it, expect FAIL first).** Create the mirror in the Android suite namespace. It re-uses the linked `CircularMeshFixtures` (which are `internal` to `FabricationAssistant.App.Tests.Measurement`; the linked compile unit carries that namespace into this assembly, so reference them by full namespace).

```csharp
using FabricationAssistant.App.Tests.Measurement; // linked fixtures
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class CircularFeatureMatrixMirrorTests
{
    [Fact]
    public void Hole_InwardNormals_ClassifiesDiameter_AndroidMirror()
    {
        var mesh = CircularMeshFixtures.HoleInPlate(
            radius: 0.5, plate: 4.0, thickness: 1.0, segments: 48);
        Vector3d wall = new(0.5, 0.0, 0.5);
        Vector3d origin = wall + new Vector3d(1, 0, 0) * 4.0;
        Vector3d dir = (wall - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            30001, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 4.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Diameter, r.Kind);
        Assert.Equal(0.5, r.Feature!.Radius, 3);
        GeoAssert.AxisParallel(r.Feature.Axis, Vector3d.UnitZ, 0.99);
    }

    [Fact]
    public void FilletStraight_ReturnsRadius_AndroidMirror()
    {
        var mesh = CircularMeshFixtures.FilletStraight(
            radius: 2.0, length: 5.0, arcSegments: 16, sweepRadians: System.Math.PI * 0.5);
        double a = System.Math.PI * 0.25;
        Vector3d surf = new(System.Math.Cos(a) * 2.0, System.Math.Sin(a) * 2.0, 2.5);
        Vector3d origin = surf + new Vector3d(System.Math.Cos(a), System.Math.Sin(a), 0) * 5.0;
        Vector3d dir = (surf - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            30002, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 5.0, coarse: false);

        Assert.True(r.Ok, $"reject {r.Reason}");
        Assert.Equal(CircularKind.Radius, r.Kind);
        Assert.Equal(2.0, r.Feature!.Radius, 2);
    }

    [Fact]
    public void FlatPlate_Rejected_AndroidMirror()
    {
        var mesh = CircularMeshFixtures.FlatPlate(size: 4.0);
        Vector3d surf = new(1, 1, 0);
        Vector3d origin = surf + Vector3d.UnitZ * 8.0;
        Vector3d dir = (surf - origin).Normalized();

        var pipe = new CircularFeatureDetectionPipeline(
            new MeshTopologyCache(), CircularDetectionConfig.Default);
        CircularDetectionResult r = pipe.Detect(
            30003, 1, mesh.v, mesh.i, origin, dir, bodyExtent: 4.0, coarse: false);

        Assert.False(r.Ok);
        Assert.Equal(RejectReason.NoFeatureHere, r.Reason);
    }
}
```

  > **Internal-access note.** `CircularMeshFixtures`/`GeoAssert` are `internal`. Because they are *compile-linked* into this assembly (not referenced via DLL), `internal` is visible here — no `InternalsVisibleTo` needed. If a future refactor makes them a `ProjectReference` instead, add `[assembly: InternalsVisibleTo("FabricationAssistant.App.Android.Tests")]` to the host test project.

- [ ] **Step 2b: Run it, expect FAIL**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrixMirrorTests"`
  Expected failure first run: **compile errors** for unresolved engine types until Step 1 links them; once linked, the tests should pass (they mirror already-green host behavior). If a linked Core file references a Core type not in the list, the compiler names it — add that `<Compile Include>` and retry.

- [ ] **Step 3: Implement** — no new production code. "Implementation" = completing the csproj link set (Step 1) until the Android suite compiles and the three mirror tests run.

- [ ] **Step 4: Run it, expect PASS**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "FullyQualifiedName~CircularFeatureMatrixMirrorTests"`
  Then run the **full** Android suite to confirm no link regression: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`

- [ ] **Step 5: Commit (ANDROID repo, explicit paths)**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/CircularFeatureMatrixMirrorTests.cs
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m "test(measure-circular): Android-suite mirror of circular detection matrix + selective Core link

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

---

### Task L.14: Full matrix green-run gate (host + Android) before device re-validation
**Files:**
- No new files — a verification gate that locks the suite green prior to on-device work.

- [ ] **Step 1: Run the full host CircularFeature matrix**
  `dotnet test src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj --filter "FullyQualifiedName~CircularFeature"`
  Expected: all `CircularFeatureMatrix_*` + legacy `CircularFeatureDetectionServiceTests` green. Record the pass count.

- [ ] **Step 2: Run the full Android suite**
  `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
  Expected: all green including `CircularFeatureMatrixMirrorTests` and the pre-existing 210 tests.

- [ ] **Step 3: If anything is red** — apply `superpowers:systematic-debugging`: reproduce the single failing test in isolation, fix the **root cause** in the owning group's component (not the test), re-run. Do not weaken an assertion to make it pass. (No code shown — fixes belong to Groups B–J.)

- [ ] **Step 4: Confirm both suites green** — re-run Steps 1 and 2; both must report 0 failed before proceeding to L.15.

- [ ] **Step 5: Commit** — nothing to commit (verification-only). If a root-cause fix was made in a Group B–J file, commit it in the **PARENT** repo (Core) or **ANDROID** repo (renderer/app glue) by explicit path with message `fix(measure-circular): <root cause> surfaced by matrix` and the standard `Co-Authored-By` trailer.

---

### Task L.15: Device re-validation — confirm the three original failing taps are fixed
**Files:**
- Test artifact (ANDROID): `Android/docs/device-validation/2026-06-06-circular-revalidation.md` (evidence log; not a `.cs` test)

> **Goal.** Reproduce the three real-device failures from spec §1 (fillet mis-fit, fillet MISS, fillet false-success) on the Samsung `R52Y80CE37L` and confirm each now yields a correct/stable R or an explicit reject — never a wrong silent value. Evidence is captured from the structured tag `FA.MeasureCircular`.

- [ ] **Step 1: Build, package, and install the debug APK.** From the PARENT root (Android packaging lives in the Android app project):
  dotnet build Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj -t:SignAndroidPackage -c Debug
  adb devices
  adb install -r Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android/com.fabricationassistant.app-Signed.apk
  (If a build-lock CS2012/XARLP7024 appears, run `dotnet build-server shutdown` and retry per the preamble.)

- [ ] **Step 2: Start a clean logcat capture filtered to the tool tag.** In a background terminal:
  adb logcat -c
  adb logcat -s FA.MeasureCircular
  Keep this stream visible/recorded to a file: `adb logcat -s FA.MeasureCircular > Android/docs/device-validation/2026-06-06-revalidation.log`

- [ ] **Step 3: Reproduce the three taps + the control.** Load the same model used in §1. Enable the CircularFeature (diameter/arc) tool. Perform, in order, capturing the resulting log block for each:
  - **Tap 1** — fillet that previously committed `R=0.0421` with 4 disagreeing fitters (was mesh 551 / tri ~214). **Expected now:** a single accepted R with `rmsOverR < 0.015` and converged fitters (no `refinedCircleFitDiverged`), OR an explicit reject — not a silently committed disputed value.
  - **Tap 2** — fillet that previously MISSED (null; flooded `smooth=446/775`, was mesh 430 / tri ~345). **Expected now:** a detected, accepted R (region no longer floods across creases), value stable when re-tapped a few px away.
  - **Tap 3** — fillet that previously FALSE-succeeded at `R=0.004` on a corner vertex-fan (was mesh 551 / tri ~401). **Expected now:** the correct fillet R (matching Tap 1's feature if same fillet) or an explicit reject — never the `0.004` garbage.
  - **Control** — the known-good hole (was mesh 349 / tri ~229, Ø R≈0.0022, `coverageDeg≈345`). **Expected:** unchanged clean Ø (no regression).

- [ ] **Step 4: Verify the log evidence and record results.** For each tap, confirm in the `FA.MeasureCircular` output:
  - no `refinedCircleFitDiverged`, no `strictTrimTooSmallAfterRobustFit`, no `patchDoesNotContainOriginalSeed`, no `noCircularPatch` on the two taps that should now succeed;
  - the committed/previewed `R` is consistent across ≥3 nearby taps on the same fillet (seed-independence on the real mesh);
  - any reject carries `NoFeatureHere`/`LowConfidence` (explicit), never a silent commit of a disputed value;
  - the control hole still logs a high `coverageDeg` and tiny `rms`.
  Write the per-tap outcome (PASS/REJECT-as-expected/FAIL), the observed `R`, `rmsOverR`, and the decisive log lines into `Android/docs/device-validation/2026-06-06-circular-revalidation.md`. If any tap still produces a wrong silent value, treat it as an open defect: route back to the owning group via `superpowers:systematic-debugging` (do not declare success).

- [ ] **Step 5: Commit the evidence (ANDROID repo, explicit paths).**
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" add docs/device-validation/2026-06-06-circular-revalidation.md
  git -C "C:/Users/skritikos/Desktop/Fabrication Assistant/Android" commit -m "docs(measure-circular): device re-validation of the three failing taps (FA.MeasureCircular)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"

The Group L task section above is complete and self-contained. Key reference paths confirmed during authoring:
- Host test style/helper source: `C:/Users/skritikos/Desktop/Fabrication Assistant/src/FabricationAssistant.App.Tests/Measurement/CircularFeatureDetectionServiceTests.cs` (xUnit `Assert.Equal(expected, actual, precision)`, fixture-builder pattern).
- Android selective-link csproj to extend in L.13: `C:/Users/skritikos/Desktop/Fabrication Assistant/Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (individual `<Compile Include>` entries with `LinkBase`, no wildcard; existing `Measurement\Engine` block is where the new entries slot in).
- Spec §10 (test matrix), §11 (traceability), §14 step 5 (device re-validation) drove L.1–L.15.
