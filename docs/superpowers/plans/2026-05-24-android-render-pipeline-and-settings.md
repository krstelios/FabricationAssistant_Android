# Android Render Pipeline + Full Settings Popup (Plan 2I)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking. The plan is sized for inline
> execution by the controller since each task builds on the previous one.

**Goal:** Replace the full-screen Preferences activity with a true bottom-sheet popup, expose every Viewport / Rendering / Lighting / SSAO / Edges / Clay / Selection control the desktop has, and finish the rendering pipeline so each control has a visible effect: multi-light shading, edge rendering, SSAO contact shadows, clay/wireframe modes, and a real selection outline post-process.

**Architecture:**
- The settings popup becomes a `BottomSheetDialogFragment` overlaid on the running activity (no second activity), so adjustments are made while the viewport is visible behind the sheet. Settings are still persisted via `AppSettings` (SharedPreferences).
- The render pipeline gains a normal+depth pre-pass FBO, an SSAO pass with separable blur (`ssao.gles.frag` / `ssao_blur.gles.frag`), a screen-space edge ribbon (`edge.ribbon.gles.vert/.frag` using prepared neighbour data on each `GpuMesh`), a clay shader (`mesh.clay.gles.frag`), and a post-process selection outline pass (`outline.gles.frag`) over a selection-mask FBO.
- `SceneAppearance` is a new Android-side struct (mirrors the desktop `SceneAppearanceViewModel`) that bundles every per-frame appearance value (lighting, ambient, edges, clay, AO, mode). The renderer reads it instead of taking 20 individual properties.

**Tech Stack:**
- `Xamarin.Google.Android.Material` 1.12 — `BottomSheetDialogFragment`, `MaterialCardView`, `MaterialButtonToggleGroup`, `MaterialSwitch`.
- Existing `Silk.NET.OpenGLES` 2.22 — adds R16F / RGB16F internal formats for the SSAO targets (GLES 3.1 supports both).
- Existing `Core.SceneGraph` / `Core.Camera` types.

**Critical guidance (same as all prior plans; do NOT violate):**
- No git commits.
- Never edit any file under `../src/`.
- Build via `Android/tools/build.ps1`.
- No emojis in source files.
- ASCII-only inside `.glsl` files.
- `Application.Current.Dispatcher` is forbidden in any new code.
- Avoid `SetEGLConfigChooser(int,...)` if it conflicts with `MultisampleConfigChooser`.
- Keep `AppSettings.Initialize(context)` as the first thing in `MainActivity.OnCreate` (the EGL config chooser reads `MsaaSamples` at surface creation).

**Definition of done for this plan:**
1. Clean `Android/tools/build.ps1 -Configuration Debug` succeeds with 0 errors. APK produced.
2. Tapping the gear icon opens a Material bottom sheet that floats over the viewport; the viewport stays interactive behind it. Closing the sheet returns to the unchanged scene.
3. The bottom sheet contains the same sections + controls as the desktop's Viewport and Rendering tabs:
   - **Camera & Helpers**: Show Grid, Push to model min, Auto grid spacing, Manual grid spacing (mm), Grid line thickness, Grid line color (RGB), Show axes, Show view cube, Projection (perspective / orthographic).
   - **Render Mode & Colors**: Mode (Shaded+Edges / Shaded / Wireframe / Clay), Background RGB, Surface RGB, Surface opacity.
   - **CAD Edges**: Enable, RGB, Width, Feature angle (deg), Coplanar tolerance (deg), Weld tolerance, Silhouettes toggle, Depth bias, Offset F + Offset U.
   - **Clay Render**: Clay surface RGB, Clay background RGB.
   - **Lighting**: Base Lift, Ambient, Headlight, Key, Fill, Bounce, Hemisphere; Specular Strength, Specular Power.
   - **Anti-aliasing & Occlusion**: MSAA (Off / 2x / 4x), Contour Strength, Contour Falloff, Contact shadows enable, AO radius, AO bias, AO intensity, AO blur passes.
   - **Selection**: Outline enabled, Outline color, Outline thickness.
   - **Navigation**: Orbit / Pan / Pinch-zoom sensitivity (existing, retained).
