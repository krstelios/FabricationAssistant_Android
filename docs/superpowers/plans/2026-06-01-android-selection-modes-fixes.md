# Android Selection Modes + Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline). Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add a 3-way Assembly/Part/Mesh selection toggle and fix the consistency/UX/cleanup bugs from the 2026-06-01 review, without touching `FabricationAssistant.Core`.

**Architecture:** All selection already flows through `AndroidModelSelectionResolver` (mesh→logical, logical→renderable) and the single `MainActivity._selectedNodeIds` set. We add an `Assembly` resolution branch (nearest `SceneNodeType.Assembly` ancestor) and wire the UI; downstream tools consume the resolved set unchanged.

**Tech Stack:** .NET Android (C#), xUnit tests, Material popup menu.

**Backward-compat note:** keep `Part = 0`, `Mesh = 1` (persisted ints); add `Assembly = 2`. Do NOT renumber, or persisted Part flips to Assembly on upgrade.

---

### Task 1: Resolver — Assembly mode + shared presented-owner API

**Files:**
- Modify: `src/FabricationAssistant.App.Android/AndroidModelSelectionResolver.cs`
- Test: `src/FabricationAssistant.App.Android.Tests/AndroidModelSelectionResolverTests.cs`

- [ ] **Step 1: Write failing tests** (append to test class)

```csharp
[Fact]
public void AssemblyModeResolvesMeshToNearestOwningAssembly()
{
    Scene scene = CreateSceneWithNestedAssemblies();
    int[] ids = AndroidModelSelectionResolver.ResolveNodeIds(scene, [4], AndroidModelSelectionMode.Assembly);
    Assert.Equal([2], ids); // A1, the nearest assembly — not the top A (1)
}

[Fact]
public void AssemblyModeFallsBackToOwningPartWhenNoAssemblyAncestor()
{
    Scene scene = CreateSceneWithMultiMeshPart(); // Root -> Part(10) -> Shape(11),Shape(12)
    int[] ids = AndroidModelSelectionResolver.ResolveNodeIds(scene, [11], AndroidModelSelectionMode.Assembly);
    Assert.Equal([10], ids);
}

[Fact]
public void AssemblyModeResolvesAssemblyDirectGeometryToOwningAssembly()
{
    Scene scene = CreateSceneWithAssemblyDirectBodies();
    int[] ids = AndroidModelSelectionResolver.ResolveNodeIds(scene, [31], AndroidModelSelectionMode.Assembly);
    Assert.Equal([30], ids);
}

[Fact]
public void PartModeKeepsAssemblyDirectBodyAsItsOwnEntity()
{
    Scene scene = CreateSceneWithAssemblyDirectBodies();
    int[] ids = AndroidModelSelectionResolver.ResolveNodeIds(scene, [31], AndroidModelSelectionMode.Part);
    Assert.Equal([31], ids); // distinct-named body under an assembly is its own part
}

[Fact]
public void RenderableNodesExpandAssemblyToAllDescendantMeshes()
{
    Scene scene = CreateSceneWithNestedAssemblies();
    int[] ids = AndroidModelSelectionResolver.ResolveRenderableNodeIds(scene, [1]); // top assembly A
    Assert.Equal([4], ids);
}
```

Add these scene builders:

```csharp
private static Scene CreateSceneWithNestedAssemblies()
{
    var document = new DocumentDto();
    document.Nodes.Add(new SceneNodeDto { Id = 0, ParentId = -1, DisplayName = "Root", NodeType = SceneNodeType.Root });
    document.Nodes.Add(new SceneNodeDto { Id = 1, ParentId = 0, DisplayName = "Assembly A", SourceNodeName = "Assembly A", NodeType = SceneNodeType.Assembly });
    document.Nodes.Add(new SceneNodeDto { Id = 2, ParentId = 1, DisplayName = "Assembly A1", SourceNodeName = "Assembly A1", NodeType = SceneNodeType.Assembly });
    document.Nodes.Add(new SceneNodeDto { Id = 3, ParentId = 2, DisplayName = "Part P1", SourceNodeName = "Part P1", NodeType = SceneNodeType.Part });
    document.Nodes.Add(new SceneNodeDto { Id = 4, ParentId = 3, DisplayName = "Body", SourceNodeName = "Body", NodeType = SceneNodeType.Shape, MeshId = 4 });
    document.Meshes.Add(CreateMesh(4));
    return Scene.FromDocument(document);
}

private static Scene CreateSceneWithAssemblyDirectBodies()
{
    var document = new DocumentDto();
    document.Nodes.Add(new SceneNodeDto { Id = 0, ParentId = -1, DisplayName = "Root", NodeType = SceneNodeType.Root });
    document.Nodes.Add(new SceneNodeDto { Id = 30, ParentId = 0, DisplayName = "Assembly", SourceNodeName = "Assembly", NodeType = SceneNodeType.Assembly });
    document.Nodes.Add(new SceneNodeDto { Id = 31, ParentId = 30, DisplayName = "Bracket", SourceNodeName = "Bracket", NodeType = SceneNodeType.Shape, MeshId = 31 });
    document.Meshes.Add(CreateMesh(31));
    return Scene.FromDocument(document);
}
```

- [ ] **Step 2: Run tests, verify they FAIL** (`Assembly` enum value does not exist → compile error is acceptable as the failing state)

- [ ] **Step 3: Edit enum** — add `Assembly = 2` after `Mesh = 1`:

```csharp
internal enum AndroidModelSelectionMode
{
    Part = 0,
    Mesh = 1,
    Assembly = 2,
}
```

- [ ] **Step 4: Edit `ResolveNode`** body:

```csharp
        if (mode == AndroidModelSelectionMode.Mesh)
            return sourceNode;

        SceneNode presentedOwner = FindPresentedOwner(sourceNode) ?? sourceNode;

        if (mode == AndroidModelSelectionMode.Assembly)
            return FindOwningAssembly(presentedOwner)
                   ?? FindOwningPart(presentedOwner)
                   ?? presentedOwner;

        return FindOwningPart(presentedOwner) ?? presentedOwner;
```

- [ ] **Step 5: Add `FindOwningAssembly`** next to `FindOwningPart`:

```csharp
    private static SceneNode? FindOwningAssembly(SceneNode sourceNode)
    {
        // Nearest enclosing assembly. Instance nodes are occurrence wrappers,
        // not assemblies, so they are intentionally skipped here.
        for (SceneNode? current = sourceNode; current is not null; current = current.Parent)
        {
            if (current.NodeType == SceneNodeType.Assembly)
                return current;
        }

        return null;
    }
```

- [ ] **Step 6: Expose presented-owner walk** for reuse (add public method, keep `FindPresentedOwner` private):

```csharp
    public static SceneNode ResolvePresentedOwner(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return FindPresentedOwner(node) ?? node;
    }
```

- [ ] **Step 7: Run tests, verify PASS.**

- [ ] **Step 8: Commit** `feat(android): add Assembly selection-mode resolution`

---

### Task 2: Settings clamp

**Files:** Modify `src/FabricationAssistant.App.Android/AppSettings.cs:410-418`

- [ ] **Step 1:** widen range to `Assembly`:

```csharp
    internal static AndroidModelSelectionMode ModelSelectionMode
    {
        get => (AndroidModelSelectionMode)GetIntInRange(
            "model_selection_mode",
            (int)AndroidModelSelectionMode.Part,
            (int)AndroidModelSelectionMode.Part,
            (int)AndroidModelSelectionMode.Assembly);
        set => Put("model_selection_mode", System.Math.Clamp((int)value, (int)AndroidModelSelectionMode.Part, (int)AndroidModelSelectionMode.Assembly));
    }
```

(Range is [0,2]; Part=0 min, Assembly=2 max, Mesh=1 valid between.)

- [ ] **Step 2: Commit** `feat(android): allow Assembly in persisted selection mode`

---

### Task 3: Strings

**Files:** Modify `src/FabricationAssistant.App.Android/Resources/values/strings.xml`

- [ ] **Step 1:** add after `cd_tool_selection_mode_mesh` (line ~21):

```xml
    <string name="cd_tool_selection_mode_assembly">Selection mode: Assembly</string>
```

after `selection_mode_mesh_changed` (line ~97):

```xml
    <string name="selection_mode_assembly">Assembly</string>
    <string name="selection_mode_assembly_changed">Assembly selection mode</string>
```

- [ ] **Step 2: Commit** `feat(android): assembly selection-mode strings`

---

### Task 4: UI wiring (MainActivity)

**Files:** Modify `src/FabricationAssistant.App.Android/MainActivity.cs`

- [ ] **Step 1: Coerce Mesh→Part on launch** (`:451`):

```csharp
        AndroidModelSelectionMode persistedSelectionMode = AppSettings.ModelSelectionMode;
        _selectionMode = persistedSelectionMode == AndroidModelSelectionMode.Mesh
            ? AndroidModelSelectionMode.Part
            : persistedSelectionMode;
        if (_selectionMode != persistedSelectionMode)
            AppSettings.ModelSelectionMode = _selectionMode;
```

- [ ] **Step 2: 3-item exclusive popup** (`OnSelectionModeClicked`, `:5262-5292`):

```csharp
    private void OnSelectionModeClicked(object? sender, EventArgs e)
    {
        if (_toolSelectionModeButton is null)
            return;

        var popup = new PopupMenu(this, _toolSelectionModeButton);
        if (popup.Menu is not { } menu)
            return;

        IMenuItem? assembly = menu.Add(0, (int)AndroidModelSelectionMode.Assembly, 0, GetString(Resource.String.selection_mode_assembly));
        IMenuItem? part = menu.Add(0, (int)AndroidModelSelectionMode.Part, 1, GetString(Resource.String.selection_mode_part));
        IMenuItem? mesh = menu.Add(0, (int)AndroidModelSelectionMode.Mesh, 2, GetString(Resource.String.selection_mode_mesh));
        if (assembly is null || part is null || mesh is null)
            return;

        menu.SetGroupCheckable(0, true, true);
        assembly.SetChecked(_selectionMode == AndroidModelSelectionMode.Assembly);
        part.SetChecked(_selectionMode == AndroidModelSelectionMode.Part);
        mesh.SetChecked(_selectionMode == AndroidModelSelectionMode.Mesh);
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item is not { } item)
                return;

            AndroidModelSelectionMode mode = item.ItemId switch
            {
                (int)AndroidModelSelectionMode.Assembly => AndroidModelSelectionMode.Assembly,
                (int)AndroidModelSelectionMode.Mesh => AndroidModelSelectionMode.Mesh,
                _ => AndroidModelSelectionMode.Part,
            };
            SetModelSelectionMode(mode, showToast: true);
        };
        popup.Show();
    }
```

- [ ] **Step 3: 3-way toast** (`SetModelSelectionMode`, `:6343-6349`):

```csharp
        if (showToast)
        {
            int toastResId = _selectionMode switch
            {
                AndroidModelSelectionMode.Assembly => Resource.String.selection_mode_assembly_changed,
                AndroidModelSelectionMode.Mesh => Resource.String.selection_mode_mesh_changed,
                _ => Resource.String.selection_mode_part_changed,
            };
            Toast.MakeText(this, toastResId, ToastLength.Short)?.Show();
        }
```

- [ ] **Step 4: 3-way button state** (`UpdateSelectionModeButtonState`, `:6354-6369`):

```csharp
    private void UpdateSelectionModeButtonState()
    {
        if (_toolSelectionModeButton is null)
            return;

        (int textResId, int contentDescriptionResId) = _selectionMode switch
        {
            AndroidModelSelectionMode.Assembly => (Resource.String.selection_mode_assembly, Resource.String.cd_tool_selection_mode_assembly),
            AndroidModelSelectionMode.Mesh => (Resource.String.selection_mode_mesh, Resource.String.cd_tool_selection_mode_mesh),
            _ => (Resource.String.selection_mode_part, Resource.String.cd_tool_selection_mode_part),
        };
        _toolSelectionModeButton.Text = GetString(textResId);
        _toolSelectionModeButton.ContentDescription = GetString(contentDescriptionResId);
        SetSelected(_toolSelectionModeButton, _selectionMode != AndroidModelSelectionMode.Part);
        SetTooltip(_toolSelectionModeButton, contentDescriptionResId);
    }
```

- [ ] **Step 5:** Inspect `:10744-10745` (other `_selectionMode == Part` use) and make it 3-way safe (read context first, adjust switch/ternary accordingly).

- [ ] **Step 6: Honor active mode in tree + BOM** (`:3803-3806`, `:4121-4124`): replace `AndroidModelSelectionMode.Part` with `_selectionMode` in both `SelectModelExplorerNode` and `SelectNodesFromBom`.

- [ ] **Step 7: Build the Android project** (see Task 6 for command); fix any compile errors.

- [ ] **Step 8: Commit** `feat(android): 3-way Assembly/Part/Mesh selection toggle + mode consistency`

---

### Task 5: Dedup + perf

**Files:** Modify `PropertiesPanelBinder.cs`, `MainActivity.cs`

- [ ] **Step 1: Dedup** — in `PropertiesPanelBinder.ShowSelection` (`:57`) replace `node = ResolvePresentedNode(node);` with `node = AndroidModelSelectionResolver.ResolvePresentedOwner(node);` and delete the private `ResolvePresentedNode` (`:412-427`).

- [ ] **Step 2: Perf memo** — in `MainActivity`, add fields and cache `CountVisibleLogicalNodes` keyed on `(scene, VisibilityVersion, mode)`:

```csharp
    private Scene? _visibleLogicalCountScene;
    private long _visibleLogicalCountVisibilityVersion = -1;
    private AndroidModelSelectionMode _visibleLogicalCountMode;
    private int _visibleLogicalCountCache;

    private int CountVisibleLogicalNodesCached(Scene scene, AndroidModelSelectionMode mode)
    {
        long version = scene.VisibilityVersion;
        if (ReferenceEquals(_visibleLogicalCountScene, scene)
            && _visibleLogicalCountVisibilityVersion == version
            && _visibleLogicalCountMode == mode)
        {
            return _visibleLogicalCountCache;
        }

        int count = AndroidModelSelectionResolver.CountVisibleLogicalNodes(scene, mode);
        _visibleLogicalCountScene = scene;
        _visibleLogicalCountVisibilityVersion = version;
        _visibleLogicalCountMode = mode;
        _visibleLogicalCountCache = count;
        return count;
    }
```

Update `CanUseExplodeView` (`:10366-10368`) to use `CountVisibleLogicalNodesCached(_runtimeScene, _selectionMode) > 1`.

- [ ] **Step 3: Build; Commit** `refactor(android): share presented-owner walk; cache visible-logical count`

---

### Task 6: Unit tests green

- [ ] **Step 1:** Determine the test command from `FabricationAssistant.App.Android.Tests.csproj` (target framework, runner).
- [ ] **Step 2:** Run the resolver + explode test suites; all green (including the 5 new tests). Fix as needed.

---

### Task 7: Build, deploy, on-device test

- [ ] **Step 1:** Confirm device: `adb devices`.
- [ ] **Step 2:** Build + install to the tablet (`dotnet build -t:Install -f <android-tfm>` or APK + `adb install -r`).
- [ ] **Step 3:** Launch via `adb shell am start`, open most-recent file (Recent button → first entry).
- [ ] **Step 4:** Exercise modes; capture `adb logcat -s FA.Selection FA.Measure FA.Explode` + screenshots to confirm Assembly→nearest assembly, Part→owning part, Mesh→exact mesh; verify explode/hide/isolate operate on the resolved entity.
- [ ] **Step 5:** Report what was verified automatically vs. needs visual confirmation.

---

## Self-review
- Spec coverage: A (Tasks 1–4), B (Task 1 tests + Assembly branch), C (Task 4.6), D-F4 (Task 4.1), D-dedup (Task 5.1), D-perf (Task 5.2), E (no core edits — confirmed), Testing (Tasks 6–7). ✓
- Backward-compat: enum keeps Part=0/Mesh=1. ✓
- Type consistency: `ResolvePresentedOwner`, `FindOwningAssembly`, `CountVisibleLogicalNodesCached` names used consistently. ✓
