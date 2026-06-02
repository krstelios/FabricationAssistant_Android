# Android BOM Consolidated — Excel-Style Column Filters (Visibility-Synced, Undoable)

- **Date:** 2026-06-02
- **Status:** Approved (verbal); pending written-spec review
- **Scope:** `FabricationAssistant.App.Android` (nested repo) for all runtime behaviour, plus a small **additive, optional** extension to the shared `FabricationAssistant.Core` undo framework (no-op for the desktop app).

## Problem

The Android consolidated BOM table (`AndroidBomPanel`, Consolidated mode) has only a single global search box. The user wants Excel-style per-column header filters that also drive 3D part visibility, with a guard so filtering never silently fights a pre-existing hidden state.

## Goal

Every column header of the consolidated BOM gets an Excel-style filter+sort dropdown. Applying filters keeps the **BOM table rows**, the **3D view part visibility**, and the **model-explorer visibility toggles** in lockstep. All filter modifications are undoable.

### Decisions locked in brainstorming

1. **Lockstep filtering.** Active column filters define which consolidated parts "pass." Passing → row shown + part visible; failing → row hidden + part hidden. The model-explorer toggles reflect the same `SceneNode.Visible` state.
2. **Dirty-visibility warning.** The filter owns visibility. While filter ↔ table ↔ view stay in sync, applying/adjusting filters is silent. After any **manual** visibility change (model-explorer toggle, isolate, hide, show-all, or undo/redo of those), visibility is **dirty**; the next filter apply warns once ("N parts are hidden — all parts will be shown first"), performs **Show All**, clears dirty, then applies. No repeat warning until the next manual change.
3. **Filter + sort.** Each dropdown has Sort A→Z / Z→A; a single active sort column reorders the table (view-only; never affects visibility); the header shows ▲/▼.
4. **Undoable.** Every filter modification (a column apply, a sort change, Clear-all, and the Show-All folded into a dirty apply) is a first-class undo entry on the existing `IUndoService` stack, interleaving with manual visibility undo.
5. **Full-dataset value lists.** Each dropdown lists the distinct values for its column from the *entire* consolidated dataset (not narrowed by other active filters), matching the desktop behaviour — so any unchecked value can always be re-checked.

## Background — verified facts

**Android consolidated BOM** (`src/FabricationAssistant.App.Android/AndroidBomPanel.cs`)
- `AndroidBomPanelKind.Consolidated`; rows loaded by `LoadConsolidatedRows()` from `_queryService.GetBomFlat(...)` into `_allRows` (`BomPanelRow`); current global search → `ApplyFilter()` → `_visibleRows` → `BomPanelAdapter`.
- Columns built in `Columns()` as `ColumnSpec(Key, Title, MinWidthDp, MaxWidthDp)`: Part, Name, Rev Name, Rev, Qty (occurrence count), Total, Units, Quantity Types, Reference Sets, Source. Header row built in `CreateHeaderRow()` (header `LinearLayout` of `TextView`).
- `BomPanelRow` exposes PartKey/Key, PartNumber, Name, RevName, RevisionId, OccurrenceCount, TotalQuantity, Units, QuantityTypes, ReferenceSets, SourceType.

**Visibility + scene mapping** (`MainActivity.cs`)
- Source of truth: `SceneNode.Visible`. `AndroidVisibilityStateAccess : IVisibilityStateAccess` (`~16218`) with `SnapshotAll()`, `SnapshotFor(ids)`, `RestoreSnapshot(VisibilityStateSnapshot)`, `GetVisible`/`SetVisible`.
- Mutations capture `CaptureVisibilitySnapshot()` (= `SnapshotAll`) before, mutate, then `FinalizeVisibilityMutation(reason)` (renderer sync + `_modelExplorerPanel.RefreshVisibility()` + overlays + render) and `AddVisibilityUndo(txn, before, description)` → `new VisibilityChange(before, after, desc)`.
- BOM row → node ids: `ResolveBomTargetNodeIds()` (`~3970`) matches `node.Metadata.SourceKey` / `SourceFullPath` against the consolidated part key.
- Manual model-explorer toggle handler: `SetModelExplorerNodeVisibility` (wired via `AndroidModelExplorerPanel.VisibilityChanged`, `~3776`). Isolate/hide live at `~7269–7451`.
- Popup pattern to reuse: `MaterialCardView` + `PopupWindow` anchored dropdown (`MainActivity ~5282`), dark theme in `Resources/values/colors.xml`.

