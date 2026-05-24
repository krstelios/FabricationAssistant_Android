# Fabrication Assistant Android App - Design

**Date:** 2026-05-23
**Status:** Approved design, Plan 1 + Plan 2A complete, Plan 2B (Draco) in progress

> **Update 2026-05-23 (post Plan 2A device testing):** Draco-compressed glTF was originally flagged as out of MVP scope (Section 15). It is now moved INTO scope as Plan 2B because the user's primary file format (`.fa` packages) embeds Draco-encoded GLBs. The architectural decision: implement Draco decode in-process via a P/Invoke to a small native wrapper around libdraco built for Android NDK, exposed through a new `FabricationAssistant.Draco.Android` project. The desktop sources (including the `Process.Start("FabricationAssistant.DracoDecode.exe")` code path in `GltfImportService`) are NOT modified — instead, a Decorator/wrapper `ISceneImportService` pre-decodes Draco-encoded files before delegating to the unmodified `GltfImportService`.
**Scope:** First-version viewer MVP for tablet (primary) and phone (secondary) on Android, sharing the existing `FabricationAssistant.Core` and `FabricationAssistant.Import.Gltf` C# source without modifying any desktop sources.
**Reference inputs:**
- `docs/current-app-state-android-portability-audit.md` (1184 lines, dated 2026-05-09; the authoritative current-state document)
- This design, written 2026-05-23, building on the audit's recommendations

---

## 1. Foundations (locked by brainstorming session)

| Decision | Choice |
|---|---|
| Target form factor | Tablet primary (10-13"), phone secondary (5-7") |
| Initial feature scope | Viewer-only MVP - load glTF/GLB + `.fa`, orbit/pan/zoom, selection, sections, measurement |
| Technology stack | .NET 8 for Android (`net8.0-android`) + native AndroidX views |
| Graphics API | OpenGL ES 3.1 (audit-recommended baseline; Android 5.0+) |
| Code reuse strategy | Shim projects that compile-link existing C# sources from `../src/`; no edits to desktop source files |
| Solution location | New `Android/` folder, isolated from root `FabricationAssistant.sln` |

Excluded from MVP: STEP (OCCT), Draco-compressed glTF, AI/MCP chat, speech (STT/TTS), drawings (PDF.js), markup, BOM workspace, VR/OpenXR, SpaceMouse.

---

## 2. Goals and non-goals

**Goals**
- A native Android app that opens glTF/GLB and `.fa` packages from SAF (`content://`) sources and renders them with the same scene-graph fidelity the desktop produces.
- Orbit / pan / pinch-zoom that matches the desktop's `CameraState` math; touch input feeds the existing `ViewportTouchGestureRecognizer` state machine.
- Selection, sections, and measurement workflows reusing Core domain code unchanged.
- Tablet landscape as primary form factor; phone portrait/landscape layouts work but are secondary.
- Zero edits to existing desktop `.cs` / `.csproj` source files. New files only, all under `Android/`.

**Non-goals**
- Feature parity with the desktop app.
- STEP import. Requires NDK OCCT or server transcoder; deferred per audit Section 17.7.
- Draco import. Requires in-process NDK Draco; deferred.
- AI/MCP, speech, drawings, markup, BOM, VR. All deferred.
- Save / export workflows (read-only viewer for MVP).
- Google Play distribution. Side-load only for MVP.

---

## 3. Architecture overview

```
                     ┌────────────────────────────────────────┐
                     │  Android app (net8.0-android33.0)      │
                     │                                        │
                     │  MainActivity                          │
                     │   ├─ ViewportSurfaceView : GLSurfaceView
                     │   ├─ AppBar / Drawer / BottomSheets    │
                     │   └─ DI root (Microsoft.Extensions.DI) │
                     │                                        │
                     │  ┌──────────────────────────────────┐  │
                     │  │ Rendering.Gles (new, local)      │  │
                     │  │  • GlesViewportRenderer          │  │
                     │  │  • GlesSceneRenderer             │  │
                     │  │  • GlesPickRenderer              │  │
                     │  │  • GLES 3.1 shaders (Shaders/)   │  │
                     │  └──────────────────────────────────┘  │
                     │                                        │
                     │  ┌──────────────────────────────────┐  │
                     │  │ Platform.Android (new, local)    │  │
                     │  │  • AndroidDispatcher             │  │
                     │  │  • AndroidPlatformPaths          │  │
                     │  │  • AndroidFileDialogService      │  │
                     │  │  • AndroidApplicationSettings    │  │
                     │  │  • AndroidClipboardService       │  │
                     │  └──────────────────────────────────┘  │
                     │                                        │
                     │  ┌──────────────────────────────────┐  │
                     │  │ Input.Gestures.Android (new)     │  │
                     │  │  • AndroidPointerSource          │  │
                     │  │  • ViewportInteractionAdapter    │  │
                     │  └──────────────────────────────────┘  │
                     │                                        │
                     │  references ▼                          │
                     │                                        │
                     │  ┌──────────────────────────────────┐  │
                     │  │ Core.Android (shim)              │  │
                     │  │  links: ../src/Core/**/*.cs      │  │
                     │  │  excludes: FabricationAssistant- │  │
                     │  │            Paths.cs              │  │
                     │  └──────────────────────────────────┘  │
                     │                                        │
                     │  ┌──────────────────────────────────┐  │
                     │  │ Import.Gltf.Android (shim)       │  │
                     │  │  links: ../src/Import.Gltf/**/*.cs│  │
                     │  └──────────────────────────────────┘  │
                     └────────────────────────────────────────┘
```

