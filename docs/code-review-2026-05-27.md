# Fabrication Assistant Android - Senior Code Review

Date: 2026-05-27
Reviewer: Senior Android / .NET review
Scope: `Android/` only. No desktop changes proposed.
Goal: find real bugs, lifecycle / threading / rendering hazards, and hardening opportunities without restructuring the codebase.

Constraints respected: no file splits, no project reorganisation, no cosmetic refactors, no edits under `../src` outside this review document, no emojis or non-ASCII in source / GLSL, regular hyphens only in prose.

> Note on agent claims: I dispatched four parallel sub-reviews; the verified findings below pass my fact-check. Several other agent-proposed bugs (HorizontalResizeTouchListener density, color-picker orphan dialogs, slider thrashing, glReadPixels format, ActivityFlags wrong type, S-Pen hover-in-recognizer) do NOT hold up against the actual code and are explicitly excluded.

---

## 1. High-level Android codebase map

```
Android/
  FabricationAssistant.Android.sln
  tools/build.ps1                       (drives the Android solution only)
  deps/prebuilt/{arm64-v8a,x86_64}/libdraco_native.so
  src/
    FabricationAssistant.App.Android/   (.NET 8 Android app: net8.0-android34.0)
      MainActivity.cs                   (9956 LOC, single Activity, [Activity] attribute-driven)
      AppServices.cs                    (DI bootstrap, singletons)
      AppSettings.cs                    (SharedPreferences-backed, schema v14, Apply(ref SceneAppearance))
      PreferencesBottomSheet.cs         (1572 LOC, programmatic settings UI)
      SafFilePicker.cs                  (ActivityResultContracts.OpenDocument wrapper)
      ImportPipeline.cs                 (SAF URI -> cache -> gltf/glb/fa import)
      ImportFileTypeResolver.cs         (display-name + MIME fallback to extension)
      ImportFileSignatureValidator.cs   (.glb / .gltf / .fa signature checks)
      RecentFilesStore.cs               (JSON-encoded recent file list, MaxEntries=10)
      ViewportSurfaceView.cs            (GLSurfaceView host + GL command queue + vsync coalescing)
      MultisampleConfigChooser.cs       (EGL config chooser)
      AndroidModelExplorerPanel.cs      (scene tree explorer)
      AndroidBomPanel.cs                (BOM hierarchical / consolidated)
      PropertiesPanelBinder.cs          (properties panel binder)
      RecentFilesBottomSheet.cs
      AndroidScenePackageState.cs       (FA-package occurrence selection / visibility)
      HorizontalResizeTouchListener.cs  (panel divider drag)
      AppSettingsValueGuards.cs         (clamp helpers; MSAA bucket clamp)
      StyledTooltipController.cs        (touch-mode tooltips)
      Tools/AndroidQrScannerDialog.cs   (ZXing + legacy android.hardware.Camera)
      Tools/AndroidViewportExplodeView.cs
      Measurement/AndroidMeasureIntegration.cs
      Measurement/AndroidMeasureRaycaster.cs
      Measurement/AndroidSectionClipper.cs
      Measurement/AndroidMeshRaycastAcceleration.cs
      Properties/AndroidManifest.xml    (no <activity>; all wiring via [Activity] attributes)
      Resources/{layout,values,drawable,mipmap}
    FabricationAssistant.Rendering.Gles/    (Silk.NET.OpenGLES renderer, FBOs, MSAA, picking)
    FabricationAssistant.Input.Gestures.Android/ (pure-C# gesture recognizer + AndroidPointerSource)
    FabricationAssistant.Platform.Android/  (IDispatcher, IPlatformPaths)
    FabricationAssistant.Core.Android/      (shim referencing shared Core)
    FabricationAssistant.Import.Gltf.Android/ (shim for Gltf importer)
    FabricationAssistant.Draco.Android/     (Draco-decoding decorator + P/Invoke)
    FabricationAssistant.App.Android.Tests/ (smoke + unit tests; not Android-instrumented)
```

