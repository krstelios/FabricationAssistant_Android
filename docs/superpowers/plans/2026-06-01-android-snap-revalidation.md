# Android Snapping Re-validation & Marker/Selection Consistency — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the red snap marker the single source of truth for measurement point selection (hover preview == committed point), re-validate every brief §11/§12 scenario as an acceptance contract, and guard it with automated + on-device tests — with no rewrite of the already-mature snap engine.

**Architecture:** Unify the hover and click point-resolution in the shared `MeshMeasurePicker` behind one `ResolveSnapPoint` method, gated by a new `PreparedOnlySelection` flag that Android sets `true` (desktop keeps its current build-on-click behaviour). Eliminate a per-hover allocation in `EdgeSnapService`. Add host-runnable tests (the net8.0 test project already compile-links `EdgeSnapService`, `AndroidSectionClipper`, `SectionPlane`, and Math) plus a documented on-device checklist for the parts only an ARM device can exercise (BVH occlusion, hidden/isolated bodies, section-face raycast, real frame rate).

**Tech Stack:** C# / .NET 8, xUnit, the Android measurement engine (`FabricationAssistant.Core.Measurement.Engine`), `dotnet test` on the dev box (no emulator).

**Spec:** `docs/superpowers/specs/2026-06-01-android-snap-revalidation-design.md`

**Key file locations (verified):**
- Android-local engine: `Android/src/FabricationAssistant.Core.Android/Measurement/Engine/EdgeSnapService.cs`
- Shared picker (desktop repo, compile-linked into Android): `../src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs`
- Android wiring: `Android/src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs`
- Tests: `Android/src/FabricationAssistant.App.Android.Tests/` (csproj at `.../FabricationAssistant.App.Android.Tests.csproj`)

> Paths below are relative to the Android repo root `C:\Users\skritikos\Desktop\Fabrication Assistant\Android` unless prefixed `../` (parent desktop repo). All `dotnet`/`git` commands run from the Android repo root.

> **Two-repo topology (verified):** the Android tree is its own git repo (`.../Android`); the shared Core lives in a SEPARATE parent repo (`.../Fabrication Assistant`). `MeshMeasurePicker.cs` is in the **parent** repo and is OUTSIDE the Android repo's working tree — it must be committed there with `git -C ".."`. The Android build compile-links the file from disk, so edits take effect for `dotnet test` regardless of commit; the commit is for version control. All other files in this plan are in the Android repo. Memory/spec/plan docs are in the Android repo.

---

## Task 0: Feature branch

- [ ] **Step 1: Branch the Android repo off master**

The Android repo default branch is `master`; branch before committing.

Run:
```bash
git checkout -b feature/android-snap-revalidation
```
Expected: `Switched to a new branch 'feature/android-snap-revalidation'`

- [ ] **Step 2: Branch the parent desktop repo (it holds `MeshMeasurePicker.cs`)**

The Task 3 picker change is committed in the parent repo. Branch it too (only if the
parent repo is on its default branch / clean enough — check first).

Run:
```bash
git -C ".." status --short
git -C ".." checkout -b feature/android-snap-revalidation
```
Expected: a new branch in the parent repo. If the parent repo has unrelated in-progress
work, instead stay on its current branch and commit the single picker file there at Task 3.

---

## Task 1: Link `MeshMeasurePicker` into the test host + add a fake raycaster

**Files:**
- Modify: `src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Create: `src/FabricationAssistant.App.Android.Tests/FakeMeasureRaycaster.cs`
- Create: `src/FabricationAssistant.App.Android.Tests/MeshMeasurePickerLinkTests.cs`

- [ ] **Step 1: Add the picker + its host-safe deps to the test compile set**

In the test csproj, inside the existing `<ItemGroup>` that links Core Measurement sources (the one containing `Measurement\Domain\*.cs`, around line 48), add these `<Compile Include>` entries (all verified to use only Core.Math / Domain / Presentation / SceneGraph-Math — no Android/Silk/Windows deps):

```xml
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeshMeasurePicker.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeasureTool.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeasurementSession.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\MeasurementStore.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\IMeasurementStore.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\IMeasureRaycaster.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\PickTypes.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FaceDetectionService.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FacePatch.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\FeatureEdgeExtractor.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\IUnitSystemService.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Engine\SceneUnitSystemService.cs"
             LinkBase="Linked\Measurement\Engine" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Presentation\Primitives.cs"
             LinkBase="Linked\Measurement\Presentation" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Presentation\PresentationSnapshot.cs"
             LinkBase="Linked\Measurement\Presentation" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Measurement\Presentation\MeasurementPresenter.cs"
             LinkBase="Linked\Measurement\Presentation" />
    <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\SceneGraph\SceneNode.cs"
             LinkBase="Linked\SceneGraph" />
