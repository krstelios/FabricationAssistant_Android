# Android Fabrication Assistant - Senior Code Review

**Branch:** master
**Build:** `Android/tools/build.ps1` -> exit 0 (clean baseline)
**Date:** 2026-05-26
**Method:** 7 parallel Explore agents (4 succeeded: renderer, input/gestures, UI panels, SAF/import; 3 Cloudflare 522 - lifecycle, GLES resources, settings - read directly).

> Scope: Android/ folder only. No desktop changes. No file splits or restructuring. Findings are bug-, lifecycle-, threading-, rendering-, and hardening-focused, with concrete `file:line` evidence.

---

## 0. Fix progress

Updated 2026-05-27 after implementation and tablet verification.

### Fixed in `955147f` (`updates`)

- **B1 / R1 / R2:** shader compile/link cleanup now deletes the vertex shader on fragment compile failure and detaches/deletes shaders on link failure.
- **B2 / R5:** `GpuScene.Load` no longer exposes an empty public mesh list during scene replacement.
- **B3 / R3:** `GpuMesh` VAO/VBO/EBO creation is lazy and upload-safe after context recreation.
- **B4 / R4:** mesh upload and edge ribbon temporary buffers use pooled arrays.
- **B6 / L4:** viewport render requests short-circuit while paused and queued renderer commands are drained on pause.
- **B8:** Android dispatcher send timeout is configurable and defaults longer.
- **B10 / I1:** SAF persistable read permission is taken before import work.
- **B11 / I2 / S1:** recents writes and settings migration use durable `Commit()`.
- **B14 / B23 / I6 / I7:** import validation checks GLB length/version, GLTF JSON asset metadata, and FA archive structure rather than trusting MIME or magic bytes alone.
- **B15:** BOM selected-row state keys off stable row identity instead of stale row references.
- **B17:** measurement integration disposes event subscriptions.
- **B18 / B20 / R9-MSAA / R9-outline / R9-grid / R9-viewport:** MSAA fallback is bounded, outline/grid state is restored, and main framebuffer reset restores the viewport.
- **B21 / B22:** S-Pen hover is wired and palm rejection arms only on stylus down/pointer down.
- **L3:** GL thread guard is explicitly initialized on surface creation.
- **L8 / U9 / U10:** undo and Android view/listener cleanup paths are hardened on destroy.
- **I4:** recent URI dedupe normalizes percent-encoded variants.
- **I9 / I10 / B12:** Draco GLB handling avoids full-file managed byte-array loading and writes decoded output through temp file streams.
- **R7:** aspect clamp logging is rate-limited instead of silent.

### Fixed in `486b5c0` (`Harden Android renderer and lifecycle follow-ups`)

- **B16:** `AndroidMeasureRaycaster` invalidates acceleration cache on scene visibility/transient/move version changes.
- **L10 / R9-readpixels:** pick readback flushes before `ReadPixels`; normal-depth and SSAO diagnostic readbacks already finish before reads.
- **R9-FBO:** pick FBO setup failure is retained and surfaced in logs instead of silently behaving like a default-framebuffer fallback.
- **R9-line-width / point-size:** measurement and section overlays query GLES primitive limits and clamp line widths / point sizes.
- **U3:** S-Pen palm-rejection toast is debounced.
- **U6:** styled tooltip controllers are rebuilt after configuration changes.
- **U7:** preferences bottom-sheet height is bounded so landscape tablets keep viewport space.
- **U8:** fullscreen property-panel restore validates the panel before reopening it.
- **L1:** saved package occurrence selection is null-safe.
- **L2:** context-loss reload now gives the user fallback UX/logging if retained data and URI reload are unavailable.

### Fixed in `8d63085` (`Use flush for GLES pick readback`)

- **L10 / R9-readpixels:** pick readback uses `Flush()` instead of `Finish()` after tablet regression showed `Finish()` forced long tap-pick stalls.

### Fixed in `5c5ced9` (`Scissor GLES pick readback to tapped pixel`)

- **R9-readpixels follow-up:** pick rendering scissors the offscreen FBO to the tapped pixel before clear/draw/readback, reducing full-viewport pick raster work.

### Fixed in `cf75428` (`Harden transient GLES framebuffer recovery`)

- **R9-FBO:** normal/depth and outline FBO setup failures now keep and re-log the setup error when the pass is unavailable.
- **Memory-trim recovery:** normal/depth, SSAO, and outline transient framebuffers are reallocated on the next render after `TrimTransientGpuResources()` instead of staying disabled.
- **R9-SSAO binding hygiene:** SSAO diagnostic readback restores framebuffer binding and texture unit 0 after stats collection.
- **R9-AO texture-unit hygiene:** main framebuffer state reset now also restores active texture unit 0.
- **R9-section-cap stencil:** section cap stencil writes now depth-test against the scene depth buffer to keep cap parity in visible depth order.
- **D2 / cleanup:** removed the unused viewport renderer uniform-cache reset helper.

### Fixed in `942c234` (`Harden settings and transform sync cleanup`)

- **R6:** `GpuScene.SyncNodeTransforms` now tracks the runtime scene's transform version counters and skips redundant full-scene matrix/bounds recomputation when no move or transient transform changed.
- **S6:** settings color-picker dialogs now track active dialogs, clear owned backgrounds on dismiss, and dispose replaced swatch drawables instead of leaving dialog drawables live until process cleanup.
- **C5:** settings schema migration default-removal and range-guard lists are centralized in typed tables, reducing the chance that future migration entries are missed or drift from the cleanup logic.

### Fixed in `deac167` (`Serialize Android model loads`)

- **B13 / I8:** user-initiated model opens now serialize through one model-load semaphore, so a canceled import must leave the critical load path before the next import can attach scene or renderer state.
- **B9:** context-loss reload now acquires the same model-load gate and uses a non-replacing exclusive load when falling back to URI reload, so automatic reloads do not trample fresh user opens.
- **U2 / L6:** active load CTS replacement and load-version assignment now happen under the same lock; pause/destroy cancellation reads the active CTS through the same gate.

### Fixed in `c0c36d6` (`Harden tooltip and MSAA settings behavior`)

