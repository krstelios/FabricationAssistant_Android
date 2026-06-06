# Fabrication Assistant — Android: End-to-End Code Review

**Date:** 2026-06-05
**Scope:** ~58,000 lines of C# across 8 projects
**Method:** 8 parallel read-only subsystem audits. Code was **not** built or run this pass.
**Reviewer:** Claude (Opus 4.8), 8-agent fan-out

---

## 1. Verdict (TL;DR)

This is a **mature, carefully-engineered codebase** — markedly better than typical for its size. The hard, easy-to-get-wrong things are mostly done **right**: cross-thread camera access is snapshot-under-lock, GPU handle lifecycles are paired and context-loss-safe, the Draco native boundary has sound single-owner free semantics, auth tokens are KeyStore-encrypted with origin-bound bearer tokens, and the measurement engine is numerically robust double-precision math with a correct BVH.

**No critical "crashes-in-normal-use" defect was found.** The dominant risks are two different kinds:

1. **Structural** — `MainActivity.cs` is a **16,809-line God class** holding ~200 mutable fields and 12 subsystems; the orchestration layer is consequently untestable.
2. **Process / safety-net** — there is **no CI of any kind**, and the most memory-dangerous code (native Draco P/Invoke) and the renderer have **no behavioral test coverage** (the renderer is "tested" by 40 source-text `grep` assertions).

Around those sit a handful of **real, bounded bugs** worth fixing soon (Draco attribute loss, a window leak, a stale-cache measurement risk, gesture/jank issues).

### Subsystem health at a glance

| Subsystem | Maturity | Headline issue |
|---|---|---|
| GLES rendering & GPU lifecycle | 🟢 Strong | Per-frame allocations in selection/hover outline pass |
| Measurement / snapping / raycast | 🟢 Strong | Caches can go stale on in-place geometry edits |
| Security / cloud / networking | 🟢 Strong | App-wide cleartext HTTP for LAN (mitigated in code) |
| Native interop / Draco / import | 🟢 Strong (safety) | Silent attribute loss (skin/color/tangent dropped) |
| Input / gestures / threading | 🟢 Strong | Camera `PropertyChanged` storm → orbit jank while sectioning |
| UI panels / data / lifecycle | 🟡 Good | Column-filter `PopupWindow` leak on panel teardown |
| **Architecture / MainActivity** | 🔴 **Weak** | **16.8k-line God class, untestable orchestration** |
| **Tests / build / CI** | 🟠 **Mixed** | **No CI; native boundary & renderer untested** |

---

## 2. P0 — Safety-net gaps (do these first)

