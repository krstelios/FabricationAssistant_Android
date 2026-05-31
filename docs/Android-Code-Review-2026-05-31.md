# Fabrication Assistant - Android Code Review (2026-05-31)

Fresh, full-app systematic review. Supersedes `Android-Code-Review-2026-05-29.md`.
Method: ten focused reviewers each did a deep read-and-trace pass over an assigned
slice group (call paths followed into `MainActivity`, shaders, and shared core where
relevant); the highest-impact new findings were verified against source. Read-only -
no files were modified during the review.

**Headline:** No Critical defects. The renderer, GL context-loss recovery, EGL/MSAA
pipeline, pick path, cancellation, and cache atomicity are unusually careful. The
actionable issues cluster in:

1. an unbounded memory/listener leak in the scene-tree / BOM list adapters,
2. lifecycle continuations on a destroyed Activity for in-flight saves,
3. silent data loss in the Draco transcoder for skinned/colored meshes,
4. several Settings UI controls that desync their displayed state from what they persisted,
5. the still-open release cleartext transport gap, and
6. test coverage that does not actually execute the Android build or the shaders.

Severity counts: High 6, Medium 14, Low 28, Dead-code 6 (plus candidates flagged inline).

Finding IDs encode their slice: `S<slice>-<n>`. Each finding carries
Type / Severity / Confidence and a fix + verify note.

---

## 1. Android codebase map

**Solution:** `FabricationAssistant.Android.sln` (separate from the desktop root sln).
Build only via `tools\build.ps1` (auto-sets ANDROID_HOME/JAVA_HOME, builds
`libdraco_native.so` per ABI, then `dotnet build`). TFM `net8.0-android34.0`;
minSdk 24, targetSdk 34.

| Project | Role |
|---|---|
| `FabricationAssistant.App.Android` | Main app: `MainActivity` (~15.8k lines, central hub), panels, settings, import, cloud, measurement |
| `FabricationAssistant.Rendering.Gles` | Silk.NET OpenGL ES 3.1 renderer, 22 GLSL ES 3.10 shaders, FBOs, `SceneAppearance` |
| `FabricationAssistant.Input.Gestures.Android` | `AndroidPointerSource`, `ViewportTouchGestureRecognizer`, `ViewportInteractionAdapter` |
| `FabricationAssistant.Draco.Android` | P/Invoke Draco decode decorator + native `libdraco_native.so` (arm64-v8a, x86_64) |
| `FabricationAssistant.Core.Android` | Shim compile-linking desktop `../src` Core (+ platform exclusions targets) |
| `FabricationAssistant.Import.Gltf.Android` | Shim for glTF/FA import core |
| `FabricationAssistant.Platform.Android` | `AndroidDispatcher`, paths |
| `FabricationAssistant.App.Android.Tests` | `net8.0` host tests (no emulator); links select source by path |

**Entry/lifecycle:** `FabricationAssistantApplication` -> `MainActivity.OnCreate`
(`AppSettings.Initialize` first -> `AppServices.Build` DI -> `ViewportSurfaceView`/renderer).
**Renderer bridge:** `ViewportSurfaceView` (GLSurfaceView, WhenDirty) -> `GlesRendererBridge`
-> `GlesViewportRenderer`. **Native:** Draco `.so` per ABI under `deps/prebuilt/`.

## 2. Review slice plan and status

All 25 slices reviewed. Markup/review tools (Slice 21): N/A - no markup feature exists
("annotation" = 3D measurement labels only).

| # | Slice | Status | Findings |
|---|---|---|---|
| 1 | Startup / DI / init | Reviewed | S1-4 |
| 2 | Android lifecycle | Reviewed | S1-1 (High), S1-2, S1-3 |
| 3 | File open / import / SAF | Reviewed | S3/4-M1, S3-M2, S3-M4, S3-L5 |
| 4 | GLB / glTF / FA import | Reviewed | S4-L2 |
| 5 | Draco decode path | Reviewed | S5-1 (High), S5-2, S5-3, S5-M5, S5-L1, S5-L4 |
| 6 | Renderer init / GLES context | Reviewed | S6-2, S6-F4, S6-F5, S6-F6, S6-F9 |
| 7 | GLES resources / shaders / passes | Reviewed | S7-1, S7-F3, S7-F6, S7-F12 |
| 8 | MSAA / framebuffer pipeline | Reviewed | S6-1, S6-F3 |
| 9 | SceneAppearance / AppSettings | Reviewed | S9-1, S9-F6, S9-F10 |
| 10 | PreferencesBottomSheet UI | Reviewed | S10-1, S10-2, S10-F2, S10-F4, S10-F7, S10-F8, S10-F9 |
| 11 | Toolbar / command state | Reviewed | S11-1 |
| 12 | Touch gestures / camera | Reviewed | S12-F2, S12-F3, S12-F4 |
| 13 | Selection / picking | Reviewed | S13-1, S15-1, S13-F7 |
| 14 | Scene tree / model explorer | Reviewed | S14-1 (High), S14-2 (High), S14-5, S14-F6, S14-F8, S14-F9 |
| 15 | Properties panel | Reviewed | S15-F10 |
| 16 | Section cut system | Reviewed | S16-2, S16-5, S16-7 |
| 17 | Measurement tools | Reviewed | S17-1, S17-4, S17-6 |
| 18 | View cube / axis gizmo | Reviewed | none (positives only) |
| 19 | Hover / outline / clay outline | Reviewed | S7/19-F4, S19-F7 |
| 20 | Rendering modes / visual parity | Reviewed | S20-F10 |
| 21 | Markup / review tools | N/A | not present |
| 22 | Cloud / login / API | Reviewed | S22-1 (High), S22-2, S22-2b, S22-3, S22-4, S22-12, S22-17, S22-18 |
| 23 | Local storage / cache / logs | Reviewed | S23-1, S23-8, S23-10, S23-11, S23-14, S23-15, S23-16 |
| 24 | Manifest / resources / packaging | Reviewed | S24-H2, S24-M3, S24-2, S24-L1, S24-L3, S24-L5 |
| 25 | Tests / build / verification | Reviewed | S25-1 (High), S25-2, S25-3, S25-L4 |

## 3. Findings by slice

Full detail (problem / why / fix / verify) for High and Medium; compact one-liners for Low.
Each slice closes with a short "Verified correct" note of what was checked and found sound.

---

### Slice 1 - Startup, DI, init

**S1-4** - `AppServices.cs:36` - Threading - Low - Confirmed.
Startup import-cache prune is a fire-and-forget `Task.Run`, untracked by any CTS. Benign
(touches only the filesystem, no Activity refs), but two rapid create/destroy cycles can
overlap prunes. Fix: acceptable as-is; optionally guard with a static gate. Verify: rapid
launch/destroy still only logs faults.

Verified correct: `AppSettings.Initialize(ApplicationContext)` is the first meaningful
statement in `OnCreate` (before `AppServices.Build` and the surface view); DI captures only
the application context / main looper - no Activity/Context leak through singletons; the
container is rebuilt per Activity and disposed in `OnDestroy`.

### Slice 2 - Android lifecycle

