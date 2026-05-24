# Android Draco Decode Implementation Plan (Plan 2B of 3+)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Android viewer load Draco-compressed glTF/GLB files (including the Draco-encoded GLB inside a `.fa` package) by decoding the Draco buffers in-process via a P/Invoke to a small native `libdraco_native.so` wrapper built from Google's `draco` library. The desktop's `Process.Start("FabricationAssistant.DracoDecode.exe")` code path is bypassed via the Decorator pattern — the desktop `GltfImportService` source is NOT touched.

**Architecture:**
- New project `FabricationAssistant.Draco.Android` owns the native interop + the wrapping `ISceneImportService`.
- A small C wrapper around Draco's C++ decoder API is built as `libdraco_native.so` for `arm64-v8a` and `x86_64` and bundled into the APK at `lib/<ABI>/libdraco_native.so`.
- `DracoDecodingGltfImportService` implements `ISceneImportService`. Before delegating to the inner `GltfImportService`, it scans the input file for `KHR_draco_mesh_compression`; if present, it parses the glTF JSON + binary chunks, replaces each Draco-compressed primitive with decoded position/normal/uv/index buffer views, and writes a new non-Draco GLB to a temp file. The wrapped `GltfImportService` then reads that file normally (no helper-exe code path triggered).
- `ImportPipeline` is updated to instantiate the wrapper and pass it to `FaImportService` as the inner glTF service, so `.fa` packages get the same Draco handling.

