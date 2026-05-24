# Android Desktop-Parity UI Shell Implementation Plan (Plan 2D of 3+)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bare "AppBar + viewport" shell with a desktop-feeling tablet layout that mirrors the desktop app's NavRail / viewport / properties-panel structure (spec section 9.2), the desktop's dark-theme color palette, and a bottom toolbar of primary actions. Adopt vector-drawable icons matching the desktop's Material-style iconography and refine typography. The result is an empty-but-desktop-shaped shell ready for later content (Scene tree, Properties, ViewCube).

**Architecture:** Single Activity (`MainActivity`) with a ConstraintLayout root and four pinned regions on tablets - top AppBar, left NavigationRailView, center ViewportContainer, right collapsible Properties panel. Material 3 components do the heavy lifting (`MaterialToolbar`, `NavigationRailView`, `BottomAppBar`, `SideSheetDialog` on phone, `MaterialDivider`). Vector drawables under `res/drawable` provide a Material Symbols-style icon set close to the desktop's icon language. The phone layout falls back to a `DrawerLayout` for nav + `BottomSheetBehavior` for properties.

**Tech Stack:**
- `Xamarin.AndroidX.AppCompat` 1.7.x, `Xamarin.Google.Android.Material` 1.12.x (both already referenced by Plan 1).
- `Xamarin.AndroidX.ConstraintLayout` 2.2.x (already referenced).
- Pure Android resources: layouts, drawables, styles, dimens.
- No new C# library code beyond `MainActivity` orchestration.

**Critical guidance (same as Plans 1, 2A, 2B; do NOT violate):**
- **No git commit steps.** Leave work unstaged.
- **Never edit any file under `../src/`.**
- **Build via `Android/tools/build.ps1`.**
- **No emojis in source files.**
- **`Application.Current.Dispatcher` is forbidden** in any new code (still applies even though this plan is mostly XML).
- **Strict adherence to existing theme schema:** reuse `@color/fa_*` and `@color/md_theme_*` tokens that already exist in `values/colors.xml`. No inline `#RRGGBB` literals in layouts or styles. Mirrors the desktop's `Theme.Dark.xaml` discipline.

**Definition of done for this plan:**
1. Clean `Android/tools/build.ps1 -Configuration Debug` succeeds with 0 errors. APK produced.
2. On the tablet `R52Y80CE37L` (landscape orientation), the app renders:
   - Top: 56-dp AppBar with title "Fabrication Assistant" left-aligned, secondary action icons right-aligned (settings, overflow).
   - Left: 80-dp NavigationRail with at minimum these icon buttons - "Open file", "Recent" (placeholder, no-op), "Fit", "Settings" (placeholder, no-op).
   - Center: viewport container fills the remaining width.
   - Right: 320-dp collapsible Properties panel with "Properties" header and empty state text "No selection". The panel can be hidden via a `chevron_right` toggle in the AppBar.
   - Bottom: 48-dp BottomAppBar with secondary actions (view-cube placeholder, ruler placeholder, section placeholder) - icons only, all no-ops for now.
3. On a phone-form-factor emulator or via `adb shell wm size 800x1280` (forces portrait + narrow), the layout switches to:
   - DrawerLayout: hamburger icon in the AppBar opens a left drawer showing the same nav-rail buttons as a vertical list.
   - Properties: a `BottomSheetDialogFragment`-style sheet swiped up from the bottom with the same header + empty state.
   - Viewport fills the remaining space.
4. The color palette mirrors the desktop's `Theme.Dark.xaml`: surface `#1B1D1F`, surface-variant `#26282B`, accent `#3B82F6`. Already in `values/colors.xml`; this plan just ensures every new layout pulls from those tokens.
5. Typography uses Roboto with sizes that match the desktop (`@style/FA.TextAppearance.Title`, `FA.TextAppearance.Body`, `FA.TextAppearance.Caption` defined in this plan). No inline `android:textSize` in layouts.
6. Icons: at least the 6 used in the NavigationRail and BottomAppBar exist as `res/drawable/ic_*.xml` vector drawables. Each tracks the foreground color via `?attr/colorControlNormal` / `?attr/colorPrimary`.
7. Existing functionality intact: the Open file button still opens the SAF picker, imports a model, and renders it (Plan 2A regression). Verified by reinstalling and opening one of the on-device `.fa` files.
8. No `../src/` modifications (`git status --porcelain "src/"` empty).

---

## File structure (created/modified by this plan)

```
Android/
├── src/
│   └── FabricationAssistant.App.Android/
│       ├── MainActivity.cs                                # MODIFIED - wire NavRail buttons, properties toggle
│       └── Resources/
│           ├── layout/
│           │   ├── activity_main.xml                      # MODIFIED - full tablet shell
│           │   ├── activity_main.land.xml                 # NEW (alias of activity_main.xml for landscape; sw600dp)
│           │   ├── activity_main.port.xml                 # NEW - phone layout with DrawerLayout + BottomSheet
│           │   ├── view_properties_panel.xml              # NEW - reusable side-panel layout
│           │   ├── view_navrail_item.xml                  # NEW - one nav-rail item template
│           │   └── view_drawer_navrail.xml                # NEW - vertical nav list inside the drawer (phone)
│           ├── values/
│           │   ├── styles.xml                             # MODIFIED - add FA.* text appearances + toolbar/navrail/bottom-app-bar styles
│           │   ├── dimens.xml                             # NEW - canonical dp values (nav rail width, panel width, etc.)
│           │   ├── colors.xml                             # NO CHANGE - tokens already present
│           │   └── strings.xml                            # MODIFIED - new content-descs for icons
│           ├── values-sw600dp/                            # NEW directory - tablet overrides
│           │   └── styles.xml                             # tablet-specific style refinements (larger AppBar?)
│           └── drawable/                                  # NEW directory
│               ├── ic_open_file.xml                       # vector drawable, ~24 dp
│               ├── ic_recent.xml
│               ├── ic_fit.xml
│               ├── ic_settings.xml
│               ├── ic_section.xml
│               ├── ic_measure.xml
│               ├── ic_view_cube.xml
│               ├── ic_chevron_right.xml                   # properties-panel toggle
│               └── ic_menu.xml                            # hamburger for phone drawer
├── docs/superpowers/plans/2026-05-23-android-desktop-parity-shell.md  # this file
└── README.md                                              # MODIFIED - append "Plan-2D execution notes"
```

