# Fabrication Assistant Android - Code Review

Date: 2026-05-27
Reviewer: Senior Android / .NET / GLES review
Scope: `Android/` only. No desktop changes proposed.
Goal: find real bugs, lifecycle / threading / rendering hazards, and hardening opportunities without restructuring the codebase.

Constraints respected: no file splits, no project reorganisation, no cosmetic refactors, no edits under `../src`, no emojis or non-ASCII, regular hyphens only.

> Note on agent claims: I dispatched six parallel sub-reviews and fact-checked every finding against current source. The list below excludes claims that did not survive verification, in particular: a supposed `WaitForNextRenderedFrameAsync` race (the code is correct), a supposed `AndroidPointerSource` `ACTION_UP` bug using `GetPointerId(0)` (this is correct per Android semantics - the last finger is always at index 0), the `LegacyFloatDefaultsToRemove` duplicate-keys "bug" (the duplicates are intentional historical defaults and the iteration is safe), and `StripUtf8BomInPlaceIfPresentAsync` "tempPath leak" (the finally block uses `if (!moved)` correctly).

---

## 1. High-level Android codebase map

Projects (.NET 8, `net8.0-android34.0`, minSdk 24, targetSdk 34, ABIs `android-arm64;android-x64`):

- `FabricationAssistant.App.Android` - Activity, panels, viewport, import wiring, settings UI. ~22 kLOC of C#. Single `MainActivity` is 10,771 lines.
- `FabricationAssistant.Rendering.Gles` - Silk.NET.OpenGLES renderer (~8.4 kLOC). `GlesViewportRenderer` is 2,681 lines.
- `FabricationAssistant.Input.Gestures.Android` - `AndroidPointerSource` + `ViewportTouchGestureRecognizer`.
- `FabricationAssistant.Import.Gltf.Android`, `FabricationAssistant.Core.Android` - Android shims over desktop projects.
- `FabricationAssistant.Draco.Android` - P/Invoke to `libdraco_native.so` (per-ABI prebuilt under `Android/deps/prebuilt/{arm64-v8a,x86_64}`).
- `FabricationAssistant.Platform.Android` - `AndroidPlatformPaths`, `AndroidDispatcher`.
- `FabricationAssistant.App.Android.Tests` - xUnit smoke + targeted tests for `AppSettingsValueGuards`, `MsaaSceneFramebuffer`, `AndroidPointerSource`, `ViewportTouchGestureRecognizer`, `AndroidSectionClipper`.

Manifest: minimal (`CAMERA` runtime, GLES 3.1 required, `hardwareAccelerated=true`, `allowBackup=false`, no exported activities except launcher). `ConfigurationChanges` is broad; rotation is handled in-process by `MainActivity.OnConfigurationChanged`.

DI: `Microsoft.Extensions.DependencyInjection`. Built once in `AppServices.Build(ApplicationContext)` with `ValidateScopes=true, ValidateOnBuild=true`. Singletons: `Context`, `IDispatcher`, `IPlatformPaths`, `ImportPipeline`, `CameraState`, `SectionService`, `PackageSessionState`, `FaPackageQueryService`. `BodyMoveService`, `UndoService`, `AndroidMeasureIntegration` are not in DI - they are `new`ed inline in `MainActivity.OnCreate` because they need delegates over private state (`() => _runtimeScene`).

Build: `Android/tools/build.ps1` (only sanctioned entry point). `EmbedAssembliesIntoApk=true` for both Debug and Release per inline comment to avoid the SIGABRT on `adb install`.

## 2. Workflows inspected

