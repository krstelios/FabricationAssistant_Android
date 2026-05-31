# Measurement Boundary Busy Animation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a small, non-blocking spinner chip while a bounding-box boundary calculation is running, mirroring the existing render busy chip, with a 180 ms anti-flicker delay.

**Architecture:** Extract the render busy chip's view construction into a shared `CreateBusyChip` helper, then add a dedicated measurement busy chip (its own overlay, fields, version token, and show/hide methods) that the bounding-box commit path drives. The delayed show only displays the chip if the calc is still running after 180 ms.

**Tech Stack:** C# / .NET for Android (Xamarin-style), `MaterialCardView` + indeterminate `ProgressBar`, `Interlocked`/`Volatile` version token, `Task.Delay`, xUnit source-assertion tests (GL/UI cannot run in the net8.0 host runner).

**Spec:** `docs/superpowers/specs/2026-06-01-measurement-boundary-busy-animation-design.md`

**Note on a spec refinement:** the spec described a tokened `HideMeasureBusy(int token)`. This plan uses a parameterless `HideMeasureBusy()` that bumps the version (cancelling any pending delayed show) and always hides. It is functionally equivalent given that bounding-box commits are serialized by `_measureBoundingBoxBusy`, and it avoids awkward double-increment at the `OnPause` call site.

**Conventions:**
- All file paths are relative to the repo root `C:\Users\skritikos\Desktop\Fabrication Assistant\Android`.
- This is a nested git repo; run git from inside `Android/` and stage by explicit path.
- The app builds with: `dotnet build src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj --nologo`
- Tests run with: `dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --nologo`
- Strings in source must stay ASCII (use `...`, not an ellipsis character).

---

## File Structure

- **Modify** `src/FabricationAssistant.App.Android/MainActivity.cs`
  - Add a private `CreateBusyChip(...)` helper (shared chip view construction).
  - Refactor `CreateRenderBusyOverlay` to call it (no behavior change).
  - Add measurement busy fields, `CreateMeasureBusyOverlay`, `ShowMeasureBusyDelayed`, `ShowMeasureBusyAfterDelayAsync`, `HideMeasureBusy`.
  - Call `CreateMeasureBusyOverlay` at setup; hide on `OnPause`; remove on destroy.
  - Hook show/hide into `CommitBoundingBoxForNodesAsync`.
- **Modify** `src/FabricationAssistant.App.Android.Tests/GlesRendererSourceTests.cs`
  - Add source-assertion tests for the shared helper, the measure chip plumbing, and the commit hook.

---

## Task 1: Extract shared `CreateBusyChip` helper and refactor the render chip

**Files:**
- Modify: `src/FabricationAssistant.App.Android/MainActivity.cs` (`CreateRenderBusyOverlay`, ~lines 14307-14358)
- Test: `src/FabricationAssistant.App.Android.Tests/GlesRendererSourceTests.cs`

- [ ] **Step 1: Write the failing test**

Add this method to the `GlesRendererSourceTests` class (e.g. right before `AdditiveBoundingBox_ArmsSelectionAndKeepsAccumulatorUntilToggle`):

```csharp
    [Fact]
    public void RenderAndMeasureBusyChips_ShareOneViewBuilder()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        // The chip view construction is centralised in one helper.
        string helper = ExtractMethod(mainActivity, "private FrameLayout CreateBusyChip");
        Assert.Contains("new ProgressBar(this) { Indeterminate = true }", helper);
        Assert.Contains("GravityFlags.Bottom | GravityFlags.CenterHorizontal", helper);
        Assert.Contains("return overlay;", helper);

        // The render overlay is built through the shared helper (no inline views).
        string renderOverlay = ExtractMethod(mainActivity, "private void CreateRenderBusyOverlay");
        Assert.Contains("CreateBusyChip(container, \"Updating render...\", out _renderBusyDetail)", renderOverlay);
        Assert.DoesNotContain("new ProgressBar", renderOverlay);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --nologo --filter "FullyQualifiedName~RenderAndMeasureBusyChips_ShareOneViewBuilder"`
Expected: FAIL — `CreateBusyChip` not found (ExtractMethod assertion fails).

- [ ] **Step 3: Add the shared helper**

In `MainActivity.cs`, immediately ABOVE the `private void CreateRenderBusyOverlay(FrameLayout container)` method, insert:

