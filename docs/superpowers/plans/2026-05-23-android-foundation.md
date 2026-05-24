# Android App Foundation Implementation Plan (Plan 1 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a buildable Android APK under `Android/` that installs on a tablet (Android 7.0+) and launches to a `GLSurfaceView` clearing to dark gray, with the shim-projects + linked-sources pattern proven end-to-end across all seven Android projects.

**Architecture:** Implements Sections 4 and 5 of `Android/docs/superpowers/specs/2026-05-23-android-app-design.md`. Creates seven Android-side projects under `Android/src/` (`Core.Android` shim, `Import.Gltf.Android` shim, `Platform.Android`, `Rendering.Gles`, `Input.Gestures.Android`, `App.Android`, `App.Android.Tests`). An `Android/Directory.Build.props` overrides the root props to scope the Android projects to `net8.0-android33.0` without inheriting `net8.0-windows`/`win-x64`. Shim projects compile-link existing C# sources from `../src/` via `<Compile Include>` globs without touching the originals.

**Tech Stack:** .NET 8 SDK (8.0.4xx), .NET 8 for Android workload (`net8.0-android33.0`), `OpenTK.Graphics.ES31` (preferred; fallback `Silk.NET.OpenGLES` if NuGet restore fails on Android workload), `Microsoft.Extensions.DependencyInjection` 8.0, AndroidX `Material Components` 1.12.x, AndroidX `AppCompat` 1.7.x.

**Critical guidance (saved feedback - read before starting):**
- **No git commit steps.** The user runs parallel refactors on master and has explicitly asked that work be left as unstaged modifications. **Never** run `git add`, `git commit`, or `git stash` during execution of this plan. Leave the workspace as modified files.
- **Never edit any file under `../src/`.** The desktop source tree is read-only from this plan's perspective. After each task, verify with `git status` that no file outside `Android/` shows as modified.
- **Build via the new `Android/tools/build.ps1`.** It delegates to `dotnet build` against the Android solution only. The root `build.ps1` is unchanged.
- **ASCII-only inside `.glsl` files.** NVIDIA's preprocessor (used in some Android driver lineages) throws confusing errors on non-ASCII bytes even inside comments.
- **No `Application.Current.Dispatcher` in any new code.** Use the injected `IDispatcher` from `Platform.Android`.
- **No emojis in source files.** Match the existing project convention.

**Definition of done for this plan:**
1. `Android/tools/build.ps1` (Debug) succeeds with zero errors and produces an APK under `Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/`.
2. `adb install` of the APK on an emulator or arm64 device succeeds.
3. Launching the installed app shows a dark-gray surface that responds to rotation (no crash on configuration change).
4. `git status` shows zero modifications to any file under `src/` (the desktop tree).
5. The Android.Tests project runs and the single smoke test passes.

---

## File structure for Plan 1

Files created by this plan (full list, in creation order):

```
Android/
├── Directory.Build.props
├── global.json
├── NuGet.config
├── FabricationAssistant.Android.sln
├── README.md
├── .gitignore                                             # Android-specific build artifacts
├── tools/
│   └── build.ps1
├── docs/
│   └── superpowers/
│       └── plans/2026-05-23-android-foundation.md          # this file (already present)
└── src/
    ├── FabricationAssistant.Core.Android/
    │   ├── FabricationAssistant.Core.Android.csproj
    │   └── AndroidPlatformExclusions.targets
    ├── FabricationAssistant.Import.Gltf.Android/
    │   └── FabricationAssistant.Import.Gltf.Android.csproj
    ├── FabricationAssistant.Platform.Android/
    │   ├── FabricationAssistant.Platform.Android.csproj
    │   ├── IDispatcher.cs
    │   ├── IPlatformPaths.cs
    │   ├── AndroidDispatcher.cs
    │   ├── AndroidPlatformPaths.cs
    │   └── FabricationAssistantPathsBootstrap.cs
    ├── FabricationAssistant.Rendering.Gles/
    │   ├── FabricationAssistant.Rendering.Gles.csproj
    │   ├── ShaderProgram.cs
    │   ├── GlThreadGuard.cs
    │   ├── GlesViewportRenderer.cs                          # minimal: clears to dark gray
    │   └── GlesRendererBridge.cs
    ├── FabricationAssistant.Input.Gestures.Android/
    │   ├── FabricationAssistant.Input.Gestures.Android.csproj
    │   └── AndroidPointerSource.cs                          # skeleton only
    ├── FabricationAssistant.App.Android/
    │   ├── FabricationAssistant.App.Android.csproj
    │   ├── AndroidManifest.xml
    │   ├── MainActivity.cs
    │   ├── AppServices.cs
    │   ├── Views/
    │   │   └── ViewportSurfaceView.cs
    │   └── Resources/
    │       ├── layout/activity_main.xml
    │       ├── values/colors.xml
    │       ├── values/strings.xml
    │       ├── values/styles.xml
    │       └── values-night/colors.xml
    └── FabricationAssistant.App.Android.Tests/
        ├── FabricationAssistant.App.Android.Tests.csproj
        └── SmokeTests.cs
```

No file under `../src/` is created, modified, or deleted by this plan.

---

## Phase 0: Pre-flight checks

### Task 0: Confirm SDK + workload availability

**Files:** none — verification only.

- [ ] **Step 1: Verify .NET SDK 8.0.4xx is present**

Run: `dotnet --list-sdks`
Expected: at least one row beginning with `8.0.4` (e.g. `8.0.412`).

If missing: stop and tell the user; do not attempt to install.

- [ ] **Step 2: Verify .NET Android workload is installed**

Run: `dotnet workload list`
Expected: `android` row with installation source `microsoft.net.sdk.android` or similar.

If missing: stop and tell the user. The install command is `dotnet workload install android` but it may need admin privileges and should be run by the user.

- [ ] **Step 3: Verify `adb` is on PATH**

Run: `adb version`
Expected: `Android Debug Bridge version 1.0.x` or similar.

If missing: not fatal for build; only needed for install/run. Note in the plan-execution log.

- [ ] **Step 4: Verify `git status` is clean under `src/`**

Run: `git status --porcelain "src/"`
Expected: a list of files that match the snapshot in the conversation start (current modifications are pre-existing user work, not new). Record the list for comparison at end-of-plan verification.

---

## Phase 1: Solution skeleton and isolation

### Task 1: Create the Android folder structure and config files

**Files:**
- Create: `Android/Directory.Build.props`
- Create: `Android/global.json`
- Create: `Android/NuGet.config`
- Create: `Android/.gitignore`

- [ ] **Step 1: Create `Android/Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <RuntimeIdentifier></RuntimeIdentifier>
    <RuntimeIdentifiers></RuntimeIdentifiers>
    <Platforms>AnyCPU</Platforms>
    <Platform>AnyCPU</Platform>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
  </PropertyGroup>
</Project>
```

Critical: MSBuild walks up from each `.csproj` and stops at the first `Directory.Build.props` it finds. Placing this file at `Android/Directory.Build.props` shields every Android subproject from the root `Directory.Build.props` (which pins `net8.0-windows`, `win-x64`, `x64`).