- **U4:** styled tooltip dismissal now excludes the active tooltip instead of dismissing itself and then continuing with a refreshed generation, closing the hover/long-press show-vs-dismiss race.
- **S4:** Android MSAA settings and framebuffer clamp helpers now preserve an 8x bucket instead of silently downgrading every value above 2x to 4x; unit coverage now includes the 8x setting and hardware-limit path.

### Fixed in `9c7e651` (`Detach MainActivity UI event handlers`)

- **B7:** MainActivity-owned navigation, properties, bottom-toolbar, section/explode/render-mode, and loading-cancel handlers now use named methods and are explicitly detached during `OnDestroy`; JNI listener clearing remains as a cleanup backstop.

### Fixed in `7b45c9b` (`Snapshot renderer appearance arrays`)

- **B5:** settings-to-renderer handoff now snapshots every `SceneAppearance` array field before queueing the GL-thread command, so future UI-side array mutation cannot alias an in-flight renderer appearance update. Unit coverage verifies array fields are cloned and scalar fields are preserved.

### Latest tablet verification

- Installed the debug APK containing `7b45c9b` on tablet `R52TA040AQT`.
- Opened the app, loaded recent file `50-0001916_00.fa`.
- Load log scan: `documentNodes=1937`, `documentMeshes=666`, `visibleMeshNodes=666`, diagonal `4.622`; tablet reports `GL_MAX_SAMPLES=4`, so the 8x-capable MSAA path clamps to 4x on this hardware. Initial `load-document` GL command run was `5161.5ms`.
- Scripted interaction: no fatal exception, ANR, import failure, GLES error, framebuffer failure, or JNI error; sampled frame timing blocks averaged `16.3-17.1ms`, max `34.0ms`, with `1` frame over `33ms` and `0` frames over `50ms`.
- Render queue burst: top `pick:tap` run `71.5ms`, top wait `63.3ms`, slow-frame log count `5`, Choreographer skipped-frame log count during scripted interaction `0`. Cold launch emitted one pre-load Choreographer skipped-frame line.
- Final app-PID logcat regression scan after scripted interaction: no crash, ANR, import, GLES, framebuffer, render, or load failure.

---

## 1. High-level Android codebase map

```
Android/
├── tools/                             build.ps1, build-libdraco.ps1, test.ps1, run-render-queue-test.ps1
├── deps/                              draco (native + unity bindings)
└── src/
    ├── FabricationAssistant.Core.Android         FabricationAssistantPathsBootstrap.cs (path shim)
    ├── FabricationAssistant.Platform.Android     AndroidDispatcher.cs, AndroidPlatformPaths.cs, IDispatcher.cs, IPlatformPaths.cs
    ├── FabricationAssistant.Import.Gltf.Android  GLTF importer Android shim
    ├── FabricationAssistant.Draco.Android        DracoNativeDecoder.cs, DracoExtensionDetector.cs
    ├── FabricationAssistant.Input.Gestures.Android  AndroidPointerSource.cs, ViewportTouchGestureRecognizer.cs,
    │                                              ViewportInteractionAdapter.cs, TouchGestureEvent.cs, Point2D.cs, Vector2D.cs
    ├── FabricationAssistant.Rendering.Gles       28 files: GlesViewportRenderer.cs (main),
    │                                              GlesNormalDepthRenderer/OutlineRenderer/PickRenderer/SsaoRenderer/GridRenderer,
    │                                              GlesSection{Plane,VisualPlane,Overlay}, GlesFaceHighlightOverlay,
    │                                              GlesMeasurementOverlay, GlesAxisTriadOverlay, GlesTransformGizmoHandle,
    │                                              MsaaSceneFramebuffer(+.ClampSamples), GlesFullscreenTriangle, GlesRenderUtil,
    │                                              ShaderProgram, GpuMesh, GpuScene, SceneUploader, CadEdgeBuilder,
    │                                              ViewportCameraMath, SceneAppearance (struct), GlThreadGuard,
    │                                              AndroidRenderModeShim, GlesRendererBridge
    ├── FabricationAssistant.App.Android           MainActivity.cs (8201 LOC, the orchestrator),
    │                                              AppSettings.cs (551 LOC, SharedPreferences-backed), AppSettingsValueGuards.cs,
    │                                              AppServices.cs (DI bootstrap), AndroidScenePackageState.cs,
    │                                              PreferencesBottomSheet.cs (1314 LOC), RecentFiles{Store,List,Entry,BottomSheet},
    │                                              SafFilePicker.cs, ImportPipeline.cs, ImportFileTypeResolver.cs,
    │                                              AndroidPathComparer.cs, AndroidBomPanel.cs, AndroidModelExplorerPanel.cs,
    │                                              PropertiesPanelBinder.cs, StyledTooltipController.cs,
    │                                              HorizontalResizeTouchListener.cs,
    │                                              Views/ViewportSurfaceView.cs, Views/MultisampleConfigChooser.cs,
    │                                              Tools/AndroidViewportExplodeView.cs, Tools/AndroidQrScannerDialog.cs,
    │                                              Measurement/AndroidMeasure{Integration,Raycaster}, AndroidSectionClipper,
    │                                              AndroidMeshRaycastAcceleration,
    │                                              Resources/{layout,drawable,color,values,mipmap-anydpi-v26}/...
    └── FabricationAssistant.App.Android.Tests     10 test files
```

State flow contract: `AppSettings` (SharedPreferences) -> `SceneAppearance` struct -> `GlesViewportRenderer` via `QueueRendererCommand`. `MainActivity` is the orchestrator (lifecycle, intents, tool modes, picker, gestures). GL work is queued via `ConcurrentQueue<Action<GL>>` and drained on the GL render thread.

---

## 2. Main Android workflows inspected