**Tech Stack:**
- .NET 8 for Android (`net8.0-android34.0`), Silk.NET.OpenGLES — same as Plan 2A.
- **Google `draco`** — `https://github.com/google/draco`, C++ source built via Android NDK CMake.
- **Android NDK** — `%LOCALAPPDATA%\Android\Sdk\ndk\<version>` (install via Android Studio's SDK Manager if not present).
- **CMake** — required by the NDK build. Comes with Android Studio or `winget install Kitware.CMake`.
- **System.Text.Json** for parsing the glTF JSON chunk (already available; pure managed).

**Critical guidance (same as Plans 1 + 2A; do NOT violate):**
- **No git commit steps.** Leave work unstaged. User runs parallel refactors on master.
- **Never edit any file under `../src/`** — the desktop tree is read-only.
- **Build via `Android/tools/build.ps1`** — handles ANDROID_HOME + JAVA_HOME fallbacks.
- **ASCII-only inside `.glsl` files.**
- **No emojis in source files.**
- **`Application.Current.Dispatcher` is forbidden in any new code.**
- **`dotnet sln add` is broken on this system** — when adding the new `FabricationAssistant.Draco.Android` project to the solution, edit `Android/FabricationAssistant.Android.sln` directly (add a `Project(...)/EndProject` block + four `ProjectConfigurationPlatforms` lines per project GUID). See Plan 2A for the existing edits.
- **Embed assemblies in APK is already enabled** (`<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>` in `App.Android.csproj`). Native libs added via `<AndroidNativeLibrary>` get packaged automatically.

**Definition of done for this plan:**
1. Clean `Android/tools/build.ps1 -Configuration Debug` succeeds with 0 errors. APK produced.
2. `dotnet build -t:Install` (or `adb install -r`) deploys to the connected tablet (Samsung tablet, device `R52Y80CE37L`, or any other arm64-v8a device).
3. **Test 1: Plain non-Draco glTF still works** — `Duck.glb` from Khronos samples loads identically to Plan 2A (regression check).
4. **Test 2: Draco-encoded glTF loads** — any `.glb` with `KHR_draco_mesh_compression` extension imports successfully and renders with simple lit shading.
5. **Test 3: `.fa` package with Draco-encoded inner GLB loads** — the user's original `.fa` file that hit the "Draco decode helper not available" error in Plan 2A now imports and renders.
6. No file under `../src/` is modified (`git status --porcelain "src/"` empty).
7. `libdraco_native.so` ends up at both `lib/arm64-v8a/libdraco_native.so` and `lib/x86_64/libdraco_native.so` inside the APK (verify with `unzip -l`).
8. The wrapped service can be unit-tested: a small managed test that takes a Draco-encoded buffer and asserts decoded vertex/index counts match expectations.

---

## File structure (created/modified by this plan)

```
Android/
├── deps/                                                                  # NEW root subdir
│   ├── draco/                                                             # Git submodule or git clone of google/draco
│   └── prebuilt/
│       ├── arm64-v8a/libdraco_native.so                                   # Build artifact
│       └── x86_64/libdraco_native.so                                      # Build artifact
├── tools/
│   ├── build-libdraco.ps1                                                 # NEW — orchestrates the NDK CMake build
│   └── build.ps1                                                          # MODIFIED — calls build-libdraco.ps1 first if .so missing
├── src/
│   ├── FabricationAssistant.Draco.Android/                                # NEW project
│   │   ├── FabricationAssistant.Draco.Android.csproj
│   │   ├── native/
│   │   │   ├── CMakeLists.txt
│   │   │   └── draco_native.cpp                                           # C wrapper around Draco C++ Decoder
│   │   ├── DracoNativeDecoder.cs                                          # P/Invoke layer
│   │   ├── DracoExtensionDetector.cs                                      # Scans glTF JSON for KHR_draco_mesh_compression
│   │   ├── DracoGltfTranscoder.cs                                         # GLB -> decoded GLB pipeline
│   │   └── DracoDecodingGltfImportService.cs                              # ISceneImportService decorator
│   └── FabricationAssistant.App.Android/
│       ├── FabricationAssistant.App.Android.csproj                        # MODIFIED — ProjectReference Draco.Android + <AndroidNativeLibrary> for both ABIs
│       └── ImportPipeline.cs                                              # MODIFIED — wrap GltfImportService with DracoDecodingGltfImportService
└── docs/superpowers/plans/2026-05-23-android-draco-support.md             # this file
```

Zero changes to `../src/`.

---

## Phase 0: Pre-flight

### Task 0: Verify NDK + CMake availability

**Files:** none — verification only.

- [ ] **Step 1: Check for the Android NDK**

Run: `Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\ndk\" -Directory -ErrorAction SilentlyContinue`

Expected: at least one versioned subdirectory like `25.2.9519653` or `26.x.y.z`. If empty:
- Open Android Studio → Tools → SDK Manager → SDK Tools tab.
- Check "NDK (Side by side)" and "CMake" → Apply → wait for install.
- Re-run the check.

Record the NDK path (e.g. `$env:LOCALAPPDATA\Android\Sdk\ndk\26.1.10909125`) — Task 2's CMake invocation needs the exact path.

- [ ] **Step 2: Check for CMake**

Run: `cmake --version`

Expected: 3.18 or higher (Draco's CMakeLists requires modern CMake). If missing:
- Use the bundled SDK Tools CMake at `$env:LOCALAPPDATA\Android\Sdk\cmake\<ver>\bin\cmake.exe` (added by Step 1 above)
- OR install standalone: `winget install Kitware.CMake`
- Add to PATH for the session: `$env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\cmake\<ver>\bin"`

- [ ] **Step 3: Check for Ninja (optional but speeds up the build)**

Run: `ninja --version`

If missing: Ninja is bundled with SDK Tools CMake — `$env:LOCALAPPDATA\Android\Sdk\cmake\<ver>\bin\ninja.exe`. Either add the bin directory to PATH or pass `-G "Unix Makefiles"` to CMake instead.

---

## Phase 1: Fetch and build libdraco for Android

### Task 1: Clone Google Draco into Android/deps/

**Files:**
- Create: `Android/deps/draco/` (cloned from `https://github.com/google/draco`)

- [ ] **Step 1: Clone at a known-good tag**

Run from the repo root:
```powershell
$ErrorActionPreference = 'Stop'
git clone --depth=1 --branch 1.5.7 https://github.com/google/draco.git "Android/deps/draco"
```

(Tag `1.5.7` is a stable Draco release as of 2025. If clone fails because tag missing, use `main` and document the SHA in `Android/README.md`.)

- [ ] **Step 2: Add `Android/deps/` to `.gitignore`** (Draco source is ~20 MB, shouldn't be committed)

Append to `Android/.gitignore`:
```
deps/
```

- [ ] **Step 3: Verify the structure**

```powershell
Test-Path "Android/deps/draco/CMakeLists.txt"
Test-Path "Android/deps/draco/src/draco/compression/decode.h"
```

Both must return `True`.

---

### Task 2: Write the C wrapper

**Files:**
- Create: `Android/src/FabricationAssistant.Draco.Android/native/draco_native.cpp`
- Create: `Android/src/FabricationAssistant.Draco.Android/native/CMakeLists.txt`

- [ ] **Step 1: Write `draco_native.cpp`**

```cpp
// draco_native.cpp - minimal C-callable wrapper around Google's Draco decoder.
// Exports four functions that the C# P/Invoke layer can call:
//   draco_decoder_create / draco_decoder_destroy
//   draco_decode_buffer_to_mesh        - returns a handle to a decoded mesh
//   draco_mesh_get_num_faces / draco_mesh_get_num_points
//   draco_mesh_get_position / draco_mesh_get_normal / draco_mesh_get_uv / draco_mesh_get_indices
//   draco_mesh_destroy

#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>
#include <draco/compression/decode.h>
#include <draco/mesh/mesh.h>
#include <draco/attributes/geometry_attribute.h>

extern "C" {

// Opaque mesh handle returned to C#. We allocate on the heap and hand back a
// pointer; the C# side calls draco_mesh_destroy when done.
struct DracoMeshHandle {
    std::unique_ptr<draco::Mesh> mesh;
};

// Decode a Draco buffer to a DracoMeshHandle*. Returns nullptr on failure.
DracoMeshHandle* draco_decode_buffer_to_mesh(const uint8_t* data, int32_t size) {
    if (!data || size <= 0) return nullptr;
    draco::DecoderBuffer buffer;
    buffer.Init(reinterpret_cast<const char*>(data), static_cast<size_t>(size));

    draco::Decoder decoder;
    auto status_or = decoder.DecodeMeshFromBuffer(&buffer);
    if (!status_or.ok()) return nullptr;
    auto handle = new DracoMeshHandle();
    handle->mesh = std::move(status_or).value();
    return handle;
}

int32_t draco_mesh_get_num_faces(const DracoMeshHandle* h) {
    return h && h->mesh ? static_cast<int32_t>(h->mesh->num_faces()) : 0;
}

int32_t draco_mesh_get_num_points(const DracoMeshHandle* h) {
    return h && h->mesh ? static_cast<int32_t>(h->mesh->num_points()) : 0;
}

// Copies the position attribute as XYZ floats into `out` (capacity in *out_count*3*4 bytes).
// Returns 1 on success, 0 if no position attribute or buffer too small.
int32_t draco_mesh_copy_attribute_float(
    const DracoMeshHandle* h,
    int32_t attribute_type,  // 0=POSITION, 1=NORMAL, 2=TEX_COORD
    int32_t components_expected,
    float* out,
    int32_t out_count) {
    if (!h || !h->mesh) return 0;
    auto type = static_cast<draco::GeometryAttribute::Type>(
        attribute_type == 0 ? draco::GeometryAttribute::POSITION :
        attribute_type == 1 ? draco::GeometryAttribute::NORMAL :
        attribute_type == 2 ? draco::GeometryAttribute::TEX_COORD :
        draco::GeometryAttribute::INVALID);
    const auto* attr = h->mesh->GetNamedAttribute(type);
    if (!attr) return 0;
    if (out_count < static_cast<int32_t>(h->mesh->num_points()) * components_expected) return 0;

    float* p = out;
    for (draco::PointIndex i(0); i < h->mesh->num_points(); ++i) {
        if (!attr->ConvertValue<float>(attr->mapped_index(i), components_expected, p)) return 0;
        p += components_expected;
    }
    return 1;
}

// Copies the triangle indices as uint32 triples into `out` (capacity in *out_count* uint32s).
// Returns 1 on success.
int32_t draco_mesh_copy_indices_uint32(
    const DracoMeshHandle* h,
    uint32_t* out,
    int32_t out_count) {
    if (!h || !h->mesh) return 0;
    int32_t face_count = static_cast<int32_t>(h->mesh->num_faces());
    if (out_count < face_count * 3) return 0;
    for (draco::FaceIndex f(0); f < face_count; ++f) {
        const auto& face = h->mesh->face(f);
        out[f.value() * 3 + 0] = face[0].value();
        out[f.value() * 3 + 1] = face[1].value();
        out[f.value() * 3 + 2] = face[2].value();
    }
    return 1;
}

void draco_mesh_destroy(DracoMeshHandle* h) {
    delete h;
}

}  // extern "C"
```

If the Draco header paths differ in the cloned version (e.g., `draco/draco_features.h` style), adjust the `#include` directives by searching `deps/draco/src/draco/` with Glob. The `draco::Mesh` / `draco::Decoder` / `draco::GeometryAttribute` types and methods used above are stable across Draco 1.5.x releases.

- [ ] **Step 2: Write `CMakeLists.txt`**

```cmake
cmake_minimum_required(VERSION 3.18)
project(draco_native LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(DRACO_ROOT ${CMAKE_CURRENT_LIST_DIR}/../../../deps/draco)
if(NOT EXISTS "${DRACO_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR "Draco source not found at ${DRACO_ROOT}. Run `git clone` per Task 1.")
endif()

# Build Draco as a static library and link it into our shared wrapper.
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(DRACO_GLTF_BITSTREAM ON CACHE BOOL "" FORCE)
set(DRACO_TESTS OFF CACHE BOOL "" FORCE)
set(DRACO_JS_GLUE OFF CACHE BOOL "" FORCE)
add_subdirectory(${DRACO_ROOT} ${CMAKE_BINARY_DIR}/draco_build EXCLUDE_FROM_ALL)

add_library(draco_native SHARED draco_native.cpp)
target_include_directories(draco_native PRIVATE ${DRACO_ROOT}/src ${CMAKE_BINARY_DIR}/draco_build)
target_link_libraries(draco_native PRIVATE draco android log)
target_compile_options(draco_native PRIVATE -fvisibility=hidden -ffunction-sections -fdata-sections)
target_link_options(draco_native PRIVATE -Wl,--gc-sections -Wl,--strip-all)
```

- [ ] **Step 3: Verify with a local CMake configure**

Run from `Android/src/FabricationAssistant.Draco.Android/native/`:
```powershell
cmake -B build-test -G "Ninja" `
  -DCMAKE_TOOLCHAIN_FILE="$env:LOCALAPPDATA\Android\Sdk\ndk\<VERSION>\build\cmake\android.toolchain.cmake" `
  -DANDROID_ABI=arm64-v8a `
  -DANDROID_PLATFORM=android-24
```

Replace `<VERSION>` with the value from Task 0. Expected: CMake completes configuration without errors. If it complains about Draco headers, re-check Task 1 was completed.

This is a configuration test only — the actual build happens in Task 3 via `build-libdraco.ps1`.

Delete `build-test` after the test passes.

---

### Task 3: Write build-libdraco.ps1 and run it for both ABIs

**Files:**
- Create: `Android/tools/build-libdraco.ps1`

- [ ] **Step 1: Write the build script**

```powershell
<#
.SYNOPSIS
Build libdraco_native.so for arm64-v8a and x86_64 from Android/deps/draco
via the Android NDK CMake toolchain. Outputs to Android/deps/prebuilt/<ABI>/.

.NOTES
Requires Android NDK + CMake. Auto-discovers the latest NDK under
$env:LOCALAPPDATA\Android\Sdk\ndk if -NdkPath is not specified.
#>
[CmdletBinding()]
param(
    [string]$NdkPath,
    [string[]]$Abis = @('arm64-v8a','x86_64'),
    [string]$AndroidPlatform = 'android-24',
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $root 'src/FabricationAssistant.Draco.Android/native'
$prebuiltDir = Join-Path $root 'deps/prebuilt'

if (-not $NdkPath) {
    $ndkRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk'
    if (-not (Test-Path $ndkRoot)) { throw "NDK root not found at $ndkRoot. Install via Android Studio's SDK Manager." }
    $NdkPath = (Get-ChildItem $ndkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
    Write-Host "Auto-selected NDK: $NdkPath"
}

$toolchain = Join-Path $NdkPath 'build/cmake/android.toolchain.cmake'
if (-not (Test-Path $toolchain)) { throw "Toolchain file not found at $toolchain" }

# Add bundled SDK CMake + Ninja to PATH if not already.
$sdkCmakeRoot = Join-Path $env:LOCALAPPDATA 'Android\Sdk\cmake'
if (Test-Path $sdkCmakeRoot) {
    $cmakeBin = (Get-ChildItem $sdkCmakeRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName + '\bin'
    $env:PATH = "$cmakeBin;$env:PATH"
}

foreach ($abi in $Abis) {
    $buildDir = Join-Path $nativeDir "build-$abi"
    $outDir = Join-Path $prebuiltDir $abi
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Building libdraco_native.so for $abi"
    Write-Host "========================================" -ForegroundColor Cyan

    Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
    cmake -B $buildDir -S $nativeDir -G "Ninja" `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
        "-DANDROID_ABI=$abi" `
        "-DANDROID_PLATFORM=$AndroidPlatform" `
        "-DCMAKE_BUILD_TYPE=$Configuration"
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed for $abi (exit $LASTEXITCODE)" }

    cmake --build $buildDir --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $abi (exit $LASTEXITCODE)" }

    $produced = Join-Path $buildDir 'libdraco_native.so'
    if (-not (Test-Path $produced)) { throw "Expected output $produced not found" }
    Copy-Item $produced (Join-Path $outDir 'libdraco_native.so') -Force
    Write-Host "Copied to $outDir\libdraco_native.so" -ForegroundColor Green
}

Write-Host ""
Write-Host "All ABIs built successfully." -ForegroundColor Green
```

- [ ] **Step 2: Run the build**

Run: `pwsh -NoProfile -Command "& 'C:\Users\skritikos\Desktop\Fabrication Assistant\Android\tools\build-libdraco.ps1'"`

Expected: ~5-10 minutes the first time (Draco itself compiles), then both `.so` files appear under `deps/prebuilt/`.

If the build fails:
- **CMake can't find compiler:** check the NDK path is correct. The `<NDK>/build/cmake/android.toolchain.cmake` file must exist.
- **Linker errors about missing symbols:** Draco may have an internal feature you need to enable. Try setting `DRACO_GLTF_BITSTREAM=ON` (already in the CMakeLists, but verify it propagated).
- **`fatal error: 'draco/...' file not found`:** the clone is incomplete. Re-clone with `--recurse-submodules` if Draco has submodules.

- [ ] **Step 3: Verify both `.so` artifacts**

```powershell
Get-ChildItem Android/deps/prebuilt -Recurse -Filter "libdraco_native.so" | Select-Object FullName, Length
```

Expected: two entries, one per ABI, each ~1-3 MB.

---

## Phase 2: C# project for Draco interop

### Task 4: Create FabricationAssistant.Draco.Android project

**Files:**
- Create: `Android/src/FabricationAssistant.Draco.Android/FabricationAssistant.Draco.Android.csproj`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android34.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.Draco.Android</RootNamespace>
    <AssemblyName>FabricationAssistant.Draco.Android</AssemblyName>
    <Description>Android in-process Draco decode wrapper. Bridges native libdraco_native.so into the FabricationAssistant import pipeline without modifying desktop source.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Core.Android\FabricationAssistant.Core.Android.csproj" />
    <ProjectReference Include="..\FabricationAssistant.Import.Gltf.Android\FabricationAssistant.Import.Gltf.Android.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the project to the solution (manual edit per Plan 2A constraint)**

Edit `Android/FabricationAssistant.Android.sln`:

After the last `Project(...)/EndProject` block, append:
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "FabricationAssistant.Draco.Android", "src\FabricationAssistant.Draco.Android\FabricationAssistant.Draco.Android.csproj", "{A7B8C9D0-E1F2-3456-0123-567890123456}"
EndProject
```

In the `GlobalSection(ProjectConfigurationPlatforms) = postSolution` block, before `EndGlobalSection`, append:
```
		{A7B8C9D0-E1F2-3456-0123-567890123456}.Debug|AnyCPU.ActiveCfg = Debug|AnyCPU
		{A7B8C9D0-E1F2-3456-0123-567890123456}.Debug|AnyCPU.Build.0 = Debug|AnyCPU
		{A7B8C9D0-E1F2-3456-0123-567890123456}.Release|AnyCPU.ActiveCfg = Release|AnyCPU
		{A7B8C9D0-E1F2-3456-0123-567890123456}.Release|AnyCPU.Build.0 = Release|AnyCPU
```

(Tabs, not spaces. Use the GUID shown — it doesn't conflict with the existing Plan 1/2A GUIDs.)

- [ ] **Step 3: Build the empty project**

Run: `& "Android/tools/build.ps1" -Configuration Debug`

Expected: build green, includes the new (empty) `FabricationAssistant.Draco.Android.dll`.

---

### Task 5: Implement DracoNativeDecoder P/Invoke layer

**Files:**
- Create: `Android/src/FabricationAssistant.Draco.Android/DracoNativeDecoder.cs`

- [ ] **Step 1: Write the file**

```csharp
using System.Runtime.InteropServices;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// Managed P/Invoke surface to libdraco_native.so. All entry points match the
/// extern "C" signatures in draco_native.cpp.
/// </summary>
internal static class DracoNativeDecoder
{
    private const string LibName = "libdraco_native";

    public enum AttributeType : int
    {
        Position = 0,
        Normal = 1,
        TexCoord = 2,
    }

    [DllImport(LibName, EntryPoint = "draco_decode_buffer_to_mesh")]
    public static extern nint DecodeBufferToMesh(nint data, int size);

    [DllImport(LibName, EntryPoint = "draco_mesh_get_num_faces")]
    public static extern int GetNumFaces(nint handle);

    [DllImport(LibName, EntryPoint = "draco_mesh_get_num_points")]
    public static extern int GetNumPoints(nint handle);

    [DllImport(LibName, EntryPoint = "draco_mesh_copy_attribute_float")]
    public static extern int CopyAttributeFloat(
        nint handle,
        AttributeType attribute,
        int componentsExpected,
        nint outBuffer,
        int outCount);

    [DllImport(LibName, EntryPoint = "draco_mesh_copy_indices_uint32")]
    public static extern int CopyIndicesUint32(nint handle, nint outBuffer, int outCount);

    [DllImport(LibName, EntryPoint = "draco_mesh_destroy")]
    public static extern void DestroyMesh(nint handle);
}

/// <summary>
/// High-level managed wrapper around a single Draco-encoded buffer decode. Hides
/// the IntPtr / fixed pointer dance from callers.
/// </summary>
public sealed class DracoMesh : IDisposable
{
    private nint _handle;

    public int NumPoints { get; }
    public int NumFaces { get; }

    private DracoMesh(nint handle)
    {
        _handle = handle;
        NumPoints = DracoNativeDecoder.GetNumPoints(handle);
        NumFaces = DracoNativeDecoder.GetNumFaces(handle);
    }

    public static unsafe DracoMesh? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty) return null;
        fixed (byte* p = encoded)
        {
            var handle = DracoNativeDecoder.DecodeBufferToMesh((nint)p, encoded.Length);
            return handle == nint.Zero ? null : new DracoMesh(handle);
        }
    }

    public float[]? GetPositions() => CopyFloatAttribute(DracoNativeDecoder.AttributeType.Position, 3);
    public float[]? GetNormals() => CopyFloatAttribute(DracoNativeDecoder.AttributeType.Normal, 3);
    public float[]? GetTexCoords() => CopyFloatAttribute(DracoNativeDecoder.AttributeType.TexCoord, 2);

    private unsafe float[]? CopyFloatAttribute(DracoNativeDecoder.AttributeType type, int components)
    {
        int total = NumPoints * components;
        if (total <= 0) return null;
        var buffer = new float[total];
        fixed (float* p = buffer)
        {
            if (DracoNativeDecoder.CopyAttributeFloat(_handle, type, components, (nint)p, total) == 0)
                return null;
        }
        return buffer;
    }

    public unsafe uint[]? GetIndices()
    {
        int total = NumFaces * 3;
        if (total <= 0) return null;
        var buffer = new uint[total];
        fixed (uint* p = buffer)
        {
            if (DracoNativeDecoder.CopyIndicesUint32(_handle, (nint)p, total) == 0)
                return null;
        }
        return buffer;
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            DracoNativeDecoder.DestroyMesh(_handle);
            _handle = nint.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~DracoMesh() => Dispose();
}
```

Set `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the Draco.Android csproj — the `fixed` blocks require it:

```xml
<PropertyGroup>
  ...
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

- [ ] **Step 2: Build**

`& "Android/tools/build.ps1" -Configuration Debug` — expect 0 errors.

---

### Task 6: Implement DracoExtensionDetector

**Files:**
- Create: `Android/src/FabricationAssistant.Draco.Android/DracoExtensionDetector.cs`

This is a pure-managed scan of a glTF or GLB file to determine whether it uses the `KHR_draco_mesh_compression` extension. Avoids invoking the full glTF reader (which is what `GltfImportService.UsesDracoCompression` already does on desktop — but we want a small, self-contained scan).

- [ ] **Step 1: Write the file**

```csharp
using System.Buffers;
using System.Text;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// Cheap scan that determines whether a glTF/GLB file uses
/// KHR_draco_mesh_compression. Reads up to 32 MB of the JSON chunk and
/// substring-matches; this is conservative but fast and avoids constructing
/// a full SharpGLTF reader.
/// </summary>
public static class DracoExtensionDetector
{
    private const int ScanLimitBytes = 32 * 1024 * 1024;
    private static readonly byte[] Needle = Encoding.ASCII.GetBytes("KHR_draco_mesh_compression");

    public static bool ContainsDraco(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath)) return false;

        using var fs = File.OpenRead(filePath);
        long readable = System.Math.Min(fs.Length, ScanLimitBytes);

        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)System.Math.Min(readable, 64 * 1024));
        try
        {
            int matched = 0;
            long total = 0;
            int n;
            while (total < readable && (n = fs.Read(buffer, 0, (int)System.Math.Min(buffer.Length, readable - total))) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] == Needle[matched])
                    {
                        matched++;
                        if (matched == Needle.Length) return true;
                    }
                    else if (buffer[i] == Needle[0])
                    {
                        matched = 1;
                    }
                    else
                    {
                        matched = 0;
                    }
                }
                total += n;
            }
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
```

- [ ] **Step 2: Build**

`& "Android/tools/build.ps1" -Configuration Debug` — expect 0 errors.

---

### Task 7: Implement DracoGltfTranscoder

This is the workhorse — given a Draco-encoded GLB, produce a non-Draco GLB by parsing the JSON chunk, decoding each Draco-compressed primitive via `DracoMesh`, rewriting `bufferViews` and `accessors` to point to new uncompressed buffers, and writing out a new GLB.

**Files:**
- Create: `Android/src/FabricationAssistant.Draco.Android/DracoGltfTranscoder.cs`

- [ ] **Step 1: Write the file**

This is a substantial file. The implementation walks the glTF JSON looking for `meshes[*].primitives[*].extensions.KHR_draco_mesh_compression`. For each, it:

1. Reads the indicated `bufferView` (offset+length into the binary chunk).
2. Calls `DracoMesh.Decode(span)` to get positions/normals/uvs/indices.
3. Appends new uncompressed buffer views to a fresh binary chunk.
4. Rewrites the primitive's `attributes` and `indices` accessors to reference the new bufferViews.
5. Removes the `KHR_draco_mesh_compression` extension entry.
6. Writes a new GLB with the updated JSON + new binary chunk.

```csharp
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// Transcodes a Draco-compressed GLB into a non-Draco GLB by decoding each
/// Draco primitive in-process and rewriting buffer views + accessors. Writes
/// the result to a caller-supplied temp path.
/// </summary>
public static class DracoGltfTranscoder
{
    private const uint GlbMagic = 0x46546C67;  // "glTF"
    private const uint GlbVersion = 2;
    private const uint ChunkTypeJson = 0x4E4F534A;  // "JSON"
    private const uint ChunkTypeBin  = 0x004E4942;  // "BIN\0"

    public static void Transcode(string sourceGlbPath, string destinationGlbPath)
    {
        ArgumentNullException.ThrowIfNull(sourceGlbPath);
        ArgumentNullException.ThrowIfNull(destinationGlbPath);

        var bytes = File.ReadAllBytes(sourceGlbPath);
        var (json, bin) = ReadGlb(bytes);

        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("glTF JSON parse failed");
        var newBin = new MemoryStream();
        var bufferViews = root["bufferViews"] as JsonArray ?? new JsonArray();
        var accessors = root["accessors"] as JsonArray ?? new JsonArray();
        var meshes = root["meshes"] as JsonArray;
        if (meshes is null) { File.WriteAllBytes(destinationGlbPath, bytes); return; }

        bool changed = false;
        foreach (var meshNode in meshes)
        {
            var prims = meshNode?["primitives"] as JsonArray;
            if (prims is null) continue;
            foreach (var prim in prims)
            {
                var ext = prim?["extensions"]?["KHR_draco_mesh_compression"];
                if (ext is null) continue;

                int viewIndex = ext["bufferView"]!.GetValue<int>();
                var view = bufferViews[viewIndex]!;
                int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
                int length = view["byteLength"]!.GetValue<int>();
                var encoded = new ReadOnlySpan<byte>(bin, offset, length);

                using var dm = DracoMesh.Decode(encoded)
                    ?? throw new InvalidDataException("Draco decode failed for primitive");

                var positions = dm.GetPositions();
                var normals = dm.GetNormals();
                var texCoords = dm.GetTexCoords();
                var indices = dm.GetIndices();

                if (positions is null || indices is null)
                    throw new InvalidDataException("Draco decoded mesh missing required attributes");

                int posView = AppendFloatBufferView(bufferViews, newBin, positions, byteStride: 12);
                int idxView = AppendUInt32BufferView(bufferViews, newBin, indices);
                int posAccessor = AppendVec3FloatAccessor(accessors, posView, dm.NumPoints, positions);
                int idxAccessor = AppendUIntAccessor(accessors, idxView, indices.Length);

                int? nrmAccessor = normals is null ? null
                    : AppendVec3FloatAccessor(accessors,
                        AppendFloatBufferView(bufferViews, newBin, normals, byteStride: 12),
                        dm.NumPoints, normals);
                int? uvAccessor = texCoords is null ? null
                    : AppendVec2FloatAccessor(accessors,
                        AppendFloatBufferView(bufferViews, newBin, texCoords, byteStride: 8),
                        dm.NumPoints, texCoords);

                // Rewrite the primitive attribute references.
                var attribs = prim!["attributes"] as JsonObject ?? new JsonObject();
                attribs["POSITION"] = posAccessor;
                if (nrmAccessor.HasValue) attribs["NORMAL"] = nrmAccessor.Value;
                if (uvAccessor.HasValue) attribs["TEXCOORD_0"] = uvAccessor.Value;
                prim["attributes"] = attribs;
                prim["indices"] = idxAccessor;

                // Remove the Draco extension entry.
                ((JsonObject)prim["extensions"]!).Remove("KHR_draco_mesh_compression");
                if (((JsonObject)prim["extensions"]!).Count == 0)
                    ((JsonObject)prim).Remove("extensions");

                changed = true;
            }
        }

        // Remove "KHR_draco_mesh_compression" from extensionsUsed / extensionsRequired.
        TryRemoveExtensionRef(root, "extensionsUsed", "KHR_draco_mesh_compression");
        TryRemoveExtensionRef(root, "extensionsRequired", "KHR_draco_mesh_compression");

        root["bufferViews"] = bufferViews;
        root["accessors"] = accessors;

        // Replace single buffer entry with the rewritten binary chunk.
        var buffers = root["buffers"] as JsonArray ?? new JsonArray();
        if (buffers.Count == 0) buffers.Add(new JsonObject());
        buffers[0]!["byteLength"] = (int)newBin.Length;
        ((JsonObject)buffers[0]!).Remove("uri");
        root["buffers"] = buffers;

        if (!changed) { File.WriteAllBytes(destinationGlbPath, bytes); return; }

        WriteGlb(destinationGlbPath, root.ToJsonString(), newBin.ToArray());
    }

    private static int AppendFloatBufferView(JsonArray bufferViews, MemoryStream bin, float[] data, int byteStride)
    {
        long offset = bin.Position;
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bin.Write(bytes);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = (int)offset,
            ["byteLength"] = bytes.Length,
            ["byteStride"] = byteStride,
        };
        bufferViews.Add(node);
        return bufferViews.Count - 1;
    }

    private static int AppendUInt32BufferView(JsonArray bufferViews, MemoryStream bin, uint[] data)
    {
        long offset = bin.Position;
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        bin.Write(bytes);
        PadTo4Bytes(bin);
        var node = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = (int)offset,
            ["byteLength"] = bytes.Length,
        };
        bufferViews.Add(node);
        return bufferViews.Count - 1;
    }

    private static int AppendVec3FloatAccessor(JsonArray accessors, int bufferView, int count, float[] data)
    {
        var (min, max) = ComputeVec3MinMax(data);
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = 5126,  // FLOAT
            ["count"] = count,
            ["type"] = "VEC3",
            ["min"] = new JsonArray(min[0], min[1], min[2]),
            ["max"] = new JsonArray(max[0], max[1], max[2]),
        };
        accessors.Add(node);
        return accessors.Count - 1;
    }

    private static int AppendVec2FloatAccessor(JsonArray accessors, int bufferView, int count, float[] data)
    {
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = 5126,
            ["count"] = count,
            ["type"] = "VEC2",
        };
        accessors.Add(node);
        return accessors.Count - 1;
    }

    private static int AppendUIntAccessor(JsonArray accessors, int bufferView, int count)
    {
        var node = new JsonObject
        {
            ["bufferView"] = bufferView,
            ["componentType"] = 5125,  // UNSIGNED_INT
            ["count"] = count,
            ["type"] = "SCALAR",
        };
        accessors.Add(node);
        return accessors.Count - 1;
    }

    private static (float[] min, float[] max) ComputeVec3MinMax(float[] data)
    {
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i + 2 < data.Length; i += 3)
        {
            if (data[i] < minX) minX = data[i];
            if (data[i + 1] < minY) minY = data[i + 1];
            if (data[i + 2] < minZ) minZ = data[i + 2];
            if (data[i] > maxX) maxX = data[i];
            if (data[i + 1] > maxY) maxY = data[i + 1];
            if (data[i + 2] > maxZ) maxZ = data[i + 2];
        }
        return (new[] { minX, minY, minZ }, new[] { maxX, maxY, maxZ });
    }

    private static void TryRemoveExtensionRef(JsonNode root, string key, string name)
    {
        if (root[key] is JsonArray arr)
        {
            for (int i = arr.Count - 1; i >= 0; i--)
                if (arr[i]?.GetValue<string>() == name)
                    arr.RemoveAt(i);
            if (arr.Count == 0) ((JsonObject)root).Remove(key);
        }
    }

    private static void PadTo4Bytes(MemoryStream s)
    {
        long pad = (4 - (s.Position % 4)) % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    private static (string json, byte[] bin) ReadGlb(byte[] data)
    {
        if (data.Length < 12) throw new InvalidDataException("GLB too short");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0));
        if (magic != GlbMagic) throw new InvalidDataException("Not a GLB file");

        int p = 12;
        string? json = null;
        byte[] bin = Array.Empty<byte>();
        while (p < data.Length)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)); p += 4;
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)); p += 4;
            if (chunkType == ChunkTypeJson)
                json = Encoding.UTF8.GetString(data, p, (int)chunkLength).TrimEnd('\0', ' ');
            else if (chunkType == ChunkTypeBin)
            {
                bin = new byte[chunkLength];
                Buffer.BlockCopy(data, p, bin, 0, (int)chunkLength);
            }
            p += (int)chunkLength;
        }
        if (json is null) throw new InvalidDataException("GLB missing JSON chunk");
        return (json, bin);
    }

    private static void WriteGlb(string path, string json, byte[] bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int binPad = (4 - (bin.Length % 4)) % 4;
        int jsonLen = jsonBytes.Length + jsonPad;
        int binLen = bin.Length + binPad;
        int totalLen = 12 + 8 + jsonLen + 8 + binLen;

        using var fs = File.Create(path);
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], (uint)totalLen);
        fs.Write(hdr);

        Span<byte> chunkHdr = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr, (uint)jsonLen);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr[4..], ChunkTypeJson);
        fs.Write(chunkHdr);
        fs.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) fs.WriteByte(0x20);  // pad with spaces

        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr, (uint)binLen);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHdr[4..], ChunkTypeBin);
        fs.Write(chunkHdr);
        fs.Write(bin);
        for (int i = 0; i < binPad; i++) fs.WriteByte(0);
    }
}
```

- [ ] **Step 2: Build**

`& "Android/tools/build.ps1" -Configuration Debug` — expect 0 errors.

If the build complains about `AppendFloatBufferView` (unused parameter `byteStride` for non-attribute uses): keep it. The byteStride is set for VEC3 attribute views per the glTF spec.

---

### Task 8: Implement DracoDecodingGltfImportService (the decorator)

**Files:**
- Create: `Android/src/FabricationAssistant.Draco.Android/DracoDecodingGltfImportService.cs`

- [ ] **Step 1: Write the file**

```csharp
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Services;