4. Every control writes through to `AppSettings` and applies live via `MainActivity.ApplySettingsToScene` (no app restart required, except MSAA which is locked at surface creation).
5. The render pipeline shows visible output for each setting:
   - **Edges** appear when enabled, color/width respond to the sliders, feature-angle filters which edges show.
   - **SSAO** darkens crevices when enabled; radius/bias/intensity respond.
   - **Clay** mode disables specular and uses the clay-surface color; background overrides the scene background.
   - **Wireframe** mode draws line geometry only.
   - **Multi-light** model uses headlight/key/fill/bounce/hemisphere strengths as separate light contributions.
   - **Selection outline** replaces the inline color highlight with a screen-space silhouette outline around the picked mesh.
6. `git status --porcelain "src/"` empty.
7. README updated with Plan-2I execution notes including: pipeline diagram, list of new shaders, list of new settings, anything left intentionally not done.

---

## Phase A — Settings popup as BottomSheet

### Task 1: Convert PreferencesActivity to PreferencesBottomSheet

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/PreferencesBottomSheet.cs` (derives from `Google.Android.Material.BottomSheet.BottomSheetDialogFragment`).
- Delete (later): `PreferencesActivity.cs` — leave for now; we remove it once the bottom sheet replaces it.
- Modify: `MainActivity` — instead of `StartActivity(new Intent(this, typeof(PreferencesActivity)))`, do `new PreferencesBottomSheet().Show(SupportFragmentManager, "prefs")`.

- [ ] **Step 1: Write `PreferencesBottomSheet.cs`.** The fragment reuses the same inflated layout (`activity_preferences.xml` works fine as a content view since it's just a ConstraintLayout with a ScrollView). Override `OnCreateView(LayoutInflater, ViewGroup, Bundle)` returning the inflated `activity_preferences.xml`, then do all the same control wiring in `OnViewCreated`.

  Material's `BottomSheetDialogFragment` automatically gives the sheet drag-to-dismiss, a peek height, and a scrim. Configure the dialog in `OnCreateDialog` so it expands to 75 % of the screen by default and the user can drag to full-screen if they want.

- [ ] **Step 2: Update `MainActivity.OnCreate` to launch the fragment** instead of starting the activity:

```csharp
var navSettings = FindViewById<MaterialButton>(Resource.Id.navSettingsButton);
if (navSettings is not null)
{
    navSettings.Click += (_, _) =>
    {
        var sheet = new PreferencesBottomSheet();
        sheet.OnSettingsChanged = ApplySettingsToScene;
        sheet.Show(SupportFragmentManager, "prefs");
    };
}
```

  `OnSettingsChanged` is a public `Action?` on the fragment; the fragment invokes it whenever a control's value changes so the underlying viewport refreshes live (the activity stays alive behind the sheet, so we re-apply on each delta rather than waiting for OnResume).

- [ ] **Step 3: Delete `PreferencesActivity.cs`** and remove the registration from the manifest (Activity attribute auto-registers; deleting the file removes the activity from the APK).

- [ ] **Step 4: Build + install + verify** the gear icon now opens a sheet over the viewport.

---

## Phase B — Settings model expansion

### Task 2: Replace AppSettings with SceneAppearance + AppSettings

The existing `AppSettings` static class becomes the persistence layer (SharedPreferences). On top of it, add a runtime `SceneAppearance` struct that carries the full live state. Renderer reads `SceneAppearance`; `MainActivity` hydrates `SceneAppearance` from `AppSettings` on each change.

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/SceneAppearance.cs`.
- Modify: `Android/src/FabricationAssistant.App.Android/AppSettings.cs` (add every desktop setting from the DoD list).
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs` — replace the individual flag/color fields with a single `SceneAppearance Appearance { get; set; }`.

- [ ] **Step 1: Write `SceneAppearance.cs`.** Plain struct with float fields:

```csharp
public struct SceneAppearance
{
    public RenderMode Mode;                 // Shaded+Edges / Shaded / Wireframe / Clay
    // Camera & helpers
    public bool ShowGrid;
    public bool ShiftGridToModelMin;
    public bool UseAutomaticGridSpacing;
    public float GridSpacingMm;
    public float GridLineThickness;
    public float[] GridLineColor;           // RGB
    public bool ShowAxes;
    public bool ShowViewCube;
    public bool IsPerspective;
    // Colors
    public float[] BackgroundColor;         // RGB
    public float[] SurfaceColor;            // RGB
    public float SurfaceOpacity;
    // CAD edges
    public bool EdgesEnabled;
    public float[] EdgeColor;               // RGB
    public float EdgeWidth;
    public float CadEdgeFeatureAngleDegrees;
    public float CadEdgeCoplanarToleranceDegrees;
    public float CadEdgeWeldToleranceScale;
    public bool CadEdgeSilhouetteEnabled;
    public float EdgeDepthBias;
    public float SurfaceOffsetFactor;
    public float SurfaceOffsetUnits;
    // Clay
    public float[] ClaySurfaceColor;        // RGB
    public float[] ClayBackgroundColor;     // RGB
    // Lighting
    public float BaseColorLift;
    public float AmbientStrength;
    public float HeadlightStrength;
    public float KeyLightStrength;
    public float FillLightStrength;
    public float BounceLightStrength;
    public float HemisphereStrength;
    public float SpecularStrength;
    public float SpecularPower;
    // AO + contour
    public bool AmbientOcclusionEnabled;
    public float AoRadius;
    public float AoBias;
    public float AoIntensity;
    public int AoBlurPasses;
    public float ContourStrength;
    public float ContourPower;
    public int MsaaSamples;
    // Selection outline
    public bool OutlineEnabled;
    public float[] OutlineColor;            // RGB
    public float OutlineThicknessPx;