The Android solution lives entirely under `Android/`. Existing desktop projects are referenced **only** through file-globbed `<Compile Include>` patterns in the shim projects' `.csproj` files. The root `FabricationAssistant.sln`, `Directory.Build.props`, and every existing `.cs` / `.csproj` remain bit-identical.

---

## 4. Solution layout

```
Android/
├── FabricationAssistant.Android.sln
├── Directory.Build.props                 # Scopes net8.0-android to this folder; shields from root props
├── global.json                           # SDK pin matching root (.NET 8.0.4xx)
├── NuGet.config                          # Mirrors root + adds maven (Android packages)
├── README.md                             # Build/run instructions
├── docs/
│   └── superpowers/
│       ├── specs/2026-05-23-android-app-design.md   # this file
│       └── plans/                                   # populated by writing-plans skill
├── src/
│   ├── FabricationAssistant.Core.Android/
│   │   ├── FabricationAssistant.Core.Android.csproj
│   │   └── AndroidPlatformExclusions.targets        # Files to exclude from linking
│   ├── FabricationAssistant.Import.Gltf.Android/
│   │   └── FabricationAssistant.Import.Gltf.Android.csproj
│   ├── FabricationAssistant.Platform.Android/
│   │   ├── FabricationAssistant.Platform.Android.csproj
│   │   ├── AndroidDispatcher.cs
│   │   ├── AndroidPlatformPaths.cs
│   │   ├── AndroidFileDialogService.cs
│   │   ├── AndroidApplicationSettingsService.cs
│   │   └── AndroidClipboardService.cs
│   ├── FabricationAssistant.Rendering.Gles/
│   │   ├── FabricationAssistant.Rendering.Gles.csproj
│   │   ├── GlesViewportRenderer.cs
│   │   ├── GlesSceneRenderer.cs
│   │   ├── GlesPickRenderer.cs
│   │   ├── GlesAmbientOcclusionRenderer.cs
│   │   ├── GlesOutlineRenderer.cs
│   │   ├── GlesMsaaFramebuffer.cs
│   │   ├── GpuMesh.cs                               # ported, not linked
│   │   ├── ShaderProgram.cs                         # ported, not linked
│   │   ├── Overlays/
│   │   │   ├── GlesViewCubeOverlay.cs
│   │   │   ├── GlesGridOverlay.cs
│   │   │   ├── GlesAxisGizmoOverlay.cs
│   │   │   ├── GlesPivotOverlay.cs
│   │   │   ├── GlesSelectionBoundsOverlay.cs
│   │   │   ├── GlesMeasurementOverlay.cs
│   │   │   ├── GlesSectionPlaneOverlay.cs
│   │   │   ├── GlesSectionEdgeOverlay.cs
│   │   │   └── GlesSectionCapOverlay.cs
│   │   └── Shaders/
│   │       ├── mesh.gles.vert                       # #version 310 es
│   │       ├── mesh.gles.frag                       # section clip via discard
│   │       ├── edge.ribbon.gles.vert                # replaces edge.geom.glsl
│   │       ├── edge.ribbon.gles.frag
│   │       ├── pick.gles.vert
│   │       ├── pick.gles.frag                       # uint output
│   │       ├── ssao.gles.frag
│   │       ├── ssao_blur.gles.frag
│   │       ├── outline.gles.vert
│   │       ├── outline.gles.frag
│   │       ├── section_plane.gles.vert/.frag
│   │       ├── section_edge.gles.vert/.frag
│   │       ├── section_cap.gles.vert/.frag
│   │       ├── viewcube.gles.vert/.frag
│   │       ├── grid.gles.vert/.frag
│   │       ├── overlay.gles.vert/.frag
│   │       └── ...
│   ├── FabricationAssistant.Input.Gestures.Android/
│   │   ├── FabricationAssistant.Input.Gestures.Android.csproj
│   │   ├── AndroidPointerSource.cs
│   │   └── ViewportInteractionAdapter.cs
│   ├── FabricationAssistant.App.Android/
│   │   ├── FabricationAssistant.App.Android.csproj
│   │   ├── MainActivity.cs
│   │   ├── PreferencesActivity.cs
│   │   ├── AppServices.cs                           # DI composition root
│   │   ├── AndroidManifest.xml
│   │   ├── Views/
│   │   │   ├── ViewportSurfaceView.cs               # GLSurfaceView subclass
│   │   │   ├── ViewportContainerView.cs
│   │   │   ├── SceneTreeView.cs
│   │   │   ├── BottomToolbarView.cs
│   │   │   ├── PropertiesBottomSheet.cs
│   │   │   ├── StandardViewMenu.cs
│   │   │   └── BindingExtensions.cs
│   │   ├── Resources/
│   │   │   ├── layout/
│   │   │   │   ├── activity_main.xml
│   │   │   │   ├── activity_preferences.xml
│   │   │   │   ├── view_scene_tree_row.xml
│   │   │   │   └── view_properties_sheet.xml
│   │   │   ├── values/
│   │   │   │   ├── colors.xml                       # theme palette mirror
│   │   │   │   ├── styles.xml
│   │   │   │   ├── strings.xml
│   │   │   │   └── dimens.xml
│   │   │   ├── values-night/
│   │   │   │   └── colors.xml
│   │   │   ├── drawable/                            # vector drawables for toolbar
│   │   │   └── mipmap-anydpi-v26/                   # adaptive launcher icon
│   │   └── Assets/
│   │       └── (none for MVP)
│   └── FabricationAssistant.App.Android.Tests/
│       ├── FabricationAssistant.App.Android.Tests.csproj   # net8.0-android21.0 host
│       ├── CameraOrbitTests.cs
│       ├── GestureRecognizerTests.cs
│       ├── GltfImportTests.cs
│       ├── FaImportTests.cs
│       └── Fixtures/
└── tools/
    └── build.ps1                                    # `dotnet build` wrapper for the Android sln
```