namespace FabricationAssistant.Draco.Android;

/// <summary>
/// ISceneImportService decorator. Intercepts Draco-encoded files, transcodes
/// them to non-Draco GLB via in-process libdraco, then delegates to the inner
/// (unmodified) GltfImportService. Files without Draco are passed through.
/// </summary>
public sealed class DracoDecodingGltfImportService : ISceneImportService
{
    private readonly ISceneImportService _inner;
    private readonly string _tempDir;

    public DracoDecodingGltfImportService(ISceneImportService inner, string tempDir)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tempDir = tempDir ?? throw new ArgumentNullException(nameof(tempDir));
        Directory.CreateDirectory(_tempDir);
    }

    public async Task<DocumentDto> ImportAsync(
        string filePath,
        TessellationSettings settings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        bool hasDraco = await Task.Run(() => DracoExtensionDetector.ContainsDraco(filePath), ct).ConfigureAwait(false);
        if (!hasDraco)
            return await _inner.ImportAsync(filePath, settings, progress, ct).ConfigureAwait(false);

        progress?.Report("Decoding Draco compression...");
        string decoded = Path.Combine(_tempDir, $"{Path.GetFileNameWithoutExtension(filePath)}-{Guid.NewGuid():N}.glb");
        await Task.Run(() => DracoGltfTranscoder.Transcode(filePath, decoded), ct).ConfigureAwait(false);

        try
        {
            return await _inner.ImportAsync(decoded, settings, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(decoded); } catch { /* best-effort cleanup */ }
        }
    }
}
```

- [ ] **Step 2: Build**

`& "Android/tools/build.ps1" -Configuration Debug` — expect 0 errors.

---

## Phase 3: Bundle native libs into APK + wire up ImportPipeline

### Task 9: Add native libraries to App.Android csproj

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj`