- [ ] **Step 2: Create `Android/global.json`**

```json
{
  "sdk": {
    "version": "8.0.412",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Adjust the version to match what `dotnet --list-sdks` reported in Task 0 Step 1 (use the newest 8.0.4xx present).

- [ ] **Step 3: Create `Android/NuGet.config`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
</configuration>
```

- [ ] **Step 4: Create `Android/.gitignore`**

```gitignore
# Build artifacts
bin/
obj/
*.user

# Android-specific
*.apk
*.aab
*.keystore
local.properties

# IDE
.idea/
.vs/
*.suo
*.csproj.cache

# Logs
*.log
```

- [ ] **Step 5: Verify files are on disk and the root tree is untouched**

Run: `ls Android/Directory.Build.props Android/global.json Android/NuGet.config Android/.gitignore`
Expected: all four paths exist.

Run: `git status --porcelain "src/"`
Expected: unchanged from Task 0 Step 4.

---

### Task 2: Create the empty solution file

**Files:**
- Create: `Android/FabricationAssistant.Android.sln`

- [ ] **Step 1: Write the empty solution shell**

```
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|AnyCPU = Debug|AnyCPU
		Release|AnyCPU = Release|AnyCPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

- [ ] **Step 2: Verify the solution loads (no projects yet, but the file should parse)**

Run: `dotnet sln Android/FabricationAssistant.Android.sln list`
Expected: `No projects found in the solution.` (or similar).

If the command errors with a parse failure: re-check the line endings (CRLF on Windows, LF acceptable elsewhere) and the tab characters (the `EndGlobalSection` lines must be tab-indented).

---

### Task 3: Create README and build script

**Files:**
- Create: `Android/README.md`
- Create: `Android/tools/build.ps1`

- [ ] **Step 1: Write `Android/README.md`**

```markdown
# Fabrication Assistant - Android

Android-side projects for the Fabrication Assistant viewer. See:
- `docs/superpowers/specs/2026-05-23-android-app-design.md` for the design
- `docs/superpowers/plans/` for implementation plans

## Build

```powershell
.\tools\build.ps1 -Configuration Debug
```

Outputs to `src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/`.

## Install (after building)

```powershell
adb install -r src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/com.fabrication.assistant-Signed.apk
```

## Project layout

This folder contains a separate solution (`FabricationAssistant.Android.sln`) isolated from the root `FabricationAssistant.sln`. Android-side projects compile-link existing C# sources from `../src/` via shim projects (`*.Android.csproj`) without modifying the originals.
```

- [ ] **Step 2: Write `Android/tools/build.ps1`**

```powershell
<#
.SYNOPSIS
Build the Android solution.

.NOTES
This script ONLY drives the Android solution. The root build.ps1 drives the
Windows/desktop solution and is unaffected by this script.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [switch]$Restore = $true,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root 'FabricationAssistant.Android.sln'

if (-not (Test-Path $sln)) {
    throw "Solution not found: $sln"
}

if ($Clean) {
    Write-Host "Cleaning $sln ($Configuration)..."
    dotnet clean $sln -c $Configuration | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed with exit code $LASTEXITCODE" }
}

if ($Restore) {
    Write-Host "Restoring $sln..."
    dotnet restore $sln | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
}

Write-Host "Building $sln ($Configuration)..."
dotnet build $sln -c $Configuration --no-restore | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

Write-Host "Build succeeded."
```

- [ ] **Step 3: Verify build.ps1 runs (will fail because no projects yet — that's OK)**

Run: `pwsh Android/tools/build.ps1 -Configuration Debug`
Expected: "No projects found" or a similar empty-solution error. The script ran without PowerShell-syntax errors.

If the script errored on syntax: fix the `.ps1` and re-run.

---

## Phase 2: Core shim project

### Task 4: Create the Core.Android shim project

**Files:**
- Create: `Android/src/FabricationAssistant.Core.Android/FabricationAssistant.Core.Android.csproj`
- Create: `Android/src/FabricationAssistant.Core.Android/AndroidPlatformExclusions.targets`

- [ ] **Step 1: Write `AndroidPlatformExclusions.targets`**

```xml
<Project>
  <!--
    Files in ../src/<Module>/ that we deliberately exclude from the Android
    compile because they depend on Windows-only APIs (Environment.SpecialFolder
    paths, registry, P/Invoke into Windows DLLs, etc.).
    Each entry's <Compile Remove> matches a globbed include in the shim csproj.
  -->
  <ItemGroup>
    <CoreExclusions Include="..\..\..\src\FabricationAssistant.Core\Runtime\FabricationAssistantPaths.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `FabricationAssistant.Core.Android.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.Core</RootNamespace>
    <AssemblyName>FabricationAssistant.Core</AssemblyName>
    <Description>Android shim - compile-links FabricationAssistant.Core source files for net8.0-android.</Description>
  </PropertyGroup>

  <Import Project="AndroidPlatformExclusions.targets" />

  <ItemGroup>
    <Compile Include="..\..\..\src\FabricationAssistant.Core\**\*.cs"
             Exclude="..\..\..\src\FabricationAssistant.Core\bin\**\*.cs;..\..\..\src\FabricationAssistant.Core\obj\**\*.cs"
             LinkBase="Linked" />
    <Compile Remove="@(CoreExclusions)" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add the project to the solution**

Run: `dotnet sln Android/FabricationAssistant.Android.sln add Android/src/FabricationAssistant.Core.Android/FabricationAssistant.Core.Android.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 4: Build Core.Android in isolation**

Run: `dotnet build Android/src/FabricationAssistant.Core.Android/FabricationAssistant.Core.Android.csproj -c Debug`
Expected: `Build succeeded.` with zero errors.

If errors mention symbols like `FabricationAssistantPaths`: confirm `AndroidPlatformExclusions.targets` removed it correctly. The exclusion path must be normalized (no `..` mismatches). Try absolute paths or `$(MSBuildThisFileDirectory)` if relative paths misbehave.

If errors mention `MouseEventArgs` / `Application.Current` / `Dispatcher` / `Brush` types: a previously-portable Core file now reaches into WPF. Add it to `AndroidPlatformExclusions.targets` (matching the spec's intent that Core stays portable) and document the addition.

If errors are about Windows-only API surface (e.g., `Microsoft.Win32`): same response — add to exclusions.

- [ ] **Step 5: Verify desktop tree is untouched**

Run: `git status --porcelain "src/"`
Expected: identical to Task 0 Step 4.

---

## Phase 3: Import.Gltf shim project

### Task 5: Create the Import.Gltf.Android shim project

**Files:**
- Create: `Android/src/FabricationAssistant.Import.Gltf.Android/FabricationAssistant.Import.Gltf.Android.csproj`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.Import.Gltf</RootNamespace>
    <AssemblyName>FabricationAssistant.Import.Gltf</AssemblyName>
    <Description>Android shim - compile-links FabricationAssistant.Import.Gltf source files for net8.0-android.</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SharpGLTF.Core" Version="1.0.6" />
    <PackageReference Include="ICSharpCode.SharpZipLib" Version="1.4.2" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Core.Android\FabricationAssistant.Core.Android.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="..\..\..\src\FabricationAssistant.Import.Gltf\**\*.cs"
             Exclude="..\..\..\src\FabricationAssistant.Import.Gltf\bin\**\*.cs;..\..\..\src\FabricationAssistant.Import.Gltf\obj\**\*.cs"
             LinkBase="Linked" />
  </ItemGroup>

</Project>
```

