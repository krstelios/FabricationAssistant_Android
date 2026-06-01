# Android Selection Architecture

## Goal

Android selection is resolved through one model-selection layer instead of each
tool deciding independently whether a picked render node is a mesh, part, or
assembly. The default user workflow is logical Part selection; Assembly mode
selects whole (sub)assemblies; Mesh selection is an explicit detail/debug mode.
A toolbar toggle next to the Select tool switches between the three modes.

## Selection Modes

- Part mode is the default. A viewport pick on any mesh resolves to its nearest
  owning `SceneNodeType.Part` in the Model Explorer hierarchy. Renderer
  highlight and commands then expand that logical node to all renderable mesh
  descendants.
- Assembly mode resolves a viewport pick to the **nearest enclosing**
  `SceneNodeType.Assembly` ancestor (so nested sub-assemblies stay individually
  selectable). `Instance` nodes are occurrence wrappers, not assemblies, and are
  skipped; when no assembly ancestor exists the resolver falls back to the Part
  result. Highlight, explode, hide, and isolate then operate on the full
  assembly subtree via the same renderable-node expansion used for parts.
- Mesh mode resolves a viewport pick to the exact source render node. The Model
  Explorer still presents the logical hierarchy; mesh mode is not allowed to
  make raw render meshes normal business objects in the tree. Mesh mode is never
  resumed on launch (a persisted Mesh mode is coerced back to Part); Part and
  Assembly persist across sessions.

## Source Of Truth

The Model Explorer hierarchy is the logical model structure. The central
resolver maps:

- render mesh/source node id -> logical selection node id
- logical selection node id -> renderable mesh node ids

Tools consume the resolved logical selection set and ask the resolver for the
renderable node ids only when they need renderer, bounds, move, or geometry
targets.

## Current Android Integration

- Viewport click and hover use source node ids from the GPU scene and resolve
  through `AndroidModelSelectionResolver`.
- Renderer selected/hovered mesh indices are derived from resolved renderable
  node ids.
- Model Explorer synchronization receives logical selection ids and highlights
  the presented logical node.
- Hide/show/isolate, properties, body move, bounding-box measurement, zoom, and
  context actions continue to read the central `_selectedNodeIds` set.
- Explode builds logical candidates in Part mode, so multiple mesh nodes under
  one part share one explode vector while still applying transforms to each
  render node.