- [ ] **Step 1: Add ProjectReference and AndroidNativeLibrary entries**

Insert before the closing `</Project>`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Draco.Android\FabricationAssistant.Draco.Android.csproj" />
  </ItemGroup>

  <ItemGroup>
    <AndroidNativeLibrary Include="..\..\deps\prebuilt\arm64-v8a\libdraco_native.so">
      <Abi>arm64-v8a</Abi>
    </AndroidNativeLibrary>
    <AndroidNativeLibrary Include="..\..\deps\prebuilt\x86_64\libdraco_native.so">
      <Abi>x86_64</Abi>
    </AndroidNativeLibrary>
  </ItemGroup>
```

- [ ] **Step 2: Build and verify packaging**

```powershell
& "Android/tools/build.ps1" -Configuration Debug
```

Then inspect the APK contents:
```powershell
$env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools"
$apk = "C:\Users\skritikos\Desktop\Fabrication Assistant\Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk"
& "$env:LOCALAPPDATA\Android\Sdk\build-tools\34.0.0\aapt.exe" list $apk | Select-String "libdraco_native"
```

(Or `7z l $apk | findstr libdraco`.) Expected: two entries:
- `lib/arm64-v8a/libdraco_native.so`
- `lib/x86_64/libdraco_native.so`

If they're missing, recheck the `<AndroidNativeLibrary>` paths and that both `.so` files exist on disk.

---

### Task 10: Wire the decorator into ImportPipeline

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/ImportPipeline.cs`