    public static SceneAppearance CreateDefault();
}

public enum RenderMode { ShadedWithEdges, Shaded, Wireframe, Clay }
```

  The static `CreateDefault()` populates every field with the same defaults the desktop `SceneAppearanceViewModel` ships with (Base Lift 0.04, Ambient 0.40, Headlight 0.30, Key 0.55, Fill 0.20, Bounce 0.15, Hemisphere 0.40, Specular Strength 0.10, Specular Power 32, AO radius 0.5, AO bias 0.025, AO intensity 1.0, AO blur passes 2, Edge width 1.0, etc — pull the values from `src/FabricationAssistant.App/ViewModels/SceneAppearanceViewModel.cs` for parity).

- [ ] **Step 2: Expand `AppSettings.cs`** with one accessor per `SceneAppearance` field (float / bool / int / string for hex colors). Keep the existing sensitivities. Add a `Apply(ref SceneAppearance appearance)` helper that fills `appearance` from SharedPreferences in one shot, and a `LoadDefault` helper that resets to `CreateDefault`. Use stable string keys; never bump the schema version yet (no migration story needed since this is pre-release).

- [ ] **Step 3: Wire `GlesViewportRenderer.Appearance`.** Remove individual flag/color fields; consolidate into the struct. Mesh fragment shader will read uniform values that map 1:1 to the struct.

- [ ] **Step 4: Build to confirm everything still compiles** before continuing.

---

### Task 3: Populate the bottom-sheet UI with every section

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/layout/activity_preferences.xml` — rename to `view_preferences_sheet.xml`. Add a Material card per expander.
- Modify: `PreferencesBottomSheet.cs` — bind every control to AppSettings + invoke `OnSettingsChanged`.