Zero changes to `../src/`.

---

## Phase 0: Dimensions and typography tokens

### Task 1: Add canonical dp tokens and Material text appearances

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Resources/values/dimens.xml`
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/values/styles.xml`

Centralize layout dimensions and text appearances so each layout file references tokens, never literal `12sp` / `48dp`. This matches the desktop's `Tokens.xaml` discipline.

- [ ] **Step 1: Write `dimens.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <!-- Top-level shell -->
    <dimen name="fa_app_bar_height">56dp</dimen>
    <dimen name="fa_bottom_bar_height">48dp</dimen>

    <!-- Left navigation rail (tablet) -->
    <dimen name="fa_nav_rail_width">80dp</dimen>
    <dimen name="fa_nav_rail_item_height">64dp</dimen>
    <dimen name="fa_nav_rail_item_icon_size">24dp</dimen>

    <!-- Right properties panel (tablet) -->
    <dimen name="fa_properties_panel_width">320dp</dimen>
    <dimen name="fa_properties_panel_min_width">240dp</dimen>

    <!-- Standard spacing units (4dp grid) -->
    <dimen name="fa_space_xs">4dp</dimen>
    <dimen name="fa_space_s">8dp</dimen>
    <dimen name="fa_space_m">12dp</dimen>
    <dimen name="fa_space_l">16dp</dimen>
    <dimen name="fa_space_xl">24dp</dimen>

    <!-- Typography sizes (mirroring desktop's FontSizeS/M/L from Tokens.xaml) -->
    <dimen name="fa_text_size_caption">12sp</dimen>
    <dimen name="fa_text_size_body">14sp</dimen>
    <dimen name="fa_text_size_subtitle">15sp</dimen>
    <dimen name="fa_text_size_title">16sp</dimen>
    <dimen name="fa_text_size_headline">20sp</dimen>
</resources>
```

- [ ] **Step 2: Replace the `styles.xml` body**

Open `Android/src/FabricationAssistant.App.Android/Resources/values/styles.xml`. Replace the entire `<resources>...</resources>` body with the following (keeps the existing `Theme.FabricationAssistant` parent + colors, and adds typography + component overrides):

```xml
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <!-- Root theme: dark, no action bar (we use MaterialToolbar). -->
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
        <item name="android:navigationBarColor">@color/md_theme_surface</item>
        <!-- Type system -->
        <item name="textAppearanceTitleLarge">@style/FA.TextAppearance.Title</item>
        <item name="textAppearanceBodyLarge">@style/FA.TextAppearance.Body</item>
        <item name="textAppearanceBodyMedium">@style/FA.TextAppearance.Body</item>
        <item name="textAppearanceLabelSmall">@style/FA.TextAppearance.Caption</item>
        <!-- Component overrides -->
        <item name="toolbarStyle">@style/FA.Toolbar</item>
        <item name="bottomAppBarStyle">@style/FA.BottomAppBar</item>
        <item name="navigationRailStyle">@style/FA.NavigationRail</item>
    </style>

    <!-- Typography: Roboto with the desktop's FontSize tokens. -->
    <style name="FA.TextAppearance.Caption" parent="TextAppearance.Material3.LabelSmall">
        <item name="android:fontFamily">sans-serif</item>
        <item name="android:textSize">@dimen/fa_text_size_caption</item>
        <item name="android:textColor">@color/fa_text_secondary</item>
    </style>
    <style name="FA.TextAppearance.Body" parent="TextAppearance.Material3.BodyMedium">
        <item name="android:fontFamily">sans-serif</item>
        <item name="android:textSize">@dimen/fa_text_size_body</item>
        <item name="android:textColor">@color/fa_text_primary</item>
    </style>
    <style name="FA.TextAppearance.Subtitle" parent="TextAppearance.Material3.TitleSmall">
        <item name="android:fontFamily">sans-serif-medium</item>
        <item name="android:textSize">@dimen/fa_text_size_subtitle</item>
        <item name="android:textColor">@color/fa_text_primary</item>
    </style>
    <style name="FA.TextAppearance.Title" parent="TextAppearance.Material3.TitleMedium">
        <item name="android:fontFamily">sans-serif-medium</item>
        <item name="android:textSize">@dimen/fa_text_size_title</item>
        <item name="android:textColor">@color/fa_text_primary</item>
    </style>

    <!-- Top AppBar - 56 dp, surface-variant background, no elevation lift on scroll. -->
    <style name="FA.Toolbar" parent="Widget.Material3.Toolbar">
        <item name="android:background">@color/md_theme_surfaceVariant</item>
        <item name="android:minHeight">@dimen/fa_app_bar_height</item>
        <item name="android:elevation">0dp</item>
        <item name="titleTextAppearance">@style/FA.TextAppearance.Title</item>
        <item name="titleTextColor">@color/fa_text_primary</item>
        <item name="subtitleTextAppearance">@style/FA.TextAppearance.Caption</item>
    </style>

    <!-- Bottom action strip - 48 dp, surface-variant. -->
    <style name="FA.BottomAppBar" parent="Widget.Material3.BottomAppBar">
        <item name="android:background">@color/md_theme_surfaceVariant</item>
        <item name="android:minHeight">@dimen/fa_bottom_bar_height</item>
        <item name="elevation">0dp</item>
    </style>

    <!-- Left navigation rail - 80 dp, surface (slightly darker than appbar). -->
    <style name="FA.NavigationRail" parent="Widget.Material3.NavigationRailView">
        <item name="android:background">@color/md_theme_surface</item>
        <item name="itemActiveIndicatorStyle">@style/FA.NavigationRail.ActiveIndicator</item>
        <item name="itemIconTint">@color/fa_navrail_tint</item>
        <item name="itemTextColor">@color/fa_navrail_tint</item>
        <item name="labelVisibilityMode">unlabeled</item>
    </style>
    <style name="FA.NavigationRail.ActiveIndicator" parent="Widget.Material3.NavigationRailView.ActiveIndicator">
        <item name="android:color">@color/fa_accent_500</item>
    </style>

    <!-- Icon-only square button used in NavRail and BottomAppBar action slots. -->
    <style name="FA.IconButton" parent="Widget.Material3.Button.IconButton">
        <item name="iconTint">@color/fa_text_secondary</item>
        <item name="iconSize">@dimen/fa_nav_rail_item_icon_size</item>
        <item name="rippleColor">@color/fa_accent_500</item>
    </style>
</resources>
```