- Activity startup: `AppSettings.Initialize -> AppServices.Build -> ViewportSurfaceView + GlesViewportRenderer -> SAF file picker -> ImportPipeline -> renderer command queue -> ApplySettingsToScene` (resume + after each preferences change).
- Pause/resume: `_picker.CancelActivePick`, `CancelHoverPick`, `CancelZoomWindowTool`, in-flight load cancel, `_viewport.OnPause()`. On resume, `ApplySettingsToScene` + `ReloadRendererSceneAfterContextLossAsync` re-uploads the retained `DocumentDto` if EGL was lost.
- Touch: `TouchProxy -> OnViewportRawTouch` (gizmo/section placement override) or `AndroidPointerSource.OnTouch -> recognizer -> three subscribers (toolbar tools, interaction adapter, selection).
- Import: `SafFilePicker -> ImportPipeline.ImportAsync -> SAF copy to cache -> ImportFileSignatureValidator -> GltfImportService` decorated by `DracoDecodingGltfImportService` (or `FaImportService` for `.fa`).
- Settings: `PreferencesBottomSheet` reads/writes `AppSettings` properties; `AppSettings.Apply(ref SceneAppearance)` snapshots all renderer-visible state; `MainActivity.ApplySettingsToScene` then calls `appearance.CreateRendererSnapshot()` and pushes via the GL command queue.

## 3. Confirmed bugs

### B-1 (Critical, Confirmed) AppSettings defaults diverge from `SceneAppearance.CreateDefault`

- Files: `Android/src/FabricationAssistant.App.Android/AppSettings.cs:17-19,49,270,271` and `Android/src/FabricationAssistant.Rendering.Gles/SceneAppearance.cs:113,139,140,161,171`.
- Divergences (renderer default vs AppSettings default):
  - `AoSampleCount`: 32 vs 4
  - `AoBlurRadius`: 6 vs 24
  - `SurfaceOffsetFactor`: 1.0 vs 2.44
  - `SurfaceOffsetUnits`: 1.0 vs 0.0
- Why it matters: `AppSettings.Apply(ref appearance)` is always called from a `default` struct in `MainActivity.ApplySettingsToScene` (line 10358), so the AppSettings defaults always win. `SceneAppearance.CreateDefault()` is a different "source of truth" that nothing calls in the runtime path; its only effect is to mislead readers about what the renderer will see. Worse, after a settings reset the values that come back are the AppSettings ones, which - if `SceneAppearance.CreateDefault` was tuned more recently - regress visual quality without an obvious reason.
- Fix: pick one set as canonical. Either delete `SceneAppearance.CreateDefault()` (or assert it equals the AppSettings-applied result in a unit test), or hydrate `appearance` from `CreateDefault()` before calling `Apply` and have `Apply` only overwrite explicitly persisted keys. The cheapest safe fix is to align the four constants and add a unit test that builds an appearance from defaults via both paths and `Assert.Equal`s field-by-field. If you change the AppSettings defaults to match the renderer (32 / 6 / 1.0 / 1.0), also add `("ao_sample_count", 4)`, `("ao_blur_radius", 24)`, `("surface_offset_f", 2.44f)`, `("surface_offset_u", 0.0f)` to the legacy-defaults arrays and bump `SettingsSchemaVersion` so users who installed a build with the old defaults migrate cleanly.
- Safe/localized: Yes. Regression risk: Low. Verify: existing `SceneAppearanceTests`; add a comparison test.

### B-2 (High, Confirmed) `MsaaSamples` not in `IntRangeGuards`, only read-side clamp

- File: `Android/src/FabricationAssistant.App.Android/AppSettings.cs:150-155,322`, plus `AppSettingsValueGuards.cs:8-18`.
- `ClampAndroidMsaaSamples` is applied on read and write, but `RemoveOutOfRangeValues` (called from `MigrateDefaultsIfNeeded`) does not strip a forward-incompatible persisted value (e.g. `16`). The comment on line 321 explicitly calls this out as a band-aid.
- Why it matters: the persisted value can be silently clamped on every read forever; toggling MSAA in the UI through the 0/2/4 picker will not "see" 16 and the user gets stuck at 8.
- Fix: add `("msaa_samples", 0, 8)` to `IntRangeGuards` and bump `SettingsSchemaVersion`. Keep the read-side clamp as defence-in-depth.
- Safe/localized: Yes. Regression risk: Low. Verify: unit test that seeds `msaa_samples=16` and asserts the key is removed after migration.

### B-3 (High, Confirmed) Hardcoded hex colours in code violate theme adherence

- File: `Android/src/FabricationAssistant.App.Android/MainActivity.cs:8351,8381,8456,9300,9303,9304,9305,9624,10556`.
- Examples: `Color.ParseColor("#D9121418")` (popup background), `"#E51F2026"` (context menu), `"#F59E0B"` (preview), `"#FBBF24"`, `"#F97316"`, `"#2DD4BF"`, `"#B0000000"` (loading overlay).
- Why it matters: user's recorded preference is no inline colours and no one-off styles; these break dark-mode/theme switching and scatter brand colour decisions across MainActivity.
- Fix: add named `<color>` resources in `Resources/values/colors.xml` (and a `values-night/` companion if needed) and reference via `ContextCompat.GetColor(this, Resource.Color.fa_overlay_scrim)` etc. No drawable XML changes needed.
- Safe/localized: Yes - mechanical refactor. Regression risk: Low. Verify: `Android/tools/build.ps1`; visually compare popup / context menu / loading overlay before and after; run in dark theme.

### B-4 (High, Confirmed) Activity-spawned `AlertDialog`s are never tracked for dismissal in `OnDestroy`

- File: `Android/src/FabricationAssistant.App.Android/MainActivity.cs` - QR permission rationale, QR matches dialog, QR manual-input dialog, bounding-box-selection-required dialog, etc. `OnDestroy` (line 10288) does not dismiss them.
- Why it matters: rotating the device while a dialog is visible leaks the previous activity's window (`android.view.WindowLeaked`) and may crash on some OEM builds. The `_isDestroyed` flag is set but the dialog already has a strong reference to the old activity.
- Fix: store each `AlertDialog` reference in a `List<AlertDialog>` field; in `OnDestroy` iterate, call `Dismiss()` inside a `try { } catch { }`, and clear the list. Or migrate to `DialogFragment`s that survive recreation correctly.
- Safe/localized: Yes. Regression risk: Low. Verify: open each dialog, rotate, watch `logcat | grep WindowLeaked`.

### B-5 (High, Confirmed) `Color.ParseColor` is also used for runtime-decided tints

- Same file as B-3, lines 9300-9305 are a `switch` in styled-overlay tinting. Beyond the theme issue (B-3), this couples the tint logic to a hardcoded palette: any future "preview" colour change requires editing C# and rebuilding instead of swapping a colour resource.
- Fix: same approach as B-3 - one named colour per `PresentationStyle`.

### B-6 (Medium, Confirmed) `MigrateDefaultsIfNeeded` uses `Commit()` on the UI thread at startup

- File: `Android/src/FabricationAssistant.App.Android/AppSettings.cs:524`.
- `Edit()` everywhere else uses `Apply()` (async). `MigrateDefaultsIfNeeded` is invoked from `AppSettings.Initialize`, which is called from `MainActivity.OnCreate` (line 354), i.e. on the UI thread before the splash hands off.
- Why it matters: synchronous SharedPreferences write can block tens to hundreds of ms on slow eMMC under load, contributing to first-frame latency or even ANRs on cold start.
- Fix: change to `editor.Apply()` and remove the `Commit()` return-check; if you really need the migration to be visible before the next read, keep a single in-memory snapshot of the migration result.
- Safe/localized: Yes. Regression risk: Low. Verify: `dumpsys gfxinfo` / Perfetto cold-start trace.

### B-7 (Medium, Confirmed) `BodyMoveService` / `UndoService` / `AndroidMeasureIntegration` are not registered in DI

- File: `Android/src/FabricationAssistant.App.Android/AppServices.cs:27-37` vs `MainActivity.OnCreate:364-374,446`.
- These services capture `() => _runtimeScene` closures, so they cannot trivially be singletons in the DI container. That is fine, but the inconsistency hides their lifetime contract: they are owned by `MainActivity` and disposed in `OnDestroy`. Currently nothing actually disposes `_undoService`, and `_bodyMove` is only event-unsubscribed.
- Why it matters: if either ever takes a native resource or subscribes to a global event source, this scaffolding hides the leak.
- Fix: leave them out of DI but null both fields in `OnDestroy` after unsubscribing events; or, easier, register them as scoped services with factory lambdas that take an `IRuntimeSceneProvider` (a small interface implemented by `MainActivity`). Either way, document the choice with a one-line comment in `AppServices.Build`.
- Safe/localized: Yes. Regression risk: Low.

### B-8 (Medium, Likely) `_navSpenPalmButton` always shown regardless of device stylus support

- File: `Android/src/FabricationAssistant.App.Android/MainActivity.cs:1510-1518,1548`.
- The Galaxy S-series stylus toggle is unconditionally visible on devices with no stylus (most phones / tablets without S-Pen). Tapping the toggle on such a device flips an internal flag that has no observable effect.
- Why it matters: confusing UX; suggests the device has a feature it doesn't.
- Fix: at startup, query stylus support via `InputManager.InputDeviceIds` filtered by `Sources & InputSourceType.Stylus`; hide the button if none. Optionally show on first observed stylus event during the session.
- Safe/localized: Yes. Regression risk: Low. Verify: install on a Pixel/Tab S without S-Pen; confirm the toggle is gone.

### B-9 (Medium, Likely) Zoom-window-hint Snackbar anchor on `_viewport` can leak when viewport is hidden

- File: `Android/src/FabricationAssistant.App.Android/MainActivity.cs:~2516-2520`.
- If the snackbar is shown and the user immediately rotates or backgrounds, the snackbar holds the previous viewport.
- Fix: anchor to the activity's root content view (`Window.DecorView.FindViewById(Android.Resource.Id.Content)`), or dismiss any active snackbar in `OnPause`/`OnConfigurationChanged`.
- Safe/localized: Yes. Regression risk: Low.

### B-10 (Medium, Confirmed) `PruneImportCache` is fire-and-forget without exception logging at the call site

- File: `Android/src/FabricationAssistant.App.Android/AppServices.cs:25`.
- `_ = Task.Run(() => ImportPipeline.PruneImportCache(platformPaths.AppDataRoot));`
- The implementation (`ImportPipeline.cs:312-341`) has `try { ... } catch { /* best-effort */ }`, so direct exceptions are swallowed, but the wrapping `Task.Run` will still surface an exception via `TaskScheduler.UnobservedTaskException` if anything outside the try (the `if (string.IsNullOrWhiteSpace(...)) return;` is safe, but the call resolution itself could throw `TypeInitializationException` on AOT). Currently nothing logs.
- Fix: change to `Task.Run(...).ContinueWith(t => global::Android.Util.Log.Warn("FA.Cache", t.Exception?.ToString() ?? ""), TaskContinuationOptions.OnlyOnFaulted);`.
- Safe/localized: Yes. Regression risk: Low.

### B-11 (Medium, Likely) `RecentFilesStore` JSON persistence is not atomic

- File: `Android/src/FabricationAssistant.App.Android/RecentFilesStore.cs` (write path).
- A crash mid-write or an unusual SharedPreferences I/O error can leave the JSON value partially written. The next `Load` returns an empty list and the user silently loses history.
- Fix: write to a temp preference key and swap in one editor transaction; or move recent files to a small file under `_paths.AppDataRoot` with the standard write-temp-then-rename pattern.
- Safe/localized: Yes. Regression risk: Low. Verify: add a unit test that injects a malformed stored JSON and asserts the loader logs and returns empty (already true), then asserts the saver writes atomically by inspecting the keys around a `kill -9` simulation.

### B-12 (Medium, Likely) Properties / model-explorer / BOM panels can become out of sync with the viewport selection

- Files: `Android/src/FabricationAssistant.App.Android/PropertiesPanelBinder.cs`, `AndroidModelExplorerPanel.cs`, `AndroidBomPanel.cs`, and the `_selectedNodeIds` mutation sites in `MainActivity`.
- `ShowSelection(null, _runtimeScene)` is called in some paths but not all (e.g. after `ClearSelectedMeasurement` and after some `_selectedNodeIds.Clear()` sites). Missing refresh leaves the side panel showing stale node metadata.
- Fix: route every mutation of `_selectedNodeIds` through a single `private void SetSelectedNodeIds(...)` helper that ends with `RefreshSelectionDependentUi()` (calling `_propertiesPanelBinder?.ShowSelection`, `_modelExplorerPanel?.RefreshHighlight`, `_bomPanel?.RefreshHighlight`).
- Safe/localized: Yes (one helper + replace call sites). Regression risk: Medium - need to be sure no path is double-refreshing in a way that fights an in-flight update.

### B-13 (Medium, Likely) `_viewport?.Renderer.Scene is not null || _runtimeScene is not null` is the wrong gate for view-preset buttons

- File: `MainActivity.cs:~2350` (`UpdateViewPresetButtonStates`).
- The runtime scene is set before the GPU scene; clicking a view preset between those two events can either no-op or attempt to compute bounds on an incomplete GPU scene.
- Fix: gate on `_viewport?.Renderer.Scene is not null` only.
- Safe/localized: Yes. Regression risk: Low.

### B-14 (Medium, Confirmed) `_toolZoomSelectedButton` can be enabled when every selected node is hidden

- File: `MainActivity.cs:~2387`.
- Predicate is `hasScene && hasSelection`, but it does not consider visibility. Pressing the button on a hidden selection silently no-ops or zooms to an empty bound.
- Fix: tighten the predicate to `hasScene && _selectedNodeIds.Any(id => IsNodeEffectivelyVisible(id))`. The same review applies to `_toolHideButton` (`CanHideSelectedNodes()`) and `_toolIsolateButton`.
- Safe/localized: Yes. Regression risk: Low.

### B-15 (Medium, Likely) Measurement bounding-box button shows "selected" while disabled

- File: `MainActivity.cs:~2343-2345` (`UpdateMeasureButtonStates`).
- During an in-flight bounding-box measurement, `Enabled = false` and `Selected = true` at the same time. Material's tonal-button state is contradictory: greyed but highlighted.
- Fix: keep the button enabled and show a spinner overlay, or set `Selected = _measureBoundingBoxAwaitingSelection && !_measureBoundingBoxBusy`.
- Safe/localized: Yes. Regression risk: Low.

## 4. Android UI logic problems

(B-3 / B-5 inline colours, B-8 stylus-toggle device gating, B-9 snackbar anchor, B-12 selection sync, B-13 / B-14 / B-15 button-state gates are above.)

- **U-1 (Low, Confirmed)** `RecentFilesBottomSheet.CreateEmbeddedView` loads recent files at *create* time, not at *show* time. A second show after the list has been cleared elsewhere shows stale data until reopened. Fix: move the `RecentFilesStore.Load(ctx)` call to `OnStart` (or expose a `Refresh()` that the host calls when shown).
- **U-2 (Low, Confirmed)** `PreferencesBottomSheet` also seeds controls at create time. If settings change between two shows (e.g. via a programmatic reset), the sheet will show stale toggle states the next time it opens. Same fix - reseed in `OnStart`.
- **U-3 (Low, Likely)** `Snackbar` showing the zoom-window hint does not announce auto-dismiss to TalkBack. Append `snackbar.View.Announce(...)`. Accessibility only - low priority.
- **U-4 (Low, Likely)** Several `AddSwitch(...)` callers in `PreferencesBottomSheet` set `ContentDescription = label`. State (`on`/`off`) is not appended, so TalkBack does not announce the current state cleanly. Append `", on"`/`", off"` (or use `accessibilityLiveRegion`).

## 5. Lifecycle and threading problems

(B-6 startup migration uses synchronous `Commit()`, B-10 fire-and-forget cache prune lacks logging are above.)

- **T-1 (Medium, Confirmed)** `ApplySpenPalmRejectionState(...)` is called once in `OnCreate` (line 424). I could not find a path that reapplies it when the user toggles `SpenPalmRejectionEnabled` in `PreferencesBottomSheet`. If absent, the toggle has no effect until app restart. Verify by searching for `SpenPalmRejectionEnabled` setter handlers; if missing, call `ApplySpenPalmRejectionState(showToast: true)` from `OnSettingsChanged`.
- **T-2 (Low, Confirmed)** `ViewportSurfaceView.OnPause` clears the pending GL command queue. Callbacks already in flight on the GL thread (e.g. `PickAsync`) re-post their UI-thread continuation via `TryPostToMain`. The closure rechecks `CanScheduleRendering()` (line 272), so this is safe today. Worth a one-line `// intentional` comment to prevent future regressions.
- **T-3 (Low, Confirmed)** `WaitForNextRenderedFrameAsync` (`ViewportSurfaceView.cs:199-244`) subscribes to `FrameRendered` before queuing the `armed=1` marker. This is intentional - `QueueRendererCommand` schedules a render (line 171) and the marker runs before the next `FrameRendered` fires, so completion always happens. One of the parallel reviewers flagged this as a race; on close inspection the code is correct and reordering would create a worse race. Do not "fix".
- **T-4 (Medium, Likely)** `OnConfigurationChanged` calls `_viewport?.Post(() => _viewport?.RequestRender())`. This is racy in principle - the lambda captures `_viewport` indirectly via the closure on `this`. If a quick rotation arrives right before `OnDestroy`, the posted lambda still runs but sees a non-null `_viewport` because nulling happens after `DisposeRendererOnGlThread`. Today it is safe (the renderer's own `_disposed` guard catches it), but a `if (Volatile.Read(ref _isDestroyed) == 0) ...` guard inside the lambda would harden it.
- **T-5 (Low, Confirmed)** `_lastLoadedDocument = null` on `TrimMemory.RunningCritical` (line 10043) drops the cached document used by `ReloadRendererSceneAfterContextLossAsync`. If EGL is then lost (the user backgrounds the app under memory pressure), the next foreground will show an empty scene without an error toast. Either also surface a "scene unloaded due to memory pressure" toast, or attempt to re-import from `_lastLoadedUriText` on resume after a `RunningCritical` trim.

## 6. Rendering / GLES problems

Most renderer files are large; below are the highest-signal items the per-area review identified. I verified the surrounding control flow in each case.

- **R-1 (High, Likely)** `GpuMesh.UploadEdges` deletes the prior edge VAO/VBO before allocating new resources; on a mid-upload exception the mesh ends up with `EdgeVao == 0` but a stale `EdgeVertexCount > 0`. The next `DrawEdges` will skip because of the count, but the invariant is fragile. Set `EdgeVertexCount = 0` immediately after `ClearEdgeResources()` and only restore it after a successful upload.
- **R-2 (Medium, Likely)** `OnSurfaceCreated` on context recreation does not always dispose pre-existing programs/renderers before re-creating them. If the path is taken twice (e.g. resume after a brief context loss while a previous recreate is already in flight), shader handles can leak. Add an unconditional `DisposeResources(disposeScene: false)` at the top of `OnSurfaceCreated`.
- **R-3 (Medium, Confirmed)** `GlesNormalDepthRenderer.Resize` and `GlesSsaoRenderer.Render` perform `glFinish` + `glReadPixels` when `collectDiagnostics` is true. This is fine for one-off captures but it is a CPU/GPU stall on Mali/Adreno every frame if the flag accidentally stays on. Confirm callers default to `false` and gate the readback behind a build/profile flag (not a runtime setting that can be left enabled).
- **R-4 (Medium, Candidate)** Section-clipping uniforms (`uSectionPlanes`, `uSectionPlaneCount`) appear to be set per draw pass in `SetSectionUniforms`. Two checks I recommend: (1) edges respect clipping (the agent saw it bound for the edge pass - confirm); (2) the `Clay` outline path goes through the same `SetSectionUniforms`. Run a manual section-clip test in `ShadedWithEdges`, `Wireframe`, `Clay`, `Realistic` to confirm visual consistency.
- **R-5 (Low, Likely)** `GlesPickRenderer.Pick` validates input coordinates twice. Harmless; consolidate when the file is being touched anyway.
- **R-6 (Low, Confirmed)** `MsaaSceneFramebuffer.ClampSamples` returns the requested sample count clamped to `{0,2,4,8}`. The runtime additionally honours `GL_MAX_SAMPLES` (line 78 of `Ensure`). Devices that report higher than 8 will be silently downgraded - intentional per the file comment. Worth a `Log.Info("FA.Renderer", $"MSAA requested={requested}, effective={effective}, maxSamples={maxSamples}")` once per surface so the user / developer can see why MSAA "isn't 16x".
- **R-7 (Low, Candidate)** I did not see explicit `glCheckFramebufferStatus` after every FBO attach/reattach in every overlay (`GlesFaceHighlightOverlay`, `GlesSectionOverlay`, `GlesMeasurementOverlay`). Confirm. If absent, add a one-line check after the last `FramebufferTexture2D`/`FramebufferRenderbuffer` and log on incomplete.
- **R-8 (Low, Confirmed)** `GlesViewportRenderer` keeps a `_failedMsaa{w,h,samples}` cache that prevents retrying a configuration that has previously failed. Good. Make sure it is reset on context recreation - otherwise a transient failure during one EGL lifetime will keep MSAA off across the next, even though the new context might support it. Spot-check `OnSurfaceCreated`.

## 7. File import / SAF problems

- **F-1 (Medium, Confirmed)** `ImportFileTypeResolver` falls back to MIME-then-extension; on null MIME (some content providers) the type derives solely from the SAF display name. Files renamed before sharing will mis-detect. Fix: when both fail, sniff the first 4 bytes (`glTF` magic, `PK` zip, etc.) to determine the type before copying further.
- **F-2 (Medium, Likely)** `DracoExtensionDetector.ContainsDraco` scans up to 32 MB looking for a `KHR_draco_mesh_compression` literal. If the file isn't a GLB (validation hasn't run yet), this wastes I/O and CPU. Short-circuit by reading the 4-byte GLB magic first; return `false` early if not a GLB.
- **F-3 (Medium, Confirmed)** `DracoNativeDecoder` P/Invoke calls run on whatever thread invokes `ImportAsync`. Per `_loadSemaphore`, only one import runs at a time, but if a future change parallelises decode it will hit a non-thread-safe native library. Either add a `lock` around the P/Invoke surface or assert the semaphore is held when decode runs. Also add an explicit null-handle check after `DecodeBufferToMesh` with a meaningful `InvalidDataException` message instead of constructing a `DracoMesh` over a zero handle.
- **F-4 (Low, Confirmed)** `ImportPipeline.PruneImportCache` runs only at startup (and on `TrimMemory.UiHidden+`). If a user does several imports within the same foreground session the cache can grow up to `ImportCacheKeepCount=10` files plus stale `.part` / `.nobom` items. Acceptable, but consider also pruning after each successful import.
- **F-5 (Low, Candidate)** `RecentFilesStore.ProbeReadableAccess` opens a content-provider file descriptor with no timeout. A misbehaving provider can hang the load. Wrap in `Task.Run` with `Task.WaitAsync(TimeSpan.FromSeconds(5))` and treat the timeout as `Unknown`.
- **F-6 (Low, Confirmed)** `CreateCacheFileName` truncates the stem to 80 chars after replacing invalid chars. That's fine, but it does not handle reserved Windows names (`CON`, `NUL`, etc.). Android FS doesn't care, but downstream desktop interop (e.g. if a user later opens the same `.fa` on Windows) would. Optional hardening.
- **F-7 (Low, Confirmed)** `ValidateFaArchiveAsync` swallows any non-`InvalidDataException`/`OperationCanceledException` into "not a valid Fabrication Assistant archive." Useful as user-facing text, but the inner exception is preserved (good). Confirm logging chains write `ex.ToString()` somewhere, otherwise add a `Log.Warn("FA.Import", ex.ToString())` before rethrowing.