**S1-1** - `MainActivity.cs` `_saveCts` (`:360`), `SaveCurrentModelAsync` (`:2511`), `OnPause` (`:14666`), `OnDestroy` (`:15105`) - Lifecycle/Threading - **High** - Confirmed.
- Problem: `OnPause`/`OnDestroy` cancel `_loadCts`/`_cloudOpenCts`/`_activityDestroyCts` but never `_saveCts`. The save awaits `ConfigureAwait(true)`, so its `finally` (`Toast.MakeText(this,...)`, `HideLoading`, `UpdateSaveButton`) resumes on the UI thread against a destroyed Activity; cloud save also outlives teardown.
- Why it matters: `WindowManager$BadTokenException` / no-op on detached views when the user saves then locks/rotates-out or finishes the Activity.
- Fix: In `OnDestroy` (and `OnPause` for cloud saves) `Interlocked.Exchange(ref _saveCts, null)?.Cancel();` and/or link the save CTS to `_activityDestroyCts`; guard the `finally` UI calls with `if (!_isDestroyed)`.
- Verify: start a slow/cloud save, rotate-to-recreate or finish - no BadTokenException.

**S1-2** - `MainActivity.cs:1143/1171` - Lifecycle - Low - Likely.
Cloud `RunOnUiThread` callback bodies (`OnCloudSessionChanged`, `OnCloudNotificationReceived`)
lack an `_isDestroyed` guard; a callback marshaled just before handler-detach can touch
torn-down views. Fix: add `if (_isDestroyed) return;` as the first line of each posted lambda.
Verify: trigger a session-revoked notification while finishing the Activity.

**S1-3** - `MainActivity.cs:14691` - Cloud/Lifecycle - Low - Likely.
`OnStop` releases the cloud reader lock fire-and-forget; a backgrounded-then-killed session
relies on the server heartbeat timeout to reclaim the lock. Fix: document the reliance, or
release synchronously-with-timeout. Verify: open cloud model, background, force-stop; observe
server reclaim timing.

Verified correct: GL context-loss recovery (drop stale `GpuScene` + re-upload), EGL
`PreserveEGLContextOnPause=true`, symmetric event attach/detach, render-queue gated after
dispose, rotation handled via `configChanges`, camera/selection/section/explode restored on
process-death recreation.

### Slice 3 - File open / import / SAF

**S3/4-M1** - `ImportPipeline.cs:55,244`; default param `MainActivity.cs:11638` - Hardening/Dead - Medium - Confirmed (bypass) / Likely (unreachable).
- Problem: `copyToImportCache:false` + `file://` imports directly, skipping `CopyToLocalAsync` where `MaxImportBytes` (2 GiB) and `CopyTimeout` are enforced. No caller passes `false`, so the branch is also currently dead.
- Fix: remove the `copyToImportCache` param + `TryResolveExistingFilePath`, OR add a `FileInfo.Length > MaxImportBytes` check on the direct path.
- Verify: grep confirms no `copyToImportCache:false` caller.

**S3-M2** - `ImportPipeline.cs:62,191` - File I/O - Medium - Likely.
The extension sniff `File.Move` and `StripUtf8BomInPlaceIfPresentAsync` mutate the source in
place; on the (dead) direct `file://` path this renames/rewrites the user's original file and
throws on a read-only location. Fix: only sniff-rename/BOM-strip the owned cache copy
(`copyToImportCache` true). Verify: enable the direct path with a read-only `.gltf` carrying a BOM.

**S3-M4** - `SafFilePicker.cs:102` - UI/Hardening - Medium - Likely.
The open-document intent requests `GrantWriteUriPermission | GrantPersistableUriPermission`;
read-only providers can't grant write, degrading the writable-save probe. Fix: request only
read+persistable for open; keep write on create/save. Verify: open a read-only Drive file,
check import + save-button state.

**S3-L5** - `ImportPipeline.cs:227,268` - Simplification - Low - Confirmed.
`ResolveFileName` and `TryResolveContentSize` issue two separate `ContentResolver.Query`
binder round-trips. Fix: single query selecting DisplayName + Size. Verify: time the prelude
on a cloud URI.

### Slice 4 - GLB / glTF / FA import

**S4-L2** - `ImportFileSignatureValidator.cs:52` vs `DracoGltfTranscoder.cs:298` - Hardening - Low - Confirmed.
The app validator requires JSON as chunk 0; `ReadGlb` tolerates any chunk order. Divergent
parsers; the looser one is currently gated by the validator. Fix: add the JSON-first check to
`ReadGlb`, or document the dependence. Verify: N/A under current flow.

Verified correct: `.fa` validated as a real archive (PK sig + manifest + required entries)
before import; empty-file/signature gates on `.glb`/`.gltf`; extension sniffing recovers
mislabeled `application/octet-stream`; no partial-scene corruption on failure (atomic GPU
scene swap; old scene/selection/tree retained).

### Slice 5 - Draco decode path

**S5-1** - `DracoGltfTranscoder.cs:96-126`, `DracoNativeDecoder.cs:85-119`, `draco_native.cpp:60-99` - Bug/Rendering - **High** - Confirmed.
- Problem: native wrapper maps only attribute types 0/1/2; transcoder rebuilds `prim["attributes"]` with only POS/NORMAL/TEXCOORD_0. TANGENT, COLOR_0, TEXCOORD_1+, JOINTS_0, WEIGHTS_0 are dropped silently.
- Why it matters: skinned/vertex-colored/normal-mapped Draco models load but render wrong - silent data loss, no error.
- Fix (Android-side): at minimum detect unsupported attrs and log `FA.Draco` warn / throw `InvalidDataException`; full fix adds generic attribute copy in the native wrapper.
- Verify: import a skinned or vertex-colored Draco GLB.

**S5-2** - `DracoGltfTranscoder.cs:83-94` - Bug/Hardening - Medium - Likely.
- Problem: reads the Draco bufferView's offset/length from the GLB BIN chunk without checking `view["buffer"]`; a glTF referencing another/external buffer feeds wrong bytes to the decoder.
- Fix: read the `buffer` index, throw `InvalidDataException` if `!= 0` or the buffer has a `uri`.
- Verify: craft a Draco GLB whose draco bufferView buffer != 0.

**S5-3** - `DracoExtensionDetector.cs:28`, `DracoDecodingGltfImportService.cs:34` - Bug - Medium - Confirmed.
- Problem: `ContainsDraco` returns false unless the first 4 bytes are GLB magic, so a text `.gltf` declaring `KHR_draco_mesh_compression` bypasses transcoding and fails opaquely.
- Fix: handle a leading `{` (JSON) too, or throw a clear `NotSupportedException` for `.gltf` Draco.
- Verify: import a text `.gltf` with `extensionsRequired:["KHR_draco_mesh_compression"]`.

**S5-M5** - `DracoNativeDecoder.cs:121` - Threading - Low - Likely.
`DracoMesh` finalizer would call native `DestroyMesh` off the decode thread (latent; all call
sites use `using`). Fix: keep `Dispose` as the sole owner; the `NativeGate` already linearizes.
Verify: confirm all decode results are inside `using` (they are).

**S5-L1** - `DracoNativeDecoder.cs:91,107` - Bug/Hardening - Low - Likely.
`int total = NumPoints * components` can overflow int32 for huge meshes -> negative -> returns
null -> misleading "missing required attributes". Fix: use `long`, throw a clear size error.
Verify: unit test with `NumPoints` near `int.MaxValue/3`.

**S5-L4** - `DracoDecodingGltfImportService.cs:79` - File I/O - Low - Likely.
The decoded-temp pruner enumerates only `*.glb`; the transcoder's `<dest>.<guid>.bin`
intermediates orphan on a hard kill. Fix: prune `*.bin` (or `*`) by age. Verify: kill the app
mid-Draco-import; check `temp/draco-decode`.