### 4.1 The shim project pattern

Each shim's `.csproj` body:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace>FabricationAssistant.Core</RootNamespace>     <!-- preserved -->
    <AssemblyName>FabricationAssistant.Core</AssemblyName>       <!-- preserved -->
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="..\..\..\src\FabricationAssistant.Core\**\*.cs"
             Link="%(RecursiveDir)%(Filename)%(Extension)" />
    <Compile Remove="..\..\..\src\FabricationAssistant.Core\Runtime\FabricationAssistantPaths.cs" />
    <Compile Remove="..\..\..\src\FabricationAssistant.Core\obj\**\*.cs" />
    <Compile Remove="..\..\..\src\FabricationAssistant.Core\bin\**\*.cs" />
  </ItemGroup>
</Project>
```

`AndroidPlatformExclusions.targets` collects all `<Compile Remove>` entries so the exclusion list is auditable in one place.

### 4.2 `Android/Directory.Build.props`

Overrides the root props (which hardcode `net8.0-windows`, `win-x64`, `x64`):

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RuntimeIdentifier></RuntimeIdentifier>
    <Platforms>AnyCPU</Platforms>
    <Platform>AnyCPU</Platform>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  </PropertyGroup>
</Project>
```

Because MSBuild walks **up** from each `.csproj` and stops at the first `Directory.Build.props`, placing one inside `Android/` isolates the Android projects entirely from the root file.

---

## 5. Project responsibilities

### 5.1 `FabricationAssistant.Core.Android` (shim)
- Compile-links all of `src/FabricationAssistant.Core/**/*.cs` minus the explicitly-excluded files.
- Excluded files (initial): `Runtime/FabricationAssistantPaths.cs` (Windows-only `Environment.SpecialFolder`).
- No package references beyond what `net8.0-android` itself provides.
- Output assembly name `FabricationAssistant.Core` (same as desktop) so type identity is preserved end-to-end.

### 5.2 `FabricationAssistant.Import.Gltf.Android` (shim)
- Compile-links `src/FabricationAssistant.Import.Gltf/**/*.cs`.
- Package references (must be Android-compatible):
  - `SharpGLTF.Core` 1.0.6 (pure managed; verified Android-compatible)
  - `SharpZipLib` 1.4.x (pure managed)
  - `Microsoft.Data.Sqlite` 8.0.x (Android ABI in nuget; pulls `libe_sqlite3.so` automatically)
- Excluded files: none expected.

### 5.3 `FabricationAssistant.Platform.Android`
Implements platform interfaces consumed by Core / Import / Rendering. All types live here.

| Interface | Implementation | Backed by |
|---|---|---|
| `IDispatcher` | `AndroidDispatcher` | `Handler(Looper.MainLooper)` |
| `IPlatformPaths` | `AndroidPlatformPaths` | `Context.FilesDir`, `Context.CacheDir` |
| `IFileDialogService` | `AndroidFileDialogService` | `ACTION_OPEN_DOCUMENT` Intent + `ActivityResultLauncher` |
| `IClipboardService` | `AndroidClipboardService` | `ClipboardManager` |
| `IApplicationSettingsService` | `AndroidApplicationSettingsService` | `SharedPreferences` (single JSON-encoded key for structured settings) |

A small wrapper `FabricationAssistantPaths.Initialize(IPlatformPaths)` lives in this project (not in Core) and exposes the desktop-shaped static API for legacy Core code that still calls it. This is the only "static shim" in the project and is the explicit price of not editing desktop sources.

### 5.4 `FabricationAssistant.Rendering.Gles`
Houses the GLES 3.1 renderer.

Two categories of files:
- **Ported** (hand-translated from desktop, shaped similarly): `GlesViewportRenderer`, `GlesSceneRenderer`, `GlesPickRenderer`, `GlesAmbientOcclusionRenderer`, `GlesOutlineRenderer`, `GlesMsaaFramebuffer`, `GpuMesh`, `ShaderProgram`, all overlays.
- **Authored fresh**: GLES 3.1 shaders. Each is a `#version 310 es` rewrite of the desktop equivalent; the desktop GLSL is *reference*, not source.