**Undo framework** (`FabricationAssistant.Core/UndoRedo`, used by Android via `MainActivity.cs:488` `new UndoService(new SceneContext(...))`)
- `IUndoableChange`: `Subsystem`, `ApplyBefore(SceneContext)`, `ApplyAfter(SceneContext)`, `Describe()`; snapshot-only, no live scene refs.
- `SceneContext` exposes per-subsystem accessors (`Visibility`, `BodyMove`, `Measurements`, `Sections`, `Selection`); composed at DI time and "touched once per phase" to add accessors.
- `VisibilityChange` restores `node.Visible` via `ctx.Visibility.RestoreSnapshot(snapshot)`. `IUndoTransaction Begin(label)` / `txn.Add(change)` group multiple changes into one undo step.

## Design

### 1. Pure filter engine (host-testable, no Android refs)

**`BomColumnFilterModel`** — one column's filter state:
- `string ColumnKey`; the distinct `Values` (each: raw value, display value where "" → "(blank)", `IsChecked`); `SearchText`.
- `bool Allows(string? value)` using an O(1) `_uncheckedValues` `HashSet<string>` cache (rebuilt on toggle) — `Values.Count == 0 || !_unchecked.Contains(value ?? "")`.
- `SetValues(IEnumerable<string>)` rebuilds the value list from the full dataset, **preserving** previously-unchecked values that still exist.
- `SelectAll()`, `Clear()`, `bool IsActive` (any value unchecked).
- No `ObservableCollection`/`ICollectionView` (those are WPF); plain lists + an `onChanged` callback. (Logic mirrors the proven desktop `BomColumnFilter`, framework-free.)

**`BomConsolidatedFilterEngine`** — aggregates the per-column models for the consolidated table:
- Holds a `BomColumnFilterModel` per filterable column, keyed by column key.
- `SortState` = `(string? ColumnKey, SortDirection Direction)` — single active sort.
- `IReadOnlyList<BomPanelRow> Apply(IReadOnlyList<BomPanelRow> allRows)` → rows passing **all** column filters (AND), then ordered by the active sort (stable; original order when no sort). A `FieldOf(row, columnKey)` map provides the per-column cell value used by both filtering and sorting.
- `IReadOnlySet<string> PassingPartKeys(allRows)` — the part keys of passing rows, for visibility.
- `bool AnyActive` (any column filtering).
- `RebuildValueLists(allRows)` calls each model's `SetValues` from the full dataset.

Both classes live in `src/FabricationAssistant.App.Android/Bom/` and are **linked into** `FabricationAssistant.App.Android.Tests` (the same `<Compile Include>` linking pattern used for `AndroidModelSelectionResolver`) so they unit-test on the net8.0 host.

### 2. Visibility coordination + dirty flag

**`BomFilterVisibilityCoordinator`** — pure decision logic (host-testable):
- Inputs: passing part-key set, a `partKey → nodeIds` map, current `node.Visible` snapshot.
- `bool RequiresShowAllWarning(bool dirty)` → `dirty` (warn iff visibility was manually changed since the filter last owned it).
- `IReadOnlyDictionary<int,bool> TargetVisibility(...)` → for each BOM-part node: visible iff its part passes; leaves non-BOM nodes (assemblies/containers) untouched/visible so passing descendants render.
- Owns no Android types; the MainActivity glue calls it and then applies via `AndroidVisibilityStateAccess` + `FinalizeVisibilityMutation`.

**Dirty flag.** A single `bool _bomFilterVisibilityDirty` in MainActivity:
- Set **true** by every visibility mutation **not** tagged as filter-driven: `SetModelExplorerNodeVisibility`, isolate, hide, show-all button, and `VisibilityChange` undo/redo.
- Set **false** immediately after a filter-driven apply establishes a consistent state.
- Filter-driven mutations run inside a `using (SuppressDirty())` guard so they don't re-set the flag.
- Initial state (parts hidden on load, or after any of the above) ⇒ dirty.