1. App boot: `MainActivity.OnCreate` -> `AppSettings.Initialize` -> `AppServices.Build` -> DI -> wire viewport / pickers / panels -> `RestoreSavedModel`.
2. File open: nav-rail `OnOpenClicked` -> `SafFilePicker.Pick` -> SAF result -> `ImportPipeline.ImportAsync` -> `GlesViewportRenderer.LoadDocument` (GL thread) -> camera frame.
3. Recent file reopen: `RecentFilesBottomSheet` -> URI -> permission take -> import.
4. Rendering: `GLSurfaceView` -> `GlesRendererBridge.OnDrawFrame` -> `GlesViewportRenderer.Render` (MSAA scene FBO -> SSAO -> blur -> composite -> overlays -> resolve -> blit).
5. Picking: tap -> `ViewportSurfaceView.PickAsync` -> GL thread `GlesPickRenderer.Pick` -> main looper callback -> selection state.
6. Gestures: `MotionEvent` -> `AndroidPointerSource` -> `ViewportTouchGestureRecognizer` -> `ViewportInteractionAdapter` -> `CameraState`.
7. Section/Measure/Explode/Move tools: tool-mode state machine in `MainActivity`, gizmo + overlay state in renderer.
8. S-Pen palm rejection: `AndroidPointerSource.SpenPalmRejectionEnabled` + suppression heuristics.
9. Preferences: `PreferencesBottomSheet` -> `AppSettings.*` setters -> `OnSettingsChanged` -> `ApplySettingsToScene`.
10. Context loss / resume: `OnResume` -> `ApplySettingsToScene` -> `ReloadRendererSceneAfterContextLossAsync` (retained DocumentDto or last URI).
11. Save/restore state: `OnSaveInstanceState` persists URI, camera, selection, sections, FA occurrences.
12. Memory trim: `OnTrimMemory` -> clear raycast acceleration + import cache; `RunningCritical` -> drop `_lastLoadedDocument` and trim GPU transient resources.

---

## 3. Confirmed bugs (P0/P1)

### B1. ShaderProgram leaks vertex shader handle on fragment-compile failure - P1
`Android\src\FabricationAssistant.Rendering.Gles\ShaderProgram.cs:23-24`
```csharp
var vs = CompileShader(ShaderType.VertexShader, vertexSource, $"{name}.vert");
var fs = CompileShader(ShaderType.FragmentShader, fragmentSource, $"{name}.frag"); // if this throws...
```
If `fs` compile throws (a real risk - fragment shaders vary widely across GPUs / driver versions), `vs` is never deleted. `CompileShader` deletes only its own handle on failure (line 54). Multiple program-load attempts after a bad shader leak one shader object each.
**Fix:** wrap `fs` compile in try/catch that calls `_gl.DeleteShader(vs)` and rethrows.

### B2. GpuScene.Load momentarily exposes an empty meshes list to readers - P1
`Android\src\FabricationAssistant.Rendering.Gles\GpuScene.cs:60-73`
```csharp
List<GpuMesh> meshes = SceneUploader.Upload(_gl, document);   // builds new list
Clear();                                                       // disposes old + clears _meshes
_document = document;
_meshes.AddRange(meshes);
```
Between `Clear()` (line 68) and `AddRange` (line 71) the public `Meshes` property reports zero meshes. Any reader on another thread - including a pending GL command from `QueueRendererCommand` - would draw an empty scene and then re-populate. The renderer is single-threaded, but `MainActivity` reads `Renderer.Scene.Meshes` from the UI thread in several places.
**Fix:** populate the new `_meshes` list before disposing the old one (build to local list, then `Clear()` + reassign under a lock or atomic field swap).

### B3. GpuMesh resources allocated in ctor are vulnerable to context loss - P1
`Android\src\FabricationAssistant.Rendering.Gles\GpuMesh.cs:92-98`
The ctor calls `GenVertexArray/GenBuffer` immediately. If a `GpuMesh` is constructed but its `Upload` is not yet called when the EGL context is recreated, the cached handles become stale. `Upload` does not re-`Gen`, it only `BindBuffer`/`BufferData`. The renderer triggers full re-import on `OnSurfaceCreated`, so this is currently hidden, but any deferred-upload path will silently corrupt.
**Fix:** lazy-allocate VAO/VBO/EBO inside `Upload`, or document that GpuMesh creation must be on the GL thread immediately followed by Upload.

### B4. GpuMesh.Upload allocates large managed arrays unpooled - P1
`Android\src\FabricationAssistant.Rendering.Gles\GpuMesh.cs:126,138`
```csharp
var interleaved = new float[vertexCount * 6];   // 24 bytes/vertex
var uintIndices = new uint[indices.Length];     // 4 bytes/index
```
A 5M-vertex CAD assembly = 120 MB on the managed heap per mesh, plus index copy. Multiple meshes uploaded sequentially cause LOH pressure and OOM on 4 GB devices. Same for `UploadEdges` ribbon allocation (line 203 - 60 bytes/segment x potentially millions of segments). Use `ArrayPool<float>.Shared.Rent` or stream directly to GL via `BufferData(null)` + `BufferSubData`.

### B5. AppSettings.Apply hands the renderer arrays the UI thread can mutate later - P1
`Android\src\FabricationAssistant.App.Android\AppSettings.cs:297,302,303,307,317,318,320,355,357` and `MainActivity.cs:9175-9180`
`Apply` allocates `new[] { ... }` arrays for every color/array field on every call (good - avoids true sharing). `MainActivity.ApplySettingsToScene` then queues the appearance struct onto the GL thread. But `SceneAppearance` is a struct holding `float[]` references - a value-type copy of the struct still aliases the *same* arrays. If the user moves a settings slider while a queued command is in flight, `AppSettings.Apply` runs again on the UI thread and re-assigns - the queued copy still holds the OLD arrays (because the struct was copied by value at queue time), but if any callback path mutates the array in place (none does today), the GL thread sees mid-mutation. The current code is correct **only** because every set-path re-allocates. The contract is undocumented at the call site.
**Fix:** add a code comment at `AppSettings.Apply` and `SceneAppearance` that ALL array fields must be replaced, never mutated in place.

### B6. ViewportSurfaceView vsync render request can be lost after OnPause - P1
`Android\src\FabricationAssistant.App.Android\Views\ViewportSurfaceView.cs:81-89, 252-256, 239-243`
On `OnPause`:
1. `ClearPendingRenderCallbacks()` clears both flags to 0.
2. `MainChoreographer.RemoveFrameCallback(_vsyncRenderCallback)` removes the callback IF the call lands on the main thread; otherwise it's posted via `_mainHandler.Post`.
3. If a `RequestRender()` call lands on the main thread between (1) and the posted `Remove`, it sets `_renderRequestScheduled = 1` and posts `PostVsyncRenderCallback`, which re-arms the callback.
**Fix:** in `OnPause`, set a `_paused` flag and have `RequestRender` short-circuit when set, then clear `_paused` in `OnResume`.