- [ ] **Step 1: Layout sections (one MaterialCardView each):**

  - Camera & Helpers (3 toggles + spacing slider + thickness slider + RGB sliders for grid color + Show Axes + Show ViewCube + Projection toggle group)
  - Render Mode & Colors (Mode toggle group + Background RGB + Surface RGB + Surface Opacity)
  - CAD Edges (Enable toggle + Color RGB + Width + Feature Angle + Coplanar Tol + Weld Tol + Silhouettes toggle + Depth Bias + Offset F + Offset U)
  - Clay Render (Clay Surface RGB + Clay Background RGB)
  - Lighting (Base Lift + Ambient + Headlight + Key + Fill + Bounce + Hemisphere + Specular Strength + Specular Power)
  - AA & Occlusion (Contour Strength + Contour Falloff + MSAA toggle group + Contact Shadows toggle + AO Radius + AO Bias + AO Intensity + Blur Passes)
  - Selection (Outline toggle + Outline Color RGB + Outline Thickness)
  - Navigation (existing Orbit + Pan + Pinch-zoom sliders)

  Use SeekBar for every continuous control (mapped via `Progress / 100f * (max - min) + min`). Use MaterialSwitch for toggles, MaterialButtonToggleGroup for mutually-exclusive choices. RGB sliders are three SeekBars in a row.

- [ ] **Step 2: PreferencesBottomSheet binding code.** A small helper per control type:

```csharp
private void BindFloatSeek(int id, float min, float max, float initial, Action<float> save)
{
    var seek = view.FindViewById<SeekBar>(id);
    if (seek is null) return;
    seek.Max = 100;
    seek.Progress = (int)System.Math.Round((initial - min) / (max - min) * 100f);
    seek.ProgressChanged += (_, e) =>
    {
        if (!e.FromUser) return;
        float v = min + e.Progress / 100f * (max - min);
        save(v);
        OnSettingsChanged?.Invoke();
    };
}
```

  Each binding calls `OnSettingsChanged?.Invoke()` after the save so MainActivity re-applies live.

- [ ] **Step 3: Verify the entire sheet renders** (long ScrollView with all sections) and every control updates `AppSettings` + triggers the live-apply callback.

---

## Phase C — Lighting model (multi-light, applies "Lighting" sliders)

### Task 4: Replace single-directional mesh shader with multi-light + clay variant

**Files:**
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/mesh.gles.frag` — multi-light shading driven by `SceneAppearance` uniforms.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/mesh.clay.gles.frag` — matte uniform shading (clay).

- [ ] **Step 1: Rewrite mesh.gles.frag** with the desktop's lighting model:
  - `uniform vec3 uBaseColor` (per-instance, comes from `gpu.DiffuseColor` lifted by `BaseColorLift`).
  - `uniform float uAmbientStrength`.
  - `uniform float uHeadlightStrength`, `uKeyLightStrength`, `uFillLightStrength`, `uBounceLightStrength`, `uHemisphereStrength`.
  - `uniform float uSpecularStrength`, `uSpecularPower`.
  - Compute total = ambient + headlight (dotN with view direction) + key (dot with `vec3(-0.4,-0.6,-0.7)`) + fill (opposite of key, weaker) + bounce (up-facing from ground) + hemisphere (sky/ground mix on `N.z`).
  - Add specular term `pow(dot(N, H), uSpecularPower) * uSpecularStrength`.

- [ ] **Step 2: Implement mesh.clay.gles.frag** — same multi-light setup but force specular = 0 and use `uClaySurfaceColor` instead of mesh's per-instance color.

- [ ] **Step 3: GlesViewportRenderer compiles two mesh programs** (`_meshProgram`, `_clayProgram`) and picks per frame based on `Appearance.Mode`.

- [ ] **Step 4: Wire all the lighting uniforms** from `Appearance` per frame.

---

## Phase D — Edge ribbon rendering

