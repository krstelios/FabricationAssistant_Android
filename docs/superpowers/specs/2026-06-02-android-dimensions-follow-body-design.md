# Dimensions Follow Their Anchor Body — Design

- **Date:** 2026-06-02
- **Status:** Approved (verbal); pending written-spec review
- **Scope:** Shared `FabricationAssistant.Core` (parent repo) + `FabricationAssistant.App.Android` (nested repo)

## Problem

A measurement (dimension) is created from the world-space positions captured at click
time and never updated. When the user later moves the body those positions were taken
from — via the body-move gizmo or the explode slider — the dimension stays floating in
its original location instead of travelling with the geometry. The user wants a
dimension to **follow the body it was built from**.

## Goal

Each newly created dimension is rigidly welded to the **first body selected when it was
created**. Moving that body (explode, gizmo move, reset, undo/redo) moves and rotates the
entire dimension with it. The dimension keeps its shape and its measured value.

### What "rigid to the first body" means (confirmed in brainstorming)

- The **whole** dimension — both ends, the line, the disks, the label — is parented to
  the **first** selected body. The second body (if any) is irrelevant to following.
- Move the first body → the entire dimension moves/rotates with it; the measured value
  stays frozen.
- Move any **other** body → the dimension does **not** change.
- This is the geometrically clean choice for face-to-face: the line stays perpendicular
  to the faces because the whole construction translates/rotates as one rigid object.

### Applies to all four dimension types

Point-to-point, face-to-point, face-to-face, **and** bounding box. (Bounding box welds
to its **primary** source body — see Bounding box below.)

## Non-goals

- **Cross-session persistence of following.** Node ids are not guaranteed stable across
  save/reload or re-import; following is an in-session behaviour. If measurements are
  serialized, the anchor may ride along, but re-establishing it across sessions is out of
  scope here.
- **`AnnotationMeasurement`** (free-text pin) — not one of the four dimension types in
  scope.
- **Desktop app wiring.** The Core changes are additive and backward-compatible; the
  desktop presenter/session simply pass no lookup and behave exactly as today. Desktop can
  opt in later by supplying the same lookup.

## Background — how the system works today (verified)

- **Measurements store absolute world positions baked at creation.** The presenter reads
  them directly each frame, which is why they don't follow.
  (`src/FabricationAssistant.Core/Measurement/Domain/Measurement.cs`,
  `.../Presentation/MeasurementPresenter.cs`.)
- **Bodies are `SceneNode`s** with
  `EffectiveWorldTransform = MoveTransform * TransientTransform * WorldTransform`
  (`SceneNode.cs:161`). `WorldTransform` is the loaded ancestor-chain product; `Move`/
  `Transient` are per-node overrides and are **not** folded into a child's `WorldTransform`.
- **Moves always land on leaf mesh nodes.** `BodyMoveSelection.ResolveMovableNodes`
  expands any Assembly/Part/Mesh selection to its **mesh-bearing leaf descendants** and the
  move is applied per leaf (`BodyMove/BodyMoveSelection.cs:17`). Explode sets
  `TransientTransform` on visible leaf nodes (`AndroidViewportExplodeView.cs`). Body-move
  gizmo calls `_bodyMove.SetMove(leafNodeId, …)` per leaf (`MainActivity.cs:10020/10037`).
- **Picks resolve to leaf mesh nodes.** A pick yields `h.NodeId` = the leaf mesh's source
  node (`MeshMeasurePicker.Pick`). Therefore **the leaf node a pick resolves to is exactly
  the node whose `EffectiveWorldTransform` changes when its body is moved.** Anchoring to
  that leaf is correct for every move pathway and every selection granularity.
- **Point picks already capture their body + local coords.**
  `MeshMeasurePicker.Pick` builds `SceneAttachment(nodeId, inverse(nodeWorld)·worldPoint)`
  on the `ScenePoint` (lines 86–94). `nodeWorld` is `EffectiveWorldTransform`
  (`AndroidMeasureRaycaster.cs:72`).
- **Face picks drop the node id.** `PickResult.PickedFace(Plane, ClickWorld, Highlight)`
  knows `h.NodeId` locally but does not propagate it (`PickTypes.cs:25`). Planes are stored
  in world space.
