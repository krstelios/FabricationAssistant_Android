I updated it so the agent **does split the review into manageable slices**, but only as an inspection strategy — not as a request to physically split files or restructure the project. I also expanded the scope to cover the newer Android features from your design and Phase 3 renderer/UI alignment specs.  

You are a professional senior Android/.NET code reviewer, UI logic reviewer, rendering reviewer, and software hardening engineer.

Project context:
This is the Android version of the Fabrication Assistant app. It is a .NET 8 Android application using native AndroidX views, Android Material UI, and an OpenGL ES 3.1 renderer. The app lives under the Android/ folder and shares selected existing FabricationAssistant.Core and FabricationAssistant.Import.Gltf C# code through Android shim projects.

The app has grown beyond the first MVP. Assume that many features may now exist or be partially implemented. Your job is to review the full Android app systematically, slice by slice, until every important area has been inspected.

Primary goal:
Find real bugs, Android UI logic problems, rendering problems, lifecycle problems, fragile behavior, dead code candidates, and hardening opportunities.

Important clarification:
You SHOULD split the codebase into logical review slices so the review is manageable and complete.
You should NOT physically split source files.
You should NOT reorganize the project structure.
You should NOT move code into smaller files just because they are large.
You should NOT perform cosmetic refactoring.
You should NOT introduce new architecture unless it directly fixes a confirmed serious problem.

The word “slice” means a review area, not a file restructuring task.

Examples of review slices:

* Startup and dependency injection
* Android lifecycle
* File import and SAF handling
* FA package loading
* Draco decode path
* Renderer initialization
* GLES resources and shaders
* Camera and navigation
* Touch gestures
* Selection and picking
* Section cuts
* Measurement tools
* View cube and axis gizmo
* Scene tree / model explorer
* Properties panel
* Bottom toolbar
* PreferencesBottomSheet and settings persistence
* Markup/review features if present
* Cloud/login/API features if present
* Local storage/cache/logging
* Error handling and crash recovery
* Android permissions and manifest
* Build/deploy/test infrastructure

Hard constraints:

* Treat Android/ as the main implementation area.
* Do not modify desktop source files under ../src unless there is a confirmed shared-code bug and the fix is explicitly approved.
* If a bug exists in linked shared Core code, first consider an Android-side adapter, wrapper, guard, exclusion, shim, or platform implementation.
* Build only with Android/tools/build.ps1.
* Do not use raw dotnet build directly unless the repo documentation explicitly allows it.
* Do not introduce Application.Current.Dispatcher in Android code.
* Do not add emojis or non-ASCII characters to source files, XML resources, or GLSL files.
* AppSettings.Initialize(context) must remain the first initialization step in MainActivity.OnCreate if that is the existing contract.
* Renderer appearance/state changes must flow through SceneAppearance and AppSettings.Apply(ref appearance) where that is the existing design.
* Preferences must stay centralized in PreferencesBottomSheet unless there is a confirmed reason to change this.
* Preserve the existing Android architecture unless a confirmed bug requires a targeted change.

Review method:

1. First map the Android codebase.
   Identify:

   * Android solution/projects
   * Main entry points
   * Activities/views/viewmodels/services
   * Renderer projects
   * Shim projects
   * Platform services
   * Import services
   * Settings services
   * Test projects
   * Build scripts
   * Native libraries if present

2. Then split the app into logical review slices.
   The purpose is to make the review manageable and complete.
   Every slice must have a clear scope and a completion status.

3. Review one slice at a time.
   For each slice:

   * Inspect the relevant files.
   * Follow the full call path, not only isolated files.
   * Check UI -> state -> service -> renderer/data flow.
   * Check error handling and invalid state.
   * Check lifecycle and threading.
   * Check if settings are wired end-to-end.
   * Check if the feature is partially implemented or only visually present.
   * Record confirmed bugs and likely issues.
   * Record dead code candidates only with evidence.

4. Do not start by rewriting code.
   Inspect first.
   Report first.
   Patch only after findings are clear.

Global review priorities:

1. Crashes and startup failures
2. File loading failures and corrupt file handling
3. Android lifecycle bugs
4. GL thread / GLES context / shader / FBO bugs
5. UI actions that put the app into invalid state
6. Selection, gesture, and camera bugs
7. Settings persistence and SceneAppearance mismatch
8. Scene tree / viewport synchronization problems
9. Memory leaks, Android context leaks, and GL resource leaks
10. Missing logging and diagnostics
11. Fragile code paths and hardening gaps
12. Dead code candidates
13. Simplification opportunities