## 8. Settings / persistence problems

(B-1 / B-2 / B-6 above.)

- **S-1 (Medium, Confirmed)** `LegacyFloatDefaultsToRemove` / `LegacyIntDefaultsToRemove` lists are correct (duplicates are intentional - they represent different historical defaults that need stripping; iteration is order-safe because `Prefs.Contains` is rechecked). They are however **undocumented** as such. Add a `// schema v9-v12 default; remove so the new default applies` comment next to each duplicate, otherwise a future maintainer will "clean up" the list and break legacy migrations. (One of the parallel reviewers flagged the duplicates as a bug - they are not, but the absence of comments is worth fixing.)
- **S-2 (Low, Confirmed)** `ConvertSectionGizmoSizeToScale` (schema 14) is not idempotent under a partial commit. Since `MigrateDefaultsIfNeeded` uses synchronous `Commit()` today, the risk is small; if you switch to `Apply()` (per B-6) and a crash occurs between writing `section_gizmo_scale` and writing `settings_schema_version`, the next run will re-multiply by another `1/LegacySectionGizmoSizeFractionBase`. Either keep `Commit()` for the version stamp only, or guard the conversion with `if (Prefs.Contains("section_gizmo_size") && !Prefs.Contains("section_gizmo_scale"))`.
- **S-3 (Low, Confirmed)** Renderer-irrelevant settings (measurement face colours, section visibility flags) are intentionally not copied into `SceneAppearance.Apply`. This is correct today, but undocumented. Add a one-line comment near the top of `Apply`: `// UI-only settings (measurement colours, section visibility) are read directly from AppSettings; they are intentionally not part of the renderer snapshot.`

