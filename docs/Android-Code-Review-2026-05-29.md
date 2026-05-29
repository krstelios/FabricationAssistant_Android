# Fabrication Assistant - Android Systematic Code Review

- **Date:** 2026-05-29
- **Scope:** Full Android app (`Android/src/**`), 25 review slices.
- **Method:** 25 parallel slice-reviewers following full call paths (UI -> state -> service -> renderer/data), then an independent adversarial verification pass that re-read the actual code and tried to refute every non-trivial finding.
- **Result:** 89 raw findings -> **53 verified-real, 9 downgraded, 1 refuted, 26 low pass-through**. Effective severity: **1 Critical, 3 High (2 distinct root causes), 19 Medium, 66 Low**.
- **Status:** Report only. No code was changed. All findings below are OPEN.

> NOTE: Findings come from code reading + adversarial verification, not from a runtime/build run (the build was not executed during review). Re-confirm cited line numbers before editing - the files may have shifted.

---

## Hard constraints (apply to every fix)

- Android-side only. Do NOT modify desktop `../src` shared files; if a shared-code bug exists, prefer an Android-side adapter/wrapper/guard/shim/exclusion.
- Build ONLY via `Android/tools/build.ps1` (never raw `dotnet build` on the desktop solution).
- Do NOT introduce `Application.Current.Dispatcher` in Android code.
- No emojis / non-ASCII in source, XML, or GLSL.
- `AppSettings.Initialize(context)` must remain the FIRST app init step in `MainActivity.OnCreate`.
- Renderer appearance/state changes flow through `SceneAppearance` + `AppSettings.Apply(ref appearance)`.
- Preferences stay centralized in `PreferencesBottomSheet`.
- Preserve existing architecture; targeted fixes only; no file splitting / cosmetic refactors.

---

## Codebase map

9 projects (`FabricationAssistant.Android.sln`), net8.0-android34, minSdk 24 / target 34, RIDs `android-arm64;android-x64`, APK, `EmbedAssembliesIntoApk=true`.

| Project | Role |
|---|---|
| **App.Android** (Exe, 51 files) | `MainActivity` (14,441 lines; OnCreate L395, lifecycle L13308-L13824), `PreferencesBottomSheet`, Cloud* (ApiClient/FilesPanel/SecureStore/NotificationClient/ServerConfig/Models), `AppSettings`, `AndroidModelExplorerPanel`, `AndroidBomPanel`, `ImportPipeline`, `SafFilePicker`, `PropertiesPanelBinder`, `RecentFiles*`, `Measurement/*`, `Tools/*` (QR, explode), `Views/ViewportSurfaceView` + `MultisampleConfigChooser`, `AndroidViewerStateSaveService` |
| **Rendering.Gles** (27 files + 14 GLSL ES) | `GlesViewportRenderer` (3,159 lines), `SceneAppearance`, Pick/Outline/Ssao/NormalDepth/Section/Measurement/AxisTriad/Grid/FaceHighlight overlays, `MsaaSceneFramebuffer`, `GpuMesh`/`GpuScene`/`SceneUploader`, `CadEdgeBuilder`, `ShaderProgram`, `GlThreadGuard` |
| **Input.Gestures.Android** | `AndroidPointerSource`, `ViewportInteractionAdapter`, `ViewportTouchGestureRecognizer`, `AndroidCameraClipPlanes` |
| **Draco.Android** | `DracoDecodingGltfImportService`, `DracoNativeDecoder` (P/Invoke), `DracoGltfTranscoder` + `libdraco_native.so` (arm64-v8a, x86_64) |
| **Core.Android / Import.Gltf.Android** | Shims compile-linking desktop `../src` shared source |
| **Platform.Android** | `AndroidDispatcher`, `AndroidPlatformPaths` |
| **App.Android.Tests** (22 files) | Gesture, MSAA, SceneAppearance, SectionCap, ViewerState, Draco, import-validator tests |

Scope confirmations: Slice 18 = axis triad only (no view cube / glyph atlas / `overlay_text`). Slice 21 markup = absent. `SelectionState` is shared Core.

---

## Slice status (all 25 reviewed)

| # | Slice | Confirmed-real |
|---|---|---|
|1|Startup/DI/init|1 M|
|2|Lifecycle|3 Low|
|3|File open/import/SAF|1 High + 1 M (+3 candidates)|
|4|GLB/glTF/FA import|1 M (+2 Low)|
|5|Draco|2 Low (+1 candidate)|
|6|Renderer init/GLES context|2 Low (+1 candidate)|
|7|GLES resources/shaders/passes|1 High + 2 Low|
|8|MSAA/framebuffer|4 Low (+1 candidate)|
|9|SceneAppearance/AppSettings|1 Low (+1 candidate)|
|10|PreferencesBottomSheet UI|4 Low (1 refuted, 1 candidate)|
|11|Toolbar/menus/command state|1 Low (+1 candidate)|
|12|Touch gestures/camera|3 M + 1 Low (+2 candidates)|
|13|Selection/picking|1 Low (+2 candidates)|
|14|Scene tree/model explorer|3 M (+2 candidates)|
|15|Properties panel|2 M + 1 Low|
|16|Section cuts|2 Low|
|17|Measurement|1 M (+2 candidates)|
|18|Axis gizmo (no view cube)|2 Low + scope note (+1 candidate)|
|19|Hover/outline/clay|1 M + 1 Low (+1 candidate)|
|20|Render modes/parity|2 M + 3 Low (+1 candidate)|
|21|Markup (absent)|0 (1 candidate)|
|22|Cloud/login/API|3 M + 1 Low (+2 candidates)|
|23|Local storage/cache/logs|1 High + 1 M + 1 Low (+2 candidates)|
|24|Manifest/resources/packaging|4 Low|
|25|Tests/build infra|**1 Critical**|