```

> Note: if the build reports a missing type from a transitive Core file, link that one file the same way (it will be Math/Domain-only). Do not pull in `FabricationAssistant.Rendering.*` or any `*.Android` project.

- [ ] **Step 2: Create the fake raycaster test helper**

Create `src/FabricationAssistant.App.Android.Tests/FakeMeasureRaycaster.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.App.Android.Tests;

/// Minimal in-memory raycaster for picker tests. Returns a single configured
/// surface hit and a single mesh (identity transform). Optionally answers the
/// supplemental snap provider with a fixed point.
internal sealed class FakeMeasureRaycaster : IMeasureRaycaster, IMeasureSupplementalPointSnapProvider
{
    private readonly MeshDto? _mesh;
    private readonly MeasureRaycastHit? _hit;
    private readonly Vector3d? _supplementalPoint;

    public FakeMeasureRaycaster(
        MeshDto? mesh,
        MeasureRaycastHit? hit,
        Vector3d? supplementalPoint = null,
        double sceneDiagonal = 10.0)
    {
        _mesh = mesh;
        _hit = hit;
        _supplementalPoint = supplementalPoint;
        SceneDiagonal = sceneDiagonal;
    }

    public double SceneDiagonal { get; }

    public MeasureRaycastHit? Raycast(Vector3d origin, Vector3d direction) => _hit;

    public MeshDto? GetMesh(int meshId) => _mesh;

    // No SceneNode in unit tests -> point picks get no attachment, which is fine.
    public SceneNode? GetNode(int nodeId) => null;

    public Matrix4d GetWorldTransform(int nodeId) => Matrix4d.Identity;

    public bool TrySnapPoint(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        double edgeAngularTolerance,
        double endpointAngularTolerance,
        out Vector3d worldPoint,
        bool allowBuild = true)
    {
        if (_supplementalPoint is { } p)
        {
            worldPoint = p;
            return true;
        }

        worldPoint = default;
        return false;
    }
}
```

- [ ] **Step 3: Add a trivial construction test that forces the link to compile**

Create `src/FabricationAssistant.App.Android.Tests/MeshMeasurePickerLinkTests.cs`:

```csharp
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
```

- [ ] **Step 4: Build + run to verify the link compiles and the new test passes**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~MeshMeasurePickerLinkTests" -c Release --nologo
```
Expected: `Passed!  - Failed: 0, Passed: 1`. If a missing-type build error appears, link that single Core file per the Step 1 note and re-run.

- [ ] **Step 5: Commit**

```bash
git add src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj src/FabricationAssistant.App.Android.Tests/FakeMeasureRaycaster.cs src/FabricationAssistant.App.Android.Tests/MeshMeasurePickerLinkTests.cs
git commit -m "test: link MeshMeasurePicker into net8.0 test host with a fake raycaster"
```

---

## Task 2: Gap A — failing marker/selection consistency tests

**Files:**
- Create: `src/FabricationAssistant.App.Android.Tests/MeshMeasurePickerConsistencyTests.cs`

These tests pin the contract: with `PreparedOnlySelection = true`, hover and click resolve to the **same** point (or both miss). They reference the new `PreparedOnlySelection` property, which does not exist yet — so they fail to compile/pass until Task 3.

- [ ] **Step 1: Write the failing tests**

