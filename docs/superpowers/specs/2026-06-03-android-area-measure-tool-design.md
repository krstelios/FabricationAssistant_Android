# Area Measurement Tool — Design

**Date:** 2026-06-03
**Status:** Approved (brainstorming) → ready for implementation plan
**Author:** skritikos + Claude

## 1. Goal

Add a new **Area** measurement tool to the Android viewer. The user taps a face of a
3D model; the tool detects the coplanar planar region (reusing the existing
face-detection logic), computes its **surface area** and **perimeter**, and shows a
persistent billboard label like the other measurement tools. The area is reported in a
unit the user selects in **Settings → Measurement** (default **m²**); a companion
**Length unit** selector (default **mm**) controls how the perimeter and the existing
distance tools print.

### Non-goals (YAGNI)
- No whole-curved-surface summation (a cylinder side is *not* unwrapped — only the
  near-coplanar patch around the tap is measured; documented behaviour, not a bug).
- No multi-face accumulation into one running total (each tap = one independent area
  measurement, following the existing multi-measure setting).
- No change to how source geometry units are interpreted. **Source geometry is always
  in millimetres**; the unit pickers affect *only the printed label text*, never stored
  or computed values.

## 2. Key decisions (from brainstorming)

| Decision | Choice |
|---|---|
| Units setting | **Two independent pickers**: Length unit (default mm) and Area unit (default m²) |
| Unit semantics | Pickers affect label printing only; canonical storage unchanged; source always mm |
| Face scope | **Planar face region** — reuse `FaceDetectionService.DetectFromSeed` coplanar patch |
| Label content | **Area + perimeter**, e.g. `1.23 m²  ·  P 4.50 m` |
| Commit model | **Single face tap commits immediately** (no multi-step draft), undoable |
| Hover behaviour | Live preview: hovered face highlights + shows a tentative area/perimeter label |

## 3. Architecture context

The measurement domain/engine/presentation lives in the **shared Core**
(`src/FabricationAssistant.Core/Measurement/…`, the *parent* repo). The Android app
compiles it via the `FabricationAssistant.Core.Android` shim
(`<Compile Include="…\src\FabricationAssistant.Core\**\*.cs">`). Therefore this feature
spans **two git repositories**:

- **Parent repo** (`Fabrication Assistant/`): Core domain/engine/presentation changes.
- **Android nested repo** (`Fabrication Assistant/Android/`): toolbar, settings UI,
  `AppSettings`, wire-up. (Per project memory: Android/ is its own git repo, gitignored
  by the parent; commit from inside each repo by explicit path.)

The build (`build.cmd` / solution) compiles both, so a Core change that breaks any other
Core consumer (e.g. a desktop app or Core test project) will fail the build. The plan
must grep every exhaustive `switch` over `MeasureToolMode` / `MeasurementResult` and
handle the new `Area` case everywhere.

## 4. Data flow (one tap)

```
tap → AndroidMeasureRaycaster.Raycast()            → MeasureRaycastHit (node, mesh, seed triangle, world point)
    → MeshMeasurePicker.Pick(Face)                  → FaceDetectionService.DetectFromSeed() → FacePatch
                                                       → PickedFace(Plane, ClickWorld, Highlight=world-space patch tris, NodeId)
    → MeasurementSession.SubmitPick → AdvanceArea   → MeasurementBuilders.BuildArea(plane, worldTris, metersPerSceneUnit)
                                                       → AreaMeasurement (committed under undo txn)
    → MeasurementPresenter (Area case)              → FaceHighlight + outline lines + centroid LabelPrimitive("1.23 m² · P 4.50 m")
    → MainActivity.MeasurementLabelLayer            → TextView positioned each frame via TryProjectWorldToViewport
```

The picker already produces `PickedFace.Highlight` whose `WorldVertices` are the
patch's triangles in **world space** (3 verts per triangle). Area/perimeter are computed
from those, so the picker needs no new plumbing.

## 5. Shared Core changes (`src/FabricationAssistant.Core/`)

### 5.1 `Domain/MeasureToolMode.cs`
Add `Area` to the enum.

### 5.2 `Domain/SceneArea.cs` (new)
Mirror of `SceneLength`. Canonical storage in **square metres**.

```csharp
public readonly record struct SceneArea(double SquareMeters)
{
    public static SceneArea FromSceneUnits(double sceneAreaUnits, double metersPerSceneUnit)
        => new(sceneAreaUnits * metersPerSceneUnit * metersPerSceneUnit); // factor squared

    public double ConvertTo(UnitSystem unit) => unit switch
    {
        UnitSystem.Millimeters => SquareMeters * 1_000_000.0,
        UnitSystem.Centimeters => SquareMeters * 10_000.0,
        UnitSystem.Meters      => SquareMeters,
        UnitSystem.Inches      => SquareMeters / (0.0254 * 0.0254),
        UnitSystem.Feet        => SquareMeters / (0.3048 * 0.3048),
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };
}
```
`FromSceneUnits` validates `metersPerSceneUnit` exactly as `SceneLength` does.

