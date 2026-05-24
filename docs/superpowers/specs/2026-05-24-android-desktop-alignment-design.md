# Fabrication Assistant Android - Desktop Alignment, Phase 3 Design

**Date:** 2026-05-24
**Status:** Approved design, awaiting Plan 3B (executed first per dependency ordering)
**Predecessor specs:** `2026-05-23-android-app-design.md`
**Predecessor plans:** Plans 1, 2A through 2I (see `../plans/`)
**Authoritative drift inventory:** see render-pipeline drift report in conversation 2026-05-24 (chat log) and `memory/project_android_renderer_port_state.md`.

---

## 1. Goal

Close the highest-impact visual gaps between the Android port (`Android/src/FabricationAssistant.Rendering.Gles`) and the desktop renderer (`src/FabricationAssistant.Rendering.OpenTK`). After Phase 3, the Android viewer supports:

1. Section-cut (clip planes, cap fill, edge outline, plane indicator, placement preview).
2. Multisample anti-aliasing.
3. In-scene navigation aids (view cube, axis gizmo) with in-scene text.
4. Hover feedback, occluded-selection dashed outline, clay-mode global silhouette outline.

This is the first of three planned alignment phases. Later phases:
- **Phase 4 (interaction parity):** measurement, face highlight, intersection overlay, triangle-level picking via ported `MeshRaycastAcceleration`.
- **Phase 5 (performance parity):** surface aggregate, GPU compute culling (where device matrix supports GLES 3.1 compute), instanced draw.
- **Phase 6 (polish):** GPU timer queries, glow halo refinement.

VR-only features (foveated compositor, XR panel mirror, VR controller overlays) stay out of scope.

---

## 2. Non-goals for Phase 3

- Surface aggregate path, GPU compute culling, instancing.
- Measurement, face highlights, intersection overlay.
- Triangle-level picking. Selection remains mesh-level.
- GPU timer queries.
- Save / export workflows.
- Edits to any file under `../src/` (desktop sources stay untouched, per existing constraint).

---

## 3. Constraints carried from prior plans

- No edits to `../src/`. New files only, all under `Android/`.
- Build via `Android/tools/build.ps1`. Never `dotnet build` directly.
- No emojis in source files. ASCII-only inside `.glsl`.
- `Application.Current.Dispatcher` is forbidden in any new code.
- `AppSettings.Initialize(context)` stays as the first thing in `MainActivity.OnCreate`.
- All renderer state changes flow through `SceneAppearance` and `AppSettings.Apply(ref appearance)`.
- The single settings UI surface is `PreferencesBottomSheet`. New controls extend it; no new preferences activity.

---

## 4. Phase 3 plan inventory

| Plan | Title | Depends on |
|---|---|---|
| 3B | MSAA framebuffer | none (executes first) |
| 3A | Section-cut foundation | 3B (shares offscreen FBO + stencil attachment) |
| 3C | View cube + axis gizmo + glyph atlas | 3B (renders into resolved color) |
| 3D | Hover + occluded outline + clay outline | 3A (clip planes must reach normal/depth shaders) |

Naming preserves feature-letter mnemonics (B = MSAA buffer, A = sectioning, C = chrome, D = depth/detail polish). Execution order is 3B -> 3A -> 3C -> 3D.

---

## 5. Plan 3B - MSAA framebuffer

### 5.1 Components

**New C# files (`Android/src/FabricationAssistant.Rendering.Gles/`):**
- `MsaaSceneFramebuffer.cs` - port of desktop `MsaaSceneFramebuffer.cs` minus GL 4.x-specific code. Holds:
  - `_msaaFbo` (target FBO)
  - `_msaaColorRbo` - `GL_RGBA8` multisample renderbuffer
  - `_msaaDepthStencilRbo` - `GL_DEPTH24_STENCIL8` multisample renderbuffer
  - `_resolveFbo` and `_resolveColorTex` - single-sample target for blit
  - `Resize(width, height, samples)` clamps `samples` against `GL_MAX_SAMPLES`.
  - `Bind()` / `ResolveAndBlitToDefault(width, height)` (uses `glBlitFramebuffer(... GL_COLOR_BUFFER_BIT, GL_NEAREST)`).

