# Android Fabrication Assistant - Full Code Review

**Date:** 2026-05-26
**Branch:** master @ `defff7a8`
**Scope:** Every file under `Android/` excluding `obj/`, `bin/`, generated resources, and `deps/draco/unity/`.
**Effort:** Max. 14 parallel investigation agents, plus targeted manual verification of high-impact findings.
**Build entry point:** `Android/tools/build.ps1` (do not call `dotnet build` directly - the script builds `libdraco_native.so` per ABI first).

> **Important context.** Two project memory entries informing the review (`project_android_renderer_port_state`, dated 2026-05-24) are now stale. Section clipping is no longer "stubbed" - the fragment shaders (`mesh.gles.frag`, `mask.gles.frag`, `normal_depth.gles.frag`, `edge.ribbon.gles.frag`, `section_stencil.gles.frag`) all implement section clipping via `discard` and the C# side wires `uSectionPlaneCount` / `uSectionPlanes` to every program that needs it. Update the memory after reading this report.

---

## 1. Codebase map

```
Android/
  FabricationAssistant.Android.sln
  Directory.Build.props
  global.json
  NuGet.config
  tools/
    build.ps1                    # canonical build (also builds libdraco_native.so per ABI)
    build-libdraco.ps1
  deps/
    prebuilt/{arm64-v8a,x86_64}/libdraco_native.so
    draco/                       # vendored Draco sources (unity decoder, etc.)
  docs/
    build-and-deploy.md
    android-model-explorer-port.md
    superpowers/{plans,specs}/...
  src/
    FabricationAssistant.App.Android/
      MainActivity.cs                                 # 8707 LOC, the god-Activity
      AppServices.cs                                  # tiny DI container
      AppSettings.cs                                  # SharedPreferences store, schema v14
      PreferencesBottomSheet.cs                       # 1473 LOC settings sheet
      ImportPipeline.cs                               # SAF copy + GLB/.fa/Draco dispatch
      SafFilePicker.cs
      RecentFilesBottomSheet.cs / RecentFilesStore.cs
      AndroidBomPanel.cs / AndroidModelExplorerPanel.cs
      PropertiesPanelBinder.cs / StyledTooltipController.cs
      HorizontalResizeTouchListener.cs
      AndroidScenePackageState.cs / AndroidPathComparer.cs
      Measurement/
        AndroidMeasureIntegration.cs
        AndroidMeasureRaycaster.cs
        AndroidMeshRaycastAcceleration.cs
        AndroidSectionClipper.cs
      Tools/
        AndroidViewportExplodeView.cs
        AndroidQrScannerDialog.cs
      Views/
        ViewportSurfaceView.cs                        # GLSurfaceView host + GL command queue
        MultisampleConfigChooser.cs
      Properties/AndroidManifest.xml
      Resources/{layout,values,drawable,color,values-night,mipmap-anydpi-v26}/
    FabricationAssistant.Input.Gestures.Android/
      AndroidPointerSource.cs
      ViewportTouchGestureRecognizer.cs
      ViewportInteractionAdapter.cs
      TouchGestureEvent.cs / Point2D.cs / Vector2D.cs
    FabricationAssistant.Rendering.Gles/
      GlesViewportRenderer.cs                         # 1959 LOC
      GlesRendererBridge.cs                           # IRenderer impl
      ShaderProgram.cs / GlThreadGuard.cs / GlesRenderUtil.cs / GlesFullscreenTriangle.cs
      GpuMesh.cs / GpuScene.cs / SceneUploader.cs / CadEdgeBuilder.cs / ViewportCameraMath.cs
      MsaaSceneFramebuffer.cs / MsaaSceneFramebuffer.ClampSamples.cs
      GlesNormalDepthRenderer.cs / GlesSsaoRenderer.cs / GlesOutlineRenderer.cs
      GlesPickRenderer.cs / GlesGridRenderer.cs
      GlesAxisTriadOverlay.cs / GlesSectionOverlay.cs
      GlesSectionPlane.cs / GlesSectionVisualPlane.cs / GlesTransformGizmoHandle.cs
      GlesMeasurementOverlay.cs / GlesFaceHighlightOverlay.cs
      SceneAppearance.cs / AndroidRenderModeShim.cs
      Shaders/*.gles.{vert,frag}
    FabricationAssistant.Draco.Android/
      DracoNativeDecoder.cs / DracoGltfTranscoder.cs
      DracoExtensionDetector.cs / DracoDecodingGltfImportService.cs
    FabricationAssistant.Platform.Android/
      AndroidDispatcher.cs / IDispatcher.cs
      AndroidPlatformPaths.cs / IPlatformPaths.cs
    FabricationAssistant.Core.Android/
      FabricationAssistantPathsBootstrap.cs
    FabricationAssistant.App.Android.Tests/
      SmokeTests.cs
      AndroidPathComparerTests.cs / AndroidPointerSourceTests.cs
      AndroidSectionClipperTests.cs / MsaaSceneFramebufferTests.cs
      ViewportInteractionAdapterTests.cs / ViewportTouchGestureRecognizerTests.cs
```

ASCII check: `Android/src/**/*.{cs,vert,frag,glsl,xml}` is clean - no non-ASCII bytes found.

---

## 2. Workflows inspected

- App startup / DI / settings bootstrap (`MainActivity.OnCreate` -> `AppSettings.Initialize` -> `AppServices.Build` -> viewport + UI + measurement + gestures).
- Activity lifecycle (OnPause / OnResume / OnDestroy / OnSaveInstanceState / OnTrimMemory / OnLowMemory).
- Touch and stylus chain (`TouchProxy/HoverProxy/GenericMotionProxy` -> `AndroidPointerSource` -> `ViewportTouchGestureRecognizer` -> `ViewportInteractionAdapter` -> `CameraState`; selection / section / gizmo / zoom-window branches in `MainActivity`).
- File open (SAF `OpenDocument` -> `ImportPipeline.CopyToLocalAsync` -> signature check -> `DracoDecodingGltfImportService` -> `GltfImportService` or `FaImportService`).
- Draco decoding (transcode in-place into a temp GLB, decompress, replace bufferViews, then forward to GLTF importer).
- GL thread render pipeline (`GlesViewportRenderer`: normal+depth prepass -> SSAO -> mesh + edges -> outline mask + sobel -> overlays).
- Section cut wiring (sub-modes X/Y/Z/Custom, three-point placement, gizmo translate/rotate, section fill/edges switches, fragment-discard clipping).
- View presets, axis triad overlay, view-cube button affordance (the renderer-side overlay is still absent on Android).
- Body Move tool (gizmo handles, drag, typed-value commit).
- Explode tool (slider, layout, scene transient transforms).
- Measurement tool (Point-to-Point, Face-to-Point, Face-to-Face, Bounding Box) + raycast acceleration cache.
- QR scan flow (camera permission, dialog, payload routing).
- Selection (mesh-index pick result, occurrence-id mirror via `PackageSessionState`, scene-node-id fallback, model-explorer/BOM-panel sync, properties panel binding, X-ray isolation dual bucket).
- Settings flow (PreferencesBottomSheet -> AppSettings -> AppSettings.Apply(ref SceneAppearance) -> renderer + interaction adapter on every OnResume).
- Saved-state restoration (last URI, section planes, package id, hidden/isolated occurrences; camera and selection are **not** saved).
- Recent files (SharedPreferences-backed list + persistable URI permissions).
- Logging (every subsystem writes through `Android.Util.Log` with `FA.*` tags).

---

## 3. Severity legend

- **Critical** - crash, data loss, or near-certain failure on common devices.
- **High** - intermittent failure, silent data corruption, or significant UX defect.
- **Medium** - subtle bug, robustness gap, or correctness drift under stress.
- **Low** - cosmetic, minor robustness, or test coverage gap.

Each finding lists Confidence (Confirmed / Likely / Candidate). Confirmed = verified by direct file read during this review. Likely = strong inference from code shape. Candidate = worth verifying.

A small number of agent-reported findings were **rejected** after manual verification and are flagged in section 4.13.

---

## 4. Findings by area