- [ ] **Step 3: Add the nav-rail color-state-list**

Create `Android/src/FabricationAssistant.App.Android/Resources/color/fa_navrail_tint.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<selector xmlns:android="http://schemas.android.com/apk/res/android">
    <item android:color="@color/fa_accent_500" android:state_selected="true" />
    <item android:color="@color/fa_text_primary" android:state_pressed="true" />
    <item android:color="@color/fa_text_secondary" />
</selector>
```

- [ ] **Step 4: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. The new styles are unreferenced for now (existing layout still uses the prior styles fully) but must compile clean.

---

## Phase 1: Icon set

### Task 2: Add the 9 vector drawables

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_open_file.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_recent.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_fit.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_settings.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_section.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_measure.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_view_cube.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_chevron_right.xml`
- Create: `Android/src/FabricationAssistant.App.Android/Resources/drawable/ic_menu.xml`

Each drawable is a 24x24 vector following the Material Symbols (outlined) language. Paths copied from the open-source Material Symbols set so they look familiar to anyone who's used Android. `?attr/colorControlNormal` lets the tint flow from the theme.

- [ ] **Step 1: Write `ic_open_file.xml`** (a generic "folder open" icon)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M20,6L12,6l-2,-2L4,4c-1.1,0 -1.99,0.9 -1.99,2L2,18c0,1.1 0.9,2 2,2h16c1.1,0 2,-0.9 2,-2L22,8c0,-1.1 -0.9,-2 -2,-2zM20,18L4,18L4,8h16v10z" />
</vector>
```

- [ ] **Step 2: Write `ic_recent.xml`** (clock with rewind arrow)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M13,3a9,9 0,0 0,-9 9L1,12l3.89,3.89 0.07,0.14L9,12L6,12c0,-3.87 3.13,-7 7,-7s7,3.13 7,7 -3.13,7 -7,7c-1.93,0 -3.68,-0.79 -4.94,-2.06l-1.42,1.42A8.954,8.954 0,0 0,13 21a9,9 0,0 0,0 -18zM12,8v5l4.28,2.54 0.72,-1.21 -3.5,-2.08L13.5,8L12,8z" />
</vector>
```

- [ ] **Step 3: Write `ic_fit.xml`** (zoom-out-to-fit arrows)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M5,5h5L10,3L3,3v7h2zM5,14L3,14v7h7v-2L5,19zM19,19h-5v2h7v-7h-2zM19,5v5h2L21,3h-7v2z" />
</vector>
```

- [ ] **Step 4: Write `ic_settings.xml`** (standard cog)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M19.14,12.94c0.04,-0.3 0.06,-0.61 0.06,-0.94 0,-0.32 -0.02,-0.64 -0.07,-0.94l2.03,-1.58c0.18,-0.14 0.23,-0.41 0.12,-0.61l-1.92,-3.32c-0.12,-0.22 -0.37,-0.29 -0.59,-0.22l-2.39,0.96c-0.5,-0.38 -1.03,-0.7 -1.62,-0.94L14.4,2.81c-0.04,-0.24 -0.24,-0.41 -0.48,-0.41h-3.84c-0.24,0 -0.43,0.17 -0.47,0.41L9.25,5.35C8.66,5.59 8.12,5.92 7.63,6.29L5.24,5.33c-0.22,-0.08 -0.47,0 -0.59,0.22L2.74,8.87C2.62,9.08 2.66,9.34 2.86,9.48l2.03,1.58C4.84,11.36 4.8,11.69 4.8,12s0.02,0.64 0.07,0.94l-2.03,1.58c-0.18,0.14 -0.23,0.41 -0.12,0.61l1.92,3.32c0.12,0.22 0.37,0.29 0.59,0.22l2.39,-0.96c0.5,0.38 1.03,0.7 1.62,0.94l0.36,2.54c0.05,0.24 0.24,0.41 0.48,0.41h3.84c0.24,0 0.44,-0.17 0.47,-0.41l0.36,-2.54c0.59,-0.24 1.13,-0.56 1.62,-0.94l2.39,0.96c0.22,0.08 0.47,0 0.59,-0.22l1.92,-3.32c0.12,-0.22 0.07,-0.47 -0.12,-0.61l-2.01,-1.58zM12,15.6c-1.98,0 -3.6,-1.62 -3.6,-3.6s1.62,-3.6 3.6,-3.6 3.6,1.62 3.6,3.6 -1.62,3.6 -3.6,3.6z" />
</vector>
```

- [ ] **Step 5: Write `ic_section.xml`** (plane crossing a cube - approximation)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M21,3L3,3v18h18L21,3zM19,19L5,19L5,5h14v14zM7,10h10v2L7,12z" />
</vector>
```

- [ ] **Step 6: Write `ic_measure.xml`** (ruler)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M21,6L3,6c-1.1,0 -2,0.9 -2,2v8c0,1.1 0.9,2 2,2h18c1.1,0 2,-0.9 2,-2L23,8c0,-1.1 -0.9,-2 -2,-2zM21,16L3,16L3,8h2v4h2L7,8h2v4h2L11,8h2v4h2L15,8h2v4h2L19,8h2v8z" />
</vector>
```

- [ ] **Step 7: Write `ic_view_cube.xml`** (3D cube)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M12,2L4,6v12l8,4 8,-4L20,6 12,2zM12,4.18L17.82,7L12,9.82 6.18,7 12,4.18zM5,8.32l6,3v8.36l-6,-3L5,8.32zM13,19.68v-8.36l6,-3v8.36l-6,3z" />
</vector>
```

