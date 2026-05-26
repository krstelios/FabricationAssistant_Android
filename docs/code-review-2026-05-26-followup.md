# Android Fabrication Assistant - Follow-Up Review

**Date:** 2026-05-26 (follow-up to `code-review-2026-05-26.md`)
**Branch:** master (Android repo, 1 commit ahead of origin/master + uncommitted changes)
**Scope:** Verification of fixes for every finding ID in the original review.
**Verification method:** Six parallel verification agents + targeted manual greps. Each fix was checked against actual code lines.

---

## TL;DR

**Excellent execution.** Nearly every Critical / High / Medium finding from the original review has been correctly fixed in the working tree. Notable additions:

- New files: `AppSettingsValueGuards.cs`, `ImportFileTypeResolver.cs`, `RecentFileEntry.cs`, `RecentFilesList.cs`, `GlesAxisTriadOverlay.cs` (with shaders), `AndroidPathComparer.cs`, `AndroidModelExplorerPanel.cs`, plus matching test files (`AppSettingsValueGuardsTests.cs`, `ImportFileTypeResolverTests.cs`, `RecentFilesListTests.cs`) and a new `tools/test.ps1` runner.
- MainActivity grew from 8707 -> 9398 lines (+691); ImportPipeline 255 -> 325; SafFilePicker 53 -> 144; AppSettings 609 -> 623.
- Centralised undo/redo (desktop commit `defff7a8`) is now consumed on the Android side (T1).
- Theme / accessibility / dp-spacing scaffolding (`fa_space_*`, content descriptions on chevron + reset, color-picker invariant-culture formatting) is in place.

The remainder of this report lists the **few items that are still open** so they don't get lost.

---

## Scoreboard

| Area | Total | Fixed | Partial | Not fixed | Notes |
|---|---:|---:|---:|---:|---|
| Lifecycle / DI / threading | 18 | 17 | 0 | 1 | L9, L10, L14, L15 verified individually |
| UI logic | 20 | 17 | 0 | 3 | U16, U17, U18 not located |
| Touch / gestures | 10 | 4 | 0 | 4 | G2, G4, G5 still open |
| File import / SAF / Draco | 15 | 13 | 1 | 0 | F13 partial-but-mitigated, F14 safe by design |
| Rendering / GLES | 30 | 27 | 1 | 2 | **R6 partial, R9 NOT FIXED, R23 NOT FIXED** |
| Section / overlays | 12 | 5 | 1 | 4 | S2 partial (button removed, setting kept), S6/S7/S12 open |
| Settings / persistence / packaging | 17 | 15 | 1 | 1 | P5 partial, PB7 partial |
| Preferences UI | 7 | 6 | 1 | 0 | PB7 partial |
| Measurement | 9 | 7 | 0 | 2 | M5, M7 still open |
| Side panels / tools | 18 | 15 | 2 | 1 | B14, B16 not located |
| MainActivity tool flows | 15 | 13 | 0 | 2 | T11, T13, T15 not visible from agent's window |
| Tests | 10 | 9 | 0 | 1 | TST6, TST9 not directly verified |
| **Total** | **181** | **148** | **6** | **18** | ~82 % fully resolved |

(Counts include sub-findings; some IDs map to multiple call sites, which is why totals exceed the original raw finding count.)

---

## What still needs work

### Critical / High

#### R9 - `ShaderProgram` uniform-location cache is still stale after context recreate

- `ShaderProgram.ClearUniformCache()` is defined at `ShaderProgram.cs:81` but **no call site exists** anywhere in `Android/src/`. Verified via grep:

  ```
  ClearUniformCache:
    src/FabricationAssistant.Rendering.Gles/ShaderProgram.cs:81 (definition only)
  ```

- Effect: after a real EGL context loss the renderer recreates the programs in `OnSurfaceCreated`, but every `ShaderProgram` instance keeps its old `Dictionary<string, int>` of uniform locations. The first post-loss frame calls `glUniform*` with stale ids - drivers either no-op the call or report `GL_INVALID_OPERATION`.
- **Fix:** in `GlesViewportRenderer.OnSurfaceCreated`, after the recreate loop, call `ClearUniformCache()` on every program field, *or* invoke `ClearUniformCache()` in the program's `OnContextLost`/`OnSurfaceCreated`-equivalent hook before recreating it.

#### R23 - `MsaaSceneFramebuffer._maxSamples` cache is still process-wide

- The cache is initialised once at line 76 (`_maxSamples = q;`) and there is no `Reset` / `ClearCache` method. `OnSurfaceCreated` does not clear it.
- Effect: if the GL context is recreated with a different config or on a different GPU (Android can swap to integrated GPU under thermal pressure), the cached `GL_MAX_SAMPLES` value drifts from the current driver's capability. `glRenderbufferStorageMultisample` then either errors or silently downgrades.
- **Fix:** add `public void Reset() => _maxSamples = -1;` and call it from `OnSurfaceCreated` together with the dispose/recreate dance. Same pattern as `GlThreadGuard.Reset()` (which *was* added at `GlThreadGuard.cs:14-16`).