### B7. MainActivity event subscriptions outlive Activity disposal for nav buttons - P1
`Android\src\FabricationAssistant.App.Android\MainActivity.cs:451-473` and `9092-9139` (OnDestroy)
All nav-rail buttons get `.Click += handler`. `OnDestroy` unsubscribes `_camera.PropertyChanged`, `_sections.Changed`, `_bodyMove.MovesCommitted`, and the three `_pointerSource.GestureRecognized` handlers - but **none** of `_navOpenButton.Click`, `_navRecentButton.Click`, `_navModelExplorerButton.Click`, `_navBomButton.Click`, `_navBomFlatButton.Click`, `_navSpenPalmButton.Click`, `_navSettingsButton.Click`, the bottom-toolbar buttons, the view-preset buttons, or the section/measure/explode buttons are unsubscribed. Same for `_viewport.SetOnTouchListener`/`SetOnHoverListener`/`SetOnGenericMotionListener` (lines 433-435) - the proxy objects hold `MainActivity` refs but are never cleared.
**Fix:** in `OnDestroy`, null out `_viewport.SetOnTouchListener(null)`, `SetOnHoverListener(null)`, `SetOnGenericMotionListener(null)` and detach button click handlers - or document the activity is `ConfigurationChanges=`-locked.

### B8. AndroidDispatcher.Send 5-second timeout can throw on slow main thread / ANR - P1
`Android\src\FabricationAssistant.Platform.Android\AndroidDispatcher.cs:42-44`
```csharp
if (!done.Wait(TimeSpan.FromSeconds(5)))
    throw new TimeoutException("Timed out waiting for Android main thread dispatch.");
```
On a janky main thread, worker threads invoking `Send` get a `TimeoutException`. If this fires inside an `ImportPipeline` continuation, it becomes a faulted Task that may surface as an unhandled exception.
**Fix:** make the timeout configurable, default longer (30 s), or post and don't wait when the caller doesn't need the result.

### B9. ReloadRendererSceneAfterContextLossAsync re-enters during a fresh open - P1
`Android\src\FabricationAssistant.App.Android\MainActivity.cs:9026-9090`
The guard at line 9036-9038 checks `_loadCts is not null` to skip the reload during an in-flight import. But the check is racy: `_loadCts` is set/cleared from `OnOpenClicked` without locking. If `OnViewportSurfaceCreated` fires from the GL thread while the user is mid-tap-to-open, the reload may start with `_loadCts is null` then collide with the new load. Worst case: two `LoadDocumentOnRendererAsync` calls drive the renderer at once.
**Fix:** guard both paths with `Interlocked.CompareExchange` on a "load-in-flight" int.

### B10. SAF persistable URI permission taken too late - P0
`MainActivity.cs:6915` + `RecentFilesStore.cs:104-118`
Permission is taken AFTER `ImportAsync` completes. Process death during import means the URI in the recents list is unreachable on next launch. Take the persistable permission immediately when the SAF result returns.

### B11. SharedPreferences uses Apply() not Commit() - P1
`AppSettings.cs:370` (`editor.Apply()`); `RecentFilesStore.cs:126-134`.
Apply is fire-and-forget. On crash mid-write, the recents addition or the schema-migration commit can be lost. For recents and schema migration, switch to `.Commit()` (synchronous, guaranteed durable).

### B12. DracoGltfTranscoder loads entire GLB into a managed byte[] and a growing MemoryStream - P1
`DracoGltfTranscoder.cs:30,43-122`
A 900 MB Draco-compressed GLB allocates ~2.7 GB total. OOM crash on lower-memory tablets. Stream decoded chunks to disk.

### B13. ImportPipeline does not serialize concurrent imports against _loadCts - P1
`MainActivity.cs:6924-6927`, `ImportPipeline.cs:40-74`
Rapid tap on "Open" twice can spawn two imports; the first is cancelled but `_lastLoadedDocument` and `_runtimeScene` can be overwritten by the slower one.

### B14. ImportFileTypeResolver trusts SAF-supplied mime type - P1
`ImportFileTypeResolver.cs:5-30`, `ImportPipeline.cs:267-279`
`application/zip` immediately becomes `.fa`. `ValidateLocalFileSignatureAsync` checks `PK` magic - but does not validate internal zip structure.

### B15. Adapter-cached state on AndroidBomPanel rows - P1
`AndroidBomPanel.cs:160, 481-486, 987, 1019-1040`
Selection state and tree expansion are kept on field references rather than node IDs. Scene mutation or filtering can leave the cached `_selectedRow` pointing at a stale row.

### B16. AndroidMeasureRaycaster cache invalidates on Scene reference change only - P1
`AndroidMeasureRaycaster.cs:80-86`, `AndroidMeshRaycastAcceleration.cs:9-10,351`
In-place scene mutation (hide/show, move) is invisible to the cache. Stale ray-triangle hits when the user moves a body, then measures.

### B17. AndroidMeasureIntegration leaks event subscriptions - P1
`AndroidMeasureIntegration.cs:50-59`
Subscribes to `_store.MeasurementsChanged` and `_session.StateChanged` with no `IDisposable`.

### B18. Renderer MSAA-resolve failure loops the entire scene pass - P1
`GlesViewportRenderer.cs:472-812 (continue@807)`
On `TryResolveMsaaFramebuffer` failure the outer `while (true)` restarts the full scene pass, risking cascading GL errors or unbounded retries.

### B19. Multiple overlay VAOs not regenerated on OnSurfaceCreated - P1
`GlesAxisTriadOverlay.cs:37,101-102`, `GlesSectionOverlay.cs:40,260-261`, `GlesMeasurementOverlay.cs:39-49,101`
Constructor-time VAO/VBO creation; on context loss the lazy check inside Render may regenerate too late.

### B20. OutlineRenderer leaves depth test disabled after the mask pass - P1
`GlesOutlineRenderer.cs:131-170`
Mask pass disables depth test, composite pass assumes still-disabled, but `ResetMainFramebufferState` never runs - leaks state to next frame.

