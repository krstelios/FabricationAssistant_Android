# Diameter / Arc (Circular-Feature) Dimension Tool — End-to-End Redesign

- **Date:** 2026-06-06
- **Status:** Design — awaiting user review
- **Author:** skritikos + Claude (Opus 4.8)
- **Scope:** The "CircularFeature" measurement mode (diameter / radius / arc) on Android, and the shared `FabricationAssistant.Core` measurement engine it drives.
- **Evidence base:** Static review of 7 subsystems (35-agent multi-agent pass, 11/27 critical-high defects adversarially confirmed) cross-checked against live device traces (`adb logcat -s FA.MeasureCircular`) reproducing fillet-miss, face-leak, and garbage-value commits on a Samsung `R52Y80CE37L`.

---

## 1. Problem statement

The diameter/arc tool reliably measures **through-holes** (full-revolution features) but fails on **fillets/rounds** and **leaks detection into irrelevant faces**. Device logs confirm three concrete failures on a real model:

| Tap | Target | Outcome | Log evidence |
|---|---|---|---|
| 1 | fillet (mesh 551, tri 214) | committed **R=0.0421**, but 4 fitters disagreed 2–3× | projected `0.042` / robust `0.097` / refined `0.032` / component `0.042`; primary `refinedCircleFitDiverged Δ=0.666` |
| 2 | fillet (mesh 430, tri 345) | **MISS (null)** | `smooth=446`/775 tris flooded; fits collapsed to R≈`0.0021`; `strictTrimTooSmallAfterRobustFit` → `noCircularPatch` |
| 3 | fillet (mesh 551, tri 401) | **FALSE success R=0.004** | projected said `0.0466`, robust collapsed to `0.004` at a corner vertex-fan, committed |
| ctrl | hole (mesh 349, tri 229) | clean **Ø, R=0.0022** | `coverageDeg=345.2`, `rms=0.00001`, region stable `57→57` |

The predictor of success is normal **spread**: holes give `spread≈0.99` (normals fan a full 360° around the axis); fillets give `spread≈0.17–0.69` (normals fan one-sided). The engine is architected for full-revolution features; it is structurally unable to handle partial-coverage features.

This is an **architecture mismatch, not a tuning problem**.

---

## 2. Goals & non-goals

### Goals
- Detect and dimension, reliably and stably: **holes/bores (Ø)**, **bosses/shafts/pins (Ø)**, **fillets/rounds (R)** on both straight (cylindrical) and curved (toroidal) edges, and **chamfers (width × angle)**.
- **No leaking** across feature edges into unrelated faces.
- **No garbage commits** — a low-confidence fit is rejected with explicit feedback, never silently committed.
- **Live hover preview** (<100 ms) showing the recognized feature + provisional value + confidence; **tap to commit**; tap again to cycle candidates.
- **Stable values** — the same feature returns the same value regardless of which triangle on it you tap.
- **Professional dimension drawing** (Ø glyph, arrowheads, center mark, radius leader, true-coverage arc, confidence styling), ISO 129/3040-leaning, standard configurable later.

### Non-goals
- No B-Rep / parametric reconstruction. This is a **mesh-only** tool; the reported radius is explicitly the mesh-approximate value, with fit residual available.
- No change to the other measurement modes (point-to-point, face, bounding box, area) beyond shared infrastructure (topology cache, annotation primitives).
- No new import-time geometry; we consume the existing tessellated `MeshDto`.

### Success criteria
1. Each of the three failing taps above (and equivalents) produces a **correct, stable** R/Ø or an **explicit "no clean feature" rejection** — never a wrong silent value.
2. A fillet tapped anywhere along its band yields the same R within tolerance.
3. The recognized region never includes triangles across a visible (≥28°) edge.
4. Hover preview budget < 100 ms on the test tablet for meshes up to ~100k triangles (after one-time precompute).
5. New parametric test matrix passes on host + an Android-suite mirror.

---

## 3. Locked design decisions