```csharp
    // Shared construction for the bottom-center busy chips (render + measure):
    // a Gone/alpha-0 overlay holding a card with an indeterminate spinner and a
    // single-line detail label. Returns the overlay; outputs the detail view so
    // callers can update its text.
    private FrameLayout CreateBusyChip(FrameLayout container, string initialDetail, out TextView detail)
    {
        var overlay = new FrameLayout(this)
        {
            Visibility = ViewStates.Gone,
            Clickable = false,
            Focusable = false,
            Alpha = 0f,
        };
        container.AddView(overlay, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        var card = new MaterialCardView(this);
        card.SetCardBackgroundColor(GetColorCompat(Resource.Color.fa_surface_background));
        card.Radius = Dp(8);
        card.StrokeWidth = Dp(1);
        card.SetStrokeColor(ColorStateList.ValueOf(GetColorCompat(Resource.Color.fa_border)));
        card.Elevation = Dp(5);

        var cardParams = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            Dp(48),
            GravityFlags.Bottom | GravityFlags.CenterHorizontal);
        cardParams.BottomMargin = Dp(18);
        overlay.AddView(card, cardParams);

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(14), 0, Dp(16), 0);
        card.AddView(row, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.MatchParent));

        var spinner = new ProgressBar(this) { Indeterminate = true };
        spinner.IndeterminateTintList = ColorStateList.ValueOf(GetColorCompat(Resource.Color.fa_accent_500));
        var spinnerParams = new LinearLayout.LayoutParams(Dp(24), Dp(24));
        spinnerParams.RightMargin = Dp(10);
        row.AddView(spinner, spinnerParams);

        detail = new TextView(this)
        {
            Text = initialDetail,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        detail.SetSingleLine(true);
        detail.SetTextColor(GetColorCompat(Resource.Color.fa_text_primary));
        detail.SetTextSize(ComplexUnitType.Px, Dp(13));
        row.AddView(detail, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent));

        return overlay;
    }
```

- [ ] **Step 4: Refactor `CreateRenderBusyOverlay` to use the helper**

Replace the entire body of `CreateRenderBusyOverlay` (from `_renderBusyOverlay = new FrameLayout(this)` down to the final `row.AddView(_renderBusyDetail, ...)` call) so the method becomes exactly:

```csharp
    private void CreateRenderBusyOverlay(FrameLayout container)
    {
        _renderBusyOverlay = CreateBusyChip(container, "Updating render...", out _renderBusyDetail);
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --nologo --filter "FullyQualifiedName~RenderAndMeasureBusyChips_ShareOneViewBuilder"`
Expected: PASS.

- [ ] **Step 6: Build the app to confirm the refactor compiles with no warnings**

Run: `dotnet build src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj --nologo`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/FabricationAssistant.App.Android/MainActivity.cs src/FabricationAssistant.App.Android.Tests/GlesRendererSourceTests.cs
git commit -m "Extract shared CreateBusyChip helper for busy overlays"
```

---

## Task 2: Add the measurement busy chip and wire it into the bounding-box commit

This task adds the fields, the overlay, the show/hide methods, the lifecycle wiring, and the commit hook together, so every new field and method is used in the same change (no unused-member warnings).

**Files:**
- Modify: `src/FabricationAssistant.App.Android/MainActivity.cs`
  - Fields near `_renderModeBusyVersion` (~line 337)
  - Setup call site (~line 506)
  - `CommitBoundingBoxForNodesAsync` (~lines 5766 and 5800)
  - `OnPause` (~line 14763)
  - `RemoveOwnedOverlayViews` (~line 15145)
  - New methods near the render busy methods (~line 14398)
- Test: `src/FabricationAssistant.App.Android.Tests/GlesRendererSourceTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these two methods to `GlesRendererSourceTests` (next to the test added in Task 1):