Package references:
- `OpenTK.Graphics.ES31` (preferred) **or** `Silk.NET.OpenGLES` (fallback if the OpenTK ES bindings don't restore on .NET 8 Android cleanly — see R7).
- `SkiaSharp` 3.x for `SkiaSharp.Views.Android` to rasterize overlay text into textures.

This project does **not** reference `FabricationAssistant.Rendering.OpenTK`. The desktop renderer uses OpenTK 4.8 desktop bindings that are not portable. The audit's per-file portability classification (Section 7.5) informs *which patterns* port; the actual code is rewritten in this project.

### 5.5 `FabricationAssistant.Input.Gestures.Android`
Tiny project that bridges `MotionEvent` to `ViewportTouchGestureRecognizer` (linked from Core unchanged).

- `AndroidPointerSource.Translate(MotionEvent)` produces `PointerEvent` records keyed by pointer ID. Honours historical samples for fidelity.
- `ViewportInteractionAdapter` subscribes to recognizer events and translates them into `CameraState` and `SelectionState` calls.
- Density conversion: gesture thresholds in the recognizer are in pixels; we feed it `MotionEvent` coordinates scaled by `1 / DisplayMetrics.Density` so 8px stays "8 DIP" (roughly matching desktop DIPs).

### 5.6 `FabricationAssistant.App.Android`
The Activity, AndroidX views, ViewModels, and DI composition root.

- `MainActivity` builds the `IServiceProvider` in `OnCreate`, instantiates `MainViewModel`, hooks the `ViewportSurfaceView` to its renderer, wires touch through `AndroidPointerSource`, binds toolbar commands, restores camera state from `savedInstanceState` if present.
- View XML in `Resources/layout/` uses `androidx.constraintlayout.widget.ConstraintLayout` as root.
- Material Components 1.12 + AppCompat 1.7.

### 5.7 `FabricationAssistant.App.Android.Tests`
xUnit on `net8.0-android21.0`. Tests run via `dotnet test`. CI runs on an emulator only when GL-dependent tests exist; pure math / gesture / import tests run on a regular `net8.0` host build (because Core compiles for both).

---

## 6. Rendering pipeline (GLES 3.1)

### 6.1 Context lifecycle
- `ViewportSurfaceView : GLSurfaceView`
  - `SetEGLContextClientVersion(3)`
  - `SetEGLConfigChooser(8, 8, 8, 0, 24, 8)` (RGB8 + Depth24 + Stencil8)
  - `SetRenderer(new GlesRendererBridge(viewModel.RendererController))`
  - `RenderMode = Rendermode.WhenDirty`
- `GlesRendererBridge : Java.Lang.Object, GLSurfaceView.IRenderer` forwards `OnSurfaceCreated / OnSurfaceChanged / OnDrawFrame` to `GlesViewportRenderer`.
- When-dirty rendering matches the desktop `_needsRedraw` model. Camera changes, selection changes, scene mutations call `ViewportSurfaceView.RequestRender()`.

### 6.2 Per-frame pipeline

| Pass | Output | Notes |
|---|---|---|
| 1. Depth pre-pass | Depth32F | Optional; on by default for opaque-heavy scenes |
| 2. Normal-Depth FBO | RG16F color + Depth32F depth | Inputs to SSAO |
| 3. SSAO raw | R16F | `ssao.gles.frag` |
| 4. SSAO blur (separable) | R16F ping/pong | `ssao_blur.gles.frag` |
| 5. Main MSAA scene | RGBA8 multisample + Depth24Stencil8 multisample (samples user-clamped to `GL_MAX_SAMPLES`) | `mesh.gles.vert/.frag`; section clipping via `discard` |
| 6. Edges | Same MSAA target | `edge.ribbon.gles.vert/.frag` - vertex-only ribbon expansion |
| 7. Section overlays | Same MSAA target | `section_*.gles.*` |
| 8. Resolve MSAA → screen | RGBA8 | `glBlitFramebuffer` |
| 9. Overlays (ViewCube, Pivot, Axis, Grid, SelectionBounds, Measurement) | Screen | All use ribbon expansion for line thickness |
| 10. Pick | R32UI FBO + Depth24 (on demand) | Async path: PBO + `glFenceSync` |

### 6.3 Shader migration table

| Desktop GLSL | GLES 3.1 GLSL | Migration |
|---|---|---|
| `mesh.vert / .frag` | `mesh.gles.vert / .frag` | `gl_ClipDistance[]` → uniform `vec4 sectionPlanes[8]` + `discard` in FS |
| `mesh.instanced.vert` | `mesh.instanced.gles.vert` | Direct port |
| `mesh.aggregate.vert / .frag` | `mesh.aggregate.gles.vert / .frag` | Direct port; SSBO + indirect on devices that report `GL_ES_VERSION_3_1` strictly |
| `mesh.aggregate.plain.vert / .frag` | `mesh.aggregate.plain.gles.vert / .frag` | Drop `SNORM_INT_2_10_10_10_REV` packed normals; upload unpacked floats |
| `edge.vert + edge.geom + edge.frag` | `edge.ribbon.gles.vert / .frag` | **Rewrite**: per-vertex offset attribute + neighbour index forms screen-space ribbon |
| `glow.*` | `outline.gles.*` | Direct port |
| `ssao.*` | `ssao.gles.*` | Direct port |
| `surface_cull.comp` | `surface_cull.gles.comp` | Direct port (ES 3.1 supports compute) |
| `pick.*` | `pick.gles.*` | Integer output → `out uint FragId` |
| `section_*.* / viewcube.* / grid.* / overlay.* / normal_depth.*` | `*.gles.*` | Direct ports |

### 6.4 Threading
- All GL calls on the `GLSurfaceView` render thread.
- A `ConcurrentQueue<Action>` accepts state-mutation commands from the UI thread; `GlesViewportRenderer.OnDrawFrame` drains it at the top of each frame.
- `GlThreadGuard` (ported, not linked from desktop) verifies that GL calls happen on the captured render-thread ID.

### 6.5 Diagnostics
- `RenderDiagnostics`, `FrameRateCounter`, `GpuTimerQueryTracker` from desktop are ported as needed (the desktop ones are mostly platform-neutral but the GL function-call surface is from the desktop OpenTK package).
- `EXT_disjoint_timer_query` is checked at startup; absence disables per-pass GPU timing (CPU timing only).

### 6.6 Aggregate fast path
- Optional. Gated on a runtime feature probe: `GL_ES_VERSION_3_1 == 1` AND `GL_OES_*_indirect` if needed.
- Default off in MVP. First-frame budget targets the non-aggregate path. Enabling later is a 2-3 day spike.

---

## 7. Input and camera

### 7.1 Touch pipeline
```
View.OnTouchListener.OnTouch(MotionEvent)
   ▼  AndroidPointerSource.Translate(MotionEvent) — historic samples + density scaling
   ▼  ViewportTouchGestureRecognizer (linked from Core, unchanged)
   ▼  events: Tap / DoubleTap / LongPress / OrbitBegin·Delta·End / PanZoomBegin·Delta·End
   ▼  ViewportInteractionAdapter
   ▼  CameraState.Orbit / Pan / DollyZoomAroundPivot / FitToBox
   ▼  ViewportSurfaceView.RequestRender()
```

### 7.2 Gestures (desktop parity)
| Gesture | Action |
|---|---|
| 1-finger drag | Orbit (after 8 DIP threshold) |
| 2-finger drag | Pan |
| 2-finger pinch | Dolly-zoom around the pivot captured at gesture start |
| Tap | Selection (single click semantics) |
| Long-press (500 ms, < 6 DIP drift) | Enter multi-select mode; subsequent taps toggle |
| Double-tap (< 350 ms, < 30 DIP) | Fit to selection (falls back to Fit to All if nothing selected) |
| View-cube tap | Snap to standard view |

### 7.3 Camera state
- `CameraState`, `StandardView`, `MouseNavigationSettings`, `Math/*` all linked from Core unchanged.
- `DollyZoomAroundPivot` (audit Section 9.1) is the pinch-zoom math.
- Pivot picking at `OrbitBegin` / `PanZoomBegin` uses `ViewportPivotPicker.TryPickPivotFromScreen` (CPU AABB slab vs visible nodes) - linked from Core unchanged.

### 7.4 Selection
- Tap → `GlesPickRenderer.Pick(x, y)` (sync `glReadPixels(GL_RED_INTEGER, GL_UNSIGNED_INT)`) → `SelectionState.SetSelection(id)`.
- Long-press → multi-select mode chip in the toolbar; subsequent taps call `SelectionState.ToggleSelect(id)`.
- Selection visualization: `GlesOutlineRenderer` glow halo (ported from desktop `OutlineRenderer`).

### 7.5 Sections
- Section planes editable via a section-tool sheet: pick plane normal (X / Y / Z / Custom), drag a slider for offset.
- Section state lives in Core's `SectionService`; the Android UI just edits it.
- Shader path: 8-plane uniform array + fragment `discard` (no `gl_ClipDistance`).

### 7.6 Measurement
- Two-tap distance and three-tap angle workflows mirror desktop.
- `MeshRaycastAcceleration` (Core, linked unchanged) does the CPU triangle-precision hit.
- Visualization: `GlesMeasurementOverlay`.

### 7.7 What's not wired
- SpaceMouse: not referenced.
- Stylus pressure: detected (`MotionEvent.GetToolType()`) but ignored for MVP.
- Keyboard shortcuts (Ctrl+O, F): visible in toolbar; physical keyboard shortcuts deferred (Bluetooth keyboards work, but listener registration is plan-execution work).

---

## 8. File I/O

### 8.1 Open flow
```
"Open" button → AndroidFileDialogService.OpenAsync(filters)
              → startActivityForResult(ACTION_OPEN_DOCUMENT, mimeTypes)
              → OnActivityResult → content Uri
              → ContentResolver.OpenInputStream(uri) → input stream
              → cached to AppDataRoot/import-cache/<sha1-of-uri>/<filename>
              → ImportFileAsync(cachedPath)
              → progress callback fans out to MainViewModel.LoadProgress
              → SceneService.LoadDocument(documentDto, cachedPath)
              → ViewportSurfaceView.RequestRender()
```

### 8.2 Format support
| Format | Loader | Notes |
|---|---|---|
| `.glb` | `GltfImportService` (linked from Import.Gltf.Android) | Pure-managed SharpGLTF; no Android changes |
| `.gltf` + external assets | Same | Limited to assets that fit alongside the picked file; SAF tree-mode pick (Documents UI) lets users grant a directory |
| `.fa` package (AES-encrypted ZIP + GLB + components JSON) | `FaImportService` (linked) | SharpZipLib AES path is pure-managed; SQLite cache backed by Android's `libe_sqlite3.so` |
| `.step / .stp / .iges` | — | Not in MVP |
| Draco-compressed glTF / GLB / GLB-inside-.fa | `DracoDecodingGltfImportService` (Plan 2B) — wraps `GltfImportService`, intercepts Draco-encoded files, decodes via in-process P/Invoke to native `libdraco_native.so`, writes a non-Draco GLB to a temp file, then delegates the unmodified path to the wrapped service. The same wrapped instance is passed to `FaImportService` so packages with Draco-encoded inner GLBs work identically. | **In scope (Plan 2B)** — replaces desktop's helper-exe with in-process libdraco; no desktop source edits |

### 8.3 Cache layout
```
{Context.FilesDir}/import-cache/<sha1-of-content-uri>/<original-filename>
{Context.CacheDir}/fa-extracts/<package-hash>/...
{Context.FilesDir}/fa-package-cache/<package-hash>/model.db        # FaPackageCacheStore
{Context.FilesDir}/settings/settings.json                          # AndroidApplicationSettingsService
{Context.CacheDir}/logs/{crash.log, import-audit.jsonl}
```

### 8.4 Threading
- All import I/O on `Task.Run`. Progress reported via `IProgress<ImportProgress>`.
- Progress callbacks marshal back via `AndroidDispatcher` for UI updates.
- Cancellation via `CancellationToken` (Open button becomes Cancel during import).

---

## 9. UI shell

### 9.1 Single Activity
`MainActivity` hosts a `ConstraintLayout` containing every viewport-time view. No Fragments. Preferences open a separate `PreferencesActivity`.

### 9.2 Tablet landscape layout
```
┌──────────────────────────────────────────────────────────────┐
│  AppBar:  ☰  FabricationAssistant      [Open] [Fit] [⋮]      │
├──────────┬───────────────────────────────────┬───────────────┤
│ Scene    │                                   │ Properties    │
│ tree     │      ViewportContainerView         │ panel         │
│ pinned   │      (GLSurfaceView + overlays)   │ (collapsible) │
│          │                                   │               │
│          │      ViewCube (top-right)         │               │
│          │      BottomToolbar (bottom)       │               │
└──────────┴───────────────────────────────────┴───────────────┘
```

### 9.3 Phone layout
- Scene tree → modal `DrawerLayout` (hamburger from app bar).
- Properties → `BottomSheetDialogFragment` (swipe up).
- Toolbar stays at bottom.

### 9.4 Material 3 theme
- `Theme.FabricationAssistant.Day` and `Theme.FabricationAssistant.Night` defined in `values/styles.xml` and `values-night/styles.xml`.
- Palette mirrors desktop `Theme.Dark.xaml`:
  - Surface `#1B1D1F`, surface-variant `#26282B`
  - Primary `#3B82F6`, on-primary `#FFFFFF`
  - Text primary `#E3E5E8`, secondary `#9CA3AF`
- Font family: `?attr/fontFamily` resolves to Roboto. Optional bundled Inter font deferred.
- Per saved feedback: no inline colors. Every color attribute pulls from theme attrs or `@color/fa_*`.

### 9.5 ViewModel reuse
ViewModels linked from Core (via the shim) for MVP:
- `MainViewModel` (subset - see exclusion list in plan execution)
- `PropertiesViewModel`
- `SceneNodeViewModel`
- `SceneAppearanceViewModel`
- `ViewportToolbarViewModel`
- `SelectionState`, `CameraState`, `SectionService`, etc.

ViewModels excluded (out of MVP or rely on Application.Current.Dispatcher):
- `ChatPanelViewModel`, `SpeechViewModel`, `MarkupWorkspaceViewModel`, `BomWorkspaceViewModel`, `BomConsolidatedWorkspaceViewModel`, `MarkupPanelViewModel`, `MarkupEditorViewModel`, anything that touches `BomColumnFilter`.

### 9.6 MVVM binding layer
A new lightweight `BindingExtensions.cs` (~150 LOC, no third-party MVVM):
- `view.BindText(vm, x => x.Title)`
- `button.BindCommand(vm.OpenFileCommand)`
- `recyclerView.BindItems(vm.SceneNodes, factory)`

Implements `INotifyPropertyChanged` subscriptions with weak references to avoid leaks across configuration changes.

### 9.7 DI composition
`AppServices.cs` builds the `IServiceProvider` in `MainActivity.OnCreate`. Pattern mirrors the desktop's `AppServiceProvider.Configure()`.

### 9.8 Lifecycle handling
- `OnPause` → `ViewportSurfaceView.OnPause()`, cancel in-flight imports.
- `OnResume` → `ViewportSurfaceView.OnResume()`.
- `OnSaveInstanceState` → persist current document URI + serialized `CameraState`.
- `OnRestoreInstanceState` → reload document, restore camera.
- `android:configChanges="orientation|screenSize|keyboardHidden|smallestScreenSize"` to keep the Activity alive across rotations (simpler than full ViewModelStore for MVP).

---

## 10. Threading model

| Thread | Responsibilities |
|---|---|
| Android main (UI) | Activity lifecycle, all view manipulation, command dispatch, settings reads/writes, file picker callbacks |
| GL render thread (owned by `GLSurfaceView`) | All GL calls; consumes the command queue at frame start |
| Import worker (`Task.Run`) | glTF/FA parsing, SQLite cache writes, hash compute |
| Pick async worker | Implicit on GL render thread via `glFenceSync`/PBO |

Cross-thread marshalling:
- UI → Render: `RendererCommandQueue.Enqueue(action)`; drained at `OnDrawFrame` start.
- Render → UI: `AndroidDispatcher.Post(action)` → main `Looper`.
- Import → UI: `IProgress<T>` callbacks routed through `AndroidDispatcher`.

`IDispatcher` is a new interface defined in `Platform.Android` and consumed by any new code. Linked Core code that still uses `Application.Current?.Dispatcher` is filtered out of the shim or excluded from MVP usage paths.

---

## 11. Error handling and diagnostics

### 11.1 Global crash handlers
`AndroidCrashLogger` registers:
- `AppDomain.CurrentDomain.UnhandledException`
- `TaskScheduler.UnobservedTaskException`
- `Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser`

All three:
- `Android.Util.Log.Error(TAG, exception)` (visible in `adb logcat`).
- Append to `{CacheDir}/logs/crash.log` (1 MB rotation; prior kept as `crash.previous.log`).

### 11.2 GL error checking
- Debug-only `[Conditional("DEBUG")] static void CheckGl(string label)` polls `glGetError()` after each draw and logs offenders.
- Release builds: zero overhead.
- No `GL_KHR_debug` callback (not universally supported on stock ES 3.1 drivers).

### 11.3 Shader compile diagnostics
- `ShaderProgram.Compile` throws with the GLSL info log on failure (matches desktop behavior).
- Compile / link failures are fatal in dev; in release they produce a friendly "Renderer initialization failed - please report this device model" Material AlertDialog and exit.

### 11.4 Import diagnostics
- `ImportAuditLog` rotating JSONL at `{CacheDir}/logs/import-audit.jsonl` (1024-line rotation matching desktop `viewer-canonical-activity.jsonl`). Records every import attempt + outcome (success / failure / partial). Audit Section 15.4 flagged this as missing on desktop; adding it on Android.
- Import failures surface a Material `AlertDialog` with the user-facing message + a "Copy details" button.

### 11.5 Logging API
- `Microsoft.Extensions.Logging` is the abstraction.
- A new `AndroidLoggerProvider` wraps `Android.Util.Log`.
- Existing Core code that uses `ILogger<T>` via DI works unchanged. Bespoke loggers (`SpeechDebugLog`, etc.) are out of MVP scope; if any linked Core code references them, the file is excluded.

### 11.6 Recovery from transient errors
- `GLSurfaceView` can lose its context (lock screen, power save). `OnSurfaceCreated` is called again - all GL resources rebuild from `GlesViewportRenderer.Initialize()`.
- Memory pressure: `Activity.OnTrimMemory(TRIM_MEMORY_RUNNING_CRITICAL)` evicts the FA package extraction cache and clears the GPU mesh upload cache.

---

## 12. Testing strategy

### 12.1 Layered tests
| Layer | Project | Host TFM | What it covers |
|---|---|---|---|
| Math / camera / DTOs | `FabricationAssistant.App.Android.Tests` (subset) | `net8.0` (regular, not Android) | Pure logic identical to desktop; no Android dependencies |
| Gesture state machine | Same | `net8.0` | `ViewportTouchGestureRecognizer` fed synthetic pointer sequences |
| File import | Same | `net8.0` | glTF/FA on fixtures in `Fixtures/` (a small sample) |
| Renderer setup / shader compile | Same | `net8.0-android` (emulator) | Smoke tests asserting `GlesViewportRenderer.Initialize()` returns without throwing |
| UI smoke | Espresso (deferred) | Emulator | Post-MVP |

### 12.2 Fixtures
- A tiny GLB (cube) and a small `.fa` package committed under `src/FabricationAssistant.App.Android.Tests/Fixtures/`.
- Avoid the user's bundled `kokoro.onnx` / Sherpa models - they're irrelevant to the viewer.

### 12.3 Running tests
- Local: `dotnet test Android/FabricationAssistant.Android.sln`.
- Emulator-required tests opt in via `[Trait("Category","Emulator")]`; CI can filter.

### 12.4 Build verification
Per saved feedback (intermittent build verification): after each meaningful change during plan execution, the implementer runs `Android/tools/build.ps1` and confirms a clean build before progressing.

---

## 13. Build and deploy

### 13.1 Build
- `Android/tools/build.ps1` is the canonical entry. It runs:
  ```powershell
  dotnet build "$PSScriptRoot/../FabricationAssistant.Android.sln" -c $Configuration
  ```
- Debug builds use Mono runtime; Release builds use AOT for `arm64-v8a`.
- ABIs: `arm64-v8a` (devices), `x86_64` (emulator). 32-bit ABIs not supported.
- `minSdkVersion = 24` (Android 7.0), `targetSdkVersion = 34` (Android 14).

### 13.2 The root `build.ps1` is unchanged
Per saved feedback, the existing root `build.ps1` (which knows how to drive the C++/CLI OcctBridge) is not modified.

### 13.3 Side-load distribution
- `dotnet publish -c Release -p:AndroidPackageFormat=apk` produces an APK.
- `adb install -r path/to/app.apk` installs it.
- Signing: a per-developer debug keystore is auto-generated on first build. Release signing config is a deferred ops concern.

### 13.4 Google Play
Not in MVP. The structure of the project supports it (`AndroidManifest.xml`, version codes), but Play Console signing and store listing are post-MVP work.

---

## 14. Risk register (with concrete mitigations)

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| R1 | Linked-source globs silently include a file that adds a Windows-only API | Medium | CI step that runs `Android/tools/build.ps1`; if it breaks, the diff added a Windows API and either the file moves out of Core or gets excluded |
| R2 | Trimming / AOT removes services accessed by reflection (DI, JSON serialization) | Medium | `[DynamicallyAccessedMembers]` annotations; trimming disabled in Mono debug builds; smoke tests on AOT release builds before sign-off |
| R3 | SharpGLTF / SharpZipLib path assumptions | Low | Both libraries are stream-friendly; passing `Stream` instead of `path` avoids most concerns; verify in plan execution |
| R4 | GLES driver quirks on Mali / Adreno / PowerVR | High | Test matrix: Pixel Tablet (G3), Samsung Tab S9 (Adreno 740), Lenovo P12 Pro (Mali-G610); each gets a per-pass screenshot sanity check |
| R5 | `Microsoft.Data.Sqlite` native packaging on Android | Medium | Verify `libe_sqlite3.so` ends up in APK; fallback to `SQLitePCLRaw.bundle_e_sqlite3` if not |
| R6 | Pervasive `Application.Current.Dispatcher` usage in linked code | Medium | Filter ViewModels with dispatcher calls out of the Core shim; mirror locally with `IDispatcher` injection; MVP scope limits the surface |
| R7 | OpenTK.Graphics.ES31 packaging on .NET-for-Android lags | Low | Fallback to `Silk.NET.OpenGLES`; both expose ES 3.1 calls; decision deferred to plan-execution spike |
| R8 | Aggregate compute-cull path has driver-specific bugs | Low (MVP) | Disabled by default in MVP; spike post-MVP |
| R9 | Edge-as-ribbon shader cost on tile-based GPUs | Medium | Pre-compute neighbour indices at mesh upload time; benchmark on a worst-case CAD scene before shipping |
| R10 | SAF on older OEM Android skins is buggy | Low | Test on Pixel + Samsung; document any known-broken OEM skin in README |

---

## 15. Out of scope for MVP

Explicitly deferred, with the audit section that documents each:
- STEP/IGES import (audit 11, 17.7-#2)
- ~~Draco-compressed glTF~~ — **moved into scope as Plan 2B (2026-05-23 update)**
- AI chat + MCP client (audit 2.1 ModelContextProtocol; 12.K)
- Speech: STT, TTS, AEC (audit 12.H, 17.7-#4)
- Drawings: PDF.js + WebView (audit 6.5, 12.G)
- Markup workspace (audit 6.1, 13)
- BOM workspaces (audit 6.1)
- VR / OpenXR (audit 5, 12.J, 17.5)
- SpaceMouse (audit 12.M, 17.4)
- Save / export workflows
- Google Play distribution + signing
- Espresso UI test suite
- Aggregate compute-cull rendering fast path
- `EXT_disjoint_timer_query` per-pass GPU profiling UI (data collected, no UI)

---

## 16. Roadmap signals (post-MVP priorities, not commitments)

The order below reflects the audit's risk ranking and likely user value:

1. **STEP via cloud transcoder.** Cheapest path to STEP-on-Android; uploads the .step, server emits .glb, client renders. Avoids OCCT-on-NDK initially.
2. **Drawings viewer.** PDF.js bundle + Android `WebView` (the JS bundle is portable; only the host wraps differently).
3. **Markup workspace.** Reuses Core markup engine + a new Android annotation surface.
4. **AI chat panel + MCP client.** Localhost MCP server (Kestrel on Android) feeds a single chat view. Speech still deferred.
5. **Speech (STT + TTS).** Sherpa Android AAR + Kokoro CPU + Android `AudioRecord` / `AudioTrack` (or Oboe).
6. **VR mode for Quest.** Quest is Android-based; the existing OpenXR code may port with a vendor-loader swap. Separate flavor of the same app.
7. **STEP via NDK OCCT.** If the cloud path is unacceptable for offline use.

Each post-MVP item gets its own spec + plan cycle.

---

## 17. Open questions (none blocking MVP; surfaced for awareness)

- **Bundled font:** Roboto vs Inter vs Cascadia Code for tool messages. MVP ships Roboto; revisit if visual departure from desktop is too jarring.
- **Multi-select gesture:** Long-press → mode chip. Alternative is a toggle in the toolbar. MVP ships the chip; collect feedback.
- **F-key fit shortcut on touch:** A 3-finger-tap as "fit to selection" was considered. MVP ships only the toolbar button; gesture deferred.
- **Display refresh-rate handling:** Some tablets support 90/120 Hz. `GLSurfaceView` honours the OS preferred rate. No special handling for MVP.

---

## 18. Definition of done (MVP)

The MVP is complete when:
1. The Android app builds via `Android/tools/build.ps1` on a clean checkout, producing a debug APK for arm64.
2. APK installs on a tablet (Android 7.0+) and launches to a viewport.
3. User can pick a `.glb` or `.fa` via the system file picker and see it rendered with shading, edges, and AO comparable to desktop.
4. Orbit / pan / pinch-zoom work with desktop-equivalent feel.
5. Tap selection, double-tap fit, long-press multi-select work.
6. Section planes can be added, oriented, offset, and clipping shows correctly (via `discard`).
7. Two-tap distance and three-tap angle measurements display correct values.
8. No `Application.Current.Dispatcher` usage in any code path exercised by the app (verified by grep).
9. Math / camera / gesture / import unit tests pass.
10. App survives rotation, lock-screen, and background/foreground cycles without crashing.

---

*End of design. Subject to the user's review before invoking `writing-plans` skill.*
