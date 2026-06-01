# Android Snap — On-Device Validation Checklist (2026-06-01)

Run on a representative ARM tablet with an S Pen. Each item maps to brief §11/§12.
These cover the parts the net8.0 host test suite cannot exercise: the BVH raycaster,
real scene visibility/isolation, the section-cap raycast, and live frame rate.

## Visibility (§11, §12)
- [ ] Front-facing visible edge snaps; red marker appears on it.
- [ ] Back-side edge (behind the solid) does NOT snap.
- [ ] Edge behind a face is not selectable; marker does not appear through the surface.
- [ ] Edge occluded by another body is not selectable.
- [ ] Hidden body: its edges are not selectable.
- [ ] Isolated body: only the isolated body's edges are selectable.
- [ ] Sectioned model: clipped-away edges are not selectable; section curves ARE.

## Section geometry (§6, §11)
- [ ] Section curves snap (endpoints + midpoint) like normal edges.
- [ ] Section cap face is selectable in Face-to-Point.
- [ ] Moving the section plane updates snap targets within a frame or two (no stale marker).

## Tools (§11, §12)
- [ ] Point-to-Point: first point and second point both snap; marker == committed point.
- [ ] Face-to-Point: select a face, then snap the point; marker == committed point.
- [ ] Endpoint-snap toggle and Midpoint-snap toggle in the measurement toolbar both take effect live.
- [ ] On a freshly loaded large model, before warmup finishes: hover shows no marker AND a tap does not commit a point (consistent — the accepted commit-only-previewed trade-off). If this stalls or is unacceptable, apply Conditional Task D from the plan.

## Performance (§12)
- [ ] Move the S Pen continuously over a dense model's edges: marker tracks with no visible lag; viewport stays ~60 FPS (watch the FA frame/jank logs).
- [ ] Repeat with a section active and many visible edges on screen.
- [ ] Move over empty space continuously: no marker, no frame drops.
- [ ] No GC churn spikes in logcat during continuous hover (the engine scan is allocation-free with diagnostics off — guarded by `EdgeSnapAllocationTests`).

## If any item fails
Record which, then apply the matching conditional task (B/D/E/F) from
`docs/superpowers/plans/2026-06-01-android-snap-revalidation.md`:
- Continuous-hover frame-rate fails → Conditional Task B (coalesce hover + single occlusion raycast).
- Click stalls on large/un-warmed mesh, or no-snap-until-warmed is unacceptable → Conditional Task D (broaden warmup / warm-on-miss).
- Marker flickers between adjacent candidates → Conditional Task E (selection hysteresis).
- A real model stalls hover on a single mesh with a huge non-weldable target count → Conditional Task F (per-mesh target cap).