| Decision | Choice |
|---|---|
| Feature scope | Holes/bores (Ø), fillets/rounds (R), bosses/shafts (Ø), chamfers (width × angle) |
| Detection model | **Hybrid** — closed circular **edge-loop** fit for holes/bosses/chamfer rims (exact Ø); **surface rolling-ball** fit for fillets/rounds (R) |
| Fillet geometry | **Cylinder** (straight edge) **+ torus** (curved edge) |
| Interaction | Live **hover-preview** (ghost + provisional Ø/R + confidence) → **tap to commit** → tap again to cycle candidates |
| Correctness | **Strict confidence gate**; reject with feedback; **never commit garbage** |
| Performance | **<100 ms** coarse fit on hover; full robust fit on commit; topology/curvature precomputed once on mesh load |
| Rendering | **Full professional** callouts, ISO-leaning style, standard configurable later |
| Rollout | **All at once** — one body of work delivering detection + torus + edge-loop + chamfer + rendering together (internal ordering in §16) |

---

## 4. Confirmed diagnosis (the review)

All references are to `FabricationAssistant.Core/Measurement/Engine/CircularFeatureDetectionService.cs` unless noted. 🔴 = adversarially confirmed critical; 🟠 = confirmed high.

### 4.1 Face-leak
- 🔴 **Parent-relative flood, no global cap** — `GrowSmoothRegion` (`:371-401`, esp. `:391`) BFS-accepts a neighbor if `abs(dot(parentNormal, neighborNormal)) ≥ cos(35°)`. Drift accumulates across the surface; the front crosses onto the adjacent plane wherever the bend is <35° (every fillet end). *Cause of `smooth=239/446`.*
- 🔴 **No crease/boundary barrier** — `BuildAdjacency` (`:344-369`) records every shared edge with no crease flag; the grower never consults feature edges. The renderer draws creases at 28° but the grower crosses them freely.
- 🟠 **`abs()` merges antiparallel normals** (`:391`) — `abs(-1) ≥ 0.8192`, so thin walls / opposite fillet flanks merge. (Same defect in `FaceDetectionService.cs:111`.)
- 🟠 **Mean-based acceptance masks contamination** — `PassesNormalValidation` (`:1369-1402`) tests the region **mean** radial/axis alignment; leaked planar triangles are outvoted by the curved majority.
- 🟠 **Leaked region poisons axis/origin** — origin (`:466-480`), PCA axis (`:1038-1075`), and projected-circle search are all derived from the (leaked) region; planar normals dominate the covariance → arbitrary axis. `normalSpreadRatio` is computed (`:1072`) but never gates.
- 🟠 **Whole-mesh indices, no spatial window, no size cap** — `MeshMeasurePicker.cs:176-185` (pick) / `:367-376` (hover) pass the entire index buffer; the BFS can flood an entire connected part and merge coaxial features.
- 🟠 **Verbatim radius, relative-only tolerances** — `MeasurementBuilders.cs:171-172` trusts `feature.Radius` with no upper bound; the detector's only checks are relative (`RMS ≤ 0.20·r`), so a big leaked circle gets a proportionally big tolerance and passes.
- 🟠 **Android `preferredNode` override** — `AndroidMeasureRaycaster.cs:151-175` short-circuits nearest-surface ordering, so the seed can come from the wrong body.

### 4.2 Fillet-miss
- 🔴 **Fixed 35° dihedral too coarse for small/coarse fillets** — a tight fillet turns >35° per facet → region <4 → `smoothRegionTooSmall` (`:11,391,47`). *Cause of `coverageTooSmall` / `strictTrimTooSmallAfterRobustFit`.*
- 🟠 **`PassesNormalValidation` mean axis ≤ 0.35** (`:1391-1401`) — a fillet's normals tilt toward the blend; mean axis-dot exceeds 0.35 → rejected.
- 🟠 **Absolute `MinimumPatchTriangles=4` floor** (`:14,47,481,543,…`) and `robust fit ≥ 12` inliers reject legitimately small fillets.
- 🟠 **`IsCylindricalTriangle` requires all 3 vertices in tolerance** (`:970-978`) — chord deviation at vertices fragments the component.
- 🟠 **sceneDiagonal-scaled tolerances collapse for small fillets** (`:989-994,…`).
- 🟠 **Coverage = `Tau − maxGap` on raw triangle vertices, reject <20°** (`:12,298-303,1439-1462`) — sparse fillets read low coverage.
- **Frozen axis never re-fit** (`:1038-1075`) — ill-conditioned for partial coverage; biases plane → divergent/degenerate fits.
- **Seed = globally nearest pierced triangle, no curvature bias** — a tap a few px off lands on the adjacent flat face.
- **`CurvedFallbackSeeds` reuses the same flawed region/axis** (`:403-432`) and requires the patch to still contain the original seed → inherits the failure (your `patchDoesNotContainOriginalSeed`).