### B21. S-Pen hover not wired to the gesture pipeline - P1
`AndroidPointerSource.cs` (no `OnHover`), violates memory note `project_android_hover_spen_required`
`OnGenericMotion` handles only ButtonPress/Release. There is no `OnHover` API on `AndroidPointerSource`. Hover-based highlight pre-pick is dead.

### B22. S-Pen palm rejection arms on hover, not on down - P1
`AndroidPointerSource.cs:207-217,220-242`
`NotifyStylusInput()` is invoked during ANY stylus event, including hover. Finger pointers are suppressed even before the pen touches.

### B23. ValidateLocalFileSignatureAsync accepts truncated GLB/GLTF - P1
`ImportPipeline.cs:264-279`
A 3-byte file containing `gl{` passes validation. Reject below minimum sizes.

---

## 4. Android UI logic problems

- **U1.** `ApplySpenPalmRejectionState(showToast:false)` called twice in `MainActivity.OnCreate` (lines 416 and 469). Redundant.
- **U2.** `_loadCts` mutation has no lock - cancel-and-replace pattern in `OnOpenClicked` is racy with `OnPause`'s `_loadCts.Cancel()` (line 8930-8934).
- **U3.** `_navSpenPalmButton.Click` calls `ToggleSpenPalmRejection()` with a Toast on each toggle - no debounce; fast tap-tap can interleave Toasts.
- **U4.** Tooltip double-dismiss - `StyledTooltipController.cs:86-101` can race on `_popup` between `OnHover` and `Show`.
- **U5.** BOM selection feedback loop - both `AndroidBomPanel.ActionRequested` and `AndroidModelExplorerPanel.NodeSelected` fire to the same VM; can cause `NotifyDataSetChanged` cascades.
- **U6.** `MainActivity.OnConfigurationChanged` (line 8897) re-clamps panel widths and posts a re-render but does not re-bind tooltip controllers; overlay sizes drift on density change.
- **U7.** Bottom-sheet `PeekHeight` hardcoded at 80% of display height (`PreferencesBottomSheet.cs:39`); on small-height landscape tablets the sheet hides the viewport entirely.
- **U8.** `_propertiesPanelExpandedBeforeFullscreen` (MainActivity.cs:225) is captured on entering fullscreen but not validated on exit if the panel was disposed in between.
- **U9.** Overlay views (`_zoomWindowOverlay`, `_loadingOverlay`, `_renderBusyOverlay`, `_contextMenuAnchor`, `_measurementLabelLayer`) are all added as child views in `OnCreate` but **none** are removed in `OnDestroy`.
- **U10.** Touch listener wrappers (TouchProxy/HoverProxy/GenericMotionProxy) are `Java.Lang.Object` subclasses; the JNI bridge holds a global ref. Never nulled in OnDestroy.

---

## 5. Lifecycle and threading problems

- **L1.** `OnSaveInstanceState` (line 8949) reads `_packageSession.SelectedOccurrenceIds.OrderBy(...)` (lines 8959, 8967) without null check on the property accessor - NRE during disposal edge case.
- **L2.** `OnTrimMemory(RunningCritical)` (line 9010-9017) nulls `_lastLoadedDocument`; subsequent `ReloadRendererSceneAfterContextLossAsync` falls back to URI reload, which may have been revoked - no fallback UX.
- **L3.** `GlThreadGuard` captures the thread ID lazily on first call. If first GL call is from a non-GL thread, guard captures wrong thread and all subsequent checks lie.
- **L4.** `ViewportSurfaceView.QueueRendererCommand` warns at 256 pending commands but never drains on `OnPause` - delegates with stale captures may run on `OnResume`.
- **L5.** `AndroidDispatcher` constructor captures `Looper.MainLooper` once. No `Reset` for looper teardown (rare).
- **L6.** `MainActivity._loadCts` is read/written without `Interlocked` from multiple paths (`OnOpenClicked`, `OnPause`, `OnDestroy`, `ReloadRendererSceneAfterContextLossAsync`).
- **L7.** `ObserveLifecycleTask` exception handling unverified - if it doesn't add a `ContinueWith` swallower, faults are lost to the TaskScheduler.
- **L8.** `_undoService.UndoFailed += info => ...` (line 363) is never unsubscribed in `OnDestroy`; `_undoService` is never nulled.
- **L9.** `FabricationAssistantPaths` is static state configured once. Multi-activity or unit tests can race.
- **L10.** SSAO/pick `ReadPixels` without prior `Flush`/`Finish` causes pipeline stalls on tile-based mobile GPUs (`GlesNormalDepthRenderer.cs:190`, `GlesSsaoRenderer.cs:310,328`). **P1**.

---

## 6. Rendering / GLES problems

- **R1.** `ShaderProgram` vs-leak on fs failure (B1, P1).
- **R2.** `ShaderProgram` link failure leaves attached shaders potentially unfreed in non-conformant drivers (P2). Detach + delete shaders before `DeleteProgram` on failure path.
- **R3.** `GpuMesh` ctor-time `Gen*` invalidated by context loss (B3, P1).
- **R4.** `GpuMesh.Upload` LOH allocations unpooled (B4, P1).
- **R5.** `GpuScene.Load` swap window (B2, P1).
- **R6.** `GpuScene.SyncNodeTransforms` recomputes `WorldNormalMatrix`/`WorldBounds` for every node every call without dirty tracking - wasted CPU on visibility-only changes.
- **R7.** `ViewportCameraMath.ClampAspect` clamps to `[0.1, 10.0]` silently (`ViewportCameraMath.cs:104-105`). Foldable / ultrawide tablets may report aspects outside this range.
- **R8.** `SceneUploader.Upload` correctly catches and disposes partial uploads (lines 88-94) - **good** (strength worth noting).
- **R9 (renderer-agent batch, all file:line):**
  - **P1** `GlesViewportRenderer.cs:472-812` MSAA recovery infinite loop (B18).
  - **P1** `GlesViewportRenderer.cs:1062-1076` Section-cap stencil depth ordering desync.
  - **P2** `GlesViewportRenderer.cs:587-594` AO bound to unit 4, ActiveTexture(0) reset - overlays must explicitly bind their unit; one missing bind reads stale AO.
  - **P2** `GlesViewportRenderer.cs:1597-1615` `ResetMainFramebufferState` restores depth/blend/stencil/cull but **not viewport** - any overlay that changes viewport leaks to the next pass.
  - **P1** `GlesOutlineRenderer.cs:131-170` mask pass disables depth test then `ResetMainFramebufferState` is not called (B20).
  - **P2** `GlesSsaoRenderer.cs:233-261,292` last blur FBO binding leaks into read framebuffer for downstream `BindFramebuffer(ReadFramebuffer,...)` callers.
  - **P1** `GlesGridRenderer.cs:152-163` `DepthMask(false)` never restored if exception interrupts the pass.
  - **P1** `GlesViewportRenderer.cs:250-260` `ClearProgramUniformCaches` runs before `DisposeResources`.
  - **P1** `GlesViewportRenderer.cs:299` `_whiteAoTexture` recreated without verifying prior dispose - orphan on double-OnSurfaceCreated.
  - **P1** multiple overlay VAO/VBO stale across context loss (B19).
  - **P2** Line widths > 1.0 in `GlesSectionOverlay`, mesh edges, `GlesMeasurementOverlay`. Android GL ES often caps `GL_ALIASED_LINE_WIDTH_RANGE` at 1.0.
  - **P2** Point sizes (`GlesSectionOverlay.cs:93` pointSize=13; `GlesAxisTriadOverlay.cs:52` 96-180 px) not clamped to `GL_POINT_SIZE_MAX_EXT`.
  - **P1** `ReadPixels` immediate stall on mobile (L10).
  - **P1** FBO incomplete falls back to FBO 0 silently (`GlesNormalDepthRenderer.cs:86-92`, `GlesPickRenderer.cs:102-110`).