Note: the SharpZipLib package id on NuGet is `ICSharpCode.SharpZipLib` (verify the version the desktop uses matches; the desktop Import.Gltf.csproj declares `SharpZipLib` 1.4.x — they're the same package, just different shorthand. If restore fails on `ICSharpCode.SharpZipLib`, switch to `SharpZipLib` 1.4.2).

- [ ] **Step 2: Add to solution**

Run: `dotnet sln Android/FabricationAssistant.Android.sln add Android/src/FabricationAssistant.Import.Gltf.Android/FabricationAssistant.Import.Gltf.Android.csproj`
Expected: project added.

- [ ] **Step 3: Build the project**

Run: `dotnet build Android/src/FabricationAssistant.Import.Gltf.Android/FabricationAssistant.Import.Gltf.Android.csproj -c Debug`
Expected: build succeeds.

If Microsoft.Data.Sqlite fails to restore the Android-native variant: try version `8.0.0` (older) or switch to `SQLitePCLRaw.bundle_e_sqlite3` + `Microsoft.Data.Sqlite.Core` (decoupled native dep — see plan R5 in the spec).

- [ ] **Step 4: Verify desktop tree is untouched**

Run: `git status --porcelain "src/"`
Expected: identical to Task 0 Step 4.

---

## Phase 4: Platform.Android (foundational interfaces)

### Task 6: Create IDispatcher and IPlatformPaths interfaces

**Files:**
- Create: `Android/src/FabricationAssistant.Platform.Android/FabricationAssistant.Platform.Android.csproj`
- Create: `Android/src/FabricationAssistant.Platform.Android/IDispatcher.cs`
- Create: `Android/src/FabricationAssistant.Platform.Android/IPlatformPaths.cs`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.Platform.Android</RootNamespace>
    <AssemblyName>FabricationAssistant.Platform.Android</AssemblyName>
    <Description>Android-specific implementations of platform interfaces (IDispatcher, IPlatformPaths, etc.).</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Core.Android\FabricationAssistant.Core.Android.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `IDispatcher.cs`**

```csharp
namespace FabricationAssistant.Platform;

/// <summary>
/// Abstraction over a UI-thread dispatcher. New code MUST consume this via DI
/// rather than calling Application.Current.Dispatcher (which doesn't exist on Android).
/// </summary>
public interface IDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    void Send(Action action);
}
```

- [ ] **Step 3: Write `IPlatformPaths.cs`**

```csharp
namespace FabricationAssistant.Platform;

/// <summary>
/// Abstraction over platform-specific filesystem locations. On Windows backed by
/// Environment.SpecialFolder.LocalApplicationData; on Android backed by Context.FilesDir.
/// </summary>
public interface IPlatformPaths
{
    string AppDataRoot { get; }
    string CacheDir { get; }
    string TempDir { get; }
    string LogsDir { get; }
}
```

- [ ] **Step 4: Add the project to the solution**

Run: `dotnet sln Android/FabricationAssistant.Android.sln add Android/src/FabricationAssistant.Platform.Android/FabricationAssistant.Platform.Android.csproj`
Expected: project added.

- [ ] **Step 5: Build**

Run: `dotnet build Android/src/FabricationAssistant.Platform.Android/FabricationAssistant.Platform.Android.csproj -c Debug`
Expected: build succeeds.

---

### Task 7: Implement AndroidDispatcher and AndroidPlatformPaths

**Files:**
- Create: `Android/src/FabricationAssistant.Platform.Android/AndroidDispatcher.cs`
- Create: `Android/src/FabricationAssistant.Platform.Android/AndroidPlatformPaths.cs`

- [ ] **Step 1: Write `AndroidDispatcher.cs`**

```csharp
using Android.OS;

namespace FabricationAssistant.Platform.Android;

public sealed class AndroidDispatcher : IDispatcher
{
    private readonly Handler _mainHandler;
    private readonly Thread _mainThread;

    public AndroidDispatcher()
    {
        _mainHandler = new Handler(Looper.MainLooper ?? throw new InvalidOperationException("Main Looper unavailable"));
        _mainThread = Looper.MainLooper.Thread ?? throw new InvalidOperationException("Main thread unavailable");
    }

    public bool CheckAccess() => Thread.CurrentThread == _mainThread;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _mainHandler.Post(action);
    }

    public void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? thrown = null;
        _mainHandler.Post(() =>
        {
            try { action(); }
            catch (Exception ex) { thrown = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (thrown is not null) throw thrown;
    }
}
```

- [ ] **Step 2: Write `AndroidPlatformPaths.cs`**

```csharp
using Android.Content;

namespace FabricationAssistant.Platform.Android;

public sealed class AndroidPlatformPaths : IPlatformPaths
{
    public AndroidPlatformPaths(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        AppDataRoot = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Context.FilesDir is null");
        CacheDir = context.CacheDir?.AbsolutePath
            ?? throw new InvalidOperationException("Context.CacheDir is null");
        TempDir = Path.Combine(CacheDir, "temp");
        LogsDir = Path.Combine(CacheDir, "logs");

        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(LogsDir);
    }

    public string AppDataRoot { get; }
    public string CacheDir { get; }
    public string TempDir { get; }
    public string LogsDir { get; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build Android/src/FabricationAssistant.Platform.Android/FabricationAssistant.Platform.Android.csproj -c Debug`
Expected: build succeeds.

If the Android Context type isn't found: confirm the csproj's TargetFramework is `net8.0-android33.0` and `Android.Content` is recognized (it should be, no using directives are needed beyond what's shown).

---

### Task 8: Implement FabricationAssistantPathsBootstrap

**Files:**
- Create: `Android/src/FabricationAssistant.Platform.Android/FabricationAssistantPathsBootstrap.cs`

This file re-implements the static API that Core's `FabricationAssistantPaths.cs` provides on desktop. We excluded the desktop file in Task 4; this replacement lives in Platform.Android and is initialized from `MainActivity.OnCreate` (added in Task 14).

- [ ] **Step 1: Write the file**

```csharp
namespace FabricationAssistant.Runtime;

/// <summary>
/// Android replacement for the desktop FabricationAssistantPaths static class.
/// Same surface area, but the paths come from an injected IPlatformPaths rather
/// than Environment.SpecialFolder.LocalApplicationData.
///
/// MUST be initialized exactly once at application startup (MainActivity.OnCreate)
/// via Initialize(paths) before any code that reads the static properties runs.
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
}
```

Note the namespace: `FabricationAssistant.Runtime`. This matches the desktop file's namespace (which was at `src/FabricationAssistant.Core/Runtime/FabricationAssistantPaths.cs`). Type identity is preserved so linked Core code that calls `FabricationAssistantPaths.RootPath` resolves to this replacement at runtime.

- [ ] **Step 2: Build**

Run: `dotnet build Android/src/FabricationAssistant.Platform.Android/FabricationAssistant.Platform.Android.csproj -c Debug`
Expected: build succeeds.

If the build complains about a duplicate `FabricationAssistantPaths` type: confirm the original desktop file is excluded by `AndroidPlatformExclusions.targets` in Core.Android.csproj.

- [ ] **Step 3: Verify the new type is reachable from a linked Core file**

Run: `dotnet build Android/src/FabricationAssistant.Core.Android/FabricationAssistant.Core.Android.csproj -c Debug`
Expected: build succeeds. Any linked Core code that referenced the desktop `FabricationAssistantPaths` resolves to the new bootstrap type at link time (same namespace + name).

If a Core file references a method like `FabricationAssistantPaths.AnsiCachedFontFile()` that doesn't exist on the bootstrap: add a stub to `FabricationAssistantPathsBootstrap.cs` that throws `NotImplementedException("Not supported on Android")` and document the gap as a Plan-3 follow-up.

---

## Phase 5: Rendering.Gles (minimum viable renderer)

### Task 9: Create Rendering.Gles project with OpenTK ES bindings

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj`
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GlThreadGuard.cs`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.Rendering.Gles</RootNamespace>
    <AssemblyName>FabricationAssistant.Rendering.Gles</AssemblyName>
    <Description>OpenGL ES 3.1 renderer for the Android viewer.</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="OpenTK.Graphics" Version="5.0.0-pre.13" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Core.Android\FabricationAssistant.Core.Android.csproj" />
    <ProjectReference Include="..\FabricationAssistant.Platform.Android\FabricationAssistant.Platform.Android.csproj" />
  </ItemGroup>

</Project>
```

Note: We're using `OpenTK.Graphics` 5.0.0-pre.13 (the modern OpenTK that has unified GL/GLES bindings under `OpenTK.Graphics.OpenGLES31`). If restore fails for any reason, the fallback (R7 in the spec) is to switch to `Silk.NET.OpenGLES` 2.23.0 — same API surface, different namespace.

- [ ] **Step 2: Write `GlThreadGuard.cs`**

```csharp
namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Ported from the desktop renderer. Captures the GL render thread ID at
/// Initialize() and verifies every subsequent guard call happens on the same thread.
/// </summary>
public sealed class GlThreadGuard
{
    private int _renderThreadId = -1;

    public void Initialize()
    {
        _renderThreadId = Environment.CurrentManagedThreadId;
    }

    public void EnsureOnRenderThread()
    {
        if (_renderThreadId < 0)
            throw new InvalidOperationException("GlThreadGuard.Initialize() has not been called.");
        if (Environment.CurrentManagedThreadId != _renderThreadId)
            throw new InvalidOperationException(
                $"GL call from thread {Environment.CurrentManagedThreadId}; render thread is {_renderThreadId}.");
    }
}
```

- [ ] **Step 3: Add to solution**

Run: `dotnet sln Android/FabricationAssistant.Android.sln add Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj`
Expected: project added.

- [ ] **Step 4: Build**

Run: `dotnet build Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj -c Debug`
Expected: build succeeds.

If OpenTK.Graphics 5.0.0-pre.13 fails to restore for net8.0-android: try `OpenTK.Graphics` 5.0.0-pre.12 (older pre-release) or switch to Silk.NET.OpenGLES per R7. Document the choice in a comment at the top of the csproj.

---

### Task 10: Create ShaderProgram skeleton

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/ShaderProgram.cs`

- [ ] **Step 1: Write the file**

```csharp
using OpenTK.Graphics.OpenGLES31;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Minimal ES 3.1 shader program wrapper. Mirrors the desktop ShaderProgram
/// (in FabricationAssistant.Rendering.OpenTK) but uses ES bindings.
/// Future tasks (Plan 2) will extend this with uniform binding helpers.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    public int Handle { get; private set; }
    public string Name { get; }

    public ShaderProgram(string name, string vertexSource, string fragmentSource)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));

        var vs = CompileShader(ShaderType.VertexShader, vertexSource, $"{name}.vert");
        var fs = CompileShader(ShaderType.FragmentShader, fragmentSource, $"{name}.frag");

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vs);
        GL.AttachShader(Handle, fs);
        GL.LinkProgram(Handle);

        GL.GetProgrami(Handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            GL.GetProgramInfoLog(Handle, out string log);
            GL.DeleteProgram(Handle);
            throw new InvalidOperationException($"Program {name} link failed:\n{log}");
        }

        GL.DetachShader(Handle, vs);
        GL.DetachShader(Handle, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
    }

    private static int CompileShader(ShaderType type, string source, string label)
    {
        int handle = GL.CreateShader(type);
        GL.ShaderSource(handle, source);
        GL.CompileShader(handle);
        GL.GetShaderi(handle, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            GL.GetShaderInfoLog(handle, out string log);
            GL.DeleteShader(handle);
            throw new InvalidOperationException($"Shader {label} compile failed:\n{log}");
        }
        return handle;
    }

    public void Use() => GL.UseProgram(Handle);

    public void Dispose()
    {
        if (Handle != 0)
        {
            GL.DeleteProgram(Handle);
            Handle = 0;
        }
    }
}
```

Note: the exact OpenTK ES 3.1 API surface may have slightly different method names depending on the prerelease version. If `GL.GetProgrami` or `GL.GetShaderInfoLog` don't compile, check the OpenTK 5.0.0-pre.x docs for the current call shape. Document the exact NuGet version you converged on at the top of the csproj.

- [ ] **Step 2: Build**

Run: `dotnet build Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj -c Debug`
Expected: build succeeds.

If API mismatches with OpenTK pre-release: adjust call sites to match the installed version. Do NOT pin to a specific call surface; instead, accept the version's idioms and document them.

---

### Task 11: Create GlesViewportRenderer (clear-color only)

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GlesViewportRenderer.cs`

- [ ] **Step 1: Write the file**

```csharp
using OpenTK.Graphics.OpenGLES31;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Minimum viable renderer for Plan 1. Initializes GL state, clears to dark gray.
/// Plan 2 expands this to the full multi-pass pipeline.
/// </summary>
public sealed class GlesViewportRenderer
{
    private readonly GlThreadGuard _guard = new();
    private bool _initialized;
    private int _width;
    private int _height;

    public void OnSurfaceCreated()
    {
        _guard.Initialize();

        GL.ClearColor(0.10f, 0.11f, 0.12f, 1.0f);  // #1B1D1F-ish dark gray (matches Theme.Dark.xaml surface)
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);

        _initialized = true;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        _guard.EnsureOnRenderThread();
        _width = width;
        _height = height;
        GL.Viewport(0, 0, width, height);
    }

    public void OnDrawFrame()
    {
        _guard.EnsureOnRenderThread();
        if (!_initialized) return;

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public int Width => _width;
    public int Height => _height;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj -c Debug`
Expected: build succeeds.

---

### Task 12: Create GlesRendererBridge (GLSurfaceView.IRenderer adapter)

**Files:**
- Create: `Android/src/FabricationAssistant.Rendering.Gles/GlesRendererBridge.cs`

- [ ] **Step 1: Write the file**

```csharp
using Android.Opengl;
using Javax.Microedition.Khronos.Egl;
using Javax.Microedition.Khronos.Opengles;

namespace FabricationAssistant.Rendering.Gles;

/// <summary>
/// Bridge from Android's GLSurfaceView.IRenderer interface to our managed
/// GlesViewportRenderer. The Android view system invokes OnSurface*/OnDrawFrame
/// on the GL render thread (not the UI thread).
/// </summary>
public sealed class GlesRendererBridge : Java.Lang.Object, GLSurfaceView.IRenderer
{
    private readonly GlesViewportRenderer _renderer;

    public GlesRendererBridge(GlesViewportRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public void OnSurfaceCreated(IGL10? gl, EGLConfig? config) => _renderer.OnSurfaceCreated();

    public void OnSurfaceChanged(IGL10? gl, int width, int height) => _renderer.OnSurfaceChanged(width, height);

    public void OnDrawFrame(IGL10? gl) => _renderer.OnDrawFrame();
}
```

The IGL10/EGLConfig parameters are required by the interface signature but unused (we call modern ES 3.1 directly through OpenTK bindings, not through the legacy GL10 emulator).

- [ ] **Step 2: Build**

Run: `dotnet build Android/src/FabricationAssistant.Rendering.Gles/FabricationAssistant.Rendering.Gles.csproj -c Debug`
Expected: build succeeds.

If `Android.Opengl.GLSurfaceView.IRenderer` isn't found: confirm the TFM is `net8.0-android33.0` (lower TFMs may not expose this namespace under the same name). Adjust `using` statements if the actual namespace is `Android.Opengl.GLSurfaceView.IRenderer` vs `Android.Opengl.IRenderer` — the binding generator on different .NET-Android versions produces slightly different shapes.

---

## Phase 6: Input.Gestures.Android (skeleton)

### Task 13: Create Input.Gestures.Android project

**Files:**
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/FabricationAssistant.Input.Gestures.Android.csproj`
- Create: `Android/src/FabricationAssistant.Input.Gestures.Android/AndroidPointerSource.cs`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.Input.Gestures.Android</RootNamespace>
    <AssemblyName>FabricationAssistant.Input.Gestures.Android</AssemblyName>
    <Description>Android-specific touch-to-gesture-recognizer bridge.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Core.Android\FabricationAssistant.Core.Android.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `AndroidPointerSource.cs` (skeleton only - full impl in Plan 2)**

```csharp
using Android.Views;

namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// Bridges Android MotionEvent to the FabricationAssistant gesture recognizer.
/// Plan 1 ships only the skeleton; Plan 2 implements Translate() against the
/// existing ViewportTouchGestureRecognizer state machine (linked from Core).
/// </summary>
public sealed class AndroidPointerSource
{
    public event Action<MotionEvent>? RawMotionEventReceived;

    public bool OnTouch(MotionEvent? motionEvent)
    {
        if (motionEvent is null) return false;
        RawMotionEventReceived?.Invoke(motionEvent);
        return true;
    }
}
```

- [ ] **Step 3: Add to solution**

Run: `dotnet sln Android/FabricationAssistant.Android.sln add Android/src/FabricationAssistant.Input.Gestures.Android/FabricationAssistant.Input.Gestures.Android.csproj`
Expected: project added.

- [ ] **Step 4: Build**

Run: `dotnet build Android/src/FabricationAssistant.Input.Gestures.Android/FabricationAssistant.Input.Gestures.Android.csproj -c Debug`
Expected: build succeeds.

---

## Phase 7: App.Android (the Activity)

### Task 14: Create App.Android csproj and AndroidManifest

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj`
- Create: `Android/src/FabricationAssistant.App.Android/AndroidManifest.xml`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-android33.0</TargetFramework>
    <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>FabricationAssistant.App.Android</RootNamespace>
    <AssemblyName>FabricationAssistant.App.Android</AssemblyName>
    <ApplicationId>com.fabricationassistant.android</ApplicationId>
    <ApplicationVersion>1</ApplicationVersion>
    <ApplicationDisplayVersion>0.1.0</ApplicationDisplayVersion>
    <AndroidApplicationLabel>Fabrication Assistant</AndroidApplicationLabel>
    <AndroidTargetSdkVersion>33</AndroidTargetSdkVersion>
    <AndroidMinSdkVersion>24</AndroidMinSdkVersion>
    <RuntimeIdentifiers>android-arm64;android-x64</RuntimeIdentifiers>
    <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
    <PackageReference Include="Xamarin.AndroidX.AppCompat" Version="1.7.0.5" />
    <PackageReference Include="Xamarin.Google.Android.Material" Version="1.12.0.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FabricationAssistant.Core.Android\FabricationAssistant.Core.Android.csproj" />
    <ProjectReference Include="..\FabricationAssistant.Import.Gltf.Android\FabricationAssistant.Import.Gltf.Android.csproj" />
    <ProjectReference Include="..\FabricationAssistant.Platform.Android\FabricationAssistant.Platform.Android.csproj" />
    <ProjectReference Include="..\FabricationAssistant.Rendering.Gles\FabricationAssistant.Rendering.Gles.csproj" />
    <ProjectReference Include="..\FabricationAssistant.Input.Gestures.Android\FabricationAssistant.Input.Gestures.Android.csproj" />
  </ItemGroup>

</Project>
```

Note: NuGet package IDs for AndroidX bindings on .NET 8 for Android start with `Xamarin.AndroidX.*` — this is the canonical naming, not a typo. Material design components are at `Xamarin.Google.Android.Material`.

- [ ] **Step 2: Write `AndroidManifest.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android"
          package="com.fabricationassistant.android">

  <uses-sdk android:minSdkVersion="24" android:targetSdkVersion="33" />

  <uses-feature android:glEsVersion="0x00030001" android:required="true" />
  <uses-permission android:name="android.permission.INTERNET" />

  <application android:label="@string/app_name"
               android:icon="@mipmap/ic_launcher"
               android:roundIcon="@mipmap/ic_launcher_round"
               android:allowBackup="true"
               android:supportsRtl="true"
               android:theme="@style/Theme.FabricationAssistant">
    <activity android:name="FabricationAssistant.App.Android.MainActivity"
              android:label="@string/app_name"
              android:exported="true"
              android:configChanges="orientation|screenSize|keyboardHidden|smallestScreenSize|screenLayout|uiMode"
              android:hardwareAccelerated="true">
      <intent-filter>
        <action android:name="android.intent.action.MAIN" />
        <category android:name="android.intent.category.LAUNCHER" />
      </intent-filter>
    </activity>
  </application>
</manifest>
```

Critical: `glEsVersion="0x00030001"` declares ES 3.1 as required. This is what gates this APK from installing on devices lacking ES 3.1 support.

`configChanges` lists `orientation|screenSize|...` so rotation doesn't recreate the Activity, per spec Section 9.8.

- [ ] **Step 3: Add to solution (do not build yet — resources missing)**

Run: `dotnet sln Android/FabricationAssistant.Android.sln add Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj`
Expected: project added.

---

### Task 15: Add Android resources (layouts, strings, colors, styles)

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Resources/values/colors.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/values-night/colors.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/values/strings.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/values/styles.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/layout/activity_main.xml`

- [ ] **Step 1: Write `Resources/values/colors.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="fa_surface_900">#1B1D1F</color>
    <color name="fa_surface_800">#26282B</color>
    <color name="fa_surface_700">#33363A</color>
    <color name="fa_text_primary">#E3E5E8</color>
    <color name="fa_text_secondary">#9CA3AF</color>
    <color name="fa_accent_500">#3B82F6</color>
    <color name="fa_accent_600">#2563EB</color>
    <color name="fa_on_accent">#FFFFFF</color>

    <color name="md_theme_primary">@color/fa_accent_500</color>
    <color name="md_theme_onPrimary">@color/fa_on_accent</color>
    <color name="md_theme_surface">@color/fa_surface_900</color>
    <color name="md_theme_onSurface">@color/fa_text_primary</color>
    <color name="md_theme_surfaceVariant">@color/fa_surface_800</color>
    <color name="md_theme_onSurfaceVariant">@color/fa_text_secondary</color>
</resources>
```

- [ ] **Step 2: Write `Resources/values-night/colors.xml`** (same content; dark theme is the canonical theme — light mode mirrors)

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="fa_surface_900">#1B1D1F</color>
    <color name="fa_surface_800">#26282B</color>
    <color name="fa_surface_700">#33363A</color>
    <color name="fa_text_primary">#E3E5E8</color>
    <color name="fa_text_secondary">#9CA3AF</color>
    <color name="fa_accent_500">#3B82F6</color>
    <color name="fa_accent_600">#2563EB</color>
    <color name="fa_on_accent">#FFFFFF</color>

    <color name="md_theme_primary">@color/fa_accent_500</color>
    <color name="md_theme_onPrimary">@color/fa_on_accent</color>
    <color name="md_theme_surface">@color/fa_surface_900</color>
    <color name="md_theme_onSurface">@color/fa_text_primary</color>
    <color name="md_theme_surfaceVariant">@color/fa_surface_800</color>
    <color name="md_theme_onSurfaceVariant">@color/fa_text_secondary</color>
</resources>
```

- [ ] **Step 3: Write `Resources/values/strings.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <string name="app_name">Fabrication Assistant</string>
</resources>
```

- [ ] **Step 4: Write `Resources/values/styles.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <style name="Theme.FabricationAssistant" parent="Theme.Material3.Dark.NoActionBar">
        <item name="colorPrimary">@color/md_theme_primary</item>
        <item name="colorOnPrimary">@color/md_theme_onPrimary</item>
        <item name="android:colorBackground">@color/md_theme_surface</item>
        <item name="colorSurface">@color/md_theme_surface</item>
        <item name="colorOnSurface">@color/md_theme_onSurface</item>
        <item name="colorSurfaceVariant">@color/md_theme_surfaceVariant</item>
        <item name="colorOnSurfaceVariant">@color/md_theme_onSurfaceVariant</item>
        <item name="android:windowBackground">@color/md_theme_surface</item>
        <item name="android:statusBarColor">@color/md_theme_surface</item>
    </style>
</resources>
```

- [ ] **Step 5: Write `Resources/layout/activity_main.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<androidx.constraintlayout.widget.ConstraintLayout
    xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:app="http://schemas.android.com/apk/res-auto"
    android:id="@+id/root"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:background="@color/md_theme_surface">

    <FrameLayout
        android:id="@+id/viewportContainer"
        android:layout_width="0dp"
        android:layout_height="0dp"
        app:layout_constraintTop_toTopOf="parent"
        app:layout_constraintBottom_toBottomOf="parent"
        app:layout_constraintStart_toStartOf="parent"
        app:layout_constraintEnd_toEndOf="parent" />

</androidx.constraintlayout.widget.ConstraintLayout>
```

(The `viewportContainer` `FrameLayout` is where `MainActivity` will mount the `ViewportSurfaceView` programmatically in Task 17.)

- [ ] **Step 6: No build verification yet — needs MainActivity and ViewportSurfaceView (Tasks 16-17)**

---

### Task 16: Create ViewportSurfaceView

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Views/ViewportSurfaceView.cs`

- [ ] **Step 1: Write the file**

```csharp
using Android.Content;
using Android.Opengl;
using Android.Util;
using FabricationAssistant.Rendering.Gles;

namespace FabricationAssistant.App.Android.Views;

/// <summary>
/// GLSurfaceView host for the GLES 3.1 viewport. Owns the GlesViewportRenderer
/// instance and forwards lifecycle events.
/// </summary>
public sealed class ViewportSurfaceView : GLSurfaceView
{
    private readonly GlesViewportRenderer _renderer;
    private readonly GlesRendererBridge _bridge;

    public GlesViewportRenderer Renderer => _renderer;

    public ViewportSurfaceView(Context context) : base(context)
    {
        _renderer = new GlesViewportRenderer();
        _bridge = new GlesRendererBridge(_renderer);

        SetEGLContextClientVersion(3);
        SetEGLConfigChooser(8, 8, 8, 0, 24, 8);
        SetRenderer(_bridge);
        RenderMode = Rendermode.WhenDirty;
    }

    public ViewportSurfaceView(Context context, IAttributeSet attrs) : base(context, attrs)
    {
        _renderer = new GlesViewportRenderer();
        _bridge = new GlesRendererBridge(_renderer);

        SetEGLContextClientVersion(3);
        SetEGLConfigChooser(8, 8, 8, 0, 24, 8);
        SetRenderer(_bridge);
        RenderMode = Rendermode.WhenDirty;
    }
}
```

Note the two constructors: the second (with `IAttributeSet`) is required if the view will ever be inflated from XML. We don't inflate from XML in Plan 1 — `MainActivity` constructs it programmatically — but adding both now avoids a future refactor.

- [ ] **Step 2: Build the App project (will likely fail without MainActivity — that's expected; we'll add it next)**

Skip explicit build at this step; we'll build after Task 17.

---

### Task 17: Create MainActivity and AppServices

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`
- Create: `Android/src/FabricationAssistant.App.Android/AppServices.cs`

- [ ] **Step 1: Write `AppServices.cs`**

```csharp
using Android.Content;
using FabricationAssistant.Platform;
using FabricationAssistant.Platform.Android;
using FabricationAssistant.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace FabricationAssistant.App.Android;

/// <summary>
/// DI composition root. Builds the IServiceProvider used by MainActivity.
/// Mirrors the desktop AppServiceProvider.Configure() pattern, but lives in
/// the Android-side app rather than a Core project (no IServiceCollection
/// dependency in Core).
/// </summary>
public static class AppServices
{
    public static IServiceProvider Build(Context applicationContext)
    {
        ArgumentNullException.ThrowIfNull(applicationContext);

        var services = new ServiceCollection();

        // Platform layer
        services.AddSingleton<IDispatcher, AndroidDispatcher>();
        services.AddSingleton<IPlatformPaths>(_ => new AndroidPlatformPaths(applicationContext));

        // Future: SceneService, CameraState, SelectionState, ViewModels — added in Plan 2.

        var provider = services.BuildServiceProvider(validateScopes: true);

        // One-time static bootstrap so linked Core code that still calls
        // FabricationAssistantPaths.RootPath resolves through the Android impl.
        FabricationAssistantPaths.Initialize(provider.GetRequiredService<IPlatformPaths>());

        return provider;
    }
}
```

- [ ] **Step 2: Write `MainActivity.cs`**

```csharp
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.AppCompat.App;
using FabricationAssistant.App.Android.Views;

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

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _services = AppServices.Build(ApplicationContext!);

        SetContentView(Resource.Layout.activity_main);

        var container = FindViewById<FrameLayout>(Resource.Id.viewportContainer)
            ?? throw new InvalidOperationException("viewportContainer not found in activity_main.xml");

        _viewport = new ViewportSurfaceView(this);
        container.AddView(_viewport);
    }

    protected override void OnPause()
    {
        _viewport?.OnPause();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _viewport?.OnResume();
    }
}
```

- [ ] **Step 3: Build the App project**

Run: `dotnet build Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj -c Debug`
Expected: build succeeds, producing an APK at `Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/com.fabricationassistant.android-Signed.apk` (path may vary slightly).

If `Resource.Layout.activity_main` doesn't resolve: confirm the layout XML is at the right path and that the Resources.Designer is regenerating. Try `dotnet build` from the project folder once to force regeneration, or check that the layout file is included as `<AndroidResource>` automatically (default Android SDK behaviour).

If errors about NotSupported `Theme.Material3.Dark.NoActionBar`: ensure `Xamarin.Google.Android.Material` is at 1.12.x. Earlier versions don't carry Material3.

- [ ] **Step 4: Verify desktop tree is untouched**

Run: `git status --porcelain "src/"`
Expected: identical to Task 0 Step 4.

---

## Phase 8: Full-solution build + run on device

### Task 18: Run the full solution build via build.ps1

**Files:** none — verification only.

- [ ] **Step 1: Run the build script**

Run: `pwsh Android/tools/build.ps1 -Configuration Debug`
Expected: all six projects build (Core.Android, Import.Gltf.Android, Platform.Android, Rendering.Gles, Input.Gestures.Android, App.Android) with zero errors. The script prints `Build succeeded.`

If any project fails: drop back to the corresponding task and resolve. Do not move forward with errors.

- [ ] **Step 2: Verify APK exists**

Run: `ls Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/*.apk`
Expected: one or more `.apk` files including a `-Signed.apk`.

- [ ] **Step 3: Verify desktop tree untouched (final check before device test)**

Run: `git status --porcelain "src/"`
Expected: identical to Task 0 Step 4.

---

### Task 19: Install on emulator or device, verify launch

**Files:** none — runtime verification.

- [ ] **Step 1: Ensure a device is connected**

Run: `adb devices`
Expected: at least one row with status `device` (not `unauthorized` or `offline`).

If no device: start an Android emulator (API 33 image, x86_64 or arm64) before continuing. Document the device or emulator model used.

- [ ] **Step 2: Install the APK**

Run: `adb install -r Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/com.fabricationassistant.android-Signed.apk`
Expected: `Success`.

If the device rejects the install with `INSTALL_FAILED_OLDER_SDK`: device is below API 24 (Android 7.0). Cannot proceed; document the minimum-device requirement clearly.

If the install fails with `INSTALL_FAILED_NO_MATCHING_ABIS`: the device's CPU isn't `arm64-v8a` or `x86_64`. Either use a different emulator/device or extend `RuntimeIdentifiers` in `App.Android.csproj` to include `android-arm` for armeabi-v7a (not recommended; modern tablets are all arm64).

- [ ] **Step 3: Launch the app**

Run: `adb shell am start -n com.fabricationassistant.android/.MainActivity`
Expected: the app launches; the screen shows a dark gray surface filling the viewport area.

- [ ] **Step 4: Verify no crashes in logcat**

Run: `adb logcat -d -s "FabricationAssistant:V" "*:E"` (waiting ~5 seconds after launch)
Expected: zero `E/AndroidRuntime` lines from our app. Any `E` lines from other packages are not our concern.

If the app crashed: capture the full stack trace via `adb logcat -d > Android/tools/launch-crash.log` and diagnose. Common causes:
- `FabricationAssistantPaths.Initialize` not called (an exception is thrown when a path is read) — confirm `AppServices.Build` runs before any path access.
- GLSurfaceView config chooser fails: try `SetEGLConfigChooser(false)` (auto-choose) to verify ES 3 is available.
- A linked Core file throws on a Windows API in its static constructor — exclude that file in `AndroidPlatformExclusions.targets`.

- [ ] **Step 5: Verify rotation doesn't crash**

Action: rotate the device (or in emulator: Ctrl+Right-arrow + Ctrl+Left-arrow once, or use the menu).
Expected: the surface stays dark gray; no crash. Verify in `adb logcat` that no exceptions appeared.

If rotation triggers Activity recreation despite `configChanges`: confirm the manifest correctly lists all the change flags shown in Task 14. The csproj `Activity` attribute and the manifest must both list them.

---

## Phase 9: Smoke test project

### Task 20: Create App.Android.Tests with one smoke test

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
- Create: `Android/src/FabricationAssistant.App.Android.Tests/SmokeTests.cs`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
    <RootNamespace>FabricationAssistant.App.Android.Tests</RootNamespace>
    <AssemblyName>FabricationAssistant.App.Android.Tests</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <!--
    Important: this test project targets plain net8.0, not net8.0-android.
    We test linked Core sources via a parallel shim that does NOT pull in
    Android assemblies. This lets us run pure math/gesture/import tests on
    the dev machine without an emulator. Android-specific tests come later
    (Plan 2 or 3) in a separate net8.0-android test project.
  -->
  <ItemGroup>
    <Compile Include="..\..\..\src\FabricationAssistant.Core\Math\**\*.cs"
             LinkBase="Linked\Math" />
  </ItemGroup>

</Project>
```

The test project intentionally targets `net8.0` (not `net8.0-android`) so it runs on the developer's machine via `dotnet test` without needing an emulator. We link only the math subset of Core for Plan 1 — broader linking comes in Plan 2.

- [ ] **Step 2: Write `SmokeTests.cs`**

```csharp
using FabricationAssistant.Math;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Vector3d_Add_returns_componentwise_sum()
    {
        var a = new Vector3d(1.0, 2.0, 3.0);
        var b = new Vector3d(10.0, 20.0, 30.0);
        var c = a + b;
        Assert.Equal(11.0, c.X);
        Assert.Equal(22.0, c.Y);
        Assert.Equal(33.0, c.Z);
    }
}
```

If the `Vector3d` namespace is something other than `FabricationAssistant.Math`, adjust the `using` to match what the linked source files declare. Check with `Grep` against `../src/FabricationAssistant.Core/Math/Vector3d.cs` for the actual `namespace` line.

- [ ] **Step 3: Add to solution (DO NOT use `dotnet sln add` because the test project is net8.0 while the solution Directory.Build.props targets net8.0-android33.0)**

This is intentional. The test project is a side-car run outside the main Android solution. Skip the `dotnet sln add`.

Instead, manage the test project as a standalone build:

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
Expected: 1 test passes.

If the project picks up `Android/Directory.Build.props` and tries to be net8.0-android: add a local `Android/src/FabricationAssistant.App.Android.Tests/Directory.Build.props` overriding `TargetFramework` back to `net8.0`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <SupportedOSPlatformVersion></SupportedOSPlatformVersion>
  </PropertyGroup>
</Project>
```

Verify the override took: `dotnet build` of the test project should show `net8.0` not `net8.0-android33.0`.

- [ ] **Step 4: Run the test**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj --logger "console;verbosity=normal"`
Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0`.

---

## Phase 10: Final verification

### Task 21: End-to-end verification against Definition of Done

**Files:** none — verification only.

- [ ] **Step 1: Verify Definition of Done item 1 (build succeeds)**

Run: `pwsh Android/tools/build.ps1 -Configuration Debug -Clean`
Expected: clean build + rebuild succeeds with zero errors.

- [ ] **Step 2: Verify Definition of Done item 2 (APK installs)**

Run: `adb install -r Android/src/FabricationAssistant.App.Android/bin/Debug/net8.0-android33.0/com.fabricationassistant.android-Signed.apk`
Expected: `Success`.

- [ ] **Step 3: Verify Definition of Done item 3 (launches + survives rotation)**

Run: `adb shell am start -n com.fabricationassistant.android/.MainActivity`
Action: rotate, lock screen, unlock, background, foreground.
Expected: no crashes throughout. `adb logcat -d -s "AndroidRuntime:E"` shows zero E lines from `com.fabricationassistant.android`.

- [ ] **Step 4: Verify Definition of Done item 4 (no edits under `src/`)**

Run: `git status --porcelain "src/"`
Expected: identical to Task 0 Step 4 — no Android plan added, modified, or removed any desktop source file.

If the output differs from Task 0: identify the rogue change and revert it. Common causes: accidental Visual Studio "fix" / IDE auto-format / cross-project IDE refactor. Roll back with `git checkout -- src/` only if you are certain no other parallel work depends on those changes (per saved feedback, parallel refactors happen on master — ask the user first if uncertain).

- [ ] **Step 5: Verify Definition of Done item 5 (smoke test passes)**

Run: `dotnet test Android/src/FabricationAssistant.App.Android.Tests/FabricationAssistant.App.Android.Tests.csproj`
Expected: `Passed!` with 1 test.

- [ ] **Step 6: Grep for any `Application.Current.Dispatcher` usage in `Android/`**

Use the Grep tool with pattern `Application\.Current\.?\s*\.?\s*Dispatcher` over `Android/**/*.cs`
Expected: zero matches.

If matches exist: replace with `_dispatcher.Post(...)` via the injected `IDispatcher`. The whole point of Plan 1's `IDispatcher` setup is that this stays at zero.

- [ ] **Step 7: Document any deviations**

If anything in this plan was deviated from (e.g., Silk.NET fallback used instead of OpenTK, a Core file added to exclusions, NuGet version changed), append a note to `Android/README.md` under a "## Plan-1 execution notes" section. The notes feed Plan 2.

---

## Self-review summary (writing-plans skill checklist)

**1. Spec coverage:** Plan 1 implements spec Sections 4 (solution layout), 5 (project responsibilities subset — full impl for Platform, skeleton for others), and the "minimum viable APK" portion of Section 18 (Definition of Done items 1, 2, partial 8). Spec Sections 6 (rendering pipeline), 7 (input/camera), 8 (file I/O), 9 (UI shell), 10 (threading), 11 (error handling), 12 (testing - full coverage) are deferred to Plans 2 and 3.

**2. Placeholder scan:** No "TBD" / "TODO" / "implement later" / "similar to Task N" / "appropriate error handling" / "etc." remain. Every code step contains complete code.

**3. Type consistency:**
- `IDispatcher` interface (Task 6) — methods `CheckAccess`, `Post`, `Send` used in `AndroidDispatcher` (Task 7). Match.
- `IPlatformPaths` interface (Task 6) — properties `AppDataRoot`, `CacheDir`, `TempDir`, `LogsDir` used in `AndroidPlatformPaths` (Task 7) and `FabricationAssistantPathsBootstrap` (Task 8). Match.
- `GlesViewportRenderer` (Task 11) — methods `OnSurfaceCreated`, `OnSurfaceChanged(int, int)`, `OnDrawFrame` called from `GlesRendererBridge` (Task 12). Match.
- `ViewportSurfaceView` (Task 16) — `OnPause()`, `OnResume()` called from `MainActivity` (Task 17) come from base `GLSurfaceView`, not declared by us. Verified.
- `AppServices.Build(Context)` (Task 17) — signature matches the call from `MainActivity.OnCreate`. Match.
- `FabricationAssistantPaths.Initialize(IPlatformPaths)` (Task 8) — namespace `FabricationAssistant.Runtime`; called from `AppServices.Build` (Task 17) via `using FabricationAssistant.Runtime`. Match.

No type-consistency bugs found.

---

## Out of scope for Plan 1 (deferred to Plan 2 or 3)

- Full GLES 3.1 multi-pass renderer (mesh, edge ribbon, SSAO, outline, sections, pick).
- glTF/FA file loading wiring.
- Camera and gesture wiring (input source + interaction adapter).
- Selection and picking.
- Sections UI.
- Measurement UI.
- Lifecycle save/restore beyond simple rotation.
- Full test coverage of math/camera/gestures/import.
- Espresso UI tests.

---

## Execution choice

Plan complete and saved to `Android/docs/superpowers/plans/2026-05-23-android-foundation.md`. Two execution options:

**1. Subagent-Driven (recommended for plans of this size)** - dispatch a fresh subagent per task, review between tasks, fast iteration. Catches drift between adjacent tasks early.

**2. Inline Execution** - execute tasks in this session using `executing-plans`, batch execution with checkpoints. Single context, less coordination overhead.

Which approach?