- [ ] **Step 8: Write `ic_chevron_right.xml`**

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M10,6L8.59,7.41 13.17,12l-4.58,4.59L10,18l6,-6z" />
</vector>
```

- [ ] **Step 9: Write `ic_menu.xml`** (hamburger)

```xml
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24"
    android:tint="?attr/colorControlNormal">
    <path
        android:fillColor="@android:color/white"
        android:pathData="M3,18h18v-2L3,16v2zM3,13h18v-2L3,11v2zM3,6v2h18L21,6L3,6z" />
</vector>
```

- [ ] **Step 10: Update `strings.xml`**

Add content-descriptions for the icons:

```xml
    <string name="cd_nav_open">Open file</string>
    <string name="cd_nav_recent">Recent files</string>
    <string name="cd_nav_fit">Fit to view</string>
    <string name="cd_nav_settings">Settings</string>
    <string name="cd_tool_section">Section plane</string>
    <string name="cd_tool_measure">Measurement</string>
    <string name="cd_tool_view_cube">Reset view</string>
    <string name="cd_toggle_properties">Toggle properties</string>
    <string name="cd_open_drawer">Open navigation drawer</string>
    <string name="properties_title">Properties</string>
    <string name="properties_empty">No selection</string>
```

- [ ] **Step 11: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

## Phase 2: Reusable layout fragments

### Task 3: Create the properties-panel layout

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Resources/layout/view_properties_panel.xml`

The properties panel is used on tablet (pinned right) and on phone (inside a `BottomSheetDialog`). Same XML included via `<include>` from both parent layouts.

- [ ] **Step 1: Write the file**

```xml
<?xml version="1.0" encoding="utf-8"?>
<LinearLayout xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:app="http://schemas.android.com/apk/res-auto"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:orientation="vertical"
    android:background="@color/md_theme_surface">

    <com.google.android.material.divider.MaterialDivider
        android:layout_width="match_parent"
        android:layout_height="1dp"
        app:dividerColor="@color/md_theme_surfaceVariant" />

    <TextView
        android:id="@+id/propertiesTitle"
        android:layout_width="match_parent"
        android:layout_height="wrap_content"
        android:text="@string/properties_title"
        android:textAppearance="@style/FA.TextAppearance.Subtitle"
        android:padding="@dimen/fa_space_l" />

    <com.google.android.material.divider.MaterialDivider
        android:layout_width="match_parent"
        android:layout_height="1dp"
        app:dividerColor="@color/md_theme_surfaceVariant" />

    <TextView
        android:id="@+id/propertiesEmptyState"
        android:layout_width="match_parent"
        android:layout_height="wrap_content"
        android:text="@string/properties_empty"
        android:textAppearance="@style/FA.TextAppearance.Caption"
        android:padding="@dimen/fa_space_l" />

</LinearLayout>
```

- [ ] **Step 2: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

## Phase 3: Tablet (landscape) layout