The Android side is intentionally a thin platform layer over the shared `src/FabricationAssistant.Core` and `src/FabricationAssistant.Import.Gltf` desktop projects, with new GLES, gesture, and platform-paths libraries. `MainActivity` is monolithic by design.

## 2. Main Android workflows inspected

- App startup -> DI build -> SharedPreferences hydration -> SurfaceView creation -> ApplySettingsToScene
- SAF file open -> cache copy -> BOM strip (gltf) -> signature validation -> Draco-decode -> GltfImportService -> SceneAppearance apply
- Touch input -> TouchProxy -> AndroidPointerSource -> ViewportTouchGestureRecognizer -> 3 GestureRecognized subscribers (toolbar tools, interaction adapter, selection)
- GL render: GLSurfaceView (vsync-coalesced via Choreographer) -> GlesRendererBridge -> GlesViewportRenderer -> MSAA FBO -> SSAO / outline / section / edges passes -> resolve
- Section cut, view cube, view preset, measurement, body-move, explode, render-mode toolbars: modal bottom-toolbar mode + AndroidModalTool state machine
- Configuration changes, OnPause/OnResume, OnTrimMemory, OnSaveInstanceState/restore, OnDestroy cleanup
- QR scan via ZXing.Net + legacy `android.hardware.Camera` (API 24 compat)

## 3. Confirmed bugs

### B-1 (High, Confirmed) - SAF picker does not request persistable URI permission; recent files become inaccessible after the app process is killed

- Files: `SafFilePicker.cs` (line 28-31, 54), `MainActivity.cs:7100`, `RecentFilesStore.cs:104-118`
- Problem type: File I/O / Hardening
- Description: `SafFilePicker` constructs `new ActivityResultContracts.OpenDocument()` and calls `_launcher.Launch(mimeTypes)`. `OpenDocument` returns content URIs that are granted transient read permission to the calling Activity. Immediately afterwards `RecentFilesStore.TryTakePersistableReadPermission(this, uri)` is invoked at `MainActivity.cs:7100`. Per the Android docs, `TakePersistableUriPermission` only succeeds if the originating `ACTION_OPEN_DOCUMENT` intent included `FLAG_GRANT_PERSISTABLE_URI_PERMISSION`. The default `OpenDocument` contract does NOT set that flag, so `TakePersistableUriPermission` raises `SecurityException` which is silently swallowed by the surrounding try/catch (only a `Log.Warn` is emitted). Result: recent-file URIs do not survive process death.
- Why it matters: First-time use looks fine, because `_picker` keeps the runtime permission, so opening within the same launch works. After Android kills the process (which happens routinely on memory pressure or after a long pause), every URI in `fa_recent_files` / `entries_json` returns `SecurityException` from `ProbeReadableAccess`, the entry is removed, and the user sees "Recent file is no longer accessible." for every entry, with no path to re-grant short of reopening the same file via the picker.
- Suggested fix: Either (a) subclass `ActivityResultContracts.OpenDocument` and override `CreateIntent` to add `Intent.FlagGrantPersistableUriPermission` (and optionally `FlagGrantReadUriPermission`); or (b) replace the `_launcher.Launch(...)` path with a hand-rolled `Intent(Intent.ActionOpenDocument)` carrying the flag, registered through `RegisterForActivityResult(new ActivityResultContracts.StartActivityForResult(), ...)`. Keep the silent-failure log behaviour as defence in depth.
- Localised: Yes. Two files.
- Regression risk: Low. New SAF intents pick up the flag; old recent entries are still valid for the current process lifetime.
- Verification: Open a file, force-stop the app, relaunch, tap the recent entry, file should still load.

### B-2 (Medium-High, Confirmed) - Synchronous import-cache pruning on the UI thread in `OnCreate`