### 5.3 `Domain/Measurement.cs` — new `AreaMeasurement`
```csharp
public sealed record AreaMeasurement(
    MeasurementId Id,
    ScenePlane Plane,                          // face plane: centroid (label anchor), normal, basis
    SceneArea Area,
    SceneLength Perimeter,
    IReadOnlyList<Vector3d> FaceTriangleVertices, // world-space, 3 per triangle, captured at pick
    int? NodeId = null,
    bool IsVisible = true) : MeasurementResult(Id, IsVisible)
{
    public override MeasureToolMode Mode => MeasureToolMode.Area;
}
```
Notes:
- Domain stays free of the Presentation `FaceHighlight` type — it stores raw
  `Vector3d` vertices. The presenter rebuilds a `FaceHighlight` from them.
- `FaceTriangleVertices` carry the highlight fill **and** the source for perimeter
  re-derivation; they are captured in world space at pick time and re-transformed by the
  body-anchor delta in the presenter so the highlight follows body moves
  (same mechanism as the other tools’ `BodyAnchor`).

### 5.4 `Engine/MeasurementBuilders.cs` — `BuildArea`
```csharp
public static AreaMeasurement BuildArea(
    ScenePlane plane,
    IReadOnlyList<Vector3d> worldTriangleVertices, // 3 per triangle
    int? nodeId,
    double metersPerSceneUnit)
```
- **Area** = Σ over each triangle `0.5 * |(v1−v0) × (v2−v0)|` (true 3-D triangle area,
  in world/scene units) → `SceneArea.FromSceneUnits(area, metersPerSceneUnit)`.
- **Perimeter** = sum of **boundary edge** lengths. An edge is boundary iff it belongs
  to exactly one triangle in the patch. Edge identity uses **welded vertices** — quantise
  positions to the same weld tolerance `FaceDetectionService` uses
  (`max(sceneDiagonal·1e-5, 1e-6)`) so glTF’s per-triangle duplicated vertices collapse.
  Sum lengths in scene units → `SceneLength.FromSceneUnits(perimeter, metersPerSceneUnit)`.
  (Holes, if any, contribute their boundary too — acceptable.)
- Degenerate/empty input → zero area & perimeter (safe).

### 5.5 `Engine/MeasureTool.cs`
In `Area` mode the pick kind is `Face` (same path as FaceToPoint/FaceToFace first click).

### 5.6 `Engine/MeasurementSession.cs` — `AdvanceArea`
A single `PickedFace` **commits immediately** (no draft state):
- Extract world-space patch triangles from `PickedFace.Highlight.WorldVertices`.
- `m = MeasurementBuilders.BuildArea(plane, worldTris, nodeId, metersPerSceneUnit)`.
- Stamp body anchor (`WithAnchor`) like the other builders.
- `using var txn = _undoService.Begin("Add Area measurement"); _store.Add(m); txn.Add(new MeasurementAddChange(m));`
- Record the selected-face highlight in the session’s selected-faces list so the overlay
  shows it (parity with FaceToFace selection highlight).
- Respect the existing single-vs-multi measure mode.

### 5.7 `Presentation/MeasurementPresenter.cs` — `Area` case
- Anchor = `Plane.Origin` (patch centroid).
- Label text = `$"{_units.FormatArea(m.Area)}  ·  P {_units.Format(m.Perimeter)}"`,
  `LabelAlignment.Billboard`.
- Emit a `FaceHighlight(transformedVerts, Selected)` for the fill, plus `LinePrimitive`s
  for the boundary outline (optional but matches the “like the rest” outline feel).
- Apply the body-anchor delta (`currentPose × PoseAtCreation⁻¹`) to the centroid and to
  every face vertex so the highlight + label follow body moves/explode.

### 5.8 `Engine/SceneUnitSystemService.cs` + `Engine/IUnitSystemService.cs`
- Add `UnitSystem AreaDisplayUnit { get; set; } = UnitSystem.Meters;`
- Add `string FormatArea(SceneArea area, int decimals = 2)` → value via
  `area.ConvertTo(AreaDisplayUnit)`, suffix from `{mm²,cm²,m²,in²,ft²}`.
- `DisplayUnit` (length) is unchanged in type but is now **driven by a setting** instead
  of the hardcoded `Millimeters`. Source-always-mm means `MetersPerSceneUnit` stays
  `0.001`; the pickers only change printed suffix/scale.

## 6. Android app changes (`Android/src/FabricationAssistant.App.Android/`)

### 6.1 Toolbar (`MainActivity.cs` + layout XML)
- Add a `measureArea` `MaterialButton` to the Measure bottom toolbar (new icon, e.g.
  an area/polygon glyph), next to Point/Face-Point/Face-Face/Bounding Box.