```csharp
    [Fact]
    public void MeasureBusyChip_IsCreatedDelayedAndCleanedUp()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        // Built through the shared helper next to the render chip.
        string createMeasure = ExtractMethod(mainActivity, "private void CreateMeasureBusyOverlay");
        Assert.Contains("CreateBusyChip(container, \"Computing bounding box...\", out _measureBusyDetail)", createMeasure);
        Assert.Contains("CreateMeasureBusyOverlay(container);", mainActivity);

        // 180 ms anti-flicker gate, guarded by the version token.
        Assert.Contains("private const int MeasureBusyShowDelayMs = 180;", mainActivity);
        string delayed = ExtractMethod(mainActivity, "private async Task ShowMeasureBusyAfterDelayAsync");
        Assert.Contains("await Task.Delay(MeasureBusyShowDelayMs)", delayed);
        Assert.Contains("token != Volatile.Read(ref _measureBusyVersion)", delayed);

        // Hide bumps the version (cancels a pending show) and lives in the loop.
        string hide = ExtractMethod(mainActivity, "private void HideMeasureBusy");
        Assert.Contains("Interlocked.Increment(ref _measureBusyVersion);", hide);
        Assert.Contains("_measureBusyOverlay.Visibility = ViewStates.Gone;", hide);

        // Lifecycle: hidden on pause, removed on teardown.
        string onPause = ExtractMethod(mainActivity, "protected override void OnPause");
        Assert.Contains("HideMeasureBusy();", onPause);
        string cleanup = ExtractMethod(mainActivity, "private void RemoveOwnedOverlayViews");
        Assert.Contains("RemoveFromParent(_measureBusyOverlay);", cleanup);
    }

    [Fact]
    public void BoundingBoxCommit_ShowsAndHidesMeasureBusyChip()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        string commit = ExtractMethod(mainActivity, "private async Task CommitBoundingBoxForNodesAsync");
        Assert.Contains("ShowMeasureBusyDelayed(\"Computing bounding box...\");", commit);
        Assert.Contains("HideMeasureBusy();", commit);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --nologo --filter "FullyQualifiedName~MeasureBusyChip_IsCreatedDelayedAndCleanedUp|FullyQualifiedName~BoundingBoxCommit_ShowsAndHidesMeasureBusyChip"`
Expected: FAIL — `CreateMeasureBusyOverlay` / `ShowMeasureBusyDelayed` not found.

- [ ] **Step 3: Add the fields**

In `MainActivity.cs`, find:

```csharp
    private int _renderModeBusyVersion;
```

Insert immediately after it:

```csharp
    private FrameLayout? _measureBusyOverlay;
    private TextView? _measureBusyDetail;
    private int _measureBusyVersion;
    private const int MeasureBusyShowDelayMs = 180;
```

- [ ] **Step 4: Create the overlay at setup**

Find (in the view setup, ~line 506):

```csharp
        CreateRenderBusyOverlay(container);
```

Replace with:

```csharp
        CreateRenderBusyOverlay(container);
        CreateMeasureBusyOverlay(container);
```

- [ ] **Step 5: Add the overlay-creation and show/hide methods**

In `MainActivity.cs`, immediately AFTER the closing brace of `HideRenderBusy` (the method ending around line 14398, right before `CreateLoadingOverlay`), insert:

```csharp
    private void CreateMeasureBusyOverlay(FrameLayout container)
    {
        _measureBusyOverlay = CreateBusyChip(container, "Computing bounding box...", out _measureBusyDetail);
    }

    // Schedules the measurement busy chip to appear only if the calculation is
    // still running after MeasureBusyShowDelayMs, so fast bounding-box commits
    // never flash the chip. Each call takes a fresh version token; a later show
    // or any hide invalidates a pending show.
    private void ShowMeasureBusyDelayed(string detail)
    {
        int token = Interlocked.Increment(ref _measureBusyVersion);
        _ = ShowMeasureBusyAfterDelayAsync(token, detail);
    }

    private async Task ShowMeasureBusyAfterDelayAsync(int token, string detail)
    {
        try
        {
            await Task.Delay(MeasureBusyShowDelayMs);
        }
        catch (Exception)
        {
            return;
        }

        void Apply()
        {
            // The token is stale once the calc finished (hide) or a newer show
            // started, so do not pop the chip up after the fact.
            if (token != Volatile.Read(ref _measureBusyVersion) || _measureBusyOverlay is null)
                return;

            if (_measureBusyDetail is not null)
                _measureBusyDetail.Text = detail;

            _measureBusyOverlay.Animate()?.Cancel();
            _measureBusyOverlay.Visibility = ViewStates.Visible;
            _measureBusyOverlay.Alpha = 0f;
            _measureBusyOverlay.Animate()?.Alpha(1f)?.SetDuration(100)?.Start();
        }

        if (Looper.MyLooper() == Looper.MainLooper)
            Apply();
        else
            RunOnUiThread(Apply);
    }

    private void HideMeasureBusy()
    {
        // Bumping the version cancels any delayed show still pending its token.
        Interlocked.Increment(ref _measureBusyVersion);

        void Apply()
        {
            if (_measureBusyOverlay is null)
                return;

            _measureBusyOverlay.Animate()?.Cancel();
            _measureBusyOverlay.Alpha = 0f;
            _measureBusyOverlay.Visibility = ViewStates.Gone;
        }

        if (Looper.MyLooper() == Looper.MainLooper)
            Apply();
        else
            RunOnUiThread(Apply);
    }
```