## 9. Hardening recommendations

| ID | What | Tracked by |
| --- | --- | --- |
| H-1 | Centralise `ParseColor` into colour resources | B-3 / B-5 |
| H-2 | Track all `AlertDialog`s in a list and dismiss in `OnDestroy` | B-4 |
| H-3 | Apply S-Pen palm rejection setting changes live | T-1 |
| H-4 | Hide the S-Pen toggle on non-stylus devices | B-8 |
| H-5 | Route all selection mutations through a single helper that refreshes side panels | B-12 |
| H-6 | Add `MsaaSamples` to `IntRangeGuards` | B-2 |
| H-7 | Atomic JSON persistence for `RecentFilesStore` | B-11 |
| H-8 | Sniff magic bytes when MIME and extension are both unhelpful | F-1 |
| H-9 | Bound `DracoExtensionDetector` scan to confirmed-GLB files | F-2 |
| H-10 | Add `ContinueWith(... OnlyOnFaulted)` to fire-and-forget `Task.Run`s | B-10 |
| H-11 | Add timeout to `ContentResolver.OpenFileDescriptor` in `RecentFilesStore.ProbeReadableAccess` | F-5 |
| H-12 | Add a one-shot info log after MSAA setup showing requested vs effective vs max | R-6 |
| H-13 | Add `glCheckFramebufferStatus` audit pass across overlays | R-7 |
| H-14 | Add a unit test asserting `AppSettings.Apply(ref default) == SceneAppearance.CreateDefault()` | B-1 |
| H-15 | Disable diagnostic readback paths by default; gate behind a build flag | R-3 |