### 4.1 Startup, DI, lifecycle, threading

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **L1** | `MainActivity.cs:8385-8386` (OnDestroy) | **Critical** | Confirmed | `_loadCts?.Cancel(); _loadCts = null;` - never disposed. Leaks the WaitHandle on every Activity recreation that occurs during an import. **Fix:** add `_loadCts?.Dispose();` between the cancel and the null. |
| **L2** | `AppSettings.cs:79-84` | **High** | Confirmed | `_prefs ??= context.GetSharedPreferences(...)` is racy. Lock around the assignment with a static `_initLock`. The Prefs getter is read-only after init; only the first write must be guarded. |
| **L3** | `MainActivity.cs:472`, `8305`, `8358` | **High** | Confirmed | Three fire-and-forget tasks (`_ = OpenModelUriAsync(uri);`, `_ = ReloadRendererSceneAfterContextLossAsync();` twice). On a throw their stack only surfaces via `TaskScheduler.UnobservedTaskException`. Wrap each with a `ContinueWith(t => { if (t.IsFaulted) Log.Error("FA.Lifecycle", t.Exception?.ToString()); }, TaskScheduler.Default);`. |
| **L4** | `MainActivity.cs:52` (ActivityAttribute) - no override exists | **High** | Confirmed | Activity opts out of recreation on 9 ConfigurationChanges (Orientation, ScreenSize, KeyboardHidden, SmallestScreenSize, ScreenLayout, UiMode, FontScale, Locale, Density, Touchscreen) but never overrides `OnConfigurationChanged`. Dp constants captured at OnCreate (panel widths, dp-converted margins) go stale on rotation. **Fix:** add `OnConfigurationChanged(Configuration newConfig)` and recompute `_propertiesPanelWidthPx`, panel widths, `RequestLayout` panels, `_viewport?.RequestRender()`, reapply fullscreen system bars. |
| **L5** | `MainActivity.cs:8308-8338` | **High** | Confirmed | `OnSaveInstanceState` saves URI / sections / package id / hidden / isolated, but not the camera state or `_selectedNodeIds`. Restore path (`RestoreSavedModel`) reopens the file and fits view; the user loses zoom / framing and selection. **Fix:** serialize `_camera.Clone()` (Position/Target/Up/OrthoWidth/IsPerspective/Near/Far) and the selection int[]; restore after the model has loaded. |
| **L6** | `MainActivity.cs:321` / `OnDestroy` | **Medium** | Confirmed | The `IServiceProvider` (`_services`) is never disposed. Dispose it in OnDestroy so future `IDisposable` singletons get cleaned up. |
| **L7** | `Views/ViewportSurfaceView.cs:185` | **Medium** | Confirmed | `PickAsync` creates a fresh `Handler` on every pick call. Hover picks fire at 60+ Hz with S-Pen. Reuse the existing `_mainHandler` field. |
| **L8** | `MainActivity.cs:8340-8348` (`OnTrimMemory`) | **Medium** | Confirmed | Only clears the raycast cache. Should also prune the import cache (`AppDataRoot/import-cache`), clear the model-explorer/BOM caches, and queue a renderer-side GPU trim at `RunningCritical`. |
| **L9** | `Platform.Android/AndroidDispatcher.cs` (Send) | **High** | Likely | `ManualResetEventSlim.Wait()` with no timeout blocks the caller indefinitely if the main looper is stalled - ANR territory. Add a 5 s timeout and throw `TimeoutException` (or document explicitly that Send is not allowed from input or render paths). |
| **L10** | `Rendering.Gles/GlThreadGuard.cs` | **High** | Likely | `_renderThreadId` is captured once and never reset on EGL surface recreation. With `PreserveEGLContextOnPause=true` this is OK on normal pause/resume, but Android can still tear down the surface; recreation spawns a new render thread and the guard then asserts on every GL call. **Fix:** capture lazily inside `EnsureOnRenderThread` (write on first call, also reset on `Renderer.OnSurfaceCreated`). |
| **L11** | `AndroidPointerSource.cs:54` | **Medium** | Confirmed | `time = DateTime.UtcNow` is captured once per `OnTouch` and reused for every historical batch and the live batch. Velocity-based smoothing in the recognizer degrades. Compute the per-batch time from `motionEvent.GetHistoricalEventTime(h)`. |
| **L12** | `MainActivity.cs:8427` (`ApplySettingsToScene`) | **Medium** | Candidate | Mutates `_viewport.Renderer.Appearance` (a struct with `float[]` arrays) from the UI thread. Renderer copies on each frame, but the array references are read non-atomically. **Fix:** queue the appearance change via `_viewport.QueueRendererCommand("apply-appearance", _ => renderer.Appearance = appearance);`. |
| **L13** | `MainActivity.cs:412` (`_camera.PropertyChanged += OnCameraChanged`) | **Low** | Candidate | If `CameraState` raises events from a non-UI thread, `OnCameraChanged` may touch views off-UI. Marshal explicitly. |
| **L14** | `Platform.Android/AndroidPlatformPaths.cs` ctor | **Low** | Candidate | `Directory.CreateDirectory` runs in the constructor with no synchronization. Two instantiations race during a quick recreate. Make the helper a once-initialized static or guard with a lock. |
| **L15** | `Core.Android/FabricationAssistantPathsBootstrap.cs:56-75` | **Medium** | Confirmed | Bare `catch (Exception)` in `NormalizeToRoot` silently swallows path errors and returns the fallback. Log the swallowed exception before returning. |
| **L16** | `AppServices.cs:36-41` | **Medium** | Confirmed | `FabricationAssistantPaths.Configure` happens **after** `BuildServiceProvider`. Any service that touched `FabricationAssistantPaths` during construction would crash. Today nothing does, but the contract is fragile. **Fix:** Configure before `BuildServiceProvider`, or assert the order in code. |
| **L17** | `AppSettings.cs:359-361` | **Medium** | Confirmed | Every individual `Put*` opens an editor and calls `Apply()`. On first launch (migrations) the batched `MigrateDefaultsIfNeeded` already does the right thing, but a future bulk-write call site will do N synchronous(-ish) writes. Expose a batched helper. |
| **L18** | `ViewportSurfaceView.cs:OnPause` and the Choreographer flags | **Low** | Candidate | `_renderRequestPending` is not cleared on pause (only on detach). A Choreographer frame can fire between `OnPause` and `OnDetachedFromWindow`. Clear both on pause as well. |

### 4.2 UI logic and state synchronization

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **U1** | `MainActivity.cs:2992-3056` (render-mode transition) | **Medium** | Likely | Finally re-enables only when `busyVersion == latest`. If the latest call throws before its finally (e.g., a bug in `ApplySettingsToScene` later), the buttons stick disabled. **Fix:** force `_renderModeChangeInFlight = false` and `UpdateRenderModeButtonStates()` in `OnPause` (and in the catch arms, regardless of version). |
| **U2** | `MainActivity.cs:2102-2105` (`_toolIsolateButton`) | **Low** | Likely | Isolate / Isolate-Xray are enabled on `hasSelection` alone; should also gate on `hasScene`. |
| **U3** | `MainActivity.cs:460-473` (`RestoreSavedModel`) | **Medium** | Confirmed | A process-kill restore re-imports the file (re-copies it to the cache and re-parses). Combined with L5 (no camera state), rotation feels like a full reload. **Mitigation:** retain `_runtimeScene` and `_camera` via `OnRetainCustomNonConfigurationInstance` (or a retained `Fragment`). |
| **U4** | `MainActivity.cs:514` | **Low** | Candidate | `PropertiesPanelBinder.ShowSelection(null, _runtimeScene)` is called before a scene is loaded. Should be null-safe; verify the binder doesn't paint stale data. |
| **U5** | `MainActivity.cs:560-571` (`SetPropertiesPanelWidth`) | **Low** | Likely | Immediate `RequestRender` may render at the old surface size for one frame. Post the render onto the next layout pass. |
| **U6** | `RecentFilesBottomSheet.cs` / `RecentFilesStore.cs` | **Medium** | Likely | Recent files list does not filter URIs that have lost permission - error only surfaces on tap. **Fix:** at `Load` time, attempt a cheap `ContentResolver.Query(uri, null, null, null, null)` and drop ones that throw. |
| **U7** | `RecentFilesStore.Add` | **Medium** | Confirmed | Read-modify-write with no lock. Two concurrent `Add`s (recent picker + new SAF result) can lose one. Lock around load/save or batch into a single `Edit()`. |
| **U8** | Icon-only `MaterialButton`s in MainActivity | **Low** | Likely | Many `_nav*` / `_tool*` / `_section*` / `_view*` buttons have `SetTooltip` (`MainActivity.cs:5552-5585`) but not `ContentDescription`. TalkBack reads "Button" with no purpose. |
| **U9** | `MainActivity.cs:64-69` (section gizmo constants) | **Low** | Confirmed | Constants `SectionGizmoTranslateHitTolerancePx`, `SectionGizmoArcOutlineTolerancePx`, `SectionPlaneHoverHitTolerancePx` are typed "Px" but the surrounding math at `:3452` and `:4218` uses them after multiplying inputs by density. Today the math works because all six constants are conventionally treated as screen pixels. Rename so a future maintainer can't double-convert. |
| **U10** | `MainActivity.cs:2082` (`hasScene`) | **Low** | Confirmed | `_viewport?.Renderer.Scene is not null \|\| _runtimeScene is not null`. Pick one source of truth (prefer `_runtimeScene`). |
| **U11** | `MainActivity.cs:8595` (`ZoomWindowOverlayView.SetRect`) | **Low** | Confirmed | After updating the rect there is no `Invalidate()`; redraw piggy-backs on a `RequestRender` cascade. Call `Invalidate()` explicitly. |
| **U12** | `MainActivity.cs:1747-1750` (`CancelActiveModalTool`) | **Verified safe** | Confirmed | `CancelSectionGizmoDrag()` is invoked (file `:4375-4393`) and does clear `_sectionGizmoDownDip`, `_sectionGizmoExceededDragThreshold`, `_sectionGizmoActive`. Earlier audit-tool claim of state leakage is **rejected** (see 4.13). |
| **U13** | `MainActivity.cs:1734` (`CancelBoundingBoxSelectionMode`) | **Medium** | Likely | Clears `_measureBoundingBoxAwaitingSelection` but not `_measureBoundingBoxBusy`. If an async bbox commit was in flight when the user exits the measure tool, the flag stays set and the bbox button stays disabled on re-entry. **Fix:** also reset `_measureBoundingBoxBusy = false`. |
| **U14** | `MainActivity.cs:5819-5836` (context menu) | **Medium** | Likely | `hasScene` is computed but action handlers (e.g. `ZoomToSelection`) deref `_runtimeScene` directly. Mirror U10 - one source of truth. |
| **U15** | `MainActivity.cs:5930-5957` (context menu colors) | **Medium** | Confirmed | Hardcoded `Color.Argb(218, 31, 32, 36)` for background, `Color.Argb(132, 82, 84, 94)` for stroke, `Color.Argb(54, 45, 212, 191)` for highlight. Move to `colors.xml`. |
| **U16** | `MainActivity.cs:5632-5649` (`ApplySystemBarsForFullscreen`) | **Low** | Candidate | On Android 10+ devices with gesture nav, the bottom gesture indicator overlaps the bottom toolbar in fullscreen. Inset bottom padding using `WindowInsets.Type.navigationBars()`. |
| **U17** | `MainActivity.cs:6235-6273` (`OnViewportHover` exit) | **Low** | Candidate | `MotionEventActions.HoverExit` clears `_bodyMoveGizmoHovered` even when `_bodyMoveGizmoActive != None`. Mid-drag, the visual hover dims while the drag is still accepting input. Gate the hover-clear on active drag state. |
| **U18** | `MainActivity.cs:2668-2706` (`HideSelectedNodes`) | **Low** | Candidate | If the selection is already fully hidden, the method is a no-op with no feedback. Either disable the button or toast "already hidden". |
| **U19** | `MainActivity.cs:2708-2740` (`ShowAllNodes`) | **Low** | Candidate | Calls `ApplyVisibilityState`, then conditionally `ApplyVisibilityToScene` if the state did not change. The redundant fallback hints at an unsynced state. Audit the visibility state machine. |
| **U20** | `MainActivity.cs:2961-2987` and `5344-5371` (renderer command queueing) | **Low** | Candidate | Visibility / transform sync queues GL commands without bound or timeout. Long-running renderer hangs could back the queue up unbounded. Add a soft-cap with a logged warning. |