Create `src/FabricationAssistant.App.Android.Tests/MeshMeasurePickerConsistencyTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Engine;
using FabricationAssistant.Core.SceneGraph;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class MeshMeasurePickerConsistencyTests
{
    private const double EdgeTol = 0.022;
    private const double EndpointTol = 0.0085;

    public MeshMeasurePickerConsistencyTests()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;
    }

    // A straight edge from (0,0,0) to (1,0,0); ray points down at its midpoint.
    private static float[] StraightEdge() => new float[] { 0, 0, 0, 1, 0, 0 };
    private static readonly Vector3d MidRayOrigin = new(0.5, 0, 1);
    private static readonly Vector3d DownRay = new(0, 0, -1);

    private static MeshDto MeshWith(float[] edgePositions) => new()
    {
        MeshId = 1,
        Positions = Array.Empty<float>(),
        Indices = Array.Empty<int>(),
        EdgePositions = edgePositions,
        Bounds = BoundingBox.Empty,
        TriangleCount = 0,
    };

    [Fact]
    public void PreparedOnly_WhenNotWarmed_HoverAndPickBothMiss()
    {
        float[] edges = StraightEdge();
        var mesh = MeshWith(edges);
        var hit = new MeasureRaycastHit(NodeId: 1, MeshId: 1, TriangleIndexOffset: 0, WorldPoint: new Vector3d(0.5, 0, 0));
        var raycaster = new FakeMeasureRaycaster(mesh, hit);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.Null(hover);                       // no red marker
        Assert.IsType<PickResult.Miss>(pick);     // ...and no pick
    }

    [Fact]
    public void PreparedOnly_WhenWarmed_HoverAndPickReturnSamePoint()
    {
        float[] edges = StraightEdge();
        new EdgeSnapService().Prepare(edges);     // warm the static model cache for THIS array
        var mesh = MeshWith(edges);
        var hit = new MeasureRaycastHit(1, 1, 0, new Vector3d(0.5, 0, 0));
        var raycaster = new FakeMeasureRaycaster(mesh, hit);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.NotNull(hover);
        var picked = Assert.IsType<PickResult.PickedPoint>(pick);
        Assert.Equal(hover!.Value.X, picked.Point.World.X, 9);
        Assert.Equal(hover!.Value.Y, picked.Point.World.Y, 9);
        Assert.Equal(hover!.Value.Z, picked.Point.World.Z, 9);
    }

    [Fact]
    public void PreparedOnly_SupplementalSnap_IsPreviewedAndCommittedIdentically()
    {
        // Mesh has no edges -> primary snap misses -> supplemental provides the point.
        var mesh = MeshWith(Array.Empty<float>());
        var hit = new MeasureRaycastHit(1, 1, 0, new Vector3d(2, 2, 0));
        var supplemental = new Vector3d(3, 3, 3);
        var raycaster = new FakeMeasureRaycaster(mesh, hit, supplementalPoint: supplemental);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol)
        {
            PreparedOnlySelection = true,
        };

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.Equal(supplemental, hover);
        var picked = Assert.IsType<PickResult.PickedPoint>(pick);
        Assert.Equal(supplemental, picked.Point.World);
    }

    [Fact]
    public void DesktopMode_PickStillBuildsOnDemand_WhileHoverStaysPreparedOnly()
    {
        // PreparedOnlySelection = false (desktop default): pick may build (allowBuild
        // true) and snap even when not warmed; hover stays prepared-only. This locks in
        // that the desktop behaviour is unchanged by the flag.
        float[] edges = StraightEdge();
        var mesh = MeshWith(edges);
        var hit = new MeasureRaycastHit(1, 1, 0, new Vector3d(0.5, 0, 0));
        var raycaster = new FakeMeasureRaycaster(mesh, hit);
        var picker = new MeshMeasurePicker(raycaster, MeasurementTolerances.Default, EdgeTol, EndpointTol);
        // PreparedOnlySelection defaults to false.

        Vector3d? hover = picker.TryHoverSnap(MidRayOrigin, DownRay);   // not warmed -> null
        PickResult pick = picker.Pick(new PickRequest(PickKind.Point, MidRayOrigin, DownRay));

        Assert.Null(hover);
        Assert.IsType<PickResult.PickedPoint>(pick);   // built synchronously
    }
}
```

> Confirmed: `ScenePoint` is `readonly record struct ScenePoint(Vector3d World, SceneAttachment? Attachment = null)` — the world-point accessor is `.World`, used in these tests and in Task 3's `new ScenePoint(r.Point, attachment)`.

- [ ] **Step 2: Run to verify they fail (do-not-compile is an acceptable red)**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~MeshMeasurePickerConsistencyTests" -c Release --nologo
```
Expected: build error `'MeshMeasurePicker' does not contain a definition for 'PreparedOnlySelection'` (or the tests fail). This is the red state.

- [ ] **Step 3: Commit the failing tests**

```bash
git add src/FabricationAssistant.App.Android.Tests/MeshMeasurePickerConsistencyTests.cs
git commit -m "test: pin hover==pick marker/selection consistency (failing)"
```

---

## Task 3: Gap A — implement `PreparedOnlySelection` + unified `ResolveSnapPoint`

**Files:**
- Modify: `../src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs`
- Modify: `src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs:61-66`

- [ ] **Step 1: Add the flag + a private resolution struct/method to `MeshMeasurePicker`**

In `MeshMeasurePicker.cs`, add the public flag near the other public members (after `DiagnosticsLog`, around line 24):

```csharp
    /// <summary>
    /// When true (Android touch policy), selection commits only what the hover
    /// preview can show: both hover and click resolve the snap point through the
    /// same prepared-only candidate set, so the red marker always equals the
    /// committed point. Click never builds snap models synchronously. When false
    /// (desktop default) click may build on demand and hover stays prepared-only,
    /// preserving the existing desktop behaviour.
    /// </summary>
    public bool PreparedOnlySelection { get; set; }