Verified correct: ABI packaging (arm64-v8a + x86_64) consistent and guarded; P/Invoke
signatures match the native `extern "C"`; native buffers bounds-checked and serialized under
`NativeGate`; `DllNotFound`/`BadImageFormat` converted to a user-facing `NotSupportedException`.

### Slice 6 - Renderer init / GLES context

**S6-2** - `GpuMesh.cs:19,292`; consumer `GlesViewportRenderer.cs:364` - Rendering - Low (was Medium) - Likely.
Process-static edge-quad VBO/IBO are never deleted on teardown (small permanent buffer per
context). The correctness piece (`ResetStaticEdgeQuad` on context recreate) is present and
load-bearing. Fix: keep "forget, don't delete"; deletion is optional (context teardown frees it).
Verify: pause/resume repeatedly; edges still render.

**S6-F4** - `MultisampleConfigChooser.cs:40` - Rendering - Low - Confirmed.
`ChooseConfig` can return null on a constrained driver -> opaque framework
`IllegalArgumentException` with no app log. Fix: log the requested attributes (or relax the
stencil minimum) before returning null. Verify: set depth/stencil minimums absurdly high.

**S6-F5** - `GpuScene.cs:196` - Scene Sync/perf - Low - Confirmed.
`SyncNodeVisibility` rebuilds a `HashSet` and full-scans every call with no version gate
(unlike the transform sync above it). Fix: add a visibility version to short-circuit. Verify:
call twice with no change; second call should no-op.

**S6-F6** - `GlesViewportRenderer.cs:463` - Rendering - Low - Likely.
`OnSurfaceChanged` silently skips the resize if `_gl is null` (unexpected callback ordering)
with no log -> stale viewport/FBO size. Fix: warn on that path. Verify: logcat under forced
surface-change-before-create.

**S6-F9** - `GlesViewportRenderer.cs:342` - Dead/Simplification - Candidate.
`ShowGrid`/`ClearColor`/`HighlightSelection` back-compat aliases route through (and snapshot)
`Appearance`, bypassing the `SceneAppearance` + `AppSettings.Apply` flow. Fix: grep the repo;
remove if unreferenced. Verify: repo-wide search for the three member names.

Verified correct: EGL context-preserve before `SetEGLConfigChooser`; FBO completeness checked
via `glCheckFramebufferStatus` AND `glGetError`; `GL_MAX_SAMPLES` queried once and clamped;
shader compile/link errors throw with the info log; `GlThreadGuard` enforces GL-thread affinity;
RenderMode.WhenDirty followed by `RequestRender` at every mutation.

### Slice 7 - GLES resources / shaders / render passes

**S7-1** - `GlesViewportRenderer.cs:734-746`, `mesh.gles.frag:108-114` - Rendering - Medium - Likely.
- Problem: the AO bilateral upsample `textureGather`s the half-res AO texture and the full-res normal-depth texture at the same UV; they cover different world footprints, so the per-texel depth-reject weights don't correspond to the AO texels.
- Why it matters: weakened/biased AO halo near silhouettes (visual, not a crash).
- Fix: store/sample a half-res depth companion matching the AO grid.
- Verify: AO on a model with thin features against a flat face.

**S7-F3** - `outline.gles.frag:24` - Rendering/Hardening - Low - Likely.
Local `vec2 step` shadows the GLSL built-in `step()` (legal but fragile on strict compilers).
Fix: rename `stepUv`. Verify: recompile shader.

**S7-F6** - `CadEdgeBuilder.cs:20`, `GpuMesh.cs:230` - Dead/Simplification - Low - Confirmed.
Edge vertices store 10 floats (pos + normalA + normalB + flags) but `UploadEdges` copies only
the 3 position floats; the silhouette test moved to a screen-space pass. Fix: drop the dead
per-vertex normal/flag payload (keep the topology classification that gates emission). Verify:
edges render identically; edge tests pass.

**S7-F12** - `GlesFaceHighlightOverlay.cs:68` - Rendering - Low - Likely.
The overlay sets `BlendFunc` then disables blend without resetting the func (latent; every
blend-enabling pass sets its own func). Fix: optionally reset `BlendFunc` in
`ResetMainFramebufferState`. Verify: no current visual defect.

Verified correct: ES 3.10 precision (highp int/uint for the R32UI pick path); no geometry
shaders / no desktop-GLSL leaks; integer texture formats correct; 16-bit packed-depth
round-trip consistent across mesh/ssao/blur/silhouette consumers; per-pass depth/blend/cull/
stencil/polygon-offset save+restore; no GL resource leaks.

### Slice 8 - MSAA / framebuffer pipeline

**S6-1** - `GlesViewportRenderer.cs:2520` (resets `:2530/2543/2562`) - Rendering - Medium - Likely.
- Problem: the MSAA allocation-failure guard keys on `_failedMsaaSamples`, whose success/reset sentinel is `0` - the same value as a legitimate "MSAA Off = 0" request. A stale failure record can suppress a valid FBO (re)allocation, dropping the D32FS8 path.
- Fix: use a `-1` sentinel (or a dedicated `bool _msaaAllocationFailed`).
- Verify: force `Ensure` to throw once, then request MSAA Off at the same size; confirm a single-sample FBO is still allocated.

**S6-F3** - `GlesViewportRenderer.cs:610` - Rendering - Low - Confirmed.
When `TryResolveMsaaFramebuffer` fails the loop re-renders the entire scene to FBO 0 (duplicate
scene draw that frame). Fix: acceptable one-frame fallback; document it. Verify: force resolve
to fail; frame still shows the scene.

Verified correct: MSAA renders into an offscreen `MsaaSceneFramebuffer` (so it applies live,
not at EGL surface creation); the EGL chooser is fixed at 0 samples; sample count clamped to
`GL_MAX_SAMPLES`; the offscreen FBO is D32FS8 so section caps have a stencil in both MSAA on/off.

### Slice 9 - SceneAppearance / AppSettings

**S9-1** - `AppSettings.cs:469` - Hardening/Bug - Medium - Confirmed.
- Problem: the `CloudServerUrl` setter ignores its `value` and just removes the key; the getter returns `CloudServerConfig.ServerUrl` (INI). Any assignment silently vanishes.
- Fix: make `CloudServerUrl` get-only; expose `ClearLegacyCloudServerUrl()` if a clear is needed.
- Verify: build after removing the setter (no caller relies on assignment).

**S9-F6** - `AppSettings.cs:129` - Settings/Persistence - Low - Confirmed.
The legacy default-removal list strips `surface_r`/`surface_g` but not `surface_b` ->
asymmetric, partially-migrated surface tint after upgrade. Fix: add the matching `surface_b`
entry. Verify: persist old surface RGB, run migration, confirm all three channels reset together.

**S9-F10** - tests - Coverage gap - Low.
`AppSettings.Apply(ref SceneAppearance)`, the migration helpers, and the `AndroidRenderModeShim`
round-trip have no direct unit test (which is how S9-F6 slipped through). Fix: add pure-helper
tests for `EffectiveRenderMode`, migration symmetry, MSAA range. Verify: run the suite.

Verified correct: MSAA live-vs-next-open behavior accurate; NaN/Infinity hardening across
`Put(float)`/clamps; Realistic(4)->Shaded shim round-trip; theme-attr colors; every render
control fires `OnSettingsChanged`.

### Slice 10 - PreferencesBottomSheet UI

**S10-1** - `PreferencesBottomSheet.cs:145-146`; `AppSettings.cs:270` - Android UI Logic - Medium - Confirmed.
- Problem: dragging "Grid spacing (mm)" force-writes `auto_grid_spacing=false`, but the "Automatic grid spacing" switch above keeps showing checked until the sheet reopens.
- Fix: clear the captured `MaterialSwitch.Checked` in the slider's save callback.
- Verify: with Auto on, drag spacing; the switch should clear.