### 4.3 Value / UX / presentation (other confirmed issues)
- **315° closed cut, no hysteresis** (`:13,305`) — a hole losing a sextant to occlusion is dimensioned as **radius** (half the correct value).
- **Robust fit bypasses the divergence guard** (`:162-168`).
- **Unconditional commit + silent dead-tap** — `MeasurementSession.cs:260,385-400` commits any pick and no-ops every reject with no feedback.
- **Presentation is a debug overlay** — `MeasurementPresenter.cs:162-218`: ASCII `"DIA"` not `Ø`; no arrowheads/center-mark/leader; **48-seg full 360° circle drawn even for open radii**; one flat color/2px; depth-off billboard text bleeds through geometry.
- **Per-hover full rebuild** — world-vertex array + TriangleData + adjacency + weld every frame (`MeshMeasurePicker.cs:367-376`).
- **`FeatureEdgeExtractor` keys edges by raw (unwelded) vertex index** (`FeatureEdgeExtractor.cs:120-142`) — boundary-edge spam on glTF unshared-index meshes.
- **Near-zero detection test coverage** — happy-path host tests only; zero CircularFeature detection tests in the Android suite.

---

## 5. Target architecture

```
TAP / HOVER
  │
  ▼
SeedResolver ── screen-space aperture cone, front-face only, curvature-biased.
  │             GPU id-buffer pick is a RE-VALIDATED HINT, never an override.
  ▼
MeshTopologyCache.For(mesh, transformVersion)   ← built once on load, reused
  │   (welded half-edge • per-tri normal/area/centroid • principal curvature k1,k2
  │    • per-edge class: smooth | crease(≥28°) | boundary | non-manifold)
  ▼
┌─ is the seed on / adjacent to a CLOSED circular crease loop? ─┐
│  yes → EdgeLoopFitter ─────────────► circle on rim loop ──────┼─► HOLE / BOSS  (Ø)
│  is the seed on a narrow planar/conical band between 2 creases?│
│  yes → ChamferDetector ────────────► band width + angle ──────┼─► CHAMFER (w×θ)
│  else → SurfaceRegionGrower ─► AnalyticSurfaceFitter ──────────┼─► FILLET / ROUND (R)
│          (curvature-continuous,    (cylinder 5-DOF / torus,    │
│           crease-bounded, grows     RANSAC + Gauss-Newton,     │
│           vs running fit)           axis re-optimized w/ r)    │
└───────────────────────────────────────────────────────────────┘
  │
  ▼
FitAcceptanceGate ── RMS/R + per-triangle inlier-fraction + radius sanity bound.
  │                  Below threshold → REJECT (no commit).
  ▼
FeatureClassifier ── closed crease loop ⇒ Ø ; open strip between 2 creases ⇒ R ;
  │                  conical/planar band ⇒ chamfer.  (boundary topology + hysteresis)
  ▼
PreviewModel (ghost + provisional value + confidence)  ──tap──►  CommitGuard
  │                                                                 │ validate center∈patch,
  │                                                                 │ radius ≤ body bound,
  │                                                                 │ recompute value from fit
  ▼                                                                 ▼
ProfessionalDimensionRenderer  ◄──── DimensionAnnotationModel ◄──── Measurement
(Ø/R, arrowheads, center mark, leader, true arc, confidence dashed)
```