```

Add this private struct + method (place near `TryHoverSnap`, e.g. after line 206):

```csharp
    private readonly record struct SnapResolution(Vector3d Point, int? AttachmentNodeId);

    /// <summary>
    /// The single point-resolution both hover and click use. Primary edge snap on
    /// the ray-hit mesh first; supplemental (other visible meshes + section curves)
    /// second. <paramref name="allowBuild"/> false = prepared-only primary (no
    /// synchronous build). Sharing this between hover and click guarantees
    /// marker == selection. NOTE: supplemental is ALWAYS prepared-only (it scans
    /// every visible mesh; building them all synchronously would stall a click) —
    /// this preserves the original desktop semantics where supplemental used
    /// allowBuild:false at every call site.
    /// </summary>
    private SnapResolution? ResolveSnapPoint(
        Vector3d rayOrigin,
        Vector3d rayDirection,
        string phase,
        bool allowBuild,
        bool includeSupplemental)
    {
        var hit = _raycaster.Raycast(rayOrigin, rayDirection);
        if (hit is { } h
            && TryEdgeSnap(h, rayOrigin, rayDirection, phase, out Vector3d primary, allowBuild))
        {
            return new SnapResolution(primary, h.NodeId);
        }

        if (includeSupplemental
            && TrySupplementalPointSnap(rayOrigin, rayDirection, out Vector3d supplemental, allowBuild: false))
        {
            return new SnapResolution(supplemental, null);
        }

        return null;
    }
```

- [ ] **Step 2: Route `TryHoverSnap` through `ResolveSnapPoint`**

Replace the body of `TryHoverSnap` (current lines 179-206) with:

```csharp
    public Vector3d? TryHoverSnap(Vector3d rayOrigin, Vector3d rayDirection)
        => ResolveSnapPoint(
            rayOrigin,
            rayDirection,
            "hover",
            allowBuild: false,
            includeSupplemental: PreparedOnlySelection)?.Point;