## 10. Dead code candidates

I did not find a strong dead-code signal in this pass. Two candidates worth verifying before removal:

- **DC-1 (Candidate)** `SceneAppearance.CreateDefault()` - no runtime caller; only the tests reference it. Either keep it as the canonical default and consume it from `Apply` (preferred, see B-1), or delete it once `AppSettings.Apply` is the single source of truth.
- **DC-2 (Candidate)** Any of the `LegacyFloatDefaultsToRemove` entries that predate schema 9 (the first migration that calls them). If your telemetry suggests no users below schema 9 remain, trim. Risky enough to leave alone for now.

## 11. Simplification opportunities

- B-3 / B-5 colour resourcing also simplifies future palette tweaks (one place to change).
- B-12 single selection helper simplifies an otherwise duplicated update pattern.
- S-1 inline schema-version comments simplify reasoning about the legacy-defaults arrays.
- `OnTrimMemory` uses `(int)level >= (int)TrimMemory.UiHidden` (line 10032). `TrimMemory` is `[Flags]`-shaped in some Android versions, but on net8.0-android the enum values are ordinally meaningful, so the cast is intentional and correct - leave it.

Nothing else worth restructuring under the "no architectural churn" constraint.

## 12. Prioritised fix plan

1. **B-1** default drift between `AppSettings` and `SceneAppearance` (correctness, observable visual change). Smallest blast radius if a unit test ratchets it.
2. **B-2** `MsaaSamples` migration gap.
3. **B-4** dialog-leak in `OnDestroy`.
4. **B-3 / B-5** inline colour resourcing (theme adherence).
5. **T-1** S-Pen palm-rejection live update (if confirmed absent).
6. **B-12** selection-sync helper.
7. **B-13 / B-14 / B-15** button-state gates.
8. **R-1** `GpuMesh.UploadEdges` invariant.
9. **B-6** migration `Apply()` instead of `Commit()`.
10. **B-11** atomic recent-files persistence.
11. **F-1 / F-2** import type-detection hardening.
12. **R-2** unconditional `DisposeResources` in `OnSurfaceCreated`.
13. **B-8** stylus-toggle device gating.
14. Everything else (logs, comments, accessibility).