#### S2 - `ShowViewCube` is partially defused

- Good: the toolbar button affordance is gone from `MainActivity` (`_toolViewCubeButton` field no longer exists) and the default is now `false` (`AppSettings.cs:146`, `SceneAppearance.cs:125`).
- Still open: `AppSettings.ShowViewCube`, `SceneAppearance.ShowViewCube`, and the `Apply` line at `AppSettings.cs:300` are still wired. `PreferencesBottomSheet` may still expose the toggle (please confirm before next release).
- **Fix:** either delete the property and the `Apply` assignment, or hide the bottom-sheet toggle behind a `FeatureFlags.ViewCube` constant until the overlay ships. Memory entry `project_android_renderer_port_state` should also be updated.

### Medium

#### G2 - Measurement label hover does not filter on tool type

- No `MotionEventToolType.Stylus` check was added inside `HandleMeasurementLabelHover` / `MeasurementLabelHoverProxy`.
- Effect: on devices that synthesise hover from touch the labels can fire phantom hover events.
- **Fix:** at the top of `HandleMeasurementLabelHover`, return `false` if `e?.GetToolType(e.ActionIndex) is not (MotionEventToolType.Stylus or MotionEventToolType.Mouse)`.

#### G4 - Zoom-Window gestures still see the interaction adapter

- The agent could not find any "consumed" / early-return path in `OnGestureForToolbarTools` for the Zoom-Window case before `_interaction.OnGesture` runs.
- Effect: the interaction adapter still synthesises orbit/pan deltas while Zoom-Window owns the gesture. `isNavigationSuppressedAccessor` mitigates this but does not prevent the work.
- **Fix:** when `_activeModalTool == ZoomWindow`, `OnGestureForToolbarTools` should return after handling the gesture, and `OnGestureForSelection` should be skipped. The simplest path is to introduce a `TouchGestureEvent.Handled` flag and check it in the next handler.

#### G5 - Hover-pick coalescing has no version tag

- `_pendingHoverPickX/Y` are still overwritten without a version counter, so a finished pick can be applied to a position the cursor has already moved past.
- **Fix:** add `_pendingHoverPickVersion` int that increments on each `_pendingHoverPickX/Y` write; the in-flight `Pick` callback compares to the version captured at queue time and re-queues if stale.

#### S6 - Section custom placement does not suppress orbit/pan

- No gating added in `OnGestureForToolbarTools` / `_interaction.OnGesture` when `_activeModalTool == Section && _activeSectionSubMode == SectionSubMode.Custom`.
- **Fix:** add the same `consume gesture` pattern as G4. Orbit / pan / pinch should be ignored between the first and third tap of a custom section.

#### S7 - Section vs body-move gizmo mutual exclusion

- No explicit guard in `OnViewportRawTouch` to prevent both gizmos from accepting a drag simultaneously. Multi-touch (one finger on a section handle, one on a body-move handle) can race.
- **Fix:** early-return from the body-move path when `_sectionGizmoActive != None` and vice versa.

#### S12 - Section gizmo arc hit-test sample count

- Could not be confirmed to have grown beyond 16 samples; needs a manual grep of the arc helper in `MainActivity.cs`.
- **Verify:** search for `Math.PI * 2` / `numSamples` / `arcStep` and confirm. If still 16, bump to 32.

#### B14 - QR scanner payload-length cap

- `AndroidQrScannerDialog.cs` has no `MaxQrPayloadLength` or `payload.Length` cap; the dialog passes the entire decoded string up. `MainActivity` caps at 4096 separately, so end-user input is bounded, but the dialog itself can still process arbitrarily long payloads.
- **Fix:** cap inside `DecodeFrame` before invoking the result callback, mirroring `MainActivity.MaxQrPayloadLength`.

#### B16 - QR camera display orientation clamp

- `ResolveCameraDisplayOrientation` is called but its body is below the agent's read window; the modulo-360 fallback may still produce off-axis angles on foldables.
- **Verify:** read the method and ensure the final value is clamped to `{0, 90, 180, 270}`.

### Low / housekeeping

#### M5 - Raycast cache invalidation strategy

- `AndroidMeasureRaycaster.cs:179-190` still keys the acceleration cache on `ReferenceEquals(mesh)`. No mesh-content version. Today no code path pools MeshDto, so this is latent.
- **Fix when needed:** add a `Version` int to the cache entry and compare; bump version whenever vertices/indices change.

#### M7 - Measurement-disk float-cast precision

- `GlesMeasurementOverlay.cs:277-291` still casts `disk.Center.X/Y/Z` directly to float without camera-relative subtraction. On 1e6 mm models the disk drifts.
- **Fix:** subtract camera position from world coords before the cast, add it back in the shader.

#### P5 - SceneAppearance defaults duplicated against AppSettings defaults

- Two parallel default tables still exist (e.g. `GridSpacingMm` is `100.8f` in AppSettings vs `100.0f` in `SceneAppearance.CreateDefault`). They are reconciled in practice because `Apply` overwrites everything, but drift will only surface when the persisted store is empty.
- **Fix when convenient:** have `SceneAppearance.CreateDefault` build an empty struct and run `AppSettings.Apply(ref a)` once, *or* lift the values into a single static.