- **`Matrix4d`** provides `TryInvert`/`Inverted`, `TransformPoint`, `TransformDirection`,
  and `operator *` (row-major, pre-multiplication, `v' = M·v`).

## Why a render-time delta is correct (and rigid)

For a point baked at creation: `world_baked = creationPose · local`.
After a move: `world_now = currentPose · local = (currentPose · creationPose⁻¹) · world_baked = delta · world_baked`.

Because the loaded `WorldTransform` is **identical** at creation and at render (only
`Move`/`Transient` change between the two), it cancels in the delta:

```
delta = currentPose · creationPose⁻¹
      = (Move_now · Trans_now · World) · (Move_c · Trans_c · World)⁻¹
      = Move_now · Trans_now · Trans_c⁻¹ · Move_c⁻¹
```

`Move` and `Transient` are always rigid (translation + rotation: explode is pure
translation; the gizmo and SpaceMouse are translation+rotation). So **`delta` is a pure
rigid motion with no scale**, even if `WorldTransform` bakes a unit scale (e.g. mm→m).
Consequently directions (plane normals, U axes, box axes) transform correctly with
`TransformDirection`, and all lengths/values are preserved without recomputation.

> Assumption: `MoveTransform` and `TransientTransform` remain rigid. If a non-uniform-scale
> move is ever introduced, normals would need inverse-transpose handling. Noted, not handled.

## Design

### 1. Data model — `Measurement.cs` (Core)

```csharp
public readonly record struct MeasurementAnchor(int NodeId, Matrix4d PoseAtCreation);

public abstract record MeasurementResult(MeasurementId Id, bool IsVisible)
{
    public abstract MeasureToolMode Mode { get; }

    /// First-body anchor. Null = legacy/static (no following). Set via `with`.
    public MeasurementAnchor? Anchor { get; init; }
}
```

Init-only property on the base record → no positional-constructor changes, no derived-type
churn, fully backward-compatible. Stamped with `measurement with { Anchor = … }`.

### 2. Capture the anchor at creation

**Propagate the leaf node id onto face picks/drafts (Core):**

- `PickResult.PickedFace`: add `int? NodeId` (default `null`). `MeshMeasurePicker.Pick`
  sets it from `h.NodeId`.