**Apply sequence** (a column dropdown OK / sort / clear-all that changes the passing set):
1. Capture `filterBefore = BomFilter.Snapshot()` and `visBefore = CaptureVisibilitySnapshot()`.
2. If `RequiresShowAllWarning(dirty)` → show `AlertDialog` ("N parts are hidden. Applying a filter will make all parts visible first. Continue / Cancel"). Cancel → revert the dropdown change, abort.
3. (continue) Apply the new filter/sort to the engine; rebuild `_visibleRows`; refresh table + headers.
4. Compute `TargetVisibility`; under `SuppressDirty()` set `node.Visible`, run `FinalizeVisibilityMutation("bom filter")`; set `dirty = false`.
5. Push one undo entry (below) with before/after of filter-state + visibility.

### 3. Undo integration (additive Core + Android)

**Core (additive, optional — desktop unaffected):**
- `IBomFilterStateAccess` (new): `BomFilterStateSnapshot Snapshot()`, `void RestoreSnapshot(BomFilterStateSnapshot)`.
- `BomFilterStateSnapshot` (new, immutable): per-column unchecked-value sets (`IReadOnlyDictionary<string, IReadOnlyList<string>>`), active sort as primitives (`string? SortColumnKey`, `bool SortDescending` — primitives so Core needs no dependency on the Android sort enum), and the `bool Dirty` flag.
- `SceneContext` gains a trailing **optional** ctor param `IBomFilterStateAccess? bomFilter = null` and a `BomFilter` property. The desktop's existing `new SceneContext(...)` call omits it → compiles unchanged, gets `null`.
- `BomFilterChange : IUndoableChange` (`Subsystem => "BomFilter"`): holds `filterBefore/after` (`BomFilterStateSnapshot`) + `visBefore/after` (`VisibilityStateSnapshot`).
  - `ApplyBefore(ctx)` → `ctx.BomFilter?.RestoreSnapshot(filterBefore); ctx.Visibility.RestoreSnapshot(visBefore);`
  - `ApplyAfter(ctx)` → analogous with the `after` snapshots.
  - Restoring filter-state re-renders the table + headers and restores the dirty flag; restoring visibility refreshes 3D + model explorer. The two together keep dropdowns, rows, and visibility consistent on undo/redo.