### Component breakdown

| Component | Responsibility | Replaces | Kills (root cause) |
|---|---|---|---|
| **MeshTopologyCache** | Per-(mesh, transform-version) welded half-edge topology + T-junction linking, per-tri normal/area/centroid, per-tri principal curvature. Built once, shared by grower, edge classifier, renderer. | per-pick `BuildTriangles`+`BuildAdjacency`+`Weld`; per-hover `ToWorldVector3Array` | perf rebuild; weld-bridging leak #9 |
| **FeatureEdgeClassifier** | Classify every welded edge once as smooth/crease(28°)/boundary/non-manifold; expose per-edge passability as a hard barrier. | render-only `FeatureEdgeExtractor` (raw-index) | leak #1, #2 |
| **SeedResolver** | Screen-space aperture cone, front-face culling, curvature/feature-type bias; GPU pick is a re-validated hint; snap seed into nearest curved band/rim. | nearest double-sided ray + `preferredNode` override | fillet-miss #8/#10/#11; leak #8; far-wall #11 |
| **EdgeLoopFitter** | Detect the closed crease/boundary loop nearest the seed; fit a plane+circle to loop vertices → exact Ø for holes/bosses/chamfer rims. | (new — current tool infers Ø from surface only) | hole-Ø accuracy; supports classifier |
| **SurfaceRegionGrower** | Curvature-continuous, same-facing (`+cos`, never `abs`), crease-bounded BFS that grows against a maintained running surface fit (deviation from the global model, not the BFS parent), capped to one connected component. | `GrowSmoothRegion` 35° parent flood + `IsCylindricalTriangle` all-3-vertex trim | leak #1/#2/#3/#5/#6; fillet-miss #1 |
| **AnalyticSurfaceFitter** | RANSAC-seeded + Gauss-Newton/IRLS least-squares **cylinder (5-DOF axis+radius)** and **torus** (rolling-ball, fillets) fit; axis re-optimized jointly with radius; reports geometric residual. | frozen eigenvector axis + `TryFindCylindricalComponent`/projected-circle/`TryFitCircleRobust`/`TryFitCircle` + 4 median fallbacks | fillet-miss #7; value instability |
| **ChamferDetector** | Detect a narrow planar/conical band bounded by two creases; report width (cross-band distance) and angle (vs adjacent reference face). | (new) | chamfer scope |
| **FitAcceptanceGate** | Scale-invariant acceptance: `RMS/R` + per-triangle inlier fraction ≥ ~90%, tolerances from fitted R + local tessellation chord (no sceneDiagonal), absolute radius ≤ ½ body bound. | `RadiusCappedTolerance` diag gates, region-mean `PassesNormalValidation`, absolute count floors, relative-only RMS | fillet-miss #3/#4/#5; leak #4/#7; garbage |
| **FeatureClassifier** | Closed(Ø) vs open(R) vs chamfer from boundary topology (closed crease loop ⇒ Ø; open strip between 2 tangent creases ⇒ fillet R; band ⇒ chamfer) + hysteresis on cross-section angular extent. | `isClosed = coverage ≥ Tau·0.875` on raw verts | value halving; leak #10 |
| **PickFeedbackAndCommitGuard** | Distinguish "no recognizable feature" from "missed model"; validate center∈patch & radius≤bound; recompute/cross-check value from the fitted surface before commit. | silent Miss collapse + unconditional `AdvanceCircularFeature` + verbatim `BuildCircularFeature` | dead-tap UX; garbage commit |
| **DimensionAnnotationModel** | First-class arrowhead/terminator, leader/jog, center-mark primitives + a fit-confidence flag; arc at actual coverage/startAngle; legible orientation. | bare chord/radial line + 15px dot + 48-seg 360° fallback + ASCII `DIA` | all presentation defects |
| **ProfessionalDimensionRenderer** | Render Ø/R with arrowheads, center cross, distinct thin dimension-line color/weight, haloed on-top text in a gap, dashed/greyed for marginal fits, unit/precision auto-select. | `GlesMeasurementOverlay` uniform 2px depth-off lines + round blob + fixed mm/F2 | presentation defects; "debug overlay" look |
| **CircularFeatureTestMatrix** | Parametric tests: interior holes (inward normals), 315°/20° boundary sweeps, hole-adjacent-hole / end-cap / curved-tangent leak guards, tessellation-density invariance, radial noise, cone/sphere/torus negatives, seed-independence, end-to-end value, Android mirror. | 14 host happy-path tests, zero Android detection coverage | regression safety |