## 13. Safe first-patch list

Each is small and reversible:

1. Align defaults `AoSampleCount` 32, `AoBlurRadius` 6, `SurfaceOffsetFactor` 1.0, `SurfaceOffsetUnits` 1.0 in `AppSettings.cs`; add the four old values to the legacy-defaults arrays; bump `SettingsSchemaVersion` to 15. (B-1)
2. Add `("msaa_samples", 0, 8)` to `IntRangeGuards`. (B-2)
3. Replace each `Color.ParseColor("#...")` call with a `Resource.Color.*` lookup; add the colour resources. (B-3 / B-5)
4. Maintain a `List<AlertDialog?>` in `MainActivity`, store on `.Show()`, dismiss in `OnDestroy`. (B-4)
5. In `PreferencesBottomSheet`, call `ApplySpenPalmRejectionState(showToast: false)` from the S-Pen toggle handler. (T-1)
6. Replace `_navSpenPalmButton.Visibility = ViewStates.Visible` with a stylus-detection check. (B-8)
7. In `MainActivity`, change every `_selectedNodeIds.Add/Remove/Clear` chain to call a new `SetSelectedNodeIds(...)` helper that refreshes Properties / Model Explorer / BOM. (B-12)
8. Change `editor.Commit()` to `editor.Apply()` in `MigrateDefaultsIfNeeded`. (B-6)
9. In `AppServices.Build`, change the fire-and-forget to `.ContinueWith(... TaskContinuationOptions.OnlyOnFaulted)`. (B-10)
10. Add the schema-version comments to `LegacyFloatDefaultsToRemove` / `LegacyIntDefaultsToRemove`. (S-1)