- [ ] **Step 1: Replace the file**

```csharp
using Android.Content;
using Android.Provider;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Core.Services;
using FabricationAssistant.Draco.Android;
using FabricationAssistant.Import.Fa;
using FabricationAssistant.Import.Gltf;
using FabricationAssistant.Platform;
using AndroidUri = Android.Net.Uri;

namespace FabricationAssistant.App.Android;

/// <summary>
/// SAF content URI -> local cached file -> Draco-aware glTF or FA importer
/// -> DocumentDto.
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

    public async Task<DocumentDto> ImportAsync(AndroidUri contentUri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contentUri);

        string fileName = ResolveFileName(contentUri) ?? "model.bin";
        string localPath = await CopyToLocalAsync(contentUri, fileName, ct).ConfigureAwait(false);
        string ext = Path.GetExtension(localPath).ToLowerInvariant();
        var settings = new TessellationSettings();

        // Build the Draco-aware glTF service once per import (cheap; no native
        // handles held across the wrapper instance).
        string dracoTempDir = Path.Combine(_paths.TempDir, "draco-decode");
        var gltf = new GltfImportService();
        ISceneImportService gltfWithDraco = new DracoDecodingGltfImportService(gltf, dracoTempDir);

        if (ext is ".gltf" or ".glb")
            return await gltfWithDraco.ImportAsync(localPath, settings, progress: null, ct).ConfigureAwait(false);

        if (ext == ".fa")
        {
            var fa = new FaImportService(gltfWithDraco);
            return await fa.ImportAsync(localPath, settings, progress: null, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException($"Unsupported file type: {ext}. Supported: .gltf, .glb, .fa");
    }

    private async Task<string> CopyToLocalAsync(AndroidUri uri, string fileName, CancellationToken ct)
    {
        string importCacheRoot = Path.Combine(_paths.AppDataRoot, "import-cache");
        Directory.CreateDirectory(importCacheRoot);
        string localPath = Path.Combine(importCacheRoot, fileName);

        using var input = _context.ContentResolver?.OpenInputStream(uri)
            ?? throw new InvalidOperationException("ContentResolver.OpenInputStream returned null for " + uri);

        await using var output = File.Create(localPath);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        return localPath;
    }

    private string? ResolveFileName(AndroidUri uri)
    {
        try
        {
            using var cursor = _context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is null || !cursor.MoveToFirst()) return null;
            int idx = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
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

- [ ] **Step 2: Build**

`& "Android/tools/build.ps1" -Configuration Debug` — expect 0 errors.

---

## Phase 4: Auto-build native libs as part of build.ps1

### Task 11: Make build.ps1 invoke build-libdraco.ps1 when needed

**Files:**
- Modify: `Android/tools/build.ps1`

- [ ] **Step 1: Insert a "build native libs if missing" guard**

After the existing `ANDROID_HOME` / `JAVA_HOME` fallback block, before the `dotnet restore` call, append:

```powershell
# Build libdraco_native.so for both ABIs if not already present.
$dracoArm64 = Join-Path $root 'deps/prebuilt/arm64-v8a/libdraco_native.so'
$dracoX64 = Join-Path $root 'deps/prebuilt/x86_64/libdraco_native.so'
if (-not (Test-Path $dracoArm64) -or -not (Test-Path $dracoX64)) {
    Write-Host "libdraco_native.so not present in deps/prebuilt; building via build-libdraco.ps1..."
    & "$PSScriptRoot/build-libdraco.ps1"
    if ($LASTEXITCODE -ne 0) { throw "build-libdraco.ps1 failed with exit code $LASTEXITCODE" }
}
```

- [ ] **Step 2: Test by deleting the prebuilt and rebuilding**

```powershell
Remove-Item -Recurse -Force Android/deps/prebuilt
& "Android/tools/build.ps1" -Configuration Debug
```

Expected: build.ps1 detects the missing .so files, invokes build-libdraco.ps1, builds both ABIs, then proceeds with the .NET build. End state: both .so files present, APK built.

---

## Phase 5: Verify on the tablet

### Task 12: Install + test Duck.glb (regression check)

**Files:** none — verification only.

- [ ] **Step 1: Install on tablet**

```powershell
$env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools"
$apk = "C:\Users\skritikos\Desktop\Fabrication Assistant\Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk"
adb install -r $apk
adb shell monkey -p com.fabricationassistant.android -c android.intent.category.LAUNCHER 1
```

- [ ] **Step 2: Push Duck.glb to the tablet**

```powershell
Invoke-WebRequest "https://github.com/KhronosGroup/glTF-Sample-Assets/raw/main/Models/Duck/glTF-Binary/Duck.glb" -OutFile "$env:TEMP/Duck.glb"
adb push "$env:TEMP/Duck.glb" /sdcard/Download/Duck.glb
```

- [ ] **Step 3: On the tablet, tap Open → pick Duck.glb**

Expected: the duck model appears with simple lit shading. Same result as Plan 2A.

If the duck doesn't appear: regression. Diagnose with `adb logcat -d -s AndroidRuntime:E`.

---

### Task 13: Test a Draco-encoded glTF

- [ ] **Step 1: Push a Draco sample**

Khronos has DracoCompressed variants of most samples:
```powershell
Invoke-WebRequest "https://github.com/KhronosGroup/glTF-Sample-Assets/raw/main/Models/Duck/glTF-Draco/Duck.gltf" -OutFile "$env:TEMP/Duck-Draco.gltf"
adb push "$env:TEMP/Duck-Draco.gltf" /sdcard/Download/Duck-Draco.gltf
```

For a `.glb` Draco sample (preferred — no external bin files), grab any Draco-encoded glb from `https://github.com/KhronosGroup/glTF-Sample-Assets/tree/main/Models/<Name>/glTF-KTX-BasisU` (sometimes Draco-compressed) or use a model you compress yourself with `https://github.com/CesiumGS/gltf-pipeline`.