### 4.3 Touch and gesture handling

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **G1** | `AndroidPointerSource.cs:86-95` (Move) | **Medium** | Confirmed | Historical samples share the same `time` (L11). Affects velocity smoothing only. |
| **G2** | `MainActivity.cs:OnViewportHover` and `MeasurementLabelHoverProxy` | **Medium** | Likely | Measurement label hovers do not re-check `MotionEventToolType.Stylus / Mouse`. Memory note `android-hover-spen-required` says hover must come from S-Pen only - on devices that synthesize hover from touch this can fire spuriously. Add the tool-type filter inside `HandleMeasurementLabelHover`. |
| **G3** | `MainActivity.cs:4056-4072` (S-Pen palm rejection arming) | **Medium** | Confirmed | Palm rejection arms on `ACTION_HOVER_ENTER` rather than on actual stylus touch. With pen ~1 cm above the screen the user's palm cannot rest on the device. Defer arming until the stylus pointer is actually `DOWN`, or gate it on pressure > 0 / distance threshold. |
| **G4** | `MainActivity.cs:OnGestureForToolbarTools` (Zoom Window) | **Medium** | Confirmed | Zoom-window gestures share the same event stream that the interaction adapter sees. The adapter suppresses camera changes via `isNavigationSuppressedAccessor`, but the gesture pipeline still synthesizes orbit events. Mark the event as consumed once Zoom-Window owns it. |
| **G5** | `MainActivity.cs:6410-6450` (`StartNextHoverPick` / `OnHoverPickResult`) | **Medium** | Likely | Hover-pick coalescing keeps only the last pending `_pendingHoverPickX/Y`. Subsequent in-flight results may apply to a position that has since moved further. Tag each pick with a version counter and ignore stale results. |
| **G6** | `ViewportInteractionAdapter.cs:309-315` (`CapturePanZoomAnchor`) | **Medium** | Likely | When pinch scale is ~1.0, the anchored pan fallback (which keeps the gesture centroid stationary) is not applied. Two-finger pan rotates around the pivot rather than under the fingers. **Fix:** always apply the anchored pan correction, not only when scale ≠ 1. |
| **G7** | `AndroidPointerSource.cs:98-113` (`ACTION_UP`) | **Verified OK** | Confirmed | An audit-tool finding suggested the code uses `GetPointerId(0)` incorrectly. **Rejected:** by definition `ACTION_UP` only fires when the last pointer is released, so index 0 is correct. |
| **G8** | `ViewportTouchGestureRecognizer.cs` 3+ finger handling | **Low** | Confirmed by behaviour | Three-finger inputs are silently dropped (state machine returns `Empty`). Document this so a future contributor doesn't assume support. |
| **G9** | `AndroidPointerSource.cs:286-294` (`ActionPointerIndex` fallback) | **Low** | Likely | When `ActionIndex` is out of range the method returns 0. Log a one-time warning so a real driver oddity is visible. |
| **G10** | `MainActivity.cs:7548-7570` (`HandleMeasurementLabelHover`) | **Low** | Confirmed | Non-stylus devices cannot hover-select measurement labels by design (S-Pen only). Documented elsewhere, but no UX hint is shown if the user expects hover on a touch device. |