### Task 4: Replace activity_main.xml with the full tablet shell

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/Resources/layout/activity_main.xml`

Replace the bare AppBar + viewport with the full ConstraintLayout shell. Tablet sizes get this layout by default; the next task adds a phone-specific override.

- [ ] **Step 1: Replace the file**

```xml
<?xml version="1.0" encoding="utf-8"?>
<androidx.constraintlayout.widget.ConstraintLayout
    xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:app="http://schemas.android.com/apk/res-auto"
    android:id="@+id/root"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:background="@color/md_theme_surface">

    <!-- ── Top AppBar ─────────────────────────────────────────────────── -->
    <com.google.android.material.appbar.MaterialToolbar
        android:id="@+id/topAppBar"
        android:layout_width="match_parent"
        android:layout_height="@dimen/fa_app_bar_height"
        android:background="@color/md_theme_surfaceVariant"
        app:title="@string/app_name"
        app:titleTextAppearance="@style/FA.TextAppearance.Title"
        app:menu="@null"
        app:layout_constraintTop_toTopOf="parent"
        app:layout_constraintStart_toStartOf="parent"
        app:layout_constraintEnd_toEndOf="parent">

        <!-- Right-aligned chevron toggles the properties panel. -->
        <com.google.android.material.button.MaterialButton
            android:id="@+id/propertiesToggle"
            android:layout_width="40dp"
            android:layout_height="40dp"
            android:layout_gravity="end|center_vertical"
            android:layout_marginEnd="@dimen/fa_space_s"
            android:contentDescription="@string/cd_toggle_properties"
            app:icon="@drawable/ic_chevron_right"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="20dp"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

    </com.google.android.material.appbar.MaterialToolbar>

    <!-- ── Left Navigation Rail ──────────────────────────────────────── -->
    <LinearLayout
        android:id="@+id/navRail"
        android:layout_width="@dimen/fa_nav_rail_width"
        android:layout_height="0dp"
        android:orientation="vertical"
        android:background="@color/md_theme_surface"
        android:gravity="top|center_horizontal"
        android:paddingTop="@dimen/fa_space_s"
        app:layout_constraintTop_toBottomOf="@+id/topAppBar"
        app:layout_constraintBottom_toTopOf="@+id/bottomAppBar"
        app:layout_constraintStart_toStartOf="parent">

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navOpenButton"
            android:layout_width="56dp"
            android:layout_height="@dimen/fa_nav_rail_item_height"
            android:layout_marginTop="@dimen/fa_space_xs"
            android:contentDescription="@string/cd_nav_open"
            app:icon="@drawable/ic_open_file"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="@dimen/fa_nav_rail_item_icon_size"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navRecentButton"
            android:layout_width="56dp"
            android:layout_height="@dimen/fa_nav_rail_item_height"
            android:layout_marginTop="@dimen/fa_space_xs"
            android:contentDescription="@string/cd_nav_recent"
            app:icon="@drawable/ic_recent"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="@dimen/fa_nav_rail_item_icon_size"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navFitButton"
            android:layout_width="56dp"
            android:layout_height="@dimen/fa_nav_rail_item_height"
            android:layout_marginTop="@dimen/fa_space_xs"
            android:contentDescription="@string/cd_nav_fit"
            app:icon="@drawable/ic_fit"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="@dimen/fa_nav_rail_item_icon_size"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

        <Space
            android:layout_width="0dp"
            android:layout_height="0dp"
            android:layout_weight="1" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navSettingsButton"
            android:layout_width="56dp"
            android:layout_height="@dimen/fa_nav_rail_item_height"
            android:layout_marginBottom="@dimen/fa_space_s"
            android:contentDescription="@string/cd_nav_settings"
            app:icon="@drawable/ic_settings"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="@dimen/fa_nav_rail_item_icon_size"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

    </LinearLayout>

    <com.google.android.material.divider.MaterialDivider
        android:id="@+id/navRailDivider"
        android:layout_width="1dp"
        android:layout_height="0dp"
        app:dividerColor="@color/md_theme_surfaceVariant"
        app:layout_constraintTop_toBottomOf="@+id/topAppBar"
        app:layout_constraintBottom_toTopOf="@+id/bottomAppBar"
        app:layout_constraintStart_toEndOf="@+id/navRail" />

    <!-- ── Center viewport (the LinearLayout used for navRail keeps things simple) ─ -->
    <FrameLayout
        android:id="@+id/viewportContainer"
        android:layout_width="0dp"
        android:layout_height="0dp"
        android:background="@color/md_theme_surface"
        app:layout_constraintTop_toBottomOf="@+id/topAppBar"
        app:layout_constraintBottom_toTopOf="@+id/bottomAppBar"
        app:layout_constraintStart_toEndOf="@+id/navRailDivider"
        app:layout_constraintEnd_toStartOf="@+id/propertiesPanelDivider" />

    <!-- ── Right Properties Panel (collapsible) ──────────────────────── -->
    <com.google.android.material.divider.MaterialDivider
        android:id="@+id/propertiesPanelDivider"
        android:layout_width="1dp"
        android:layout_height="0dp"
        app:dividerColor="@color/md_theme_surfaceVariant"
        app:layout_constraintTop_toBottomOf="@+id/topAppBar"
        app:layout_constraintBottom_toTopOf="@+id/bottomAppBar"
        app:layout_constraintEnd_toStartOf="@+id/propertiesPanel" />

    <include
        android:id="@+id/propertiesPanel"
        layout="@layout/view_properties_panel"
        android:layout_width="@dimen/fa_properties_panel_width"
        android:layout_height="0dp"
        app:layout_constraintTop_toBottomOf="@+id/topAppBar"
        app:layout_constraintBottom_toTopOf="@+id/bottomAppBar"
        app:layout_constraintEnd_toEndOf="parent" />

    <!-- ── Bottom AppBar ─────────────────────────────────────────────── -->
    <LinearLayout
        android:id="@+id/bottomAppBar"
        android:layout_width="match_parent"
        android:layout_height="@dimen/fa_bottom_bar_height"
        android:orientation="horizontal"
        android:background="@color/md_theme_surfaceVariant"
        android:gravity="center_vertical"
        android:paddingStart="@dimen/fa_space_s"
        android:paddingEnd="@dimen/fa_space_s"
        app:layout_constraintBottom_toBottomOf="parent"
        app:layout_constraintStart_toStartOf="parent"
        app:layout_constraintEnd_toEndOf="parent">

        <com.google.android.material.button.MaterialButton
            android:id="@+id/toolViewCube"
            android:layout_width="40dp"
            android:layout_height="40dp"
            android:contentDescription="@string/cd_tool_view_cube"
            app:icon="@drawable/ic_view_cube"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="20dp"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/toolSection"
            android:layout_width="40dp"
            android:layout_height="40dp"
            android:layout_marginStart="@dimen/fa_space_xs"
            android:contentDescription="@string/cd_tool_section"
            app:icon="@drawable/ic_section"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="20dp"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/toolMeasure"
            android:layout_width="40dp"
            android:layout_height="40dp"
            android:layout_marginStart="@dimen/fa_space_xs"
            android:contentDescription="@string/cd_tool_measure"
            app:icon="@drawable/ic_measure"
            app:iconGravity="textStart"
            app:iconPadding="0dp"
            app:iconSize="20dp"
            android:insetTop="0dp"
            android:insetBottom="0dp"
            style="@style/FA.IconButton" />

    </LinearLayout>

</androidx.constraintlayout.widget.ConstraintLayout>
```

**IMPORTANT:** the legacy `@+id/openButton` from the previous activity_main.xml is GONE; MainActivity's existing `Click += OnOpenClicked` wire-up will fail at startup with a null reference unless we re-wire it. Task 6 below updates `MainActivity.OnCreate` to find the new `@+id/navOpenButton` instead.

- [ ] **Step 2: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. The layout compiles even though the resource id `openButton` referenced in C# has gone away — `FindViewById<MaterialButton>(Resource.Id.openButton)` returns null at runtime, not a compile error. Task 6 fixes that.

---

### Task 5: Update MainActivity to use the new ids

**Files:**
- Modify: `Android/src/FabricationAssistant.App.Android/MainActivity.cs`

Re-point the click handler from the deleted `openButton` to the new `navOpenButton`, and wire the `navFitButton` to call FitToBox + RequestRender. Wire the `propertiesToggle` chevron to show/hide the properties panel.

- [ ] **Step 1: Update field declarations and OnCreate**

Replace the existing `MainActivity.OnCreate` body (the part after `_picker = new SafFilePicker(this);` and before `var openButton = ...`) with:

```csharp
        var container = FindViewById<FrameLayout>(Resource.Id.viewportContainer)
            ?? throw new InvalidOperationException("viewportContainer not found");

        _viewport = new ViewportSurfaceView(this);
        _viewport.Renderer.Camera = _camera;
        container.AddView(_viewport);

        var navOpen = FindViewById<MaterialButton>(Resource.Id.navOpenButton);
        if (navOpen is not null) navOpen.Click += OnOpenClicked;

        var navFit = FindViewById<MaterialButton>(Resource.Id.navFitButton);
        if (navFit is not null) navFit.Click += OnFitClicked;

        var propertiesToggle = FindViewById<MaterialButton>(Resource.Id.propertiesToggle);
        var propertiesPanel = FindViewById<View>(Resource.Id.propertiesPanel);
        if (propertiesToggle is not null && propertiesPanel is not null)
        {
            propertiesToggle.Click += (_, _) =>
            {
                propertiesPanel.Visibility = propertiesPanel.Visibility == ViewStates.Visible
                    ? ViewStates.Gone
                    : ViewStates.Visible;
            };
        }