---

## 7. File import and SAF problems

- **I1.** **P0** SAF persistable permission taken AFTER import success - dead recents on process death (B10).
- **I2.** **P1** `RecentFilesStore` uses `Apply()` not `Commit()` (B11).
- **I3.** **P1** ContentResolver input stream lifecycle on timeout cancellation (`ImportPipeline.cs:107-117`). Prefer `await using`.
- **I4.** **P1** URI dedup uses `StringComparison.Ordinal` (`RecentFilesList.cs:45`) - percent-encoded variants duplicate.
- **I5.** **P2** `RecentFilesStore.Load.FilterAccessible` (`RecentFilesStore.cs:20-35`) probes via `HasReadableAccess()` - flicker if permission is intermittent.
- **I6.** **P1** `ImportFileTypeResolver` trusts mime type for `.fa` mapping (B14).
- **I7.** **P1** `ValidateLocalFileSignatureAsync` accepts truncated header files (B23).
- **I8.** **P1** Concurrent imports not serialized (B13).
- **I9.** **P1** Draco transcoder loads whole GLB into memory (B12).
- **I10.** **P1** Draco MemoryStream grows unbounded during decode (`DracoGltfTranscoder.cs:43,84-96`).
- **I11.** `AndroidScenePackageState` uses `SHA256.HashData` for deterministic occurrence IDs (line 189-198). Stable; safe.
- **I12.** `AppServices.Build` (line 38-43) calls `ValidateOnBuild = true` - validates DI graph at build time. **Strength**.

---

## 8. Settings / persistence problems

- **S1.** **P1** `AppSettings.Edit` (line 365-371) uses `editor.Apply()` (async). Schema migration commit at line 502 is also `.Apply()` - migration not durable on crash.
- **S2.** **P2** `AppSettings.Apply(ref appearance)` allocates a fresh array per color field on every call. Acceptable today; future hot-path callers would churn.
- **S3.** **P2** Setters call `Prefs.Edit().PutXxx().Apply()` (line 373-375) - one transaction per setter.
- **S4.** **P1** `AppSettings.MsaaSamples` clamp (line 230 + `AppSettingsValueGuards.ClampAndroidMsaaSamples` line 8-14) maps `value > 2 -> 4`. If a future version offers 8x, this read-side clamp downgrades silently with no notice.
- **S5.** **P2** `PreferencesBottomSheet.CreateEmbeddedView` calls `AppSettings.Initialize(ctx)` defensively (line 65) - idempotent so OK.
- **S6.** **P2** `PreferencesBottomSheet.OnDestroy` (line 57) only nulls `OnSettingsChanged`; doesn't dispose color-picker drawables created in `ShowColorPickerDialog`.
- **S7.** **P3** Schema migration drops settings only if value approximately equals OLD default (lines 404-484). Idempotent - good. App killed mid-migration re-runs all removes on next launch.
- **S8.** **P3** Setter clamps are present everywhere but no setter raises a "Changed" event. `PreferencesBottomSheet.NotifySettingsChanged` (line 1458) handles it via the enclosing closures - implicit contract.

---

## 9. Hardening recommendations

1. **Take SAF persistable URI permission immediately on pick** (I1, P0).
2. **Switch RecentFilesStore writes and schema-migration writes to `Commit()`** (S1, I2, P1).
3. **Add a load-in-flight semaphore around `OpenModelUriAsync`/`ReloadRendererSceneAfterContextLossAsync`** (B9, I8, P1).
4. **Wrap `GpuMesh.Upload` large allocations with `ArrayPool`** (B4, P1).
5. **Make `GpuScene.Load` atomic** (B2, P1).
6. **Fix MSAA resolve loop to fall back exactly once** (R9-MSAA, P1).
7. **Call `ResetMainFramebufferState` after every overlay pass** that mutates GL state (R9-multiple, P1).
8. **Regenerate every overlay's VAO/VBO on `OnSurfaceCreated`**, not lazily on next draw (R9-VAO, P1).
9. **Implement S-Pen hover at `AndroidPointerSource`** (B21, P1).
10. **Defer palm rejection arming to ACTION_DOWN** (B22, P1).
11. **Validate file structure beyond magic bytes for `.fa`/`.gltf`/`.glb`** (B14, B23, I6, I7, P1).
12. **Stream Draco transcoding to disk; never load whole GLB into byte[]** (B12, I9, I10, P1).
13. **Add Scene version counter so raycast acceleration invalidates on in-place mutation** (B16, P1).
14. **Make `AndroidMeasureIntegration` IDisposable; unsubscribe events on tool switch** (B17, P1).
15. **Wrap `ShaderProgram` ctor in try/catch to delete `vs` if `fs` fails; detach+delete shaders before deleting failed program** (B1, R2, P1).
16. **Query `GL_ALIASED_LINE_WIDTH_RANGE` and `GL_POINT_SIZE_MAX` at renderer init; clamp**, or expand sub-pixel lines into ribbons (R9-line-width, P2).
17. **Add `Finish()` before `ReadPixels` on mobile or gate behind explicit diagnostic flag** (R9-readpixels, P1).
18. **Propagate FBO-incomplete errors to caller; do not silently fall back to FBO 0** (R9-fbo, P1).
19. **Add `_paused` flag short-circuit in `ViewportSurfaceView.RequestRender`** (B6, P1).
20. **In `OnDestroy`, null the View listeners** and detach all button click handlers (B7, P1).
21. **Log when `ViewportCameraMath.ClampAspect` triggers** (R7, P2).
22. **Selection state on BOM/ModelExplorer should key on node ID, not row reference** (B15, P1).
23. **Document the `SceneAppearance` array-immutability contract at every consumer** (B5, P1).
24. **Lengthen / parameterize `AndroidDispatcher.Send` timeout, or eliminate `Send` in favor of `Post`** (B8, P1).
25. **Lazy-init `GpuMesh` VAO/VBO/EBO in `Upload`** (B3, P1).