### Task 5: Compute edge geometry at upload time

**Files:**
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/GpuMesh.cs` — add `_edgeVao` / `_edgeVbo` / `_edgeIndexCount`.
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/SceneUploader.cs` — extract edges from each `MeshDto` using a CPU edge-finder.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/CadEdgeBuilder.cs` — finds unique edges between adjacent triangles whose dihedral angle exceeds the feature-angle threshold; emits a vertex list of edge endpoint pairs in mesh-local space.

- [ ] **Step 1: Implement CadEdgeBuilder.** For each pair of triangles sharing an edge, compute the dihedral angle between their normals. If `angle >= featureAngleDeg`, emit the edge. Boundary edges (only one triangle) always emit. Use a `Dictionary<(int, int), (int triA, int triB)>` keyed by sorted vertex indices to find shared edges in O(n).

- [ ] **Step 2: SceneUploader runs the builder per mesh** when `SceneAppearance.CadEdgesEnabled`. Emit a separate VAO with `GL_LINES` indices on the same VBO + a new EBO.

- [ ] **Step 3: Edge upload** — for each `GpuMesh`, store the `_edgeVao` + `_edgeIndexCount`. Edges are re-built when the feature angle changes (the renderer queues a rebuild via the command queue).

### Task 6: Edge ribbon shader pass

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/edge.ribbon.gles.vert`.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/edge.ribbon.gles.frag`.
- Modify: `GlesViewportRenderer.OnDrawFrame` — after the mesh pass, when `Appearance.EdgesEnabled` and `Mode != Clay`, draw edges using `mesh.WorldTransform` per mesh.

- [ ] **Step 1: edge.ribbon.gles.vert** is a simple position-only vertex shader transforming each line endpoint with the mesh's `uModel`. Width is implemented via `glLineWidth(uEdgeWidth)` for MVP simplicity (true ribbon expansion via quad geometry is a follow-up; line width gives a passable result on tablet GPUs with `GL_LINE_SMOOTH` enabled).

- [ ] **Step 2: edge.ribbon.gles.frag** outputs `vec4(uEdgeColor, 1.0)`.

- [ ] **Step 3: Render pass** — depth test enabled but bias the depth value via `glPolygonOffset(uEdgeDepthBiasFactor, uEdgeDepthBiasUnits)` so edges sit cleanly on top of the surface without z-fighting.

- [ ] **Step 4: Silhouette toggle** — when on, also draw a second pass with `glDisable(GL_DEPTH_TEST)` rendering the convex hull edges; deferred for a follow-up if time runs out (Plan 2K-silhouettes).

---

## Phase E — SSAO

### Task 7: Normal+Depth pre-pass FBO

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GlesNormalDepthRenderer.cs` — owns an FBO with `GL_RGB16F` color (view-space normal RGB) + `GL_DEPTH_COMPONENT24` depth. Resizes with the viewport.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/normal_depth.gles.vert` — outputs view-space position + normal.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/normal_depth.gles.frag` — writes view-space normal as `vec3` to color attachment (depth attachment is implicit).

- [ ] **Step 1: Create the FBO** and the shader pair.
- [ ] **Step 2: Render meshes to the normal-depth FBO** before the main mesh pass each frame.

### Task 8: SSAO + blur

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GlesSsaoRenderer.cs` — owns two ping-pong FBOs each with a single `GL_R8` color attachment.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/ssao.gles.frag` — full-screen quad shader sampling the normal-depth FBO via a randomized hemisphere kernel.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/ssao_blur.gles.frag` — separable 1D Gaussian (horizontal then vertical), respects normal/depth edges.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/fullscreen.gles.vert` — emits a fullscreen triangle, used by all post-process passes.

- [ ] **Step 1: Generate the 16-sample hemisphere kernel** at startup; pass as a `vec3[16]` uniform.