- File: `AppServices.cs:25`; `ImportPipeline.cs:80-86`, `312-345`
- Problem type: Threading / ANR risk
- Description: `AppServices.Build(applicationContext)` is called from `MainActivity.OnCreate` on the UI thread. The first thing it does after configuring paths is `ImportPipeline.PruneImportCache(platformPaths.AppDataRoot);` which calls `new DirectoryInfo(...).GetFiles()`, filters, sorts by `LastWriteTimeUtc`, and synchronously deletes stale `.part`, `.nobom`, and beyond-keep-count cache files.
- Why it matters: On low-end devices with slow internal storage or many residual files (a previous failed import can leave many `.part` files), this blocks the UI thread before `SetContentView`, raising the risk of ANR and slow first paint.
- Suggested fix: Move the prune to a background `Task.Run(() => ImportPipeline.PruneImportCache(root))` started after `SetContentView`. Pruning is best-effort and already swallows exceptions.
- Localised: Yes. One call site in `AppServices.cs`.
- Regression risk: Very low; the prune does not need to complete before any import.

### B-3 (Medium, Confirmed) - Stale `_density` in `AndroidPointerSource` after density configuration changes

- File: `AndroidPointerSource.cs:22, 28-33, 320, 361`
- Problem type: Android UI Logic / Bug
- Description: `MainActivity` is declared with `ConfigurationChanges = ... | ConfigChanges.Density | ...`, meaning the activity is NOT recreated when display density changes (foldable unfold, runtime font/dpi change, multi-window resize crossing density buckets). `AndroidPointerSource` captures `_density` in its constructor and uses it for every coordinate conversion in `Sample`/`SampleAt`. After a density change the cached value is wrong, so all gesture thresholds (8 dp drag, 6 dp long-press, 30 dp double-tap) and emitted positions are off by a factor of `oldDensity / newDensity`.
- Why it matters: On foldable / DeX / split-screen workflows, orbit / pan feel will drift after a config event without an apparent cause.
- Suggested fix: Add a `RefreshDensity(Context ctx)` method on `AndroidPointerSource` that re-reads `ctx.Resources.DisplayMetrics.Density`, and call it from `MainActivity.OnConfigurationChanged`.
- Localised: Yes. One class.
- Regression risk: Low. Density rarely changes per-touch, so the simplest fix is a tiny method called from `OnConfigurationChanged`.

### B-4 (Medium, Confirmed) - `ImportFileTypeResolver` has no `.fa` MIME fallback

- File: `ImportFileTypeResolver.cs:12-18`
- Problem type: File I/O / Hardening
- Description: Switch covers `model/gltf-binary`, `model/gltf+json`, `application/gltf-buffer` only. The picker is launched with `"application/zip"` in its MIME list (see `MainActivity.cs:7082`) and `.fa` files are ZIP archives. If a provider returns a display name without an extension and reports `application/zip` (or `application/x-zip-compressed`), the resolver falls through to `.bin`, and `ImportPipeline.ImportAsync` rejects it with `NotSupportedException("Unsupported file type: .bin ...")`.
- Why it matters: Edge case where a `.fa` is selected from a content provider that strips extensions; the user sees an inscrutable error and the file looks broken.
- Suggested fix: Add cases for `"application/zip"` and `"application/x-zip-compressed"` mapping to `.fa`. Keep the existing fallbacks.
- Regression risk: Negligible; only takes effect when extension is missing.

### B-5 (Medium, Likely) - Resize listener (`HorizontalResizeTouchListener`) does not track pointer ID

- File: `HorizontalResizeTouchListener.cs:32-58`
- Problem type: Android UI Logic / Multi-touch
- Description: The listener uses `e.RawX` without filtering by pointer index / id. Side note: contrary to a sub-agent claim, the units are correct since `_startWidth` is in px and `e.RawX` is in px, so the math is dimensionally consistent. The real issue is multi-touch: if a second finger lands on the divider during a drag, then the first finger lifts, the system's `ACTION_POINTER_UP` / `ACTION_UP` flow does not re-anchor `_startRawX` / `_startWidth`, and `MotionEventActions.Move` continues with the second finger's position, producing a discontinuous jump.
- Suggested fix: Capture `e.GetPointerId(e.ActionIndex)` on `ACTION_DOWN`, then in `Move` find that pointer's index via `FindPointerIndex(id)` and read `GetX(idx) + (e.RawX - e.GetX(0))` (or `GetRawX(idx)` on API 29+). Reset `_dragging` on any `ACTION_POINTER_UP` whose lifted pointer matches the tracked id.
- Regression risk: Low; mostly defensive logic.

