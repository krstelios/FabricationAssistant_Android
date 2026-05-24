# Plan 3B - MSAA Framebuffer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render the Android viewport scene into an offscreen multisample FBO with a `GL_DEPTH24_STENCIL8` attachment, then blit-resolve to the default backbuffer. This eliminates aliased edges, replaces the current EGL-level MSAA with a renderer-owned FBO, and lays the stencil-attached canvas that Plan 3A's section-cut cap algorithm will write into.

**Architecture:** A new `MsaaSceneFramebuffer` class owns the FBO + multisample color renderbuffer + multisample depth/stencil renderbuffer. `GlesViewportRenderer` binds it at the top of `OnDrawFrame`, runs the existing grid/mesh/edge passes into it, then blit-resolves color to the default framebuffer (FBO 0) before the selection-outline post-process. The EGL-level MSAA request on `MultisampleConfigChooser` is dropped (default backbuffer becomes single-sample with depth+stencil 8) since the offscreen FBO now carries antialiasing.

**Tech Stack:**
- `Silk.NET.OpenGLES` (already referenced) - new calls: `RenderbufferStorageMultisample`, `BlitFramebuffer`, `CheckFramebufferStatus`.
- `xunit` 2.9.2 - pure-arithmetic test for `ClampSamples` in `FabricationAssistant.App.Android.Tests`.

**Critical guidance (carried from prior plans; do NOT violate):**
- No git commits anywhere in this plan. Leave changes as unstaged mods.
- Never edit any file under `../src/`.
- Build via `Android/tools/build.ps1`. Never `dotnet build`.
- No emojis in source files. ASCII-only inside `.glsl` files (no shader changes in 3B, but the rule stands).
- `Application.Current.Dispatcher` is forbidden in any new code.

**Definition of done for this plan:**
1. `Android/tools/build.ps1 -Configuration Debug` returns 0.
2. `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` returns 0 with all tests passing including the new `MsaaSceneFramebufferTests`.
3. APK installs on tablet emulator. A glTF/GLB model renders without visible jagged edges at MSAA = 4x. At MSAA = Off, jagged edges are visible at 45-degree features.
4. Section/stencil readiness check: `glGetFramebufferAttachmentParameteriv` for `GL_STENCIL_ATTACHMENT` on the bound FBO returns a non-zero object name when the renderer is active (verified via a one-time `Log.Debug` line guarded behind a `#if DEBUG`).
5. Toggling MSAA in the bottom sheet (Off / 2x / 4x) takes effect when the sheet closes, with no app restart required.
6. `git status --porcelain "src/"` empty.

---

## File structure

**Create:**
- `Android/src/FabricationAssistant.Rendering.Gles/MsaaSceneFramebuffer.cs` - FBO + multisample renderbuffer lifecycle wrapper. ~140 lines.
- `Android/src/FabricationAssistant.App.Android.Tests/MsaaSceneFramebufferTests.cs` - xUnit unit tests for the pure-arithmetic `ClampSamples` function. ~40 lines.

**Modify:**
- `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs` - integrate the FBO into the render loop. About 25 lines of changes spread across `OnSurfaceCreated`, `OnSurfaceChanged`, `OnDrawFrame`, and the existing private `ResetMainFramebufferState`.
- `Android/src/FabricationAssistant.App.Android\Views\ViewportSurfaceView.cs` - drop EGL-level MSAA request (pass 0 to `MultisampleConfigChooser`).
- `Android/src/FabricationAssistant.App.Android\PreferencesBottomSheet.cs` - update the MSAA hint string from "MSAA changes apply on next app launch." to "MSAA applies when the sheet closes."
- `Android/src/FabricationAssistant.App.Android.Tests\FabricationAssistant.App.Android.Tests.csproj` - add a single `<Compile Include>` for `MsaaSceneFramebuffer.cs` so the test project can reference it without taking the whole Android renderer DLL.

---