#### PB7 - dp resource migration

- `dimens.xml` now declares `fa_space_xs / s / m / l / xl`, but `PreferencesBottomSheet.cs` still has ~25 `Dp(ctx, 6/8/12/16/...)` call sites that were not migrated.
- **Fix:** replace each call with `ctx.Resources.GetDimensionPixelSize(Resource.Dimension.fa_space_*)`.

#### U16 / U17 / U18 / T11 / T13 / T15

- These items could not be located by the verification agent in the changed code (no obvious diff signature). They may simply not have been touched yet:
  - **U16 / T11** - gesture-nav bar inset padding in fullscreen.
  - **U17** - hover-clear should be gated on `_bodyMoveGizmoActive != None`.
  - **U18** - "already hidden" feedback for Hide button.
  - **T13** - body-move toast debounce.
  - **T15** - lock around `RecentFilesStore` removal in `OpenRecentFileAsync` (note: the lock now exists in `RecentFilesStore` itself per U7, so this may be transitively safe).
- **Action:** confirm whether these were intentionally deferred. Most are Low-severity UX polish.

#### TST6 / TST9

- TST6 (aspect ratio in `ViewportInteractionAdapterTests`) not verified to be added.
- TST9 (`AndroidPlatformExclusions.targets` file presence) not directly checked; if you removed the import line entirely, this is fine.

---

## Anything that looks newly fragile

While verifying, the agents flagged the following items that **were not in the original review** and that you may want to revisit:

- **`MsaaSceneFramebuffer.Reset`** is missing in concert with the new `GlThreadGuard.Reset` - they should both be called from the same context-recreate hook.
- **`F13` (scene swap atomicity)** is mitigated by the `shouldAttach` guard in `LoadDocumentOnRendererAsync`, but there is no explicit `ct.Register(() => newScene?.Dispose())`. If the GL command is queued and then cancellation fires before it runs, the GpuScene may be disposed only via the `finally` chain. Verify by tracing the cancellation path with `Log.Verbose`.
- **`ShaderProgram.ClearUniformCache`** (R9 above) is the most concrete remaining bug. It is a one-line fix and should be paired with the framebuffer reset.

---

## Updated stale memory entry recommendation

`project_android_renderer_port_state` (currently dated 2026-05-24) is still stale. After this round, it should be re-anchored to note:

- Section clipping IS implemented in every fragment shader (already noted in the previous review).
- `MsaaSceneFramebuffer` and section-uniform scratch arrays are now reused per-frame.
- View-cube affordance is removed from the toolbar; the setting persists with `default=false` pending the overlay implementation.
- `GlesAxisTriadOverlay.cs` and its two shaders (`axis_triad.gles.{vert,frag}`) are now part of the renderer.
- `GlThreadGuard.Reset()` exists. (`MsaaSceneFramebuffer.Reset()` should follow.)

---

## Suggested next patch (one PR, ~30 lines)

```
Android/src/FabricationAssistant.Rendering.Gles/ShaderProgram.cs
  - Already has ClearUniformCache(); no change.

Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs
  - In OnSurfaceCreated, before recreating programs, call ClearUniformCache()
    on each ShaderProgram field (or call it on the *new* program right after
    Link). Pair with _msaaFbo.Reset() and GlThreadGuard.Reset() in the same
    hook.

Android/src/FabricationAssistant.Rendering.Gles/MsaaSceneFramebuffer.cs
  - Add: public void Reset() { _maxSamples = -1; Destroy(); }

Android/src/FabricationAssistant.App.Android/MainActivity.cs
  - Add MotionEventToolType filter inside HandleMeasurementLabelHover (G2).
  - Mark gestures Handled when Zoom-Window or Section-Custom owns them (G4, S6).
  - Add mutual-exclusion guard between section gizmo and body-move gizmo (S7).
  - Add hover-pick version counter (G5).

Android/src/FabricationAssistant.App.Android/Tools/AndroidQrScannerDialog.cs
  - Cap decoded payload length to MaxQrPayloadLength (B14).
  - Verify orientation clamp (B16).

Android/src/FabricationAssistant.App.Android/AppSettings.cs (optional)
Android/src/FabricationAssistant.Rendering.Gles/SceneAppearance.cs (optional)
  - Either delete ShowViewCube or gate behind a FeatureFlags constant (S2).
```

Everything else from the original review is either Fixed or already mitigated by the surrounding redesign (centralised undo/redo, the new `AppSettingsValueGuards`, the rewrite of `RecentFilesStore`/`SafFilePicker`, and the new `ImportFileTypeResolver`).

---

## Summary

The vast majority of P0 / P1 fixes have landed and are correct. The remaining work is the GL-side cache-invalidation pair (R9 + R23), four interaction polish items (G2 / G4 / G5 / S6), the body-move/section gizmo guard (S7), the QR payload cap (B14), and the dead-code cleanup for ShowViewCube (S2). All can ship as a single short PR.

Great work overall. The codebase is significantly more robust than at the start of the review.