---

## 6. Component specifications

### 6.1 MeshTopologyCache
- **Input:** `MeshDto` (vertices, indices) + a transform-version token.
- **Output:** immutable `MeshTopology { Vertex[] welded; HalfEdge[]; int[] triToHalfEdge; TriData[] (normal, area, centroid); Curvature[] (k1,k2,principalDir); EdgeClass[] }`.
- **Algorithm:** weld vertices on a local-feature-size tolerance (`max(localEdgeLen·1e-3, ε)`, **not** sceneDiagonal); build half-edge with T-junction/partial-edge linking (reuse `FaceDetectionService` welding which already does this); per-triangle normal/area/centroid; per-triangle principal curvature via a least-squares quadric over the 1-ring (used by SeedResolver + grower). Stored **in local mesh space**; the seed ray and bounds are transformed into local space once per pick (avoids re-materializing the whole mesh to world every hover).
- **Caching:** keyed on `(meshId, transformVersion)`; built off-thread on mesh load via the existing `AndroidSnapWarmupRunner` budget; LRU-bounded.
- **Notes:** retires the per-hover `ToWorldVector3Array` full re-alloc.

### 6.2 FeatureEdgeClassifier
- Classifies each welded edge: `boundary` (1 incident tri), `non-manifold` (>2), else `crease` if dihedral ≥ **CreaseAngle (28°, shared with the renderer)**, else `smooth`.
- Exposes `bool CanGrowAcross(halfEdge)` = `smooth` only. Creases and boundaries are **hard walls**.
- Replaces raw-index `FeatureEdgeExtractor`; one welded edge set is shared by grower + renderer so **what you see selected is what's drawn**.

### 6.3 SeedResolver
- Cast a small **aperture cone** (configurable, ~6 px radius) of rays around the tap; gather front-facing hits (cull back faces via Möller-Trumbore determinant sign; for an interior bore accept the inward-facing wall).
- Rank candidate triangles by curvature signature: prefer cylindrical/toroidal (one principal curvature ≈ 0 with one finite, or two finite) over planar (both ≈ 0).
- The GPU id-buffer pick is a **hint** re-validated against the CPU nearest front hit; it never overrides nearest-surface ordering.
- Output: `Seed { triIndex, hitPointLocal, surfaceKind∈{planar,cylindrical,toroidal,conical} }`.

### 6.4 EdgeLoopFitter (holes / bosses / chamfer rims)
- From the seed, walk crease/boundary edges to assemble the **nearest closed loop**.
- Fit a best plane (PCA) then a circle in-plane to the loop vertices (Kasa + one Gauss-Newton refine).
- Output: `Circle { center, axis(=plane normal), radius, isClosed=true, residual }`. Exact Ø matches drawing callouts.
- If no closed loop within reach → defer to SurfaceRegionGrower.

### 6.5 SurfaceRegionGrower
- BFS from the seed over **smooth** edges only (`CanGrowAcross`). A candidate triangle joins iff **all** hold:
  1. does not cross a crease/boundary edge,
  2. **same-facing**: `dot(n_candidate, n_runningFit_at_candidate) ≥ +cos(NormalTol)` (signed — never `abs`),
  3. curvature-continuous: principal curvature sign/magnitude consistent with the running surface model,
  4. radial residual to the **running incremental fit** ≤ `k · chord(R)` (deviation from the global model, not the BFS parent).
