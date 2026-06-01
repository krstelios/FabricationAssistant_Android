# Android Measurement Snapping — Re-validation & Marker/Selection Consistency

Date: 2026-06-01
Status: Design (awaiting review)
Author: pairing session (investigation + design)

## 1. Context

A task brief asked to "review, optimize, and improve" the edge/point snapping used by
the Point-to-Point and Face-to-Point measurement tools, asserting that snapping is
brute-force, slow, scans the whole model per hover, and needs a new real-time strategy.

A grounded investigation (reading the actual Android source + a micro-benchmark built
and run against the real `EdgeSnapService`) found the premise does **not** match the
code. The system already implements the brief's entire "recommended strategy" list:

- Precomputed snap candidates per immutable edge buffer (`ConditionalWeakTable`
  cache; `EdgeSnapService.cs:33,67`).
- Straight-edge welding (`VertexWelder`), segmented-arc detection (least-squares
  circle fit + sweep/plane/radial validation), mixed/closed-loop/fallback runs.
- Per-mesh BVH raycast acceleration (`AndroidMeshRaycastAcceleration`), cached and
  version-invalidated.
- Visibility filtering (section clip + occlusion raycast; `EdgeSnapService.VisibilityFilter`).
- Background warmup (`AndroidMeasureIntegration.BeginSnapWarmup`) + a prepared-only
  hover path (`TrySnapPrepared`) that never builds on the hot path.
- Endpoint + Midpoint snap toggles already present in the measurement toolbar and
  wired through `AppSettings` → `ApplySettings` → `EdgeSnapService`.
- Section curves (welded `EdgePositions`) and the section cap face already integrated
  into the same snap/raycast model.

### Measured performance (desktop x64; ARM ~3–8× slower per op)

Per-hover engine scan (`TrySnapPrepared`, single hit mesh):

| Model | Build once | Hover scan | Alloc/scan |
|---|---|---|---|
| welded 1k seg | 2.7 ms | 19 µs | 64 B |
| welded 100k seg | 37 ms | 133 µs | 64 B |
| faceted cylinder 6.4k (realistic) | 4.9 ms | 28 µs | 64 B |
| dense non-weldable 100k (pathological) | 104 ms | 1600 µs | 64 B |

Conclusion: hover snapping is microsecond-scale for realistic models and only scans the
mesh under the cursor. Build is the only expensive step and is already off the hot path
(background warmup + prepared-only hover). **No rewrite or new strategy is warranted.**

## 2. Genuine gaps found

- **A — Preview ≠ selection (reliability):** hover uses `allowBuild:false` + no
  supplemental; click uses `allowBuild:true` + supplemental. A click can therefore
  commit a point the red marker never previewed (un-warmed/large mesh, or a
  supplemental/section edge). This is the "not always reliable" symptom and violates
  the brief's req #7/#12. (`MeshMeasurePicker.cs:71,85,88,184,333`)
- **B — Unthrottled hover + multiple occlusion raycasts** on the UI thread. The
  visibility filter raycasts once per *improving* candidate inside `TrySnapTargets`;
  hover runs once per delivered hover event with no per-frame coalescing.
- **C — Per-hover string allocation when diagnostics are off**
  (`$"edgeBuffer={…}"` built every scan even when `DiagnosticsLog == null`;
  `EdgeSnapService.cs:99`). The 64 B/op above.
- **D — Click hitch on large/un-warmed meshes** (warmup skips meshes > 10k segments
  and has a 2.5 s budget; first click then builds synchronously on the UI thread).
- **E — No selection hysteresis** → flicker between near-equidistant candidates.
- **F — No per-mesh target cap** for the pathological dense-mesh scan.

## 3. Goals / Non-goals

**Goals**
1. Treat the brief's sections 11 (validation) and 12 (acceptance criteria) as a formal
   acceptance contract: map every scenario to an automated test, an existing test, or a
   documented manual on-device check; surface real failures.
2. Make the red marker the single source of truth (gap A) via the **commit-only-what-
   was-previewed** approach: hover and click resolve the snap point through one shared,
   prepared-only routine with one shared candidate set.
3. Add device-representative perf-regression tests so the acceptance criteria
   ("close to 60 FPS", "no GC spikes") are guarded mechanically.
4. Fix only the gaps that validation proves matter; C is fixed unconditionally (clear waste).

**Non-goals**
- No rewrite of the snap engine, BVH, welding, arc detection, or section pipeline.
- No new snap strategy. No desktop-implementation assumptions.
- No new measurement tools or UI redesign beyond what validation requires.

## 4. Design

### 4.1 Unified snap resolution (gap A — commit only what was previewed)

Introduce a single resolution routine used identically by hover and click, e.g.
`MeshMeasurePicker.ResolveSnapPoint(rayOrigin, rayDir, allowBuild: false)` returning the
snapped world point (or null). Both `TryHoverSnap` and `Pick(PickKind.Point)` call it.