- [ ] **Step 6: Hook the chip into the bounding-box commit**

In `CommitBoundingBoxForNodesAsync`, find:

```csharp
        ApplyMeasurementSettings();
        _measureBoundingBoxBusy = true;
```

Replace with:

```csharp
        ApplyMeasurementSettings();
        _measureBoundingBoxBusy = true;
        ShowMeasureBusyDelayed("Computing bounding box...");
```

Then find the start of the same method's `finally` block:

```csharp
        finally
        {
            _measureBoundingBoxBusy = false;
```

Replace with:

```csharp
        finally
        {
            _measureBoundingBoxBusy = false;
            HideMeasureBusy();
```

- [ ] **Step 7: Hide on pause**

In `OnPause`, find:

```csharp
        HideRenderBusy(pausedRenderVersion);
```

Replace with:

```csharp
        HideRenderBusy(pausedRenderVersion);
        HideMeasureBusy();
```

- [ ] **Step 8: Remove the overlay on teardown**

In `RemoveOwnedOverlayViews`, find:

```csharp
        RemoveFromParent(_renderBusyOverlay);
```

Replace with:

```csharp
        RemoveFromParent(_renderBusyOverlay);
        RemoveFromParent(_measureBusyOverlay);
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --nologo --filter "FullyQualifiedName~MeasureBusyChip_IsCreatedDelayedAndCleanedUp|FullyQualifiedName~BoundingBoxCommit_ShowsAndHidesMeasureBusyChip"`
Expected: PASS (both).

- [ ] **Step 10: Build the app to confirm it compiles with no warnings**

Run: `dotnet build src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj --nologo`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 11: Commit**

```bash
git add src/FabricationAssistant.App.Android/MainActivity.cs src/FabricationAssistant.App.Android.Tests/GlesRendererSourceTests.cs
git commit -m "Add measurement bounding-box busy chip with anti-flicker delay"
```

---

## Task 3: Verify — full suite + device smoke test

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --nologo`
Expected: `Passed!` with 0 failures (231 tests: the prior 228 plus the 3 new ones).

- [ ] **Step 2: Deploy to the connected tablet**

Run: `dotnet build src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj -t:Install --nologo`
Expected: `Build succeeded.` and the app installs on device `R52Y80CE37L`.

- [ ] **Step 3: Launch and exercise a bounding-box measurement**

Run: `adb logcat -c; adb shell monkey -p com.fabricationassistant.android -c android.intent.category.LAUNCHER 1`
Then, on the tablet: open a model, enter the Measure tool, choose Bounding Box, and select a node (or a large multi-node selection so the calc is slow enough to see the chip).

- [ ] **Step 4: Confirm behavior in logs and on screen**

Run: `adb logcat -d -v brief | grep -iE "FA.Measure|BBox commit|AndroidRuntime|FATAL|Fatal signal"`
Expected: a `BBox commit requested` log per commit, no `FATAL`/`AndroidRuntime` app crash. On screen: the bottom-center spinner chip ("Computing bounding box...") appears for a slow commit and does NOT flash for a fast (small-selection) commit.

- [ ] **Step 5: Report results**

Summarize: tests passed, build clean, and the observed chip behavior (appeared on slow commit, hidden on fast commit, dismissed on completion).

---

## Self-Review

**Spec coverage:**
- Small bottom spinner chip mirroring the render chip → Task 1 (shared helper) + Task 2 (`CreateMeasureBusyOverlay`). ✓
- Bounding-box commit only → Task 2 Step 6 hooks `CommitBoundingBoxForNodesAsync` only. ✓
- 180 ms anti-flicker delay → Task 2 (`MeasureBusyShowDelayMs`, `ShowMeasureBusyAfterDelayAsync`). ✓
- No interference with render/import overlays → separate fields + separate version token; render path only refactored to a pure view helper. ✓
- Lifecycle (hide on pause, remove on destroy) → Task 2 Steps 7-8. ✓
- Testing via source assertions, then build + device smoke → Tasks 1-3. ✓

**Placeholder scan:** none — every step has exact code/commands.

**Type/name consistency:** `_measureBusyOverlay`, `_measureBusyDetail`, `_measureBusyVersion`, `MeasureBusyShowDelayMs`, `CreateBusyChip`, `CreateMeasureBusyOverlay`, `ShowMeasureBusyDelayed`, `ShowMeasureBusyAfterDelayAsync`, `HideMeasureBusy` are used identically across tasks and tests. The detail text literal `"Computing bounding box..."` matches between the overlay default, the show call, and the tests.