```

> Desktop (`PreparedOnlySelection == false`): hover stays primary-only + prepared-only (unchanged). Android (`true`): hover now also previews supplemental/section, matching what click commits.

- [ ] **Step 3: Route the Point branch of `Pick` through `ResolveSnapPoint`**

In `Pick` (current lines 58-117), the method currently raycasts at the top (line 65) and the Point branch reuses it. Restructure so the Point branch uses `ResolveSnapPoint` and the Face branch keeps its own raycast. Replace lines 58-117 (from `public PickResult Pick(PickRequest request)` through the end of the `if (request.Kind == PickKind.Point)` block, i.e. up to and including the closing `}` at line 117) with:

```csharp
    public PickResult Pick(PickRequest request)
    {
        // S13-B-003 fix: face highlight is carried inside PickResult.PickedFace.Highlight.
        if (request.Kind == PickKind.Point)
        {
            SnapResolution? resolved = ResolveSnapPoint(
                request.RayOrigin,
                request.RayDirection,
                "pick",
                allowBuild: !PreparedOnlySelection,
                includeSupplemental: true);

            if (resolved is not { } r)
            {
                LogSnapPipeline("pick", "miss: primary and supplemental snap missed", force: true);
                return PickResult.MissSingleton;
            }

            SceneAttachment? attachment = null;
            if (r.AttachmentNodeId is int nodeId && _raycaster.GetNode(nodeId) is not null)
            {
                Matrix4d nodeWorld = _raycaster.GetWorldTransform(nodeId);
                if (nodeWorld.TryInvert(out Matrix4d inverse))
                    attachment = new SceneAttachment(nodeId, inverse.TransformPoint(r.Point));
            }

            return new PickResult.PickedPoint(new ScenePoint(r.Point, attachment));
        }

        var hit = _raycaster.Raycast(request.RayOrigin, request.RayDirection);
```

> This leaves the existing Face-branch code (current lines 119-160, starting `if (hit is null)`) intact and using the `hit` local now declared at the end of the replacement. Confirm the Face branch still compiles against the `hit` variable.

- [ ] **Step 4: Set the flag on the Android picker**

In `AndroidMeasureIntegration.cs`, the picker is created at lines 61-66. Change it to set the flag via an object initializer:

```csharp
        var picker = new MeshMeasurePicker(
            _raycaster,
            MeasurementTolerances.Default,
            AndroidEdgeSnapAngularTolerance,
            AndroidEndpointSnapAngularTolerance)
        {
            PreparedOnlySelection = true,
        };
```

- [ ] **Step 5: Run the consistency tests + the full existing snap suite**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~MeshMeasurePickerConsistencyTests|FullyQualifiedName~AndroidEdgeSnapServiceTests" -c Release --nologo
```
Expected: `Passed!  - Failed: 0` (4 consistency + 33 engine = 37 passed).

- [ ] **Step 6: Commit (TWO repos — the picker is in the parent repo)**

```bash
# Android repo: the integration wiring that sets the flag.
git add src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs
git commit -m "feat: enable commit-only-previewed snapping on Android (PreparedOnlySelection)"

# Parent desktop repo: the shared picker change (desktop-safe; flag defaults false).
git -C ".." add "src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs"
git -C ".." commit -m "feat: unify hover/click point resolution behind PreparedOnlySelection flag"
```

---

## Task 4: Gap C — remove the per-hover diagnostic-source allocation

**Files:**
- Create: `src/FabricationAssistant.App.Android.Tests/EdgeSnapAllocationTests.cs`
- Modify: `src/FabricationAssistant.Core.Android/Measurement/Engine/EdgeSnapService.cs:73,99`

- [ ] **Step 1: Write the failing allocation test**

Create `src/FabricationAssistant.App.Android.Tests/EdgeSnapAllocationTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class EdgeSnapAllocationTests
{
    [Fact]
    public void TrySnapPrepared_WithDiagnosticsOff_DoesNotAllocatePerScan()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;

        var svc = new EdgeSnapService();
        float[] edges = { 0, 0, 0, 1, 0, 0 };
        svc.Prepare(edges);
        var origin = new Vector3d(0.5, 0, 1);
        var dir = new Vector3d(0, 0, -1);

        // warm up JIT
        for (int i = 0; i < 200; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);

        const int iterations = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // With diagnostics off there must be no per-scan heap allocation.
        Assert.True(allocated < 256, $"allocated {allocated} bytes over {iterations} scans");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~EdgeSnapAllocationTests" -c Release --nologo
```
Expected: FAIL — `allocated ~128000 bytes` (the per-scan `$"edgeBuffer=…"` string × 2000).

- [ ] **Step 3: Guard the diagnostics-source string at both hover call sites**

In `EdgeSnapService.cs`, the `TrySnap(float[]...)` overload (line 73) and `TrySnapPrepared` (line 99) each pass `$"edgeBuffer={RuntimeHelpers.GetHashCode(edgePositions)}"` as `diagnosticsSource`. The string is only used when `DiagnosticsLog` is set. Replace both occurrences of:

```csharp
                $"edgeBuffer={RuntimeHelpers.GetHashCode(edgePositions)}",
```

with:

```csharp
                DiagnosticsLog is null ? string.Empty : $"edgeBuffer={RuntimeHelpers.GetHashCode(edgePositions)}",
```

- [ ] **Step 4: Run to verify it passes + re-run the snap suite**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~EdgeSnapAllocationTests|FullyQualifiedName~AndroidEdgeSnapServiceTests" -c Release --nologo
```
Expected: `Passed!  - Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/FabricationAssistant.App.Android.Tests/EdgeSnapAllocationTests.cs src/FabricationAssistant.Core.Android/Measurement/Engine/EdgeSnapService.cs
git commit -m "perf: drop per-hover diagnostics-source string allocation when logging is off"
```

---

## Task 5: Performance-regression guard (engine build + scan)

**Files:**
- Create: `src/FabricationAssistant.App.Android.Tests/EdgeSnapPerfGuardTests.cs`

A catastrophic-regression guard, not a microbenchmark. Thresholds are desktop x64 baselines with large headroom (measured: 10k welded build ~40 ms, scan ~12 µs). Device frame-rate stays a manual checklist item (Task 7).

- [ ] **Step 1: Write the guard test**

Create `src/FabricationAssistant.App.Android.Tests/EdgeSnapPerfGuardTests.cs`:

```csharp
using System.Diagnostics;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class EdgeSnapPerfGuardTests
{
    public EdgeSnapPerfGuardTests()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;
    }

    [Fact]
    public void HoverScan_OnTenThousandSegmentMesh_StaysWellUnderFrameBudget()
    {
        float[] edges = WeldedRuns(10_000);
        var svc = new EdgeSnapService();
        svc.Prepare(edges);
        var origin = new Vector3d(5, 0, 1000);
        var dir = new Vector3d(0, 0, -1);

        for (int i = 0; i < 500; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);

        const int iters = 2000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            svc.TrySnapPrepared(edges, Matrix4d.Identity, origin, dir, 0.022, 0.0085);
        sw.Stop();
        double perScanMs = sw.Elapsed.TotalMilliseconds / iters;

        // Desktop baseline ~0.012 ms. Guard at 2 ms (catastrophic-regression only).
        Assert.True(perScanMs < 2.0, $"hover scan {perScanMs:0.000} ms/op exceeds 2 ms guard");
    }

    [Fact]
    public void Build_OnFiftyThousandSegmentMesh_CompletesInReasonableTime()
    {
        float[] edges = WeldedRuns(50_000);
        var svc = new EdgeSnapService();
        var sw = Stopwatch.StartNew();
        svc.Prepare(edges);
        sw.Stop();

        // Desktop baseline ~40 ms. Guard at 1500 ms (ARM headroom; build is off the
        // hot path on a background warmup thread).
        Assert.True(sw.Elapsed.TotalMilliseconds < 1500, $"build {sw.Elapsed.TotalMilliseconds:0} ms exceeds 1500 ms guard");
    }

    private static float[] WeldedRuns(int segments)
    {
        var v = new float[segments * 6];
        const int perRun = 10;
        for (int s = 0; s < segments; s++)
        {
            int i = s % perRun;
            float y = (s / perRun) * 5.0f;
            int o = s * 6;
            v[o] = i; v[o + 1] = y; v[o + 2] = 0;
            v[o + 3] = i + 1; v[o + 4] = y; v[o + 5] = 0;
        }
        return v;
    }
}
```

- [ ] **Step 2: Run to verify it passes**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~EdgeSnapPerfGuardTests" -c Release --nologo
```
Expected: `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 3: Commit**

```bash
git add src/FabricationAssistant.App.Android.Tests/EdgeSnapPerfGuardTests.cs
git commit -m "test: add engine build/scan perf-regression guards"
```

---

## Task 6: Validation-matrix coverage — host-runnable §11 scenarios

**Files:**
- Create: `src/FabricationAssistant.App.Android.Tests/SnapValidationMatrixTests.cs`
- Create: `src/FabricationAssistant.App.Android.Tests/AndroidSectionClipperTests.cs`

Fill the gaps in the §11 scenarios that the host can reach (dense segmented model, very small arc segments, non-square rectangle, and section-clip visibility). Existing `AndroidEdgeSnapServiceTests` already cover single/welded straight, internal-joint rejection, segmented arcs, two-segment arcs, mixed line/arc chains, closed circles, rounded rectangles, compound tangent arcs, half-ellipse base, and the visibility-filter fallback.

- [ ] **Step 1: Write the §11 gap tests**

Create `src/FabricationAssistant.App.Android.Tests/SnapValidationMatrixTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Engine;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class SnapValidationMatrixTests
{
    private const double EdgeTol = 0.1;
    private const double EndpointTol = 0.05;
    private readonly EdgeSnapService _svc = new();

    public SnapValidationMatrixTests()
    {
        EdgeSnapService.SnapEnabled = true;
        EdgeSnapService.EndpointSnapEnabled = true;
        EdgeSnapService.MidpointSnapEnabled = true;
        EdgeSnapService.EdgeSnapToleranceFactor = 1.0;
        EdgeSnapService.EndpointSnapToleranceFactor = 1.0;
        EdgeSnapService.WeldToleranceScale = 1.0e-5;
        EdgeSnapService.VisibilityFilter = null;
        EdgeSnapService.DiagnosticsLog = null;
    }

    // §11 Basic geometry: dense segmented model — many independent edges. Snapping
    // near one isolated segment's midpoint returns it; empty space returns null.
    [Fact]
    public void DenseIndependentSegments_SnapToNearestMidpoint_AndMissEmptySpace()
    {
        // 9 isolated horizontal unit segments on a 3x3 grid, spaced 5 apart.
        var list = new System.Collections.Generic.List<float>();
        for (int gy = 0; gy < 3; gy++)
        for (int gx = 0; gx < 3; gx++)
        {
            float x = gx * 5f, y = gy * 5f;
            list.AddRange(new[] { x, y, 0f, x + 1f, y, 0f });
        }
        float[] edges = list.ToArray();

        // Midpoint of the centre cell's segment is (5.5, 5, 0).
        EdgeSnapResult? hit = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(5.5, 5, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);
        Assert.NotNull(hit);
        Assert.Equal(5.5, hit!.Value.WorldPoint.X, 6);
        Assert.Equal(5.0, hit.Value.WorldPoint.Y, 6);

        // A point in the gap between cells snaps to nothing.
        EdgeSnapResult? miss = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(3.0, 2.5, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);
        Assert.Null(miss);
    }

    // §11 Arc geometry: very small arc segments still resolve to the arc midpoint.
    [Fact]
    public void VerySmallArcSegments_SnapToArcMidpoint()
    {
        float[] edges = ArcEdges(radius: 0.5, startRadians: 0.0, sweepRadians: System.Math.PI / 2.0, segments: 16);
        double c = System.Math.Sqrt(0.5) * 0.5; // midpoint at 45 deg on r=0.5

        EdgeSnapResult? hit = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(c, c, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);

        Assert.NotNull(hit);
        Assert.Equal(c, hit!.Value.WorldPoint.X, 5);
        Assert.Equal(c, hit.Value.WorldPoint.Y, 5);
    }

    // §11 Basic geometry: a non-square rectangle exposes its corners (endpoints),
    // not the interiors of its sides.
    [Fact]
    public void NonSquareRectangle_ExposesCorners_NotSideInteriors()
    {
        EdgeSnapService.MidpointSnapEnabled = false;
        // 4x2 rectangle as a closed loop.
        float[] edges =
        {
            0,0,0, 4,0,0,
            4,0,0, 4,2,0,
            4,2,0, 0,2,0,
            0,2,0, 0,0,0,
        };

        EdgeSnapResult? corner = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(4, 0, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);
        EdgeSnapResult? sideInterior = _svc.TrySnap(edges, Matrix4d.Identity,
            new Vector3d(2, 0, 1), new Vector3d(0, 0, -1), EdgeTol, EndpointTol);

        Assert.NotNull(corner);
        Assert.Equal(4.0, corner!.Value.WorldPoint.X, 6);
        Assert.Null(sideInterior); // midpoint off -> no snap mid-side
    }

    private static float[] ArcEdges(double radius, double startRadians, double sweepRadians, int segments)
    {
        var values = new float[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            double a0 = startRadians + sweepRadians * i / segments;
            double a1 = startRadians + sweepRadians * (i + 1) / segments;
            int o = i * 6;
            values[o] = (float)(System.Math.Cos(a0) * radius);
            values[o + 1] = (float)(System.Math.Sin(a0) * radius);
            values[o + 2] = 0f;
            values[o + 3] = (float)(System.Math.Cos(a1) * radius);
            values[o + 4] = (float)(System.Math.Sin(a1) * radius);
            values[o + 5] = 0f;
        }
        return values;
    }
}
```

- [ ] **Step 2: Write the section-clip visibility tests**

`AndroidSectionClipper` is already linked into the test project. Create `src/FabricationAssistant.App.Android.Tests/AndroidSectionClipperTests.cs`:

```csharp
using System.Numerics;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Sections;
using FabricationAssistant.App.Android.Measurement;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidSectionClipperTests
{
    private static SectionPlane PlaneKeepingPositiveX()
        => new(
            System.Guid.NewGuid(),
            Anchor: new Vector3(0, 0, 0),
            AxisX: new Vector3(0, 1, 0),
            AxisY: new Vector3(0, 0, 1),
            Normal: new Vector3(1, 0, 0)); // keep side: dot(normal, p) >= offset(0)

    [Fact]
    public void IsPointVisible_NoPlanes_IsAlwaysVisible()
    {
        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(-5, 0, 0), null, 10.0));
    }

    [Fact]
    public void IsPointVisible_OnKeptSide_IsVisible()
    {
        var planes = new[] { PlaneKeepingPositiveX() };
        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(1, 0, 0), planes, 10.0));
    }

    [Fact]
    public void IsPointVisible_OnClippedSide_IsHidden()
    {
        var planes = new[] { PlaneKeepingPositiveX() };
        Assert.False(AndroidSectionClipper.IsPointVisible(new Vector3d(-1, 0, 0), planes, 10.0));
    }
}
```

> Confirmed: `SectionPlane` is `readonly record struct SectionPlane(Guid Id, Vector3 Anchor, Vector3 AxisX, Vector3 AxisY, Vector3 Normal)` using `System.Numerics.Vector3`, with `Offset => Dot(Normal, Anchor)` and kept-side `dot(Normal, p) >= Offset`. The test's `using System.Numerics;` + ctor order match.

- [ ] **Step 3: Run both new files + the full suite**

Run:
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" -c Release --nologo
```
Expected: `Passed!  - Failed: 0` for the entire suite (existing + all new tests).