- Maintain an incremental cylinder fit; refit every N additions. Cap to one connected component reachable without crossing a feature edge.
- Output: triangle set + seeded fit. No absolute triangle-count floor — small patches are allowed; acceptance is decided later by residual + inlier fraction.

### 6.6 AnalyticSurfaceFitter
- **Cylinder (5-DOF):** axis point (2-DOF on the perpendicular plane) + axis direction (2-DOF) + radius (1-DOF). Seed: RANSAC over pairs of triangle normals on the Gaussian sphere (axis ≈ normal of the great circle they span); refine with Gauss-Newton minimizing Σ(dist_to_axis − r)². Reference: Eberly / Lukács-Marshall-Martin.
- **Torus (rolling-ball, fillets on curved edges):** axis + ring center + ring radius R + tube radius r; **r = fillet radius**. Cylinder is the R→∞ limit. Try cylinder first; if the patch bends (axis poorly constrained, residual high but curvature shows a second finite component) try torus and keep the lower-residual model.
- Uses the covariance eigenvector **only as an initial guess**, gated by `normalSpreadRatio`.
- Output: `SurfaceFit { kind, axis, center, R, r?, geometricRms }`.

### 6.7 ChamferDetector
- Detect a **narrow band** (small cross-band extent) of triangles bounded by two creases, whose surface is planar (straight edge break) or conical (around a hole).
- Width = perpendicular distance between the two bounding crease loops/edges (averaged); Angle = dihedral between the chamfer face and a chosen adjacent reference face (or the part axis for a countersink).
- Output: `Chamfer { width, angleDeg, axis?, residual }`.

### 6.8 FitAcceptanceGate
- Accept iff: `geometricRms / R ≤ RmsOverR (1.5%)` **and** per-triangle inlier fraction `≥ InlierFraction (90%)` (a triangle is an inlier if its centroid radial residual ≤ `k · chord(R)`) **and** `R ≤ MaxRadiusFraction (0.5) · pickedBodyExtent`.
- Below threshold → **reject** with a reason code (no commit). Marginal band (just below accept) → **preview-only dashed**, requires explicit confirm.

### 6.9 FeatureClassifier
- **Ø (closed):** the patch is bounded by a **closed crease loop** around its full circumference (hole/boss), OR cross-section angular extent ≥ **ClosedDeg (300°)** with hysteresis (300–330° sticky).
- **R (open):** open strip between two tangent creases (fillet/round) — always radius.
- **Chamfer:** conical/planar band per ChamferDetector.
- Angular extent measured on **cross-section support points**, not raw triangle vertices.

### 6.10 PickFeedbackAndCommitGuard
- Reject reasons surfaced to UI: `NoFeatureHere` (hit model but no clean feature) vs `MissedModel` (no hit).
- Before commit: assert center lies within the patch's projected extent and `R ≤ MaxRadiusFraction · bodyExtent`; recompute the reported value from the accepted analytic fit (no verbatim pass-through).
- Replaces unconditional `AdvanceCircularFeature` commit + silent Miss.

### 6.11 DimensionAnnotationModel + ProfessionalDimensionRenderer
- New presentation primitives: `Arrowhead(pos, dir, style)`, `Leader(points[], jog)`, `CenterMark(pos, axisU, axisV, size)`, plus a `confidence∈{committed,preview,marginal}` flag on the snapshot.
- **Diameter:** center-mark cross at the axis; dimension line through the center with **double arrowheads** at both rim points; `Ø` (U+00D8) + value in a gap/shoulder; thin dimension-line color distinct from model edges; haloed text on-top.
- **Radius:** single **leader from the arc** with one arrowhead at the arc, `R`+value on a shoulder **outside** the feature; arc drawn at **actual coverage/startAngle** (no 360° phantom; no chord).
- **Chamfer:** leader with `width × angle°` (e.g. `2 × 45°`).
- **Confidence:** marginal/preview fits render dashed/greyed; committed render solid.
- Unit/precision auto-selected from feature size and scene unit (mm vs m thresholds, significant figures), replacing fixed `mm/F2`.
- Style is ISO 129/3040-leaning; a `DimensionStandard` setting (ISO/ASME) is stubbed for later.