### B-6 (Medium, Confirmed) - `_pointerSource.GestureRecognized = null;` in `Dispose` clears the invocation list without notifying subscribers

- File: `AndroidPointerSource.cs:395`
- Problem type: Threading / Async
- Description: `Dispose()` nulls the event field directly. For C# events, this is legal inside the declaring class but does not call any external removal logic. The subscribers (`OnGestureForToolbarTools`, `_interaction.OnGesture`, `OnGestureForSelection`) are owned by `MainActivity` which already explicitly removes them in `OnDestroy` before calling `_pointerSource.Dispose()` (lines 9573-9579), so this is currently safe. It is a fragile pattern though: any future subscriber that retains `_pointerSource` will fail to detect detach via standard `-=` semantics from outside the class.
- Suggested fix: Keep the existing OnDestroy `-=` calls (already there) and document `Dispose()` as not unsubscribing automatically; or convert `GestureRecognized` to a backing field with explicit `add` / `remove` so the null-out is consistent.
- Regression risk: Very low. Current usage works.

### B-7 (Low, Confirmed) - `ViewportSurfaceView.OnDetachedFromWindow` queues renderer disposal via `QueueEvent`, but the GL thread may exit before the queued lambda runs

- File: `ViewportSurfaceView.cs:73-77, 154-175`
- Problem type: Lifecycle / Resource leak
- Description: `OnDetachedFromWindow` calls `MarkDisposed` then `DisposeRendererOnGlThread`, which `QueueEvent`s the renderer `Dispose()` onto the GL thread. The GLSurfaceView's surface destruction signals the GL thread to exit; while Android typically drains pending queue events before exit, this is not contractually guaranteed. The renderer holds VBOs / FBOs / textures whose disposal is "fire and best-effort".
- Why it matters: On rare occasions the OS may terminate the GL thread before the dispose lambda runs, leaking the EGL context-owned resources. The process exits soon after anyway so this is mostly cosmetic.
- Suggested fix: Optional. Block briefly on a `ManualResetEventSlim` (with short timeout, e.g. 250 ms) signalled at the end of the dispose lambda; if it does not signal, log and proceed. Or accept the current behaviour and add a comment explaining the reliance on GLSurfaceView draining its queue.

## 4. Android UI logic problems

### U-1 (Medium, Likely) - `OnPause` cancels the active load but does not cancel pending picks or QR scans

- `_picker` is left open if the user pressed Open and then backgrounded; the SAF picker dialog often disappears with the activity, but `_pending` TaskCompletionSource in `SafFilePicker` is only cancelled on `Dispose` or after the 5-minute internal timeout. While paused, `_pending.TrySetCanceled()` will not be invoked unless the user explicitly cancels.
- Suggested fix: in `OnPause`, call `_picker?.CancelActivePicker()` if such a method is added (or `_picker?.Dispose()` is too heavy, exposing a `CancelActivePick` that resets `_pending` is preferable).

### U-2 (Low, Confirmed) - `BindBottomToolbar` attaches click handlers regardless of scene presence