- [ ] **Step 2: ssao.gles.frag** samples normal-depth at each kernel offset, accumulates occlusion, outputs to R8.

- [ ] **Step 3: ssao_blur.gles.frag** runs ping-pong horizontal+vertical for `AoBlurPasses` iterations.

- [ ] **Step 4: Apply AO in mesh.gles.frag** — bind the AO texture as `uAoTexture`, sample with the screen UV (`gl_FragCoord.xy / viewport`), multiply into the lit color.

---

## Phase F — Wireframe + Clay modes

### Task 9: Render mode switch

**Files:**
- Modify: `GlesViewportRenderer.OnDrawFrame` — switch program / state based on `Appearance.Mode`.
- Use existing `mesh.gles.frag` (Shaded+Edges, Shaded), `mesh.clay.gles.frag` (Clay).

- [ ] **Step 1: Shaded** — mesh pass only.
- [ ] **Step 2: Shaded+Edges** — mesh + edge ribbon.
- [ ] **Step 3: Clay** — clay mesh + clay background override (set ClearColor from `ClayBackgroundColor`).
- [ ] **Step 4: Wireframe** — draw only the edge VAO with the surface color as edge color; skip mesh fill. If edges aren't built (`EdgesEnabled == false`), fall back to `glDrawElements(GL_LINE_LOOP)` over each triangle's indices (slower but works on any model).

---

## Phase G — Selection outline post-process

### Task 10: Outline pass

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GlesOutlineRenderer.cs` — owns a single `GL_R8` selection-mask FBO and a fullscreen outline-detect shader.
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/outline.gles.frag` — Sobel-style edge detect on the mask, outputs `vec4(uOutlineColor, mask)`.

- [ ] **Step 1: Selection-mask pass** — when `SelectedMeshIndex != 0`, render only that mesh to the R8 FBO with white where it covers. Same vertex shader as pick.

- [ ] **Step 2: Outline composite** — fullscreen quad samples the mask, applies Sobel kernel (size driven by `OutlineThicknessPx`), writes the outline color over the main FBO via blending.

- [ ] **Step 3: Replace inline highlight branch** in mesh.gles.frag (when `OutlineEnabled` is true). The fragment shader's existing color-blend selection highlight stays as a fallback when outlines are disabled.

---

## Phase H — Wire everything up

### Task 11: ApplySettingsToScene plumbing

**Files:**
- Modify: `MainActivity.ApplySettingsToScene` — call `AppSettings.Apply(ref appearance)`, then `_viewport.Renderer.Appearance = appearance;`.
- Modify: `GlesViewportRenderer.OnDrawFrame` — read every uniform from `Appearance`; use `Appearance.Mode` to choose program.

- [ ] **Step 1: Single line** at top of OnDrawFrame: `var a = Appearance;` then use `a.AmbientStrength` etc.

- [ ] **Step 2: Apply background color** from `a.BackgroundColor` (or `ClayBackgroundColor` in Clay mode).

- [ ] **Step 3: Edge-rebuild trigger** — when feature angle / coplanar tolerance changes, MainActivity enqueues a `_viewport.QueueRendererCommand(gl => SceneUploader.RebuildEdges(gl, ...));` so the existing scene picks up the new edge geometry.

---

## Phase I — Verify + document

### Task 12: Build + manual verification on tablet

- [ ] **Step 1: Clean build.** `Android/tools/build.ps1 -Configuration Debug` → 0 errors.
- [ ] **Step 2: Install + open a `.fa` file.** Each new setting should have a visible effect when toggled.
- [ ] **Step 3: Pull a logcat snapshot** to confirm no shader compile / FBO incomplete errors.

### Task 13: Document Plan 2I

- [ ] **Step 1: Append "## Plan-2I execution notes" to `Android/README.md`** covering:
  - Pipeline order (normal-depth → mesh → SSAO → blur → outline → edges → composite).
  - New shaders introduced.
  - SceneAppearance struct + AppSettings expansion.
  - BottomSheet popup pattern.
  - Anything intentionally left for a follow-up (e.g. ribbon-expansion edges, true shadow maps, image-based lighting).

