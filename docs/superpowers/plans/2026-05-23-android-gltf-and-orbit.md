# Android glTF + Orbit Camera Implementation Plan (Plan 2A of 3+)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open a .glb file via the Android system file picker, render it with simple lit shading in the GLES 3.1 viewport, and let the user orbit / pan / pinch-zoom with touch. The first interactive viewer.

**Architecture:** Builds on the Plan 1 foundation. Extends `FabricationAssistantPathsBootstrap` so the Core/Import.Gltf path API surface compiles end-to-end. Adds GPU mesh upload (`GpuMesh`, `GpuScene`), a minimum `mesh.gles.vert/.frag` shader pair, scene-level draw loop using camera matrices from Core's `CameraState`. Wires SAF (`ACTION_OPEN_DOCUMENT`) for file picking and the existing `ViewportTouchGestureRecognizer` state machine from Core for gestures, with a new `AndroidPointerSource` translating Android `MotionEvent` into the recognizer's input model.

**Tech Stack:** Same as Plan 1 — .NET 8 for Android (`net8.0-android34.0`), Silk.NET.OpenGLES 2.22.0, AndroidX AppCompat + Material 1.12 + ConstraintLayout 2.2, SharpGLTF 1.0.6 (already in `Import.Gltf.Android` shim).

**Critical guidance (same as Plan 1):**
- **No git commit steps.** Leave work unstaged. User runs parallel refactors on master.
- **Never edit any file under `../src/`** — the desktop tree is read-only.
- **Build via `Android/tools/build.ps1`** — handles ANDROID_HOME + JAVA_HOME fallbacks.
- **ASCII-only inside `.glsl` files** (NVIDIA preprocessor quirk on some Android driver lineages).
- **No emojis in source files.**
- **`dotnet sln add` is broken on this system** — when adding new projects to the solution, edit `Android/FabricationAssistant.Android.sln` directly using the `Project(...)/EndProject` block + four `ProjectConfigurationPlatforms` lines pattern. (Plan 2A does NOT add new projects; only new files in existing projects, so this point shouldn't bite.)
- **`Application.Current.Dispatcher` is forbidden in any new code.** Use the injected `IDispatcher` (from `FabricationAssistant.Platform`) or the `AndroidDispatcher` directly.

**Definition of done for this plan:**
1. Build green via `Android/tools/build.ps1 -Configuration Debug`. APK produced.
2. The four Import.Gltf files excluded by Plan 1 (`FaImportService.cs`, `FaPackageCacheStore.cs`, `FaPackageStorageOptions.cs`, `GltfImportService.cs`) are NO LONGER excluded — the extended bootstrap satisfies their path dependencies. (FA packages still require runtime work that's out of Plan 2A scope; .glb files load and render.)
3. The app shows a Material toolbar with an "Open" button.
4. Tapping "Open" launches the Android system file picker (`ACTION_OPEN_DOCUMENT`).
5. Selecting a `.glb` file imports it and renders the model with simple lit shading.
6. One-finger drag orbits the model around its bounding-box center; two-finger drag pans; two-finger pinch zooms.
7. New `dotnet test` smoke tests:
   - `CameraState.Orbit` produces the expected camera position for a unit-circle orbit.
   - `ViewportTouchGestureRecognizer` produces the expected `OrbitDelta` from a synthetic one-finger drag.
8. No `Application.Current.Dispatcher` anywhere in `Android/**/*.cs`.
9. Desktop `src/` tree shows zero modifications.

**Out of scope (deferred to Plan 2B or Plan 3):**
- Picking, selection, hover, single-tap-selection visualization.
- Edge rendering (the ribbon technique).
- SSAO, normal-depth FBO, blur passes.
- Section planes (UI, shader clipping, plane/edge/cap overlays).
- Measurement tool.
- View-cube overlay, axis-gizmo overlay, grid overlay, pivot overlay.
- FA encrypted packages (`.fa`) — viewer ignores them in this plan.
- Draco-compressed glTF (`GltfImportService` will throw `NotSupportedException` on Draco detection — by design).
- BOM workspace, markup, drawings PDF viewer.
- Saving / exporting / preferences UI.
- Espresso UI tests; only `dotnet test` host-side unit tests for now.

---

## File structure (created/modified by this plan)

```
Android/src/FabricationAssistant.Platform.Android/
    FabricationAssistantPathsBootstrap.cs                       # MODIFIED — add FaImportExtractionDirectory, DracoDecodeDirectory, ConstrainDirectoryToRoot

Android/src/FabricationAssistant.Core.Android/
    AndroidPlatformExclusions.targets                            # MODIFIED — leaves only FabricationAssistantPaths.cs excluded

Android/src/FabricationAssistant.Import.Gltf.Android/
    FabricationAssistant.Import.Gltf.Android.csproj              # MODIFIED — remove the 4 file-level <Compile Remove> entries

Android/src/FabricationAssistant.Rendering.Gles/
    Shaders/                                                      # NEW folder
        mesh.gles.vert                                            # NEW
        mesh.gles.frag                                            # NEW
    GpuMesh.cs                                                    # NEW
    GpuScene.cs                                                   # NEW
    GlesViewportRenderer.cs                                       # MODIFIED — scene draw loop, MVP uniforms
    SceneUploader.cs                                              # NEW — DocumentDto -> GpuMesh list
    ViewportCameraMath.cs                                         # NEW — view + projection matrices from CameraState

Android/src/FabricationAssistant.Input.Gestures.Android/
    AndroidPointerSource.cs                                       # MODIFIED — implement MotionEvent translation
    ViewportInteractionAdapter.cs                                 # NEW — gesture events -> CameraState

Android/src/FabricationAssistant.App.Android/
    FabricationAssistant.App.Android.csproj                       # (unchanged)
    Properties/AndroidManifest.xml                                # (unchanged)
    Resources/layout/activity_main.xml                            # MODIFIED — add app bar with Open button
    Resources/values/strings.xml                                  # MODIFIED — add "open_button" string
    Resources/drawable/ic_open.xml                                # NEW — Material file-open vector drawable
    Views/ViewportSurfaceView.cs                                  # MODIFIED — set OnTouchListener
    AppServices.cs                                                # MODIFIED — register CameraState, SceneService, etc.
    MainActivity.cs                                               # MODIFIED — Open button click, SAF launch, import flow
    SafFilePicker.cs                                              # NEW — ActivityResultLauncher + content URI -> local file
    ImportPipeline.cs                                             # NEW — orchestrates import -> upload -> render

Android/src/FabricationAssistant.App.Android.Tests/
    FabricationAssistant.App.Android.Tests.csproj                 # MODIFIED — link more Core sources for tests
    CameraOrbitTests.cs                                           # NEW
    GestureRecognizerTests.cs                                     # NEW
```

Total: 13 new files, 9 modified files, all under `Android/`. Zero changes to `../src/`.

---

## Phase 1: Extend the path bootstrap so the full Import.Gltf surface compiles

### Task 1: Read what API the linked Import.Gltf code actually needs

**Files:** none — investigation only.

- [ ] **Step 1: Identify the static members called by the 4 excluded files**

Use Grep against the desktop sources to enumerate `FabricationAssistantPaths` API surface called by the four excluded files:

Run: Grep with pattern `FabricationAssistantPaths\.\w+` in `..\..\..\src\FabricationAssistant.Import.Gltf\` (relative to `Android/src/FabricationAssistant.Platform.Android/`). Use the Grep tool (NOT raw grep).

Expected: a short list of static members. As of Plan 1's exclusion notes:
- `FaImportExtractionDirectory` (string property)
- `CacheDirectory` (string property — already exposed by current bootstrap)
- `DracoDecodeDirectory` (string property)
- `ConstrainDirectoryToRoot(string path)` (static method)

If the actual Grep result includes other members not in this list, note them and fold them into Task 2.

- [ ] **Step 2: Read the desktop `FabricationAssistantPaths.cs` once to copy the signatures verbatim**

Read the file at `../../../src/FabricationAssistant.Core/Runtime/FabricationAssistantPaths.cs` (relative to `Android/src/FabricationAssistant.Platform.Android/`).

Capture the exact signatures of:
- `FaImportExtractionDirectory` (note: return type, getter expression, any conditional logic).
- `DracoDecodeDirectory`.
- `ConstrainDirectoryToRoot(string)` — parameters, return type, exceptions thrown.

These are the signatures Task 2 must mirror.

---

### Task 2: Extend FabricationAssistantPathsBootstrap

**Files:**
- Modify: `Android/src/FabricationAssistant.Platform.Android/FabricationAssistantPathsBootstrap.cs`

- [ ] **Step 1: Rewrite the file with the extended surface**

Use Write to replace the file contents (or Edit to append properties). The full intended content:

```csharp
namespace FabricationAssistant.Runtime;

/// <summary>
/// Android replacement for the desktop FabricationAssistantPaths static class.
/// MUST be initialized once at startup via Initialize(paths) before any code
/// that reads the static members runs.
/// </summary>
public static class FabricationAssistantPaths
{
    private static FabricationAssistant.Platform.IPlatformPaths? _paths;

    public static void Initialize(FabricationAssistant.Platform.IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    private static FabricationAssistant.Platform.IPlatformPaths Paths
        => _paths ?? throw new InvalidOperationException(
            "FabricationAssistantPaths.Initialize(IPlatformPaths) must be called at startup before any path lookup.");

    public static string RootPath => Paths.AppDataRoot;
    public static string CacheDirectory => Paths.CacheDir;
    public static string TempDirectory => Paths.TempDir;
    public static string LogsDirectory => Paths.LogsDir;

    /// <summary>
    /// Directory under which FA package GLB content is extracted for import.
    /// Mirrors the desktop static layout: under TempDirectory/fa-extracts.
    /// </summary>
    public static string FaImportExtractionDirectory
    {
        get
        {
            var dir = Path.Combine(Paths.TempDir, "fa-extracts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Directory the desktop app uses for Draco-decoded GLB caching. On Android,
    /// Draco helper exe is not available — but the property must exist so the
    /// linked GltfImportService compiles. If Draco transcoding is actually
    /// attempted at runtime the helper-exe Process.Start will throw; that
    /// failure should be surfaced to the user, not caught silently.
    /// </summary>
    public static string DracoDecodeDirectory
    {
        get
        {
            var dir = Path.Combine(Paths.CacheDir, "draco-decode");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Ensures `path` is within `root` (path traversal guard). Returns the
    /// fully-resolved `path` if it is, otherwise throws.
    /// </summary>
    public static string ConstrainDirectoryToRoot(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var pathFull = Path.GetFullPath(path);
        if (!pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
            pathFull + Path.DirectorySeparatorChar != rootFull)
        {
            throw new InvalidOperationException($"Path '{path}' escapes root '{root}'.");
        }
        return pathFull;
    }
}
```

If Task 1 Step 2 revealed a different signature for `ConstrainDirectoryToRoot` (e.g., it takes only one parameter and uses an implicit root), match the desktop signature exactly. The body above is the safe behaviour; the SIGNATURE must match what the linked source calls.

- [ ] **Step 2: Build Platform.Android in isolation**

Run: `dotnet build "Android/src/FabricationAssistant.Platform.Android/FabricationAssistant.Platform.Android.csproj" -c Debug`
Expected: build succeeds with 0 errors.

If `ConstrainDirectoryToRoot` complains about signature mismatch when the Import.Gltf shim re-includes the four files (Task 3): come back to this task and adjust the signature.

---

### Task 3: Un-exclude the four Import.Gltf files

**Files:**
- Modify: `Android/src/FabricationAssistant.Import.Gltf.Android/FabricationAssistant.Import.Gltf.Android.csproj`

- [ ] **Step 1: Read the current csproj to see the existing `<Compile Remove>` entries**

Read the file. The existing exclusions list:

```xml
<Compile Remove="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Import.Gltf\FaImportService.cs" />
<Compile Remove="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Import.Gltf\FaPackageCacheStore.cs" />
<Compile Remove="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Import.Gltf\FaPackageStorageOptions.cs" />
<Compile Remove="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Import.Gltf\GltfImportService.cs" />
```

(The actual paths/spelling may differ — confirm via Read first.)

- [ ] **Step 2: Remove all four `<Compile Remove>` entries**

Use Edit to delete each `<Compile Remove>` line. After the edit, the csproj should have no `<Compile Remove>` entries (the global `<Compile Include>` glob still excludes `bin/` and `obj/` via its `Exclude` attribute).

- [ ] **Step 3: Build the full solution**

Run: `& "Android/tools/build.ps1" -Configuration Debug`

Expected: build succeeds. If any of the four files report compile errors:
- If the error is "missing static member on FabricationAssistantPaths": go back to Task 2 and add the missing member. Document exactly which member.
- If the error is a WPF / Windows-only API (`Microsoft.Win32`, `Application.Current`, etc.): the file genuinely needs to stay excluded. Re-add it to the csproj's `<Compile Remove>` and document the reason.

- [ ] **Step 4: Verify desktop tree untouched**

Run: `git status --porcelain "src/"`
Expected: empty.

---

## Phase 2: GPU mesh upload primitives

### Task 4: Create GpuMesh

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GpuMesh.cs`

- [ ] **Step 1: Write the file**

```csharp
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// One GPU mesh: VAO + VBO + EBO + draw count. Positions are float32 (x,y,z),
/// normals float32 (x,y,z), colors float32 (r,g,b). Indices are uint32.
/// Plan 2A uses a fixed layout; Plan 2B+ may add per-vertex IDs (for picking)
/// and packed normals.
/// </summary>
public sealed class GpuMesh : IDisposable
{
    public uint Vao { get; private set; }
    public uint Vbo { get; private set; }
    public uint Ebo { get; private set; }
    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }

    private readonly GL _gl;

    public GpuMesh(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        Vao = _gl.GenVertexArray();
        Vbo = _gl.GenBuffer();
        Ebo = _gl.GenBuffer();
    }

    /// <summary>
    /// Upload interleaved vertex data + indices. The layout is:
    ///   attribute 0: position vec3 (offset 0)
    ///   attribute 1: normal   vec3 (offset 12)
    ///   attribute 2: color    vec3 (offset 24)
    /// Stride: 36 bytes (9 floats).
    /// </summary>
    public unsafe void Upload(
        ReadOnlySpan<float> positions,
        ReadOnlySpan<float> normals,
        ReadOnlySpan<float> colors,
        ReadOnlySpan<uint> indices)
    {
        if (positions.Length % 3 != 0)
            throw new ArgumentException("positions length must be a multiple of 3", nameof(positions));
        if (normals.Length != positions.Length)
            throw new ArgumentException("normals length must match positions length", nameof(normals));
        if (colors.Length != positions.Length)
            throw new ArgumentException("colors length must match positions length", nameof(colors));

        int vertexCount = positions.Length / 3;
        var interleaved = new float[vertexCount * 9];
        for (int i = 0; i < vertexCount; i++)
        {
            interleaved[i * 9 + 0] = positions[i * 3 + 0];
            interleaved[i * 9 + 1] = positions[i * 3 + 1];
            interleaved[i * 9 + 2] = positions[i * 3 + 2];
            interleaved[i * 9 + 3] = normals[i * 3 + 0];
            interleaved[i * 9 + 4] = normals[i * 3 + 1];
            interleaved[i * 9 + 5] = normals[i * 3 + 2];
            interleaved[i * 9 + 6] = colors[i * 3 + 0];
            interleaved[i * 9 + 7] = colors[i * 3 + 1];
            interleaved[i * 9 + 8] = colors[i * 3 + 2];
        }

        _gl.BindVertexArray(Vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, Vbo);
        fixed (float* p = interleaved)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleaved.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, Ebo);
        fixed (uint* p = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        }

        const int stride = 9 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);

        VertexCount = vertexCount;
        IndexCount = indices.Length;
    }

    public void Draw()
    {
        _gl.BindVertexArray(Vao);
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (Vao != 0)
        {
            _gl.DeleteVertexArray(Vao);
            Vao = 0;
        }
        if (Vbo != 0)
        {
            _gl.DeleteBuffer(Vbo);
            Vbo = 0;
        }
        if (Ebo != 0)
        {
            _gl.DeleteBuffer(Ebo);
            Ebo = 0;
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

### Task 5: Create the mesh.gles.vert + mesh.gles.frag shaders

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/mesh.gles.vert`
- Create: `Android/src/FabricationAssistant.Rendering.Gles/Shaders/mesh.gles.frag`

- [ ] **Step 1: Write `mesh.gles.vert`**

```glsl
#version 310 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec3 aColor;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMatrix;

out vec3 vNormalWorld;
out vec3 vColor;
out vec3 vPositionWorld;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vPositionWorld = worldPos.xyz;
    vNormalWorld = normalize(uNormalMatrix * aNormal);
    vColor = aColor;
    gl_Position = uProjection * uView * worldPos;
}
```

Verify the file is ASCII-only. Saved feedback: GLSL files with non-ASCII bytes break NVIDIA's preprocessor with misleading "unexpected $end at EOF" errors.

- [ ] **Step 2: Write `mesh.gles.frag`**

```glsl
#version 310 es
precision highp float;

in vec3 vNormalWorld;
in vec3 vColor;
in vec3 vPositionWorld;

uniform vec3 uCameraPosWorld;
uniform vec3 uLightDirWorld;

out vec4 fragColor;

void main()
{
    vec3 N = normalize(vNormalWorld);
    vec3 L = normalize(-uLightDirWorld);
    vec3 V = normalize(uCameraPosWorld - vPositionWorld);
    vec3 H = normalize(L + V);

    float ambient = 0.25;
    float diffuse = max(dot(N, L), 0.0);
    float specular = pow(max(dot(N, H), 0.0), 32.0) * 0.15;

    vec3 lit = vColor * (ambient + diffuse) + vec3(specular);
    fragColor = vec4(lit, 1.0);
}
```

ASCII-only. No emojis.

- [ ] **Step 3: Add the shaders folder to the csproj as `EmbeddedResource`**

Modify `Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj` — append an `ItemGroup` BEFORE the closing `</Project>`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Shaders\*.vert" />
    <EmbeddedResource Include="Shaders\*.frag" />
  </ItemGroup>
```

This makes the .vert / .frag files embedded resources accessible via `Assembly.GetManifestResourceStream`.

- [ ] **Step 4: Build to verify the embedded resources are included**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

Run: `dotnet ildasm` is unavailable; instead, check the DLL with PowerShell:
```powershell
[System.Reflection.Assembly]::LoadFrom("C:\Users\skritikos\Desktop\Fabrication Assistant\Android\src\FabricationAssistant.Rendering.Gles\bin\Debug\FabricationAssistant.Rendering.Gles.dll").GetManifestResourceNames()
```
Expected: lists `FabricationAssistant.Rendering.Gles.Shaders.mesh.gles.vert` and `.frag`.

If listed: proceed. If not, re-check the `<EmbeddedResource Include>` glob path (the `\` may need to be `/` on case-sensitive systems, but on Windows backslash is fine).

---

## Phase 3: Scene representation and upload

### Task 6: Create SceneUploader (DocumentDto -> GpuMesh list)

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/SceneUploader.cs`

- [ ] **Step 1: Investigate Core DTO field names**

Use Read on `../../../src/FabricationAssistant.Core/SceneGraph/DocumentDto.cs` (relative to `Android/src/FabricationAssistant.Rendering.Gles/`) to confirm the property names you'll use in SceneUploader.

Use Read on `../../../src/FabricationAssistant.Core/SceneGraph/MeshDto.cs` similarly. Capture:
- The property name for vertex positions (likely `Positions` or `Vertices`)
- For normals (likely `Normals`)
- For colors per vertex (likely `Colors` or per-material — verify)
- For triangle indices (likely `Indices`)

The exact names guide the upload code in Step 2.

- [ ] **Step 2: Write the uploader**

```csharp
using FabricationAssistant.Core.SceneGraph;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Walks a DocumentDto and creates a GpuMesh for each MeshDto it finds.
/// Plan 2A treats the scene as a flat mesh list — node transforms are not
/// composited (each MeshDto rendered at world origin). Plan 2B will introduce
/// per-node transforms.
/// </summary>
public static class SceneUploader
{
    public static List<GpuMesh> Upload(GL gl, DocumentDto document)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(document);

        var meshes = new List<GpuMesh>();

        foreach (var meshDto in document.Meshes)
        {
            var gpu = new GpuMesh(gl);
            gpu.Upload(
                meshDto.Positions.AsSpan(),
                meshDto.Normals.AsSpan(),
                ResolveVertexColors(meshDto),
                meshDto.Indices.AsSpan());
            meshes.Add(gpu);
        }

        return meshes;
    }

    private static ReadOnlySpan<float> ResolveVertexColors(MeshDto mesh)
    {
        if (mesh.Colors is { Length: > 0 } c)
            return c.AsSpan();

        // No per-vertex colors authored: synthesize a constant white array so the
        // upload code path is uniform. Allocates per-mesh; fine for Plan 2A.
        int n = mesh.Positions.Length;
        var white = new float[n];
        white.AsSpan().Fill(0.75f);
        return white;
    }
}
```

**Adjust field/property names to match what Task 6 Step 1 reveals.** The above uses guessed names — confirm against the actual DTOs.

If the DTOs use different names (e.g., `Vertices` not `Positions`, or `Triangles` not `Indices`), adjust the field references and re-run the build.

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors. If the DTO names don't match the code above, the build fails with `CS0117: 'MeshDto' does not contain a definition for 'Positions'` (or similar). Fix the property name based on what Read revealed.

---

### Task 7: Create GpuScene

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GpuScene.cs`

- [ ] **Step 1: Write the file**

```csharp
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// The collection of GPU meshes for the currently-loaded document, plus the
/// world-space bounding box that frames the scene. The renderer uses this to
/// fit the camera and to draw.
/// </summary>
public sealed class GpuScene : IDisposable
{
    private readonly GL _gl;
    private readonly List<GpuMesh> _meshes = new();

    public GpuScene(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
    }

    public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

    public IReadOnlyList<GpuMesh> Meshes => _meshes;

    public void Load(DocumentDto document)
    {
        Clear();
        foreach (var m in SceneUploader.Upload(_gl, document))
            _meshes.Add(m);
        Bounds = ComputeBounds(document);
    }

    private static BoundingBox ComputeBounds(DocumentDto document)
    {
        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        bool any = false;
        foreach (var mesh in document.Meshes)
        {
            var p = mesh.Positions;
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                min = new Vector3d(System.Math.Min(min.X, p[i]), System.Math.Min(min.Y, p[i + 1]), System.Math.Min(min.Z, p[i + 2]));
                max = new Vector3d(System.Math.Max(max.X, p[i]), System.Math.Max(max.Y, p[i + 1]), System.Math.Max(max.Z, p[i + 2]));
                any = true;
            }
        }
        return any ? new BoundingBox(min, max) : BoundingBox.Empty;
    }

    public void Draw()
    {
        foreach (var m in _meshes) m.Draw();
    }

    public void Clear()
    {
        foreach (var m in _meshes) m.Dispose();
        _meshes.Clear();
        Bounds = BoundingBox.Empty;
    }

    public void Dispose() => Clear();
}
```

If `BoundingBox.Empty` doesn't exist or has a different name in Core: use `default(BoundingBox)` or look up the actual API via Read of `../../../src/FabricationAssistant.Core/Math/BoundingBox.cs`.

If `BoundingBox(Vector3d min, Vector3d max)` constructor doesn't exist (e.g., it might take floats or have a different signature): adjust to match the real API.

- [ ] **Step 2: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

## Phase 4: Camera math + renderer integration

### Task 8: Create ViewportCameraMath

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/ViewportCameraMath.cs`

- [ ] **Step 1: Investigate Core CameraState API**

Read `../../../src/FabricationAssistant.Core/Camera/CameraState.cs` (path may differ — Glob `..\..\..\src\FabricationAssistant.Core\**\CameraState.cs` first).

Capture:
- The property exposing the eye position (likely `Position` or `Eye`).
- The property exposing the look-at target (likely `Target`).
- The property exposing the up direction (likely `UpDirection` or `Up`).
- The property exposing FOV (likely `FieldOfView`, in radians).
- The property exposing near/far clip planes.
- Whether `Matrix4d.CreateLookAt(...)` exists in Core/Math and its signature.
- Whether `Matrix4d.CreatePerspectiveFieldOfView(...)` or similar exists.

- [ ] **Step 2: Write the file**

```csharp
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Builds 4x4 view and projection matrices from a CameraState. The desktop renderer
/// builds these internally; on Android we expose them as a separate utility so the
/// renderer code can pass them to the shader as plain float[16] uniforms.
/// </summary>
public static class ViewportCameraMath
{
    public static float[] ViewMatrix(CameraState camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var view = Matrix4d.CreateLookAt(camera.Position, camera.Target, camera.UpDirection);
        return ToColumnMajorFloats(view);
    }

    public static float[] ProjectionMatrix(CameraState camera, float aspect)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var fov = camera.FieldOfView;
        var near = camera.NearClip;
        var far = camera.FarClip;
        var proj = Matrix4d.CreatePerspectiveFieldOfView(fov, aspect, near, far);
        return ToColumnMajorFloats(proj);
    }

    public static float[] IdentityModelMatrix() => new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    };

    public static float[] NormalMatrixFromModel(float[] model)
    {
        // Plan 2A uses identity model -> normal matrix is identity 3x3.
        // Plan 2B replaces this with a proper inverse-transpose computation.
        return new float[]
        {
            1, 0, 0,
            0, 1, 0,
            0, 0, 1,
        };
    }

    private static float[] ToColumnMajorFloats(Matrix4d m)
    {
        // Matrix4d storage convention in Core's math layer needs to be verified
        // against the desktop renderer's expectation. The desktop uses
        // "right-handed, column-vectors, pre-multiply" per audit Section 9.1,
        // which means GLSL `column_major` is the natural layout. If the Core
        // matrix is row-major in memory, transpose here.
        return new float[]
        {
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44,
        };
    }
}
```

Adjust:
- Property names (`Position`, `Target`, `UpDirection`, `FieldOfView`, `NearClip`, `FarClip`) to match what Read revealed.
- The Matrix4d field accessors (`M11`, `M12`, ...) — if Core uses different naming (e.g., `Row0.X` style), adjust.
- The `CreatePerspectiveFieldOfView` / `CreateLookAt` signatures.

If the Core math API is fundamentally different (e.g., no CreateLookAt method), compute the view matrix inline:

```csharp
var forward = (camera.Target - camera.Position).Normalized();
var right = Vector3d.Cross(forward, camera.UpDirection).Normalized();
var up = Vector3d.Cross(right, forward);
// Then build the matrix manually.
```

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

### Task 9: Replace GlesViewportRenderer with scene-rendering version

**Files:**
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs`

- [ ] **Step 1: Replace the file with the full scene renderer**

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Plan 2A renderer. Compiles the mesh program, holds a GpuScene reference,
/// and draws every frame using uniforms derived from a CameraState supplied
/// from outside (the App project sets CameraState before each RequestRender).
/// </summary>
public sealed class GlesViewportRenderer
{
    private readonly GlThreadGuard _guard = new();
    private GL? _gl;
    private ShaderProgram? _meshProgram;
    private bool _initialized;
    private int _width;
    private int _height;

    public GpuScene? Scene { get; set; }
    public CameraState? Camera { get; set; }

    public void OnSurfaceCreated()
    {
        _guard.Initialize();

        _gl = GL.GetApi(new SurfaceViewGlContext());

        _gl.ClearColor(0.10f, 0.11f, 0.12f, 1.0f);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

        var vs = LoadEmbeddedShader("Shaders.mesh.gles.vert");
        var fs = LoadEmbeddedShader("Shaders.mesh.gles.frag");
        _meshProgram = new ShaderProgram(_gl, "mesh", vs, fs);

        _initialized = true;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        _guard.EnsureOnRenderThread();
        if (_gl is null) return;
        _width = width;
        _height = height;
        _gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    public void OnDrawFrame()
    {
        _guard.EnsureOnRenderThread();
        if (!_initialized || _gl is null || _meshProgram is null) return;

        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (Scene is null || Camera is null || _width == 0 || _height == 0)
            return;

        _meshProgram.Use();

        var aspect = (float)_width / _height;
        var model = ViewportCameraMath.IdentityModelMatrix();
        var view = ViewportCameraMath.ViewMatrix(Camera);
        var proj = ViewportCameraMath.ProjectionMatrix(Camera, aspect);
        var normal = ViewportCameraMath.NormalMatrixFromModel(model);

        SetMat4(_meshProgram.Handle, "uModel", model);
        SetMat4(_meshProgram.Handle, "uView", view);
        SetMat4(_meshProgram.Handle, "uProjection", proj);
        SetMat3(_meshProgram.Handle, "uNormalMatrix", normal);
        SetVec3(_meshProgram.Handle, "uCameraPosWorld",
            (float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        // Light direction: a fixed key-light from upper-front-left in world space.
        SetVec3(_meshProgram.Handle, "uLightDirWorld", -0.4f, -0.6f, -0.7f);

        Scene.Draw();
    }

    private void SetMat4(uint program, string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(program, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, false, m);
    }

    private void SetMat3(uint program, string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(program, name);
        if (loc < 0) return;
        _gl.UniformMatrix3(loc, false, m);
    }

    private void SetVec3(uint program, string name, float x, float y, float z)
    {
        int loc = _gl!.GetUniformLocation(program, name);
        if (loc < 0) return;
        _gl.Uniform3(loc, x, y, z);
    }

    private static string LoadEmbeddedShader(string relativeName)
    {
        var asm = typeof(GlesViewportRenderer).Assembly;
        var full = "FabricationAssistant.Rendering.Gles." + relativeName;
        using var s = asm.GetManifestResourceStream(full)
            ?? throw new InvalidOperationException("Embedded shader not found: " + full);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public int Width => _width;
    public int Height => _height;
}

internal sealed class SurfaceViewGlContext : Silk.NET.Core.Contexts.INativeContext
{
    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        addr = nint.Zero;
        if (NativeLibrary.TryLoad("libGLESv3.so", out var libv3) &&
            NativeLibrary.TryGetExport(libv3, proc, out var sym))
        {
            addr = sym;
        }
        if (addr == nint.Zero && NativeLibrary.TryLoad("libGLESv2.so", out var libv2))
        {
            if (NativeLibrary.TryGetExport(libv2, proc, out var sym2)) addr = sym2;
        }
        return addr != nint.Zero;
    }

    public nint GetProcAddress(string proc, int? slot = null)
    {
        if (TryGetProcAddress(proc, out var addr, slot)) return addr;
        throw new EntryPointNotFoundException("GLES function not found: " + proc);
    }

    public void Dispose() { }
}
```

- [ ] **Step 2: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors. Note that the previous Plan 1 GlesViewportRenderer is REPLACED; the SurfaceViewGlContext inner class moves to its own internal class at the bottom of the same file (do NOT duplicate it).

---

## Phase 5: SAF file open + import pipeline

### Task 10: Implement SafFilePicker

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/SafFilePicker.cs`

- [ ] **Step 1: Write the file**

```csharp
using Android.App;
using Android.Content;
using Android.Net;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.AppCompat.App;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Wraps Storage Access Framework ACTION_OPEN_DOCUMENT into a single
/// Task<Uri?> call. The contract is registered once during MainActivity.OnCreate;
/// PickAsync() can be called repeatedly afterwards.
/// </summary>
public sealed class SafFilePicker
{
    private readonly AppCompatActivity _activity;
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<Uri?>? _pending;

    public SafFilePicker(AppCompatActivity activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));

        var contract = new ActivityResultContracts.OpenDocument();
        var callback = new ResultCallback<Uri>(uri =>
        {
            var p = _pending;
            _pending = null;
            p?.TrySetResult(uri);
        });

        _launcher = _activity.RegisterForActivityResult(contract, callback)
            ?? throw new InvalidOperationException("RegisterForActivityResult returned null");
    }

    /// <summary>
    /// Launch the picker. Returns the picked content URI, or null if the user cancelled.
    /// </summary>
    public Task<Uri?> PickAsync(string[] mimeTypes)
    {
        ArgumentNullException.ThrowIfNull(mimeTypes);
        if (_pending is not null)
            throw new InvalidOperationException("A file picker is already active.");

        _pending = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _launcher.Launch(mimeTypes);
        return _pending.Task;
    }

    private sealed class ResultCallback<T> : Java.Lang.Object, IActivityResultCallback
        where T : class
    {
        private readonly Action<T?> _action;
        public ResultCallback(Action<T?> action) { _action = action; }
        public void OnActivityResult(Java.Lang.Object? result) => _action(result as T);
    }
}
```

If `AndroidX.Activity.Result` types are not found, the NuGet package providing them needs to be added. They typically come transitively from `Xamarin.AndroidX.Activity` (which is a dependency of `Xamarin.AndroidX.AppCompat`). If a build error says they're missing, add `Xamarin.AndroidX.Activity` as an explicit `<PackageReference>` in `FabricationAssistant.App.Android.csproj`.

- [ ] **Step 2: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

### Task 11: Implement ImportPipeline

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/ImportPipeline.cs`

- [ ] **Step 1: Read what GltfImportService expects**

Read `../../../src/FabricationAssistant.Import.Gltf/GltfImportService.cs` to confirm:
- The public entry point's signature (`ImportAsync(string path, ...) : Task<DocumentDto>` or similar).
- What it does on failure (throws? returns null?).
- What progress reporting API it uses (`IProgress<...>`).

- [ ] **Step 2: Write the pipeline**

```csharp
using Android.Content;
using Android.Net;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Import.Gltf;
using FabricationAssistant.Platform;

namespace FabricationAssistant.App.Android;

/// <summary>
/// Orchestrates the SAF-content-URI -> local cached file -> GltfImportService
/// flow. The result is a managed DocumentDto that the GL render thread can
/// then upload via SceneUploader.
/// </summary>
public sealed class ImportPipeline
{
    private readonly Context _context;
    private readonly IPlatformPaths _paths;

    public ImportPipeline(Context context, IPlatformPaths paths)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<DocumentDto> ImportAsync(Uri contentUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contentUri);

        var fileName = ResolveFileName(contentUri) ?? "model.glb";
        var localPath = await CopyToLocalAsync(contentUri, fileName, ct).ConfigureAwait(false);

        var service = new GltfImportService();
        // The GltfImportService signature is verified at Task 11 Step 1; the call
        // below uses the canonical positional shape. Adjust if the actual API
        // takes a settings object or progress callback.
        return await Task.Run(() => service.Import(localPath), ct).ConfigureAwait(false);
    }

    private async Task<string> CopyToLocalAsync(Uri uri, string fileName, CancellationToken ct)
    {
        var importCacheRoot = Path.Combine(_paths.AppDataRoot, "import-cache");
        Directory.CreateDirectory(importCacheRoot);
        var localPath = Path.Combine(importCacheRoot, fileName);

        using var input = _context.ContentResolver?.OpenInputStream(uri)
            ?? throw new InvalidOperationException("ContentResolver.OpenInputStream returned null for " + uri);

        await using var output = File.Create(localPath);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return localPath;
    }

    private string? ResolveFileName(Uri uri)
    {
        try
        {
            using var cursor = _context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst()) return null;
            int idx = cursor.GetColumnIndex(global::Android.Provider.OpenableColumns.DisplayName);
            if (idx < 0) return null;
            return cursor.GetString(idx);
        }
        catch
        {
            return null;
        }
    }
}
```

The `service.Import(localPath)` call may not match the actual `GltfImportService` API. Task 11 Step 1 reveals the real signature:
- If it's `ImportAsync(string path) : Task<DocumentDto>`, replace `service.Import(localPath)` with `await service.ImportAsync(localPath)` (no extra `Task.Run`).
- If it takes a settings record, construct `new GltfImportSettings()` and pass it.
- If it returns a different DTO type, adjust the return type accordingly.

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

### Task 12: Wire MainActivity to launch SAF and trigger import

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/layout/activity_main.xml`
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/values/strings.xml`
- Modify: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`
- Modify: `Android/src/FabricationAssistant.App.Android/AppServices.cs`

- [ ] **Step 1: Add the toolbar + Open button to `activity_main.xml`**

Replace the file:

```xml
<?xml version="1.0" encoding="utf-8"?>
<androidx.constraintlayout.widget.ConstraintLayout
    xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:app="http://schemas.android.com/apk/res-auto"
    android:id="@+id/root"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:background="@color/md_theme_surface">

    <com.google.android.material.appbar.MaterialToolbar
        android:id="@+id/topAppBar"
        android:layout_width="match_parent"
        android:layout_height="?attr/actionBarSize"
        android:background="@color/md_theme_surfaceVariant"
        android:elevation="4dp"
        app:title="@string/app_name"
        app:titleTextColor="@color/md_theme_onSurface"
        app:layout_constraintTop_toTopOf="parent"
        app:layout_constraintStart_toStartOf="parent"
        app:layout_constraintEnd_toEndOf="parent">

        <com.google.android.material.button.MaterialButton
            android:id="@+id/openButton"
            android:layout_width="wrap_content"
            android:layout_height="wrap_content"
            android:layout_gravity="end|center_vertical"
            android:layout_marginEnd="8dp"
            android:text="@string/open_button"
            style="@style/Widget.Material3.Button.TonalButton" />
    </com.google.android.material.appbar.MaterialToolbar>

    <FrameLayout
        android:id="@+id/viewportContainer"
        android:layout_width="0dp"
        android:layout_height="0dp"
        app:layout_constraintTop_toBottomOf="@+id/topAppBar"
        app:layout_constraintBottom_toBottomOf="parent"
        app:layout_constraintStart_toStartOf="parent"
        app:layout_constraintEnd_toEndOf="parent" />

</androidx.constraintlayout.widget.ConstraintLayout>
```

- [ ] **Step 2: Add the `open_button` string**

Edit `Resources/values/strings.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <string name="app_name">Fabrication Assistant</string>
    <string name="open_button">Open</string>
</resources>
```

- [ ] **Step 3: Update AppServices to register the import dependencies**

Replace `AppServices.cs`:

```csharp
using Android.Content;
using FabricationAssistant.App.Android;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Selection;
using FabricationAssistant.Platform;
using FabricationAssistant.Platform.Android;
using FabricationAssistant.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace FabricationAssistant.App.Android;

public static class AppServices
{
    public static IServiceProvider Build(Context applicationContext)
    {
        ArgumentNullException.ThrowIfNull(applicationContext);

        var services = new ServiceCollection();
        services.AddSingleton<IDispatcher, AndroidDispatcher>();
        services.AddSingleton<IPlatformPaths>(_ => new AndroidPlatformPaths(applicationContext));
        services.AddSingleton(_ => applicationContext);
        services.AddSingleton<ImportPipeline>();
        services.AddSingleton<CameraState>();
        services.AddSingleton<SelectionState>();

        var provider = services.BuildServiceProvider(validateScopes: true);

        FabricationAssistantPaths.Initialize(provider.GetRequiredService<IPlatformPaths>());

        return provider;
    }
}
```

If `SelectionState` is not at namespace `FabricationAssistant.Core.Selection`, adjust via Glob to find it.

- [ ] **Step 4: Update MainActivity to wire the Open button**

Replace `MainActivity.cs`:

```csharp
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.AppCompat.App;
using FabricationAssistant.App.Android.Views;
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Platform;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;

namespace FabricationAssistant.App.Android;

[Activity(
    Label = "@string/app_name",
    Theme = "@style/Theme.FabricationAssistant",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Unspecified,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode)]
public sealed class MainActivity : AppCompatActivity
{
    private IServiceProvider? _services;
    private ViewportSurfaceView? _viewport;
    private SafFilePicker? _picker;
    private ImportPipeline? _import;
    private CameraState? _camera;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _services = AppServices.Build(ApplicationContext!);
        _import = _services.GetRequiredService<ImportPipeline>();
        _camera = _services.GetRequiredService<CameraState>();

        SetContentView(Resource.Layout.activity_main);
        _picker = new SafFilePicker(this);

        var container = FindViewById<FrameLayout>(Resource.Id.viewportContainer)
            ?? throw new InvalidOperationException("viewportContainer not found");

        _viewport = new ViewportSurfaceView(this);
        _viewport.Renderer.Camera = _camera;
        container.AddView(_viewport);

        var openButton = FindViewById<MaterialButton>(Resource.Id.openButton);
        if (openButton is not null) openButton.Click += OnOpenClicked;
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        if (_picker is null || _import is null || _viewport is null) return;

        var uri = await _picker.PickAsync(new[] { "model/gltf-binary", "application/octet-stream" });
        if (uri is null) return;

        try
        {
            var document = await _import.ImportAsync(uri, CancellationToken.None);
            _viewport.QueueRendererCommand(gl =>
            {
                var scene = _viewport.Renderer.Scene ?? new GpuScene(gl);
                scene.Load(document);
                _viewport.Renderer.Scene = scene;
                if (_camera is not null)
                {
                    _camera.FitToBox(scene.Bounds, (float)_viewport.Width / _viewport.Height);
                }
            });
            _viewport.RequestRender();
        }
        catch (Exception ex)
        {
            new AndroidX.AppCompat.App.AlertDialog.Builder(this)
                .SetTitle("Import failed")!
                .SetMessage(ex.Message)!
                .SetPositiveButton("OK", (_, _) => { })!
                .Show();
        }
    }

    protected override void OnPause() { _viewport?.OnPause(); base.OnPause(); }
    protected override void OnResume() { base.OnResume(); _viewport?.OnResume(); }
}
```

Note the `_viewport.QueueRendererCommand` and `_viewport.Renderer` member references — these are added to `ViewportSurfaceView` in Task 13.

The `CameraState.FitToBox(box, aspect)` signature comes from the desktop audit Section 9.1. If the actual signature differs, look up the real one via Read of `CameraState.cs`.

- [ ] **Step 5: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: build SUCCEEDS or fails with a clear missing-member error in `ViewportSurfaceView` (because `QueueRendererCommand` is added in Task 13). If only that error appears, this task is done; proceed.

If unrelated errors appear (e.g., `MaterialButton` not found, `FitToBox` signature mismatch), resolve them before moving on.

---

## Phase 6: Renderer command queue + GL upload glue

### Task 13: Extend ViewportSurfaceView with a render-thread command queue

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs`
- Modify: `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs`

- [ ] **Step 1: Add the queue + Renderer exposure to ViewportSurfaceView**

Replace `ViewportSurfaceView.cs`:

```csharp
using System.Collections.Concurrent;
using Android.Content;
using Android.Opengl;
using Android.Util;
using FabricationAssistant.Rendering.Gles;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.App.Android.Views;

/// <summary>
/// Hosts the GLES viewport. Owns the renderer + bridge and exposes a thread-safe
/// command queue so UI-thread import work can enqueue GL upload calls that run
/// on the GL render thread.
/// </summary>
public sealed class ViewportSurfaceView : GLSurfaceView
{
    private readonly GlesViewportRenderer _renderer;
    private readonly GlesRendererBridge _bridge;
    private readonly ConcurrentQueue<Action<GL>> _pending = new();

    public GlesViewportRenderer Renderer => _renderer;

    public ViewportSurfaceView(Context context) : base(context)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _bridge = new GlesRendererBridge(_renderer);
        ConfigureContext();
    }

    public ViewportSurfaceView(Context context, IAttributeSet attrs) : base(context, attrs)
    {
        _renderer = new GlesViewportRenderer { CommandQueue = _pending };
        _bridge = new GlesRendererBridge(_renderer);
        ConfigureContext();
    }

    public void QueueRendererCommand(Action<GL> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _pending.Enqueue(action);
        RequestRender();
    }

    private void ConfigureContext()
    {
        SetEGLContextClientVersion(3);
        SetEGLConfigChooser(8, 8, 8, 0, 24, 8);
        SetRenderer(_bridge);
        RenderMode = Rendermode.WhenDirty;
    }
}
```

- [ ] **Step 2: Drain the queue at the top of OnDrawFrame in GlesViewportRenderer**

Modify `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs` — add a `CommandQueue` setter and drain at frame start. Use Edit to add the property and the drain logic, OR replace the file with the complete updated version below:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using FabricationAssistant.Core.Camera;
using Silk.NET.OpenGLES;

namespace FabricationAssistant.Rendering.Gles;

public sealed class GlesViewportRenderer
{
    private readonly GlThreadGuard _guard = new();
    private GL? _gl;
    private ShaderProgram? _meshProgram;
    private bool _initialized;
    private int _width;
    private int _height;

    public GpuScene? Scene { get; set; }
    public CameraState? Camera { get; set; }
    public ConcurrentQueue<Action<GL>>? CommandQueue { get; set; }

    public void OnSurfaceCreated()
    {
        _guard.Initialize();

        _gl = GL.GetApi(new SurfaceViewGlContext());

        _gl.ClearColor(0.10f, 0.11f, 0.12f, 1.0f);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

        var vs = LoadEmbeddedShader("Shaders.mesh.gles.vert");
        var fs = LoadEmbeddedShader("Shaders.mesh.gles.frag");
        _meshProgram = new ShaderProgram(_gl, "mesh", vs, fs);

        _initialized = true;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        _guard.EnsureOnRenderThread();
        if (_gl is null) return;
        _width = width;
        _height = height;
        _gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    public void OnDrawFrame()
    {
        _guard.EnsureOnRenderThread();
        if (!_initialized || _gl is null || _meshProgram is null) return;

        // Drain pending GL-thread commands (e.g., scene uploads).
        if (CommandQueue is { } q)
        {
            while (q.TryDequeue(out var cmd)) cmd(_gl);
        }

        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (Scene is null || Camera is null || _width == 0 || _height == 0)
            return;

        _meshProgram.Use();

        var aspect = (float)_width / _height;
        var model = ViewportCameraMath.IdentityModelMatrix();
        var view = ViewportCameraMath.ViewMatrix(Camera);
        var proj = ViewportCameraMath.ProjectionMatrix(Camera, aspect);
        var normal = ViewportCameraMath.NormalMatrixFromModel(model);

        SetMat4(_meshProgram.Handle, "uModel", model);
        SetMat4(_meshProgram.Handle, "uView", view);
        SetMat4(_meshProgram.Handle, "uProjection", proj);
        SetMat3(_meshProgram.Handle, "uNormalMatrix", normal);
        SetVec3(_meshProgram.Handle, "uCameraPosWorld",
            (float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        SetVec3(_meshProgram.Handle, "uLightDirWorld", -0.4f, -0.6f, -0.7f);

        Scene.Draw();
    }

    private void SetMat4(uint program, string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(program, name);
        if (loc < 0) return;
        _gl.UniformMatrix4(loc, false, m);
    }

    private void SetMat3(uint program, string name, float[] m)
    {
        int loc = _gl!.GetUniformLocation(program, name);
        if (loc < 0) return;
        _gl.UniformMatrix3(loc, false, m);
    }

    private void SetVec3(uint program, string name, float x, float y, float z)
    {
        int loc = _gl!.GetUniformLocation(program, name);
        if (loc < 0) return;
        _gl.Uniform3(loc, x, y, z);
    }

    private static string LoadEmbeddedShader(string relativeName)
    {
        var asm = typeof(GlesViewportRenderer).Assembly;
        var full = "FabricationAssistant.Rendering.Gles." + relativeName;
        using var s = asm.GetManifestResourceStream(full)
            ?? throw new InvalidOperationException("Embedded shader not found: " + full);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public int Width => _width;
    public int Height => _height;
}

internal sealed class SurfaceViewGlContext : Silk.NET.Core.Contexts.INativeContext
{
    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        addr = nint.Zero;
        if (NativeLibrary.TryLoad("libGLESv3.so", out var libv3) &&
            NativeLibrary.TryGetExport(libv3, proc, out var sym))
        {
            addr = sym;
        }
        if (addr == nint.Zero && NativeLibrary.TryLoad("libGLESv2.so", out var libv2))
        {
            if (NativeLibrary.TryGetExport(libv2, proc, out var sym2)) addr = sym2;
        }
        return addr != nint.Zero;
    }

    public nint GetProcAddress(string proc, int? slot = null)
    {
        if (TryGetProcAddress(proc, out var addr, slot)) return addr;
        throw new EntryPointNotFoundException("GLES function not found: " + proc);
    }

    public void Dispose() { }
}
```

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors. The full pipeline now compiles.

---

## Phase 7: Touch gestures

### Task 14: Implement AndroidPointerSource

**Files:**
- Modify: `Android/src/FabricationAssistant.Input.Gestures.Android/AndroidPointerSource.cs`

- [ ] **Step 1: Read the recognizer's input API**

Glob for `ViewportTouchGestureRecognizer.cs` in the desktop sources to confirm the input shape:

Glob pattern: `..\..\..\src\FabricationAssistant.Core\**\ViewportTouchGestureRecognizer.cs` (relative to `Android/src/FabricationAssistant.Input.Gestures.Android/`).

Read the file. Capture:
- The public method name that ingests pointer events (likely `OnPointerEvent(...)`).
- The event-args struct (likely `PointerEvent { int Id, float X, float Y, long TimestampMs }`).
- The pointer action enum (likely `PointerAction { Down, Move, Up, Cancel }`).

- [ ] **Step 2: Implement the translation**

Replace `AndroidPointerSource.cs` with the full implementation. The skeleton below shows the structure; adjust the method/type names to match what the recognizer actually uses.

```csharp
using Android.Util;
using Android.Views;
using FabricationAssistant.Core.Input;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Bridges Android MotionEvent into the FabricationAssistant gesture recognizer.
/// Density-scales coordinates so the recognizer's pixel thresholds align with
/// desktop DIPs.
/// </summary>
public sealed class AndroidPointerSource
{
    private readonly ViewportTouchGestureRecognizer _recognizer;
    private readonly float _density;

    public AndroidPointerSource(ViewportTouchGestureRecognizer recognizer, DisplayMetrics metrics)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        ArgumentNullException.ThrowIfNull(metrics);
        _density = metrics.Density <= 0f ? 1f : metrics.Density;
    }

    public bool OnTouch(MotionEvent? motionEvent)
    {
        if (motionEvent is null) return false;

        long t = motionEvent.EventTime;
        var action = motionEvent.ActionMasked;
        int actionIndex = motionEvent.ActionIndex;

        switch (action)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                Emit(motionEvent, actionIndex, PointerAction.Down, t);
                return true;

            case MotionEventActions.Move:
                for (int i = 0; i < motionEvent.PointerCount; i++)
                    Emit(motionEvent, i, PointerAction.Move, t);
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                Emit(motionEvent, actionIndex, PointerAction.Up, t);
                return true;

            case MotionEventActions.Cancel:
                for (int i = 0; i < motionEvent.PointerCount; i++)
                    Emit(motionEvent, i, PointerAction.Cancel, t);
                return true;
        }
        return false;
    }

    private void Emit(MotionEvent e, int index, PointerAction action, long t)
    {
        int id = e.GetPointerId(index);
        float x = e.GetX(index) / _density;
        float y = e.GetY(index) / _density;
        _recognizer.OnPointerEvent(new PointerEvent(id, action, x, y, t));
    }
}
```

If `ViewportTouchGestureRecognizer`, `PointerEvent`, `PointerAction` live in a different namespace than `FabricationAssistant.Core.Input`, fix the `using` directive and the type qualifiers.

If the recognizer's API uses `Inject(int id, float x, float y, long t)` etc., use that signature instead.

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors. If the recognizer's namespace/types differ, fix references until clean.

---

### Task 15: Implement ViewportInteractionAdapter

**Files:**
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/ViewportInteractionAdapter.cs`

- [ ] **Step 1: Read what gesture events the recognizer emits**

From Task 14 Step 1's read of `ViewportTouchGestureRecognizer.cs`, identify the events:
- `OrbitBegin`, `OrbitDelta(float dx, float dy)`, `OrbitEnd`
- `PanZoomBegin`, `PanZoomDelta(float panDx, float panDy, float pinchScale)`, `PanZoomEnd`
- `Tap(float x, float y)`, `DoubleTap`, `LongPress`

Adjust the adapter below to match the actual API.

- [ ] **Step 2: Write the adapter**

```csharp
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Input;
using FabricationAssistant.Core.Math;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Translates gesture recognizer events into CameraState mutations. Plan 2A
/// supports orbit / pan / pinch only — Tap and DoubleTap are forwarded as
/// no-ops that Plan 2B (picking) and Plan 3 (fit-selection) will fill in.
/// </summary>
public sealed class ViewportInteractionAdapter
{
    private readonly CameraState _camera;
    private readonly ViewportTouchGestureRecognizer _recognizer;
    private readonly Action _requestRender;
    private Vector3d _pivot = Vector3d.Zero;

    public ViewportInteractionAdapter(
        CameraState camera,
        ViewportTouchGestureRecognizer recognizer,
        Action requestRender)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _requestRender = requestRender ?? throw new ArgumentNullException(nameof(requestRender));

        _recognizer.OrbitDelta += OnOrbitDelta;
        _recognizer.PanZoomDelta += OnPanZoomDelta;
    }

    private void OnOrbitDelta(float dx, float dy)
    {
        _camera.Orbit(dx, dy);
        _requestRender();
    }

    private void OnPanZoomDelta(float panDx, float panDy, float pinchScale)
    {
        if (System.Math.Abs(panDx) > 0 || System.Math.Abs(panDy) > 0)
        {
            _camera.Pan(panDx, panDy);
        }
        if (System.Math.Abs(pinchScale - 1f) > 1e-4f)
        {
            _camera.DollyZoomAroundPivot(_pivot, pinchScale);
        }
        _requestRender();
    }

    public void SetPivot(Vector3d pivot) => _pivot = pivot;
}
```

The signatures `Orbit(float, float)`, `Pan(float, float)`, `DollyZoomAroundPivot(Vector3d, float)` come from the audit. If the actual `CameraState` uses `double` instead of `float` (likely, given the double-precision math layer), change the parameters accordingly.

If the recognizer's `OrbitDelta` event is a `delegate void` with different parameter order or types (e.g., `EventHandler<OrbitDeltaEventArgs>`), use that signature.

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

### Task 16: Wire pointer source + adapter into MainActivity

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`
- Modify: `Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs`

- [ ] **Step 1: Add OnTouchListener support to ViewportSurfaceView**

Edit `ViewportSurfaceView.cs` — add a public `PointerSource` property and forward touch events:

```csharp
// Add to the field list:
public FabricationAssistant.Input.Gestures.Android.AndroidPointerSource? PointerSource { get; set; }

// Add an override:
public override bool OnTouchEvent(Android.Views.MotionEvent? e)
{
    if (PointerSource is { } src && src.OnTouch(e)) return true;
    return base.OnTouchEvent(e);
}
```

(Insert the property declaration after the existing `Renderer` property, and place the `OnTouchEvent` override after `QueueRendererCommand`.)

- [ ] **Step 2: Construct the recognizer + source + adapter in MainActivity.OnCreate**

Add to `MainActivity.OnCreate(...)` AFTER the `container.AddView(_viewport)` line:

```csharp
var recognizer = new FabricationAssistant.Core.Input.ViewportTouchGestureRecognizer();
var pointerSource = new FabricationAssistant.Input.Gestures.Android.AndroidPointerSource(recognizer, Resources!.DisplayMetrics!);
_viewport.PointerSource = pointerSource;

var adapter = new FabricationAssistant.Input.Gestures.Android.ViewportInteractionAdapter(
    _camera!, recognizer, () => _viewport.RequestRender());
```

The `_ = adapter;` discard is not needed because the adapter subscribes to events in its constructor; holding a reference in a local variable keeps it alive as long as MainActivity is alive (the recognizer-event subscription prevents GC).

If the recognizer's namespace differs from `FabricationAssistant.Core.Input`, adjust both `using` directives and qualifiers.

- [ ] **Step 3: Build**

Run: `& "Android/tools/build.ps1" -Configuration Debug`
Expected: 0 errors.

---

## Phase 8: Tests

### Task 17: Extend the test project to cover camera + gestures

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Create: `Android/src/FabricationAssistant.App.Android.Tests/CameraOrbitTests.cs`
- Create: `Android/src/FabricationAssistant.App.Android.Tests/GestureRecognizerTests.cs`

- [ ] **Step 1: Expand the linked-source glob in the test csproj**

Edit `FabricationAssistant.App.Android.Tests.csproj` — replace the existing `<Compile Include="..\..\..\src\FabricationAssistant.Core\Math\**\*.cs" .../>` with a broader glob that picks up Camera + Input source files too:

```xml
<ItemGroup>
  <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Math\**\*.cs"
           LinkBase="Linked\Math" />
  <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Camera\**\*.cs"
           LinkBase="Linked\Camera" />
  <Compile Include="$(MSBuildThisFileDirectory)..\..\..\src\FabricationAssistant.Core\Input\**\*.cs"
           LinkBase="Linked\Input" />
</ItemGroup>
```

If `..\..\..\src\FabricationAssistant.Core\Camera\` doesn't exist (the path may be `..\..\..\src\FabricationAssistant.Core\Cameras\` or just `Camera*` files in the root), use Glob to find the actual location and adjust the path.

If `..\..\..\src\FabricationAssistant.Core\Input\` doesn't exist for `ViewportTouchGestureRecognizer.cs`, use Glob and adjust.

- [ ] **Step 2: Write `CameraOrbitTests.cs`**

```csharp
using FabricationAssistant.Core.Camera;
using FabricationAssistant.Core.Math;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class CameraOrbitTests
{
    [Fact]
    public void Orbit_around_target_changes_position_but_preserves_distance()
    {
        var c = new CameraState
        {
            Position = new Vector3d(0, 0, 5),
            Target = new Vector3d(0, 0, 0),
            UpDirection = new Vector3d(0, 1, 0),
        };
        double distanceBefore = (c.Position - c.Target).Length;

        c.Orbit(0.5f, 0.0f);

        double distanceAfter = (c.Position - c.Target).Length;
        Assert.Equal(distanceBefore, distanceAfter, 4);
        Assert.NotEqual(0.0, c.Position.X, 4);
    }
}
```

The `CameraState` constructor and properties may use `init` setters or be `record`s; if the object initialiser syntax above doesn't compile, switch to whatever the actual API supports (e.g., a static factory `CameraState.CreateDefault()` or a constructor `new CameraState(position, target, up)`).

If `Vector3d.Length` is a method `Length()` instead of a property, adjust.

If `Orbit(float, float)` takes doubles, change the arguments.

- [ ] **Step 3: Write `GestureRecognizerTests.cs`**

```csharp
using FabricationAssistant.Core.Input;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class GestureRecognizerTests
{
    [Fact]
    public void OneFingerDrag_emits_OrbitDelta()
    {
        var rec = new ViewportTouchGestureRecognizer();
        float orbitDx = 0, orbitDy = 0;
        int orbitCount = 0;
        rec.OrbitDelta += (dx, dy) => { orbitDx += dx; orbitDy += dy; orbitCount++; };

        long t = 0;
        rec.OnPointerEvent(new PointerEvent(0, PointerAction.Down, 100f, 200f, t));
        t += 16;
        rec.OnPointerEvent(new PointerEvent(0, PointerAction.Move, 130f, 210f, t));
        t += 16;
        rec.OnPointerEvent(new PointerEvent(0, PointerAction.Move, 160f, 220f, t));
        t += 16;
        rec.OnPointerEvent(new PointerEvent(0, PointerAction.Up, 160f, 220f, t));

        Assert.True(orbitCount > 0, "OrbitDelta should have fired at least once");
        Assert.True(System.Math.Abs(orbitDx) + System.Math.Abs(orbitDy) > 0,
            "Cumulative orbit delta should be non-zero");
    }
}
```

Match the actual recognizer's API signatures from Task 14 Step 1. Adjust `OnPointerEvent`, `PointerEvent`, `PointerAction`, and the event signature accordingly.

If the recognizer requires an 8 DIP drag threshold (per audit Section 9.4), the input above (30+30+30 px) easily crosses it. If thresholds are different, increase the deltas.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --logger "console;verbosity=normal"`
Expected: 3 tests passing (the original Vector3d smoke test + 2 new tests).

If a test fails on first attempt, the failure tells you what the API actually does — adjust the test assertions to match real behaviour (e.g., a real `Orbit(0.5, 0)` might preserve distance to within 1e-9, not 1e-4 — tighten the tolerance).

---

## Phase 9: End-to-end verification

### Task 18: Full-solution build + DoD check

**Files:** none — verification.

- [ ] **Step 1: Clean build**

Run: `& "Android/tools/build.ps1" -Configuration Debug -Clean`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 2: Verify APK exists**

Run: `ls "Android/src/FabricationAssistant.App.Android/bin/Debug/*.apk"`
Expected: `com.fabricationassistant.android-Signed.apk` present.

- [ ] **Step 3: Run tests**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
Expected: 3 tests pass.

- [ ] **Step 4: Verify no Application.Current.Dispatcher leaked into Android code**

Run Grep with pattern `Application\.Current\.?\s*\.?\s*Dispatcher` over `Android/**/*.cs`. Output mode `files_with_matches`.
Expected: only `IDispatcher.cs` (documentation comment) and the spec/plan docs match — no actual usage.

- [ ] **Step 5: Verify desktop tree untouched**

Run: `git status --porcelain "src/"`
Expected: empty.

- [ ] **Step 6: Document deviations in README**

If Phases 1-7 required any deviations from this plan (renamed types, alternate package versions, additional exclusions, signature mismatches resolved by reading Core), append a "## Plan-2A execution notes" section to `Android/README.md` listing each.

- [ ] **Step 7: Manual device verification (if adb available)**

```powershell
adb install -r "Android/src/FabricationAssistant.App.Android/bin/Debug/com.fabricationassistant.android-Signed.apk"
adb shell am start -n com.fabricationassistant.android/com.fabricationassistant.android.MainActivity
```

Expected: app launches; "Open" button visible; tapping it opens system file picker; selecting a small .glb (e.g., `Data samples/box.glb` if present, or any public sample) imports it; model appears with simple shading; one-finger drag rotates; two-finger drag pans; pinch zooms.

If a real device or emulator isn't available, document Step 7 as "manual verification deferred" and proceed.

---

## Self-review checklist (writing-plans skill)

**1. Spec coverage:** Plan 2A covers spec Section 6 PARTIALLY (single mesh pass, no edges/SSAO/sections/picking), Section 7 PARTIALLY (orbit/pan/pinch only — no tap selection, no double-tap fit, no long-press multi-select), Section 8 PARTIALLY (glTF only — no .fa). The remaining items move to Plan 2B (picking, edges, FA package, tap selection) and Plan 3 (sections, measurement, lifecycle, polish).

**2. Placeholder scan:** No "TBD" / "TODO" / "etc." / "similar to Task N" / "add appropriate error handling" remain. Every code step has complete code. Some adjustment-to-actual-API guidance is intentional — the linked Core sources have authoritative API surface that the plan tasks must read and match, and reading them in the plan would duplicate; the task pattern is "read what's there, adjust the template to match" with clear specifics about what to match.

**3. Type consistency:**
- `GpuMesh` constructor takes `GL gl`. Caller `SceneUploader.Upload(gl, document)` matches.
- `GpuMesh.Upload(positions, normals, colors, indices)` — all `ReadOnlySpan<float>` / `ReadOnlySpan<uint>`. Caller `SceneUploader` builds spans via `AsSpan()`. Matches.
- `GpuScene.Load(DocumentDto)` — caller `MainActivity.OnOpenClicked` invokes it inside the queued GL command. Matches.
- `GlesViewportRenderer.Scene` (`GpuScene?` property), `GlesViewportRenderer.Camera` (`CameraState?` property), `GlesViewportRenderer.CommandQueue` (`ConcurrentQueue<Action<GL>>?` property) — set from `ViewportSurfaceView` constructor and `MainActivity.OnCreate`. Matches.
- `ViewportSurfaceView.PointerSource` (`AndroidPointerSource?` property) — set from `MainActivity.OnCreate`. `OnTouchEvent` reads it. Matches.
- `ViewportSurfaceView.QueueRendererCommand(Action<GL>)` — called from `MainActivity.OnOpenClicked`. Matches.
- `ViewportInteractionAdapter` subscribes to `recognizer.OrbitDelta` / `recognizer.PanZoomDelta`. Real event signatures from the recognizer must match Task 15's template.
- `FabricationAssistantPathsBootstrap` adds `FaImportExtractionDirectory`, `DracoDecodeDirectory`, `ConstrainDirectoryToRoot(string, string)`. These are consumed by the (newly re-included) Import.Gltf source files.

No type-consistency bugs found.

---

## Execution choice

Plan complete and saved to `Android/docs/superpowers/plans/2026-05-23-android-gltf-and-orbit.md`. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, brief two-stage review between tasks. Good for catching API-signature mismatches early.

**2. Inline Execution** — execute all 18 tasks in this session sequentially via the controller (me). Faster end-to-end, less coordination overhead, but burns context.

Which approach?