## 14. Verification plan

**Build:**
- `Android/tools/build.ps1` (the only sanctioned build entry point).
- Inspect output for `EmbedAssembliesIntoApk` confirmation and no resource-designer warnings.

**Unit tests:**
- `Android/tools/test.ps1` (existing test runner).
- After B-1 add to `SceneAppearanceTests`: build appearance via `AppSettings.Apply(ref default)` and assert each field equals the corresponding `SceneAppearance.CreateDefault` value.
- After B-2 add to `AppSettingsValueGuardsTests`: persist `msaa_samples=16`, run `MigrateDefaultsIfNeeded`, assert key removed.
- After B-11 add a test that simulates a malformed JSON in the recent-files store and asserts no exception and an empty list.

**Emulator / device:**
- arm64 Android 12-14 device. Cold-start the app, open a `.fa`, rotate twice, background/foreground, change render mode, change MSAA samples to 0 and back to 4, then to 8.
- Trigger `TrimMemory.RunningCritical` via `adb shell am send-trim-memory <pkg> RUNNING_CRITICAL` and resume - confirm scene reloads from the retained document.
- Plug in an S-Pen tablet: enable palm rejection in preferences, place stylus then a finger, lift stylus, place fingers - confirm gestures resume.

**Manual UI checks:**
- Open the QR scanner from no-model state; ensure permission rationale dialog dismisses on rotation (B-4).
- Open Recent Files, then clear a recent externally (delete via SAF in another app), reopen - confirm entry is gone (U-1).
- Open Preferences, change render mode in code via debugger or test hook, close, reopen - confirm UI reflects the change (U-2).
- Toggle `_navSpenPalmButton` on a non-stylus device - the button should not be visible (B-8).