**S10-2** - `PreferencesBottomSheet.cs:155-156` (`AddFloatField` `:827`) - Android UI Logic - Medium - Confirmed.
- Problem: the Near/Far clip fields commit on every keystroke (no enter-to-confirm); `SetCameraNearClipMm`/`FarClipMm` rewrite both planes and apply the `far<=near` correction per keystroke, so partially-typed values shuffle the planes and render each digit.
- Fix: wire the existing `DialogKeyboard.ConfirmOnEnter` / focus-loss commit (as the color picker does).
- Verify: enable Manual clip planes; type a multi-digit near value.

**S10-F2** - `PreferencesBottomSheet.cs:146` - UI - Low - Confirmed.
Grid-spacing slider max is 1000 but the setting clamp is 1,000,000; a persisted value > 1000
pins the thumb and the first drag silently truncates it. Fix: align the slider range with the
clamp (likely log mapping). Verify: persist `grid_spacing_mm=5000`, open Settings.

**S10-F4** - `PreferencesBottomSheet.cs:174` - UI - Low - Confirmed.
Re-enabling edges can bump `edge_width` to 1.0, but the edge-width slider keeps its stale value.
Fix: re-read `AppSettings.EdgeWidth` and update the slider after the toggle. Verify: set width
0.3, disable then re-enable edges.

**S10-F7** - `PreferencesBottomSheet.cs:174` - UI - Low - Confirmed.
Edge-width slider min is 0.05, below the enforced visible floor 0.75 (and migration deletes
sub-0.75) -> the bottom ~19% of the range is non-durable. Fix: raise the slider min to 0.75.
Verify: set 0.3, bump schema version, reopen.

**S10-F8** - `PreferencesBottomSheet.cs:177,225,179` - UI - Low - Likely.
Weld-tolerance / AO-bias / depth-bias sliders map tiny ranges (e.g. 1e-6..1e-4) across 1000
linear steps -> effectively un-tunable. Fix: numeric field (with `ConfirmOnEnter`) or log
mapping. Verify: try to set weld tolerance to exactly 2e-5 by dragging.

**S10-F9** - `PreferencesBottomSheet.cs:322` - UI - Low - Confirmed.
"Section gizmo scale" sits under the Navigation card instead of Section Tools (writes correctly).
Fix: move the slider to the `sections` section. Verify: visual.

Verified correct: opening the sheet before a scene/renderer exists is safe (reads only
AppSettings; `NotifySettingsChanged` guards on `Activity.IsDestroyed`); all colors via theme
attrs; content descriptions/tooltips throughout; cloud text fields intentionally skip the
render notify.

### Slice 11 - Toolbar, menus, command state

**S11-1** - `MainActivity.cs:6242` (`OnGestureForToolbarTools` OrbitEnd); recognizer `:84-107` - Android UI Logic - Medium - Confirmed.
- Problem: during a Zoom Window marquee a second finger makes the recognizer emit `OrbitEnd`+`PanZoomBegin`; the handler treats any `OrbitEnd` as "marquee finished" and unconditionally commits + exits the tool.
- Why it matters: an accidental second contact (common on tablets/palm) zooms to a half-finished rect or kicks the user out of the tool.
- Fix: treat an immediate Orbit->PanZoom transition as cancel-marquee/keep-tool rather than commit.
- Verify: start a one-finger marquee, drop a second finger.

Verified correct: command-enabled state is centrally refreshed and gates Move/Zoom-Window/
Zoom-Selected/Fit/View/Section/Measure/Explode/Hide/Isolate/ShowAll/RenderMode on
scene/selection/visibility; empty-selection commands early-return; nav-rail buttons de-dupe
activations and respect Enabled; import-busy disables nav buttons.

### Slice 12 - Touch gestures and camera

**S12-F2** - `ViewportInteractionAdapter.cs:55,444` - Scene Sync - Low - Likely.
`_lastResolvedPivot` is never reset across model loads; first orbit/pinch over empty space after
a model swap uses the previous model's pivot. Fix: add `ResetNavigationPivot()` and call it on
scene load. Verify: load A, orbit a body, load B, orbit empty space.

**S12-F3** - `HorizontalResizeTouchListener.cs:49` - Android UI Logic - Low - Likely.
A transient stylus PointerDown hijacks `_activePointerId` during a finger divider-drag; if the
stylus lifts first, the drag ends while the finger is still down. Fix: don't adopt a transient
second pointer / restore to a remaining valid pointer. Verify: drag with a finger, tap+lift the
S Pen.

**S12-F4** - `ViewportInteractionAdapter.cs:163` - Rendering - Candidate.
Orbit/PanZoom Begin calls `_camera.NormalizeZoomAroundPivot` without a `RequestRender`. If that
is visually neutral (it should be), this is a non-issue. Fix (only if non-neutral): add
`_requestRender()` to the Begin cases. Verify: log camera before/after `NormalizeZoomAroundPivot`
at gesture start.

Verified correct: pointer index-vs-ID handling; ACTION_CANCEL resets all gesture/long-press/
palm state and the recognizer; two-finger transitions; world-locked pinch pivot; double-tap vs
section-placement separation; long-press one-shot tick under WhenDirty; density scaling
refreshed on config change; RequestRender on every Delta/End/Wheel/Fit path.

### Slice 13 - Selection and picking

**S13-1** - `MainActivity.cs:4172` -> `GpuScene.cs:77` (linear scans `:39-75`) - Threading (UI perf) - Medium - Confirmed.
- Problem: selecting a large subtree resolves each renderable node to a mesh index via a full linear `_meshes` scan -> O(selected x meshes) on the UI thread.
- Fix: build `Dictionary<sourceNodeId, meshIndex>` (+ reverse maps) once per `GpuScene.Load`; preserve first-match semantics.
- Verify: profile selecting a large group before/after.

**S15-1** - `ViewportSurfaceView.cs:259`, `MainActivity.cs:11073` (scene swap `:11858`) - Threading/Scene Sync - Medium - Likely.
- Problem: the GL-thread pick mesh index is posted to the looper; if a new document swapped `renderer.Scene` meanwhile, `OnPickResult` maps the stale index through the new scene and selects the wrong node.
- Fix: capture `_loadVersion`/scene ref at pick issue; ignore the callback if it changed.
- Verify: tap repeatedly while opening a different model.

**S13-F7** - `GlesViewportRenderer.cs:756` - Rendering/UI - Low - Likely.
The in-shader fill tint uses a single `uSelectedMeshIndex`, so a multi-mesh group selection
highlights only the first mesh (the outline covers all). Fix: pass the selection set to the
mesh shader, or document that the fill tint is single-mesh by design. Verify: select a multi-body
group.

Verified correct: pick coord mapping + Y-flip (`glY = height-1-y`) + R32UI encode/decode + scissor;
stale pick buffers handled on resize; selection cleared on new file; x-ray ghosts excluded from pick.

### Slice 14 - Scene tree / model explorer