---

## CRITICAL

### S25#1 - The entire test gate is dead
- **Type:** Bug. **Files:** `App.Android.Tests/SectionCapGeometryBuilderTests.cs:207-208`, `tools/test.ps1:9`, `FabricationAssistant.Android.sln`.
- **Problem:** Three compounding failures:
  1. `SectionCapGeometryBuilderTests.cs:207-208` use `{ GroupId = 42 }` on `SectionCapMeshSource`, but the shared record struct (`SectionCapGeometry.cs:12`: `public readonly record struct SectionCapMeshSource(MeshDto Mesh, Matrix4d ModelMatrix)`) has no `GroupId` -> **CS0117 x2 -> Build FAILED** (verified by empirical build, exit 1). A failed assembly compile means all ~22 test files run ZERO tests.
  2. `test.ps1:9` (`dotnet test`) has no `$LASTEXITCODE` guard; `$ErrorActionPreference='Stop'` does not convert native nonzero exits to terminating errors (the repo's own `build.ps1` uses `if($LASTEXITCODE -ne 0){throw}` 5x; test.ps1 omits it).
  3. The Tests project is absent from the `.sln`, so `build.ps1` never compiles it.
- **Why:** The safety net is silently off. Any regression - including every fix in this report - goes undetected.
- **Fix:** Remove/skip the two `GroupId` grouping tests (`SectionCapGeometryBuilderTests.cs:176-219`); add `if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }` after `test.ps1:9`; add the Tests project to `FabricationAssistant.Android.sln` (note: `dotnet sln add` is broken on this workstation - edit the sln by hand).
- **Safe/localized:** yes (1 test file, 1 PS line, 1 sln entry). **Regression risk:** low; restores ~22 test files and fails loudly.
- **Verify:** `tools/test.ps1` - today Build FAILED yet exits success; after fix tests compile/run and a forced failure exits nonzero. **DO THIS FIRST so the rest can be verified.**

---

## HIGH

### S3#1 = S23#1 - Recent-files access probe blocks the UI thread (ANR)
- **Type:** Threading/Async. **Files:** `RecentFilesStore.cs:178-222,16-31`, `RecentFilesList.cs:19-30`, `RecentFilesBottomSheet.cs:89/105`, `MainActivity.cs:1040,10422`.
- **Problem:** Opening the Recent panel (`MainActivity.ToggleRecentPanel:1040` -> `panel.CreateEmbeddedView` -> `RefreshContent` -> `RecentFilesStore.Load`) and **every successful import** (`OpenModelUriAsync` resumes on the UI thread - no `ConfigureAwait(false)` - and calls `RecentFilesStore.Add` at L10422) run `FilterReadableOrUnknown`, which calls `ProbeReadableAccess` **once per entry, serially, on the UI thread**: `Task.Run(() => ProbeReadableAccessCore(...)).WaitAsync(5s).GetAwaiter().GetResult()`. The probe is a synchronous `ContentResolver.OpenFileDescriptor(uri,"r")` binder round-trip. With `MaxEntries=10` cloud-backed/offline URIs (the app's primary SAF source), that is up to **~50s** of main-thread blocking; a single offline entry already exceeds Android's 5s ANR threshold.
- **Fix:** Move probing off the UI thread. Either (a) probe all entries concurrently with a single shared timeout (`Task.WhenAll`) instead of sequential `GetResult()`, or (b) have `RefreshContent` load entries via `Task.Run` and post the pruned result back to the still-attached sheet (guard on `_disposed`/`_contentRoot != null`). Wrap the post-import `Add` at L10422 in `Task.Run` (the static `Gate` lock already serializes). Keep the 5s per-probe timeout as a backstop; preserve that `RecentFileAccessStatus.Unknown` entries are NOT pruned. Do not change persisted JSON.
- **Safe/localized:** yes. **Regression risk:** low-medium (timing only; ensure sheet still attached before mutating views).
- **Verify:** Add recent entries backed by a cloud provider, enable airplane mode, tap Recent and import: before fix UI freezes/ANR; after fix the panel opens immediately.

### S7#1 - Static edge-ribbon quad VBO/IBO survive GL context loss
- **Type:** Rendering/GLES. **Files:** `GpuMesh.cs:19-20,281-309,254/262,358-364`; `GlesViewportRenderer.cs` OnSurfaceCreated ~L356.
- **Problem:** `_staticEdgeQuadVbo`/`_staticEdgeQuadIbo` are **process-static** and `EnsureStaticEdgeQuad()` short-circuits forever once non-zero (`if (_staticEdgeQuadVbo != 0 && _staticEdgeQuadIbo != 0) return;`); they are never reset. After a real (non-preserved) EGL context recreation - `ViewportSurfaceView.cs:286-289` explicitly warns the background path destroys every GL handle; `PreserveEGLContextOnPause=true` (L290) is best-effort - `OnSurfaceCreated` builds a new `_gl` and re-uploads the scene, but `EnsureStaticEdgeQuad` skips recreation, so `UploadEdges` binds the dead static buffer names (L254/L262) into the fresh VAO. Result: corrupted/missing CAD edges & wireframe across all bodies, or `GL_INVALID_OPERATION`, until process restart. Verifier confirmed the full reachable path.
- **Fix:** Add `public static void ResetStaticEdgeQuad() { _staticEdgeQuadVbo = 0; _staticEdgeQuadIbo = 0; }` to `GpuMesh` (do NOT `glDelete` - the names belong to the dead context) and call it from `GlesViewportRenderer.OnSurfaceCreated()` right after building the new `_gl` (near L356), before any scene re-upload.
- **Safe/localized:** yes (2-line static + 1 call site). **Regression risk:** very low.
- **Verify:** Load a model with CAD edges, enable ShadedWithEdges, background+resume on a device that does not honor `PreserveEGLContextOnPause` (or temporarily set it false). Without fix edges corrupt/missing after resume; with fix correct.

---

## MEDIUM (confirmed)

### M1. S1#1 = S23#2 - No global unhandled-exception / crash handler
- **Type:** Hardening. **Files:** `MainActivity.cs` OnCreate L395-585; no `[Application]` subclass; `AndroidManifest.xml` L9-17 (no `android:name`); `AppServices.cs:15-52`; `AndroidPlatformPaths.cs:18/23`.
- **Problem:** Design spec S11.1 mandates an `AndroidCrashLogger` registering `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` + `AndroidEnvironment.UnhandledExceptionRaiser`, logging to `{CacheDir}/logs/crash.log`. None exists (repo-wide search: zero matches in .cs). `CacheDir/logs` is created but nothing writes to it (dead). `OnCreate` has no try/catch. On-device crashes leave no persisted artifact; logcat is gone after a field crash.
- **Fix:** Add an Android-only `AndroidCrashLogger.Install()` and register the three handlers at the earliest startup point - cleanest is a minimal `[Application]` subclass (set `android:name` in the manifest) so pre-MainActivity crashes are also caught; otherwise register in `OnCreate` after `base.OnCreate`. Append (timestamp+type+message+stack, ASCII-only) to a size-bounded `crash.log` (1 MB rotation -> `crash.previous.log`). Best-effort (swallow IO errors); leave `Handled=false`.
- **Verify:** Throw in `OnCreate` and on a background Task; confirm logcat + `crash.log` under cache/logs; confirm normal launch writes nothing.

### M2. S3#2 - ImportPipeline runs ContentResolver metadata + OpenInputStream synchronously on the UI thread
- **Type:** Threading/Async. **Files:** `ImportPipeline.cs:46-57,223-242,145-167`; caller `MainActivity.cs:10386`.
- **Problem:** `OpenModelUriAsync` (UI thread) calls `await _import.ImportAsync(...)` with no `Task.Run`. The synchronous prelude before the first real await (L175) runs `ResolveFileName` -> `ContentResolver.Query` (L227), `ResolveFileNameWithMimeFallback` -> `GetType` (L242), and in `CopyToLocalAsync` `TryResolveContentSize` -> `Query` (L268) + `OpenInputStream` (L165) - all blocking IPC, on the UI thread. Cloud-provider URIs jank the UI right after file pick. (The byte copy itself is correctly async.)
- **Fix:** Hop off the UI thread at the start: `var document = await Task.Run(() => _import.ImportAsync(uri, progress, cts.Token, copyToImportCache), cts.Token);` at L10386 (the `Progress<string>` still marshals back), or `await Task.Run` around the synchronous prelude inside `ImportAsync`. Do not change `CopyToLocalAsync`'s async copy.
- **Verify:** Pick a model from a slow cloud provider; watch the main thread (StrictMode or frame-time). Before: visible stall during Query/OpenInputStream. After: loading overlay animates smoothly.

### M3. S4#2 - FA cache root == Context.CacheDir, colliding with temp/ and logs/
- **Type:** File I/O. **Files:** `AppServices.cs:19-24`, `FaPackageStorageOptions.cs:24`, `AndroidPlatformPaths.cs:15-24`.
- **Problem:** `FaPackageStorageOptions` defaults `CacheRootPath` to `FabricationAssistantPaths.CacheDirectory`, and `AppServices` configures that to bare `Context.CacheDir`. `AndroidPlatformPaths` also creates `CacheDir/temp` and `CacheDir/logs` as siblings. The store enumerates child dirs of the cache root as candidate package caches. Harm is latent today only because cleanup is unwired (M-low S4#1); but the recommended cleanup fix becomes destructive against logs/temp, and retention size accounting is corrupted. Design doc L459 specifies a dedicated `fa-package-cache/<hash>/` path.
- **Fix:** When constructing the production `FaPackageCacheStore`, set `FaPackageStorageOptions.CacheRootPath = Path.Combine(platformPaths.CacheDir, "fa-package-cache")`. Do not repoint the global `FabricationAssistantPaths.CacheDirectory`.
- **Verify:** `adb shell run-as <pkg> ls` CacheDir: package caches now under `fa-package-cache/`, with `temp/` and `logs/` as siblings of it, not inside it.

### M4. S12#1 - Pan/Zoom sensitivity sliders are inert
- **Type:** Settings/Persistence. **Files:** `ViewportInteractionAdapter.cs:228-268,477-502,469-475`.
- **Problem:** On a real device the anchored pinch/pan path is always taken (`CapturePanZoomAnchor` always assigns a non-null anchor; `viewportWidthDip` is hundreds-to-thousands, far above the `>1` guard). `TryApplyAnchoredPanZoom` references neither `ZoomSensitivityMultiplier` nor `PanSensitivityMultiplier`; they are consulted only in the effectively-never-hit fallback branch (L241-256). The wired-up Pan/Zoom sliders (`MainActivity.cs:13894-13895`) therefore do nothing on normal two-finger gestures, while Orbit sensitivity works (surprising inconsistency).
- **Fix:** In `PanZoomDelta`, apply the multipliers inside the anchored path: raise the pinch scale to the `ZoomSensitivityMultiplier` exponent before `DollyZoomAroundPivot`, and scale the pan `correction` vector by `PanSensitivityMultiplier`. Preserve identity (world-locked) behavior at multiplier == 1.0 so the existing `OrthographicPan_UsesViewportWorldUnitsPerDip` test stays green.
- **Verify:** Set Pan/Zoom sensitivity to extremes; two-finger pan/pinch must scale with the slider. Add a unit test with non-default multipliers.

### M5. S12#2 - Long-press fails after a quick prior tap (tick latch never reset)
- **Type:** Android UI Logic. **Files:** `AndroidPointerSource.cs:777-787,32`.
- **Problem:** `ScheduleLongPressTick` guards on `_tickScheduled` and only clears it inside the posted 500ms callback or in Dispose - never on pointer-up/cancel. So a second press whose down lands within ~500ms of any prior press gets no tick posted; under RenderMode.WhenDirty nothing re-drives `Tick`, so the long-press (viewport context menu, `MainActivity:9249`) silently never fires.
- **Fix:** Re-arm reliably: give the posted Runnable a token and call `_handler.RemoveCallbacks` before each `PostDelayed`, OR reset `_tickScheduled = false` in the Up/PointerUp/Cancel branches when no pointers remain.
- **Verify:** Tap once, then within ~300ms press-and-hold a body for >500ms; context menu must appear. Repeat rapidly.

### M6. S12#3 - Double-tap Fit fires during axis section placement
- **Type:** Android UI Logic. **Files:** `MainActivity.cs:5008-5012,4948-4970,9235-9303,6398-6437`.
- **Problem:** A double-tap emits Tap (places axis section plane) then DoubleTap (re-fits camera). `ShouldSuppressNavigationGestures` suppresses navigation only for `Section && Custom`, not the X/Y/Z sub-modes, so with `DoubleTapFitScreenEnabled` (non-default) on, double-tapping to place an axis section both places the plane and jumps the camera. (`ViewportInteractionAdapter.OnGesture:142` already suppresses DoubleTap when `IsNavigationSuppressed()` is true - so broadening the predicate engages an existing path.)
- **Fix:** Make `ShouldSuppressNavigationGestures` (or specifically the DoubleTap dispatch in `OnGestureForNavigation`) also return true when `_activeModalTool == Section && _activeSectionSubMode != None`. Prefer guarding just DoubleTap so orbit/pan during axis placement stays allowed.
- **Verify:** Enable double-tap-fit, enter Section X mode, double-tap a body: plane placed, no camera re-fit. Confirm Custom-section and Select-mode double-tap-fit unchanged.

### M7. S14#1 - Pack/Unpack drops the selection highlight (tree<->viewport desync)
- **Type:** Scene Sync. **Files:** `AndroidModelExplorerPanel.cs:294-307,151-162`.
- **Problem:** Pack/Unpack buttons call `SetScene` directly, which unconditionally clears `_selectedPresentedId`/`_highlightedPresentedIds`/`_pathPresentedIds` and rebuilds, but never re-applies MainActivity's `_selectedNodeIds` (no callback). Viewport keeps selection; tree shows nothing highlighted until next click. Contradicts documented two-way sync.
- **Fix:** Raise a one-shot `SceneRebuilt` callback at the end of `SetScene` (when triggered by Pack/Unpack) and have MainActivity wire it to `SyncModelExplorerSelectionFromViewport(scrollToSelection: false)` (reuses the existing `SetSelection` path, which already maps onto packed virtual groups).
- **Verify:** Select a part (highlighted in tree), tap Pack then Unpack; row stays highlighted.

### M8. S14#2 - ListView adapters never reuse convertView
- **Type:** Android UI Logic. **Files:** `AndroidModelExplorerPanel.cs:460-545`; `AndroidBomPanel.cs:1106-1127`.
- **Problem:** `GetView` ignores `convertView` and allocates a full LinearLayout + Space + 2 TextViews + ImageButton + 2 new click listeners per call; BOM adapter adds 8-10 cells/row. `FastScrollEnabled=true`. Contradicts the documented "ListView row recycling for large models" goal -> GC churn / scroll jank on large assemblies.
- **Fix:** Implement convertView reuse with a `View.Tag` holder; build the subtree once and on reuse only update text/icon/alpha/background and re-bind the per-row click listeners. Must reset alpha (`:522`) and background (`:479`) on reuse to avoid stale recycled state.
- **Verify:** Load a large assembly, Expand All, fling-scroll; profile GC/inflation before vs after.

### M9. S14#3 - Full tree build + expand run synchronously on the UI thread
- **Type:** Threading/Async. **Files:** `AndroidModelExplorerPanel.cs:627-687,151-162,284-288,798-839`; called from `MainActivity.AttachRuntimeScene:11126`.
- **Problem:** `SetScene` (called on the UI thread after the import await chain resumes) runs `Tree.Build` (recursive whole-graph walk + per-node subtree recursion + duplicate packing). Expand-all materializes one row per node; virtual-group selection runs `GetVisualGroupSceneNodeIds` at `O(descendants x NodesById.Count)`. All on the UI thread -> hitches/ANR risk on big models.
- **Fix (lowest-risk subset):** (1) cap/guard Expand-all for very large trees; (2) cache a `sceneNodeId -> presentedGroup` map so `GetVisualGroupSceneNodeIds` avoids the per-call full scan. Optionally build the tree off-thread and assign on the UI thread (only after confirming the SceneNode graph is immutable during attach).
- **Verify:** Load a large assembly; time SetScene / Expand-all; watch for dropped frames.

### M10. S15#1 - Multi-select shows only one arbitrary node's properties
- **Type:** Android UI Logic. **Files:** `MainActivity.cs:3503-3508,144,3470`.
- **Problem:** Selection is a `HashSet<int>`; `ResolveSelectedNode` returns `FirstOrDefault` over it (unspecified enumeration order) and passes one node to `ShowSelection`. With N>1 selected (a supported state - Hide/Isolate iterate all selected), the panel shows one effectively-random part with no multi-select indication.
- **Fix (Android-side):** Make `ResolveSelectedNode` deterministic (`OrderBy(id)`) and have `ShowSelection` prepend a "Selection: N items" caption when `_selectedNodeIds.Count > 1`. Keep changes in MainActivity + `PropertiesPanelBinder`.
- **Verify:** Select 2+ parts, open Properties: deterministic node + "N items selected" caption.

### M11. S15#2 - Properties panel builds unbounded views synchronously
- **Type:** Threading/Async. **Files:** `PropertiesPanelBinder.cs:45-97,178-189,253-305`; `view_properties_panel.xml:30-48`.
- **Problem:** `ShowSelection` iterates `Metadata.Properties` (open-ended source metadata) 3x and inflates ~4-5 Views per property into a plain LinearLayout inside a ScrollView (no virtualization). For a metadata-heavy CAD part this builds thousands of Views synchronously on the UI thread (callers `RefreshSelectionDependentUi`, context-menu, and the L13523 reload continuation all run on the UI thread).
- **Fix (Android-side, in the binder):** Cap rows per group in `AddPropertyGroup` (show first K + an "N more..." row), or back the Attributes/Expressions/Other groups with a RecyclerView. Prefer the cap first.
- **Verify:** Select a node with very many properties; measure `ShowSelection` time / frame drops.

### M12. S17#1 - Bounding-box measurement under-reports extents (vertex subsampling)
- **Type:** Bug. **Files:** `AndroidMeasureIntegration.cs:326-347 (stride L331), 14`.
- **Problem:** When committing a bbox from a selection, vertices are subsampled with `stride = max(1, VertexCount / 4096)`. For meshes >4096 verts, only every Nth vertex is fed to `CommitBoundingBox`, which computes min/max directly from supplied points (both AABB and the default BestFit/OBB paths). Extreme corner vertices are frequently skipped -> reported X/Y/Z lengths are too small, unbounded for dense meshes. Silent wrong number in a quantitative tool.
- **Fix:** Do not subsample for extents. Either (a) a 6-double running min/max over the full `float[]` on the existing background `Task.Run`, or (b) capture `mesh.Bounds` into `MeshSnapshot` (it currently has no Bounds field - `MeshDto.Bounds` exists) and always include its 8 transformed corners. Option (a) is the cleaner fix.
- **Verify:** Select a >4096-vert node, commit bbox, compare reported dims to scene Bounds; before fix dims read short.

### M13. S19#1 - Silhouette overlay silently disabled whenever AO is off
- **Type:** Rendering/GLES. **Files:** `GlesViewportRenderer.cs:958-965,534-562,2574-2587`.
- **Problem:** The screen-space silhouette overlay reads the normal-depth prepass texture, but that prepass runs ONLY inside the `ssaoActive` block, and `silhouetteOverlayActive` hard-requires `ssaoActive`. `GetSsaoInactiveReason` returns "disabled" when AO is off -> `ssaoActive=false` -> prepass never runs -> turning Silhouettes on with AO off does nothing, no feedback. Two independent UI toggles.
- **Fix:** Decouple the prepass from SSAO. Add a `needNormalDepth` gate (e.g. `ssaoEligible || (CadEdgeSilhouetteEnabled && Mode != Clay && Mode != Wireframe && SectionPlanes.Count == 0 && !lightweightNavigationActive)`), run `_normalDepthRenderer.Render(...)` when set, and gate the overlay on whether the prepass actually ran THIS frame (do NOT gate on `NormalTexture != 0` - it is allocated once and stays non-zero). Keep SSAO consumption gated by `ssaoActive`. Preserve the `SectionPlanes.Count == 0` clause (asserted by `GlesRendererSourceTests`). Optional UI-only mitigation: gray out the Silhouettes switch when AO is off.
- **Verify:** Silhouettes on + AO off, load model: silhouettes appear after fix; AO-on behavior unchanged; Clay/Wireframe/section still emit none.

### M14. S20#1 - X-ray ghosts are selectable at full opaque pick depth
- **Type:** Rendering/GLES. **Files:** `GlesPickRenderer.cs:181,214-215`; cf. `GlesViewportRenderer.cs:1319-1337`.
- **Problem:** During x-ray isolate, background meshes render ghosted (alpha ~0.18, depth-write off, drawn last). But `GlesPickRenderer.Pick` draws every `mesh.Visible || IsXrayBackgroundMesh(mesh)` at full integer index with normal depth write (no `DepthMask(false)`; the pick shader has no alpha awareness). A faint ghost in front of the isolated body wins the pick depth test -> tapping the clearly-visible isolated body selects the ghost instead. Consumed without guard in `OnPickResult` (`MainActivity:9787`).
- **Fix (in GlesPickRenderer, decide product intent):** If ghosts should not be selectable, skip x-ray background meshes in the pick inclusion test. If they should be selectable but never occlude the isolated set, give the isolated (`XrayOpaque`) meshes pick priority or draw background meshes with depth-write off in the pick pass. NOTE: `XrayOpaqueNodeIds` is NOT currently wired to the pick renderer (only `XrayBackgroundNodeIds` is at `GlesViewportRenderer:258,2792`) - you must pass it through.
- **Verify:** Isolate (x-ray), orbit so a ghost sits between camera and isolated body, tap the isolated body; confirm it (not the ghost) is selected.

### M15. S20#2 - Non-FA x-ray isolate marks ghosted nodes Visible=false (tree desync)
- **Type:** Scene Sync. **Files:** `MainActivity.cs:5725-5745 (ApplyIsolatedVisibility 5741), 5861`; `GlesViewportRenderer.cs:1155-1156`.
- **Problem:** In the non-FA isolate path, `ApplyIsolatedVisibility` sets every non-isolated node `Visible=false`, but the renderer keeps drawing them ghosted via `ShouldRenderMesh = mesh.Visible || IsXrayBackgroundMesh` (driven by `XrayBackgroundNodeIds`, not `Visible`). So the model tree shows them hidden/dimmed while they are visibly ghosted. The FA path calls `EnsureBackgroundNodesVisible`; the non-FA branch does not.
- **Fix:** In the non-FA branch, after `SetXrayIsolationState`, call `EnsureBackgroundNodesVisible(scene, backgroundNodeIds)` (existing helper; mirrors FA path). Ghosting is driven by node-id sets, so visuals are unchanged; tree becomes consistent.
- **Verify:** Open a non-.fa GLB, select a body, Isolate (x-ray): tree shows ghosted bodies as visible; Show All returns to fully visible.

### M16. S22#1 - Release build permits cleartext to all domains
- **Type:** Hardening. **Files:** `network_security_config_release.xml:3`; `AndroidManifest.xml:14-15`.
- **Problem:** Release `base-config` sets `cleartextTrafficPermitted="true"` globally + manifest `usesCleartextTraffic="true"`. Needed for LAN http servers, but applied to the Internet/HTTPS profile too; `NormalizeServerUrl` only enforces "http or https" (`CloudApiClient:1447-1451`). Auth tokens/model data exposed to downgrade/MITM if an Internet URL is http.
- **Fix:** Replace the global base-config with a `domain-config` permitting cleartext only for RFC1918/local hosts (or the configured local_url host); set `base-config cleartextTrafficPermitted="false"`; remove `usesCleartextTraffic="true"` from the manifest. Optionally reject `http://` for the Internet profile in config resolution.
- **Verify:** Release build; Internet http URL blocked, local 192.168.x http still connects.

### M17. S22#2 - Upload validation poll timeout surfaces as bare cancellation
- **Type:** Cloud/API. **Files:** `CloudApiClient.cs:938-969`; consumed `MainActivity.cs:2150-2227`.
- **Problem:** `WaitForUploadOperationAsync` polls up to `OperationPollTimeout` (5 min) on an internal linked `timeoutCts`. On timeout it throws `OperationCanceledException`; MainActivity's `catch ... when (ex is not OperationCanceledException)` skips it -> recovery copy not written, reader session degraded (`LockId=null`, heartbeat stopped), and the outer guarded catch (`when cts.IsCancellationRequested`) also misses it -> generic "task canceled" message. (Verifier scoped the "lost commit/desync" to a possible consequence, not proven; the error-handling/recoverability gap is proven.)
- **Fix:** Wrap the poll loop: `catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CloudApiException("Cloud validation is taking longer than expected. The save may still complete on the server; refresh before saving again.", code: "operation_poll_timeout"); }` (the ctor exists at `CloudModels.cs:215`). Genuine user cancellation still propagates.
- **Verify:** Server stub keeps an operation non-terminal >5 min (or shorten the timeout in a test); save surfaces the timeout message, writes a recovery copy, reacquires the reader.

### M18. S22#3 - HttpClient infinite timeout, no ConnectTimeout
- **Type:** Threading/Async. **Files:** `CloudApiClient.cs:33-37,51`.
- **Problem:** Shared `HttpClient.Timeout = Timeout.InfiniteTimeSpan` (intentional for large streams) but the `SocketsHttpHandler` sets no `ConnectTimeout`/`PooledConnectionLifetime`. A half-dead server (TCP connect OK, no bytes) hangs `SendAsync` forever; `IsTransientNetworkError` only treats thrown exceptions as transient, so a silent stall never retries. Worse: `RefreshCoreAsync:1170` hardcodes `CancellationToken.None`, and `TryRestoreSessionAsync` passes `None` - only escape is activity destroy.
- **Fix:** Set `SocketsHttpHandler.ConnectTimeout = TimeSpan.FromSeconds(15)` (and optionally `PooledConnectionLifetime = 5 min`) in the handler initializer. Keep overall `Timeout` infinite for streaming, but add a bounded linked `CancelAfter` around control-plane calls (sign-in, refresh, metadata, lock); fix the hardcoded `CancellationToken.None` in `RefreshCoreAsync`. All in `CloudApiClient`.
- **Verify:** Socket that accepts TCP but never responds; sign-in now times out instead of hanging; large legitimate downloads still complete.

---

## LOW (confirmed) - digest

| ID | Type | Title |
|---|---|---|
|S2#1|Lifecycle|Gesture handler detach condition (OnDestroy 13785) doesn't match unconditional attach (504-506)|
|S2#2|Lifecycle|`_activityDestroyCts`/`_loadSemaphore`/`_cloudSessionGate` never disposed|
|S2#3|Lifecycle|Delayed main-thread render callback not removed on dispose (relies on re-check guard)|
|S4#1|File I/O|FA SQLite package cache never cleaned / retention-bounded on Android (cleanup methods unwired)|
|S5#1|Bug|Inner desktop GltfImportService keeps a Draco helper-exe path that can throw FileNotFoundException on Android|
|S5#2|Hardening|Transcoder budget casts to int then can throw OverflowException escaping the decorator catch list|
|S6#1|Rendering|MultisampleConfigChooser can return null with no relaxed fallback (GL-thread crash)|
|S6#2|Dead Code|`GlThreadGuard.Initialize()`/`Reset()` unused in the Android port|
|S7#2|Rendering|Pick pass clears depth without forcing DepthMask=true at entry|
|S7#3|Rendering|Process-static edge quad buffers never deleted (minor GL leak)|
|S8#1|Dead Code|MultisampleConfigChooser 2x/4x branches dead (always constructed with 0 samples)|
|S8#2|Dead Code|MsaaSceneFramebuffer XML summary describes a path the renderer never exercises|
|S8#3|Simplification|Redundant `_msaaFbo.Reset()` in OnSurfaceCreated then TryDispose+null in DisposeResources|
|S8#4 / S9#1|UI / Settings|8x MSAA supported by clamp/FBO but not selectable in Preferences (mislabeled)|
|S10#1|Dead Code|BottomSheetDialogFragment dialog-lifecycle machinery dead in the embedded-view hosting path|
|S10#2 / S24#3|Dead Code|`activity_preferences.xml` + entire `preferences_*` string cluster unused (live UI is code-built)|
|S10#3|UI|SeekBar sliders lack ContentDescription / accessibility labeling|
|S10#4|UI|Section header chevron ContentDescription set to section title (misleading)|
|S11#1|Settings|Render-mode toolbar selected-state goes stale after changing render mode/edges in Preferences|
|S12#4|UI|First tap of a double-tap still triggers selection/section pick before the fit|
|S13#1|Rendering|Pick depth/color clear depends on inherited GL mask state (occluded false positives)|
|S15#3|Bug|Inconsistent culture formatting of numeric property values (`PropertiesPanelBinder:359` vs `362`)|
|S16#1|UI|Custom 3-point section placement soft-locks when the first two points coincide|
|S16#2|Rendering|CAD silhouette edges suppressed scene-wide whenever any section plane is active (likely intentional)|
|S18#2|Rendering|Triad orientation/labels verified consistent with camera (check passed)|
|S18#3|Settings|Axis triad size/corner/margin hardcoded - no live settings (no view-cube settings exist)|
|S19#2|Settings|Hover-outline thickness slider partly dead: <1px clamped to 1px, default is sub-pixel (0.378)|
|S20#3|Settings|`AndroidRenderModeShim` "round-trips on desktop" rationale inaccurate (enum orderings differ)|
|S20#4|Rendering|Clay "screen-space outline" from design doc not implemented (clay uses geometric edges only)|
|S21#1|Settings|Annotation note with empty/whitespace text dropped on load, then permanently lost on next save|
|S22#4|File I/O|Non-transient send failure in SendAuthorizedAsync leaks HttpRequestMessage (+ upload FileStream)|
|S22#5|Dead Code|`CloudSecureStore.Save/LoadRememberedPassword` never called|
|S23#3|Hardening|Import-cache & cloud temp file paths logged at Info to logcat|
|S24#1|Bug (constraint)|Non-ASCII MULTIPLICATION SIGN in `strings.xml:127-128` (preferences_msaa_2x/4x)|
|S24#2|Bug (constraint)|Non-ASCII box-drawing chars (U+2500) in `activity_preferences.xml` comments L37/114/174|
|S24#4|Dead Code|`values-night/colors.xml` byte-identical to `values/colors.xml` (app hard-coded dark)|

## Candidates (need verification)

| ID | Type | Title |
|---|---|---|
|S3#3|UI|OpenModelUriAsync can leave nav buttons disabled if it early-returns before ShowLoading|
|S3#4|UI|Recent file selection has no busy guard while a prior import/picker runs|
|S3#5|Hardening|`ResolveFileName` ContentResolver.Query ignores the cancellation token|
|S4#3|File I/O|Valid-geometry FA package missing BOM entries aborts the whole import|
|S5#3|Threading|`DracoMesh` finalizer takes the global native lock (finalizer-thread stall risk)|
|S6#3|Rendering|OnSurfaceCreated may delete stale GPU resources through the destroyed context after true loss|
|S8#5|UI|Hardware/resolve MSAA fallback silently reduces quality while the toggle shows the requested level|
|S9#2|Settings|`Put(float)` coerces non-finite to 0.0, can persist below a setting's clamp floor|
|S10#6|Settings|Grid-spacing slider caps at 1000mm while the setting allows up to 1,000,000mm|
|S11#2|UI|Explode button enabled for multi-mesh scenes even when no part can move (HasExplodableUnits unused)|
|S12#5|Hardening|Palm-cancel (FLAG_CANCELED) detection is a no-op below API 33|
|S12#6|Bug|TryApplyAnchoredPanZoom drops centroid pan correction when zoom succeeds but world-projection fails|
|S13#2 / S19#3|Hardening|`GlesOutlineRenderer.Render` indexes `outlineColor[0..2]` without a length guard (GL-thread throw)|
|S13#3|Rendering|X-ray background meshes write depth in the pick pass and can occlude isolated foreground bodies|
|S14#4|Settings|Showing a node while isolation active reports success but no visible change (.fa path)|
|S14#5|UI|"Selection hidden by the current filter" status can be overwritten by the visible/hidden toggle|
|S17#2|Rendering|Committed measurement overlays are not section-clipped (float over sectioned-away geometry)|
|S17#3|Threading|Measurement hover runs a synchronous exact BVH raycast + full edge-set transform on the UI thread|
|S18#4|UI|Triad anchored bottom-left with a fixed pixel margin, not inset-aware (does not currently overlap)|
|S20#5|Rendering|Ground grid radial fade centered on camera XY, not the model (can fade grid under the model)|
|S21#2|Settings|Manifest declares Viewer/Markups.json + snapshot folder unconditionally even with no markups|
|S22#6|Cloud/API|SignalR client reconnects indefinitely even after the session is permanently gone|
|S23#4|File I/O|Cloud->cloud direct switch may not delete the previous cloud temp file|
|S23#5|File I/O|Active import-cache file can be deleted by cloud/token cleanup, breaking save & viewer-state re-reads|

## Refuted (false alarm - caught by verification)

- **S10#5** - "Keep me signed in clears remembered password even when ON" - refuted; the toggle does not clear it when on.

---

## Prioritized fix plan

1. **CRITICAL first:** S25#1 - restore the test gate (so everything else is verifiable).
2. **HIGH:** S7#1 (static edge-quad reset), S3#1/S23#1 (recent-files probe off the UI thread).
3. **MEDIUM, small & localized:** S12#3, S14#1, S20#2, S12#1, S22#1, S22#3, S22#2, S1#1/S23#2 (crash logger), S12#2, S19#1, S20#1.
4. **MEDIUM, needs care:** S3#2, S14#2/S14#3, S15#1/S15#2, S17#1, S4#2.
5. **LOW/cleanup:** S24#1/S24#2 (non-ASCII constraint violations - trivial), then dead-code removal.

## Safe first patch list

| Order | Fix | Scope | Risk |
|---|---|---|---|
|1|S25#1 restore tests (2 tests, test.ps1 exit guard, Tests project in sln)|1 test file, 1 PS line, 1 sln entry|Low|
|2|S7#1 `GpuMesh.ResetStaticEdgeQuad()` + call in OnSurfaceCreated|2 files|Very low|
|3|S24#1 + S24#2 strip non-ASCII (`x`, `---`)|strings.xml, activity_preferences.xml|Trivial|
|4|S12#3 broaden DoubleTap suppression to section sub-modes|1 predicate|Low|
|5|S14#1 re-sync selection after Pack/Unpack|panel + 1 wiring line|Low|
|6|S20#2 add EnsureBackgroundNodesVisible to non-FA x-ray branch|1 line|Low|
|7|S3#1/S23#1 probe recent files off the UI thread|RecentFilesStore/Sheet|Low-Med|

Second batch (after batch 1 builds + verifies green): S1#1 crash logger, S19#1 prepass decouple, S12#1 sensitivity, S22#* cloud, S20#1 pick, then the medium-but-careful items.

---

## Verification plan

### Build / deploy / log workflow (from docs/build-and-deploy.md)

```powershell
# Build (Debug)
.\Android\tools\build.ps1

# APK output
#   Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk

# Deploy + launch
adb shell am force-stop com.fabricationassistant.android
adb logcat -c
adb install -r "Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk"
adb shell am start -n com.fabricationassistant.android/crc649abf7c96d03d876b.MainActivity

# Stream FA logs + crashes
adb logcat -v color *:S FA.App:V FA.Renderer:V FA.Gles:V FA.Shader:V FA.Ssao:V FA.NormalDepth:V FA.MeshUpload:V AndroidRuntime:E DOTNET:V

# After a crash: managed trace
adb logcat -b crash -d -t 100
# Native (EGL/driver) crash:
adb shell ls -1t /data/tombstones | head -3
```

(The Activity CRC name `crc649abf7c96d03d876b.MainActivity` is stable unless the C# class namespace/name changes.)

### Tests (after S25#1 lands)
`Android\tools\test.ps1` - confirm ~22 files compile & run, and that a forced failure makes the script exit nonzero.

### Per-fix manual checks (on tablet, using a recent .fa from the Recent list)
- S7#1: load model with CAD edges + ShadedWithEdges; background -> resume; edges intact.
- S3#1: Recent panel + import with airplane mode / offline cloud entry; no freeze/ANR.
- S12#1: two-finger pan/pinch at non-default sensitivity scales with the slider.
- S12#2: tap then press-and-hold within ~300ms; context menu appears.
- S12#3: enable double-tap-fit, Section X mode, double-tap; plane placed, no camera jump.
- S14#1: select part, Pack/Unpack; highlight persists.
- S20#2: non-FA GLB x-ray isolate; tree consistent with viewport.
- S20#1: x-ray isolate, orbit a ghost in front, tap isolated body; correct selection.
- S19#1: Silhouettes on + AO off; silhouettes render.
- M-cloud: half-dead server -> sign-in times out; long validation -> clear message + recovery copy.
- Lifecycle: rotate, lock/unlock, background-during-import, reload after context loss.

---

## Optional deeper refactors (only if requested later)

`MainActivity.cs` at 14,441 lines is the systemic risk behind several cross-cutting findings (selection/visibility/section/cloud interleaved). Not recommended now per the no-split constraint - recorded only. Model-explorer/properties UI-thread items (S14#2/3, S15#2) would benefit from RecyclerView virtualization if large assemblies are a real target.