---

## 10. Dead code candidates

- **D1.** **`AndroidPathComparer`** (`Android\src\FabricationAssistant.App.Android\AndroidPathComparer.cs`) - SAF/import agent reports zero callers outside its own test file. Verify with `Grep` for `AndroidPathComparer\.` before deletion.
- **D2.** **`GlesViewportRenderer.SetMat3`/`SetMat4` non-shader-program overloads** (`GlesViewportRenderer.cs:1672-1684`) - replaced by the `(ShaderProgram, name, matrix)` overload everywhere; no callers.
- **D3.** **`MainActivity._preferredPointerFallbackLogged`** (line 97) static int - assigned via `Interlocked.Exchange` once for one-shot logging; works but unusual.
- **D4.** **`MainActivity._lastSlowFrameLogTicks`** (`GlesViewportRenderer.cs:56,1234-1241`) - throttle that never resets across mode changes.
- **D5.** **`HorizontalTableScrollTouchListener`** in `AndroidBomPanel.cs:1081-1145` - reimplements horizontal scroll on top of `HorizontalScrollView`. Verify the standard widget cannot do this before keeping.
- **D6.** **`BomPanelAdapter` ">" / "v" expansion text indicator** (`AndroidBomPanel.cs:840`) - redundant with the click-driven expand button.

---

## 11. Simplification opportunities

- **C1.** `AppSettings.Initialize` could use `Interlocked.CompareExchange` instead of lock + `??=`. Marginal.
- **C2.** `AppSettings.Edit` block - skip (no disposable editor on AndroidX).
- **C3.** `GpuScene.IsIdentity(float[])` and `IsIdentity(Matrix4d)` - keep both (intentionally distinct inputs).
- **C4.** `MainActivity`'s 9000+ lines - you have explicitly forbidden splitting.
- **C5.** The migration block in `AppSettings.MigrateDefaultsIfNeeded` (lines 385-503) lists every prior default by hand. A `static readonly (string key, float old)[]` table would shrink the surface and prevent missed entries.
- **C6.** `AndroidScenePackageState.GetNodeIdsForOccurrences` (line 41-56) iterates `scene.NodesById.Values` twice - LINQ chain already optimal.

---

## 12. Prioritized fix plan

### P0 (do first - data loss / unrecoverable)
1. B10 - Persist SAF URI permission immediately at pick.

### P1 (do next - crash / functional failure)
2. R9-FBO-incomplete silent fallback; R9-MSAA recovery loop (B18); R9-Outline depth-test leak (B20); R9-Grid DepthMask leak.
3. B11 - Use `Commit()` for recents + schema migration.
4. B13 - Serialize concurrent imports.
5. B9 - Atomic load-in-flight guard.
6. B12 + I10 - Stream Draco transcode; don't load whole GLB.
7. B23 + B14 - Validate file structure beyond magic bytes.
8. B21 + B22 - S-Pen hover wiring + palm-rejection arm timing.
9. B1 - ShaderProgram vs leak on fs failure.
10. B16 + B17 - Raycast cache version + measurement integration IDisposable.
11. B2 - Atomic `GpuScene.Load`.
12. B4 - Pool large mesh upload buffers.
13. R9-overlay-VAO stale across context loss (B19) - centralize "reinit overlays on OnSurfaceCreated".
14. R9-readpixels stall - gate diagnostic reads behind explicit flag.
15. B7 - OnDestroy view-listener cleanup + button unsub.
16. B6 - Paused-flag short-circuit in RequestRender.
17. B15 - BOM/ModelExplorer ID-based selection.
18. B5 - Document SceneAppearance array-immutability at consumers.
19. B3 - Lazy GpuMesh GL allocation or contract documentation.
20. B8 - AndroidDispatcher.Send timeout.

### P2 (do in hardening pass)
21. R6 - Dirty-track GpuScene.SyncNodeTransforms.
22. R7 - Log when aspect is clamped.
23. R9-line-width / point-size mobile clamping.
24. R9-AO texture unit hygiene + viewport restore.
25. I3 - `await using` for SAF input stream.
26. I4 - Normalize URI before recents dedup.
27. S6 - Dispose color-picker drawables on sheet teardown.

### P3 (chore / docs)
28. U1 - Dedupe ApplySpenPalmRejectionState call.
29. C5 - Migration table refactor.
30. D1, D2, D3, D5, D6 - Dead code deletion after verification.

---

## 13. Safe first patch list

Order matters - apply top-down; each item is a small, mechanical change with low blast radius.

