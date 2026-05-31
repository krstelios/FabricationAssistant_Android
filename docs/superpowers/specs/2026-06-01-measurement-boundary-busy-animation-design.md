# Measurement boundary-calculation busy animation

**Date:** 2026-06-01
**Status:** Approved (design)
**Area:** Android app — measurement tools / UI feedback

## Problem

Committing a bounding-box measurement sometimes takes a noticeable amount of
time (the boundary calculation runs asynchronously over the selected nodes).
During that wait the UI gives no feedback, so the app can feel unresponsive.
The render pipeline already shows a small "Updating render..." busy chip during
render-mode changes; we want the same kind of lightweight animation while a
bounding-box boundary calculation is in progress.

## Goal

While a bounding-box commit is computing, show a small, non-blocking spinner
chip that visually matches the existing render busy chip. It must not flash for
fast calculations, and must not interfere with the existing render or import
overlays.

## Non-goals

- Other measurement modes (point / edge / face raycasts). These are
  effectively instant and intentionally excluded.
- Any change to the render busy chip or the model-import loading overlay
  beyond a small shared view-construction helper.
- Progress percentage / cancellation. The chip is indeterminate, like the
  render chip.

## Existing building blocks (as of this design)

- `_renderBusyOverlay` / `_renderBusyDetail` / `_renderModeBusyVersion`
  (`MainActivity.cs`): a bottom-center `MaterialCardView` holding an
  indeterminate `ProgressBar` + a `TextView`. Shown by `ShowRenderBusy(detail)`
  (fade-in over 100 ms) and hidden by `HideRenderBusy(token)`. An
  `Interlocked.Increment` version token guards against a stale hide racing a
  newer show. Built in `CreateRenderBusyOverlay(container)`; hidden on
  `OnPause`; removed from its parent on destroy.
- `CommitBoundingBoxForNodesAsync(nodeIds, selectionVersion, reason)`
  (`MainActivity.cs:5709`): the only measurement path that does heavy async
  work. It sets `_measureBoundingBoxBusy = true` (~line 5766), awaits
  `_measure.TryCommitBoundingBoxFromSelectionWithIdAsync(group)` once per commit
  group, then clears the flag in a `finally` (~line 5800). Triggered from the
  model-explorer selection, the BOM selection, and the viewport bbox tap. Early
  returns for empty selections happen *before* the busy flag is set.

## Design

### Approach

Add a dedicated measurement busy chip that mirrors the render chip, rather than
reusing or generalizing the render one. This matches the codebase convention of
one overlay per purpose (import overlay + render overlay already coexist) and
keeps the proven render path untouched. To avoid duplicating ~40 lines of view
code, extract the card+spinner+text construction into a small shared helper that
both the render overlay and the new measurement overlay call.

Rejected alternatives:

- **Generalize into one shared busy chip keyed by an owner token.** Requires
  reworking the working render path and defining behavior when render and
  measurement are busy simultaneously. YAGNI for a single new caller.
- **Reuse the render chip directly.** Shares `_renderModeBusyVersion`, so a
  render-mode change and a bbox commit would clobber each other's show/hide.
  Fragile.

### Components

1. **Shared view helper** — `CreateBusyChip(FrameLayout container, out TextView detail)`:
   builds the `Gone`/alpha-0 overlay `FrameLayout`, the bottom-center card, the
   indeterminate spinner, and the detail `TextView`, returning the overlay and
   the detail view. `CreateRenderBusyOverlay` is refactored to use it (no
   behavior change); `CreateMeasureBusyOverlay` uses it too.

2. **New fields** (`MainActivity`): `FrameLayout? _measureBusyOverlay`,
   `TextView? _measureBusyDetail`, `int _measureBusyVersion`.

3. **`CreateMeasureBusyOverlay(container)`** — called next to
   `CreateRenderBusyOverlay` during view setup.

4. **`int ShowMeasureBusyDelayed(string detail)`** — increments and captures
   `_measureBusyVersion`, returns the token, and schedules a delayed show:
   after `Task.Delay(MeasureBusyShowDelayMs)` (180 ms), it shows the chip
   (fade-in 100 ms) only if the token is still current. The delay is the
   anti-flicker gate: a calc that finishes within 180 ms never shows the chip.

5. **`void HideMeasureBusy(int token)`** — increments `_measureBusyVersion`
   (cancelling any pending delayed show), and if `token` matches the pre-bump
   value, fades the chip out and sets it `Gone`. UI-thread-guarded via
   `Looper`/`RunOnUiThread`, like the render equivalents.

### Data / control flow

In `CommitBoundingBoxForNodesAsync`:

```
... _measureBoundingBoxBusy = true;            // ~5766
    int busyToken = ShowMeasureBusyDelayed("Computing bounding box...");
... try { await ...commit loop... }
    finally {
        _measureBoundingBoxBusy = false;       // ~5800
        HideMeasureBusy(busyToken);
        ... existing finally logic ...
    }
```

The chip wraps the entire commit-group loop, so single-node, multi-node, and
additive commits each show at most one chip. Queued additive commits chain into
a fresh `CommitBoundingBoxForNodesAsync` call (existing behavior); each chained
call manages its own token, and the 180 ms delay prevents flicker across a fast
chain.

### Lifecycle

- `OnPause`: hide the measurement chip alongside the existing render-chip hide
  (increment the version, then `HideMeasureBusy`).
- Destroy/teardown: `RemoveFromParent(_measureBusyOverlay)` next to the existing
  `RemoveFromParent(_renderBusyOverlay)`.

### Threading

`CommitBoundingBoxForNodesAsync` is invoked from UI handlers and its
continuations resume on the UI synchronization context, so show/hide normally
run on the UI thread; the `Looper`/`RunOnUiThread` guards (copied from the
render chip) make this safe regardless.

## Error handling

- The commit's existing `catch` (swallows non-fatal exceptions) is unchanged;
  `HideMeasureBusy` runs in `finally`, so the chip is always dismissed even on
  failure or timeout.
- A pending delayed show whose calc finishes first is cancelled by the version
  bump in `HideMeasureBusy`, so no orphan chip appears after completion.

## Testing

Follow the repo's source-assertion convention for `MainActivity`/UI code
(GL/UI cannot run in the net8.0 host runner):

1. `CommitBoundingBoxForNodesAsync` calls `ShowMeasureBusyDelayed(...)` after
   setting the busy flag and `HideMeasureBusy(...)` in its `finally`.
2. `ShowMeasureBusyDelayed` gates the show on the 180 ms delay and the current
   version token.
3. `CreateMeasureBusyOverlay` builds an indeterminate spinner chip via the
   shared helper, and the overlay is removed from its parent on destroy.

Then: build the app, run the full test suite, and device-smoke on the connected
tablet — confirm the chip appears during a slow bounding-box commit and stays
hidden for fast ones.

## Risks

- Low. The change is additive UI state plus three call sites in one existing
  method; the render path is only refactored to call a pure view helper with no
  behavior change.