- [ ] **Step 4: Commit**

```bash
git add src/FabricationAssistant.App.Android.Tests/SnapValidationMatrixTests.cs src/FabricationAssistant.App.Android.Tests/AndroidSectionClipperTests.cs
git commit -m "test: §11 validation-matrix coverage (dense/tiny-arc/rectangle/section-clip)"
```

---

## Task 7: On-device validation checklist (the parts the host can't reach)

**Files:**
- Create: `docs/Android-Snap-Validation-Checklist-2026-06-01.md`

Items that require the BVH raycaster, real scene visibility/isolation, the section-cap raycast, or a live frame counter — none of which load in the net8.0 host — are validated manually and tracked here.

- [ ] **Step 1: Write the checklist**

Create `docs/Android-Snap-Validation-Checklist-2026-06-01.md`:

```markdown
# Android Snap — On-Device Validation Checklist (2026-06-01)

Run on a representative ARM tablet with an S Pen. Each item maps to brief §11/§12.

## Visibility (§11, §12)
- [ ] Front-facing visible edge snaps; red marker appears on it.
- [ ] Back-side edge (behind the solid) does NOT snap.
- [ ] Edge behind a face is not selectable; marker does not appear through the surface.
- [ ] Edge occluded by another body is not selectable.
- [ ] Hidden body: its edges are not selectable.
- [ ] Isolated body: only the isolated body's edges are selectable.
- [ ] Sectioned model: clipped-away edges are not selectable; section curves ARE.

## Section geometry (§6, §11)
- [ ] Section curves snap (endpoints + midpoint) like normal edges.
- [ ] Section cap face is selectable in Face-to-Point.
- [ ] Moving the section plane updates snap targets within a frame or two (no stale marker).

## Tools (§11, §12)
- [ ] Point-to-Point: first point and second point both snap; marker == committed point.
- [ ] Face-to-Point: select a face, then snap the point; marker == committed point.
- [ ] Endpoint-snap toggle and Midpoint-snap toggle in the measurement toolbar both take effect live.
- [ ] On a freshly loaded large model, before warmup finishes: hover shows no marker AND a tap does not commit a point (consistent — the accepted commit-only-previewed trade-off).

## Performance (§12)
- [ ] Move the S Pen continuously over a dense model's edges: marker tracks with no visible lag; viewport stays ~60 FPS (watch the FA frame/jank logs).
- [ ] Repeat with a section active and many visible edges on screen.
- [ ] Move over empty space continuously: no marker, no frame drops.
- [ ] No GC churn spikes in logcat during continuous hover (the engine scan is allocation-free with diagnostics off — guarded by EdgeSnapAllocationTests).

## If any item fails
Record which, then apply the matching conditional task (B/D/E/F) from the plan.
```