### 4.4 File import and SAF flow

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **F1** | `ImportPipeline.cs:73-112` (`CopyToLocalAsync`) | **High** | Confirmed | No max-file-size check and no copy timeout. A malicious or hung URI streams indefinitely. **Fix:** query the SAF `_size` column up front, reject `> 2 GB`; wrap the copy in `CancellationTokenSource.CreateLinkedTokenSource(ct).CancelAfter(TimeSpan.FromMinutes(15))`. |
| **F2** | `ImportPipeline.cs:44-46` (extension routing) | **Medium** | Confirmed | When the SAF display name has no extension (common for `application/octet-stream`), the import falls through to `NotSupportedException`. **Fix:** when ext is empty / `.bin`, fall back to `_context.ContentResolver?.GetType(uri)` (`model/gltf-binary` -> `.glb`, `application/zip` -> `.fa`). |
| **F3** | `ImportPipeline.cs:104` (`TryPruneImportCache`) | **Medium** | Confirmed | Cache pruning only runs after a successful copy. Crashes mid-import leave `.part` / `.nobom` orphans. Run a startup prune from `AppServices.Build`. |
| **F4** | `DracoGltfTranscoder.cs:24` (`ReadAllBytes`) | **High** | Confirmed | The entire compressed GLB is read into a `byte[]` and then decompressed into a `MemoryStream`. For 500 MB Draco files the peak heap is `source + decoded + transcoded` - easily 2 GB+. OOM kills the app. **Fix:** cap the input size at e.g. 1 GB and decompress streamingly. Pair with F1. |
| **F5** | `DracoGltfTranscoder.cs:37` (`newBin` grows unbounded) | **High** | Confirmed | No cap on the decoded binary stream. Combine with F4 - a 1 KB Draco blob can decompress to gigabytes. **Fix:** abort once decoded bytes exceed `MaxDecodedBytes`. |
| **F6** | `SafFilePicker.cs:36-44` (`_pending` lifecycle) | **Medium** | Likely | If the Activity dies between launch and callback, `_pending` is never cleared. Next picker throws "A file picker is already active." **Fix:** clear `_pending` in a `Dispose` (or in MainActivity's `OnDestroy`) and add a defensive timeout. |
| **F7** | `MainActivity.cs:8260-8263` (`IsFileAccessRevoked`) | **Medium** | Likely | Only catches `Java.Lang.SecurityException` and `UnauthorizedAccessException`. File-deleted-since-pick raises `FileNotFoundException`. Add `FileNotFoundException` and `IOException` matching `"access"` / `"revoked"` patterns to the friendly bucket. |
| **F8** | `RecentFilesStore.TryTakePersistableReadPermission` | **Medium** | Confirmed | Swallows exceptions silently. Log once on failure so a missing provider is debuggable. |
| **F9** | `DracoDecodingGltfImportService.cs` exception remapping | **Medium** | Confirmed | All native failure modes are coalesced to `NotSupportedException("Draco compression is not supported on this device.")`. Wrong-ABI installations get this misleading message. Log the original exception (with stack trace) to `FA.Draco` before rethrowing. |
| **F10** | `ImportPipeline.cs:114-141` (`StripUtf8BomInPlaceIfPresentAsync`) | **Low** | Confirmed | Finally always tries to delete the temp `.nobom`. Benign (`File.Delete` is idempotent) but reduces log signal. Only delete when the move did not succeed. |
| **F11** | `MainActivity.cs` (display name in logs) | **Low** | Candidate | The full URI / display name lands in logcat at info level. Most CAD names are non-sensitive, but logcat is harvestable. Consider truncating long names. |
| **F12** | `MainActivity.cs` MIME-list for `SafFilePicker.PickAsync` | **Medium** | Candidate | Verify the file-open click handler passes `model/gltf-binary`, `model/gltf+json`, `application/zip` (for `.fa`), and a wildcard. Without `application/zip`, some providers hide `.fa` files. |
| **F13** | `MainActivity.cs:6513-6592` + `LoadDocumentOnRendererAsync` | **High** | Likely | The runtime scene swap from import-thread to GL-thread isn't strictly atomic vs cancellation. Cancellation that fires after `newScene.Load(document)` returns but before `renderer.Scene = newScene` happens can leak the GpuScene. Register a `ct.Register(() => newScene?.Dispose())` and dispose it before completing on cancel. |
| **F14** | `MainActivity.cs:6803-6817` (`BindPackageSessionForScene`) | **Medium** | Likely | If the import is cancelled after `_packageSession.BindToPackage` but before the runtime scene is attached, the package session ends up bound to a scene that never materializes. Unbind on cancel paths. |
| **F15** | `AndroidManifest.xml` permissions | **Verified OK** | Confirmed | CAMERA + GLES feature only; no INTERNET, no `READ_EXTERNAL_STORAGE`. This is correct for SAF-only access on API 24+. Earlier agent's suggestion to add storage permissions was rejected; do not add them. |

### 4.5 Rendering / GLES

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **R1** | `GlesNormalDepthRenderer.cs:84-91`, `GlesOutlineRenderer.cs:68-71` | **Medium** | Confirmed | On FBO incomplete, both log and continue with `_fbo != 0`. Downstream passes sample undefined data. **Fix:** zero `_fbo` and attachment handles on incomplete; have `Render()` early-return on `_fbo == 0`. |
| **R2** | `GlesPickRenderer.cs` `Pick()` | **Medium** | Likely | No `GetError` drain after `glReadPixels`. On Mali / weak integer-attachment drivers, a failed read silently returns 0 ("no hit"). **Fix:** drain errors after `ReadPixels`; log once on failure. |
| **R3** | `GlesViewportRenderer.cs:1275`, `GlesOutlineRenderer.cs:185-198`, `GlesNormalDepthRenderer.cs:309-320` | **Low** | Confirmed | `new float[32]` per frame per program for section uniforms. Cache as a renderer field. |
| **R4** | `GlesViewportRenderer.cs:763-770` | **Medium** | Likely | GL state restore between overlays is implicit. `ResetMainFramebufferState` should explicitly reset depth test, depth mask, cull face, blend, stencil to canonical values so a future overlay that forgets to restore state doesn't break the outline pass. |
| **R5** | `GlesViewportRenderer.LoadEmbeddedShader` (~`:1688`) | **Medium** | Likely | Throws raw `InvalidOperationException` when `GetManifestResourceStream` returns null. With AOT / trimming enabled in Release this is the visible failure mode if a shader gets stripped. **Fix:** add a startup smoke-load helper and explicit `<EmbeddedResource Include="Shaders\**\*.gles.*" />` in csproj. |
| **R6** | `Views/ViewportSurfaceView.cs:200` (`PreserveEGLContextOnPause = true`) + reload path | **Medium** | Confirmed | Context preservation works, so the reload short-circuits in normal use. Edge case: an actual context loss leaves `GpuScene` GLuints stale; reload re-imports the file. **Fix:** when context is recreated and `_runtimeScene` is still in memory, queue a GPU re-upload instead of re-importing. |
| **R7** | Overlay buffer recreation | **Low** | Likely | `GlesAxisTriadOverlay`, `GlesFaceHighlightOverlay`, `GlesMeasurementOverlay` create VAO/VBO once in their constructors. Confirm `Render()` lazily recreates on `_vao == 0` so they survive a real context loss. |
| **R8** | Pick ID encoding (R32UI mesh-array index vs desktop R32I node id) | **Documentation** | Confirmed | Cross-platform consumers of pick results must translate. Already noted by the memory entry; add an XML doc on `GlesPickRenderer.Pick`. |
| **R9** | `ShaderProgram.cs` uniform-location cache | **Medium** | Likely | The cache is not invalidated on `OnSurfaceCreated`. After a real context loss, cached locations refer to deleted programs. **Fix:** add `Clear()` and call it from the renderer's `OnSurfaceCreated`. |
| **R10** | `GpuMesh.cs:112-156` (`Upload` index conversion) | **Medium** | Confirmed | Indices arrive as `ReadOnlySpan<int>` and are cast to `uint[]`. Negative indices wrap to enormous unsigned values and `glDrawElements` reads garbage. **Fix:** validate `>=0` (and `< vertexCount`) before upload. |
| **R11** | `GpuMesh.cs:23` (`IndexCount` is `int`) | **Low** | Confirmed | For meshes > 2.14 B indices the count truncates negative. Unrealistic ceiling for mobile, but flag for future. |
| **R12** | `GpuScene.SyncNodeTransforms` (~`:118-126`) | **Medium** | Confirmed | Bounds are computed from float-precision transformed corners. CAD models translated 1e6 mm from origin jitter. Promote intermediate maths to double, cast to float only at the end. |
| **R13** | `CadEdgeBuilder.cs:70` (`Dictionary` initial capacity) | **Medium** | Likely | Initial capacity equals `mesh.Indices.Length`, far higher than the actual edge count (`vertexCount * ~1.5`). Wastes memory on dense meshes. Use `Math.Max(64, mesh.Indices.Length / 6)`. |
| **R14** | `CadEdgeBuilder` quantization (`1e-5f`) | **Low** | Candidate | At sub-tolerance scales, distinct edges can quantize to the same key and one is dropped. Verify against a model whose finest edge length is near the tolerance. |
| **R15** | `ViewportCameraMath.cs:47-61` (`ProjectionMatrix`) | **High** | Confirmed | Orthographic divides by `Math.Max(aspect, 1e-6)`. Tiny aspect during a transient resize creates extreme distortion. Clamp aspect to a sane range `[0.1, 10.0]` before use. |
| **R16** | `ViewportCameraMath.ViewMatrix` | **Medium** | Likely | `Matrix4d.CreateLookAt(position, target, up)` returns NaNs when position == target. Add a guard in the upload path: if any matrix entry is NaN, skip the upload and log once. |
| **R17** | `GlesAxisTriadOverlay.Render` (~`:89`) | **Low** | Confirmed | Disables depth + culling, enables blend; restores depth/cull but not the previous `glBlendFunc`. Snapshot and restore explicitly. |
| **R18** | `GlesSectionOverlay.cs:70` (fill path) | **Low** | Confirmed | `if (fillVisible && planes.Count > 0)` then iterates; missing null check inside the loop. Add `if (plane != null) AppendPlaneQuad(...)`. |
| **R19** | `GlesNormalDepthRenderer.cs:56-58` (normal texture format `RGBA8`) | **Medium** | Candidate | 8-bit per channel is lossy for surface normals - banding shows in smooth surfaces. Consider `RG16F` with octahedral encoding (the comment claims this already). Validate the shader matches the texture format. |
| **R20** | `GlesOutlineRenderer.cs:54-57` (R8 mask filter Linear) | **Low** | Candidate | Linear filtering on a binary mask blurs the outline edge. Use `TextureMinFilter.Nearest`. |
| **R21** | `GlesPickRenderer.cs:158-161` (Y-flip / bounds) | **Low** | Confirmed | Y-flip is correct. Add `if (glY < 0 || glY >= _height) return null;` as a defensive guard. |
| **R22** | `GlesSsaoRenderer.cs:88-90` (depth-range clamp) | **Medium** | Confirmed | `if (linearDepthMax <= linearDepthMin) linearDepthMax = linearDepthMin + 1f;` papers over an inverted near/far. Log when the inversion happens; better to fail loudly than to silently corrupt SSAO. |
| **R23** | `MsaaSceneFramebuffer.cs` `Ensure` caches `_maxSamples` globally | **Low** | Candidate | If a second GL surface with a different config exists, the cached limit may be wrong. Clear on `OnSurfaceCreated`. |
| **R24** | `MsaaSceneFramebuffer.cs:182-189` (DrainGlErrors loop bound) | **Low** | Confirmed | Drains at most 8 errors. On a misconfigured driver that backs up errors, the next blit reports a phantom failure. Loop until `NoError` or cap at 32. |
| **R25** | `MsaaSceneFramebuffer.cs:165` (`glReadBuffer` before blit) | **Low** | Candidate | Calling `ReadBuffer(COLOR_ATTACHMENT0)` on a renderbuffer-backed FBO is OK by spec but verify on Mali and Adreno that no `GL_INVALID_OPERATION` is reported. |
| **R26** | `MsaaSceneFramebuffer.cs:102` (`Rgb8` color renderbuffer) | **Low** | Candidate | Some drivers internally pad RGB8 to RGBA8; the memory cost is the same but the bandwidth may be higher. Benchmark before changing. |
| **R27** | `GlesRendererBridge.cs:25-31` (OnSurfaceCreated ordering) | **Low** | Candidate | Verify the renderer's `OnSurfaceCreated` does not allocate any size-dependent FBOs - those must wait for `OnSurfaceChanged`. |
| **R28** | `GlesViewportRenderer.cs:625-700` (depth-mask restore around edges) | **Low** | Confirmed | `DepthMask(true)` is restored after the edge pass, but `ResetMainFramebufferState` should set it explicitly so an overlay that disables it cannot leak state. |
| **R29** | `GlesViewportRenderer.cs:381-388` (command queue exception swallowed) | **Low** | Confirmed | Per-command exceptions are caught and logged at warning level. If the command name and exception type are not in the log message, debugging is hard. Include the type name in the warning. |
| **R30** | `mesh.gles.frag:65-68` back-face flip | **Low** | Confirmed | The shader flips back-face normals even when no section is active. With single-sided meshes this can mask pipeline errors. Gate on `uSectionPlaneCount > 0` (or an explicit uniform). |

### 4.6 Section cut and overlays

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **S1** | `MainActivity.cs:_toolSectionsButton` wiring | **Verified** | Confirmed | Section clipping IS implemented via fragment-discard in every relevant shader. The earlier memory claiming it was stubbed is stale. |
| **S2** | View cube affordance vs renderer overlay | **Medium** | Confirmed | `AppSettings.ShowViewCube` is persisted, flows through `SceneAppearance.ShowViewCube`, but there is no `GlesViewCubeOverlay` (file does not exist). The plan doc `2026-05-24-android-desktop-alignment-design.md:253` explicitly notes "Existing `ic_view_cube` button: toggle ShowViewCube and ApplySettingsToScene. (Currently no-op.)". **Fix:** hide the toggle / button affordance until the overlay ships, or build a feature flag. |
| **S3** | `MainActivity.cs:3528-3545` (`PickSectionPoint`) | **High** | Likely | The snapped result is returned without NaN/Inf validation; downstream `AddCustomSectionPoint` builds a plane from it, and a NaN point produces NaN normals. **Fix:** validate finite components; on snap failure, return the ray-plane intersection. |
| **S4** | `MainActivity.cs:3582-3595` (`AddCustomSectionPoint`) | **High** | Likely | Collinearity check `normal.LengthSquared < scale*scale*1e-8`. For very small scales the tolerance collapses; for very large scales it becomes too permissive. Clamp the absolute tolerance floor and ceiling. |
| **S5** | `MainActivity.cs:5161-5171` (`ClearSectionPlacementDraft`) | **Medium** | Confirmed | `_sectionHoverPoint` is referenced in the change-detect comparison but is not cleared. Stale hover point can flash after cancel. Set `_sectionHoverPoint = null` in the clear path. |
| **S6** | `MainActivity.cs:OnGesture` for section custom | **Medium** | Likely | Orbit / pan continue to receive events while the user is placing a three-point custom section. Consider suppressing camera motion until placement completes. |
| **S7** | Body-move / section gizmo mutual exclusion | **Medium** | Likely | Section and body-move share the same gizmo handle enum and the same drag start path. Add an explicit guard: a section gizmo cannot begin a drag while a body-move drag is active and vice versa. |
| **S8** | `GlesAxisTriadOverlay`, `GlesSectionOverlay` | **Low** | Confirmed | Hardcoded axis colors (X red, Y green, Z blue). Acceptable but inconsistent with the rest of the renderer where appearance is driven by `SceneAppearance`. |
| **S9** | `OnSaveInstanceState` section sub-mode | **Low** | Confirmed | Section custom mode is not saved across recreation; on rotation mid-placement the mode resets but the visual prompt does not. Save `_activeSectionSubMode` as a string. |
| **S10** | `Measurement/AndroidSectionClipper.cs:22-26` | **Medium** | Likely | Plane normal is assumed unit length. If a future codepath creates a `SectionPlane` with a non-unit normal, distances scale incorrectly. Either normalize inside `IsPointVisible` or assert in the constructor. |
| **S11** | `AndroidSectionClipper.cs:9` plane tolerance is absolute | **Low** | Likely | `PlaneTolerance = 1e-6` mm is too tight on million-mm models and too loose on millimeter-scale models. Tie to scene diagonal where the call site has the context. |
| **S12** | Section gizmo arc hit test fallback (`MainActivity.cs:4226-4263`) | **Low** | Candidate | 16-sample approximation can miss a hit on small gizmos. Increase to 32 samples or compute a closest-point-on-arc analytically. |

### 4.7 Settings, persistence, manifest, packaging

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **P1** | `AppSettings.cs` color setters | **High** | Confirmed | `GridLineColorR/G/B`, `BackgroundR/G/B`, `SurfaceR/G/B`, `EdgeR/G/B`, `ClaySurfaceR/G/B`, `ClayBackgroundR/G/B`, `ClayFeatureEdgeR/G/B`, `OutlineR/G/B`, `HoverOutlineR/G/B` all use bare `set => Put(...)`. `SectionPlaneR/G/B` and `SectionCapR/G/B` correctly clamp. Add `Math.Clamp(value, 0f, 1f)` everywhere. |
| **P2** | `AppSettings.cs:231` (`OutlineThicknessPx`) | **High** | Confirmed | Getter has no `GetFloatInRange`, setter has no clamp. UI slider is 1.0..8.0 but the setter accepts anything. Mirror `HoverOutlineThicknessPx`'s pattern. |
| **P3** | `AppSettings.cs:174` (`EdgeDepthBias`), `:195-203` (lighting), `:221-222` (contour), etc. | **Medium** | Confirmed | Many non-color floats also lack setter-side clamping. Add `Math.Clamp(value, min, max)` so direct API calls cannot poison persistence. |
| **P4** | `AppSettings.cs:588-600` (`ConvertSectionGizmoSizeToScale`) | **Medium** | Confirmed | Migration relies on the constant `DefaultSectionGizmoSizeFraction = 0.10f`. Bumping that constant retro-actively miscalculates persisted scales. Pin the constant or store the absolute fraction. |
| **P5** | `SceneAppearance.CreateDefault` vs `AppSettings.Apply` | **Low** | Confirmed | Defaults are duplicated. Drift between the two only shows when a default changes. Have `CreateDefault` call `AppSettings.Apply` once or centralize the default table. |
| **P6** | `AppSettings.ResetToDefaults` | **High** | Likely | Clears all keys and bumps schema version, but `PreferencesBottomSheet` controls keep their UI state until the sheet is reopened. **Fix:** after `NotifySettingsChanged`, refresh every visible control (or rebuild the sheet). |
| **P7** | `PreferencesBottomSheet.cs:210-214` (`DimensionHighlight` colors) | **Verified used** | Confirmed | The colors flow through `MainActivity.cs:8452-8455` to `_viewport.Renderer.DimensionHighlightColor` (a `Vector3` property), not through `SceneAppearance`. **Earlier "dead setting" finding rejected.** |
| **P8** | `PreferencesBottomSheet.cs:146-159` (Clay section) | **Medium** | Confirmed | `ClayFeatureEdgeDepthBias` is persisted, applied via `SceneAppearance`, but no UI control exposes it. Either add a slider or remove the persisted key. |
| **P9** | `PreferencesBottomSheet.cs:701, 711, 731, 741` (`G3` float format) | **Medium** | Confirmed | `$"{label}: {v:G3}"` can produce scientific notation for small values (`AoRadius`, `AoBias`) and uses culture-dependent formatting (no `CultureInfo.InvariantCulture`). On German/French locales the decimal separator flips. Use `F4` (or finer for very small ranges) with `InvariantCulture`. |
| **P10** | `PreferencesBottomSheet.cs:640-643, 789-800` | **Medium** | Confirmed | Chevron `ImageView` and color-picker "Pick" button lack `ContentDescription`. TalkBack announces "Button" with no purpose. |
| **P11** | `PreferencesBottomSheet.cs:1183-1189, 1294-1298` (`SetColor` / `SetHue`) | **Medium** | Likely | No `IsFinite` check on the inputs. A corrupted setting passing `float.NaN` propagates through the gradient renderer. |
| **P12** | `AndroidManifest.xml` | **Verified OK** | Confirmed | `CAMERA` + GLES feature only. Earlier suggestion to add `READ_EXTERNAL_STORAGE` / `INTERNET` is **rejected** (SAF-only file access, no networking). |
| **P13** | `csproj` / `Directory.Build.props` | **Medium** | Likely | `<AndroidTargetSdkVersion>` is not declared explicitly (relies on `net8.0-android34.0`). Pin it explicitly to survive SDK updates. Same for `TargetFrameworks` consistency. |
| **P14** | `csproj` AOT / Trimming | **Likely** | Candidate | Release builds: confirm `<PublishTrimmed>` and `<TrimMode>` are set safely or off. DI registrations and embedded shaders rely on reflection/resource lookup; add `<EmbeddedResource Include="Shaders\**\*.gles.*" />` and any required `TrimmerRootDescriptor`. |
| **P15** | `csproj` native libraries | **Low** | Confirmed | `AndroidNativeLibrary` for `libdraco_native.so` is declared per ABI; relative paths reference `deps/prebuilt/...`. CI should fail fast if those .so files are missing - `tools/build.ps1` does build them, but add a guard so a partial repo doesn't silently produce an APK without Draco support. |
| **P16** | `MultisampleConfigChooser.cs` fallback | **Low** | Candidate | Falls back to `configs[0]` if no exact MSAA match found, without re-validating depth/stencil/RGB8. Add a minimum-attributes check. |
| **P17** | `AppSettings.cs:223, 602-608` | **Low** | Candidate | `MsaaSamples` clamps on read AND on write. Redundant but harmless. Document that the read-side clamp exists for forward compat. |

### 4.8 PreferencesBottomSheet UI details

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **PB1** | (See P9) culture-aware float formatting | High | Confirmed | Use `InvariantCulture` + `F4`/`F6` based on range. |
| **PB2** | (See P10) accessibility content descriptions | High | Confirmed | Chevron + pick button. |
| **PB3** | (See P6) reset doesn't refresh visible controls | High | Likely | Rebuild the sheet's view tree after `ResetToDefaults`. |
| **PB4** | `:653-660` section expand state | Low | Likely | Expand/collapse state is local; lost on recreation. Save to instance bundle if user retention matters. |
| **PB5** | `:163` (`BaseColorLift` range 0..0.25) | Low | Candidate | Verify range matches desktop intent; the default `0.1095f` sits 44% along the slider, which is acceptable. |
| **PB6** | `:1452-1472` (`ToggleListener`) | Low | Candidate | Assumes single selection. The MaterialButtonToggleGroup is configured single-select, so safe; document the assumption. |
| **PB7** | dp magic numbers in `Dp(ctx, ...)` calls | Low | Confirmed | Many `Dp(ctx, 6/8/12/16)` constants. Lift to `dimens.xml` (`fa_spacing_small`, `fa_spacing_medium`, ...). |

### 4.9 Measurement subsystem

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **M1** | `AndroidMeshRaycastAcceleration.cs:92-94` | **High** | Confirmed | `RayTriangleIntersectionEpsilon = 1e-10` absolute. On 1e6 mm CAD models the relative epsilon is too tight. Scale by scene diagonal or move to barycentric tolerance. |
| **M2** | `AndroidMeasureIntegration.cs:258-330` (BoundingBox async task) | **High** | Confirmed | The `Task.Run` reads `_sceneAccessor()`; if the scene is unloaded mid-task, the result is committed against a stale scene. **Fix:** capture the scene reference at task start, validate `ReferenceEquals` before committing. |
| **M3** | `AndroidMeasureIntegration.cs:375-391` (`EnforceSingleMeasurementIfNeeded`) | **High** | Likely | Iterates and removes from the store without synchronisation. If the UI thread is concurrently adding/removing, you mutate during enumeration. Snapshot once, mutate by snapshot. |
| **M4** | `MainActivity.cs:6947-6963` (`RefreshMeasurementOverlays`) | **High** | Likely | Builds the presentation and face highlights on UI thread, accessing `MeasurementSession.CurrentDraft`, `SelectedFaces`, and `Store.Snapshot()` without synchronisation. Tap and refresh can race. Either snapshot the entire session at frame start or lock around mutators. |
| **M5** | `AndroidMeasureRaycaster.cs:80-86, 179-190` | **Medium** | Likely | `_cachedScene` and acceleration cache use `ReferenceEquals` to invalidate. If a `MeshDto` is pooled/reused (no current code does this, but future caching might), stale acceleration is served. Add a content-hash or version field. |
| **M6** | `GlesMeasurementOverlay.cs:110-123` | **Medium** | Likely | `worldPerPixelFactor` derived from `projection[5]` silently becomes 0 for degenerate projections, making disks invisible. Clamp to a minimum and fall back to a sensible pixel radius. |
| **M7** | `GlesMeasurementOverlay.cs:267-290` (disk float cast) | **Low** | Likely | Disk center cast to float for the vertex stream loses precision on large world coordinates. Subtract the camera position before cast to keep precision local. |
| **M8** | `GlesFaceHighlightOverlay.cs:64-76` | **Medium** | Likely | Disables depth and cull, enables blend, then re-enables in sequence. An exception between disable and re-enable leaves GL state corrupted for the next overlay. Wrap in try/finally. |
| **M9** | `AndroidSectionClipperTests.cs:27-40` | **Low** | Confirmed | Tests cover only "first 8 planes" semantics. Add tests for NaN normals, point on plane, near-tolerance points. |

### 4.10 Side panels and other tools

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **B1** | `AndroidBomPanel.cs:155` (ListView item-click) | **High** | Likely | `ItemClick` reads `_visibleRows[e.Position]` after the adapter may have mutated. Capture the row identity before posting the action. |
| **B2** | `AndroidBomPanel.cs:639-650` (`MeasureColumnWidths`) | **High** | Confirmed | O(rows × columns) text measurement on every layout. For a 10k-row BOM this is 80k measures per refresh. Cache per row+column with a dirty bit. |
| **B3** | `AndroidBomPanel.cs:554-558, 472-473` | **Medium** | Confirmed | `GetPartOccurrenceCount` is O(n) per call and the selection-after-filter path silently drops the selection without feedback. |
| **B4** | `AndroidModelExplorerPanel.cs:342-348` | **Medium** | Likely | `ScrollToSelected` posts a `SetSelection` without ensuring the adapter has finished a layout pass first; intermittent off-position scroll. |
| **B5** | `AndroidModelExplorerPanel.cs:162-169` (`SetSelection` / `ExpandToRoot`) | **Medium** | Candidate | Path-presented IDs are populated then walked; a tree rebuild mid-call results in expansion on a stale tree. |
| **B6** | `PropertiesPanelBinder.cs:393-398` (`ResolvePresentedNode`) | **Medium** | Likely | Walks the parent chain without null-guarding `presented.Parent` on a node that may have been removed from the scene mid-refresh. |
| **B7** | `PropertiesPanelBinder.cs:45-97` | **Medium** | Likely | No refresh on `PackageInfo` change. Switching between two FA packages leaves stale package rows. |
| **B8** | `StyledTooltipController.cs:109-110` (Dispose) | **Medium** | Likely | Removes listeners before flushing `_handler` callbacks; flip the order so a queued show callback cannot re-attach a listener after detach. Also retains a strong reference to the anchor view - swap to a `WeakReference`. |
| **B9** | `HorizontalResizeTouchListener.cs:56` | **Low** | Likely | Default case returns `_dragging`; pre-drag taps are not consumed, allowing parent ViewGroups to scroll. Return `false` on idle. |
| **B10** | `AndroidViewportExplodeView.cs:76` | **Low** | Confirmed | `scene.GetNode(unit.NodeId)` returning null is silently ignored. Log a warning so deleted nodes don't simply vanish from the explode layout. |
| **B11** | `Tools/AndroidQrScannerDialog.cs:472` (camera parameters) | **High** | Confirmed | `Camera.SetParameters()` is called **after** `SetPreviewDisplay()`. Memory `project_opencv_msmf_gotchas` ("no post-open Set calls (LED flicker)") applies. Move parameter setup before `SetPreviewDisplay`. |
| **B12** | `Tools/AndroidQrScannerDialog.cs:165-205` | **Medium** | Likely | Decode throttle: `elapsedMs < 180.0` check is outside the `Interlocked.CompareExchange`, so two frames within the same 180 ms can both pass the elapsed check and both enter the CAS race. Restructure so the CAS happens first. |
| **B13** | `Tools/AndroidQrScannerDialog.cs:186-187` | **Medium** | Confirmed | `byte[] frame = new byte[data.Length]` per preview frame at 60 fps - ~250 MB/s of allocation pressure. Use a pooled buffer (`ArrayPool<byte>.Shared.Rent`). |
| **B14** | `Tools/AndroidQrScannerDialog.cs:195` (payload length) | **Low** | Likely | No upper bound check before delivering the payload (MainActivity caps at 4096 but the dialog should also defend). |
| **B15** | `Tools/AndroidQrScannerDialog.cs:197` (`_preview?.Post`) | **Low** | Likely | The Task.Run continuation captures `_preview` lazily; if the dialog is dismissed first, the result is dropped silently. Capture locally. |
| **B16** | `Tools/AndroidQrScannerDialog.cs:869` (display orientation) | **Low** | Candidate | `(orientation + 270) % 360` may produce non-standard angles on foldables. Clamp to `{0, 90, 180, 270}` after the calc. |
| **B17** | `AndroidScenePackageState.cs:189-190` (`FormatStableId`) | **Low** | Likely | Normalises to FormC inside the helper; some callers may have already normalised to a different form. Document the contract or normalise at every entry point. |
| **B18** | `AndroidPathComparer.cs:31` | **Low** | Likely | Final `string.CompareOrdinal` is unreachable when both paths are equal up to that point. Either remove or comment. |

### 4.11 MainActivity tool flows (Body Move, Explode, QR, X-ray, hide/isolate, presets, fullscreen)

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **T1** | Body Move - no undo/redo integration | **High** | Confirmed | Per `Core/BodyMove/BodyMoveService.cs:25-26`, the service explicitly notes "Does NOT record history - callers must push a BodyMoveChange to UndoService." `MainActivity.CommitBodyMoveGizmoDrag` (`:4956-4968`) commits but never pushes to undo. Desktop has centralised undo/redo (see `recent commits: Centralize undo/redo across Sections, Move, Measure, Visibility`). Android is missing this wire-up. |
| **T2** | Body Move - state leak on rapid tool switch | **Medium** | Likely | `ActivateBodyMoveTool` does not check `_bodyMoveGizmoActive != None`; switching tools mid-drag can leave the renderer-side gizmo state set. Cancel any active drag in the activator. |
| **T3** | Explode - state not preserved on rotation / scene change | **Medium** | Likely | `_explodeAmount` and `_explodeLayout` are not in `OnSaveInstanceState`. Mid-explode rotation resets to 0%. Save and restore (subject to scene identity match). |
| **T4** | Body Move + orbit race during multi-touch | **Medium** | Candidate | Two-finger input with one on the gizmo and one for orbit can route to both `HandleActiveBodyMoveGizmoTouch` and `_interaction.OnGesture`. Add an early return in `OnGestureForToolbarTools` when a gizmo drag is active. |
| **T5** | QR scanner - no permission rationale dialog | **Medium** | Likely | The flow asks for `CAMERA` permission with no rationale and silently falls through to manual input on denial. Add `ShouldShowRequestPermissionRationale` + a settings deep-link on repeated denial. |
| **T6** | X-ray isolation - dual-bucket gate | **Medium** | Confirmed | Per memory `project_xray_isolation_dual_bucket_gates`, the historical bug is that gates only check the opaque set. Verify in `SetXrayIsolationState` / `SyncRuntimeSceneVisibilityToRenderer` that both buckets are honoured (background non-empty + opaque empty must still apply the effect). |
| **T7** | Context menu accessibility | **Low** | Likely | Items lack content descriptions and accessibility roles. TalkBack reads nothing meaningful. |
| **T8** | Loading overlay cancel button re-enable | **Low** | Candidate | After a tap, `_loadingCancelButton.Enabled = false`. `ShowLoading` does not re-enable on subsequent attempts. Re-enable on each `ShowLoading`. |
| **T9** | Scene swap race in `OpenModelUriAsync` | **High** | Likely | Between import completion and `AttachRuntimeScene`, a newer load can win the version race; `AttachRuntimeScene` does not re-verify the version. Capture `loadVersion` and check before assignment. |
| **T10** | Selection not in instance state | **Medium** | Confirmed | `_selectedNodeIds` and `_packageSession` selection are lost on Activity recreation. Serialise the int[] and the occurrence-id list. |
| **T11** | Fullscreen + gesture nav overlap | **Low** | Candidate | On gesture-nav devices the bottom toolbar overlaps the system gesture pill in immersive sticky. Apply navigation-bar inset padding. |
| **T12** | `MainActivity.cs:5061-5070` Section gizmo label reused for body move | **Low** | Confirmed | `SectionGizmoInputLabel` is reused for body-move typed-input. Rename to `TransformGizmoInputLabel` for clarity. |
| **T13** | Body-move toast spam | **Low** | Candidate | Activating the tool with no selection always shows the prompt toast. After selection arrives the toast still floats; debounce or replace with an inline hint. |
| **T14** | Explode slider type mismatch | **Low** | Confirmed | SeekBar uses int 0..100 progress; `_explodeAmount` is double 0..1 stored separately. Bidirectional sync depends on the progress-changed callback; external state changes can desync. Compute `_explodeAmount` from the slider on demand. |
| **T15** | `OpenRecentFileAsync` removal race | **Low** | Candidate | Two rapid taps on the same recent entry can both trigger import and both attempt to remove on revocation. Acquire a `lock` before mutating the store. |

### 4.12 Tests + build + ABI

| ID | File:line | Severity | Confidence | Issue |
|---|---|---|---|---|
| **TST1** | `SmokeTests.cs:9-17` | Low | Confirmed | Trivial Vector3d.Add test. Provides false sense of coverage. |
| **TST2** | No `AppSettingsTests` | Medium | Confirmed | Schema migrations v1->v14, color clamping, `Apply(ref SceneAppearance)`, `ResetToDefaults` - none are tested. Add a project-local unit test class that uses a fake `ISharedPreferences` (or extract the migration helpers to a static taking a dictionary). |
| **TST3** | No `ImportPipelineTests` | Medium | Confirmed | MIME fallback, size limits, BOM stripping, signature validation. Add tests for cancellation mid-copy, corrupted ZIP, missing inner GLB. |
| **TST4** | No `RecentFilesStoreTests` | Low | Confirmed | Revoked-URI filter on Load, concurrent Add. |
| **TST5** | `AndroidPointerSourceTests.cs` cancel during Locked state | Medium | Likely | Tests cover Cancel during Orbit/PanZoom; no test exists for Cancel after LongPress (Locked). Add. |
| **TST6** | `ViewportInteractionAdapterTests.cs` aspect ratio | Low | Candidate | Tests use square aspect; add a 16:9 case. |
| **TST7** | `AndroidSectionClipperTests.cs` plane normal edge cases | Low | Likely | Add NaN normal and inverted-normal coverage. |
| **TST8** | `tools/build.ps1` smoke | Confirmed | Confirmed | Builds `libdraco_native.so` per ABI, then `dotnet restore` + `dotnet build`. No test invocation. Add a `tools/test.ps1` so CI doesn't drift. |
| **TST9** | `Core.Android.csproj` `AndroidPlatformExclusions.targets` import | High | Candidate | Verify the file exists at the path the import expects; if it is a generated artifact, the build can silently include desktop-only sources. |
| **TST10** | Native lib build-time guard | Low | Likely | `<AndroidNativeLibrary>` references `deps/prebuilt/{abi}/libdraco_native.so`. If a contributor builds without first running `build-libdraco.ps1` and the .so is missing, the APK builds but Draco imports fail at runtime. Add an MSBuild target that fails fast if the file is absent. |

### 4.13 Findings explicitly rejected after manual verification

These were flagged by sub-agents but do not hold up against the code:

- **"Section clipping is stubbed"** - the shaders implement section clipping via `discard`; not stubbed. Memory entry is stale.
- **"`GlesFullscreenTriangle.cs` is missing"** - the file exists (`Android/src/FabricationAssistant.Rendering.Gles/GlesFullscreenTriangle.cs`).
- **"`DimensionHighlightR/G/B` are dead"** - they are applied to `_viewport.Renderer.DimensionHighlightColor` at `MainActivity.cs:8452-8455`. Used.
- **"`SpenPalmRejectionEnabled` is dead"** - applied via `ApplySpenPalmRejectionState` (`MainActivity.cs:380, 433, 1375, 8461`). Used.
- **"AndroidManifest is missing `READ_EXTERNAL_STORAGE` / `INTERNET`"** - SAF is the only file access; networking is not used. The manifest is intentionally minimal.
- **"`ACTION_UP` uses `GetPointerId(0)` wrong"** - by Android contract, `ACTION_UP` only fires when the last pointer is released, so index 0 is correct.
- **"`CancelActiveModalTool` does not clear section gizmo state"** - it does, via `CancelSectionGizmoDrag` at line 1748 (sets `_sectionGizmoDownDip`, `_sectionGizmoExceededDragThreshold`, and the renderer-side gizmo handle).

---

## 5. Hardening recommendations (consolidated)

1. Dispose `_loadCts` (L1) and `_services` (L6) in `OnDestroy`.
2. Lock around `AppSettings.Initialize` (L2).
3. Add exception logging to all three fire-and-forget tasks (L3).
4. Override `OnConfigurationChanged` (L4) and recompute dp constants.
5. Save and restore the camera + selection across recreation (L5, T10).
6. Make `OnTrimMemory` actually free meaningful caches (L8).
7. Add `AndroidDispatcher.Send` timeout (L9).
8. Lazy / resettable thread-id capture in `GlThreadGuard` (L10).
9. Queue `Appearance` mutations on the GL thread (L12).
10. Pull all hardcoded colors into `colors.xml` (U15, R17, etc.); use `GetColorCompat`.
11. Clamp every color and float setter in `AppSettings` (P1, P2, P3).
12. Add max file size + copy timeout in `ImportPipeline` (F1) and Draco bombs guard (F4, F5).
13. MIME fallback for extension detection (F2). Cache pruning at startup (F3).
14. Zero FBO and attachment handles on incomplete-status (R1).
15. Drain GL errors after `glReadPixels` in `GlesPickRenderer` (R2).
16. Cache the section `float[32]` arrays (R3).
17. Clamp aspect in `ProjectionMatrix` (R15) and guard NaN matrices on upload (R16).
18. Verify Y-flip and bounds in pick reads (R21).
19. Hide / disable the `ic_view_cube` toggle until the overlay ships (S2).
20. Wire body-move undo/redo through the existing UndoService (T1).
21. Reuse `_mainHandler` in `PickAsync` (L7).
22. Make `RecentFilesStore` thread-safe and filter revoked URIs (U6, U7).
23. Move QR camera setup before `SetPreviewDisplay`; pool preview frame buffers (B11, B13).

---

## 6. Dead code candidates (only where supported by evidence)

- **`ic_view_cube` toggle path** (S2) - the overlay does not exist; the toggle is documented as no-op in `docs/superpowers/specs/2026-05-24-android-desktop-alignment-design.md:253`. Mark as such or remove until the overlay lands.
- **`_sectionStencilProgram`** (renderer) - created but never executed in the current draw path; kept for the section-cap algorithm that has not landed yet.
- **`AndroidPathComparer.cs:31`** - unreachable `string.CompareOrdinal` (B18).
- **`ClayFeatureEdgeDepthBias`** - persisted and applied, but no PreferencesBottomSheet control (P8). Either add the slider or delete the setting.

(Several agent-reported "dead code" claims for `DimensionHighlight`, `SpenPalmRejectionEnabled`, etc. were rejected; see 4.13.)

---

## 7. Simplification opportunities

- Consolidate the two parallel "is scene loaded" checks (`_viewport?.Renderer.Scene` vs `_runtimeScene`) into one accessor (U10).
- Hoist per-frame `float[32]` section uniform arrays to fields (R3).
- Replace the throwaway `Handler` in `ViewportSurfaceView.PickAsync` with the existing `_mainHandler` (L7).
- Drop the duplicate default values - centralise into a single static (P5).
- Replace `Color.ParseColor` literals with `GetColorCompat(Resource.Color...)` everywhere (U15, R17, B8, etc.).

---

## 8. Prioritized fix plan

**P0 (crash / data-loss / startup-blocker)**

1. **L1** Dispose `_loadCts` in OnDestroy.
2. **L2** Thread-safe `AppSettings.Initialize`.
3. **F4 / F5 / F1** Bound input GLB size, copy timeout, bound Draco decoded size.
4. **R15** Clamp aspect in `ViewportCameraMath.ProjectionMatrix`.
5. **T1** Wire body-move into the centralised UndoService.

**P1 (lifecycle, threading, rendering correctness)**

6. **L3** Exception logging on fire-and-forget tasks.
7. **L4** Override `OnConfigurationChanged`; recompute dp-derived widths.
8. **L5 / T10** Save and restore camera + selection.
9. **L10** Resettable `GlThreadGuard`.
10. **R1** FBO completeness handling (zero handles + early return).
11. **R2** Drain GL errors after `glReadPixels`.
12. **R5** Trim-safe embedded shader loading + smoke load on startup.
13. **R9** Invalidate uniform-location cache on `OnSurfaceCreated`.
14. **R10** Validate index buffer in `GpuMesh.Upload`.

**P2 (UI / settings / measurement)**

15. **P1 / P2 / P3** Clamp every color and float setter.
16. **P6** Refresh the preferences sheet after `ResetToDefaults`.
17. **P9 / PB1** Culture-invariant numeric formatting in the sheet.
18. **U13** Reset `_measureBoundingBoxBusy` on tool exit.
19. **M1** Scene-relative ray-triangle epsilon.
20. **M2** Capture scene reference at bbox task start.
21. **M3 / M4** Snapshot measurement session per frame.
22. **U6 / U7** Recent files filter + lock.
23. **F2** MIME fallback.
24. **F3** Startup cache prune.

**P3 (hardening, dead code, accessibility, theme)**

25. **L7** Reuse `_mainHandler` in `PickAsync`.
26. **L8** Expand `OnTrimMemory`.
27. **L9** `AndroidDispatcher.Send` timeout.
28. **S2** Hide / disable view-cube toggle until overlay ships.
29. **U8 / P10** Add `ContentDescription` on icon-only buttons; tooltip text alone is insufficient.
30. **U15 / R17 / B8** Move hardcoded colors into `colors.xml`.
31. **B11** QR `SetParameters` before `SetPreviewDisplay`.
32. **B13** Pool QR preview frame buffers.
33. **R17 / R18 / R20 / R22 / R28** Explicit GL state restore + log on degenerate ranges.

**P4 (tests, packaging, refactors)**

34. **TST2 / TST3 / TST4** New unit tests around `AppSettings`, `ImportPipeline`, `RecentFilesStore`.
35. **TST9** Confirm `AndroidPlatformExclusions.targets` exists and is in source control.
36. **TST10** Build-time guard for `libdraco_native.so` files.

---

## 9. Safe first-patch list (one PR, fully localized to `Android/`)

```
Android/src/FabricationAssistant.App.Android/MainActivity.cs
  // L1 dispose _loadCts; L6 dispose _services
  // L4 override OnConfigurationChanged
  // L5 save/restore camera; T10 save/restore selection
  // L3 ContinueWith logging on three fire-and-forget tasks
  // L8 expanded OnTrimMemory
  // L12 marshal Appearance writes through QueueRendererCommand
  // U10 single hasScene accessor
  // U13 reset _measureBoundingBoxBusy on tool exit

Android/src/FabricationAssistant.App.Android/AppSettings.cs
  // L2 lock in Initialize
  // P1, P2, P3 clamp every setter

Android/src/FabricationAssistant.App.Android/ImportPipeline.cs
  // F1 size cap + copy timeout
  // F2 MIME fallback
  // F3 startup prune entry point

Android/src/FabricationAssistant.App.Android/SafFilePicker.cs
  // F6 cancel _pending on Activity destroy

Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs
  // L7 reuse _mainHandler in PickAsync

Android/src/FabricationAssistant.App.Android/RecentFilesStore.cs
  // U6 filter revoked URIs on Load
  // U7 lock around Load/Save

Android/src/FabricationAssistant.Draco.Android/DracoGltfTranscoder.cs
  // F4, F5 bound input + decoded size

Android/src/FabricationAssistant.Rendering.Gles/GlesPickRenderer.cs
  // R2 drain + log after ReadPixels
  // R21 bounds guard on glY

Android/src/FabricationAssistant.Rendering.Gles/GlesNormalDepthRenderer.cs
Android/src/FabricationAssistant.Rendering.Gles/GlesOutlineRenderer.cs
  // R1 zero handles on incomplete FBO

Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs
  // R3 cache float[32] for section uniforms
  // R4 explicit ResetMainFramebufferState in canonical form

Android/src/FabricationAssistant.Rendering.Gles/ShaderProgram.cs
  // R9 Clear() on context recreate

Android/src/FabricationAssistant.Rendering.Gles/GpuMesh.cs
  // R10 validate indices on Upload

Android/src/FabricationAssistant.Rendering.Gles/ViewportCameraMath.cs
  // R15 clamp aspect
  // R16 NaN guard

Android/src/FabricationAssistant.Rendering.Gles/GlThreadGuard.cs
  // L10 resettable / lazy capture

Android/src/FabricationAssistant.Platform.Android/AndroidDispatcher.cs
  // L9 5s timeout in Send

Android/src/FabricationAssistant.App.Android/PreferencesBottomSheet.cs
  // PB1 InvariantCulture formatting
  // PB2 ContentDescription on chevron + pick button
  // PB3 refresh controls after ResetToDefaults
  // P8 add (or remove) ClayFeatureEdgeDepthBias control

Android/src/FabricationAssistant.App.Android/Resources/values/colors.xml
  // new fa_measurement_*, fa_tooltip_*, fa_zoom_window_*, fa_loading_overlay_scrim,
  // fa_context_menu_*, fa_axis_*, fa_dimension_preview
Android/src/FabricationAssistant.App.Android/StyledTooltipController.cs
Android/src/FabricationAssistant.App.Android/Tools/AndroidQrScannerDialog.cs
  // B11 move SetParameters before SetPreviewDisplay
  // B13 pool preview frame buffers
  // Replace literal Color.Argb / Color.ParseColor with GetColorCompat
```

All changes additive and reversible; no architectural change.

---

## 10. Verification plan

**Build**

```
Android/tools/build.ps1 -Configuration Debug
Android/tools/build.ps1 -Configuration Release   # exercises AOT/trim path
```

**Unit tests**

```
dotnet test Android/FabricationAssistant.Android.sln
```

Add tests for:

- `AppSettingsTests` - schema migrations v1->v14; clamp color setters; `Apply` writes every field; `ResetToDefaults` clears.
- `ImportPipelineTests` - MIME fallback; oversize file rejection; corrupted ZIP rejection; BOM stripping survives a mid-write crash.
- `RecentFilesStoreTests` - revoked URI filtering at `Load`; concurrent `Add`.
- `DracoTranscoderTests` - bomb rejection; ASCII-only metadata.
- Extend `MsaaSceneFramebufferTests` with maxSamples=0 / negative / 1 cases.

**Emulator / device tests**

- **API 26 emulator** (minSdk boundary): cold start -> open `.glb` -> rotate device -> camera framing preserved (L5).
- **Samsung tablet with S-Pen**: hover -> `HoverMeshIndex` updates only on stylus; palm rejection arms only on actual stylus touch (G3).
- **Low-mem device**: open three large GLBs sequentially; `OnTrimMemory` must release the import cache and second/third opens must succeed (L8).
- **Pixel + Mali emulator**: confirm `GlesPickRenderer.Pick` works; any R32UI driver quirk now logs instead of silently returning "no hit" (R2).
- **Gesture-nav phone (Android 12+)**: fullscreen + bottom toolbar should not collide with the gesture pill (U16).
- **Foldable / unusual rotations**: QR scanner display orientation handled (B16).

**Manual UI checks**

- Toggle every switch in `PreferencesBottomSheet`; reopen the app; every toggle restores.
- Three-point custom section: collinear / off-mesh / NaN-leaning placements all reject cleanly with a toast (S3, S4).
- Recent files after reboot: revoked URIs disappear (U6).
- Render-mode switch under rapid load: buttons never stick disabled (U1).
- Properties panel resize: viewport never paints a frame at the old width (U5).
- Body Move drag -> Undo restores the model (T1).
- Explode mid-progress -> rotate device -> slider position restored (T3).
- QR scanner: deny camera -> rationale -> deny -> settings deep-link offered (T5).

**Rendering checks**

- Open a model larger than the viewport bounds; rotate through all four orientations; FBO sizes follow the surface; no GL errors in `logcat | grep FA.RenderQueue`.
- Force EGL context loss via developer setting "Don't keep activities"; reopen - model is re-uploaded, not re-imported (R6).
- Tap near the four screen corners; pick must select the right object (R21).
- Engage SSAO at extreme camera near/far ratios; log warning when degenerate, no visible artifacts (R22).
- Three-point section with one point on a section plane edge - no crash, no flicker (S3).

---

## 11. Optional deeper refactors (consider only after the patch list lands)

- Move `_runtimeScene` ownership behind a retained `Fragment` or `OnRetainCustomNonConfigurationInstance` to remove the per-rotation full re-import (U3).
- Replace the `AppSettings` static singleton with `ISettingsStore` injected via DI; opens the door to clean unit testing of `Apply` and migrations.
- Generate `SceneAppearance` defaults and the `AppSettings.Apply` body from a single declarative table (C# source generator) - eliminates the duplicate defaults drift (P5).
- Lift the GL command queue into an explicit `IGlCommandBus` interface and add a `WaitForNextFrame` primitive; the current implementation is fine but harder to test.
- Introduce a small `GlStateStack` helper that captures/restores depth, blend, cull, stencil; replace ad-hoc state save/restore in overlays.

---

## 12. Stale memory entries to refresh after this review

- `project_android_renderer_port_state` (2026-05-24) - section clipping is no longer stubbed; `mask.gles.frag` is implemented. View cube overlay is still absent.
- Consider adding a new project memory pointing future sessions to this document.
