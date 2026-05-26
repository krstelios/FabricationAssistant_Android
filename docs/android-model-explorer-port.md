# Android Model Explorer Port Notes

## Windows behavior

The Windows Model Explorer is owned by `MainViewModel` and bound to a
`TreeView` over `RootNode.Children`, where each row is a
`SceneNodeViewModel`. The view model wraps real `SceneNode` instances and can
also create virtual grouping folders when duplicate sibling names are packed.

The tree is not a direct visual copy of `Scene.NodesById`. It projects the
runtime scene graph through the same rules used by Windows:

- importer-generated helper nodes with labels such as `Node 12` and no source
  name are hidden from the explorer;
- descendants that belong to the same source key as their visible owner are
  projected under that owner instead of shown as separate helper rows;
- every raw scene node ID is mapped to a presented tree node ID so viewport
  selection can select the user-visible row even when the picked mesh belonged
  to a hidden helper node;
- optional duplicate packing creates virtual folder rows that propagate
  visibility to their descendant real scene nodes.

Selection is synchronized through `SelectedTreeNode` and `SelectionState`.
Tree selection uses a guard (`_isSyncingSelection`) to avoid feedback loops.
Viewport selection resolves the picked scene node to a presented node ID,
expands the ancestor path, and asks `TreeViewSelectionCoordinator` to select and
scroll the realized row.

Visibility uses two paths. For `.fa` packages, Windows resolves tree rows to
stable occurrence IDs and mutates `PackageSessionState.HiddenOccurrenceIds`.
For GLB/glTF/native scene nodes, Windows resolves the presented row to the real
scene node IDs in its visual group, then sets `SceneNode.Visible`. In both
paths the renderer is refreshed from the scene after the mutation.

## Android mapping

Android keeps the same runtime `Scene` and `SceneNode` graph, the same
`PackageSessionState`, and the same renderer-facing `SceneNode.Visible`
contract. The Android port therefore mirrors the Windows explorer with:

- an Android tree model that builds the same projected hierarchy and
  raw-node-to-presented-node lookup;
- a slide-out left-pane panel using the existing Android side-panel shell;
- `ListView` row recycling for large models;
- tree row visibility buttons that call the same Android visibility primitives
  already used by Hide/Show/Isolate;
- selection synchronization from viewport to explorer via presented node IDs,
  and from explorer to viewport through actual scene node IDs or `.fa`
  occurrence IDs.

PDF, markup, and drawing commands are intentionally out of scope for this
Android implementation pass.