**Android:**
- `AndroidBomFilterStateAccess : IBomFilterStateAccess` bridges to the live `BomConsolidatedFilterEngine` + panel (`Snapshot` reads engine state + dirty; `RestoreSnapshot` writes them back and triggers a table/header refresh **without** itself mutating visibility — visibility is restored by the change's `Visibility` step).
- Wire it into the Android `SceneContext` composition at `MainActivity.cs:488`.
- Each filter apply/sort/clear records `txn.Add(new BomFilterChange(filterBefore, BomFilter.Snapshot(), visBefore, CaptureVisibilitySnapshot(), describe))` inside a `_undoService.Begin(label)` transaction — so the Show-All + apply collapse into one undo step.

### 4. Android UI

- **Header affordance.** Each consolidated header cell becomes tappable, showing the title + a filter glyph (filled when that column `IsActive`) + a ▲/▼ when it is the sort column. Built where `CreateHeaderRow()` runs.
- **`BomColumnFilterPopup`** (Android): a `MaterialCardView` in a `PopupWindow` anchored under the tapped header, dark-themed. Contents top→bottom: **Sort A→Z / Z→A** buttons; a **search** `EditText` (live-filters the value checklist); **Select all / Clear**; a scrollable **checklist** (`ListView`/adapter) of the column's distinct values with checkboxes; a **Done** button that applies (triggers the apply sequence). Reuses the existing popup/card styling.
- **Panel-level controls.** A **"Clear all filters"** action (resets all column models → passing set = all → Show All) and a small **"X of Y parts"** summary line.

### 5. Data flow summary

- **Filter/sort change →** apply sequence (§2) → table rebuilt from `engine.Apply(_allRows)`, visibility set from `engine.PassingPartKeys`, one `BomFilterChange` recorded.
- **Manual visibility change →** existing path, plus `dirty = true`. Table is **not** force-resynced (it reflects the last filter); the next filter apply reconciles via the warning + Show All.
- **Undo/redo →** `BomFilterChange` restores filter-state + visibility + dirty together; `VisibilityChange` (manual ops) restores visibility and (via the dirty hook on restore) marks dirty.

## Edge cases

- A consolidated part = several occurrence nodes → all toggled together by pass/fail.
- Dropdown value lists come from the full consolidated dataset, independent of current visibility/other filters.
- Clearing all filters = all values checked = passing set is everything = Show All.
- Non-BOM nodes (assemblies/containers, geometry without a BOM row) are never hidden by the filter.
- Switching the BOM panel to Hierarchy mode, or loading a new scene, resets filter state (engine rebuilt from new rows); dirty recomputed from the new visibility.
- Empty passing set (all values unchecked in some column) → table empty + all BOM parts hidden; "Clear all filters" recovers.

## File structure

**Core (parent repo — additive, optional, desktop gets no-op):**
- `src/FabricationAssistant.Core/UndoRedo/IBomFilterStateAccess.cs` (new)
- `src/FabricationAssistant.Core/UndoRedo/BomFilterStateSnapshot.cs` (new)
- `src/FabricationAssistant.Core/UndoRedo/BomFilterChange.cs` (new)
- `src/FabricationAssistant.Core/UndoRedo/SceneContext.cs` (add optional `BomFilter` accessor)
- `SortDirection` enum (Ascending/Descending) lives **Android-side** in the `Bom` namespace (engine-local); the Core snapshot uses primitives, so no Core↔enum coupling.

**Android (nested repo):**
- `src/FabricationAssistant.App.Android/Bom/BomColumnFilterModel.cs` (new, pure)
- `src/FabricationAssistant.App.Android/Bom/BomConsolidatedFilterEngine.cs` (new, pure)
- `src/FabricationAssistant.App.Android/Bom/BomFilterVisibilityCoordinator.cs` (new, pure)
- `src/FabricationAssistant.App.Android/Bom/BomColumnFilterPopup.cs` (new, Android view)
- `src/FabricationAssistant.App.Android/Bom/AndroidBomFilterStateAccess.cs` (new, bridge)
- `src/FabricationAssistant.App.Android/AndroidBomPanel.cs` (edit: header affordances, hold engine, rebuild rows from engine, raise apply)
- `src/FabricationAssistant.App.Android/MainActivity.cs` (edit: dirty flag + hooks, apply sequence + warning dialog, BOM→node map, SceneContext wiring, undo recording)
- `src/FabricationAssistant.App.Android.Tests/*` (link the 3 pure files; add unit tests + a source guard)

## Testing

**Host unit tests** (net8.0):
- `BomColumnFilterModel`: `Allows` with checked/unchecked, unchecked-set O(1) cache, `SetValues` preserves unchecked across rebuilds, SelectAll/Clear, IsActive.
- `BomConsolidatedFilterEngine`: AND across columns, sort A→Z/Z→A stability, `PassingPartKeys`, full-dataset value lists.
- `BomFilterVisibilityCoordinator`: `RequiresShowAllWarning` (dirty transitions), `TargetVisibility` (multi-occurrence parts toggle together; non-BOM nodes untouched).
- `BomFilterChange` round-trip: ApplyBefore/ApplyAfter restore filter-state + visibility + dirty via a fake `SceneContext` (fake `IBomFilterStateAccess` + `IVisibilityStateAccess`).

**Source guard:** assert `AndroidBomPanel`/`MainActivity` wire the engine, the dirty flag hooks, and the `BomFilterChange` recording.

**Manual device smoke:** filter a column → table + 3D + model explorer update; manual-hide then filter → warning → Show All → applies; sort ▲/▼; Clear all → all visible; **undo** a filter apply restores prior selections + prior (manual) visibility; redo re-applies.

## Non-goals

- Desktop app behaviour (Core additions are inert there — no-op accessor).
- Multi-column simultaneous sort (single active sort column).
- Hierarchy-mode BOM filters (consolidated only).
- Narrowing one column's value list by other columns' active filters (full-dataset lists, matching desktop).

## Open questions

- Spec assumes filters **apply on the dropdown's "Done"/close** (one undo entry per apply, a clean warning point) rather than live-per-toggle. Flag if you'd prefer live application.