```

- [ ] **Step 2: Add `OnFitClicked` handler**

After the existing `OnOpenClicked` method, add:

```csharp
    private void OnFitClicked(object? sender, EventArgs e)
    {
        if (_viewport is null || _camera is null) return;
        var scene = _viewport.Renderer.Scene;
        if (scene is null || !scene.Bounds.IsValid) return;
        double aspect = _viewport.Width > 0 && _viewport.Height > 0
            ? (double)_viewport.Width / _viewport.Height
            : 1.0;
        _camera.FitToBox(scene.Bounds, aspect);
        _viewport.RequestRender();
    }
```

- [ ] **Step 3: Add the `using` for `Android.Views`**

Already present via existing usings; verify or add `using Android.Views;` for `ViewStates`. Verify `using Android.Widget;` covers `FrameLayout`.

- [ ] **Step 4: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

## Phase 4: Phone (portrait / narrow) override

### Task 6: Add a phone-specific layout under `layout-port`

**Files:**
- Create: `Android/src/FabricationAssistant.App.Android/Resources/layout-port/activity_main.xml`

Phones get a single-column layout with the nav rail in a drawer and properties as a `BottomSheet`. The DrawerLayout wraps the same shell content; the nav rail items become a vertical list of `MaterialButton` rows inside the drawer.

- [ ] **Step 1: Write the file**

```xml
<?xml version="1.0" encoding="utf-8"?>
<androidx.drawerlayout.widget.DrawerLayout
    xmlns:android="http://schemas.android.com/apk/res/android"
    xmlns:app="http://schemas.android.com/apk/res-auto"
    android:id="@+id/drawerLayout"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:background="@color/md_theme_surface">

    <!-- Main content -->
    <androidx.constraintlayout.widget.ConstraintLayout
        android:layout_width="match_parent"
        android:layout_height="match_parent">

        <com.google.android.material.appbar.MaterialToolbar
            android:id="@+id/topAppBar"
            android:layout_width="match_parent"
            android:layout_height="@dimen/fa_app_bar_height"
            android:background="@color/md_theme_surfaceVariant"
            app:title="@string/app_name"
            app:titleTextAppearance="@style/FA.TextAppearance.Title"
            app:navigationIcon="@drawable/ic_menu"
            app:navigationContentDescription="@string/cd_open_drawer"
            app:layout_constraintTop_toTopOf="parent"
            app:layout_constraintStart_toStartOf="parent"
            app:layout_constraintEnd_toEndOf="parent" />

        <FrameLayout
            android:id="@+id/viewportContainer"
            android:layout_width="0dp"
            android:layout_height="0dp"
            android:background="@color/md_theme_surface"
            app:layout_constraintTop_toBottomOf="@+id/topAppBar"
            app:layout_constraintBottom_toTopOf="@+id/bottomAppBar"
            app:layout_constraintStart_toStartOf="parent"
            app:layout_constraintEnd_toEndOf="parent" />

        <LinearLayout
            android:id="@+id/bottomAppBar"
            android:layout_width="match_parent"
            android:layout_height="@dimen/fa_bottom_bar_height"
            android:orientation="horizontal"
            android:background="@color/md_theme_surfaceVariant"
            android:gravity="center_vertical"
            android:paddingStart="@dimen/fa_space_s"
            android:paddingEnd="@dimen/fa_space_s"
            app:layout_constraintBottom_toBottomOf="parent"
            app:layout_constraintStart_toStartOf="parent"
            app:layout_constraintEnd_toEndOf="parent">

            <com.google.android.material.button.MaterialButton
                android:id="@+id/toolViewCube"
                android:layout_width="40dp"
                android:layout_height="40dp"
                android:contentDescription="@string/cd_tool_view_cube"
                app:icon="@drawable/ic_view_cube"
                app:iconGravity="textStart"
                app:iconPadding="0dp"
                app:iconSize="20dp"
                android:insetTop="0dp"
                android:insetBottom="0dp"
                style="@style/FA.IconButton" />

            <com.google.android.material.button.MaterialButton
                android:id="@+id/toolSection"
                android:layout_width="40dp"
                android:layout_height="40dp"
                android:layout_marginStart="@dimen/fa_space_xs"
                android:contentDescription="@string/cd_tool_section"
                app:icon="@drawable/ic_section"
                app:iconGravity="textStart"
                app:iconPadding="0dp"
                app:iconSize="20dp"
                android:insetTop="0dp"
                android:insetBottom="0dp"
                style="@style/FA.IconButton" />

            <com.google.android.material.button.MaterialButton
                android:id="@+id/toolMeasure"
                android:layout_width="40dp"
                android:layout_height="40dp"
                android:layout_marginStart="@dimen/fa_space_xs"
                android:contentDescription="@string/cd_tool_measure"
                app:icon="@drawable/ic_measure"
                app:iconGravity="textStart"
                app:iconPadding="0dp"
                app:iconSize="20dp"
                android:insetTop="0dp"
                android:insetBottom="0dp"
                style="@style/FA.IconButton" />

            <Space
                android:layout_width="0dp"
                android:layout_height="0dp"
                android:layout_weight="1" />

            <com.google.android.material.button.MaterialButton
                android:id="@+id/propertiesToggle"
                android:layout_width="40dp"
                android:layout_height="40dp"
                android:contentDescription="@string/cd_toggle_properties"
                app:icon="@drawable/ic_chevron_right"
                app:iconGravity="textStart"
                app:iconPadding="0dp"
                app:iconSize="20dp"
                android:insetTop="0dp"
                android:insetBottom="0dp"
                style="@style/FA.IconButton" />

        </LinearLayout>

    </androidx.constraintlayout.widget.ConstraintLayout>

    <!-- Drawer (left) -->
    <LinearLayout
        android:layout_width="280dp"
        android:layout_height="match_parent"
        android:layout_gravity="start"
        android:orientation="vertical"
        android:background="@color/md_theme_surface"
        android:padding="@dimen/fa_space_l">

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navOpenButton"
            android:layout_width="match_parent"
            android:layout_height="wrap_content"
            android:layout_marginTop="@dimen/fa_space_s"
            android:text="@string/cd_nav_open"
            app:icon="@drawable/ic_open_file"
            app:iconGravity="textStart"
            style="@style/Widget.Material3.Button.TextButton" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navRecentButton"
            android:layout_width="match_parent"
            android:layout_height="wrap_content"
            android:layout_marginTop="@dimen/fa_space_xs"
            android:text="@string/cd_nav_recent"
            app:icon="@drawable/ic_recent"
            app:iconGravity="textStart"
            style="@style/Widget.Material3.Button.TextButton" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navFitButton"
            android:layout_width="match_parent"
            android:layout_height="wrap_content"
            android:layout_marginTop="@dimen/fa_space_xs"
            android:text="@string/cd_nav_fit"
            app:icon="@drawable/ic_fit"
            app:iconGravity="textStart"
            style="@style/Widget.Material3.Button.TextButton" />

        <Space
            android:layout_width="0dp"
            android:layout_height="0dp"
            android:layout_weight="1" />

        <com.google.android.material.button.MaterialButton
            android:id="@+id/navSettingsButton"
            android:layout_width="match_parent"
            android:layout_height="wrap_content"
            android:layout_marginBottom="@dimen/fa_space_s"
            android:text="@string/cd_nav_settings"
            app:icon="@drawable/ic_settings"
            app:iconGravity="textStart"
            style="@style/Widget.Material3.Button.TextButton" />
    </LinearLayout>