- [ ] **Step 2: Commit**

```bash
git add docs/Android-Snap-Validation-Checklist-2026-06-01.md
git commit -m "docs: on-device snap validation checklist (§11/§12 manual items)"
```

---

## Conditional follow-up tasks (implement ONLY if the Task 7 checklist flags them)

These address gaps B/D/E/F from the spec. They are not implemented up front because the
engine profile shows they are not needed for realistic models; gate each on a specific
failed checklist item.

### Conditional Task B — coalesce hover + single occlusion raycast
**Trigger:** continuous-hover frame-rate item fails on-device.
- In `MainActivity` measure-hover dispatch (`MainActivity.cs:11370-11390`), drop hover work to ≤1 per rendered frame: store the latest `(rayOrigin, rayDirection)` and run `HandleHover` from the existing per-frame invalidate/render callback instead of per motion event.
- In `EdgeSnapService.TrySnapTargets` (`EdgeSnapService.cs:169-219`), stop visibility-checking every improving candidate. Instead score all candidates first, sort the top few by angular score, then visibility-check in order and accept the first visible one. This turns up to O(targets) occlusion raycasts into ~1.
- Add a test asserting the chosen candidate equals the best *visible* candidate (extend `AndroidEdgeSnapServiceTests` visibility-fallback cases).