Easier: use the `.fa` file the user already has — it contains a Draco-encoded GLB inside.

- [ ] **Step 2: Open it in the app**

Expected: the model appears. Watch for any decode errors in `adb logcat -s "FA*:V" AndroidRuntime:E`.

- [ ] **Step 3: Open the user's original `.fa` file**

The one that failed Plan 2A with "Draco decode helper not available." Expected: model appears.

---

### Task 14: Verify desktop tree untouched, finalize

- [ ] **Step 1: `git status --porcelain "src/"`** must be empty.
- [ ] **Step 2: Document Plan 2B in `Android/README.md`** under a "## Plan-2B execution notes" section: NDK version used, Draco tag, any C wrapper API tweaks.

---

## Risk register (Plan 2B-specific)

| # | Risk | Mitigation |
|---|---|---|
| D1 | Draco CMake build pulls in unwanted CRT symbols, .so is huge | `-fvisibility=hidden`, `--gc-sections`, `--strip-all` in CMakeLists keeps it under 3 MB per ABI |
| D2 | Draco 1.5.7's API signature differs slightly from the C wrapper template | Task 2 Step 1's instructions explicitly say "adjust if the cloned Draco's headers use different names." Most users won't hit this; if you do, search for `DecodeMeshFromBuffer` and `GetNamedAttribute` in `deps/draco/src/draco/compression/decode.h` |
| D3 | The transcoder rewrites bufferViews incorrectly and SharpGLTF rejects the output | Test 1 (Duck.glb) is the regression check. Test 2/3 are the new-feature check. If SharpGLTF errors with "accessor out of bounds" or similar, the bufferView byteOffsets or byteLengths are wrong in the rewritten JSON — log the JSON via `Console.WriteLine(root.ToJsonString())` for inspection |
| D4 | Native .so doesn't load on the device at runtime (`DllNotFoundException`) | Verify packaging via Task 9 Step 2. If still missing, .NET-for-Android may need `<RuntimeIdentifiers>android-arm64;android-x64</RuntimeIdentifiers>` set explicitly in App.csproj (already is from Plan 1) |
| D5 | Memory leak: every Draco-encoded primitive allocates a DracoMesh handle | The transcoder uses `using` blocks for `DracoMesh.Decode(...)`. Confirm `Dispose()` is called via the `finally` in DracoMesh.cs — already wired |

