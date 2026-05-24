# Fabrication Assistant - Android

Android-side projects for the Fabrication Assistant viewer. See:
- `docs/superpowers/specs/2026-05-23-android-app-design.md` for the design
- `docs/superpowers/plans/` for implementation plans

## Build

```powershell
.\tools\build.ps1 -Configuration Debug
```

APK outputs to `src/FabricationAssistant.App.Android/bin/Debug/com.fabricationassistant.android-Signed.apk`.

## Install (after building)

```powershell
adb install -r src/FabricationAssistant.App.Android/bin/Debug/com.fabricationassistant.android-Signed.apk
adb shell am start -n com.fabricationassistant.android/com.fabricationassistant.android.MainActivity
```

## Project layout

This folder contains a separate solution (`FabricationAssistant.Android.sln`) isolated from the root `FabricationAssistant.sln`. Android-side projects compile-link existing C# sources from `../src/` via shim projects (`*.Android.csproj`) without modifying the originals.

## Plan-1 execution notes

Deviations from the original Plan-1 spec, in effect:

- **TFM is `net8.0-android34.0`, not `net8.0-android33.0`.** The installed .NET Android workload (`android 35.0.105/9.0.100`) rejects API 33 with `NETSDK1140`. API 34 is the conservative supported floor.
- **`Core.Android/AndroidPlatformExclusions.targets` excludes** `FabricationAssistant.Core/Runtime/FabricationAssistantPaths.cs`. The Import.Gltf shim additionally excludes four files (`FaImportService.cs`, `FaPackageCacheStore.cs`, `FaPackageStorageOptions.cs`, `GltfImportService.cs`) that reference path APIs the bootstrap does not yet implement. Plan 2 extends `FabricationAssistantPathsBootstrap` with the missing surface and un-excludes those.
- **`tools/build.ps1` auto-sets `ANDROID_HOME` and `JAVA_HOME`** if they are not already in env. ANDROID_HOME defaults to `%LOCALAPPDATA%\Android\Sdk`. JAVA_HOME is redirected to a JDK 17 install under `%LOCALAPPDATA%\JDK17` if present, because JDK 21 produces noisy `XA0033` warnings (its version string `21+35-LTS-2513` confuses the workload's `ValidateJavaVersion` task).
- **`dotnet sln add` is unreliable on this system** (throws `AnyCPU key already added`). The `.sln` is edited manually for each new project: add a `Project(...)/EndProject` block AND four `ProjectConfigurationPlatforms` lines per project GUID.
- **Renderer uses `Silk.NET.OpenGLES` 2.22.0**, not OpenTK ES bindings. Silk.NET is more stable on .NET-for-Android.
- **Test project (`*.Android.Tests`) targets plain `net8.0`** (not `net8.0-android`) so it runs locally via `dotnet test` without an emulator. It has its own `Directory.Build.props` to override the surrounding `net8.0-android34.0`.

## Plan-2B execution notes

In-process Draco decoding via a P/Invoke wrapper around Google's `draco` C++
library, bundled per-ABI inside the APK. Implementation lives in
`src/FabricationAssistant.Draco.Android/`. Desktop `../src/` sources remain
read-only - `DracoDecodingGltfImportService` is a Decorator over the
unmodified `GltfImportService`.

- **NDK:** `26.3.11579264` (NDK r26d LTS), installed via
  `cmdline-tools/latest/bin/sdkmanager.bat`. Auto-discovered by
  `tools/build-libdraco.ps1` (newest version under
  `%LOCALAPPDATA%\Android\Sdk\ndk\` if `-NdkPath` is not specified).
- **Draco tag:** `1.5.7` (cloned to `deps/draco/`, gitignored).
- **Target ABIs:** `arm64-v8a` (device) and `x86_64` (emulator). Output is
  copied to `deps/prebuilt/<ABI>/libdraco_native.so` (~1.3 MB stripped).
- **Visibility:** Draco's internals are intentionally hidden via
  `-fvisibility=hidden`; the seven `extern "C"` entry points carry
  `__attribute__((visibility("default")))` (the `DRACO_NATIVE_EXPORT` macro
  in `native/draco_native.cpp`) so the JNI runtime can resolve them via
  `dlsym`. Without the visibility attribute the symbols are not in `.dynsym`
  and the runtime logs
  `monodroid-assembly: Symbol 'draco_decode_buffer_to_mesh' not found`.
- **CMake link target:** `draco::draco` (the cross-platform alias). Linking
  against bare `draco` works only when `BUILD_SHARED_LIBS=ON` on MSVC; on
  Android with `BUILD_SHARED_LIBS=OFF` the produced target is `draco_static`.
- **Include path:** Draco generates `draco/draco_features.h` into
  `${CMAKE_BINARY_DIR}/draco/` (the top-level binary dir, not the
  sub-project's). Both paths must be on the include list.
- **build-libdraco.ps1 stderr handling:** PowerShell 5.1 wraps CMake's
  deprecation-warning lines as `NativeCommandError` records that terminate
  scripts running under `$ErrorActionPreference = 'Stop'`. The script
  switches to `'Continue'` and relies on explicit `$LASTEXITCODE` checks.
- **Auto-trigger:** `tools/build.ps1` invokes `build-libdraco.ps1` whenever
  either ABI's `.so` is missing under `deps/prebuilt/` (after the
  ANDROID_HOME / JAVA_HOME setup, before `dotnet restore`).
- **Decorator wiring:** `ImportPipeline` constructs a single
  `DracoDecodingGltfImportService` wrapping `GltfImportService` and uses it
  both directly for `.glb`/`.gltf` and as the inner service passed to
  `FaImportService`, so `.fa` packages with Draco-encoded inner GLBs flow
  through the decoder identically.
- **Transcoder scope:** `DracoGltfTranscoder.Transcode` reads a Draco GLB,
  decodes each `KHR_draco_mesh_compression` primitive in-process, appends
  fresh non-Draco bufferViews+accessors, rewrites the primitive's attribute
  references, and writes a new GLB. The new GLB's single binary chunk
  contains only the decoded data; this matches the `.fa`-embedded GLB shape
  (Draco geometry, no other binary content). Files that mix Draco with
  embedded textures in the same binary chunk are outside the MVP scope.

### Diagnostics

Confirm the .so symbols are exported (after a clean native rebuild):

```powershell
$ndkBin = "$env:LOCALAPPDATA\Android\Sdk\ndk\26.3.11579264\toolchains\llvm\prebuilt\windows-x86_64\bin"
& "$ndkBin\llvm-nm.exe" --dynamic --defined-only "Android\deps\prebuilt\arm64-v8a\libdraco_native.so" | Select-String draco_
```

Should list all six `draco_*` entry points with `T` (text) bindings.

### Verifying Draco import on the tablet

1. Install: `adb install -r Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk`
2. Launch: `adb shell monkey -p com.fabricationassistant.android -c android.intent.category.LAUNCHER 1`
3. Tap "Open" -> system file picker -> pick a `.fa` package or Draco-encoded `.glb`.
4. Watch logs: `adb logcat -s "FabricationAssistant.Draco.Android:V" "AndroidRuntime:E"`
5. Expected: model renders. No `Symbol '... ' not found` warnings.

## Plan-2D execution notes

Tablet-first UI shell that mirrors the desktop `MainWindow.xaml` structure
(spec section 9.2): top AppBar with chevron toggle, left NavigationRail
with icon buttons, center viewport container, right collapsible Properties
panel, bottom action toolbar. Phone form factor (via `layout-port/`)
collapses the rail into a left drawer behind a hamburger icon, and stacks
the properties panel as a bottom region.

- **Tokens-only layouts.** All sizes and paddings live in
  `res/values/dimens.xml` under `fa_*` names (e.g. `fa_app_bar_height`,
  `fa_nav_rail_width`, `fa_space_l`). Text styling lives in
  `FA.TextAppearance.Caption / Body / Subtitle / Title`. No inline
  `textSize` or `textColor` values in any layout. Mirrors the
  desktop's `Resources/Tokens.xaml` discipline.
- **Color palette unchanged** from Plan 1: `@color/fa_*` and
  `@color/md_theme_*` already match `Theme.Dark.xaml`. All new layouts
  and styles pull exclusively from those tokens.
- **Icon language.** 9 vector drawables under `res/drawable/ic_*.xml`,
  modeled after Material Symbols Outlined: `ic_open_file`, `ic_recent`,
  `ic_fit`, `ic_settings`, `ic_section`, `ic_measure`, `ic_view_cube`,
  `ic_chevron_right`, `ic_menu`. Each tinted via
  `?attr/colorControlNormal` so dark/light theming applies naturally.
- **Component overrides.** `FA.Toolbar`, `FA.IconButton` extend the
  Material 3 base styles with desktop-matched heights, backgrounds, and
  ripple tints (`fa_accent_500`). The toolbar style is applied globally
  via `toolbarStyle` in the root theme.
- **Tablet layout** (`layout/activity_main.xml`): single
  ConstraintLayout root with 5 regions - top AppBar (with right-aligned
  chevron toggle that hides/shows the properties panel), left NavRail
  (80 dp wide, 4 buttons: open / recent / fit / settings; settings pinned
  bottom via a `Space` weighted spacer), viewport (fills remaining), right
  Properties panel (320 dp wide, includes `view_properties_panel.xml`),
  bottom toolbar (3 placeholder tool icons: viewcube / section / measure).
  Hairline `MaterialDivider`s separate the rail and panel from the viewport.
- **Phone layout** (`layout-port/activity_main.xml`): `DrawerLayout`
  wraps a ConstraintLayout. The toolbar shows `ic_menu` as
  `app:navigationIcon` opening the drawer. The drawer's contents are the
  same 4 nav items as `Widget.Material3.Button.TextButton` rows. The
  properties chevron lives at the right end of the bottom bar. Same
  resource ids as the tablet layout so MainActivity wires both with
  null-tolerant `FindViewById<MaterialButton>(...)?.Click += ...` calls.
- **DrawerLayout package.** Pulled in transitively by
  `Xamarin.AndroidX.AppCompat 1.7.0.5` -> `Xamarin.AndroidX.DrawerLayout
  1.2.0.15`. No explicit reference needed; an attempted explicit
  `1.2.0.13` reference caused a `NU1605` package-downgrade error.
- **Nav-rail tint selector.** `res/color/fa_navrail_tint.xml` provides
  the selected/pressed/default color states for `itemIconTint`/`itemTextColor`
  on the `FA.NavigationRail` style (reserved for a future migration to
  the actual `NavigationRailView` widget; the current layout uses a
  plain `LinearLayout` of `MaterialButton`s to avoid version coupling).

### Verification notes

The layout is verified via `adb shell uiautomator dump`, which on this
Samsung tablet reports every expected view id (topAppBar, navRail,
navOpenButton, navRecentButton, navFitButton, navSettingsButton,
viewportContainer, propertiesPanel, propertiesTitle, propertiesEmptyState,
bottomAppBar, toolViewCube, toolSection, toolMeasure, propertiesToggle)
with sensible bounds. `adb shell screencap -p` returns a solid black PNG
on this device - likely a Samsung security restriction or DeX/freeform
artifact - so visual verification must happen on the device itself.

## Plan-2C execution notes

Touch input wired through a ported `ViewportTouchGestureRecognizer`. The
desktop recognizer at `src/FabricationAssistant.App/Services/ViewportTouchGestureRecognizer.cs`
is unchanged; the Android port substitutes `System.Windows.Point` and
`System.Windows.Vector` for local `Point2D` / `Vector2D` value structs
living next to the recognizer in
`src/FabricationAssistant.Input.Gestures.Android/`.

- **Pipeline:** `View.IOnTouchListener.OnTouch(MotionEvent)` ->
  `AndroidPointerSource.OnTouch` -> per-pointer historical + current
  sample translation through `ViewportTouchGestureRecognizer` ->
  `GestureRecognized` event -> `ViewportInteractionAdapter.OnGesture` ->
  `CameraState.OrbitAroundPoint` / `Pan` / `DollyZoomAroundPivot` /
  `FitToBox` -> `ViewportSurfaceView.RequestRender()`.
- **DIP scaling:** `MotionEvent` X/Y are physical pixels. The pointer
  source divides by `Resources.DisplayMetrics.Density` so the recognizer's
  8 / 6 / 30 DIP thresholds feel consistent across density profiles.
- **LongPress under WhenDirty:** `Rendermode.WhenDirty` means no per-frame
  callback is available to drive the recognizer's `Tick`. The pointer
  source schedules a one-shot `Handler.PostDelayed(500 ms)` at each
  first-finger PointerDown and calls `Tick(now)` from there.
- **Cancel:** added a new `ViewportTouchGestureRecognizer.Cancel(time)`
  method (not present on the desktop recognizer) so
  `MotionEventActions.Cancel` releases an in-flight Orbit / PanZoom
  cleanly instead of leaving the state machine stuck.
- **Pivot:** the adapter uses `scene.Bounds.Center` as the orbit / pan /
  zoom pivot. Proper pivot picking (`ViewportPivotPicker`) is deferred
  to a later plan along with selection.
- **DoubleTap:** maps to `CameraState.FitToBox(scene.Bounds, aspect)`.
- **Tap and LongPress** are consumed silently for now; the events are
  emitted on the `GestureRecognized` channel for hosts that want to
  observe them, but the adapter takes no camera action. Selection
  integration is a later plan.
- **Sensitivity:** orbit at `0.005 rad/DIP`, pan at `distance * 0.002`
  (perspective) or `distance * 0.002` (orthographic fallback - the
  proper ortho `OrthoWidth / viewportWidth` ratio lands when the host
  passes a viewport-width accessor). Pinch zoom calls
  `DollyZoomAroundPivot(pivot, pinchScale)` directly; a 0.005 deadband
  around `scale == 1` keeps near-still two-finger gestures from
  compounding into drift.
- **Touch listener:** `MainActivity.OnCreate` installs a small
  `Java.Lang.Object`-derived `TouchProxy` as the
  `View.IOnTouchListener` on `ViewportSurfaceView` so JNI marshalling
  works (a plain C# class isn't enough).
- **Test project link:** `App.Android.Tests.csproj` adds
  `<Compile Include>` for `Point2D.cs`, `Vector2D.cs`,
  `TouchGestureEvent.cs`, and `ViewportTouchGestureRecognizer.cs` so the
  desktop test suite ports directly without an emulator. The host `net8.0`
  target keeps `dotnet test` emulator-free. 21 tests pass (20 ported
  recognizer tests + 1 prior smoke test) plus 4 new
  `AndroidPointerSourceTests` covering `Cancel` semantics.

## Plan-2E execution notes

Tap-to-select wired end-to-end via an offscreen R32UI pick FBO. A single tap
on the viewport runs an offscreen render of every mesh keyed by its 1-based
`MeshIndex`, reads back the pixel at the tap location, and highlights the
matching mesh in the next frame.

- **Mesh identity:** `GpuMesh.MeshIndex` (1-based; 0 reserved for no-hit) is
  assigned by `SceneUploader` from the mesh's position in
  `DocumentDto.Meshes`. The 1-based scheme lets the pick FBO use a `glClear`
  of 0 as the "no hit" sentinel.
- **Pick shaders:** `Shaders/pick.gles.vert` is the bare position-only
  vertex shader; `Shaders/pick.gles.frag` writes `uniform uint uMeshIndex`
  to a `layout(location = 0) out uint fragId` (requires `#version 310 es`
  and `precision highp int;`).
- **Pick FBO:** `GlesPickRenderer` owns a `GL_R32UI` color texture +
  `GL_DEPTH_COMPONENT24` depth renderbuffer. `Resize` recreates both when
  the surface size changes. `GlesViewportRenderer.OnSurfaceChanged` calls
  `Resize` so the pick FBO tracks the main viewport.
- **Pick coordinate transform:** Android tap coords are top-down,
  `glReadPixels` is bottom-up - the renderer flips: `glY = height - 1 - y`.
- **Highlight uniform:** `mesh.gles.frag` adds `uMeshIndex` (per-draw,
  `int`) and `uSelectedMeshIndex` (per-frame, `int`). When the two match
  and the selection is non-zero, the fragment mixes the lit color toward
  an orange highlight `(1.0, 0.62, 0.20)` at 45% with a 10% emissive boost.
- **Async pick flow:** `ViewportSurfaceView.PickAsync(x, y, callback)`
  queues a GL-thread command to invoke `renderer.Pick(x, y)`, then posts
  the int? result to the main looper via a `Handler` so callers can update
  UI-thread state.
- **Tap wiring:** `MainActivity` adds a second subscription to
  `AndroidPointerSource.GestureRecognized` alongside the camera adapter.
  The selection handler filters for `TouchGestureKind.Tap`, converts the
  DIP position back to pixels via `Resources.DisplayMetrics.Density`, and
  calls `PickAsync`. The callback sets `renderer.SelectedMeshIndex`,
  requests a render, and updates the Properties-panel empty-state TextView
  to `"Mesh #<n>"` or back to "No selection".
- **`Core.SelectionState` integration deferred.** The state class works in
  terms of node IDs, while the pick FBO returns mesh indices. A later plan
  will resolve mesh-index -> SceneNode -> SelectionState.SelectedNodeId
  along with multi-select on long-press.

## Plan-2H execution notes

First Settings popup ships, plus the easy half of the rendering polish:
MSAA. Settings live in SharedPreferences via `AppSettings.cs` and apply on
every `MainActivity.OnResume`.

- **MSAA via EGL config.** `Views/MultisampleConfigChooser.cs` is a custom
  `GLSurfaceView.IEGLConfigChooser` that tries 4x → 2x → off, capped by
  `AppSettings.MsaaSamples`. The `MaxSamples=4`/`2`/`0` constructor
  argument is honored so the user can downshift on slower devices. The
  EGL config is locked at surface-creation time, so MSAA changes only
  apply on the next activity start (documented in the Rendering card hint).
- **AppSettings.** Tiny static helper around `ISharedPreferences`. Six
  settings today: orbit/pan/zoom sensitivity floats (clamped 0.1..5.0),
  show-grid bool, show-selection-highlight bool, msaa-samples int (0/2/4),
  background-preset int (0=slate, 1=grey, 2=near-black). `Initialize`
  must be called once from `MainActivity.OnCreate` before any other code
  touches it - particularly the EGL config chooser, which reads
  `MsaaSamples` at surface init.
- **Preferences UI** (`activity_preferences.xml` +
  `PreferencesActivity.cs`). ConstraintLayout with a top
  `MaterialToolbar` (back arrow finishes the activity), then a
  `ScrollView` of `MaterialCardView` sections - one per desktop tab we
  care about right now (Viewport / Navigation / Rendering). Each control
  binds to AppSettings via lambdas. SeekBar instead of Material Slider
  (the Material Slider binding's listener interface is brittle across
  versions; SeekBar's `ProgressChanged` event has been stable for years).
  SeekBar max=49 -> sensitivity = 0.1 + progress * 0.1.
- **Renderer + adapter ack settings.** `GlesViewportRenderer` gained
  `ShowGrid`, `HighlightSelection`, `ClearColor` properties.
  `ViewportInteractionAdapter` gained `OrbitSensitivityMultiplier`,
  `PanSensitivityMultiplier`, `ZoomSensitivityMultiplier` (the last
  applied as a power on the pinch ratio so it stretches/compresses
  intensity without changing direction). `MainActivity.OnResume` reads
  AppSettings and writes through to both - returning from
  PreferencesActivity applies changes the moment the user gets back.
- **Gear icon wiring.** `navSettingsButton.Click` launches
  `PreferencesActivity` via a plain `Intent`. Fully-qualified
  `global::Android.Content.Intent` because the project namespace
  `FabricationAssistant.App.Android` would otherwise mask the `Android`
  root namespace.

### Deferred to follow-up plans

The big-ticket rendering items from spec section 6.2 are NOT in this
build. Scoping them honestly:

- **SSAO** (depth pre-pass + normal-depth FBO + ssao raw + separable
  blur ping-pong). Four shaders, two FBOs, careful framebuffer state.
  Plan 2I.
- **Edge ribbon** (`edge.ribbon.gles.vert/.frag`). Per-vertex offset +
  neighbour index, ribbon expansion in screen space, crease test. Needs
  neighbour data prepared at upload time. Plan 2J.
- **Selection outline post-process** (replace the inline color
  highlight with a real silhouette outline pass over the selected mesh).
  Plan 2K.
- **Remaining desktop preferences tabs** (Import, VR, Input, Voice, AI,
  Clash Review). Most are subsystems Android doesn't have yet; their
  preferences will land alongside the features themselves.

Build clean (0 errors). APK installs and launches without exceptions or
shader-compile errors. Tap the gear icon in the left nav rail to open
Settings.

## Plan-2I execution notes (phases A–G all landed)

Plan: `Android/docs/superpowers/plans/2026-05-24-android-render-pipeline-and-settings.md`.

### Phase A — Settings popup as BottomSheet (done)

- `PreferencesActivity.cs` removed.
- `PreferencesBottomSheet.cs` is a `BottomSheetDialogFragment` that floats over `MainActivity`. The viewport stays visible behind a translucent scrim; the sheet defaults to 80 % peek and is freely draggable.
- `MainActivity` invokes `new PreferencesBottomSheet { OnSettingsChanged = ApplySettingsToScene }.Show(SupportFragmentManager, "prefs")`.

### Phase B — Full settings model + comprehensive popup UI (done)

- `Rendering.Gles/SceneAppearance.cs` — single struct that bundles every per-frame appearance value (mode, helpers, scene colors, edges, clay, lighting, AO, contour, selection). `CreateDefault()` mirrors the desktop `SceneAppearanceViewModel` defaults.
- `App.Android/AppSettings.cs` — every desktop Viewport + Rendering setting persisted via `SharedPreferences`. ~50 properties. `Apply(ref SceneAppearance)` reads them all in one call.
- `PreferencesBottomSheet` builds its UI **programmatically** instead of via XML — the surface is large and very repetitive (label + SeekBar + value text), so a small builder API keeps it under ~300 lines. Sections:
  - **Render Mode** (Shaded+Edges / Shaded / Wireframe / Clay).
  - **Camera & Helpers** (Show Grid, Push to model min, Auto grid spacing, Spacing mm, Line thickness, Grid color RGB, Show Axes, Show ViewCube, Projection).
  - **Scene Colors** (Background RGB, Surface RGB, Surface opacity).
  - **CAD Edges** (Enable, Color, Width, Feature Angle, Coplanar Tol, Weld Tol, Silhouettes, Depth bias, Offset F/U).
  - **Clay Render** (Clay surface RGB, Clay background RGB).
  - **Lighting** (Base Lift, Ambient, Headlight, Key, Fill, Bounce, Hemisphere, Specular Strength + Power).
  - **Anti-aliasing & Occlusion** (MSAA Off/2x/4x, Contour Strength, Contour Falloff, AO enable, AO Radius, AO Bias, AO Intensity, AO Blur Passes).
  - **Selection** (Highlight body, Outline body, Outline color, Outline thickness).
  - **Navigation** (Orbit / Pan / Pinch-zoom sensitivity).
- Every control writes through to `AppSettings` and fires the `OnSettingsChanged` callback so `MainActivity.ApplySettingsToScene` updates the renderer live (no app restart, except MSAA which is locked to the EGL config at surface creation).

### Phase C — Multi-light shading + Clay shader (done)

- `Shaders/mesh.gles.frag` rewritten. Lights: hemisphere (sky/ground), key (downward-front), fill (broader opposite-side), headlight (eye-aligned), bounce (ground-up), plus ambient and specular (Blinn-Phong). Each is gated by its own uniform strength so the Lighting sliders have visible effect.
- `Shaders/mesh.clay.gles.frag` — identical lighting except specular = 0 and the surface color is forced to `uClaySurfaceColor`. Used when `Appearance.Mode == Clay`.
- `GlesViewportRenderer` compiles both programs at surface creation and switches per frame based on `Appearance.Mode`. In Clay mode the background also flips to `Appearance.ClayBackgroundColor`.

### Phase D — CAD edges (done)

- `CadEdgeBuilder.Build(positions, indices, featureAngleDeg)` walks every triangle, builds an `(int,int) -> (tri0, tri1, count)` map of undirected edges, then emits edges where the dihedral angle between the two adjacent face normals exceeds the feature threshold. Boundary (1 triangle) and non-manifold (≥3 triangles) edges are always emitted.
- `GpuMesh.UploadEdges(ReadOnlySpan<uint>)` creates a second VAO that reuses the position VBO with attribute 0 only, plus its own EBO of line-endpoint pairs for `GL_LINES`.
- `SceneUploader` runs the builder per mesh with the desktop default feature angle (25°) and uploads the edges immediately. Changing the feature-angle slider live requires a re-load for now; live rebuild is deferred.
- `Shaders/edge.ribbon.gles.vert` + `edge.ribbon.gles.frag` — position-only shader that writes `uEdgeColor`. The mesh pass enables `GL_POLYGON_OFFSET_FILL` with `SurfaceOffsetFactor` / `SurfaceOffsetUnits` so edges sit cleanly above the surface without z-fighting.

### Phase E — SSAO + contact shadows (done)

- `Shaders/normal_depth.gles.vert/.frag` writes view-space normal + linear depth into a single `RGBA16F` color attachment. `GlesNormalDepthRenderer` owns that FBO + a D24 depth renderbuffer and renders the scene before the main mesh pass.
- `Shaders/ssao.gles.frag` samples it via a 16-sample cosine-weighted hemisphere kernel (deterministic, pre-built in `GlesSsaoRenderer.BuildKernel`). Per-pixel TBN basis is jittered to spread the noise; the blur cleans it up.
- `Shaders/ssao_blur.gles.frag` is a 5-tap binomial (1-4-6-4-1) separable Gaussian. `GlesSsaoRenderer` ping-pongs between two R8 FBOs for `AoBlurPasses × 2` iterations (horizontal then vertical).
- `mesh.gles.frag` samples `uAoTexture` at `gl_FragCoord.xy / uViewportSize` and lerps `lit -> lit * ao` by `uAoStrength`. When SSAO is off the renderer binds a 1×1 white texture so the multiply is identity. SSAO pre-pass is skipped in Clay and Wireframe modes.

### Phase F — Wireframe render mode (done)

- `Appearance.Mode == Wireframe`: main mesh draw is skipped, edge pass runs unconditionally (uses the surface color so wires read against the background), polygon-offset block is suppressed.
- Clay mode (from Phase C) keeps using `mesh.clay.gles.frag` + the clay background.

### Phase G — Selection outline post-process (done)

- `Shaders/mask.gles.frag` writes 1.0 to an R8 mask FBO (`GlesOutlineRenderer`). Only the selected mesh is drawn into the mask, reusing `pick.gles.vert` as the position-only vertex shader.
- `Shaders/outline.gles.frag` runs a 4-tap cardinal Sobel scaled by `OutlineThicknessPx`, alpha-blends the outline color over the default framebuffer.
- The inline color highlight in `mesh.gles.frag` stays as a fallback when `OutlineEnabled == false`.

### Render pipeline order

```
clear default FBO
 1. (optional) normal-depth pre-pass -> RGBA16F FBO
 2. (optional) SSAO + N×2 blur passes -> R8 ping-pong FBOs
 3. ground grid (if ShowGrid)
 4. mesh pass (chooses mesh / clay program by mode; samples AO texture)
 5. edge pass (if EdgesEnabled or Wireframe mode)
 6. outline composite (if OutlineEnabled and a mesh is selected)
```

### What works live in this build

- **All Lighting sliders** — Base Lift / Ambient / Headlight / Key / Fill / Bounce / Hemisphere / Specular Strength + Power. Visible per-stroke.
- **Render Mode** — Shaded+Edges, Shaded, Wireframe, Clay all switch live.
- **CAD Edges** — Enable toggle, Color RGB, Surface offsets all visible. Feature angle requires re-load to rebuild edge geometry.
- **Anti-aliasing & Occlusion** — SSAO enable, Radius / Bias / Intensity / Blur Passes all visible in crevices.
- **Selection** — Outline toggle, color, thickness all visible when a mesh is picked.
- **Scene Colors** — Background RGB, Clay surface + background RGB, Surface color all live.
- **Navigation** — same as Plan 2H.

### Deferred to follow-up plans

- True silhouette extraction (view-dependent edges on top of per-mesh dihedral features).
- Live edge rebuild when the feature-angle slider moves (rerun CadEdgeBuilder on the GL thread + re-upload the edge EBO per mesh).
- `glLineWidth > 1.0` is a no-op on tablet drivers; wider edges need true ribbon expansion via quad geometry.
- Real shadow maps (current "shadow" feel is entirely from SSAO contact shadows).
- ViewCube / pivot / axes overlays.
- Custom grid spacing / shifting controls — backend renderer doesn't read those yet.

## Manual verification step (cannot be automated without adb on PATH)

Once you have `adb` on PATH and a device or emulator running:

```powershell
adb install -r src/FabricationAssistant.App.Android/bin/Debug/com.fabricationassistant.android-Signed.apk
adb shell am start -n com.fabricationassistant.android/com.fabricationassistant.android.MainActivity
```

Expected: dark-gray surface fills the viewport area; rotation does not crash; backgrounding/foregrounding does not crash.

To locate adb (if Android Studio is installed):
```powershell
$env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools"
```