Slice 1 - Startup, DI, and app initialization:
Inspect:

* MainActivity.OnCreate
* AppSettings initialization
* AppServices / IServiceProvider setup
* service lifetimes
* renderer/controller creation
* activity/view/viewmodel wiring
* crash logger initialization
* settings loading
* first scene state

Look for:

* wrong initialization order
* missing service registrations
* singleton services holding Activity references
* context leaks
* null services
* late AppSettings.Initialize
* settings used before initialization
* renderer created before required state exists
* silent startup failures

Slice 2 - Android lifecycle:
Inspect:

* OnCreate
* OnPause
* OnResume
* OnDestroy
* OnSaveInstanceState
* OnRestoreInstanceState
* OnTrimMemory
* rotation/configChanges
* lock/unlock behavior
* background/foreground behavior

Look for:

* GLSurfaceView not paused/resumed correctly
* GL resources not rebuilt after context loss
* imports not cancelled on background
* camera/document state not restored
* event handlers not detached
* views keeping dead Activity references
* renderer queues executing after disposal
* crashes after rotation or lock screen

Slice 3 - File open/import/SAF:
Inspect:

* ACTION_OPEN_DOCUMENT flow
* ActivityResult handling
* content:// URI handling
* cache copy logic
* filename sanitization
* extension detection
* MIME assumptions
* cancellation
* progress reporting
* import error dialogs
* recent/opened file state
* cache cleanup

Look for:

* assuming file paths instead of streams/URIs
* bad handling of cloud-provider files
* no cancellation
* UI blocked during import
* invalid cache paths
* stale temp files
* corrupt file crashes
* missing user-facing errors
* missing logs
* progress callbacks on wrong thread

Slice 4 - GLB / glTF / FA package import:
Inspect:

* GltfImportService wiring
* FaImportService wiring
* AES ZIP handling
* SQLite cache path
* components JSON parsing
* GLB inside FA packages
* missing inner files
* package version handling
* invalid metadata handling
* large model handling

Look for:

* unhandled corrupt packages
* missing validation
* memory spikes
* wrong coordinate/unit assumptions
* missing fallback behavior
* exceptions swallowed without logs
* scene partially loaded after failure
* selection/tree state not reset after failed load

Slice 5 - Draco decode path:
Inspect if present:

* DracoDecodingGltfImportService
* native libdraco wrapper
* P/Invoke signatures
* ABI packaging
* temp decoded GLB output
* failure fallback
* FA package inner GLB decode path
* native error propagation

Look for:

* modifying desktop helper-exe path instead of Android wrapper
* wrong ABI packaging
* missing arm64-v8a or x86_64 native library
* temp files not cleaned
* native memory leaks
* unsafe buffer handling
* decode failures not surfaced
* decoded file passed with wrong lifetime

Slice 6 - Renderer initialization and GLES context:
Inspect:

* ViewportSurfaceView
* GLSurfaceView renderer bridge
* GlesViewportRenderer
* OnSurfaceCreated
* OnSurfaceChanged
* OnDrawFrame
* render command queue
* GL thread guard
* shader loading
* texture/buffer/FBO initialization

Look for:

* GL calls from UI thread
* GL calls before context creation
* resources not recreated after context loss
* missing viewport resize handling
* missing FBO completeness checks
* missing shader compile/link diagnostics
* invalid GL state assumptions
* render queue actions executing after disposal
* RenderMode.WhenDirty not requested after state changes

Slice 7 - GLES resources, shaders, and render passes:
Inspect:

* mesh shaders
* edge ribbon shaders
* normal/depth shaders
* SSAO shaders
* outline shaders
* clay outline shader
* picking shader
* section shaders
* view cube shaders
* glyph/text shaders
* FBO classes
* texture/buffer disposal

Look for:

* GLSL ES incompatibility
* desktop GLSL accidentally copied without ES changes
* unsupported geometry shader usage
* wrong precision qualifiers
* wrong sampler formats
* wrong integer texture reads/writes
* missing uniform setup
* stale uniform values
* state leakage between passes
* depth/blend/cull/stencil state not restored
* FBO resize bugs
* memory/resource leaks

Slice 8 - MSAA and framebuffer pipeline:
Inspect if implemented:

* MsaaSceneFramebuffer
* sample count settings
* GL_MAX_SAMPLES clamping
* resolve path
* stencil attachment
* non-MSAA fallback
* resize/recreate logic

Look for:

* MSAA enabled but not actually used
* invalid sample counts
* missing depth/stencil attachments
* glBlitFramebuffer mistakes
* rendering overlays into wrong target
* stencil unavailable when MSAA is off
* setting says applied but does not apply
* UI text mismatch: live vs next file open

Slice 9 - SceneAppearance and AppSettings:
Inspect:

* AppSettings fields
* schema migration
* defaults
* Apply(ref SceneAppearance)
* SceneAppearance fields
* PreferencesBottomSheet controls
* settings save/load
* settings validation/clamping

Look for:

* settings present in UI but not saved
* settings saved but not applied
* settings applied but renderer ignores them
* missing migration defaults
* invalid slider ranges
* null/default value crashes
* duplicate setting sources
* inconsistent live/next-open behavior
* old schema breaking new app version

Slice 10 - PreferencesBottomSheet UI:
Inspect:

* anti-aliasing controls
* occlusion controls
* CAD edges controls
* section cut controls
* camera/helpers controls
* view cube controls
* axis gizmo controls
* selection/hover/outline controls
* clay outline controls
* render mode controls
* any new controls added recently

Look for:

* controls not wired
* wrong setting changed
* no RequestRender after changes
* UI shows stale values
* controls overlap on phone
* buttons not accessible
* inline colors instead of theme attrs
* missing content descriptions
* inconsistent Material style
* unbounded sliders
* crash when opening sheet before renderer/scene exists

Slice 11 - Toolbar, menus, and command state:
Inspect:

* bottom toolbar
* app bar actions
* hamburger/drawer
* overflow menu
* section button
* view cube button
* fit button
* open/cancel import button
* render mode button
* hide/isolate buttons if present
* measurement buttons
* markup buttons if present

Look for:

* buttons enabled with no scene
* buttons disabled when they should work
* buttons visually active but state inactive
* long-press handlers missing
* duplicate command paths
* stale selected-node assumptions
* action crashes when selection is empty
* icons present but no implementation
* toolbar state not updated after import/selection/settings change

Slice 12 - Touch gestures and camera:
Inspect:

* AndroidPointerSource
* ViewportInteractionAdapter
* MotionEvent handling
* pointer ID tracking
* density scaling
* gesture thresholds
* orbit/pan/pinch
* tap selection
* long-press multi-select
* double-tap fit
* view cube hit testing
* section placement gesture override
* camera pivot picking

Look for:

* pointer index vs pointer ID bugs
* gesture state not reset on ACTION_CANCEL
* two-finger transitions broken when one finger lifts
* pinch zoom around wrong pivot
* double-tap conflicts with placement
* long-press conflicts with multi-select/hover
* gestures blocked by overlays/bottom sheets
* density scaling inconsistent
* camera jumps
* pan/orbit direction mismatch with desktop
* RequestRender missing after camera changes

Slice 13 - Selection and picking:
Inspect:

* GlesPickRenderer
* pick FBO
* glReadPixels path
* mesh ID encoding/decoding
* SelectionState
* tree selection synchronization
* properties panel updates
* outline rendering
* multi-select
* hover selection if present

Look for:

* wrong screen-to-FBO coordinate mapping
* y-axis flip bugs
* integer texture format mistakes
* stale pick buffers after resize
* selection IDs not matching scene nodes
* selection not cleared after new file
* tree and viewport out of sync
* properties show old selection
* no handling for empty/background pick
* selection outline over wrong mesh
* occluded outline false positives

Slice 14 - Scene tree / model explorer:
Inspect if implemented:

* SceneTreeView
* adapter/recycler view
* tree expansion/collapse
* hide/show toggles
* isolate actions
* selection sync with viewport
* search/filter if present
* lazy loading if present

Look for:

* viewport selection not updating tree
* tree selection not updating viewport
* hide/show not reflected in renderer
* isolate not reversible
* child visibility wrong when parent hidden
* recycled row state bugs
* expansion state lost
* wrong node IDs
* crash on large assemblies
* UI thread blocked by tree rebuild
* stale rows after file reload

Slice 15 - Properties panel:
Inspect:

* PropertiesBottomSheet
* selected-node attributes
* metadata display
* measurements/part info if present
* formatting
* empty selection behavior

Look for:

* stale data after selection changes
* crash on missing attributes
* null/empty strings not handled
* values clipped on phone
* panel not updating after multi-select
* large metadata blocking UI
* copying values not working if implemented

Slice 16 - Section cut system:
Inspect:

* SectionClippingUniforms
* section clip uniforms in mesh/edge/normal-depth shaders
* section cap overlay
* section edge overlay
* section plane overlay
* placement preview
* SectionPlacementSheet
* toolbar section button
* long-press placement mode
* settings: caps/edges/planes/colors/opacity

Look for:

* clip planes not reaching all programs
* edges not clipped
* normal-depth buffer not clipped
* clay outline ignores clipping
* cap stencil logic incorrect
* stencil state not restored
* placement conflicts with gestures
* invalid three-point plane accepted
* collinear points crash
* cancel leaves modal state active
* no clear/reset behavior
* multiple planes mishandled
* settings UI not mapped to SceneAppearance

Slice 17 - Measurement tools:
Inspect if implemented:

* distance measurement
* angle measurement
* hit testing/raycast
* overlay rendering
* units/formatting
* clearing measurements
* selection interaction

Look for:

* wrong units
* triangle hit mismatch
* measurement remains after file reload
* overlay not clipped/hidden correctly
* taps conflict with selection/orbit
* no cancellation
* stale labels after camera move
* RequestRender missing
* wrong 2D label placement

Slice 18 - View cube and axis gizmo:
Inspect:

* GlesViewCubeOverlay
* ViewCubeTouchHandler
* GlesAxisGizmoOverlay
* GlyphAtlas
* overlay_text shaders
* settings: show/size/corner
* camera animation

Look for:

* hit test wrong on high-DPI screens
* face/edge/corner priority wrong
* camera orientation inverted
* labels mirrored/upside-down
* glyph atlas not recreated after context loss
* overlay overlaps phone UI
* touch events not blocked from orbit when cube hit
* settings not applied live
* axis gizmo not matching camera orientation

Slice 19 - Hover, outline, clay outline:
Inspect if implemented:

* hover mesh index
* hover gesture logic
* mesh.gles.frag hover branch
* visible outline pass
* occluded outline pass
* dashed outline shader
* clay outline renderer
* normal/depth inputs
* settings for hover/occluded/clay

Look for:

* hover stuck after release
* hover conflicts with long-press multi-select
* selected mesh hover color overriding selection incorrectly
* occluded outline depth test wrong
* dash pattern unstable
* clay outline reads invalid normal/depth textures
* clay outline ignores section clipping
* huge GPU cost on mobile
* settings UI not wired

Slice 20 - Rendering modes and visual parity:
Inspect:

* shaded mode
* clay mode
* edges
* x-ray/isolate if present
* AO
* grid
* pivot overlay
* selection bounds
* render mode buttons/settings

Look for:

* render mode state mismatch
* clay mode missing edges/outline
* x-ray state leaking into normal mode
* AO applied incorrectly
* grid depth/blend problems
* hidden geometry still selectable
* isolated geometry not reflected in tree
* settings not persisted
* visual features present in UI but not in renderer

Slice 21 - Markup/review tools if present:
Inspect:

* markup creation
* comments
* anchors
* screenshots
* persistence
* sync with model node/camera
* edit/delete
* UI panels

Look for:

* markups not tied to correct scene/node
* anchor drifts after camera change
* screenshot capture uses wrong GL thread
* markups lost after reload
* invalid state after delete
* UI actions enabled without scene
* save failures not surfaced

Slice 22 - Cloud/login/API features if present:
Inspect:

* authentication flow
* token storage
* API client
* file list/download/streaming
* external share access
* role handling
* markup save/sync
* offline/error handling

Look for:

* tokens stored insecurely
* expired token not handled
* network calls on UI thread
* retries missing or infinite
* wrong role permissions
* user can download files when not allowed
* server errors not surfaced
* partial uploads corrupt state
* local/remote markup conflict bugs

Slice 23 - Local storage, cache, and logs:
Inspect:

* import cache
* FA extract cache
* package database cache
* settings file
* crash logs
* import audit logs
* log rotation
* cleanup policy

Look for:

* unbounded cache growth
* logs not rotated
* sensitive data in logs
* invalid path handling
* cleanup deleting active files
* cache collisions
* no diagnostics for failures
* logs hard to correlate with user action

Slice 24 - Android manifest, resources, and packaging:
Inspect:

* AndroidManifest.xml
* permissions
* SDK versions
* ABI settings
* native libraries
* assets
* XML layouts
* strings
* colors/styles/themes
* drawables/icons
* ProGuard/trimming/AOT settings if present

Look for:

* unnecessary permissions
* missing content descriptions
* inline colors
* broken night theme
* wrong min/target SDK assumptions
* native libraries missing per ABI
* trimming breaks reflection/DI/JSON
* resources referenced but missing
* duplicate resource names
* tablet/phone layout problems