- `FaceToPointDraft.AwaitingPoint`: add `int? NodeId` (the face's node).
- `FaceToFaceDraft`: add `int? FirstNodeId` (face A's node).
- Point picks need nothing new — node id is in `ScenePoint.Attachment.NodeId`.

**Capture pose in `MeasurementSession` (Core):**

- New optional ctor param `Func<int, Matrix4d?>? nodePoseLookup = null`.
- A private helper resolves the **first pick's** node id per tool:
  - PtP: `draft.A.Attachment?.NodeId`
  - FtP (point-first): `awaitingFace.Point.Attachment?.NodeId`
  - FtP (face-first): `awaitingPoint.NodeId`
  - F2F: `draft.FirstNodeId`
- On each commit, if the lookup is present and the node id resolves to a pose, stamp
  `m = m with { Anchor = new MeasurementAnchor(nodeId, pose) }` before `_store.Add(m)`.
  When the node id is absent or the lookup returns null → no anchor (static).

**Bounding box:** `CommitBoundingBox` / `CommitComputedBoundingBox` gain an optional
`int? anchorNodeId`. The Android integration passes the **primary** source node, defined
deterministically as the **lowest node id among the resolved movable (leaf) nodes** of the
selection (`BodyMoveSelection.ResolveMovableNodes(...)`); the session stamps the anchor the
same way. Multi-body boxes weld to that primary node and move rigidly with it (they do not
re-fit when other bodies move — consistent with the rigid model). Single-body boxes (the
common case) follow exactly.

### 3. Follow at render — `MeasurementPresenter` (Core)

- `Build(...)` gains an optional trailing `Func<int, Matrix4d?>? nodePoseLookup = null`.
- New helper on the snapshot:
  `PresentationSnapshot TransformedBy(Matrix4d delta)` — maps every primitive:
  - `BallPrimitive.Center`, `LinePrimitive.Start/End`, `DiskPrimitive.Center`,
    `LabelPrimitive.Anchor` → `delta.TransformPoint(...)`
  - `DiskPrimitive.Normal`, `DiskPrimitive.U`, and `LabelAlignment.LineAligned`
    `LineDirection`/`FacingHint` → `delta.TransformDirection(...)`
- Per committed measurement:

```csharp
var snap = BuildFor(m, style, showDeltas);
if (m.Anchor is { } a
    && nodePoseLookup?.Invoke(a.NodeId) is { } current
    && a.PoseAtCreation.TryInvert(out var inv))
{
    var delta = current * inv;
    if (delta != Matrix4d.Identity) snap = snap.TransformedBy(delta);
}
snapshots.Add(snap);
```

Singular creation pose (`TryInvert` false) or missing node → render static (no transform).
Per-frame cost: one matrix invert + multiply + a handful of vec transforms per measurement
— negligible (measurements number in the tens).

### 4. Android wiring — `AndroidMeasureIntegration`

- Build one lookup `id => _scene?.GetNode(id)?.EffectiveWorldTransform` (scene already
  accessible here) and pass it to **both** the `MeasurementSession` ctor (capture) and
  `_presenter.Build(...)` in `BuildPresentation()` (render).
- For bounding-box commits, pass the primary selected source node id as `anchorNodeId`.

### 5. Edge cases / fallbacks

| Case | Behaviour |
|------|-----------|
| First pick not on a body (supplemental/free-space snap) | No anchor → static (today's behaviour) |
| Anchored node deleted/hidden after scene reload | Lookup returns null → static |
| Singular creation pose | `TryInvert` false → static |
| Pre-existing measurement (`Anchor == null`) | Static — only new measurements follow |
| Desktop (no lookup passed) | Identical to today |

## File-by-file changes

**Core (parent repo — `Fabrication Assistant/src/FabricationAssistant.Core`)**
- `Measurement/Domain/Measurement.cs` — add `MeasurementAnchor` struct + `Anchor` init prop.
- `Measurement/Engine/PickTypes.cs` — add `int? NodeId` to `PickedFace`.
- `Measurement/Domain/MeasurementDraft.cs` — add node ids to `AwaitingPoint`, `FaceToFaceDraft`.
- `Measurement/Engine/MeshMeasurePicker.cs` — set `NodeId` on the `PickedFace` result.
- `Measurement/Engine/MeasurementSession.cs` — `nodePoseLookup` ctor param; first-node
  resolution; stamp `Anchor` on every commit path (PtP/FtP/F2F/Bbox).
- `Measurement/Presentation/MeasurementPresenter.cs` — `nodePoseLookup` param; apply delta;
  add `TransformedBy` helper (on `PresentationSnapshot`, likely in `Primitives.cs`).

**Android (nested repo — `Android/src/FabricationAssistant.App.Android`)**
- `Measurement/AndroidMeasureIntegration.cs` — construct the lookup; pass to session +
  presenter; pass primary node id on bbox commit.

> Two commits across two repos (Core in parent, glue in `Android/`), per repo topology.

## Testing

**Core unit tests (`FabricationAssistant.App.Tests` / Core test project)**
- For each of the 4 types: with an `Anchor`, a translated node pose moves all primitives by
  the same translation; a rotated pose rotates them about the node; the measured value/label
  is unchanged.
- `Anchor == null` → snapshot identical to today.
- Lookup returns null / singular pose → snapshot identical to today (static).
- `MeasurementSession`: committing each type with a lookup stamps the expected
  `Anchor.NodeId`; first-pick resolution picks the correct body for face-first vs point-first
  face-to-point.

**Android integration test (`FabricationAssistant.App.Android.Tests`)**
- Create each dimension type on a body; apply a `MoveTransform`/`TransientTransform` to that
  leaf; assert the presented primitives follow. Move a **different** body; assert no change.

## Acceptance criteria

1. Creating any of the four dimension types and then moving (gizmo) or exploding the first
   selected body moves/rotates the whole dimension with it; the value is unchanged.
2. Moving a different body leaves the dimension untouched.
3. Reset/undo of a move returns the dimension to its original place.
4. A dimension whose first pick wasn't on a body, and any pre-existing dimension, behave as
   today (static).
5. Desktop behaviour is unchanged. All existing tests stay green; new tests cover the above.

## Open questions

- **Measurement persistence:** confirm whether measurements are serialized (FA Cloud /
  project save). If so, decide whether to serialize `Anchor` now or leave it transient
  (cross-session following is a non-goal regardless).