## Task 1: Pure-arithmetic test for ClampSamples

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android.Tests/MsaaSceneFramebufferTests.cs`
- Modify: `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj` (link the source file under test)

- [ ] **Step 1: Write the failing test file** at `Android/src/FabricationAssistant.App.Android.Tests/MsaaSceneFramebufferTests.cs`:

```csharp
using FabricationAssistant.Rendering.Gles;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class MsaaSceneFramebufferTests
{
    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(7, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    [InlineData(32, 16)]
    public void ClampSamples_snaps_to_nearest_bucket(int desired, int expected)
    {
        Assert.Equal(expected, MsaaSceneFramebuffer.ClampSamples(desired));
    }

    [Fact]
    public void ClampSamples_handles_all_legal_buckets()
    {
        Assert.Equal(1, MsaaSceneFramebuffer.ClampSamples(1));
        Assert.Equal(2, MsaaSceneFramebuffer.ClampSamples(2));
        Assert.Equal(4, MsaaSceneFramebuffer.ClampSamples(4));
        Assert.Equal(8, MsaaSceneFramebuffer.ClampSamples(8));
        Assert.Equal(16, MsaaSceneFramebuffer.ClampSamples(16));
    }
}
```

- [ ] **Step 2: Link the source file under test** by adding this `<ItemGroup>` to `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`, immediately before the closing `</Project>` tag:

```xml
  <!--
    Link MsaaSceneFramebuffer's static ClampSamples helper into the test
    assembly so unit tests can exercise the pure-arithmetic path without
    pulling in the full FabricationAssistant.Rendering.Gles project (which
    targets net8.0-android and references Silk.NET.OpenGLES, neither of
    which can load in the net8.0 host test runner).
  -->
  <ItemGroup>
    <Compile Include="$(MSBuildThisFileDirectory)..\FabricationAssistant.Rendering.Gles\MsaaSceneFramebuffer.cs"
             LinkBase="Linked\Rendering.Gles" />
  </ItemGroup>
```

- [ ] **Step 3: Run the test and verify it fails** with a build error (because `MsaaSceneFramebuffer.cs` does not yet exist):

```
cd "Android"
dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
```

Expected: build error: `Could not find file '...MsaaSceneFramebuffer.cs'`. This proves the test wiring is in place.

---

## Task 2: Implement MsaaSceneFramebuffer

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/MsaaSceneFramebuffer.cs`

- [ ] **Step 1: Create the file with skeleton + `ClampSamples`** at `Android/src/FabricationAssistant.Rendering.Gles/MsaaSceneFramebuffer.cs`:

```csharp
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Offscreen multisample framebuffer for the Android viewport. Owns a
/// multisample color renderbuffer (RGBA8) + multisample depth-stencil
/// renderbuffer (D24S8) attached to a single FBO. The renderer draws every
/// non-post-process pass into this FBO, then blit-resolves the color
/// attachment to FBO 0 before the selection outline composite.
///
/// When the requested sample count is <= 1, single-sample storage is used
/// (still through this wrapper) so the render path stays uniform: scene
/// always renders into the FBO, then blits to default. The stencil
/// attachment is included regardless of sample count because Plan 3A's
/// section-cap algorithm depends on it.
/// </summary>
public sealed class MsaaSceneFramebuffer : IDisposable
{
    private readonly GL _gl;
    private uint _fbo;
    private uint _colorRbo;
    private uint _depthStencilRbo;
    private int _width;
    private int _height;
    private int _samples;
    private bool _disposed;

    public MsaaSceneFramebuffer(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
    }

    public uint FboHandle => _fbo;
    public int Width => _width;
    public int Height => _height;
    public int Samples => _samples;

    /// <summary>
    /// Snaps a desired sample count to the nearest supported bucket
    /// (1, 2, 4, 8, 16). Pure arithmetic - does not read GL state.
    /// The effective sample count is further capped against
    /// GL_MAX_SAMPLES inside <see cref="Ensure"/>.
    /// </summary>
    public static int ClampSamples(int desired)
    {
        return desired switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 4 => 4,
            <= 8 => 8,
            _ => 16
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        Destroy();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: Verify the unit test passes** by running:

```
cd "Android"
dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --filter "MsaaSceneFramebufferTests"
```

Expected output: `Passed!  - Failed: 0, Passed: 11, Skipped: 0`. (Theory has 10 inline data rows + 1 Fact = 11 results.)

- [ ] **Step 3: Add the Ensure method** inside the class, immediately after `ClampSamples`. Insert this method into `MsaaSceneFramebuffer.cs`:

```csharp
    /// <summary>
    /// Allocates (or re-allocates) the FBO + renderbuffers at the given
    /// dimensions and sample count. Re-uses the existing allocation when
    /// (width, height, samples) all match - safe to call every frame.
    /// Throws <see cref="InvalidOperationException"/> if the FBO is
    /// incomplete after attachment.
    /// </summary>
    public unsafe void Ensure(int width, int height, int samples)
    {
        if (width <= 0 || height <= 0)
        {
            Destroy();
            return;
        }

        int clamped = ClampSamples(samples);
        int maxSamples = 0;
        _gl.GetInteger(GLEnum.MaxSamples, &maxSamples);
        if (maxSamples > 0 && clamped > maxSamples)
            clamped = maxSamples;

        if (_fbo != 0 && _width == width && _height == height && _samples == clamped)
            return;

        Destroy();
        _width = width;
        _height = height;
        _samples = clamped;

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRbo);
        if (clamped > 1)
        {
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
                (uint)clamped, InternalFormat.Rgba8, (uint)width, (uint)height);
        }
        else
        {
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                InternalFormat.Rgba8, (uint)width, (uint)height);
        }
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _colorRbo);

        _depthStencilRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthStencilRbo);
        if (clamped > 1)
        {
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer,
                (uint)clamped, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        }
        else
        {
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        }
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthStencilRbo);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            Destroy();
            throw new InvalidOperationException(
                $"MsaaSceneFramebuffer: FBO incomplete after attachment, status = 0x{(int)status:X4}");
        }

        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }
```

- [ ] **Step 4: Add Bind + ResolveToDefault methods** inside the same class, immediately after `Ensure`:

```csharp
    /// <summary>
    /// Binds this FBO as the current draw target. Caller is responsible
    /// for setting the viewport and clearing color/depth/stencil after
    /// binding.
    /// </summary>
    public void Bind()
    {
        if (_fbo == 0) return;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
    }

    /// <summary>
    /// Blit-resolves the multisample color attachment into FBO 0 (the
    /// default backbuffer) at the same dimensions. Depth and stencil are
    /// not resolved - the selection outline post-process only reads color.
    /// </summary>
    public void ResolveToDefault()
    {
        if (_fbo == 0 || _width <= 0 || _height <= 0) return;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.ReadBuffer(GLEnum.ColorAttachment0);
        _gl.BlitFramebuffer(
            0, 0, _width, _height,
            0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        // Restore the standard binding so subsequent draws hit the default
        // framebuffer (the selection outline runs after Resolve).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }
```

- [ ] **Step 5: Add Destroy** inside the same class, immediately after `ResolveToDefault`:

```csharp
    /// <summary>
    /// Releases all GL resources. Safe to call when nothing is allocated.
    /// Must be called on the GL render thread.
    /// </summary>
    public void Destroy()
    {
        if (_colorRbo != 0) { _gl.DeleteRenderbuffer(_colorRbo); _colorRbo = 0; }
        if (_depthStencilRbo != 0) { _gl.DeleteRenderbuffer(_depthStencilRbo); _depthStencilRbo = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        _width = 0;
        _height = 0;
        _samples = 0;
    }
```

- [ ] **Step 6: Verify the renderer project compiles** with the new file. Run:

```
cd "Android"
.\tools\build.ps1 -Configuration Debug
```

Expected: build succeeds with 0 errors. (The new file is picked up automatically by the wildcard `<Compile>` glob in the .csproj.)

---

## Task 3: Integrate MsaaSceneFramebuffer into GlesViewportRenderer

**Files:**
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs`

- [ ] **Step 1: Add a private field for the MSAA FBO** by inserting this line into `GlesViewportRenderer.cs` after the existing `_outlineRenderer` field declaration (around line 29):

```csharp
    private MsaaSceneFramebuffer? _msaaFbo;
```

- [ ] **Step 2: Instantiate the FBO in `OnSurfaceCreated`** by inserting these lines into `GlesViewportRenderer.OnSurfaceCreated`, immediately after the `_outlineRenderer = new GlesOutlineRenderer(...)` call (around line 118) and before `_initialized = true;`:

```csharp
        // Plan 3B: offscreen multisample FBO. All scene passes (grid, mesh,
        // edge) render into this FBO and the resolved color is blitted to
        // the default backbuffer before the selection outline post-process.
        _msaaFbo = new MsaaSceneFramebuffer(_gl);
```

- [ ] **Step 3: Call Ensure in OnSurfaceChanged** by inserting this line into `GlesViewportRenderer.OnSurfaceChanged`, immediately after the existing `_outlineRenderer?.Resize(width, height);` call (around line 133):

```csharp
        _msaaFbo?.Ensure(width, height, Appearance.MsaaSamples);
```

- [ ] **Step 4: Re-allocate the FBO when MSAA samples or viewport size changes**, inside `OnDrawFrame`. Insert these lines right after the existing `var a = Appearance; EnsureEdgesMatchAppearance(a);` statements (around line 151):

```csharp
        // Re-allocate the MSAA FBO if the user changed the sample count
        // via the preferences sheet, or if the viewport was resized. Cheap
        // when nothing has changed (Ensure compares cached dims + samples).
        _msaaFbo?.Ensure(_width, _height, a.MsaaSamples);
```

- [ ] **Step 5: Bind the MSAA FBO at the top of the scene pass**, replacing the existing `ResetMainFramebufferState()` call inside `OnDrawFrame` (around line 172) with:

```csharp
        // Bind the offscreen MSAA FBO. ResetMainFramebufferState restores
        // depth/blend/cull defaults; the bind itself happens here so all
        // subsequent draw calls land in the multisample renderbuffer.
        if (_msaaFbo is not null && _msaaFbo.FboHandle != 0)
            _msaaFbo.Bind();
        else
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        ResetMainFramebufferState();
```

Then, in the existing `ResetMainFramebufferState` method (around line 461), replace the first line:

```csharp
        _gl!.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
```

with:

```csharp
        // Framebuffer is bound by the caller (OnDrawFrame). Do NOT re-bind
        // FBO 0 here - that would discard the MSAA target the caller just
        // selected. This method now only resets pipeline state.
```

- [ ] **Step 6: Clear stencil along with color and depth.** Find the existing line (around line 178):

```csharp
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
```

Replace with:

```csharp
        _gl.ClearStencil(0);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit));
```

- [ ] **Step 7: Resolve to default before the outline pass.** Find the start of the "Selection outline post-process (Phase G)" comment block inside `OnDrawFrame` (around line 371). Immediately before the comment, insert:

```csharp
        // Plan 3B: resolve the MSAA color attachment to the default
        // backbuffer. The selection outline post-process draws into the
        // default FBO over the resolved color.
        if (_msaaFbo is not null && _msaaFbo.FboHandle != 0)
            _msaaFbo.ResolveToDefault();

```

- [ ] **Step 8: Verify the integration compiles.** Run:

```
cd "Android"
.\tools\build.ps1 -Configuration Debug
```

Expected: build succeeds with 0 errors.

---

## Task 4: Drop EGL-level MSAA, keep stencil request

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs`

- [ ] **Step 1: Change the MSAA argument passed to MultisampleConfigChooser** to 0. Find the existing line around line 67:

```csharp
        SetEGLConfigChooser(new MultisampleConfigChooser(AppSettings.MsaaSamples));
```

Replace with:

```csharp
        // Plan 3B: the renderer now owns multisampling via an offscreen
        // FBO. The default backbuffer becomes single-sample with depth +
        // stencil so blit-resolve writes work; the chooser is still
        // required for the EglStencilSize=8 guarantee that section-cut
        // (Plan 3A) needs as a fallback when the offscreen FBO is unable
        // to allocate (low-memory devices).
        SetEGLConfigChooser(new MultisampleConfigChooser(0));
```

- [ ] **Step 2: Update the comment above** (lines 64-66) for accuracy:

```csharp
        // Default backbuffer: RGB8 + Depth24 + Stencil8, single-sample.
        // MSAA is handled by the renderer's offscreen FBO; see Plan 3B
        // (MsaaSceneFramebuffer). The simple integer overload of
        // SetEGLConfigChooser cannot request EGL_STENCIL_SIZE, so we still
        // use MultisampleConfigChooser - just at 0 samples.
```

- [ ] **Step 3: Verify the app project still builds.** Run:

```
cd "Android"
.\tools\build.ps1 -Configuration Debug
```

Expected: build succeeds.

---

## Task 5: Update bottom-sheet MSAA hint

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/PreferencesBottomSheet.cs`

- [ ] **Step 1: Update the MSAA hint string.** Find the existing line in `PreferencesBottomSheet.cs` (around line 134):

```csharp
        AddSubtle(ctx, aa, "MSAA changes apply on next app launch.");
```

Replace with:

```csharp
        AddSubtle(ctx, aa, "MSAA applies when the sheet closes.");
```

Also find the existing line (around line 158):

```csharp
        AddSubtle(ctx, root, "Rendering controls apply live except MSAA, which is selected when the app starts.");
```

Replace with:

```csharp
        AddSubtle(ctx, root, "Rendering controls apply live when the sheet closes.");
```

- [ ] **Step 2: Verify build.** Run:

```
cd "Android"
.\tools\build.ps1 -Configuration Debug
```

Expected: build succeeds.

---

## Task 6: Run full test suite

**Files:** none modified.

- [ ] **Step 1: Run all xUnit tests** to confirm the new test passes and no existing test regressed:

```
cd "Android"
dotnet test src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj
```

Expected: all tests pass. The test count should be the previous total + 11 (10 Theory rows + 1 Fact).

---

## Task 7: Manual verification on emulator or device

**Files:** none.

- [ ] **Step 1: Build a Debug APK.** Run:

```
cd "Android"
.\tools\build.ps1 -Configuration Debug
```

Expected: APK produced under `Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android34.0/`.

- [ ] **Step 2: Install on an emulator or tethered device.** Using `adb`:

```
adb install -r "Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android34.0/com.fabricationassistant.app.android-Signed.apk"
```

Adjust the APK filename if the build produces a different one (look in the output directory).

- [ ] **Step 3: Open a sample glTF or .fa model.** Use the SAF file picker, point at any model from `Data samples/` (transfer to the device first via `adb push` if needed).

- [ ] **Step 4: Inspect edges at MSAA = Off.** Open the preferences bottom sheet. In the "Anti-aliasing & Occlusion" section, select "Off". Close the sheet. Verify CAD edges on 45-degree features show visible stair-stepping aliasing.

- [ ] **Step 5: Inspect edges at MSAA = 4x.** Re-open the sheet. Select "4x". Close the sheet. Verify the same edges now look smooth (no visible stair-stepping). No app restart required.

- [ ] **Step 6: Verify stencil attachment** by adding a one-time debug log line inside `GlesViewportRenderer.OnDrawFrame`, immediately after `_msaaFbo.Bind()`. Insert temporarily (revert before final review):

```csharp
        // Temporary debug instrumentation - revert before final commit.
        #if DEBUG
        if (_msaaFbo is not null && _msaaFbo.FboHandle != 0)
        {
            int stencilName = 0;
            unsafe { _gl.GetFramebufferAttachmentParameter(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.StencilAttachment,
                FramebufferAttachmentParameterName.FramebufferAttachmentObjectName,
                &stencilName); }
            if (stencilName != 0)
                Android.Util.Log.Debug("FA.Renderer", "MSAA FBO has stencil attachment: rbo=" + stencilName);
        }
        #endif
```

Run the app once with `adb logcat -s FA.Renderer:D`. Expected: one log line per frame showing `MSAA FBO has stencil attachment: rbo=<nonzero>`.

- [ ] **Step 7: Remove the temporary debug log.** Delete the `#if DEBUG` block added in step 6. Verify build still succeeds.

---

## Task 8: Definition-of-done verification

**Files:** none.

- [ ] **Step 1: Verify desktop sources untouched.** Run:

```
git status --porcelain src/
```

Expected: empty output.

- [ ] **Step 2: Verify build at Release also works** (sanity check, in case Debug-only conditional compilation hides an issue):

```
cd "Android"
.\tools\build.ps1 -Configuration Release
```

Expected: build succeeds.

- [ ] **Step 3: Cross-check the spec section 5.4 definition-of-done.** Walk through each numbered item:
  1. `Android/tools/build.ps1 -Configuration Debug` returns 0 - confirmed by Task 7 step 1.
  2. APK installs - confirmed by Task 7 step 2.
  3. MSAA = 4x removes stair-stepping; MSAA = Off shows it - confirmed by Task 7 steps 4-5.
  4. Toggling MSAA in the sheet applies on close, no app restart - confirmed by Task 7 step 5.
  5. `git status --porcelain "src/"` empty - confirmed by step 1 above.

- [ ] **Step 4: Leave changes unstaged.** Do NOT run `git add` or `git commit` anywhere in this plan. The user merges parallel refactor branches manually; auto-staging by an agent is forbidden by standing instruction.
