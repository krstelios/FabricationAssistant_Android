# Dimensions Follow Their Anchor Body — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rigidly weld each newly created dimension (point-to-point, face-to-point, face-to-face, bounding box) to the first body selected when it was created, so moving/exploding that body moves the whole dimension with it.

**Architecture:** Store a `MeasurementAnchor(NodeId, PoseAtCreation)` on each measurement at commit time. At render time the presenter looks up the body's *current* `EffectiveWorldTransform`, computes `delta = current × creationPose⁻¹`, and transforms every primitive of that measurement by `delta`. Capture and render both receive a `Func<int, Matrix4d?>` node-pose lookup; without it, behaviour is identical to today (so the desktop app is unaffected).

**Tech Stack:** C# / .NET, xUnit tests. Shared `FabricationAssistant.Core` (parent git repo) + `FabricationAssistant.App.Android` (nested git repo).

**Spec:** `Android/docs/superpowers/specs/2026-06-02-android-dimensions-follow-body-design.md`

---

## Repository layout & commands

There are **two git repos**:

- **Parent repo** — `C:\Users\skritikos\Desktop\Fabrication Assistant` (branch `master`). Holds
  `src/FabricationAssistant.Core/...` and the Core tests in
  `src/FabricationAssistant.App.Tests/...`. **Tasks 1–5 commit here.**
- **Android repo** — `C:\Users\skritikos\Desktop\Fabrication Assistant\Android` (branch `master`).
  Holds `src/FabricationAssistant.App.Android/...` and its tests. **Task 6 commits here.**

Build/test commands (run from the **parent** dir unless noted):