**S14-1** - `AndroidModelExplorerPanel.cs:478` (leak `:567`); `AndroidBomPanel.cs:1115` (`:1136`); `StyledTooltipRegistry.cs:11,34` - Lifecycle/UI (resource leak) - **High** - Confirmed (verified).
- Problem: both adapters ignore `convertView`, inflate a fresh row every call, and `AttachTree` adds ~5 new `StyledTooltipController` entries (each wiring touch/long-click listeners) to the panel-lifetime dictionary. Rows are never `DisposeTree`'d, so scrolling a large assembly grows memory and listener count without bound. (The Properties panel avoids this by calling `DisposeTree` before each rebuild.)
- Fix: reuse `convertView` with a holder; OR minimally call `_tooltips.DisposeTree(convertView)` at the top of `GetView` when `convertView != null` before re-attaching. Apply to both adapters.
- Verify: fling the tree/BOM repeatedly on a large model; `_tooltips.Count` / controller heap count stays bounded.

**S14-2** - `AndroidModelExplorerPanel.cs:163` (`SetScene`), `:865`; caller `MainActivity.cs:12451` - Scene Sync - **High** - Likely.
- Problem: `SetScene` always feeds the previous tree's expansion map (keyed by int node IDs) into the new scene; on opening a different file, overlapping node IDs apply the old model's expand/collapse state to unrelated nodes (default `ExpandInitialNodes` only runs when the map is empty).
- Fix: track the Scene reference/load token; pass `expansionState: null` when the scene instance differs (Pack/Unpack keep the same scene, so they're unaffected).
- Verify: open A, expand branches, open B -> B uses default expansion.

**S14-5** - `AndroidModelExplorerPanel.cs:163,370`; `PackDuplicateChildren` `:943` - Threading (UI block) - Medium - Likely.
- Problem: tree build + visible-row flatten run synchronously on the UI thread on scene attach, every expand/collapse, and Pack/Unpack; `PackDuplicateChildren` is O(siblings^2) per parent (inner `Where` per group).
- Fix: at minimum group duplicates in one pass into a dictionary; ideally build off the UI thread and post the finished tree.
- Verify: open a very large assembly and Pack/Unpack; measure main-thread time.

**S14-F6** - `AndroidModelExplorerPanel.cs:478`, `AndroidBomPanel.cs:1115` - UI perf - Low (root cause of S14-1) - Confirmed.
Both adapters ignore `convertView` -> no ListView recycling -> allocation + listener re-wiring
per row. Fix: holder reuse with per-row rebind. Verify: scroll; per-frame allocations drop.

**S14-F8** - `AndroidModelExplorerPanel.cs:215` - UI/Scene Sync - Low - Likely.
`_selectedPresentedId = highlighted.OrderBy(id).First()` prefers the most-negative ID, i.e. a
virtual (packed) group, over real nodes as the focused/scrolled row. Fix: prefer the smallest
non-negative real ID when one exists. Verify: with Pack on, multi-select members under a virtual
group.

**S14-F9** - `AndroidBomPanel.cs:977` - Dead code - Low - Confirmed.
`row` is fetched by index from `_visibleRows`, then immediately checked for membership in the same
list (always true). Fix: remove the redundant `Contains` check. Verify: inspection.

Verified correct: viewport selection clears on new file then restores pending state; tree<->viewport
sync both directions; hide/show reflected in renderer; isolate reversible.

### Slice 15 - Properties panel

**S15-F10** - `AndroidBomPanel.cs:1119` - Android UI Logic - Low - Candidate.
BOM rows use a fixed 24dp height with 12sp single-line cells; at large system font scale (sp
scales, dp doesn't) text can clip vertically. Fix: `WrapContent` + `MinHeight`, or size from
measured text. Verify: set device font to largest, open BOM.

Verified correct: empty selection handled; per-group row cap (50) prevents UI block; multi-select
count surfaced; null/empty values -> "(empty)"; values selectable for copy.

### Slice 16 - Section cut system

**S16-2** - `MainActivity.cs:8410` - Android UI Logic - Low - Likely.
Custom (3-point) section placement routes all touches to the gesture source and drops every
non-Tap gesture, freezing orbit/pan until the plane is placed or the tool is exited (awkward when
a point is occluded). Fix: allow orbit/pan during Custom placement; intercept only the commit Tap.
Verify: enter Section -> Custom, place 1 point, attempt to orbit.

**S16-5** - `GlesViewportRenderer.cs:1449` - Rendering - Low - Likely.
If the async cap-geometry build is one generation stale (fewer entries than visual planes),
trailing planes render no cap for a frame or two with no warning outside diagnostics. Fix:
re-render/log when lengths differ. Verify: rapidly add/remove planes while caps are visible.

**S16-7** - `GlesSectionOverlay.cs:449` - Rendering - Low - Confirmed.
The overlay feeds `uSectionPlaneCount=0`, so visual plane rectangles and contour lines are not
clipped by other intersecting planes (the caps are clipped). Cosmetic: planes poke past their
mutual intersection. Fix: plumb `SectionPlanes` into the contour-line draws (the ribbon shader
already has the uniform). Verify: add two perpendicular planes.

Verified correct: clip uniforms reach mesh/clay/CAD-edge/normal-depth/outline-mask/pick programs
with identical logic; cap stencil pass balances state (D32FS8); collinear/degenerate 3-point
planes rejected with a toast; cancel/clear/reset paths reset modal state; sections cleared on real
reload.

### Slice 17 - Measurement tools

**S17-1** - `GlesMeasurementOverlay.cs:55` - Rendering - Low - Confirmed.
The measurement overlay has no section-clip and renders depth-test off, so a measurement in a
sectioned-away region still draws over the cut. Likely intended (annotations shouldn't be
occluded). Fix: if clipping is wanted, add `uSectionPlanes` to the measure shaders. Verify: place
a face-to-face spanning a body, add a section through it.

**S17-4** - `AndroidMeshRaycastAcceleration.cs:68` - Hardening - Low - Likely.
`Span<int> stack = stackalloc int[128]` BVH traversal with no bounds check (not reachable with the
balanced median split, but a future split heuristic could overflow). Fix: add a stack bound /
size from tree depth. Verify: force unbalanced splits in a unit test.

**S17-6** - `AndroidMeasureIntegration.cs:58` (`EdgeSnapService` statics) - Settings - Low - Likely.
Snap configuration (endpoint/midpoint snap, tolerances, visibility filter) is set as static
globals, so measurement snap toggles silently change section custom-point snapping, and
`_measure.Dispose()` nulls the shared visibility filter. Fix: make snap config instance-scoped
(touches shared `../src` core - confirm as a shared design decision first). Verify: toggle measure
endpoint/midpoint snap, then place a custom section point.

Verified correct: units/formatting; raycast hit testing; labels refresh on camera move in
consistent pixel space; measurements cleared on real reload; cancellation present.

### Slice 18 - View cube and axis gizmo

No findings. Verified correct: the axis triad (`GlesAxisTriadOverlay`) is a render-only,
non-interactive overlay gated on `ShowAxes`, depth-sorted, camera-aligned, with 2D screen-space
labels that cannot mirror; section-gizmo hit-test arc<->render axis mapping matches the renderer;
gizmo down-hit consumes the touch (blocks orbit); dip->px conversion uses density. (There is no
interactive view cube, so the hit-test / mirrored-label / touch-block risks do not apply.)

### Slice 19 - Hover, outline, clay outline

**S7/19-F4** - `GlesOutlineRenderer.cs:179`; `GlesViewportRenderer.cs:1009-1032` - Rendering - Low - Confirmed.
The selection/hover outline mask renders with no depth test (no depth attachment), so the outline
shows around the full silhouette even where the selected body is occluded - inconsistent with the
depth-tested inline highlight. Fix: decide intent; if occluded-visible isn't wanted, attach a depth
renderbuffer to the mask FBO and depth-test the mask draw. Verify: select a body, orbit so another
occludes it.

**S19-F7** - `GlesViewportRenderer.cs:163,1011` - UI/Scene Sync - Candidate.
`HoveredMeshIndex` is never auto-cleared by the renderer; it relies on the host clearing it on
pointer-leave. If a hover-end event is dropped (S Pen lifted off-surface), the hover highlight can
stick. Fix: verify the host clears `HoveredMeshIndex=0` on ActionHoverExit/Up/Cancel; optionally a
renderer-side safeguard when navigation starts. Verify: hover with S Pen, lift outside the viewport.

Verified correct: hover skipped when the hovered mesh is also selected; selection outline drawn
after hover (selection wins); clay mode correctly disables the silhouette pass; selection/hover
precedence consistent in the mesh shader.

### Slice 20 - Rendering modes and visual parity

**S20-F10** - `GlesViewportRenderer.cs:760,997` - Rendering parity - Candidate.
In plain "Shaded" (not ShadedWithEdges) geometric CAD edges are off, but the screen-space
silhouette overlay still runs (gate only excludes Clay/Wireframe). So Shaded shows silhouette lines
but no feature/boundary edges - confirm this matches desktop intent. Fix: if Shaded should have no
edge overlays, add `Mode == ShadedWithEdges` to the silhouette gate. Verify: switch to Shaded and
check for silhouette lines.

Verified correct: ShadedWithEdges / Shaded / Wireframe / Clay all switch live; Realistic shimmed to
Shaded; x-ray isolation state doesn't leak across frames; grid depth/blend correct; hidden geometry
excluded from pick.

### Slice 21 - Markup / review tools

N/A - no markup/redline/annotation-review feature exists in the Android app ("annotation" in
`MainActivity` refers to 3D measurement labels). The only related surfaces are the QR scanner and
measurement overlays, covered in slices 22 and 17.

### Slice 22 - Cloud / login / API

**S22-1** - `AndroidManifest.xml:14-15`; `network_security_config_release.xml:3` - Hardening - **High** - Confirmed.
- Problem: release globally permits cleartext for all domains (`usesCleartextTraffic="true"` + `<base-config cleartextTrafficPermitted="true">`); `CloudServerConfig` supports an `http://` Local profile and `NormalizeServerUrl` accepts `http`. Bearer/refresh tokens, sign-in password, TOTP, and downloads can travel in plaintext. The `_debug` config is byte-identical and unreferenced.
- Fix: `<base-config cleartextTrafficPermitted="false">` + a narrow `<domain-config>` allowlisting only the operator LAN host(s) (or gate cleartext to the Local profile); remove `usesCleartextTraffic` from the manifest.
- Verify: release build blocks `http://` internet login, allows the configured local host, HTTPS unaffected.

**S22-2** - `CloudApiClient.cs:915,983` - Cloud/API - Medium - Likely.
Per-chunk PUT is retried, but an ultimate chunk failure restarts the whole upload with a new
`uploadId` (no received-chunk reconciliation); duplicate-chunk handling relies on undocumented
server idempotency. Fix: query received chunks on retry / document the digest-based idempotency.
Verify: kill the connection mid-upload.

**S22-2b** - `CloudApiClient.cs:1332` - Cloud/Hardening - Low - Confirmed.
`EnsureSuccessAsync` reads the entire error body with no size cap and no CancellationToken before
deserializing. Fix: capped read + thread the operation `ct`. Verify: stub an oversized error body.

**S22-3** - `CloudApiClient.cs:29` - Hardening - Low - Likely.
Untrusted JSON has no overall size/list caps (MaxDepth defaults to 64, ok); list properties
(`projects`/`packages`/`versions`) are unbounded. Fix: content-length guard and/or list-size caps.
Verify: stub an oversized projects response.

**S22-4** - `CloudApiClient.cs:438,827` - Hardening - Low - Likely.
Server `download_url`/`preview_url` may be absolute; downloads are SHA-256-verified, but previews
are fetched + decoded from a server-chosen host with no hash check; default auto-redirect could move
fetches cross-host. Fix: host-check the resolved URL before sending / disable auto-redirect. Verify:
point `preview_url` at a foreign host in a stub.

**S22-12** - `CloudFilesPanel.cs:228` - UI/Threading - Low - Likely.
`RefreshAsync` mixes direct view mutation with `RunOnUi` (currently safe because the direct paths
run on the UI-thread caller before any await). Fragile if an earlier await is added. Fix: route all
view mutations through `RunOnUi`. Verify: assert UI-thread at method entry.

**S22-17** - `CloudSecureStore.cs:29`, `CloudApiClient.cs:143` - Hardening - Candidate.
On `must_change_password` (and session-clear) the remembered password (encrypted at rest - fine) is
not cleared, so it's re-presented next dialog open. Fix: clear remembered password on those paths.
Verify: force `must_change_password`.

**S22-18** - `AndroidQrScannerDialog.cs:555` - Hardening - Info.
The full scanned QR payload is logged at Info (part numbers today). Awareness only; redact if QR
payloads ever carry sensitive data.

Verified correct: tokens encrypted via AndroidKeyStore AES/GCM (not plaintext); bearer host-
validation (`ValidateAuthorizedRequestUri`) prevents cross-host token leakage; SHA-256 download
verification; control-plane refresh timeout-bounded; chunk idempotency keys stable per operation;
no tokens/passwords/PII in logs; QR camera-permission flow correct.

### Slice 23 - Local storage, cache, and logs

**S23-1** - `RecentFilesStore.cs:209` (timeout `:13`); caller `RecentFilesBottomSheet.cs:119` - Threading/Async - Medium - Confirmed.
- Problem: `ProbeReadableAccess` does `Task.Run(...).WaitAsync(5s).GetAwaiter().GetResult()` per entry (up to 10) inside the `Gate` lock; `Load` can block the calling thread ~50s and serialize all recent-files access. Only the one bottom-sheet caller offloads; `Load` is public static, so any UI-thread caller ANRs.
- Fix: `LoadAsync` probing concurrently with `Task.WhenAll`, or at least move probing outside the lock; doc-warn `Load` is not UI-thread-safe.
- Verify: 10 slow URIs stay bounded; lock not held during probes.

**S23-8** - `CloudApiClient.cs:760` - File I/O - Candidate.
`DeleteCloudTempFile` deletes unconditionally with no guard against the live session path. Fix:
compare against `_activeCloudSession?.LocalPath` and skip if equal. Verify: audit call sites.

**S23-10** - `AndroidCrashLogger.cs:112` - File I/O - Low - Confirmed.
If truncation (`WriteAllText(logPath,"")`) throws after the rotate copy, the catch swallows and the
next `AppendAllText` grows the un-truncated log; a perpetually-failing truncate grows it unbounded.
Fix: skip the append when truncation fails. Verify: simulate `WriteAllText` throwing.

**S23-11** - `AndroidLogcatFeed.cs:96` - Threading - Low - Likely.
`ReadLineAsync()` is awaited without the cancellation token; a stalled logcat read only unblocks on
process Destroy. Fix: use `ReadLineAsync(token)` (.NET 8). Verify: start/stop the feed rapidly.

**S23-14** - `RecentFilesStore.cs:164` - Dead code - Low - Confirmed.
Private `Save` (locks then calls `SaveUnsafe`) has no callers. Fix: delete. Verify: build.

**S23-15** - `RecentFilesStore.cs:104` - Dead code - Low - Confirmed.
Public `TryTakePersistableReadPermission` wrapper just delegates to the read+write variant and has
zero callers (misleadingly named). Fix: delete (or implement a real read-only acquisition). Verify:
build/tests.

**S23-16** - `CloudApiClient.cs:1447` - File I/O - Low - Likely.
The `fa-cloud-open` download cache has no size/age eviction (only purged on sign-out / explicit
delete); a download that succeeds but is never registered (crash) orphans large files. Fix:
opportunistic age/byte sweep on startup, excluding the active session path (see S23-8). Verify:
create stale files in `fa-cloud-open`.

Verified correct: crash/logcat buffers bounded and rotated; atomic `.part` rename + age/keep-N
cache pruning; free-space checks before downloads; `.part` temp cleaned on failure; no token/PII in
logs.

### Slice 24 - Manifest, resources, packaging

**S24-H2** - `FabricationAssistant.App.Android.csproj:20,72` - Packaging - Medium - Likely.
Only arm64-v8a + x86_64 are packaged (no armeabi-v7a); minSdk 24 admits 32-bit-only devices where
the Draco P/Invoke would fail. Fix: confirm arm64-only is intended and enforce (Play 64-bit signal),
or add the ABI via `build-libdraco.ps1 -Abis`. Verify: inspect `lib/` in the APK; try a Draco import
on an armeabi-v7a emulator.

**S24-M3** - `activity_main.xml:977-1028,690` - UI/a11y - Low - Confirmed.
Section/measure switches use `cd_*` (content-description) strings as their visible `android:text`,
coupling label wording to a11y wording. Fix: add dedicated visible-label strings; keep `cd_*` for
`contentDescription`. Verify: visual + TalkBack.

**S24-2** - `network_security_config_debug.xml` - Dead/Hardening - Low - Likely.
The `_debug` config is byte-identical to release and unreferenced by the manifest (which hard-codes
`_release`). Fix: delete it or wire it per build config (meaningful only after S22-1). Verify: search
manifest/csproj for the debug name.

**S24-L1** - `strings.xml:4,77` - Dead code - Low - Likely.
`open_button` and `cd_open_drawer` are unreferenced (the app uses a custom nav rail, not a drawer).
Fix: remove after a full-tree confirm. Verify: build (resource codegen + lint).

**S24-L3** - manifest vs mipmaps - Dead code - Candidate.
The manifest uses `fa_launcher`; the `ic_launcher*` icon set may be unreferenced (or still pulled in
by the adaptive-icon XMLs). Fix: confirm `fa_launcher.xml` foreground/background/monochrome refs
before removing. Verify: open `fa_launcher.xml`.

**S24-L5** - `FabricationAssistant.App.Android.csproj:21,31` - Packaging - Low - Confirmed.
No profiled AOT and no trimming configured (larger APK / slower start). Positive side: trimming OFF
means DI/JSON/SignalR/P-Invoke reflection paths are not at risk. Fix: optional - evaluate profiled
AOT; only consider `AndroidLinkMode=SdkOnly` with explicit `[DynamicDependency]` roots and full
end-to-end validation. Verify: measure cold start.

Verified correct: minimal/justified permissions (INTERNET, CAMERA with `required=false` + rationale);
`allowBackup="false"`; GLES 3.1 `uses-feature required` matches the renderer; min/target SDK
consistent across all projects; thorough content descriptions; no inline `#RRGGBB` colors; no
duplicate resource names; native-lib + debug-keystore build-time guards present.

### Slice 25 - Tests, build, verification

**S25-1** - `GlesRendererSourceTests.cs`; test csproj `:127-132` - Tests/Verification - **High** - Confirmed.
- Problem: the renderer can't load in the `net8.0` host, so these "tests" assert substrings exist in `.cs`/`.frag` files - they pass with wrong logic if the string is present and break on benign refactors. No GLSL compile or framebuffer behavior is executed. There is no shader-compile smoke test.
- Fix: rename to `*SourceGuards`; add a host-runnable GLSL lint and an emulator shader-compile smoke (extend `run-render-queue-test.ps1`) asserting `COMPILE_STATUS`.
- Verify: add a shader typo -> the new test fails (current suite still passes, proving the gap).

**S25-2** - `...Tests.csproj:54-139`, `Directory.Build.props` - Tests/Verification - Medium - Confirmed.
The test project compile-links select Android source into a plain `net8.0` assembly (CA1416
suppressed) and `tools\test.ps1` runs only that - it never builds the real `net8.0-android` TFM. A
green `test.ps1` can coexist with a broken (or non-compiling) Android app, and the link list is
brittle. Fix: document that verification needs both `build.ps1` and `test.ps1`; optionally chain
them. Verify: break an Android-only file -> `test.ps1` passes, `build.ps1` fails.

**S25-3** - tests folder; `tools\build-libdraco.ps1:25` - Tests/Verification - Medium - Likely.
No tests for zero-byte / zip-bomb `.fa` (despite `FaArchiveLimits` being linked), truncated Draco
through the native decoder, or GL surface re-create. `build-libdraco.ps1`'s global
`ErrorActionPreference='Continue'` can let a failed `Copy-Item` ship a stale `.so` past the
`Exists()` guard. Fix: add `FaArchive`/limits + zero-byte host tests and a device corrupt-import
smoke; scope `Continue` to the cmake calls and add a non-zero-length `.so` check. Verify:
`tools\test.ps1`; simulate a locked output dir.

**S25-L4** - `run-render-queue-test.ps1:127` - Verification - Low - Likely.
The frame/queue timing regexes can match nothing if the log format changes, reporting 0 frames with
exit 0 (false pass). Fix: fail if the parsed frame count is 0. Verify: run against renamed log tags.

Verified correct: `tools\build.ps1` does NOT hide errors (`Out-Host` + explicit `$LASTEXITCODE`
checks under `Stop`); native-lib validation target present; both `.so` files exist under
`deps\prebuilt`.

## 4. Confirmed bugs (clear evidence / call path)

S14-1 (tree/BOM leak), S1-1 (save on dead Activity), S5-1 (Draco attribute loss),
S5-3 (.gltf Draco bypass), S13-1 (quadratic selection sync), S23-1 (sync-over-async
recent files), S10-1/S10-2/S9-1/S10-F4/S10-F7/S10-F2 (settings desync/no-op),
S11-1 (zoom-window second finger), S7/19-F4 (occluded outline), S7-F6/S14-F9/S23-14/S23-15
(dead code), S16-7 (overlay clip), S22-1 (cleartext), S25-1/S25-2 (test gaps).
Likely-but-strong: S14-2, S5-2, S15-1, S6-1, S7-1, S22-2.

## 5. Android UI logic problems

S10-1/2/F4/F7/F8/F9 (Preferences control desync and ranges), S11-1 (zoom window),
S12-F3 (divider stylus), S14-F8 (presented-ID), S15-F10 (BOM clip), S16-2 (placement
blocks orbit), S24-M3 (cd_* labels).

## 6. Lifecycle / threading problems

S1-1 (save), S1-2 (cloud callbacks), S13-1 and S15-1 (UI-thread/scene race),
S14-5 (sync tree build), S23-1 and S23-11 (sync-over-async / no CT), S5-M5 (finalizer).
Verified correct: AppSettings.Initialize-first, no singleton/context leaks, EGL
context-preserve + drop-and-reupload on context loss, symmetric event attach/detach,
render-queue gated after dispose, rotation handled via configChanges.

## 7. Rendering / GLES problems

S7-1 (AO upsample), S6-1 (MSAA sentinel), S7/19-F4 (occluded outline), S16-7 (overlay
clip), S17-1 (measurement clip), S6-F4/F5/F6, S7-F3/F6/F12. Verified correct: ES 3.10
precision, integer texture formats, 16-bit packed-depth round-trip, per-pass state
save/restore, section clip uniforms across all programs, cap stencil balance (D32FS8), no
GL resource leaks, shader diagnostics, GL-thread guard, RequestRender after state changes.

## 8. File import / FA / Draco problems

S5-1 (attribute loss), S5-2 (buffer index), S5-3 (.gltf Draco), S5-L1 (overflow),
S5-L4 (.bin orphans), S3/4-M1/S3-M2 (dead direct path bypasses size cap + in-place
mutation), S3-M4 (write grant), S4-L2 (validator/ReadGlb divergence). Verified correct:
ABI packaging + P/Invoke + native bounds-checking + `NativeGate` serialization; no
partial-scene corruption; `.fa` validated before import; atomic `.part` rename + pruning.

## 9. Settings and persistence problems

S9-1 (no-op setter), S9-F6 (surface_b migration), S10-F2/F4/F7 (UI/clamp mismatches),
S6-1 (MSAA), S17-6 (shared snap statics), S9-F10 (Apply/migration untested). Verified
correct: MSAA applies live, NaN/Infinity hardening, Realistic->Shaded shim round-trip,
theme-attr colors, every render control fires `OnSettingsChanged`.

## 10. Scene tree / viewport synchronization problems

S14-1 (leak), S14-2 (expansion leak), S15-1 (pick race), S13-1 (perf), S14-5 (sync
build), S14-F8 (presented ID), S13-F7 (multi-mesh fill). Verified correct: pick
coord/Y-flip/R32UI encode, selection cleared on new file, properties empty/null handling,
x-ray ghosts excluded from pick.

## 11. Markup / cloud / API problems

Markup: none exist (Slice 21 N/A). Cloud: S22-1 (cleartext), S22-2 (upload resume),
S22-2b/3/4 (response hardening), S23-16 (cache growth), S22-17 (remembered pwd). Verified
correct: tokens encrypted via AndroidKeyStore AES/GCM, bearer host-validation, SHA-256
download verification, no tokens/passwords/PII in logs, QR permission flow.

## 12. Hardening recommendations

Transport (S22-1), unbounded reads/JSON (S22-2b/3), redirect host-check (S22-4), cache
sweeps (S23-16), BVH bound (S17-4), `ChooseConfig` null log (S6-F4), `OnSurfaceChanged`
null-`_gl` log (S6-F6), crash-log truncate-failure (S23-10), QR payload redaction
awareness (S22-18). Pattern: where a guard prevents a crash, also log the invalid state.

## 13. Dead code candidates

Confirmed: `RecentFilesStore.Save` (S23-14), `TryTakePersistableReadPermission` (S23-15),
BOM redundant `Contains` (S14-F9), edge-vertex normal/flag payload (S7-F6),
`network_security_config_debug.xml` (S24-2), strings `open_button`/`cd_open_drawer`
(S24-L1). Candidate (verify first): renderer back-compat aliases (S6-F9),
`copyToImportCache:false` path (S3/4-M1), `ic_launcher*` mipmaps (S24-L3).

## 14. Simplification opportunities

Single ContentResolver query (S3-L5), `GpuScene` index maps (S13-1), visibility version
gate (S6-F5), one-pass `PackDuplicateChildren` grouping (S14-5), consolidate appearance
aliases through `SceneAppearance` (S6-F9).

## 15. Prioritized fix plan

1. S14-1 tree/BOM tooltip leak (functional leak on the core workflow).
2. S1-1 cancel `_saveCts` + guard UI continuations on destroy.
3. S5-1 / S5-3 / S5-2 Draco: surface unsupported-attribute / `.gltf` / buffer-index cases (stop silent data loss).
4. S14-2 expansion-state cross-file leak; S15-1 pick-vs-swap guard.
5. S10-1/2/F4/F7 + S9-1 + S9-F6 Preferences desync/no-op/migration.
6. S22-1 release cleartext config.
7. S13-1 / S23-1 quadratic selection sync and sync-over-async recent files (perf/ANR).
8. S25-1/S25-2/S25-3 test-gap closure (shader smoke + corrupt-file + build-in-CI).
9. Dead-code and low-risk hardening sweep (S23-14/15, S14-F9, S7-F6, S24-2/L1, S17-4, S6-F4/F6).

## 16. Safe first patch list (small, localized, low-regression)

- P1 - S1-1: cancel `_saveCts` in `OnDestroy`; guard `finally` UI calls with `!_isDestroyed`. (localized)
- P2 - S9-1: make `CloudServerUrl` get-only (remove value-discarding setter). (localized)
- P3 - S9-F6: add `surface_b` to `LegacyFloatDefaultsToRemove`. (one line)
- P4 - S10-F9: move "Section gizmo scale" slider to Section Tools. (relocation)
- P5 - S10-1/F4: refresh the Automatic-grid switch / edge-width slider after dependent writes. (localized)
- P6 - S14-F9 / S23-14 / S23-15: delete confirmed dead members. (deletions)
- P7 - S5-1/S5-3: add `FA.Draco` warn/throw for unsupported attributes and `.gltf` Draco. (decorator-local)
- P8 - S17-4: add BVH stack bound; S6-F4/F6: add the missing diagnostic logs.
- P9 - S14-1: the leak fix (slightly larger - `DisposeTree(convertView)` minimal form first, holder reuse as follow-up).

Recommended order: P1-P8 as the first build-and-verify batch (smallest blast radius),
then P9 (the leak) as its own batch with focused tree/BOM testing. S22-1, S25-x, and the
perf items (S13-1/S23-1) each warrant their own change + verification.

## 17. Verification plan

- Build: `tools\build.ps1 -Configuration Debug` after each batch; on failure, report the exact error and stop. Also run `tools\build.ps1 -Configuration Release` for the cleartext (S22-1) change. Note: `tools\test.ps1` (net8.0 host) does NOT prove the Android build - always run `build.ps1` too (S25-2).
- Unit tests: `tools\test.ps1`; add new tests for `AppSettings.Apply`/migration symmetry (S9-F10), `FaArchiveLimits`/zero-byte rejection (S25-3), Draco unsupported-attribute detection (S5-1).
- Emulator/device smoke: `adb install -r ...-Signed.apk`; launch; extend `run-render-queue-test.ps1` with a shader-compile smoke (S25-1) and a corrupt-import case.
- Manual UI: Preferences - toggle Auto-grid then drag spacing (switch clears); re-enable edges (slider matches); type clip values (commit on Done). Zoom Window with a second finger. Scroll a large tree/BOM and watch memory (S14-1).
- Rendering: a Draco-skinned/colored GLB (warn/throw fires); AO on thin silhouettes; selection outline behind an occluder; sections reaching all passes.
- Lifecycle: rotate, lock/unlock, background/foreground, open then background during import, save then finish/rotate (no BadTokenException), reload after context loss.
- Import: valid GLB, valid FA, corrupt file, missing inner package content, Draco GLB.

## 18. Optional deeper refactors (only if justified later)

- ListView holder-reuse across both panels (turns S14-1/S14-F6 into a clean recycling implementation).
- Off-UI-thread tree build for very large assemblies (S14-5) guarded against concurrent scene swaps.
- Generic Draco attribute preservation in the native wrapper (full fix for S5-1).
- Instance-scoped `EdgeSnapService` config to decouple measurement/section snapping (S17-6) - touches shared core, needs sign-off.