**Modified files:**
- `GlesViewportRenderer.cs`:
  - In `OnSurfaceCreated`, instantiate `MsaaSceneFramebuffer`.
  - In `OnSurfaceChanged`, call `Resize(width, height, AppSettings.MsaaSamples)`.
  - In `OnDrawFrame`, bind the MSAA FBO at the top of the mesh pass, then `Resolve` before any post-process and before grid/outline overlays that the user wants antialiased (grid yes; selection outline post is single-sampled by definition).
  - When `MsaaSamples <= 1`, skip MSAA path entirely and render to default FB.

### 5.2 Settings + UI

- `AppSettings.MsaaSamples` already exists (default 4). No new field.
- `PreferencesBottomSheet`: add control inside the existing "Anti-aliasing & Occlusion" card. Three values: Off / 2x / 4x via `MaterialButtonToggleGroup`. Tooltip text in `strings.xml`: "Anti-aliasing. Takes effect on next file open." (Acceptable v1 because runtime sample-count switching needs GLSurfaceView re-creation; deferred.)

### 5.3 No new shaders. No new icons.

### 5.4 Definition of done

1. `Android/tools/build.ps1 -Configuration Debug` returns 0.
2. APK installs on tablet emulator.
3. With MSAA = 4x, CAD edges on a 45-degree feature show no visible stair-stepping. With MSAA = Off, stair-stepping is present.
4. Toggling MSAA in the bottom sheet, then re-opening the file, applies the change.
5. `git status --porcelain "src/"` empty.

---

## 6. Plan 3A - Section-cut foundation

### 6.1 Components

**Modified shaders** (Android/src/FabricationAssistant.Rendering.Gles/Shaders/):
- `mesh.gles.vert` + `mesh.gles.frag` - add:
  ```glsl
  // vert: pass world position to frag
  out vec3 vWorldPosition;
  vWorldPosition = (uModel * vec4(aPosition, 1.0)).xyz;

  // frag: clip-plane discard
  uniform int uSectionPlaneCount;
  uniform vec4 uSectionPlanes[8];
  for (int i = 0; i < uSectionPlaneCount; i++) {
      if (dot(uSectionPlanes[i].xyz, vWorldPosition) - uSectionPlanes[i].w < 0.0) {
          discard;
      }
  }
  ```
  (Vertex shader already computes world position for AO; reuse the existing varying or add it.)