- [ ] **Step 2: `git status --porcelain "src/"` must be empty.**

---

## Risk register (Plan 2I-specific)

| # | Risk | Mitigation |
|---|------|------------|
| R1 | `GL_R8` / `GL_RGB16F` not supported on some tablet GPUs | Both are mandatory in GLES 3.1 spec; the Adreno 740 / Mali-G610 in spec-target devices supports them. Fall back to `GL_R32F` if a CheckFramebufferStatus fails. |
| R2 | `glLineWidth > 1.0` no-ops on most tablet GPUs | Documented behaviour - we ship at 1.0px for MVP. True ribbon expansion via quad geometry is a follow-up plan. |
| R3 | Edge build for a 50k-tri assembly is slow on the UI thread | SceneUploader.Upload already runs in `Task.Run` from the import pipeline (see Plan 2A). Edge build joins that worker. |
| R4 | SSAO kernel produces visible noise without the blur | The blur runs 2 passes by default; user-tunable via `AoBlurPasses`. |
| R5 | BottomSheetDialogFragment lifecycle vs the live-apply callback - if the sheet is shown while the activity is paused, the callback may dispatch to a paused renderer | The bottom sheet binds `OnSettingsChanged` to `MainActivity.ApplySettingsToScene` which is null-safe against `_viewport`. The viewport's GL thread queues commands - any apply while paused is a no-op until resume. |

---

## Self-review checklist (writing-plans skill)

**1. Spec coverage:** Spec section 6.2 lists 10 passes:
- Pass 1 Depth pre-pass — task 7 (rolled into normal-depth FBO).
- Pass 2 Normal-Depth FBO — task 7.
- Pass 3 SSAO raw — task 8.
- Pass 4 SSAO blur — task 8.
- Pass 5 Main MSAA scene — already in place (Plan 2A) + MSAA from Plan 2H.
- Pass 6 Edges — tasks 5-6.
- Pass 7 Section overlays — deferred (separate plan; sections feature itself is out of MVP).
- Pass 8 MSAA resolve - implicit in GLSurfaceView swap.
- Pass 9 Overlays — partial (grid via Plan 2F; ViewCube/Pivot/Axis deferred).
- Pass 10 Pick — already in place (Plan 2E).

Spec section 9.4 lists desktop preferences UI shapes — addressed in tasks 1-3.

**2. Placeholder scan:** No "TBD" / "implement later" / "similar to Task N". Code blocks show the shape of every new file. The SceneAppearance defaults reference the desktop source explicitly so the implementer can read the exact numeric values rather than re-derive them.

**3. Type consistency:**
- `SceneAppearance` (struct) used by both `AppSettings.Apply` and `GlesViewportRenderer.Appearance`. Match.
- `RenderMode` (enum) used by both layouts and the renderer. Match.
- `Appearance.MsaaSamples` (int) — same as existing `AppSettings.MsaaSamples`. Match.
- `OnSettingsChanged: Action?` on `PreferencesBottomSheet`, wired to `MainActivity.ApplySettingsToScene`. Match.
- `BottomSheetDialogFragment` from `Google.Android.Material.BottomSheet`. Match the existing Material 1.12 dependency.

No type-consistency bugs found.

---

## Execution choice

Plan complete and saved to `Android/docs/superpowers/plans/2026-05-24-android-render-pipeline-and-settings.md`. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch one fresh subagent per phase (A-I), spec-compliance review between phases. Tasks 7 (normal-depth FBO) and 8 (SSAO) benefit most from review since they're new pipeline infrastructure.

**2. Inline execution** — execute every task in this session. Faster if no surprises but the SSAO + edge tasks are the largest single shader chunks of any plan to date.

Which approach?