- **Prepared-only**: `allowBuild` is `false` on both paths. Click no longer builds snap
  models synchronously on the UI thread. If the warmup has not yet prepared a mesh,
  neither hover nor click snaps to it → no marker, no pick (consistent by construction).
- **One shared candidate set.** To avoid regressing section snapping (req #6), the shared
  set is the union both paths already need: primary hit-mesh prepared targets **+**
  prepared supplemental edges **+** prepared section curves. Hover is upgraded to include
  supplemental + section (prepared, bounded) so the preview shows everything a click can
  commit. The supplemental prepared path is already bounded (24-candidate cap / 48 ms
  budget in `AndroidMeasureRaycaster`).
- **Result:** the committed point is, by construction, exactly the previewed marker point.
  Face-to-Point reuses the same routine for its point step, so both tools stay consistent
  (req #7/#12, no duplicated logic — req #8).

Trade-off (accepted): a mesh the warmup skipped (> 10k segments) is not snappable until
warmed. Mitigation considered in §4.4.

### 4.2 Validation matrix (the acceptance contract)

Every brief §11/§12 item is assigned a validation method. Automated where the net8.0
test host can reach it (the `EdgeSnapService` + Math + `AndroidSectionClipper` +
`SectionCapGeometry` + `SectionPlane` are linked into the test project today); otherwise
an explicit manual on-device checklist item.

Basic geometry (single edge, welded run, rectangle, dense) — **automated** (extends the
33 existing `AndroidEdgeSnapServiceTests`). Arc geometry (clean/partial arc, arc+straight,
non-arc chain, tiny segments) — **automated** (existing + new). Visibility (front/back,
behind-face, occluded, hidden/isolated body, sectioned) — **mixed**: clip + filter logic
automated via `EdgeSnapService.VisibilityFilter` and `AndroidSectionClipper`; full
occlusion-raycast behaviour is **manual on-device** (BVH lives in the android-only
assembly). Tools (P2P / F2P preview==final) — **automated** at the engine seam (one
resolution routine ⇒ equality by construction) + **manual** end-to-end. Performance —
**automated** perf-regression (§4.3) + **manual** on-device frame check.

The matrix is delivered as a checklist in the spec/plan and as test names; each row is
either green (existing/new test) or a tracked manual item.

### 4.3 Perf-regression tests

Add guarded tests against the real `EdgeSnapService`:

- Build time and scan time vs segment count stay within thresholds (generous, set from
  the measured desktop baseline with headroom; documented as desktop, not device).
- Scan allocation is ~0 B/op after gap C is fixed (asserts no per-hover string alloc).
- A documented device-representative budget note: hover scan must be a small fraction of
  the 16.6 ms frame; the dominant cost is raycasts, validated manually on-device.

### 4.4 Conditional fixes (only if validation flags them)

- **C (do unconditionally):** skip building the diagnostics source string when
  `DiagnosticsLog == null` (and in `AndroidMeasureRaycaster` log helpers) — removes the
  per-hover allocation. Tiny, safe, measurable.
- **B (if perf check flags it):** coalesce measure hover to ≤ 1 computation per frame,
  and defer the occlusion raycast so only the finally-chosen candidate is verified
  (O(k) → O(1) raycasts), falling back to next-best only if the chosen one is occluded.
- **D (if click-latency check flags it):** make the warmup cover snappable meshes more
  completely (raise/remove the 10k cap or warm-on-demand in the background) so the
  commit-only-previewed model rarely leaves a mesh un-snappable; never build on the UI
  thread during a click.
- **E (if flicker reproduces in tests):** add light hysteresis so the previously chosen
  target wins ties within a small margin.
- **F (if a real model hits it):** cap or spatially bucket per-mesh targets.

## 5. Risk assessment

- **Behavioural change from gap A:** click stops snapping to un-warmed meshes. Risk:
  user taps a freshly loaded large mesh before warmup completes and gets no snap.
  Mitigation: §4.4 D + the existing warmup-on-mode-enter; covered by a manual checklist
  item. This is the explicitly chosen trade-off.
- **Hover doing supplemental+section now:** more per-hover work than today. Mitigation:
  prepared-only + existing supplemental budget/caps; guarded by the perf-regression test.
- **Tests that mutate `EdgeSnapService` static flags:** already a pattern in the suite
  (reset in ctor); new tests must reset statics to avoid cross-test bleed.
- **Desktop vs ARM numbers:** perf thresholds are desktop baselines with headroom; device
  validation stays manual. No threshold may silently cap coverage without a logged note.

## 6. Out of scope / follow-ups

- Moving the whole hover pipeline off the UI thread (only pursued if §4.4 B is
  insufficient on-device).
- Any change to the section geometry algorithm itself.