### ① No CI/CD pipeline exists — `[Critical]`
Full verification requires running three PowerShell scripts by hand (`build.ps1`, `test.ps1`, `run-render-queue-test.ps1`). The host test project deliberately targets plain `net8.0` and links only a *subset* of source, so it can stay green while the real Android app fails to compile. Only `deps/draco/.github/workflows/ci.yml` exists (Google's vendored CI, irrelevant to this app).
**Fix:** Add GitHub Actions running `tools/verify.ps1` (build + host tests) on every PR; gate merges. Track the device smoke as a manual/self-hosted stage.

### ② Native Draco decode boundary has zero behavioral coverage — `[Critical]`
`DracoNativeDecoder` (`unsafe` + `[DllImport]`, pinning, capacity marshalling, overflow guards) is never executed by any test — `DracoGltfTranscoderTests` only exercises the *managed* GLB container path. The most memory-corruption-prone code is the least tested, with no automated gate; a regression surfaces only as a crash on a user's device.
**Fix:** Add a device/instrumented round-trip test (encode a cube → `DracoMesh.Decode` → assert point/face counts and vertices) and host tests for the overflow/null paths.

---

## 3. P1 — Real bugs to fix

### ③ Draco transcode silently drops all attributes except POSITION / NORMAL / TEXCOORD_0 — `[High]`
`Draco.Android/native/draco_native.cpp:59-82` maps only 3 attribute types; `GetNamedAttribute(TEX_COORD)` returns only the first UV set. TANGENT, COLOR_0, extra UVs, JOINTS_0/WEIGHTS_0, and GENERIC attributes are discarded — only a `FA.Draco` logcat warning is emitted (`DracoGltfTranscoder.cs:188-205`). Skinned, vertex-colored, and normal-mapped Draco models import and render incorrectly.
**Fix:** Enumerate `mesh->num_attributes()`; return each attribute's `attribute_type()`, `data_type()`, `num_components()`, `normalized()` and copy raw per-point values preserving the original component type (do not force JOINTS/COLOR through float). Emit matching accessors in the transcoder. (This is the full fix deferred in the 2026-05-31 review.)

### ④ BOM column-filter `PopupWindow` leaks (and can throw `BadTokenException`) — `[High]`
`AndroidBomPanel.cs:411-422` (`OpenColumnFilter`) opens the popup into a local `var` and keeps no reference; `Dispose` (`:230-262`) never dismisses it. Open a header dropdown, then switch tools / reload model / close the panel → the `PopupWindow` stays attached, leaking its window token, the anchor cell, and the captured filter model; on some OEM builds a still-showing popup whose anchor was detached throws `BadTokenException`/`IllegalArgumentException`.
**Fix:** Store the active popup in a field; dismiss it at the top of `OpenColumnFilter` and in `Dispose`. Add `BomColumnFilterPopup.Dismiss() => _popup?.Dismiss();`.

### ⑤ Raycast/snap caches go stale on in-place geometry edits — `[High]`
`Measurement/AndroidMeasureRaycaster.cs:733-780` keys the BVH cache on a **3-sample** content hash (`Positions[0]`, `[mid]`, `[^1]`, plus length/bounds/tri-count); `FeatureEdgeExtractor` (`Core/Measurement/Engine/FeatureEdgeExtractor.cs:25,38-41`) keys only on the array *reference*. A mesh that mutates its `float[] Positions` in place (same reference/length/bounds, changes not touching the 3 sampled floats) yields a BVH and snap-edge set built over old geometry → raycasts hit phantom triangles and **measurements are silently wrong**.
**Fix:** Add a per-mesh geometry/content version that participates in `MeshAccelerationKey` and `FeatureEdgeExtractor`; or require importers to allocate a fresh array on any vertex change (and document the contract).

### ⑥ Camera `PropertyChanged` fires synchronously inside the adapter's `lock(_camera)` — `[High — perf]`
`ViewportInteractionAdapter.cs:192-198` (and PanZoom/Dolly/Zoom paths) raise `PropertyChanged` 4-6× per delta. Those callbacks run on the UI thread *while the camera lock is held* and, when a section/body-move gizmo is active, each one recomputes the gizmo via `MainActivity.cs:13376 → 13392 → 8722 → 8836 → SnapshotCamera (16331)`, performing a full `camera.Clone()`. Net: **4-6 camera clones + gizmo recomputes per touch-move sample**, holding the lock longer than necessary.
**Fix:** Coalesce — `OnCameraChanged` sets a dirty flag and `Post`s a single gizmo refresh; suppress per-property work during an active gesture and do one refresh on `*End`. Never `SnapshotCamera()` inside the per-property handler.

### ⑦ Engine config + visibility filter are process-wide mutable statics — `[High]`
`EdgeSnapService.cs:40-54` exposes all toggles/tolerances and `VisibilityFilter`/`DiagnosticsLog` as `static`, but `AndroidMeasureRaycaster` creates *instance* services. Two live `AndroidMeasureIntegration`s (or overlapping lifetimes) overwrite each other's filter/settings globally (`AndroidMeasureIntegration.cs:65-66,129-134`); the visibility callback then closes over a different scene than the one queried.
**Fix:** Make these instance properties (pass config + visibility delegate into `TrySnap*`), or assert single-instance ownership.

### ⑧ Palm-rejection activating mid-gesture can strand navigation — `[Medium]`
On the tool-consume path (Zoom-Window / 3-point Custom section, `MainActivity.cs:16614`), `OnViewportRawTouch` is bypassed, so the only suppression is `AndroidPointerSource.cs:634-645` self-activating **without** emitting `Cancel`. A finger already in Orbit/PanZoom has its moves filtered and its `Up` ignored, leaving the recognizer latched and `InteractiveNavigationActive` stuck. With S-Pen palm rejection on, a stylus touch mid-drag freezes navigation until a full new touch sequence.
**Fix:** When `_suppressFingerPointers` transitions false→true inside the source, emit `_recognizer.Cancel(time)` (mirroring `CancelActiveGesture`).

### ⑨ Settings rebuild on every resume orphans open color-picker dialogs — `[Medium]`
`PreferencesBottomSheet.cs:72-77` (`OnStart` → `RefreshVisibleSettingsView`) rebuilds the entire settings view on first show **and** every resume, calling `DisposeLogFeed()` but **not** `DismissColorPickerDialogs()` (`:379-405`). An open color dialog leaks its (now-detached) view tree and its "OK" updates a stale swatch. The full rebuild on every resume is also wasteful (dozens of Views + SharedPreferences reads).
**Fix:** Call `DismissColorPickerDialogs()` before rebuild; gate the rebuild so it runs only when needed (e.g. after a reset).

### ⑩ Native bounds checks overflow `int32`; no ceiling on Draco-declared counts — `[Medium]`
`draco_native.cpp:74` (`out_count < num_points() * components_expected`) and `:91` (`out_count < face_count * 3`) compute required size in 32-bit arithmetic — above ~715M points the product overflows negative, the guard is skipped, and the loop writes out of bounds. Today this is masked only by `DracoNativeDecoder.cs:94-98` throwing first, so the native function is independently memory-unsafe. Separately, `NumPoints`/`NumFaces` feed `new float[total]` bounded only by `int.MaxValue` (≈8 GiB), so a crafted small file can trigger a multi-GB allocation (DoS).
**Fix:** Compute required count in `int64_t`; clamp `NumPoints`/`NumFaces` to a sane import ceiling and throw `InvalidDataException` above it.

---

## 4. P2 — Performance (90 fps target)

| Sev | Finding | Location | Fix |
|---|---|---|---|
| High | Outline pass allocates `HashSet`+`List` **every frame** a body is selected/hovered | `GlesOutlineRenderer.cs:156-170` | Cache & `Clear()` scratch collections |
| Med | Axis-triad does 4× `glGetInteger` blend queries/frame (sync stall on Adreno/Mali) | `GlesAxisTriadOverlay.cs:107-110` | Set own blend func; rely on caller's `ResetMainFramebufferState` |
| Med | Per-touch-move array allocations (× historical samples) + pinch LINQ | `ViewportTouchGestureRecognizer.cs:163-176,346`; `AndroidPointerSource.cs:761` | Reusable single-element/scratch buffers; manual two-lowest-id scan |
| Med | Supplemental snap allocates + sorts a candidate list every hover; visibility filter does a 2nd raycast per candidate | `AndroidMeasureRaycaster.cs:366-385` | Pooled buffers; bounded top-K; cache per-hover visibility raycast |
| Med | BOM header cells recreated + re-subscribe `Click` on every filter apply / layout change | `AndroidBomPanel.cs:355-375` | Update cells in place; reusable per-column click listener |

---

## 5. P3 — Structural / maintainability (strategic)

### ⑪ `MainActivity.cs` is a 16,809-line God class — `[High]`
One non-partial `sealed` class: **~470 members, ~200 instance fields (L119-408), 33 nested types, zero `#region`s**, owning ≥12 concerns — Activity lifecycle, hand-rolled DI (`OnCreate` ~217 lines, L450), chrome binding (`BindBottomToolbar` ~205 lines, L5116), the toolbar/modal-tool state machine, gesture routing (3 subscribers), section + body-move gizmo drag math (L9333-10237), measurement/annotation overlays (L13412-14600), the full cloud auth/session/save subsystem (~2,400 lines, L1366-3800), QR, explode, settings, save/restore. Longest methods 167-317 lines. This is the #1 maintainability liability and the root cause of ⑫/⑬.
**Fix:** Extract cohesive controllers behind interfaces — `CloudSessionController`, `ToolbarStateController`, `GizmoInteractionController`, `MeasurementOverlayController`, `ImportController`, `QrController` — each owning its own fields; reduce the Activity to a composition root + lifecycle forwarder. Interim: split into `partial` files by concern.

### ⑫ Orchestration logic is structurally untestable; the test project hand-links ~70 source files — `[High]`
Because all controller logic lives in an `AppCompatActivity` (can't instantiate off-device), `...Tests.csproj:27-204` re-`<Compile Include>`s individual `.cs` files to recreate a testable subset, with comments admitting deferred dependencies. Every new cross-file dependency silently breaks this list; pure orchestration helpers in MainActivity have no test path.
**Fix:** Move pure static helpers into plain non-Activity classes the test project can `ProjectReference`; longer term the controller extraction (⑪) lets you reference the app assembly's logic directly.

### ⑬ ~200 fields of shared mutable state coordinated by ~32 ad-hoc re-entrancy flags — `[High]`
Every subsystem's state is a bare field on the Activity, mutated from dozens of methods. Re-entrancy is handled by ~32 boolean `*Updating`/`*InProgress` sentinels set/reset in matched `try/finally`s. Mode is tracked redundantly across `_bottomToolbarMode` / `_activeModalTool` / `_activeSectionSubMode` / `_isInFixedView` / `_leftToolPanelKind`; correctness depends on every site keeping a large implicit invariant in sync.
**Fix:** Group related fields into small state objects owned by the extracted controllers; model mutually-exclusive modes as one enum/state machine; replace `*Updating` bools with a reusable `using var _ = SuppressEvents();` scope (generalize the existing `BomDirtySuppressor`).

### Other structural (Medium)
- **Gesture fan-out coupling:** one `GestureRecognized` event has three handlers attached in a fixed order (`MainActivity.cs:582`); none can suppress the others, so cross-handler swallowing is patched with comments instead of routing. → chain-of-responsibility `bool TryHandle(ev)`.
- **Shim/reused-source fragility:** `Core.Android.csproj` globs the entire desktop `Core/**/*.cs` then subtracts Windows-incompatible files via an **opt-out** `<CoreExclusions>` list — any new desktop file using a Windows API is included by default and fails only at Android build/runtime. → opt-in linking or a banned-namespace build check.
- **Silent failure handling:** undo failures (`OnUndoFailed`, L734) and context-loss reload failures (L15850) only log a warning — no user feedback; `OnCreate`'s 217-line body has no `try`, so a subsystem-init throw is an unrecoverable launch crash. → surface failures via the existing `ShowError`; isolate `OnCreate` init.

---

## 6. P4 — Build & config hygiene

- **`[High]` NDK not pinned** — `build-libdraco.ps1:44-48` builds the native `.so` against the newest NDK present (`Sort-Object Name -Descending | Select -First 1`). Two machines → different binaries; `global.json` pins the .NET SDK but not the NDK. → pin `26.3.11579264` as the script default.
- **`[High]` `TreatWarningsAsErrors=false` + no analyzers** (`Directory.Build.props:8`) despite `Nullable=enable`; `CA1416` blanket-suppressed solution-wide. Nullable/obsolete regressions accumulate as ignorable warnings (there are already `#pragma warning disable` sites). → enable `EnableNETAnalyzers` (`AnalysisLevel=latest-minimum`), promote nullable `CSxxxx` to errors.
- **`[High]` Renderer "tests" are 40 source-text grep assertions** (`GlesRendererSourceGuards.cs`) — pass on wrong logic, fail on benign renames, inflate the apparent count (~14% of facts). → extract pure decision logic (sample-fallback order, depth-format selection, section-cap filtering) to host-testable methods (mirror the `MsaaSceneFramebuffer.ClampSamples` split); device smoke for the GL-bound remainder.
- **`[Medium]` `.sln` hand-authored** with sequential placeholder GUIDs and manual `Build.0` blocks (8 projects × 4 lines) — a collision or mis-edit silently drops a project. → manage via `dotnet sln add/remove`.
- **`[Medium]` Host test project diverges from the real app** by design (links a curated file subset; overrides TFM to `net8.0`) — coverage rots as the app evolves and the green host run never built the Android app. → factor host-testable logic into a shared `net8.0`/`netstandard2.0` library both reference.
- **`[Low]` `deps/` is fully git-ignored** yet the build needs vendored Draco source + prebuilt `.so` there; the exact upstream commit isn't recorded. → pin Draco as a git submodule.
- **`[Low]` Static-mutable test config** (`EdgeSnapService`, `AndroidCameraClipPlanes`) is fenced today by a non-parallel `[Collection]`, but any new snap/clip test that forgets the attribute will flake. **`[Low]` Timing/allocation perf-guard tests** (`EdgeSnapPerfGuardTests`, `EdgeSnapAllocationTests`) are inherently nondeterministic on CI — mark `[Trait("Category","Perf")]` and run nightly.

*Dependency pinning is otherwise exemplary:* every NuGet ref is exact-versioned, SDK pinned via `global.json`, Draco pinned to 1.5.7, `NuGet.config` cleared to nuget.org only.

---

## 7. P5 — Security hardening (posture is good)

The security review found **no hardcoded secrets, no TLS-validation bypass, no QR/intent/SSRF injection path, no zip-slip, and a minimal manifest** (INTERNET + CAMERA only, camera `required=false`, `allowBackup=false`, no `debuggable`, no deep links, only the standard exported LAUNCHER activity). Tokens are AES-256-GCM in AndroidKeyStore (`SetRandomizedEncryptionRequired(true)`, per-value IV); downloads are SHA-256-verified against server metadata; the bearer token is bound to the session origin via `ValidateAuthorizedRequestUri` before every request (defeating token-leak/SSRF via a malicious server-returned absolute URL); unknown-size downloads are capped at 2 GiB with free-space checks.

- **`[Medium]` App-wide cleartext HTTP for LAN servers.** `network_security_config_release.xml:13` permits cleartext globally; the only restriction is `CloudApiClient.cs:1602-1666`, which treats **any single-label hostname** (and `*.local`) as "private/LAN". With a Local `http://` profile, sign-in password, bearer/refresh tokens, TOTP codes, and `.fa` downloads travel in cleartext — interceptable on a hostile/shared Wi-Fi. The server URL is operator-configured (not attacker-injectable), so this is exposure, not injection.
  **Fix:** In-UI warning whenever a cleartext profile is active (not just a DEBUG logcat line); consider requiring an IP-literal or `*.local` host and dropping the bare single-label allowance; prefer HTTPS-with-private-CA.
- **`[Low]` Server-controlled `package_id` is concatenated into a cache path** without `SanitizePathPart` (`CloudApiClient.cs:1505-1511,1519-1523`) — path traversal limited to app-private storage, reachable only from the trusted backend. → run it through the existing `SanitizePathPart`.
- **`[Low]` Raw QR payload / decoded part number logged verbatim** to logcat (`AndroidQrScannerDialog.cs:547-549`; `MainActivity.cs:8070-8072`). Low-sensitivity but untrusted input. → log length + outcome only.

---

## 8. Cross-cutting themes

1. **One monolith, many clean satellites.** The team clearly *can* write small, single-responsibility units (`StyledTooltipController`, `AndroidViewportExplodeView`, the measurement engine). The problem is concentrated almost entirely in `MainActivity`. Decomposition is the highest-leverage investment.
2. **Allocations on hot paths.** The allocate-per-frame / per-touch-move anti-pattern recurs in rendering, gestures, snapping, and BOM — a single "reuse scratch buffers" sweep addresses most of §4.
3. **Process-wide mutable statics** (`EdgeSnapService` config, snap visibility filter) create both correctness cross-talk (⑦) and a standing test-flakiness hazard.
4. **The safety net lags the code.** Sophisticated code (native interop, GL, threading) is guarded by string-grep tests and no CI — the riskiest code has the weakest gate.

## 9. What's genuinely strong (keep doing this)

- **Camera thread-safety:** every mutation under `lock(_camera)`; GL thread reads only via `SnapshotCamera()` cloning under the same lock — no torn reads. Render scheduling is lock-free (`Interlocked`/`Volatile`/`ConcurrentQueue`).
- **GPU resource discipline:** every owning type pairs `Gen*`/`Delete*`, rolls back partial construction on throw, validates FBO completeness, drains `glGetError`, and correctly handles EGL context loss/resume (`RecoverIfContextResourcesLost` cheaply probes `IsProgram`).
- **Native memory safety:** single-owner opaque handle, idempotent double-checked free, all calls (incl. finalizer) serialized through one `NativeGate` lock, minimal `fixed` pinning — no double-free/use-after-free exposure. Typed failure translation for a missing/incompatible `.so`.
- **Security defaults:** KeyStore-backed token encryption, origin-bound bearer, SHA-256 download verification, size caps, a clean QR trust boundary, `.fa` entries read by fixed manifest names (no `ExtractToDirectory`).
- **Measurement engine:** double precision throughout, scale-relative epsilons, local-ray re-normalization for correct picking under non-uniform scale, a correct median-split BVH with iterative-stack overflow safety, and triangulation-correct area with proper squared-unit conversion — exhaustively tested with realistic fixtures (arcs, welds, noisy circles).
- **Test discipline:** ~234 of ~277 facts are genuine behavioral tests with real edge cases (zero-byte/garbage archives, NaN/overflow clamps, NTFS-reserved-name rejection), injected clocks, per-test GUID temp dirs, and `finally`-based global-state restoration.

---

## 10. Suggested sequencing

1. **This week:** stand up CI (`verify.ps1` on PR) ①; fix the `PopupWindow` leak ④, gizmo-jank coalescing ⑥, palm-rejection cancel ⑧ — all small, high-value.
2. **Next:** Draco generic-attribute copy ③ + native `int64` bounds/clamp ⑩ (pair with the round-trip test ②); cache-versioning for raycast/snap ⑤; make `EdgeSnapService` config instance-scoped ⑦.
3. **Sweep:** the §4 allocation-reuse pass; enable analyzers + pin the NDK (§6).
4. **Strategic (allocate real time):** carve `MainActivity` into controllers ⑪⑫⑬ — unblocks testing of everything else.

---

## Appendix — method & caveats

- Reviewed **statically**; the suite was not built or run this pass (the repo needs the documented build-lock workarounds, and the request was for a report). Run `tools/verify.ps1` for verified pass/fail status.
- Several issues from prior reviews appear **already fixed** in the current tree (tree/BOM listener leak, panel-expansion leak, the earlier double-tap popup leak, SSAA buffer restore, the context-loss reload path) — confirmed in-code during this pass.
- Severity scale: **Critical** (crash/data-loss/security/corruption or guaranteed-wrong in normal use), **High** (real common-path bug, serious leak, significant perf/security issue), **Medium** (edge-case bug, moderate hazard), **Low** (minor), **Info** (observation).