Many bottom-toolbar buttons are then disabled via `UpdateMainToolButtonStates`. This is correct, but a few buttons (`_toolScanQrButton` for QR) require `HasScene()`; the disabled-button visual treatment is `Alpha = 0.55f` (`SetEnabled`'s side effect at `MainActivity.cs:9199-9201`) but `Click` is still attached, so a programmatic click could fire. Manual taps respect `Enabled` so this is theoretical.

### U-3 (Medium, Confirmed) - `RecentFilesBottomSheet` probes every URI via `ContentResolver.OpenFileDescriptor` on the UI thread

- File: `RecentFilesStore.cs:140-168` invoked from `RecentFilesBottomSheet.CreateEmbeddedView`.
- `OpenFileDescriptor` is fast for local SAF URIs but can block on remote providers (Drive, Dropbox, network mounts).
- Suggested fix: probe asynchronously; show entries optimistically, mark as "checking..." and remove unreachable rows when probes return.

### U-4 (Low, Confirmed) - `UpdateBottomToolbarVisibility` toggles dozens of `Visibility` flags per call

Some controls (the section fill / edges switches) update `Checked` inside `UpdateSectionButtonStates` with `_sectionSwitchUpdating = true;` guard. Good. Make sure each new toolbar control follows the same gated pattern.

### U-5 (Confirmed correct) - `OnExplodeSliderProgressChanged` only writes when `e.FromUser`

`_explodeSliderUpdating` flag prevents thrash when programmatic updates fire. Verified correct.

### U-6 (Low, Confirmed) - `OpenPicker` for color swatches uses a single click handler attached to the row, swatch, hex text and pick button

Four `Click` registrations. They all open the same dialog. Slight overhead, not a correctness bug.

### U-7 (Low, Confirmed) - `AndroidManifest.xml` declares only the application + permissions; no `<intent-filter>` for `ACTION_VIEW` on `.glb` / `.fa`

This is consistent with the in-app-picker-only UX; flag if you want "Open with" support, otherwise leave as-is.

## 5. Lifecycle and threading problems

### L-1 (Confirmed correct) - `OnPause` / `OnResume`

- Cancels active load on pause, dismisses styled tooltips, clears hover, hides render-busy. `_viewport?.OnPause()` triggers the surface view's pause path which clears pending callbacks and queued GL commands.
- `OnResume` re-applies settings and dispatches `ReloadRendererSceneAfterContextLossAsync` which re-uploads the retained `DocumentDto` or falls back to URI reload (`MainActivity.cs:9336-9451`). This is excellent, covers the GLSurfaceView context-recreation path properly.

### L-2 (Confirmed correct) - `OnSaveInstanceState`

Persists URI, display name, camera, selected node IDs, selected occurrence IDs (FA packages), explode amount, section sub-mode, section planes + selected, hidden / isolated occurrence IDs. Comprehensive.

### L-3 (Confirmed correct) - `OnTrimMemory` / `OnLowMemory`

- At UiHidden / RunningLow / RunningCritical: clears raycast acceleration caches and prunes import cache.
- At RunningCritical: clears `_lastLoadedDocument` and queues `TrimTransientGpuResources()` on the GL thread.

### L-4 (Confirmed correct) - `OnDestroy`

Sets `_isDestroyed = true`, increments load version, cancels active load, unsubscribes camera / sections / bodyMove / undo events, disposes pointer source, measure, model explorer, picker, viewport renderer (on GL thread), and disposes services. Excellent cleanup.

### L-5 (Confirmed correct) - S-Pen palm rejection

`ApplySpenPalmRejectionState` calls `_pointerSource?.CancelActiveGesture()` before flipping the flag; correctly avoids leaving the recognizer half-locked.

### L-6 (Medium-Low) - `AppServices.Build` invokes `ImportPipeline.PruneImportCache` on the UI thread

See B-2.

### L-7 (Low, Likely) - `OnPause` does not call `RemoveOwnedOverlayViews` or detach the GestureRecognized subscribers

These survive across pause / resume which is correct, but if `OnDestroy` is skipped (process death without `OnDestroy`), subscribers leak via static-equivalent references. Process death also disposes the heap, so this is academic.

## 6. Rendering / GLES problems

> Most renderer findings come from sub-agent analysis. The full GLES library was not read line-by-line in this pass; the following are flagged for follow-up. I explicitly rejected the agent's "critical" `glReadPixels` finding: `GL_RED_INTEGER + GL_UNSIGNED_INT` is valid for `R32UI` textures in GLES 3.0+ (the device target). See `GlesPickRenderer.cs:199`.

### R-1 (Medium, Likely) - Overlay texture state leakage

The renderer binds the AO texture to unit 4, then returns to unit 0 without explicitly unbinding unit 4. If an overlay pass later samples without resetting, the AO sampler is implicitly bound. Mitigated by explicit `BindTexture` calls in each pass; tighten by extending `ResetMainFramebufferState()` to set `ActiveTexture(Texture0)` and unbind unit 4.

### R-2 (Low, Candidate) - MSAA failure logging is gated by `_failedMsaaWidth`; transient FBO incompleteness after the first failure is silent

Suggested: log every FBO failure once with a hash of the config, then back off retry.

### R-3 (Confirmed correct, per agent) - Section clipping uniforms are broadcast to mesh, edge, stencil, pick, and outline programs

Co-verified by reading the call sites the agent cited.

### R-4 (Confirmed correct, per agent) - Pick FBO Y-flip is correct

Android top-left to GL bottom-left conversion is applied.

### R-5 (Confirmed correct, per agent) - `ShaderProgram` logs compile / link errors with program / shader names

### R-6 (Confirmed correct, per agent) - MSAA samples are clamped twice

At the read site in `AppSettings.MsaaSamples` via `ClampAndroidMsaaSamples` (snap to 0 / 2 / 4 / 8) and again against `GL_MAX_SAMPLES` in `MsaaSceneFramebuffer`.

### R-7 (Low, Candidate) - Verify no non-ASCII byte exists in inline GLSL shader strings

Per the user's `feedback_ascii_only_shaders.md` memory, this would be a build-blocker on NVIDIA's preprocessor. Recommended check:

```
Grep -P "[^\x00-\x7F]" --type cs Android/src/FabricationAssistant.Rendering.Gles
```

Not run as part of this review.

## 7. File import and SAF problems

- F-1 (High) - Persistable URI permission. See B-1.
- F-2 (Medium) - Synchronous prune in OnCreate. See B-2.
- F-3 (Medium) - `.fa` MIME mapping. See B-4.
- F-4 (Confirmed correct) - Cancellation. `CopyToLocalAsync` uses a linked CTS with a 15-minute hard `CopyTimeout`; `CopyToAsyncWithLimit` respects the token per buffer read. On cancellation the catch block deletes `.part`. Stream disposal is via `await using`, so the file handle is closed before `TryDeleteFile`. Reliable.
- F-5 (Confirmed correct) - `ValidateGlbAsync` is strict: header magic / version / length match, first chunk must be JSON of non-zero length, chunk alignment to 4 bytes, no chunk exceeds bounds. Truncated GLBs are caught at validation, not at parse time.
- F-6 (Confirmed correct) - `ValidateGltfJsonAsync` requires `asset.version == "2.0"`.
- F-7 (Confirmed correct) - `ValidateFaArchiveAsync` validates the `PK` magic, parses the manifest JSON, and requires the geometry / components entries. Strong.
- F-8 (Low) - `StripUtf8BomInPlaceIfPresentAsync` does not handle the file-is-exactly-3-bytes-of-BOM case explicitly; the resulting empty file will then fail JSON parse. Not a crash, just a less specific error.
- F-9 (Low) - File size cap is 2 GiB on copy; the GLB importer may load the entire file into memory. For low-end devices, consider lowering the practical cap or surfacing a "this file is too large for this device" message based on `ActivityManager.MemoryInfo`.

## 8. Settings / persistence problems

- S-1 (Confirmed correct) - Schema migration. `MigrateDefaultsIfNeeded` runs on `Initialize`. Old `ao_*`, legacy float / int defaults, and `section_gizmo_size` to `section_gizmo_scale` conversions are handled. Versioned at `SettingsSchemaVersion = 14`.
- S-2 (Confirmed correct) - Range clamping. Every setter clamps; getters use `GetFloatInRange` / `GetIntInRange` with defaults on out-of-range stored values. Resilient to corrupted prefs.
- S-3 (Confirmed correct) - `AppSettings.Apply(ref SceneAppearance)` populates every field in one call. Verified field-by-field against the implementation.
- S-4 (Confirmed correct) - `Edit(editor => ...)` always calls `Apply()` (async). Migration uses `Commit()` to make the migration durable on first run.
- S-5 (Confirmed correct) - `SetEdgesEnabledFromUi(true)` defensively bumps `edge_width` up to `DefaultEdgeWidth` when the persisted value is below the visible-threshold. Good UX.
- S-6 (Low) - `LegacyFloatDefaultsToRemove` has duplicate `("ao_radius", 0.009f)` and `("ao_bias", 0.0002f)` entries (`AppSettings.cs:87-89`), harmless, but suggests copy-paste cleanup.
- S-7 (Low) - `PreferencesBottomSheet.AddFloatSlider` integer-quantises to 1000 buckets which is fine for most ranges but lossy for `ao_max_distance` (0.05..2.0, step ~0.00195). Acceptable.

## 9. Hardening recommendations

- H-1 Replace the SAF picker contract with one that includes `FLAG_GRANT_PERSISTABLE_URI_PERMISSION` (root cause of B-1).
- H-2 Move `PruneImportCache` off the UI thread (B-2).
- H-3 Refresh `_density` after `OnConfigurationChanged` (B-3).
- H-4 Add `.fa` / ZIP to MIME fallback (B-4).
- H-5 Track pointer ID in `HorizontalResizeTouchListener` (B-5).
- H-6 Verify no non-ASCII bytes in inline GLSL strings under `Rendering.Gles` (R-7), automated grep can be added to `build.ps1`.
- H-7 Add a configurable backoff / log policy to the MSAA fallback path so transient FBO incompleteness is visible without spam (R-2).
- H-8 In `RecentFilesBottomSheet`, probe access asynchronously and update the list as results return (U-3).
- H-9 Consider exposing a `SafFilePicker.CancelActivePickAsync()` to call from `OnPause` (U-1).
- H-10 When opening recent files surfaces "no longer accessible", include the original display name in the toast and provide a "Re-open" affordance that re-launches the picker.

## 10. Dead code candidates

- D-1 (Candidate) - `AppSettings.LegacyFloatDefaultsToRemove` (line 87-90, 105-106): repeated entries for `ao_radius`, `ao_bias`, `edge_width`. Removing duplicates is safe.
- D-2 (Candidate) - `RecentFilesStore.Save` (private) at lines 120-124 is a duplicate of `SaveUnsafe` plus a lock; `Save` is never called from inside the class (callers use `SaveUnsafe` under the gate). Confirm via Grep before removing.
- D-3 (Candidate) - `MainActivity._lastMeasurementOverlayLogKey` / `_lastMeasurementLabelLogKey` / `_lastProjectionLogKey` look like debug log de-duplication keys. If any never reads them, prune.

No high-confidence dead-code removals identified.

## 11. Simplification opportunities (low priority; do not pursue unless risk-reducing)

- Si-1 Consolidate the 30+ `DetachClick(...)` calls in `ClearAndroidViewListeners` and the matching `Click +=` attachments in `BindBottomToolbar` into a small registry helper that records (button, handler) on attach and replays them on destroy. Reduces drift risk between attach / detach lists.
- Si-2 `EnumerateRenderableNodeIds(scene, nodeIds)` and `EnumerateRenderableNodeIds(SceneNode)` could be merged with a single `IEnumerable<int>` variant. Minor.

## 12. Prioritised fix plan

1. B-1 SAF persistable URI permission (High; recent files unusable after process restart).
2. B-2 Move PruneImportCache off the UI thread (Medium-High; first-paint regression risk).
3. B-3 Refresh `_density` on `OnConfigurationChanged` (Medium; foldable / DeX correctness).
4. B-4 `.fa` MIME fallback (Medium; user-visible "Unsupported file type" on edge case).
5. R-7 Static check for non-ASCII bytes in inline GLSL strings (Medium; latent compile bomb on NVIDIA-derived drivers per saved memory).
6. B-5 `HorizontalResizeTouchListener` pointer tracking (Medium; multi-touch drift).
7. U-3 Async URI probing in recent files (Medium; ANR risk on remote providers).
8. R-1 Tighten texture-unit reset in ResetMainFramebufferState (Medium-Low; overlay correctness).
9. R-2 Log every FBO failure once + back off (Low; observability).
10. U-1 `OnPause` cancels the SAF picker (Low; cleanliness).
11. F-8 / F-9 Edge-case import errors (Low; polish).
12. D-1 / D-2 Dead-code cleanups (Low; only if you accept the risk of breaking some unseen caller).

## 13. Safe first-patch list

Smallest patches that buy the most safety with no architectural churn. Each is local, roughly 25 lines or less, no public API changes:

- `SafFilePicker.cs`: wrap the contract or the launched Intent with `Intent.FlagGrantPersistableUriPermission`.
- `AppServices.cs`: `Task.Run(() => ImportPipeline.PruneImportCache(...))` instead of synchronous.
- `AndroidPointerSource.cs`: new `RefreshDensity(Context)` method; called from `MainActivity.OnConfigurationChanged`.
- `ImportFileTypeResolver.cs`: extend switch with two ZIP MIME cases mapping to `.fa`.
- `HorizontalResizeTouchListener.cs`: track pointer ID; ignore non-tracked pointers in Move.

## 14. Verification plan

### Build

- `pwsh Android/tools/build.ps1 -Configuration Debug` from a console that does not have `obj/` open in any IDE / explorer.
- Note: build attempted during this review failed with `XARLP7024: System.IO.IOException: The process cannot access the file '...\obj\Debug\lp\83\jl\R.txt'`. This is a Windows file-lock race against parallel reads of `obj/`; the underlying C# compilation reported 5 warnings, 0 errors before that. Re-run the build from a clean shell (or `-Clean`) to confirm 0 errors. Build verification did not complete cleanly through me; please re-run standalone before merging any patch.

### Unit tests

- `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` covers `ViewportTouchGestureRecognizerTests`, `AndroidPointerSourceTests`, `AndroidSectionClipperTests`, `ImportFileTypeResolverTests`, `SmokeTests`. Extend `ImportFileTypeResolverTests` after B-4 with two new ZIP / no-extension cases.

### Emulator / device smoke

- Cold-start with no scene -> Open flow -> recent file present -> kill app -> reopen -> tap recent -> must still load (B-1 fix).
- Open a `.fa` from a provider that strips extensions (for example via `adb shell content insert` with explicit zip MIME) -> must import (B-4).
- Rotate / multi-window resize on a foldable; verify drag thresholds remain consistent (B-3, B-5).
- Open a large file (~1.5 GiB) on a 4 GB device; confirm OOM is caught and an error shown (existing behaviour; sanity).
- Pause-resume during long import -> import completes or aborts cleanly without crash; `OnTrimMemory(RunningCritical)` injected via `adb shell am send-trim-memory <pid> RUNNING_CRITICAL` -> renderer still recovers.
- QR scan: grant camera, deny, deny+"don't ask again" paths each navigate to the right dialog and recover (verified by code reading).

### Rendering checks

- Toggle every render mode (Shaded, Wireframe, ShadedWithEdges, Clay) and each section axis with sectionFill / sectionEdges on / off. Confirm overlays do not show through clipped regions. Cycle several times to detect overlay state-leak (R-1).
- Manually test on a low-end device (for example Pixel 4a) to expose ANR / density issues.

## 15. Optional deeper refactors (not recommended now)

- Splitting `MainActivity` into partials by feature area (recent panel, body-move, sections, measurement, explode, render-mode), would lower cognitive load, but the user has explicitly ruled out reorganisation. Skip.
- Migrating the QR scanner from `android.hardware.Camera` to CameraX. Real benefit on API 28+, but adds a transitive dependency and replaces working code. Skip unless camera quality complaints surface.

---

## Summary

The Android port is in good shape. Lifecycle handling is thorough, `OnDestroy` is meticulous about subscription cleanup, GLES context loss is recovered via re-upload of retained `DocumentDto`, settings persistence has proper schema migration and range clamping. The one user-facing high-severity finding is the missing `FLAG_GRANT_PERSISTABLE_URI_PERMISSION` (B-1) which silently fails the recent-files flow after process death. Four medium-severity findings (UI-thread cache prune, stale density, ZIP MIME fallback, resize listener pointer tracking) are all small localised patches. Renderer-side findings need a second pass once non-ASCII GLSL scan and texture-state audit are run; nothing critical is confirmed there. Build verification did not complete cleanly under this review session; please re-run `Android/tools/build.ps1 -Clean -Configuration Debug` independently.