- `FindViewById` it; wire `Click += OnMeasureAreaClicked`.
- `private void OnMeasureAreaClicked(object? s, EventArgs e) => SetMeasureMode(MeasureToolMode.Area);`
- Include `Area` in `IsInteractiveMeasureMode(...)` and any active-tool bookkeeping so
  the mode activates/toggles like the others.

### 6.2 Settings (`PreferencesBottomSheet.cs`, "Measurement Tools" section)
- Add two unit selectors. Because each has **5 options** (mm/cm/m/in/ft and the squared
  variants), use a dropdown/spinner control (cleaner than a 5-way segmented toggle); a
  toggle row is the fallback if a spinner helper isn’t readily available.
  - "Length unit" → `AppSettings.MeasurementLengthUnit` (default mm)
  - "Area unit" → `AppSettings.MeasurementAreaUnit` (default m²)
- Each setter calls the existing `Edit()/Apply()` + `NotifySettingsChanged()` path.

### 6.3 `AppSettings.cs`
- Add `MeasurementLengthUnit` (persisted int enum, default `UnitSystem.Millimeters`).
- Add `MeasurementAreaUnit` (persisted int enum, default `UnitSystem.Meters`).
- Use the existing `GetIntInRange` / `Put` clamp pattern (range 0..4).
- Bump `SettingsSchemaVersion` 22 → 23; add the corresponding `MigrateDefaultsIfNeeded`
  entry (no legacy key to remove — just the version bump so the migration test pattern
  holds).

### 6.4 Wire-up (`MainActivity.cs`)
- In the settings-applied path (e.g. `ApplySettingsToScene` / measurement-settings apply)
  and on scene attach, push:
  - `_measure.Units.DisplayUnit = AppSettings.MeasurementLengthUnit`
  - `_measure.Units.AreaDisplayUnit = AppSettings.MeasurementAreaUnit`
  then refresh measurement labels so existing labels re-print live.
- Expose the `SceneUnitSystemService` from `AndroidMeasureIntegration` if not already
  reachable (it currently holds a private `_units`).

## 7. Edge cases

- **Curved surface**: only the near-coplanar patch is measured. Acceptable & documented.
- **Detection miss / isolated seed triangle**: fall back to the single seed triangle —
  area of that triangle, perimeter = its 3 edges.
- **Body moved / exploded**: area & perimeter are intrinsic (unchanged); label + face
  highlight follow via `MeasurementAnchor`.
- **Non-finite / degenerate geometry**: guarded to zero; never throws into the render loop.
- **Unit change while labels exist**: re-format on `OnSettingsChanged`, no re-pick needed.

## 8. Testing (xUnit, matching existing test style)

- `SceneAreaTests`: `FromSceneUnits` squares the factor; `ConvertTo` for all 5 units;
  mm-source round-trips (1e6 mm² == 1 m²).
- `MeasurementBuildersAreaTests`: unit square (1×1) → area 1, perimeter 4; a two-triangle
  quad → same; an L-shape / patch with an interior shared edge → interior edge excluded
  from perimeter; welded duplicate vertices don’t double-count edges.
- `MeasurementPresenterAreaTests`: `AreaMeasurement` yields one billboard label with the
  `m² · P` text and a face highlight; follows body anchor transform.
- `AppSettings` tests: length/area unit round-trip + clamp; schema 22→23 migration keeps
  existing keys (extend `AppSettingsMigrationSourceTests` pattern).
- Then: full build + on-device smoke (tap a face → label appears; change units in
  settings → label re-prints; undo removes it), per the usual loop.

## 9. Files touched (summary)

**Parent repo (Core):**
- `Domain/MeasureToolMode.cs` (edit), `Domain/SceneArea.cs` (new),
  `Domain/Measurement.cs` (edit), `Engine/MeasurementBuilders.cs` (edit),
  `Engine/MeasureTool.cs` (edit), `Engine/MeasurementSession.cs` (edit),
  `Engine/SceneUnitSystemService.cs` (edit), `Engine/IUnitSystemService.cs` (edit),
  `Presentation/MeasurementPresenter.cs` (edit), + any exhaustive-switch consumers.

**Android repo:**
- `MainActivity.cs` (toolbar button, handler, IsInteractiveMeasureMode, wire-up),
  Measure toolbar layout XML + new icon drawable,
  `PreferencesBottomSheet.cs` (two selectors),
  `AppSettings.cs` (two settings + schema bump + migration),
  `Measurement/AndroidMeasureIntegration.cs` (expose units; selected-face highlight),
  test files under `FabricationAssistant.App.Android.Tests` (+ Core test project if present).

## 10. Open implementation details (resolve in plan, not blockers)

- Exact spinner vs toggle control for 5-option unit pickers in `PreferencesBottomSheet`.
- Confirm `MeshMeasurePicker` always sets `PickedFace.Highlight` (it does today); guard
  for null → degenerate area.
- Whether to draw the boundary outline `LinePrimitive`s or rely on the translucent fill
  alone (cosmetic).
- Icon asset for the toolbar button.