- `edge.ribbon.gles.vert` + `edge.ribbon.gles.frag` - same uniform pair, same discard. Apply per-vertex `HideVertex()` is insufficient because partial-segment clipping needs fragment-level discard.
- `normal_depth.gles.vert` + `normal_depth.gles.frag` - same uniform pair. (Plan 3D's clay outline reads the normal/depth G-buffer, so clipped geometry must be excluded from the G-buffer too.)

**New shaders:**
- `section_cap.gles.vert` + `section_cap.gles.frag` - quad on plane, sized to `2 * sceneBoundsDiagonal`, oriented by plane normal + arbitrary in-plane axis. Frag: flat `uColor` with optional vertex tint.
- `section_edge.gles.vert` + `section_edge.gles.frag` - line geometry (`GL_LINES`), per-vertex color (allows selected-plane highlighting), depth-tested LEQUAL.
- `section_plane.gles.vert` + `section_plane.gles.frag` - translucent fill quad sized by plane extent. Blend `GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA`. Depth-tested LEQUAL, depth writes off, cull off.

**New C# files:**
- `SectionClippingUniforms.cs` - holds scratch `float[32]` (8 vec4). `Pack(IReadOnlyList<Core.Sections.SectionPlane> planes)` writes normal.xyz + offset.w into the array, sets `_count`. `BroadcastTo(uint program)` sets `uSectionPlaneCount` and `uSectionPlanes[0]` via `GL.Uniform4(loc, _count, _scratch)`.
- `Overlays/GlesSectionCapOverlay.cs` - stencil-parity cap algorithm:
  1. `glClearBufferiv(GL_STENCIL, 0, &zero)` (mask 0x80 only).
  2. Disable color + depth writes. Cull off. Depth test on, LEQUAL.
  3. `glStencilOp(GL_KEEP, GL_KEEP, GL_INVERT)`, `glStencilFunc(GL_ALWAYS, 0, 0)`, `glStencilMask(0x80)`.
  4. Re-draw all opaque geometry using the same clipped mesh program (each fragment that passes depth flips its stencil bit; an odd count means the fragment is inside the clipped solid).
  5. Restore color + depth writes. `glStencilFunc(GL_EQUAL, 0x80, 0x80)`, `glStencilMask(0)`. Depth test off (cap is on the plane).
  6. Draw cap quad with `section_cap.gles.*`. Color from `SceneAppearance.SectionCapColor`.
  7. Restore previous stencil state.
- `Overlays/GlesSectionEdgeOverlay.cs` - for each active plane, builds four corner verts (rectangle on plane) and draws as `GL_LINES`. Highlighted plane gets a per-vertex color override.
- `Overlays/GlesSectionPlaneOverlay.cs` - one translucent quad per plane. Caches geometry per plane hash (normal + offset + extent); rebuilds only when changed.
- `Overlays/GlesSectionPlacementPreviewOverlay.cs` - line strip between committed pick points + rubber-band line to current hover. Reuses `section_edge.gles.*` for color flexibility. Depth test off so it draws over everything during placement.

**Modified files:**
- `GlesViewportRenderer.cs`:
  - After main mesh pass and before edge pass: invoke `SectionClippingUniforms.Pack(scene.SectionPlanes)`, broadcast to all six clip-aware programs (mesh, edge, normal_depth, section_cap, section_edge, section_plane).
  - After edge pass and before selection outline: invoke section overlays in order: cap, plane, edge. Placement preview last if active.
- `Views/MultisampleConfigChooser.cs` (or new `EglConfigChooser`): request `EGL_STENCIL_SIZE = 8`. (The MSAA FBO created in 3B will carry its own stencil RBO, but the default FB also needs stencil for non-MSAA mode.)

### 6.2 Settings + UI

**New `AppSettings` fields** (schema version 2 -> 3):
- `SectionPlanesEnabled` (bool, default true) - global on/off.
- `ShowSectionCaps` (bool, default true).
- `ShowSectionEdges` (bool, default true).
- `ShowSectionPlanes` (bool, default true).
- `SectionCapColorR/G/B` (float, default 0.85/0.78/0.65).
- `SectionEdgeColorR/G/B` (float, default 0.20/0.20/0.22).
- `SectionPlaneColorR/G/B` (float, default 0.35/0.55/0.85).
- `SectionPlaneOpacity` (float 0-1, default 0.18).

**Matching `SceneAppearance` fields** added with one-to-one names. `AppSettings.Apply` writes them through.

**Bottom-sheet UI** (`activity_preferences.xml`): new `MaterialCardView` titled "Section cut" placed after "CAD Edges". Contains:
- `MaterialSwitch` for `SectionPlanesEnabled`.
- Three switches for caps / edges / planes.
- Three color rows (RGB sliders) for cap, edge, plane colors.
- One slider for plane opacity (0-1, step 0.05).

**Toolbar wiring** (`MainActivity.cs`, `activity_main.xml`):
- Existing `ic_section` button already exists. Wire its click to toggle `SectionPlanesEnabled` and call `ApplySettingsToScene`.
- Long-press on the button opens a placement-mode lightweight bottom popup (`SectionPlacementSheet`) that displays:
  - "Tap three points on the model" instruction text.
  - "Cancel" + "Accept" actions.
  - Activates `GlesSectionPlacementPreviewOverlay` until dismissed.

**New drawables:**
- `ic_section_active.xml` - filled variant of existing `ic_section.xml` for the toggled-on state.

### 6.3 Definition of done

1. `Android/tools/build.ps1 -Configuration Debug` returns 0.
2. APK installs on tablet.
3. Tapping `ic_section` toggles section planes on/off. Toggling on with no planes does nothing; toggling on with planes clips geometry.
4. Long-press on `ic_section` opens placement mode. Tapping three points on the model commits a new plane; "Cancel" backs out.
5. With a plane active:
   - Cap fill is solid in the plane region under clipped geometry, transparent outside the model.
   - Edge outline shows the rectangle of the plane in edge color.
   - Plane indicator shows as translucent fill in plane color + opacity.
6. CAD edges and clay outline (Plan 3D) also respect the clip plane.
7. `git status --porcelain "src/"` empty.

---

## 7. Plan 3C - View cube + axis gizmo + glyph atlas

### 7.1 Glyph atlas

**New C# file:**
- `Overlays/GlyphAtlas.cs`:
  - On init, build a 512 x 256 ARGB bitmap using Android `Canvas.drawText` with `Typeface.Default` at 48 px, anti-alias on. Iterate ASCII 0x20-0x7E, lay out left-to-right with line wrap at row width.
  - For each glyph, record `(uMin, vMin, uMax, vMax, bearingX, bearingY, advanceX, height)` into a `Dictionary<char, GlyphMetrics>`.
  - Upload bitmap as `GL_RGBA8` texture with linear filtering.
  - `MeasureText(string)` returns width in pixels.
  - `BuildQuads(string, out float[] vertices, out int vertexCount)` emits two triangles per glyph with UV + per-vertex position relative to the text origin.

**New shaders:**
- `overlay_text.gles.vert` - takes screen-space position (in pixels) + UV, multiplies position by `uViewportInvSize * 2 - 1` for NDC.
- `overlay_text.gles.frag` - samples atlas, outputs premultiplied alpha. `uColor` modulates.

### 7.2 View cube

**New C# file:**
- `Overlays/GlesViewCubeOverlay.cs`:
  - Builds a chamfered cube mesh at init (cached). 26 region IDs: 6 face quads, 12 edge bevel strips, 8 corner triangles.
  - Renders in a small ortho viewport in the chosen corner (`AppSettings.ViewCubeCorner`), sized in dp via `AppSettings.ViewCubeSizeDp`. Uses cube model-space rotation matched to the inverse of `CameraState.Orientation`.
  - For each face, calls `GlyphAtlas.BuildQuads("FRONT")` etc., then draws the text into the face's screen-space rectangle.
  - Hit test: project region centroids to screen; pick the closest within an 8-dp slop, preferring face > edge > corner.
  - `HitTest(touchX, touchY) -> (RegionId regionId, Quaternion targetOrientation)`.

**New shader:**
- `viewcube.gles.vert` + `viewcube.gles.frag` - per-vertex color, flat-shaded. No need for lighting (cube is its own visual cue).

**Modified files:**
- `GlesViewportRenderer.cs`: invoke `ViewCubeOverlay.Render` at end of frame, after outline pass.
- `MainActivity.cs`: forward touch-down events at the view cube viewport region to `ViewCubeOverlay.HitTest`. On hit, animate `CameraState` toward target orientation over 250 ms (use existing camera animation if any; otherwise simple slerp).

### 7.3 Axis gizmo

**New C# file:**
- `Overlays/GlesAxisGizmoOverlay.cs`:
  - Generates three line segments + three cone arrow heads (small triangle fans) at init.
  - Renders bottom-left in a small ortho viewport sized via `AppSettings.AxisGizmoSizeDp`. Same cube-style rotation matched to `CameraState`.
  - Uses GlyphAtlas for "X" / "Y" / "Z" labels at the tip of each axis arrow.

Reuses `viewcube.gles.*` shader.

### 7.4 Settings + UI

**Existing fields:** `ShowViewCube`, `ShowAxes` (both bool).

**New `AppSettings` fields:**
- `ViewCubeSizeDp` (int, default 96, range 64-160).
- `ViewCubeCorner` (int 0-3, default 1 = top-right; order TL=0, TR=1, BR=2, BL=3).
- `AxisGizmoSizeDp` (int, default 80, range 48-128).

**Bottom-sheet UI:** extend existing "Camera & Helpers" card:
- Under existing "Show view cube" switch: add size slider + corner picker (4-button toggle group).
- Under existing "Show axes" switch: add size slider.

**Toolbar wiring:**
- Existing `ic_view_cube` button: toggle `ShowViewCube` and `ApplySettingsToScene`. (Currently no-op.)

**No new drawables.**

### 7.5 Definition of done

1. Build returns 0. APK installs.
2. View cube appears in the chosen corner, rotates with the camera, displays face labels.
3. Tapping a face animates the camera to that orientation. Tapping an edge / corner does likewise.
4. Axis gizmo appears bottom-left, rotates with camera, shows X/Y/Z labels.
5. Toggling either via the bottom sheet or via `ic_view_cube` button hides/shows it.
6. Size + corner pickers in the bottom sheet take effect immediately.

---

## 8. Plan 3D - Hover + occluded outline + clay outline

### 8.1 Mesh shader changes

`mesh.gles.frag` additions:
```glsl
uniform int uHoverMeshIndex;
uniform vec3 uHoverColor;
// after existing selection branch:
if (uHoverMeshIndex > 0 && uMeshIndex == uHoverMeshIndex) {
    color = mix(color, uHoverColor, 0.25) + uHoverColor * 0.05;
}
```

`GlesViewportRenderer`: track `HoverMeshIndex` (settable from the touch layer via a long-press or hover gesture; defaults to 0 = none). Forward to the program each frame.

### 8.2 Two-pass selection outline (visible + occluded)

`GlesOutlineRenderer` becomes two passes:
- Pass A (visible): existing Sobel post-process over the R8 selection mask. Depth-tested when generating the mask (the mask renderer already uses depth test, so the mask is implicitly visible-only). Output: solid outline in `OutlineColor`.
- Pass B (occluded): re-render the selection mask with depth-test func `GREATER` (occluded portions only) to a second R8 attachment. Then run Sobel + dash pattern over that mask.

**New shader:**
- `outline.dashed.gles.frag` - same Sobel core but multiplies coverage by:
  ```glsl
  float dashCoord = (gl_FragCoord.x + gl_FragCoord.y) * uDashFrequency;
  float dash = step(0.5, fract(dashCoord));
  ```
  Result blended with `OutlineOccludedColor`.

### 8.3 Clay-mode global outline

**New shader:**
- `clay_outline.gles.frag` - port of desktop `clay_outline.frag.glsl`. Reads from existing AO G-buffer (RG8 octahedral normal at `uNormalTexture`, D24 at `uDepthTexture`). Two gates:
  - Background-vs-foreground: if `depth >= 0.999999` but a neighbor sample is < 0.999999, emit edge.
  - Within-object: compute depth delta (with `smoothstep(uRadius, uRadius + uFeather, distance)` spatial weight) and normal delta (`1.0 - dot(centerN, sampleN)` thresholded). Combine.
  - 8-tap kernel: cardinal + diagonal directions at radius 1, then same at radius 3. (Desktop uses up to 17-tap; cap at 8 for mobile.)

**Modified files:**
- `GlesViewportRenderer`: when `RenderMode == Clay`, replace the call to `GlesOutlineRenderer.Render` with a call to `RenderClayOutline()` which runs `clay_outline.gles.frag` as a fullscreen pass. The selection outline still runs over the top in Clay mode.

### 8.4 Settings + UI

**New `AppSettings` fields:**
- `HoverEnabled` (bool, default true).
- `HoverColorR/G/B` (float, default 0.40 / 0.78 / 1.00).
- `OutlineOccludedEnabled` (bool, default true).
- `OutlineOccludedDashFrequency` (float, default 0.15, range 0.05-0.40).
- `OutlineOccludedColorR/G/B` (float, default 1.00 / 0.80 / 0.40).
- `ClayOutlineRadius` (float, default 1.5, range 1.0-4.0, units = pixels).
- `ClayOutlineFeather` (float, default 1.0, range 0.5-3.0, units = pixels).
- `ClayOutlineNormalThreshold` (float, default 0.18, range 0.05-0.50, dot-product delta).

**Bottom-sheet UI:** extend existing "Selection" card:
- Below existing outline controls, add subgroup "Hover" with switch + color row.
- Below that, subgroup "Occluded outline" with switch + dash-frequency slider + color row.
- New card "Clay outline" appears in Clay mode (or always; render mode is already a setting). Three sliders.

**No new drawables.**

### 8.5 Definition of done

1. Build returns 0. APK installs.
2. Hovering (long-press 200 ms or proximity hover on Quest) shows hover tint on the touched mesh; releasing clears it.
3. Selecting a mesh that's partially behind another shows the visible portion of the outline as solid in `OutlineColor` and the occluded portion as dashed in `OutlineOccludedColor`.
4. Switching render mode to Clay produces a global silhouette + crease outline equivalent to desktop's clay mode.
5. All controls in the bottom sheet take effect live.

---

## 9. Cross-cutting inventory

### 9.1 New AppSettings fields (Plans 3A + 3C + 3D total = 29 new)

```
SectionPlanesEnabled (bool)
ShowSectionCaps (bool)
ShowSectionEdges (bool)
ShowSectionPlanes (bool)
SectionCapColorR/G/B (3 floats)
SectionEdgeColorR/G/B (3 floats)
SectionPlaneColorR/G/B (3 floats)
SectionPlaneOpacity (float)
ViewCubeSizeDp (int)
ViewCubeCorner (int)
AxisGizmoSizeDp (int)
HoverEnabled (bool)
HoverColorR/G/B (3 floats)
OutlineOccludedEnabled (bool)
OutlineOccludedDashFrequency (float)
OutlineOccludedColorR/G/B (3 floats)
ClayOutlineRadius (float)
ClayOutlineFeather (float)
ClayOutlineNormalThreshold (float)
```

Schema version: 2 -> 3. Migration adds new keys at their defaults; no value remapping.

`AppSettings.Apply(ref SceneAppearance appearance)` writes every new field through. `SceneAppearance` (`Android/src/FabricationAssistant.Rendering.Gles/SceneAppearance.cs`) gains matching fields.

### 9.2 New drawable assets

- `ic_section_active.xml` - filled variant of `ic_section.xml` for the toggled-on state of the section button. Same 24 dp viewport, same path data with solid fill instead of stroke.

No other new drawables. View cube uses existing `ic_view_cube` for its toolbar entry. The cube faces themselves are rendered geometry with glyph-atlas text labels (no XML icons).

### 9.3 New string resources

About 30 new keys in `Resources/values/strings.xml` for new bottom-sheet labels and content descriptions. Detailed list lives in the per-plan deliverable.

### 9.4 New shaders (paths)

```
Shaders/section_cap.gles.vert
Shaders/section_cap.gles.frag
Shaders/section_edge.gles.vert
Shaders/section_edge.gles.frag
Shaders/section_plane.gles.vert
Shaders/section_plane.gles.frag
Shaders/viewcube.gles.vert
Shaders/viewcube.gles.frag
Shaders/overlay_text.gles.vert
Shaders/overlay_text.gles.frag
Shaders/outline.dashed.gles.frag
Shaders/clay_outline.gles.frag
```

Modified shaders: `mesh.gles.{vert,frag}`, `edge.ribbon.gles.{vert,frag}`, `normal_depth.gles.{vert,frag}` (clip planes), `mesh.gles.frag` again (hover).

### 9.5 New C# files (renderer)

```
Rendering.Gles/MsaaSceneFramebuffer.cs                              # Plan 3B
Rendering.Gles/SectionClippingUniforms.cs                           # Plan 3A
Rendering.Gles/Overlays/GlesSectionCapOverlay.cs                    # Plan 3A
Rendering.Gles/Overlays/GlesSectionEdgeOverlay.cs                   # Plan 3A
Rendering.Gles/Overlays/GlesSectionPlaneOverlay.cs                  # Plan 3A
Rendering.Gles/Overlays/GlesSectionPlacementPreviewOverlay.cs       # Plan 3A
Rendering.Gles/Overlays/GlyphAtlas.cs                               # Plan 3C
Rendering.Gles/Overlays/GlesViewCubeOverlay.cs                      # Plan 3C
Rendering.Gles/Overlays/GlesAxisGizmoOverlay.cs                     # Plan 3C
Rendering.Gles/Overlays/GlesClayOutlineRenderer.cs                  # Plan 3D
```

`Overlays/` is a new subfolder under `Rendering.Gles/`. Existing `GlesGridRenderer` will move there in 3C for consistency (no namespace change required since it's a flat namespace today).

### 9.6 New C# files (app)

```
App.Android/SectionPlacementSheet.cs                                # Plan 3A
App.Android/ViewCubeTouchHandler.cs                                 # Plan 3C
```

### 9.7 Modified layouts

`Resources/layout/activity_preferences.xml`:
- New `MaterialCardView` "Section cut" after "CAD Edges".
- Existing "Camera & Helpers" card gets view-cube size + corner picker.
- Existing "Selection" card gets hover + occluded subgroups.
- New `MaterialCardView` "Clay outline".

`Resources/layout/activity_main.xml`:
- Wire `ic_section` and `ic_view_cube` button click handlers (they exist as buttons today but call no handlers).

---

## 10. Risks and mitigations

### R1 - Stencil buffer on default GLSurfaceView

The default `MultisampleConfigChooser` may not request stencil bits. Plan 3A needs stencil for cap parity.

**Mitigation:** Plan 3B's MSAA FBO already provisions `GL_DEPTH24_STENCIL8`. When MSAA = Off, fall back to also rendering into a single-sample offscreen FBO with the stencil attachment (cheap; reuses the resolve-target texture). The default backbuffer is only used for the final blit. This means 3B's `MsaaSceneFramebuffer` covers stencil regardless of sample count and 3A inherits it.

### R2 - Plan 3A and 3B share an FBO refactor

The current renderer draws to the default framebuffer. Plan 3B introduces an offscreen FBO; Plan 3A needs that FBO to have a stencil attachment.

**Mitigation:** Execute 3B first. Its definition of done includes "FBO is in place even when MSAA = Off" (using single-sample renderbuffer in that case). Plan 3A's first step is to verify the FBO has stencil attached; if not, fail loudly.

### R3 - Glyph atlas font availability

Roboto vs Noto vs system default. Bitmaps generated on-device must be deterministic enough for layout tests.

**Mitigation:** Use `Typeface.Default` (Roboto on stock Android). Atlas layout is computed from the actual measured metrics, not assumed. If a future device ships a different default, the renderer recomputes correctly. No bundled font for v1.

### R4 - View-cube hit-test on small overlay

96 dp is roughly 8 mm; corner regions are tiny.

**Mitigation:** 8-dp slop. Prefer face > edge > corner when multiple regions are within slop. Touch state tracks first-touch position so a tap on a face boundary doesn't accidentally hit an edge.

### R5 - Clay outline cost on mobile GPU

Desktop's clay outline does up to 17 taps. Doing the same on a 2k x 1.2k tablet display at 60 fps is ~100M texture reads/sec.

**Mitigation:** Cap kernel at 8 taps (cardinal + diagonal at radius 1 + cardinal at radius 3). If performance issues arise on weaker devices, future plan adds a half-resolution buffer + upsample. v1 ships at full res.

### R6 - `MsaaSamples` locked at surface creation

GLSurfaceView's EGL config is fixed once a surface exists.

**Mitigation:** Plan 3B's UI shows "Takes effect on next file open." Future plan can re-create the GLSurfaceView on sample-count change.

### R7 - Section placement gesture conflicts with orbit

Three-tap placement may collide with double-tap-to-fit or pinch-to-zoom.

**Mitigation:** Placement mode is a modal state (entered by long-press on `ic_section`). While active, the gesture recognizer routes pointer events to placement instead of orbit. Cancel button exits the mode.

---

## 11. Open questions and explicit deferrals

- **Edit existing sections after placement.** Plan 3A places new planes via long-press flow. Editing the offset or angle of an existing plane is not in 3A; deferred to a small follow-up plan (3A.1) if needed.
- **Multiple section planes simultaneously.** The clip-plane uniform array supports 8. UI supports placing successive planes. There is no list-view of placed planes in 3A's UI; the bottom sheet shows a "Clear all planes" button. Per-plane edit UI is deferred.
- **Save persistence of section planes.** Section planes are scene-level state in `Core.Sections`. v1 does not persist them across file reload. Reload clears the array.
- **Hover gesture on touch devices.** Phones/tablets have no hover concept. v1 maps hover to long-press-and-hold (200 ms). On Quest browser, native hover events route through. The hover decision tree lives in `MainActivity` touch handler.
- **Clay outline color.** Desktop's clay outline color is derived from the surface clay color minus a constant. v1 follows the same logic (no separate setting).
- **View cube on phone portrait.** With small viewports the cube can overlap toolbars. v1 uses dp-based sizing so 96 dp -> ~360 px on a 3.75 dpi phone display, leaving 100+ px of inset. If overlap is observed, future plan adds an auto-corner-on-portrait setting.

---

## 12. Definition of done - Phase 3 as a whole

After all four plans (3B, 3A, 3C, 3D) land:

1. All four plan-level definitions of done satisfied.
2. The Android viewer can:
   - Render with 2x / 4x MSAA.
   - Display section cut: cap, edge, plane indicator, placement preview.
   - Display a view cube and an axis gizmo with text labels.
   - Show hover feedback on long-press.
   - Show selection outline with visible portion solid and occluded portion dashed.
   - In Clay mode, show a global silhouette + crease outline.
3. All new appearance fields are persistable through `AppSettings` and exposed in `PreferencesBottomSheet`.
4. The `ic_section` and `ic_view_cube` toolbar buttons are functional.
5. Schema version bumped to 3 with a no-op migration adding default values for new keys.
6. `git status --porcelain "src/"` empty.
7. README updated with Phase 3 execution notes including: pipeline diagram update, list of new shaders, list of new settings, anything left intentionally not done.