---

## 7. Data-model changes
- `CircularFeaturePick` (PickTypes): add `SurfaceKind`, `GeometricRms`, `Confidence`, optional torus `TubeRadius`/`RingRadius`, `RejectReason?`.
- `MeasureToolMode`: unchanged (`CircularFeature` covers Ø/R); **add** a chamfer result kind in `Measurement` (`MeasurementKind.Chamfer` with width+angle) — or model chamfer as its own pick carried in the same mode. (Decision: keep one mode, branch by classifier; `Measurement` gains a chamfer variant.)
- Presentation `Primitives`: add arrowhead/leader/center-mark types + confidence flag.

---

## 8. Interaction & correctness flow
1. **Hover:** SeedResolver (aperture) → topology cache (hit) → grower (coarse, capped) → cylinder-only quick fit → gate (preview threshold) → **ghost** (dashed if marginal) + provisional Ø/R + confidence cue. Budget < 100 ms.
2. **Tap commit:** full grower + RANSAC+GN cylinder/torus (or edge-loop / chamfer branch) → gate (commit threshold) → classifier → CommitGuard → Measurement. If rejected → **`NoFeatureHere` toast/hint**, no measurement.
3. **Cycle:** if multiple candidates qualify under the aperture, repeated taps cycle them (largest-confidence first).

---

## 9. Constants / defaults (scale-invariant; all tunable)
| Name | Default | Replaces |
|---|---|---|
| CreaseAngle (grow barrier) | 28° (shared with renderer) | 35° flood; render-only 28° |
| NormalTol (same-facing grow) | 12° (`dot ≥ +cos`) | `abs(dot) ≥ cos(35°)` |
| MembershipChordK | 2.0 × local chord error at R | diag-scaled `RadiusCappedTolerance` |
| RmsOverR (accept) | 1.5% | relative `RMS ≤ 0.20·r` |
| InlierFraction (accept) | 90% per-triangle | region-mean validation |
| ClosedDeg (Ø) | 300° + hysteresis 300–330° | 315° hard, no hysteresis |
| MaxRadiusFraction | 0.5 × picked-body extent | none (unbounded) |
| Aperture radius (seed) | ~6 px | single-pixel ray |
| Local scale source | picked node bounds + fitted R | whole-scene sceneDiagonal |

> `sceneDiagonal` is retired for **detection** tolerances (still fine for raycast culling).

---

## 10. Test matrix (host + Android mirror)
- **Holes:** interior cylinder with **inward** normals → Ø (today indistinguishable from boss because of `abs`); various radii/tessellation densities; seed-independence (every triangle on the wall → same Ø).
- **Bosses:** exterior cylinder → Ø.
- **Fillets:** straight-edge (cylinder) and curved-edge (torus); tight + coarse tessellation; seed-independence → same R; **no leak** onto adjacent planar faces.
- **Chamfers:** straight and conical; width+angle correctness.
- **Boundary sweeps:** 295°/305°/320°/330° around ClosedDeg (Ø/R flip + hysteresis); small-coverage fillets near reject threshold.
- **Leak guards:** hole-adjacent-hole, end-cap, fillet→plane tangent, curved-tangent, thin wall (antiparallel), two coaxial features.
- **Negatives:** flat quad, cone, sphere, noisy surface → reject with reason.
- **Robustness:** radial noise; tessellation-density invariance (value stable across densities).
- **End-to-end:** pick → committed Measurement value in mm; reject → no commit + reason.
- **Perf guard:** hover < 100 ms after warmup on a ~100k-tri fixture.

---