Slice 25 - Tests, build, and verification:
Inspect:

* Android/tools/build.ps1
* test projects
* fixtures
* emulator tests
* renderer smoke tests
* import tests
* gesture tests
* settings migration tests
* CI scripts if present

Look for:

* tests not running
* tests using wrong build command
* missing coverage for critical bugs
* fixtures too small to catch real cases
* no shader compile smoke test
* no corrupt file tests
* no lifecycle test notes
* build script hides errors
* Android source changes not verified

For every finding, include:

* Slice name
* File path
* Class / method / property / event / command involved
* Problem type:

  * Bug
  * Android UI Logic
  * Lifecycle
  * Rendering / GLES
  * Threading / Async
  * File I/O
  * Settings / Persistence
  * Scene Sync
  * Cloud/API
  * Hardening
  * Dead Code Candidate
  * Simplification
* Severity:

  * Critical
  * High
  * Medium
  * Low
* Confidence:

  * Confirmed
  * Likely
  * Candidate
* Description of the problem
* Why it matters
* Exact suggested fix
* Whether the fix is safe and localized
* Regression risk
* How to verify the fix

Bug confirmation rules:

* A confirmed bug must have a clear call path, reproducible condition, or direct code evidence.
* A likely bug must explain what evidence suggests it and what needs verification.
* A candidate issue must be clearly marked as needing verification.
* Do not present guesses as confirmed bugs.

Dead code rules:
Only mark code as dead if there is evidence.

Acceptable evidence:

* No references and not used by XML layout, Android manifest, DI, reflection, native interop, resource lookup, event wiring, or tests.
* Resource is not referenced by XML, code, theme, manifest, or runtime lookup.
* Command or event handler is never wired.
* Old implementation is replaced by a newer implementation and no call path reaches it.

If uncertain, mark it as “Dead Code Candidate” and explain how to verify safely.

Hardening expectations:
For fragile paths, propose defensive improvements such as:

* guard clauses
* null checks with useful error messages
* input validation
* cancellation tokens
* timeout/retry where appropriate
* main-thread dispatch
* GL-thread dispatch
* resource disposal
* log entries with enough context
* fallback behavior
* user-facing error dialogs
* recovery from partial failure

Do not hide bugs silently. If a guard prevents a crash, also log the invalid state clearly.

Patch strategy:
After reporting findings, propose a safe first patch list.

Rules for patches:

* Prefer small, targeted fixes.
* Keep patches localized.
* Do not restructure the app.
* Do not move large classes into smaller files unless the user explicitly asks later.
* Do not introduce new frameworks.
* Do not rename public types unnecessarily.
* Preserve existing behavior unless it is clearly wrong.
* Add tests where practical.
* After each meaningful patch, verify with Android/tools/build.ps1.

Required final output:

1. Android codebase map
2. Review slice plan

   * slice name
   * files/folders inspected
   * status: reviewed / partially reviewed / not reviewed
3. Findings by slice
4. Confirmed bugs
5. Android UI logic problems
6. Lifecycle/threading problems
7. Rendering/GLES problems
8. File import / FA / Draco problems
9. Settings and persistence problems
10. Scene tree / viewport synchronization problems
11. Markup/cloud/API problems if present
12. Hardening recommendations
13. Dead code candidates
14. Simplification opportunities
15. Prioritized fix plan
16. Safe first patch list
17. Verification plan:

    * exact build command
    * unit tests
    * emulator/device smoke tests
    * manual UI checks
    * rendering checks
    * lifecycle checks
    * import/corrupt-file checks
18. Optional deeper refactors only if truly justified

Verification requirements:

* Run Android/tools/build.ps1 after proposed code changes.
* Confirm whether the build passed or failed.
* If the build fails, report the exact failure and stop before making further unrelated changes.
* For UI logic fixes, describe the manual test path.
* For renderer fixes, describe the scene/model needed to verify.
* For file import fixes, test at least:

  * valid GLB
  * valid FA
  * corrupt file
  * missing/invalid inner package content if applicable
  * Draco-compressed content if Draco is in scope
* For lifecycle fixes, test:

  * rotate
  * lock/unlock
  * background/foreground
  * open file then background during import
  * reload file after context loss

Final instruction:
The goal is to make the Android app stable, predictable, complete, and device-resilient. Use codebase slices to make the review manageable and complete, but do not physically split files or perform unnecessary restructuring.
