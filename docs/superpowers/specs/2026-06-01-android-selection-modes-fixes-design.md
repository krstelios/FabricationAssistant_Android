# Android Selection — Assembly/Part/Mesh Modes + Correctness Fixes

Date: 2026-06-01
Status: Approved (scope + design decisions confirmed)

## Goal

Close the gaps from the 2026-06-01 selection review. The centralization layer
(`AndroidModelSelectionResolver` + `MainActivity._selectedNodeIds`) is sound; this
work adds the missing **Assembly** selection mode and fixes the correctness,
consistency, and cleanup issues — without touching `FabricationAssistant.Core`.

## Confirmed decisions

1. Implement the **full 3-way** Assembly / Part / Mesh toggle.
2. **Pragmatic** part grouping: improve the resolver heuristics + tests only. No
   core model or importer changes (no explicit part-id added to `SceneNode`).
3. Assembly mode resolves a pick to the **nearest enclosing assembly**
   (immediate `SceneNodeType.Assembly` ancestor), so nested sub-assemblies stay
   individually selectable.

## Changes

### A. Assembly mode
- `AndroidModelSelectionMode`: add `Assembly = 2`.
- `AndroidModelSelectionResolver.ResolveNode`: after computing `presentedOwner`:
  - `Mesh` → source node (unchanged).
  - `Assembly` → nearest `SceneNodeType.Assembly` ancestor of `presentedOwner`
    (inclusive); if none, fall back to the Part-mode result
    (`FindOwningPart` → `presentedOwner`). `Instance` is NOT treated as an
    assembly (it is an occurrence wrapper).
  - `Part` → `FindOwningPart(presentedOwner) ?? presentedOwner` (unchanged).
- `AppSettings.ModelSelectionMode`: widen clamp upper bound to `Assembly`.
- UI: 3-item popup in `OnSelectionModeClicked`; 3-way `UpdateSelectionModeButtonState`
  (text/contentDescription/selected styling); new strings
  `selection_mode_assembly`, `cd_tool_selection_mode_assembly`,
  `selection_mode_assembly_changed`.
- No downstream tool changes required: explode (`EnumerateCandidates` groups by
  resolved logical id), hide/isolate (subtree via parent-visibility propagation +
  descendant collection), highlight (`ResolveRenderableNodeIds`), Model Explorer
  sync and properties all consume the resolved logical set generically.

### B. Part-grouping hardening (pragmatic)
- Keep `FindOwningPart` (first `SceneNodeType.Part` ancestor) and the
  `ShouldCollapseIntoPresentedOwner` collapse as the fallback path. Add tests that
  pin behavior for assembly-direct multi-body geometry (shared vs distinct
  `SourceKey`), nested assemblies, and the new Assembly resolution.

### C. Mode consistency (F2)
- `SelectModelExplorerNode` and `SelectNodesFromBom` use the active `_selectionMode`
  instead of hardcoded `Part`, so tree/BOM selection follows the toggle.

### D. UX cleanups
- F4: on launch, coerce a persisted `Mesh` mode back to `Part` (Part/Assembly persist).
- Dedup: `PropertiesPanelBinder.ResolvePresentedNode` calls a shared resolver method
  (expose `FindPresentedOwner`) instead of re-implementing the walk.
- Perf: memoize `CountVisibleLogicalNodes` per `(mode, VisibilityVersion)`.

### E. Out of scope
- No `FabricationAssistant.Core` edits. `SceneNode.IsSelected` (dead field) is left
  as-is; the Android selection path verified not to read it.

## Testing
- Unit: extend `AndroidModelSelectionResolverTests` and `AndroidViewportExplodeViewTests`.
- Device: build, deploy to USB tablet, open most-recent file, exercise the three
  modes; confirm via `FA.Selection` / `FA.Measure` logcat + screenshots.

## Acceptance
- Build clean; all Android unit tests green (incl. new ones).
- On device: Assembly/Part/Mesh toggle present and functional; clicking a mesh
  resolves to nearest assembly / owning part / exact mesh respectively; explode,
  hide, isolate operate on the resolved entity.