### Conditional Task D — eliminate the click build-hitch on large/un-warmed meshes
**Trigger:** the "freshly loaded large model" item shows a click stall, or the
no-snap-until-warmed trade-off is unacceptable in practice.
- In `AndroidMeasureIntegration.cs`, raise/remove `SnapWarmupMaxSegmentsPerMesh` (currently 10_000, line 17) and/or add a background "warm on hover-miss" trigger: when hover misses on a mesh whose model is not prepared, enqueue that mesh into the existing warmup task.
- Never set `PreparedOnlySelection = false` on Android (that would reintroduce UI-thread build on click and the marker/selection gap).

### Conditional Task E — selection hysteresis
**Trigger:** marker flicker between adjacent candidates is observed.
- Add a small previous-target stickiness in `EdgeSnapService` (remember the last chosen target's identity; require a new candidate to beat it by a margin before switching). Add a test with two near-equidistant targets + a jittered ray asserting the choice is stable.

### Conditional Task F — per-mesh target cap
**Trigger:** a real model exhibits a single hit-mesh with >~50k non-weldable targets and a measurable hover stall.
- Add an optional target cap / coarse spatial bucketing in `SnapModelBuilder`; document the cap via `DiagnosticsLog` so truncation is never silent.

---

## Final verification

- [ ] **Run the entire test suite:**
```bash
dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" -c Release --nologo
```
Expected: `Passed!  - Failed: 0` (existing 33 + ~12 new).

- [ ] **Build the Android app** (requires the Android workload; if unavailable on this box, note it and defer to CI):
```bash
dotnet build "src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj" -c Release --nologo
```
Expected: build succeeds (confirms the `MeshMeasurePicker` change compiles in the Android target too).

- [ ] **Work through the on-device checklist** (Task 7) on a real tablet; record results; trigger any conditional tasks.

- [ ] **Summarise** results back to the requester: deliverables 14.1–14.8 from the brief (investigation, before/after profile, strategy, changes, scenarios tested, limitations, follow-ups), and the explicit statement of whether snapping is production-ready (expected: yes for hover; click consistency now guaranteed by construction).
```