1. **B1** `ShaderProgram.cs:23-24` - wrap `fs` compile in try/catch that calls `_gl.DeleteShader(vs)` and rethrows. ~10 LOC.
2. **R2** `ShaderProgram.cs:32-37` - on link failure, `DetachShader(Handle, vs); DetachShader(Handle, fs); DeleteShader(vs); DeleteShader(fs);` before `DeleteProgram`. ~6 LOC.
3. **B11** `RecentFilesStore.cs:126-134` - swap `Apply()` for `Commit()`. ~1 LOC.
4. **B11 (schema)** `AppSettings.cs:502` - swap `editor.Apply()` for `editor.Commit()` in the migration path only. ~1 LOC.
5. **U1** `MainActivity.cs:469` - remove the second `ApplySpenPalmRejectionState(showToast: false)` call (keep line 416). ~1 LOC.
6. **B10** `MainActivity.cs:6915` (and `SafFilePicker.cs` completion path) - call `RecentFilesStore.TryTakePersistableReadPermission` immediately on SAF result, before import work. ~10 LOC.
7. **B22** `AndroidPointerSource.cs:220-242` - move `NotifyStylusInput()` to fire only when `ev.ActionMasked is Down or PointerDown && ContainsStylusOrEraser(ev)`. ~5 LOC.
8. **R9-grid-depthmask** `GlesGridRenderer.cs:152-163` - wrap pass in try/finally restoring `DepthMask(true)`. ~3 LOC.
9. **R9-outline-restore** `GlesOutlineRenderer.cs:170` - call `ResetMainFramebufferState()` at end of mask pass. ~1 LOC.
10. **R9-msaa-loop** `GlesViewportRenderer.cs:807` - replace `continue` with a `break` + counter-bounded fallback. ~5 LOC.
11. **B2** `GpuScene.cs:60-73` - build new `_meshes` to a local list, then swap into the field atomically. ~10 LOC.
12. **D2** Remove `GlesViewportRenderer.SetMat3`/`SetMat4` orphans (lines 1672-1684). ~12 LOC.

Total: ~65 LOC, no architectural change, no file restructuring, no desktop edits. Re-run `Android/tools/build.ps1` after each block.

---

## 14. Verification plan

### Build
```pwsh
powershell -NoProfile -ExecutionPolicy Bypass -File Android\tools\build.ps1
```
Confirm exit 0 (current baseline: passes).

### Unit tests
```pwsh
powershell -NoProfile -ExecutionPolicy Bypass -File Android\tools\test.ps1
```
Existing files: `SmokeTests`, `MsaaSceneFramebufferTests`, `AndroidPointerSourceTests`, `ViewportTouchGestureRecognizerTests`, `AndroidPathComparerTests`, `AndroidSectionClipperTests`, `AppSettingsValueGuardsTests`, `ImportFileTypeResolverTests`, `RecentFilesListTests`, `ViewportInteractionAdapterTests`.

### Render-queue stress
```pwsh
powershell -NoProfile -ExecutionPolicy Bypass -File Android\tools\run-render-queue-test.ps1
```

### Emulator / device tests (manual checklist)
- App boot from cold start; verify renderer surface appears.
- Open a small GLB via SAF; verify recents entry appears AND survives a process kill (`adb shell am force-stop com.fa.app`).
- Open a large CAD assembly (10+ MB); watch logcat for `FA.RenderQueue` soft-cap warnings and `FA.Renderer` slow-frame log.
- Rotate device under load; verify no double-render / no crash.
- Toggle dark mode mid-session.
- Open Preferences, drag every slider; verify renderer updates live and survives a back-press.
- Reset settings to defaults from Preferences header; verify everything redraws correctly.
- Section tool: place X/Y/Z planes; toggle Fill/Edges; switch to Custom plane.
- Measure tool: PointToPoint, FaceToPoint, FaceToFace, BoundingBox; verify clear button works.
- Body Move: select, drag axis gizmo; undo (verify undo wiring per `_undoService`).
- Explode: drag slider 0 -> 1; verify no leftover offset on disable.
- QR scanner: deny camera permission, verify graceful fallback message.
- S-Pen: hover over viewport; verify hover does NOT trigger palm rejection (after B22 fix).
- Hide/Isolate/X-ray isolation: verify both opaque and background buckets respond.
- Backgrounded > 30 minutes: verify on resume the scene re-uploads from `_lastLoadedDocument` (context-loss recovery).
- Low-memory: trigger `OnTrimMemory` via `adb shell am send-trim-memory <pid> RUNNING_CRITICAL`; verify caches clear without crash.

### Rendering checks (visual)
- Section cap stencil: no z-fighting on cap planes (after R9-cap-stencil fix).
- Outline pass: outlines persist across frame transitions (after R9-outline-restore fix).
- AO: no flashing AO from previous frame on overlay-heavy frames (after R9-AO-unit fix).
- Grid: depth writes restored if grid pass throws (after R9-grid-depthmask fix).
- MSAA toggle Off/2x/4x: no infinite render loop on devices that report `MAX_SAMPLES < requested` (after B18 fix).
- Mobile line width: outlines visible (use ribbon expansion or accept 1 px until the line-width follow-up).

---

## 15. Optional deeper refactors (only if truly justified)

- **DR1.** Make `SceneAppearance` immutable (record / read-only struct with `with`-expressions). Eliminates B5 array-aliasing class. Cost: every field assignment site rewrites; high churn for one bug class. **Recommend only if you keep hitting array-mutation bugs.**
- **DR2.** Centralize "reinit-after-OnSurfaceCreated" into a single registry (`List<Action> _contextCreatedHandlers`). Removes B19 bug class at one place rather than fix-on-touch in each overlay. **Recommended if you find a third or fourth overlay with this same bug.**
- **DR3.** Move `MainActivity._loadCts` plus open/import orchestration into a `ModelLoadController` class. Removes L6 + B9. **Recommended only if you keep hitting concurrent-load bugs in production.**
- **DR4.** Promote `GpuMesh.Upload` to take pooled buffers from a `MeshUploadStreamer` that streams to GL via `BufferData(null) + BufferSubData(chunks)`. Eliminates B4 entirely. **Recommended if you see OOM crashes on 4 GB Quest-class devices.**

---

**Bottom line:** Architecture and contracts are sound (DI bootstrap, dispatcher, paths, GL guard, SceneAppearance contract, schema migrations, EGL context preservation, deterministic occurrence IDs). The big risks are concentrated in **resource-lifetime hygiene around GL context loss, large-allocation paths during import/upload, SAF persistence ordering, and overlay-state bleeds between render passes** - all fixable in the current architecture without restructuring. Build is green; the safe first patch list (~65 LOC across 12 small fixes) clears most of the P0/P1 floor without architectural churn.