- Core tests: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~Measurement"`
- Android tests (run from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~SourceGuards"`

> If `dotnet test` for the whole filter is slow, target a single class with
> `--filter "FullyQualifiedName~MeasurementPresenterTests"` etc.

## File structure

**Core (parent repo)**
| File | Responsibility | Change |
|------|----------------|--------|
| `src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs` | Measurement records | Add `MeasurementAnchor` struct + `Anchor` init property |
| `src/FabricationAssistant.Core/Measurement/Presentation/PresentationSnapshot.cs` | Render primitives container | Add `TransformedBy(Matrix4d)` |
| `src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs` | Builds snapshots | `nodePoseLookup` param + apply delta |
| `src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs` | Pick results | Add `int? NodeId` to `PickedFace` |
| `src/FabricationAssistant.Core/Measurement/Domain/MeasurementDraft.cs` | In-progress drafts | Carry node id on face drafts |
| `src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs` | Picker | Set `NodeId` on `PickedFace` |
| `src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs` | Commit pipeline | `nodePoseLookup` ctor param; stamp `Anchor`; bbox `anchorNodeId` |
| `src/FabricationAssistant.Core/Measurement/Engine/MeasureTool.cs` | Thin tool wrapper | Forward `anchorNodeId` on bbox commit |

**Core tests (parent repo)** — `src/FabricationAssistant.App.Tests/Measurement/`: new `MeasurementAnchorTests.cs`, new `PresentationSnapshotTransformTests.cs`, additions to `MeasurementPresenterTests.cs` and `MeasurementSessionTests.cs`.

**Android (nested repo)**
| File | Responsibility | Change |
|------|----------------|--------|
| `src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs` | Wiring | Build lookup; pass to session + presenter; bbox primary node |
| `src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs` | Source-guard tests | Add wiring guard `[Fact]` |

---

## Task 1: Domain — `MeasurementAnchor` + `Anchor` property

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/MeasurementAnchorTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/MeasurementAnchorTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public class MeasurementAnchorTests
{
    [Fact]
    public void Anchor_DefaultsToNull_AndWithPreservesDerivedType()
    {
        var m = new PointToPointMeasurement(
            MeasurementId.New(),
            new ScenePoint(Vector3d.Zero),
            new ScenePoint(new Vector3d(1, 0, 0)),
            new SceneLength(1.0),
            new Vector3d(1, 0, 0));

        Assert.Null(m.Anchor);

        var pose = Matrix4d.CreateTranslation(5, 0, 0);
        MeasurementResult anchored = m with { Anchor = new MeasurementAnchor(7, pose) };

        Assert.IsType<PointToPointMeasurement>(anchored);
        Assert.NotNull(anchored.Anchor);
        Assert.Equal(7, anchored.Anchor!.Value.NodeId);
        Assert.Equal(pose, anchored.Anchor!.Value.PoseAtCreation);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~MeasurementAnchorTests"`
Expected: FAIL to compile — `MeasurementAnchor` does not exist and `MeasurementResult` has no `Anchor`.

- [ ] **Step 3: Add the type and property**

In `Measurement.cs`, replace the base record block (lines 5–8):

```csharp
public abstract record MeasurementResult(MeasurementId Id, bool IsVisible)
{
    public abstract MeasureToolMode Mode { get; }
}
```

with:

```csharp
/// <summary>
/// Rigidly welds a measurement to the body it was built from. <see cref="NodeId"/>
/// is the leaf mesh node the first pick resolved to; <see cref="PoseAtCreation"/>
/// is that node's <c>EffectiveWorldTransform</c> at the moment of creation. The
/// presenter renders the measurement at <c>currentPose × PoseAtCreation⁻¹</c> so it
/// follows the body when it is moved or exploded.
/// </summary>
public readonly record struct MeasurementAnchor(int NodeId, Matrix4d PoseAtCreation);

public abstract record MeasurementResult(MeasurementId Id, bool IsVisible)
{
    public abstract MeasureToolMode Mode { get; }

    /// <summary>First-body anchor. Null = static (legacy / first pick not on a body).</summary>
    public MeasurementAnchor? Anchor { get; init; }
}
```

(`Measurement.cs` already has `using FabricationAssistant.Core.Math;` at the top, so `Matrix4d` resolves.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~MeasurementAnchorTests"`
Expected: PASS.

- [ ] **Step 5: Commit (parent repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs src/FabricationAssistant.App.Tests/Measurement/MeasurementAnchorTests.cs
git commit -m "feat(measure): add MeasurementAnchor to measurement records

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `PresentationSnapshot.TransformedBy(Matrix4d)`

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Presentation/PresentationSnapshot.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/PresentationSnapshotTransformTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `src/FabricationAssistant.App.Tests/Measurement/PresentationSnapshotTransformTests.cs`:

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Measurement.Domain;
using FabricationAssistant.Core.Measurement.Presentation;
using Xunit;

namespace FabricationAssistant.App.Tests.Measurement;

public class PresentationSnapshotTransformTests
{
    [Fact]
    public void TransformedBy_TranslatesPoints_AndRotatesDirections()
    {
        var snap = new PresentationSnapshot(
            MeasurementId.New(),
            PresentationStyle.Committed,
            Balls: new[] { new BallPrimitive(new Vector3d(1, 0, 0)) },
            Disks: new[] { new DiskPrimitive(new Vector3d(1, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.5) },
            Lines: new[] { new LinePrimitive(new Vector3d(1, 0, 0), new Vector3d(2, 0, 0)) },
            Labels: new[] { new LabelPrimitive("x", new Vector3d(1, 0, 0),
                new LabelAlignment.LineAligned(Vector3d.UnitX, Vector3d.UnitY)) });

        // 90° about Z: (x,y,z) -> (-y, x, z); then translate +10 on Y.
        var delta = Matrix4d.CreateTranslation(0, 10, 0) * Matrix4d.CreateRotationZ(System.Math.PI / 2);
        var moved = snap.TransformedBy(delta);

        AssertVec(new Vector3d(0, 11, 0), moved.Balls[0].Center);     // point: (1,0,0)->(0,1,0)->+10y
        AssertVec(new Vector3d(0, 11, 0), moved.Disks[0].Center);
        AssertVec(new Vector3d(0, 1, 0), moved.Disks[0].Normal);      // direction: UnitX -> UnitY (no translation)
        AssertVec(new Vector3d(-1, 0, 0), moved.Disks[0].U);          // UnitY -> -UnitX
        AssertVec(new Vector3d(0, 11, 0), moved.Lines[0].Start);
        AssertVec(new Vector3d(0, 12, 0), moved.Lines[0].End);        // (2,0,0)->(0,2,0)->+10y

        var la = Assert.IsType<LabelAlignment.LineAligned>(moved.Labels[0].Alignment);
        AssertVec(new Vector3d(0, 1, 0), la.LineDirection);           // UnitX -> UnitY
        Assert.Equal("x", moved.Labels[0].Text);                     // text unchanged
    }

    private static void AssertVec(Vector3d expected, Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~PresentationSnapshotTransformTests"`
Expected: FAIL to compile — `TransformedBy` does not exist.

- [ ] **Step 3: Add `TransformedBy`**

Replace the whole body of `PresentationSnapshot.cs` with:

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
    IReadOnlyList<LabelPrimitive> Labels)
{
    /// <summary>
    /// Returns a copy with every primitive rigidly transformed by <paramref name="delta"/>:
    /// positions via <see cref="Matrix4d.TransformPoint"/>, directions (disk normal/U,
    /// line-aligned label axes) via <see cref="Matrix4d.TransformDirection"/>. Style, id,
    /// and label text are unchanged. Used to make an anchored measurement follow its body.
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

        return new PresentationSnapshot(Id, Style, balls, disks, lines, labels);
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

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~PresentationSnapshotTransformTests"`
Expected: PASS.

- [ ] **Step 5: Commit (parent repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Presentation/PresentationSnapshot.cs src/FabricationAssistant.App.Tests/Measurement/PresentationSnapshotTransformTests.cs
git commit -m "feat(measure): PresentationSnapshot.TransformedBy for rigid follow

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Presenter applies the anchor delta

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/MeasurementPresenterTests.cs` (add tests)

- [ ] **Step 1: Write the failing tests**

Append to `MeasurementPresenterTests.cs` (inside the class, before the closing brace):

```csharp
    private static PointToPointMeasurement AnchoredPtp(int nodeId, Matrix4d pose) =>
        new PointToPointMeasurement(
            MeasurementId.New(),
            new ScenePoint(new Vector3d(0, 0, 0)),
            new ScenePoint(new Vector3d(1, 0, 0)),
            new SceneLength(1.0),
            new Vector3d(1, 0, 0))
        { Anchor = new MeasurementAnchor(nodeId, pose) };

    [Fact]
    public void Anchored_FollowsTranslation_AndKeepsValue()
    {
        var m = AnchoredPtp(7, Matrix4d.Identity);
        var presenter = new MeasurementPresenter(new FakeUnits());

        var moved = Matrix4d.CreateTranslation(0, 10, 0);
        var snap = presenter.Build(new[] { m }, null, null, MeasureToolMode.None,
            nodePoseLookup: id => id == 7 ? moved : (Matrix4d?)null);

        Assert.Equal(0, snap[0].Balls[0].Center.X, 6);
        Assert.Equal(10, snap[0].Balls[0].Center.Y, 6);
        Assert.Equal(1, snap[0].Balls[1].Center.X, 6);
        Assert.Equal(10, snap[0].Balls[1].Center.Y, 6);
        Assert.Equal("1.00 m", snap[0].Labels[0].Text); // value frozen
    }

    [Fact]
    public void Anchored_FollowsRotation()
    {
        var m = AnchoredPtp(7, Matrix4d.Identity);
        var presenter = new MeasurementPresenter(new FakeUnits());

        var rot = Matrix4d.CreateRotationZ(System.Math.PI / 2); // (1,0,0) -> (0,1,0)
        var snap = presenter.Build(new[] { m }, null, null, MeasureToolMode.None,
            nodePoseLookup: id => rot);

        Assert.Equal(0, snap[0].Balls[1].Center.X, 6);
        Assert.Equal(1, snap[0].Balls[1].Center.Y, 6);
    }

    [Fact]
    public void Anchored_WithMissingNode_RendersStatic()
    {
        var m = AnchoredPtp(7, Matrix4d.Identity);
        var presenter = new MeasurementPresenter(new FakeUnits());

        var snap = presenter.Build(new[] { m }, null, null, MeasureToolMode.None,
            nodePoseLookup: id => (Matrix4d?)null); // node gone

        Assert.Equal(1, snap[0].Balls[1].Center.X, 6); // original baked position
        Assert.Equal(0, snap[0].Balls[1].Center.Y, 6);
    }

    [Fact]
    public void Unanchored_WithLookup_IsUnchanged()
    {
        var m = new PointToPointMeasurement(
            MeasurementId.New(),
            new ScenePoint(new Vector3d(0, 0, 0)),
            new ScenePoint(new Vector3d(1, 0, 0)),
            new SceneLength(1.0),
            new Vector3d(1, 0, 0)); // no Anchor
        var presenter = new MeasurementPresenter(new FakeUnits());

        var snap = presenter.Build(new[] { m }, null, null, MeasureToolMode.None,
            nodePoseLookup: id => Matrix4d.CreateTranslation(0, 99, 0));

        Assert.Equal(1, snap[0].Balls[1].Center.X, 6);
        Assert.Equal(0, snap[0].Balls[1].Center.Y, 6);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~MeasurementPresenterTests"`
Expected: FAIL to compile — `Build` has no `nodePoseLookup` parameter.

- [ ] **Step 3: Add the parameter and delta application**

In `MeasurementPresenter.cs`, change the `Build` signature to add the trailing parameter:

```csharp
    public IReadOnlyList<PresentationSnapshot> Build(
        IReadOnlyList<MeasurementResult> committed,
        MeasurementId? selectedId,
        MeasurementDraft? currentDraft,
        MeasureToolMode activeTool,
        Vector3d? hoverPoint = null,
        MeasurementId? hoveredId = null,
        bool showPointToPointDeltas = false,
        Func<int, Matrix4d?>? nodePoseLookup = null)
```

Inside `Build`, replace the line `snapshots.Add(BuildFor(m, style, showPointToPointDeltas));` with:

```csharp
            PresentationSnapshot snap = BuildFor(m, style, showPointToPointDeltas);
            snapshots.Add(ApplyAnchor(snap, m, nodePoseLookup));
```

Add this private helper to the class (e.g. just after `Build`):

```csharp
    private static PresentationSnapshot ApplyAnchor(
        PresentationSnapshot snap,
        MeasurementResult m,
        Func<int, Matrix4d?>? nodePoseLookup)
    {
        if (nodePoseLookup is null || m.Anchor is not { } anchor)
            return snap;
        if (nodePoseLookup(anchor.NodeId) is not Matrix4d current)
            return snap;                                  // node gone → static
        if (current.Equals(anchor.PoseAtCreation))
            return snap;                                  // unmoved → no-op
        if (!anchor.PoseAtCreation.TryInvert(out Matrix4d inverse))
            return snap;                                  // singular pose → static
        return snap.TransformedBy(current * inverse);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~MeasurementPresenterTests"`
Expected: PASS (new tests + all pre-existing presenter tests).

- [ ] **Step 5: Commit (parent repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Presentation/MeasurementPresenter.cs src/FabricationAssistant.App.Tests/Measurement/MeasurementPresenterTests.cs
git commit -m "feat(measure): presenter renders anchored dimensions at the body's live pose

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Propagate the face node id through picks and drafts

This is additive plumbing with defaults — no behaviour change yet. Verified by build + existing tests staying green. It enables face-anchor capture in Task 5.

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs`
- Modify: `src/FabricationAssistant.Core/Measurement/Domain/MeasurementDraft.cs`
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs`

- [ ] **Step 1: Add `NodeId` to `PickedFace`**

In `PickTypes.cs`, change the `PickedFace` record (line ~25):

```csharp
    public sealed record PickedFace(ScenePlane Plane, Vector3d ClickWorld, FaceHighlight? Highlight = null) : PickResult;
```

to:

```csharp
    public sealed record PickedFace(
        ScenePlane Plane,
        Vector3d ClickWorld,
        FaceHighlight? Highlight = null,
        int? NodeId = null) : PickResult;
```

- [ ] **Step 2: Carry node id on the face drafts**

In `MeasurementDraft.cs`, change:

```csharp
    public sealed record AwaitingPoint(ScenePlane Plane) : FaceToPointDraft;
```

to:

```csharp
    public sealed record AwaitingPoint(ScenePlane Plane, int? NodeId = null) : FaceToPointDraft;
```

and change:

```csharp
public sealed record FaceToFaceDraft(ScenePlane First, Vector3d FirstClick) : MeasurementDraft
```

to:

```csharp
public sealed record FaceToFaceDraft(ScenePlane First, Vector3d FirstClick, int? FirstNodeId = null) : MeasurementDraft
```

- [ ] **Step 3: Set `NodeId` in the picker**

In `MeshMeasurePicker.cs`, change the face return (line ~138):

```csharp
        return new PickResult.PickedFace(patch.Plane, h.WorldPoint, highlight);
```

to:

```csharp
        return new PickResult.PickedFace(patch.Plane, h.WorldPoint, highlight, h.NodeId);
```

- [ ] **Step 4: Build + run the existing measurement tests (no regression)**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~Measurement"`
Expected: PASS — all existing tests still green (defaults keep call sites valid).

- [ ] **Step 5: Commit (parent repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/PickTypes.cs src/FabricationAssistant.Core/Measurement/Domain/MeasurementDraft.cs src/FabricationAssistant.Core/Measurement/Engine/MeshMeasurePicker.cs
git commit -m "feat(measure): propagate picked-face node id through picks and drafts

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Session captures the anchor on every commit path

**Files:**
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs`
- Modify: `src/FabricationAssistant.Core/Measurement/Engine/MeasureTool.cs`
- Test: `src/FabricationAssistant.App.Tests/Measurement/MeasurementSessionTests.cs` (add tests)

- [ ] **Step 1: Write the failing tests**

Append to `MeasurementSessionTests.cs` (inside the class):

```csharp
    // A lookup that maps each node id to a distinct, recognizable pose so a test can
    // assert BOTH the captured node id and its pose in one shot.
    private static Func<int, Matrix4d?> PoseByNode =>
        id => Matrix4d.CreateTranslation(id, 0, 0);

    private static MeasurementSession NewAnchoringSession(out MeasurementStore store)
    {
        store = new MeasurementStore();
        return new MeasurementSession(
            store, new FakeUnits(), MeasurementTolerances.Default,
            undoService: null, nodePoseLookup: PoseByNode);
    }

    [Fact]
    public void PointToPoint_AnchorsToFirstPointsNode()
    {
        var session = NewAnchoringSession(out var store);
        session.SelectTool(MeasureToolMode.PointToPoint);
        session.SubmitPick(new PickResult.PickedPoint(
            new ScenePoint(Vector3d.Zero, new SceneAttachment(7, Vector3d.Zero))));
        session.SubmitPick(new PickResult.PickedPoint(
            new ScenePoint(new Vector3d(1, 0, 0), new SceneAttachment(9, new Vector3d(1, 0, 0)))));

        var anchor = store.Snapshot()[0].Anchor;
        Assert.NotNull(anchor);
        Assert.Equal(7, anchor!.Value.NodeId); // first pick wins, not 9
        Assert.Equal(Matrix4d.CreateTranslation(7, 0, 0), anchor!.Value.PoseAtCreation);
    }

    [Fact]
    public void FaceToFace_AnchorsToFirstFacesNode()
    {
        var session = NewAnchoringSession(out var store);
        session.SelectTool(MeasureToolMode.FaceToFace);
        var a = new ScenePlane(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitX, Vector3d.UnitZ, 1.0);
        var b = new ScenePlane(new Vector3d(0, 2, 0), Vector3d.UnitY, Vector3d.UnitX, Vector3d.UnitZ, 1.0);
        session.SubmitPick(new PickResult.PickedFace(a, a.Origin, NodeId: 3));
        session.SubmitPick(new PickResult.PickedFace(b, b.Origin, NodeId: 4));

        var anchor = store.Snapshot()[0].Anchor;
        Assert.NotNull(anchor);
        Assert.Equal(3, anchor!.Value.NodeId);
    }

    [Fact]
    public void FaceToPoint_FaceFirst_AnchorsToFace()
    {
        var session = NewAnchoringSession(out var store);
        session.SelectTool(MeasureToolMode.FaceToPoint);
        var plane = new ScenePlane(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitX, Vector3d.UnitZ, 1.0);
        session.SubmitPick(new PickResult.PickedFace(plane, plane.Origin, NodeId: 5));
        session.SubmitPick(new PickResult.PickedPoint(
            new ScenePoint(new Vector3d(0, 0.3, 0), new SceneAttachment(9, Vector3d.Zero))));

        Assert.Equal(5, store.Snapshot()[0].Anchor!.Value.NodeId);
    }

    [Fact]
    public void FaceToPoint_PointFirst_AnchorsToPoint()
    {
        var session = NewAnchoringSession(out var store);
        session.SelectTool(MeasureToolMode.FaceToPoint);
        session.SubmitPick(new PickResult.PickedPoint(
            new ScenePoint(new Vector3d(0, 0.3, 0), new SceneAttachment(8, Vector3d.Zero))));
        var plane = new ScenePlane(Vector3d.Zero, Vector3d.UnitY, Vector3d.UnitX, Vector3d.UnitZ, 1.0);
        session.SubmitPick(new PickResult.PickedFace(plane, plane.Origin, NodeId: 5));

        Assert.Equal(8, store.Snapshot()[0].Anchor!.Value.NodeId);
    }

    [Fact]
    public void BoundingBox_AnchorsToProvidedNode()
    {
        var session = NewAnchoringSession(out var store);
        session.SelectTool(MeasureToolMode.BoundingBox);
        var points = new[]
        {
            new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), new Vector3d(1, 1, 0), new Vector3d(0, 1, 0),
            new Vector3d(0, 0, 1), new Vector3d(1, 0, 1), new Vector3d(1, 1, 1), new Vector3d(0, 1, 1),
        };
        session.CommitBoundingBox(points, BoundingBoxMode.AxisAligned, anchorNodeId: 11);

        Assert.Equal(11, store.Snapshot()[0].Anchor!.Value.NodeId);
    }

    [Fact]
    public void NoLookup_LeavesAnchorNull()
    {
        var session = NewSession(out var store); // no nodePoseLookup
        session.SelectTool(MeasureToolMode.PointToPoint);
        session.SubmitPick(new PickResult.PickedPoint(
            new ScenePoint(Vector3d.Zero, new SceneAttachment(7, Vector3d.Zero))));
        session.SubmitPick(new PickResult.PickedPoint(new ScenePoint(new Vector3d(1, 0, 0))));

        Assert.Null(store.Snapshot()[0].Anchor);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~MeasurementSessionTests"`
Expected: FAIL to compile — the session ctor has no `nodePoseLookup`, and `CommitBoundingBox` has no `anchorNodeId`.

- [ ] **Step 3: Add the lookup field, ctor param, and `WithAnchor` helper**

In `MeasurementSession.cs`, add a field next to the others (after `_undoService`):

```csharp
    private readonly Func<int, Matrix4d?>? _nodePoseLookup;
```

Change the constructor to add the parameter and assign the field:

```csharp
    public MeasurementSession(
        IMeasurementStore store,
        IUnitSystemService units,
        MeasurementTolerances tolerances,
        IUndoService? undoService = null,
        Func<int, Matrix4d?>? nodePoseLookup = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _tolerances = tolerances ?? throw new ArgumentNullException(nameof(tolerances));
        _undoService = undoService ?? NullUndoService.Instance;
        _nodePoseLookup = nodePoseLookup;
    }
```

Add this helper near the bottom of the class (e.g. just above `private void Raise()`):

```csharp
    /// <summary>
    /// Stamps the first-body anchor onto a freshly built measurement. No-op when no
    /// pose lookup is wired, the first pick had no body, or the node can't be resolved.
    /// </summary>
    private MeasurementResult WithAnchor(MeasurementResult m, int? nodeId)
    {
        if (_nodePoseLookup is null || nodeId is not int id)
            return m;
        if (_nodePoseLookup(id) is not Matrix4d pose)
            return m;
        return m with { Anchor = new MeasurementAnchor(id, pose) };
    }
```

- [ ] **Step 4: Stamp the anchor at each commit site**

In `AdvancePtp`, replace the commit block:

```csharp
        if (CurrentDraft is PointToPointDraft draft)
        {
            var m = MeasurementBuilders.BuildPointToPoint(draft.A, pp.Point, _units.MetersPerSceneUnit);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
        return new PointToPointDraft(pp.Point);
```

with:

```csharp
        if (CurrentDraft is PointToPointDraft draft)
        {
            MeasurementResult m = MeasurementBuilders.BuildPointToPoint(draft.A, pp.Point, _units.MetersPerSceneUnit);
            m = WithAnchor(m, draft.A.Attachment?.NodeId);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
        return new PointToPointDraft(pp.Point);
```

In `AdvanceFtp`, the initial-draft branch — change:

```csharp
                PickResult.PickedFace pf => new FaceToPointDraft.AwaitingPoint(pf.Plane),
```

to:

```csharp
                PickResult.PickedFace pf => new FaceToPointDraft.AwaitingPoint(pf.Plane, pf.NodeId),
```

Then the AwaitingPoint+point commit:

```csharp
        if (CurrentDraft is FaceToPointDraft.AwaitingPoint awaitingPoint
            && result is PickResult.PickedPoint ppt)
        {
            var m = MeasurementBuilders.BuildFaceToPoint(
                awaitingPoint.Plane, ppt.Point, _units.MetersPerSceneUnit);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
```

becomes:

```csharp
        if (CurrentDraft is FaceToPointDraft.AwaitingPoint awaitingPoint
            && result is PickResult.PickedPoint ppt)
        {
            MeasurementResult m = MeasurementBuilders.BuildFaceToPoint(
                awaitingPoint.Plane, ppt.Point, _units.MetersPerSceneUnit);
            m = WithAnchor(m, awaitingPoint.NodeId);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
```

And the AwaitingFace+face commit:

```csharp
        if (CurrentDraft is FaceToPointDraft.AwaitingFace awaitingFace
            && result is PickResult.PickedFace pface)
        {
            var m = MeasurementBuilders.BuildFaceToPoint(
                pface.Plane, awaitingFace.Point, _units.MetersPerSceneUnit);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
```

becomes (anchor to the point's body, since the point was picked first):

```csharp
        if (CurrentDraft is FaceToPointDraft.AwaitingFace awaitingFace
            && result is PickResult.PickedFace pface)
        {
            MeasurementResult m = MeasurementBuilders.BuildFaceToPoint(
                pface.Plane, awaitingFace.Point, _units.MetersPerSceneUnit);
            m = WithAnchor(m, awaitingFace.Point.Attachment?.NodeId);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
```

In `AdvanceF2f`, change the commit block:

```csharp
        if (CurrentDraft is FaceToFaceDraft draft)
        {
            var m = MeasurementBuilders.BuildFaceToFace(
                draft.First, pf.Plane, draft.FirstClick, _units.MetersPerSceneUnit, _tolerances);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
        return new FaceToFaceDraft(pf.Plane, pf.ClickWorld);
```

to:

```csharp
        if (CurrentDraft is FaceToFaceDraft draft)
        {
            MeasurementResult m = MeasurementBuilders.BuildFaceToFace(
                draft.First, pf.Plane, draft.FirstClick, _units.MetersPerSceneUnit, _tolerances);
            m = WithAnchor(m, draft.FirstNodeId);
            using var txn = _undoService.Begin($"Add {m.Mode} measurement");
            _store.Add(m);
            txn.Add(new MeasurementAddChange(m));
            return null;
        }
        return new FaceToFaceDraft(pf.Plane, pf.ClickWorld, pf.NodeId);
```

- [ ] **Step 5: Thread `anchorNodeId` through the bounding-box commit**

In `MeasurementSession.cs`, change `CommitBoundingBox`:

```csharp
    public void CommitBoundingBox(IReadOnlyList<Vector3d> worldPoints, BoundingBoxMode mode)
    {
```

to:

```csharp
    public void CommitBoundingBox(IReadOnlyList<Vector3d> worldPoints, BoundingBoxMode mode, int? anchorNodeId = null)
    {
```

and at the end of that method change `CommitComputedBoundingBox(m);` to `CommitComputedBoundingBox(m, anchorNodeId);`.

Change `CommitComputedBoundingBox`:

```csharp
    public void CommitComputedBoundingBox(BoundingBoxMeasurement? m)
    {
        try
        {
            if (m is not null)
            {
                using var txn = _undoService.Begin($"Add {m.Mode} measurement");
                _store.Add(m);
                txn.Add(new MeasurementAddChange(m));
            }
            ResetTransientState();
        }
```

to:

```csharp
    public void CommitComputedBoundingBox(BoundingBoxMeasurement? m, int? anchorNodeId = null)
    {
        try
        {
            if (m is not null)
            {
                MeasurementResult stamped = WithAnchor(m, anchorNodeId);
                using var txn = _undoService.Begin($"Add {stamped.Mode} measurement");
                _store.Add(stamped);
                txn.Add(new MeasurementAddChange(stamped));
            }
            ResetTransientState();
        }
```

- [ ] **Step 6: Forward `anchorNodeId` in `MeasureTool`**

In `MeasureTool.cs`, change:

```csharp
    public void CommitBoundingBox(IReadOnlyList<Vector3d> worldPoints, BoundingBoxMode mode)
        => _session.CommitBoundingBox(worldPoints, mode);
```

to:

```csharp
    public void CommitBoundingBox(IReadOnlyList<Vector3d> worldPoints, BoundingBoxMode mode, int? anchorNodeId = null)
        => _session.CommitBoundingBox(worldPoints, mode, anchorNodeId);
```

and change:

```csharp
    public void CommitComputedBoundingBox(BoundingBoxMeasurement? measurement)
        => _session.CommitComputedBoundingBox(measurement);
```

to:

```csharp
    public void CommitComputedBoundingBox(BoundingBoxMeasurement? measurement, int? anchorNodeId = null)
        => _session.CommitComputedBoundingBox(measurement, anchorNodeId);
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj" --filter "FullyQualifiedName~Measurement"`
Expected: PASS — new session tests + all pre-existing measurement tests.

- [ ] **Step 8: Commit (parent repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant"
git add src/FabricationAssistant.Core/Measurement/Engine/MeasurementSession.cs src/FabricationAssistant.Core/Measurement/Engine/MeasureTool.cs src/FabricationAssistant.App.Tests/Measurement/MeasurementSessionTests.cs
git commit -m "feat(measure): capture first-body anchor on every commit path

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Android wiring — supply the node-pose lookup and bbox anchor

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs`
- Test: `Android/src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs` (add `[Fact]`)

- [ ] **Step 1: Write the failing source-guard test**

In `GlesRendererSourceGuards.cs`, add this `[Fact]` inside the class:

```csharp
    [Fact]
    public void MeasureIntegration_WiresNodePoseLookup_SoDimensionsFollowBodies()
    {
        string src = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Measurement\AndroidMeasureIntegration.cs"));

        // Lookup resolves each node's live effective transform.
        Assert.Contains("_nodePoseLookup", src);
        Assert.Contains("EffectiveWorldTransform", src);

        // Passed to the session (capture at commit) and the presenter (follow at render).
        Assert.Contains(
            "new MeasurementSession(_store, _units, MeasurementTolerances.Default, undoService, _nodePoseLookup)",
            src);
        Assert.Contains("_nodePoseLookup);", src); // last arg of _presenter.Build(...)

        // Bounding box anchors to the primary movable (leaf) node.
        Assert.Contains("BodyMoveSelection.ResolveMovableNodes", src);
        Assert.Contains("_tool.CommitComputedBoundingBox(boundingBox, primaryNodeId)", src);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run (from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~MeasureIntegration_WiresNodePoseLookup"`
Expected: FAIL — those strings are not yet in the integration source.

- [ ] **Step 3: Add the lookup field and usings**

In `AndroidMeasureIntegration.cs`, ensure these usings are present near the top (add any that are missing):

```csharp
using System.Linq;
using FabricationAssistant.Core.BodyMove;
```

Add the field next to the other readonly fields (after `_units`):

```csharp
    private readonly Func<int, Matrix4d?> _nodePoseLookup;
```

- [ ] **Step 4: Build the lookup and pass it to the session**

In the constructor, replace:

```csharp
        _session = new MeasurementSession(_store, _units, MeasurementTolerances.Default, undoService);
```

with:

```csharp
        _nodePoseLookup = nodeId =>
        {
            Scene? scene = _sceneAccessor();
            return scene?.GetNode(nodeId) is SceneNode node
                ? node.EffectiveWorldTransform
                : (Matrix4d?)null;
        };
        _session = new MeasurementSession(_store, _units, MeasurementTolerances.Default, undoService, _nodePoseLookup);
```

(`_sceneAccessor` is assigned earlier in the constructor, so the lambda captures a valid field.)

- [ ] **Step 5: Pass the lookup to the presenter**

In `BuildPresentation()`, change the `_presenter.Build(...)` call to add the trailing argument:

```csharp
        IReadOnlyList<PresentationSnapshot> snapshots = _presenter.Build(
            _store.Snapshot(),
            _store.SelectedId,
            _session.CurrentDraft,
            _session.ActiveTool,
            _session.HoverPoint,
            _store.HoveredId,
            _showDeltaBreakdown,
            _nodePoseLookup);
```

- [ ] **Step 6: Anchor the bounding box to its primary node**

In `TryCommitBoundingBoxFromSelectionWithIdAsync`, inside the `try` block, just before
`_tool.CommitComputedBoundingBox(boundingBox);`, compute the primary node and pass it:

```csharp
            IReadOnlySet<int> selectionSet = selectedNodeIds as IReadOnlySet<int>
                ?? new HashSet<int>(selectedNodeIds);
            IReadOnlyList<SceneNode> movableNodes =
                BodyMoveSelection.ResolveMovableNodes(sceneAtStart, selectionSet);
            int? primaryNodeId = movableNodes.Count > 0
                ? movableNodes.Min(node => node.Id)
                : (int?)null;

            _tool.CommitComputedBoundingBox(boundingBox, primaryNodeId);
```

(Replace the existing `_tool.CommitComputedBoundingBox(boundingBox);` line with the block above.)

- [ ] **Step 7: Run the source-guard test to verify it passes**

Run (from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj" --filter "FullyQualifiedName~MeasureIntegration_WiresNodePoseLookup"`
Expected: PASS.

- [ ] **Step 8: Commit (Android repo)**

```bash
cd "C:/Users/skritikos/Desktop/Fabrication Assistant/Android"
git add src/FabricationAssistant.App.Android/Measurement/AndroidMeasureIntegration.cs src/FabricationAssistant.App.Android.Tests/GlesRendererSourceGuards.cs
git commit -m "feat(android): dimensions follow their anchor body (wire node-pose lookup)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Full verification

- [ ] **Step 1: Build + run the full Core test suite (parent repo)**

Run: `dotnet test "src/FabricationAssistant.App.Tests/FabricationAssistant.App.Tests.csproj"`
Expected: PASS — entire suite green (confirms no regression in non-measurement areas).

- [ ] **Step 2: Build + run the Android test suite (Android repo)**

Run (from `Android/`): `dotnet test "src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj"`
Expected: PASS — including the new source guard.

- [ ] **Step 3: Device smoke test (manual)**

Deploy to a device/emulator and confirm:
1. Create a **point-to-point** dimension on a body, then drag that body with the move gizmo → the whole dimension follows; the value is unchanged. Move a **different** body → the dimension does not move.
2. Repeat for **face-to-point** and **face-to-face** (the first-picked face's body is the anchor; the line stays perpendicular for face-to-face).
3. Create a **bounding box** on a single body, then move/explode it → the box follows.
4. Drag the **explode** slider → anchored dimensions travel smoothly with their bodies; sliding back to 0 returns them home.
5. **Undo** a body move → dimensions return to the prior position.
6. A point-to-point whose first click was on empty space (no body) stays static (unchanged behaviour).

- [ ] **Step 4: Record the smoke result**

Note pass/fail for each of the six checks in the PR / commit description. If any fail, debug with superpowers:systematic-debugging before claiming completion.

---

## Self-review notes

- **Spec coverage:** rigid-to-first-body (Tasks 1,3,5); all four types incl. bbox (Tasks 3,5,6); follows explode/gizmo/reset/undo (render reads live `EffectiveWorldTransform`, Task 3); leaf-node anchoring (face id in Task 4, point attachment already present); fallbacks for no-body/missing-node/singular/legacy (Task 3 `ApplyAnchor`, Task 5 `WithAnchor`); desktop unaffected (optional params throughout); tests per type (Tasks 3,5) + wiring guard (Task 6) + manual smoke (Task 7).
- **Type consistency:** `MeasurementAnchor(int NodeId, Matrix4d PoseAtCreation)`, `Anchor` init prop, `Build(..., Func<int, Matrix4d?>? nodePoseLookup = null)`, `PickedFace(..., int? NodeId = null)`, `AwaitingPoint(ScenePlane, int? NodeId = null)`, `FaceToFaceDraft(ScenePlane, Vector3d, int? FirstNodeId = null)`, `CommitBoundingBox(..., int? anchorNodeId = null)`, `CommitComputedBoundingBox(..., int? anchorNodeId = null)`, `WithAnchor(MeasurementResult, int?)`, `TransformedBy(Matrix4d)` — names used identically across tasks.
- **No placeholders:** every code/test step shows full content; commands have expected output.