## 11. Traceability (root cause → component → test)
| Root cause | Killed by | Test |
|---|---|---|
| 35° flood / no cap (leak #1) | SurfaceRegionGrower (fit-deviation, capped) | fillet→plane leak guard |
| No crease barrier (leak #2) | FeatureEdgeClassifier | crossing-crease leak guard |
| `abs()` antiparallel (leak #3) | grower same-facing `+cos` | thin-wall guard |
| Mean validation (leak #4) | FitAcceptanceGate inlier-fraction | contaminated-patch negative |
| Verbatim/unbounded R (leak #7) | CommitGuard + MaxRadiusFraction | oversized-leak negative |
| 35° too coarse (fillet-miss #1) | curvature-continuity grow | tight-fillet detect |
| Mean axis ≤ 0.35 (fillet-miss) | classifier topology, no axis-mean gate | fillet detect |
| Frozen axis (fillet-miss #7) | AnalyticSurfaceFitter joint GN | partial-coverage fit stability |
| Seed off-feature (#8/#10) | SeedResolver aperture+curvature | off-by-px seed |
| 315° halving | FeatureClassifier hysteresis | boundary sweep |
| Garbage commit | FitAcceptanceGate + CommitGuard | garbage-reject negative |
| 360° phantom / ASCII | DimensionAnnotationModel/Renderer | render snapshot |
| Per-hover rebuild | MeshTopologyCache | perf guard |

---

## 12. Risks & mitigations
- **Torus fit complexity/stability** → cylinder-first with torus only when curvature warrants; both residual-gated; extensive negatives.
- **Curvature estimation cost on large meshes** → precompute once on load, off-thread, LRU-bounded; hover uses cached data only.
- **Aperture-cone seeding cost** → small fixed sample count; reuse cached topology.
- **Behavior change for users used to current values** → CommitGuard makes wrong values impossible to commit silently; values become trustworthy (intended).
- **Shared infra touching other tools** (topology cache, primitives) → additive; existing modes keep their paths until opted in.

---

## 13. Affected files (indicative)
- New (Core/Measurement/Engine): `MeshTopologyCache`, `FeatureEdgeClassifier`, `SeedResolver`, `EdgeLoopFitter`, `SurfaceRegionGrower`, `AnalyticSurfaceFitter`, `ChamferDetector`, `FitAcceptanceGate`, `FeatureClassifier`, `CircularFeatureDetectionPipeline` (orchestrator) + `PickFeedbackAndCommitGuard`.
- Replace/retire: `CircularFeatureDetectionService` monolith (decomposed into the above), raw-index parts of `FeatureEdgeExtractor`.
- Modify: `MeshMeasurePicker` (pick/hover routing + local-space geometry), `MeasurementSession.AdvanceCircularFeature`, `MeasurementBuilders.BuildCircularFeature`, `Measurement`/`PickTypes` (chamfer + confidence + torus fields), `MeasurementPresenter` + `Primitives`, `GlesMeasurementOverlay` (Android), `AndroidMeasureRaycaster` (drop `preferredNode` override; front-face policy), `SceneUnitSystemService` (unit/precision auto-format).
- Tests: new `CircularFeatureTestMatrix` (host) + Android mirror.

---

## 14. Rollout (all-at-once, internal ordering)
Single body of work, but built/verified in dependency order so it stays green:
1. MeshTopologyCache + FeatureEdgeClassifier (+ unit tests).
2. SeedResolver + SurfaceRegionGrower + AnalyticSurfaceFitter(cylinder) + FitAcceptanceGate + FeatureClassifier + CommitGuard → **holes + straight fillets correct, no leak, no garbage**.
3. Torus fit + EdgeLoopFitter (exact Ø) + ChamferDetector.
4. DimensionAnnotationModel + ProfessionalDimensionRenderer + unit auto-format.
5. Full test matrix + Android mirror + perf guard; device re-validation against the three failing taps.

---

## 15. Open assumptions (resolved unless you say otherwise)
- **Mesh-only**, no B-Rep; reported radius is mesh-approximate with residual available.
- **Closed/open** is topology-primary (closed crease loop) with angular-coverage+hysteresis fallback for noisy rims.
- **EdgeSnapService** stays independent for v1 but **shares MeshTopologyCache**; cross-validating the snap arc vs the diameter fit is a later enhancement.
- **Diagnostics** move from static sinks to instance-scoped observers to survive scene reloads.
- **Draw standard:** ISO-leaning now, ISO/ASME setting later.