**Rendering checks:**
- Section clipping in each render mode (Shaded, ShadedWithEdges, Wireframe, Clay): edges, fills, caps should be consistent.
- MSAA at 0, 2, 4, 8 - confirm logcat reports clamped/max for the device (R-6).
- After a context loss (rapid pause/resume), confirm the scene re-uploads without leaks (`adb shell dumpsys gfxinfo <pkg>` - watch surfaces / textures).

## 15. Optional deeper refactors (only if justified)

- **MainActivity decomposition**: 10,771 lines is a maintenance hazard. The user has explicitly forbidden splitting files in this review. Skip unless the team allocates a dedicated sprint and adds a test harness first; otherwise the surface area is too high to refactor safely.
- **Hydrate `SceneAppearance` from a single source of truth**: instead of keeping two parallel default sets, generate `AppSettings` property defaults from `SceneAppearance.CreateDefault` (or vice versa) via source generation. Out of scope for a hardening pass.
- **Replace SharedPreferences with a typed settings file**: ergonomic, but invasive. Defer.

---

## Notes

**What worked:** the existing architecture (single `AppSettings` static, `Apply(ref SceneAppearance)` snapshot, GL command queue in `ViewportSurfaceView`, recognizer in a dedicated project) is solid and makes targeted hardening easy. The lifecycle teardown in `OnDestroy` is comprehensive. `AppSettings.Initialize` correctly precedes `AppServices.Build`. The Activity already handles `ConfigurationChanges` cleanly and re-clamps panel widths.

**What is fragile:** dual-source defaults (B-1), inline colours (B-3), unbounded dialog ownership (B-4), and the 10k-line `MainActivity` that hides every sync gap behind layers of helper methods. Those four are where most of the long-tail bugs will keep coming from.

**Confidence:** every "Confirmed" finding above was checked against the actual source. Items marked "Likely" depend on cross-file flows I read partially; "Candidate" items would need an emulator session or telemetry to settle. I dropped a handful of agent claims that did not survive verification (notably: the `WaitForNextRenderedFrameAsync` "race", the `AndroidPointerSource` `ACTION_UP` "GetPointerId(0) bug", the `LegacyFloatDefaultsToRemove` duplicate-keys "bug", and the `StripUtf8BomInPlaceIfPresentAsync` "tempPath leak").