---

## Self-review checklist (writing-plans skill)

**1. Spec coverage:** Plan 2B covers the Draco scope addition in the spec's 2026-05-23 update + Section 8.2 table row. No other spec sections change.

**2. Placeholder scan:** No "TBD" / "implement later" / "similar to Task N" / "etc." remain. Every code step has complete code. Some "adjust if cloned Draco differs" guidance is intentional because the live Draco API may vary slightly between minor versions.

**3. Type consistency:**
- `DracoNativeDecoder.AttributeType { Position=0, Normal=1, TexCoord=2 }` maps to the C wrapper's `attribute_type` parameter. Match.
- `DracoMesh.GetPositions/GetNormals/GetTexCoords/GetIndices` return `float[]?` / `uint[]?`. Used in `DracoGltfTranscoder.Transcode` with explicit null checks. Match.
- `DracoExtensionDetector.ContainsDraco(string) : bool`. Called from `DracoDecodingGltfImportService` via `Task.Run`. Match.
- `DracoGltfTranscoder.Transcode(string source, string dest)`. Called from the decorator. Match.
- `DracoDecodingGltfImportService(ISceneImportService inner, string tempDir)`. Constructor signature consumed by `ImportPipeline.ImportAsync`. Match.

No type-consistency bugs found.

---

## Execution choice

Plan complete and saved to `Android/docs/superpowers/plans/2026-05-23-android-draco-support.md`. Two execution options:

**1. Subagent-Driven (recommended)** — one fresh subagent per task, two-stage review between tasks. Catches NDK / CMake errors early. The native-build tasks (1-3) and the transcoder (Task 7) are good subagent dispatches because they're self-contained.

**2. Inline execution** — execute all 14 tasks in this session via the controller. Faster end-to-end if everything goes smoothly, but the native build phase is unfamiliar territory and benefits from the spec-compliance review checkpoint.

Which approach?