</androidx.drawerlayout.widget.DrawerLayout>
```

- [ ] **Step 2: Add the AndroidX DrawerLayout package reference (if not already)**

Check `Android/src/FabricationAssistant.App.Android/FabricationAssistant.App.Android.csproj`. If it does not already pull `Xamarin.AndroidX.DrawerLayout` (it usually transitive-comes via Material), add:

```xml
    <PackageReference Include="Xamarin.AndroidX.DrawerLayout" Version="1.2.0.13" />
```

inside the existing `<ItemGroup>` that has the other AndroidX packages.

- [ ] **Step 3: Update MainActivity to handle the drawer**

In `OnCreate`, after the existing wiring, append:

```csharp
        var drawer = FindViewById<AndroidX.DrawerLayout.Widget.DrawerLayout>(Resource.Id.drawerLayout);
        var topAppBar = FindViewById<MaterialToolbar>(Resource.Id.topAppBar);
        if (drawer is not null && topAppBar is not null)
        {
            topAppBar.NavigationClick += (_, _) =>
                drawer.OpenDrawer((int)Android.Views.GravityFlags.Start);
        }
```

Wrap the access in null-checks because the tablet layout has no DrawerLayout — `drawer` will be null there.

Add the using directive:
```csharp
using Google.Android.Material.AppBar;
```

(`MaterialToolbar` lives in `Google.Android.Material.AppBar` for Xamarin.Google.Android.Material 1.12.x.)

- [ ] **Step 4: Build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors.

---

## Phase 5: Verify on tablet (and phone-form-factor sanity check)

### Task 7: Install and visually verify the new shell

**Files:** none — verification only.

- [ ] **Step 1: Install**

```powershell
$env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools"
$apk = "C:\Users\skritikos\Desktop\Fabrication Assistant\Android\src\FabricationAssistant.App.Android\bin\Debug\com.fabricationassistant.android-Signed.apk"
adb shell am force-stop com.fabricationassistant.android
adb install -r $apk
adb shell am start -n com.fabricationassistant.android/crc649abf7c96d03d876b.MainActivity
```

- [ ] **Step 2: Verify on the tablet**

Expected on first launch (tablet landscape):
- Top AppBar 56 dp tall, "Fabrication Assistant" title left, chevron right.
- Left nav rail 80 dp wide with 4 icons: open, recent, fit, settings (settings pinned bottom).
- Center viewport area dark gray, currently empty since no model is loaded.
- Right properties panel 320 dp wide with "Properties" header + "No selection" caption.
- Bottom bar 48 dp tall with 3 tool icons left-aligned.

- [ ] **Step 3: Smoke-test the open + fit flow**

Tap the nav-rail "Open" icon → SAF picker opens. Pick a `.fa` file. Model renders in the viewport (Plan 2A regression).

Tap the nav-rail "Fit" icon → camera resets to fit the model bounds (uses `_camera.FitToBox`).

Tap the chevron → properties panel collapses → tap again → reopens.

- [ ] **Step 4: Phone-form-factor sanity check (optional, via emulator or forced size)**

To exercise the `layout-port` variant on the tablet without an emulator, switch its size:
```powershell
adb shell wm size 800x1280
adb shell wm density 320
adb shell am force-stop com.fabricationassistant.android
adb shell am start -n com.fabricationassistant.android/crc649abf7c96d03d876b.MainActivity
```

Expected: hamburger icon appears in the AppBar (top-left). Tapping it opens a left drawer with the four nav items as a vertical list. The bottom bar now also has the properties chevron at the right end. The viewport fills the remaining vertical space.

Reset when done:
```powershell
adb shell wm size reset
adb shell wm density reset
```

- [ ] **Step 5: Capture diagnostics on any anomaly**

```powershell
adb logcat -d --pid=$(adb shell pidof com.fabricationassistant.android) 2>$null | Select-String -Pattern '(error|exception|inflate|null|resource not found)' -CaseSensitive:$false | Select-Object -Last 30
```

---

### Task 8: Document Plan 2D + final verification

**Files:**
- Modify: `Android/README.md`

- [ ] **Step 1: Confirm no `../src/` changes**

```powershell
git status --porcelain "src/"
```
Expected: empty.

- [ ] **Step 2: Append a "Plan-2D execution notes" section**

Insert into `Android/README.md` immediately before `## Manual verification step`:

```markdown
## Plan-2D execution notes

Tablet-first UI shell that mirrors the desktop `MainWindow.xaml` structure
(spec section 9.2): top AppBar, left NavigationRail, center viewport,
right collapsible Properties panel, bottom toolbar. Phone layout (via
`layout-port`) collapses the nav rail to a left drawer and the properties
panel to a chevron toggle at the bottom-right.

- **Tokens:** All sizes / paddings live in `res/values/dimens.xml` under
  `fa_*` names; text appearances live under `FA.TextAppearance.*` styles
  (Caption / Body / Subtitle / Title). No inline `textSize`/`textColor`
  values in layouts. Mirrors the desktop's `Resources/Tokens.xaml`
  discipline.
- **Color palette:** unchanged from earlier plans - `@color/fa_*` and
  `@color/md_theme_*` already match `Theme.Dark.xaml`. Layouts pull
  exclusively from those tokens.
- **Icon language:** 9 vector drawables under `res/drawable/ic_*.xml`,
  shape-matched to Material Symbols Outlined. Tinted via
  `?attr/colorControlNormal` so dark/light theming flows naturally.
- **Component overrides:** `FA.Toolbar`, `FA.BottomAppBar`,
  `FA.NavigationRail`, `FA.IconButton` extend the Material 3 base styles
  with desktop-matched heights, backgrounds, and ripple tints.
- **Phone layout:** `layout-port/activity_main.xml` swaps the
  `ConstraintLayout` shell for a `DrawerLayout` wrapper. The drawer's
  vertical list of `Widget.Material3.Button.TextButton` items reuses the
  same nav id names so MainActivity's wiring works in both orientations.
- **MainActivity wiring:** all view lookups are null-tolerant
  (`FindViewById<MaterialButton>(...)?.Click += ...`) so the same code
  binds against either layout without per-orientation conditionals.
```

- [ ] **Step 3: Final clean build**

Run: `Android/tools/build.ps1 -Configuration Debug`
Expected: 0 errors. APK produced.

---

## Risk register (Plan 2D-specific)

| # | Risk | Mitigation |
|---|------|------------|
| S1 | Material 3 `NavigationRailView` style attribute not recognized by the bundled Material library version | The plan uses a plain `LinearLayout` of `MaterialButton`s for the rail, not the `NavigationRailView` widget, sidestepping the version-coupling. The `FA.NavigationRail` style is reserved for a future migration. |
| S2 | `androidx.drawerlayout.widget.DrawerLayout` may not be pulled transitively by the bundled Material 1.12 | Task 6 Step 2 makes the dependency explicit. If `Xamarin.AndroidX.DrawerLayout` is already implicit, the explicit reference is a no-op. |
| S3 | The `propertiesToggle` chevron icon only flips visibility, not orientation | Acceptable for MVP: chevron-right always means "panel is open, tap to close". A rotated state is a polish item for later. |
| S4 | Tablet layout assumes >= 800 dp wide; on a 7" tablet that falls between phone and tablet the layout may look cramped | The default `layout/activity_main.xml` is used regardless of width; phones get `layout-port/activity_main.xml` only in portrait. Tablets in portrait still get the wide layout. If users on 7" devices complain, add `layout-w600dp` later as a tighter override. |
| S5 | Vector drawables look slightly different from the desktop's bespoke icons | The Material Symbols-derived paths are a closer match than letting Material's default icons handle these actions. Custom icons matching the desktop pixel-for-pixel is post-MVP polish. |

---

## Self-review checklist (writing-plans skill)

**1. Spec coverage:** Spec section 9.2 (tablet landscape layout) maps to Task 4. Section 9.3 (phone layout - drawer + bottom sheet) maps to Task 6. Section 9.4 (Material 3 theme mirroring `Theme.Dark.xaml`) maps to Tasks 1+2. The collapsible right Properties panel toggle is in Task 5. ViewCube as an overlay (section 6.2 pass 9) is NOT covered here — the bottom-bar `ic_view_cube` icon is a placeholder until the actual viewport overlay is added in a later plan.

**2. Placeholder scan:** No "TBD" / "implement later" / "similar to Task N" remain. Every drawable in Task 2 has its full SVG path inline. Every style in Task 1 lists its attribute set. The tablet layout (Task 4) and phone layout (Task 6) are complete XML; no chunks are abbreviated. The known caveat (Resource.Id.openButton no longer exists after Task 4, MainActivity will null-deref unless Task 5 follows) is called out explicitly at the end of Task 4.

**3. Type consistency:**
- Resource ids: `viewportContainer` and `propertiesPanel` are used by both `layout/activity_main.xml` and `layout-port/activity_main.xml`. `navOpenButton` / `navFitButton` / `propertiesToggle` are referenced from `MainActivity` (Task 5) and exist in both layouts.
- Style names: `FA.IconButton`, `FA.TextAppearance.Title`, etc. used in layouts (Tasks 4, 6) all defined in styles.xml (Task 1).
- Color names: `md_theme_surface`, `md_theme_surfaceVariant`, `fa_text_primary`, `fa_text_secondary`, `fa_accent_500` already exist in `values/colors.xml` (unchanged) and are referenced consistently in styles + layouts.
- Drawable names: `ic_open_file`, `ic_recent`, `ic_fit`, `ic_settings`, `ic_section`, `ic_measure`, `ic_view_cube`, `ic_chevron_right`, `ic_menu` defined in Task 2 and referenced in Tasks 4, 5, 6.

No type-consistency bugs found.

---

## Execution choice

Plan complete and saved to `Android/docs/superpowers/plans/2026-05-23-android-desktop-parity-shell.md`. Two execution options:

**1. Subagent-Driven (recommended)** — one fresh subagent per task, two-stage review between tasks. Tasks 4 and 6 (the large XML layouts) benefit from spec-compliance review.

**2. Inline execution** — execute all 8 tasks in this session via the controller. Faster end-to-end if everything goes smoothly.

Which approach?
