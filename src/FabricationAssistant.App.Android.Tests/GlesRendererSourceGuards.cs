using System.Runtime.CompilerServices;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

// S25-1: these are SOURCE GUARDS, not behavioural tests. The GLES renderer cannot
// load in the net8.0 host runner, so each fact asserts on the *source text* of a
// renderer/Android file (a specific line or shader fragment is present/absent). They
// catch regressions of specific fixes but execute no GL and can break on a benign
// refactor. Real shader compilation (COMPILE_STATUS) is covered on-device by
// tools/run-render-queue-test.ps1, which drives rendering and asserts no shader failure.
public sealed class GlesRendererSourceGuards
{
    [Fact]
    public void MeshShader_FlipsBackFaceNormalsOutsideSectionMode()
    {
        string shader = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\Shaders\mesh.gles.frag"));

        Assert.Contains("if (!gl_FrontFacing) normal = -normal;", shader);
        Assert.DoesNotContain("uSectionPlaneCount > 0 && !gl_FrontFacing", shader);
    }

    [Fact]
    public void SplitMaterialPrimitiveMeshes_SelectTheirParentBody()
    {
        string gpuMesh = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GpuMesh.cs"));
        Assert.Contains("public int SelectableNodeId { get; set; } = -1;", gpuMesh);

        string uploader = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\SceneUploader.cs"));
        Assert.Contains("gpu.SelectableNodeId = ResolveSelectableNodeId(nodesById, node);", uploader);

        string resolver = ExtractMethod(uploader, "private static int ResolveSelectableNodeId");
        Assert.Contains("node.NodeType == SceneNodeType.Shape", resolver);
        Assert.Contains("parent.NodeType == SceneNodeType.Part", resolver);
        Assert.Contains("parent.MeshId is null", resolver);
        Assert.Contains("return parent.Id;", resolver);

        string scene = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GpuScene.cs"));
        // S13-1: the selectable-node resolution now lives in the lookup builder.
        string selectableLookup = ExtractMethod(scene, "private void RebuildNodeMeshLookups");
        Assert.Contains("mesh.SelectableNodeId >= 0", selectableLookup);
        Assert.Contains("? mesh.SelectableNodeId", selectableLookup);
        Assert.Contains(": mesh.SourceNodeId", selectableLookup);

        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string selectionMethod = ExtractMethod(mainActivity, "private void OnPickResult");
        Assert.Contains("TryGetSourceNodeIdForMeshIndex", selectionMethod);
        Assert.Contains("AndroidModelSelectionResolver.ResolveNodeIds", selectionMethod);
        Assert.Contains("_selectionMode", selectionMethod);
    }

    [Fact]
    public void ModelExplorerHidesSplitMaterialPrimitiveShapes()
    {
        string explorer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidModelExplorerPanel.cs"));

        string hideMethod = ExtractMethod(explorer, "private static bool ShouldHideFromExplorer");
        Assert.Contains("AndroidModelSelectionResolver.ShouldCollapseIntoPresentedOwner(node, visibleOwner)", hideMethod);

        string resolver = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidModelSelectionResolver.cs"));
        string splitMethod = ExtractMethod(resolver, "private static bool IsSplitMaterialPrimitiveShape");
        Assert.Contains("node.NodeType == SceneNodeType.Shape", splitMethod);
        Assert.Contains("visibleOwner.NodeType == SceneNodeType.Part", splitMethod);
        Assert.Contains("visibleOwner.MeshId is null", splitMethod);
        Assert.Contains("node.MeshId.HasValue", splitMethod);
    }

    [Fact]
    public void SectionCapContourMask_UsesContourGeometryWithDepthOnlyOnCapPass()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int methodStart = renderer.IndexOf("private void RenderSectionCaps", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "RenderSectionCaps was not found.");

        int stencilDraw = renderer.IndexOf(
            "_sectionOverlay.RenderCapMaskTriangles(view, projection, geometry.TriangleVertices);",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(stencilDraw > methodStart, "Section cap contour draw was not found.");

        int capPassStart = renderer.IndexOf(
            "_gl.ColorMask(true, true, true, true);",
            stencilDraw,
            StringComparison.Ordinal);
        Assert.True(capPassStart > stencilDraw, "Section cap color pass was not found.");

        string stencilPass = renderer[methodStart..capPassStart];
        Assert.Contains("_gl.Disable(EnableCap.DepthTest);", stencilPass);
        Assert.DoesNotContain("_gl.Enable(EnableCap.DepthTest);", stencilPass);

        string capPass = renderer[capPassStart..];
        Assert.Contains("_gl.Enable(EnableCap.DepthTest);", capPass);
        Assert.Contains("_gl.DepthFunc(DepthFunction.Lequal);", capPass);
    }

    [Fact]
    public void SectionCapContourMask_DoesNotUseSceneRedrawPath()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int methodStart = renderer.IndexOf("private void RenderSectionCaps", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "RenderSectionCaps was not found.");
        string method = renderer[methodStart..renderer.IndexOf("private void ApplySectionOverlaySettings", methodStart, StringComparison.Ordinal)];

        Assert.Contains("GetSectionCapGeometries", method);
    }

    [Fact]
    public void SectionCaps_WriteDepthAndCurves_RespectDepth()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesSectionOverlay.cs"));

        Assert.Contains("depthMask: true", overlay);
        Assert.Contains("DrawRibbonPositions(view, projection, lineVertices, color, depthTest: true", overlay);
        Assert.Contains("_gl.DepthMask(depthMask);", overlay);
    }

    [Fact]
    public void SectionCapSourceFilter_IncludesTransparentVisibleMeshes()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        string filter = ExtractMethod(renderer, "private bool ShouldRenderSectionCapSourceMesh");
        Assert.Contains("!mesh.Visible", filter);
        Assert.Contains("IsXrayBackgroundMesh(mesh)", filter);
        Assert.Contains("return true;", filter);
        Assert.DoesNotContain("GetEffectiveMeshAlpha", filter);
        Assert.DoesNotContain("OpaqueAlphaThreshold", filter);
    }

    [Fact]
    public void EdgeThicknessSliders_AreSettingsDriven()
    {
        string settings = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AppSettings.cs"));
        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesSectionOverlay.cs"));

        Assert.Contains("public static float SectionEdgeWidth", settings);
        Assert.Contains("\"Normal edge thickness\", 0.75f, 4f", preferences);
        Assert.Contains("\"Section edge thickness\", 0.5f, 8f", preferences);
        Assert.Contains("renderer.SectionEdgeWidth = AppSettings.SectionEdgeWidth;", mainActivity);
        Assert.Contains(": System.Math.Clamp(a.EdgeWidth, 0.05f, 4.0f)", renderer);
        Assert.Contains("_sectionOverlay.EdgeWidth = SectionEdgeWidth;", renderer);
        Assert.Contains("new GlesSectionOverlay(_gl, measureVs, measureFs, edgeVs, edgeFs)", renderer);
        Assert.Contains("uLineWidthPixels", overlay);
        Assert.Contains("DrawElementsInstanced", overlay);
    }

    [Fact]
    public void FaceHighlightOverlay_IsOccludedByGeometry()
    {
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesFaceHighlightOverlay.cs"));
        string render = ExtractMethod(overlay, "public void Render");

        // The translucent face fill is depth-tested against the scene so it is
        // hidden behind bodies in front of the face, rather than drawn
        // unconditionally on top (which made it visible through geometry).
        Assert.Contains("_gl.Enable(EnableCap.DepthTest)", render);
        Assert.DoesNotContain("_gl.Disable(EnableCap.DepthTest)", render);
        Assert.Contains("DepthFunction.Lequal", render);

        // A polygon offset toward the camera keeps the fill from z-fighting with
        // the coplanar mesh face it tints, while still losing to closer geometry.
        Assert.Contains("EnableCap.PolygonOffsetFill", render);

        // Still a translucent overlay: it must not write depth.
        Assert.Contains("_gl.DepthMask(false)", render);
    }

    [Fact]
    public void BoundingBoxSelectionHover_HighlightsBodyUnderPointer()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        // The bounding-box (boundary) tool is body-selection-driven: a tap picks
        // the body under the pointer (HandleBoundingBoxSelectionTap), so the S Pen
        // hover must body-pick too. The fix adds a _measureBoundingBoxAwaitingSelection
        // branch BEFORE the generic "_measure is { IsActive: true }" branch, which
        // would otherwise clear the highlight and run feature-snap hover (a no-op for
        // the body-driven BoundingBox mode).
        string hover = ExtractMethod(mainActivity, "private bool OnViewportHover");
        int bboxBranch = hover.IndexOf("_measureBoundingBoxAwaitingSelection", StringComparison.Ordinal);
        int measureBranch = hover.IndexOf("_measure is { IsActive: true }", StringComparison.Ordinal);
        Assert.True(bboxBranch >= 0, "OnViewportHover is missing the bounding-box selection hover branch.");
        Assert.True(measureBranch >= 0, "OnViewportHover is missing the measure-active hover branch.");
        Assert.True(
            bboxBranch < measureBranch,
            "The bounding-box hover branch must precede the feature-snap measure branch.");

        // The hover-pick result must be applied (not force-cleared) while the
        // bounding-box selection is awaiting a body, so the body highlight survives.
        string hoverResult = ExtractMethod(mainActivity, "private void OnHoverPickResult");
        Assert.Contains("_measureBoundingBoxAwaitingSelection", hoverResult);
    }

    [Fact]
    public void Slice24_DeadStringsRemovedAndSwitchLabelsDecoupled()
    {
        string strings = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Resources\values\strings.xml"));
        string layout = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Resources\layout\activity_main.xml"));

        // S24-L1: the two unreferenced strings are deleted.
        Assert.DoesNotContain("name=\"open_button\"", strings);
        Assert.DoesNotContain("name=\"cd_open_drawer\"", strings);

        // S24-M3: each toolbar switch now has a dedicated visible label string...
        Assert.Contains("name=\"label_section_fill\"", strings);
        Assert.Contains("name=\"label_section_edges\"", strings);
        Assert.Contains("name=\"label_section_curves\"", strings);
        Assert.Contains("name=\"label_section_caps\"", strings);
        Assert.Contains("name=\"label_measure_snap_endpoint\"", strings);
        Assert.Contains("name=\"label_measure_snap_midpoint\"", strings);

        // ...the visible text points at the label, and the cd_* string is the
        // separate contentDescription - so no switch reuses cd_* as android:text.
        Assert.DoesNotContain("android:text=\"@string/cd_", layout);
        Assert.Contains("android:contentDescription=\"@string/cd_section_fill\"", layout);
        Assert.Contains("android:text=\"@string/label_section_fill\"", layout);
    }

    [Fact]
    public void Slice23_StorageAndLogHardening()
    {
        string recent = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\RecentFilesStore.cs"));
        // S23-1: probing runs outside the Gate lock.
        string load = ExtractMethod(recent, "public static IReadOnlyList<RecentFileEntry> Load");
        Assert.Contains("probe OUTSIDE the lock", load);
        // S23-14 / S23-15: dead members are gone.
        Assert.DoesNotContain("private static void Save(", recent);
        Assert.DoesNotContain("TryTakePersistableReadPermission", recent);

        // S23-10: the crash logger skips the append when truncation fails.
        string crash = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidCrashLogger.cs"));
        string write = ExtractMethod(crash, "private static void WriteToFile");
        Assert.Contains("must not grow the log unbounded", write);

        // S23-11: the logcat read is cancellable.
        string feed = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidLogcatFeed.cs"));
        Assert.Contains("reader.ReadLineAsync(token)", feed);
    }

    [Fact]
    public void Slice22_CleartextGatedToLanAndErrorBodyCapped()
    {
        string client = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\CloudApiClient.cs"));

        // S22-1: http:// is rejected for non-private/non-loopback (internet) hosts.
        string normalize = ExtractMethod(client, "public static string NormalizeServerUrl");
        Assert.Contains("uri.Scheme == Uri.UriSchemeHttp", normalize);
        Assert.Contains("IsPrivateOrLoopbackHost(uri.Host)", normalize);

        // S22-1: the blunt global cleartext flag is gone from the manifest.
        string manifest = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Properties\AndroidManifest.xml"));
        Assert.DoesNotContain("usesCleartextTraffic", manifest);

        // S22-2b: the error-body read is capped and cancellation-aware.
        string ensure = ExtractMethod(client, "private static async Task EnsureSuccessAsync");
        Assert.Contains("CancellationToken ct", ensure);
        Assert.Contains("ReadCappedAsync", ensure);
        Assert.DoesNotContain("response.Content.ReadAsStringAsync()", ensure);
    }

    [Fact]
    public void Slice20_SilhouetteOverlayOnlyInShadedWithEdges()
    {
        // S20-F10: the screen-space silhouette runs only in ShadedWithEdges (desktop
        // parity), not in plain Shaded; the old Clay/Wireframe exclusion is replaced.
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int start = renderer.IndexOf("bool silhouetteOverlayActive = normalDepthRanThisFrame", StringComparison.Ordinal);
        Assert.True(start >= 0, "silhouetteOverlayActive assignment was not found.");
        int end = renderer.IndexOf("if (silhouetteOverlayActive)", start, StringComparison.Ordinal);
        Assert.True(end > start, "silhouetteOverlayActive assignment end was not found.");
        string assignment = renderer[start..end];

        Assert.Contains("a.Mode == RenderMode.ShadedWithEdges", assignment);
        Assert.DoesNotContain("a.Mode != RenderMode.Clay", assignment);
    }

    [Fact]
    public void Slice19_HoverClearedOnNavStartAndOutlineIntentDocumented()
    {
        // S19-F7: starting a camera gesture clears any (possibly stuck) hover.
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string setNav = ExtractMethod(mainActivity, "private void SetInteractiveNavigationActive");
        Assert.Contains("SetHoveredMesh(0)", setNav);

        // S7/19-F4: the outline-through-occluders behavior is documented as intentional.
        string outline = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesOutlineRenderer.cs"));
        Assert.Contains("S7/19-F4", outline);
        Assert.Contains("through occluders", outline);
    }

    [Fact]
    public void Slice17_BvhStackBoundedAndMeasureOverlayUnclippedByDesign()
    {
        // S17-4: the BVH raycast worklist is sized from the tree height with a heap
        // fallback, so a deep/unbalanced tree can't overflow the inline stackalloc.
        string bvh = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Measurement\AndroidMeshRaycastAcceleration.cs"));
        Assert.Contains("ComputeMaxTraversalStackDepth", bvh);
        string tryRaycast = ExtractMethod(bvh, "public bool TryRaycast");
        Assert.Contains("_maxTraversalStackDepth <= InlineTraversalStackCapacity", tryRaycast);
        Assert.Contains("new int[_maxTraversalStackDepth]", tryRaycast);
        Assert.DoesNotContain("stackalloc int[128]", tryRaycast);

        // S17-1: the measurement overlay is intentionally not section-clipped; the
        // decision is documented so it is not re-flagged as a missing clip.
        string overlay = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesMeasurementOverlay.cs"));
        Assert.Contains("intentionally NOT section-clipped", overlay);
    }

    [Fact]
    public void Slice16_SectionPlacementAndStaleCaps()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        // S16-2: Custom section placement no longer suppresses camera navigation.
        // (ShouldSuppressNavigationGestures is expression-bodied, so slice to the ';'.)
        int suppressStart = mainActivity.IndexOf("private bool ShouldSuppressNavigationGestures()", StringComparison.Ordinal);
        Assert.True(suppressStart >= 0, "ShouldSuppressNavigationGestures was not found.");
        // Slice from the '=>' (past the doc comment) to the terminating ';'.
        int suppressArrow = mainActivity.IndexOf("=>", suppressStart, StringComparison.Ordinal);
        int suppressEnd = mainActivity.IndexOf(';', suppressArrow);
        Assert.True(suppressArrow > suppressStart && suppressEnd > suppressArrow, "ShouldSuppressNavigationGestures body was not found.");
        string suppress = mainActivity[suppressArrow..suppressEnd];
        Assert.DoesNotContain("SectionSubMode.Custom", suppress);
        Assert.Contains("AndroidModalTool.ZoomWindow", suppress);

        // S16-5: a stale section-cap build (fewer geometries than visual planes) is
        // surfaced and re-rendered instead of silently dropping trailing caps.
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));
        string caps = ExtractMethod(renderer, "private void RenderSectionCaps");
        Assert.Contains("capGeometries.Length < SectionVisualPlanes.Count", caps);
        Assert.Contains("Section caps stale", caps);
    }

    [Fact]
    public void Slice15_BomRowsWrapHeightForLargeFontScale()
    {
        // S15-F10: BOM rows must not pin a fixed 24dp height around sp-scaled text.
        string bom = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidBomPanel.cs"));
        string getView = ExtractMethod(bom, "public override View GetView");
        // Row height wraps content with a 24dp floor instead of a fixed 24dp box.
        Assert.Contains("new AbsListView.LayoutParams(", getView);
        Assert.Contains("ViewGroup.LayoutParams.WrapContent", getView);
        Assert.Contains("SetMinimumHeight(Dp(_ctx, 24))", getView);
    }

    [Fact]
    public void Slice14_TreeExplorerAndBomFixes()
    {
        string explorer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidModelExplorerPanel.cs"));

        // S14-2: expansion state is only carried forward for the same scene instance.
        string setScene = ExtractMethod(explorer, "public void SetScene");
        Assert.Contains("ReferenceEquals(_scene, scene)", setScene);
        Assert.Contains("? _tree?.CaptureExpansionState() : null", setScene);

        // S14-5: duplicate packing buckets children in one pass (no inner per-group scan).
        string pack = ExtractMethod(explorer, "private static IReadOnlyList<AndroidModelExplorerNode> PackDuplicateChildren");
        Assert.Contains("Dictionary<string, List<AndroidModelExplorerNode>>", pack);
        Assert.DoesNotContain("children.Where(", pack);

        // S14-F6: the model-explorer row recycles convertView through a holder.
        string getView = ExtractMethod(explorer, "public override View GetView");
        Assert.Contains("convertView?.Tag as RowHolder", getView);

        // S14-F8: the focused row prefers a real (non-negative) presented id.
        string setSelection = ExtractMethod(explorer, "public void SetSelection");
        Assert.Contains("_highlightedPresentedIds.Where(id => id >= 0).Min()", setSelection);

        // S14-F9: the always-true _visibleRows.Contains guard is removed.
        string bom = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidBomPanel.cs"));
        string click = ExtractMethod(bom, "private void OnListItemClick");
        Assert.DoesNotContain("_visibleRows.Contains(row)", click);
    }

    [Fact]
    public void Slice13_SelectionLookupsArePrebuiltAndPicksGuarded()
    {
        // S13-1: GpuScene resolves selection via prebuilt maps (built in Load),
        // not per-call linear _meshes scans.
        string gpuScene = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GpuScene.cs"));
        Assert.Contains("RebuildNodeMeshLookups(meshes);", gpuScene);
        string forNode = ExtractMethod(gpuScene, "public bool TryGetMeshIndexForSourceNodeId");
        Assert.Contains("_meshIndexBySourceNodeId.TryGetValue", forNode);
        Assert.DoesNotContain("foreach", forNode);

        // S15-1: model-body picks are issued through a load-version guard so a
        // result that arrives after a document swap is ignored.
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string pickHelper = ExtractMethod(mainActivity, "private void PickModelBodyAsync");
        Assert.Contains("issuedLoadVersion != Volatile.Read(ref _loadVersion)", pickHelper);
        Assert.Contains("PickModelBodyAsync(px, py, pickReason", mainActivity);
        Assert.Contains("PickModelBodyAsync(px, py, \"context\"", mainActivity);
        Assert.Contains("PickModelBodyAsync(px, py, \"body-move-select\"", mainActivity);
    }

    [Fact]
    public void MultiMeshSelection_FillTintsEverySelectedMesh()
    {
        // S13-F7 (verified false positive): DrawSurfaceMesh sets uSelectedMeshIndex
        // per mesh from the full selection set, so EVERY selected body is fill-
        // tinted, not just the first. This locks in that per-mesh behavior.
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));
        string draw = ExtractMethod(renderer, "private void DrawSurfaceMesh");
        Assert.Contains("IsSelectedMesh(mesh.MeshIndex) ? mesh.MeshIndex : 0", draw);
        string isSelected = ExtractMethod(renderer, "private bool IsSelectedMesh");
        Assert.Contains("_selectedMeshIndexLookup.Contains(meshIndex)", isSelected);
    }

    [Fact]
    public void Slice12_NavigationPivotResetAndDividerDragRobustness()
    {
        // S12-F2: the navigation pivot is reset when a scene is attached.
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string attach = ExtractMethod(mainActivity, "private void AttachRuntimeScene");
        Assert.Contains("_interaction?.ResetNavigationPivot();", attach);

        // S12-F3: the divider drag no longer adopts a transient second (stylus)
        // pointer, and restores to a remaining pointer when the active one lifts.
        string resize = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\HorizontalResizeTouchListener.cs"));
        Assert.DoesNotContain("IsPointerStylusOrEraser", resize);
        Assert.Contains("TryFindOtherPointer", resize);
    }

    [Fact]
    public void ZoomWindowMarquee_KeepsToolOnSecondFingerOrbitEnd()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        string handler = ExtractMethod(mainActivity, "private void OnGestureForToolbarTools");

        // S11-1: a second-finger OrbitEnd (Orbit->PanZoom hand-off) must cancel the
        // marquee and keep the tool, not commit + exit.
        int guardIndex = handler.IndexOf("if (ev.IsMultiTouchTransition)", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "OrbitEnd handling must guard on ev.IsMultiTouchTransition.");

        int resetIndex = handler.IndexOf("ResetZoomWindowOverlay();", guardIndex, StringComparison.Ordinal);
        int commitIndex = handler.IndexOf("ApplyZoomWindowSelection();", StringComparison.Ordinal);
        Assert.True(resetIndex > guardIndex, "The transition guard should cancel the marquee via ResetZoomWindowOverlay.");
        Assert.True(commitIndex > resetIndex, "The commit path must be guarded out for the second-finger transition.");
    }

    [Fact]
    public void PreferencesControls_ApplySlice10Fixes()
    {
        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));

        // S10-1: dragging grid spacing clears the Automatic-grid switch.
        Assert.Contains("var autoGridSwitch = AddSwitch(ctx, helpers, \"Automatic grid spacing\"", preferences);
        Assert.Contains("autoGridSwitch.Checked = false;", preferences);

        // S10-F2: grid-spacing slider spans the full clamp (no longer pinned at 1000).
        Assert.Contains("\"Grid spacing (mm)\", 0.001f, 1_000_000f", preferences);

        // S10-2: numeric fields commit on Enter/focus loss, not on every keystroke.
        string addFloatField = ExtractMethod(preferences, "private EditText AddFloatField");
        Assert.Contains("commitOnChange: false", addFloatField);
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(input, Commit)", addFloatField);
        Assert.Contains("if (!e.HasFocus)", addFloatField);

        // S10-F4: re-enabling edges re-syncs the edge-width slider to the saved value.
        Assert.Contains("edgeWidthSlider?.SetValue(AppSettings.EdgeWidth)", preferences);

        // S10-F7: edge-width slider min is the durable visible floor (0.75).
        Assert.Contains("\"Normal edge thickness\", 0.75f, 4f", preferences);

        // S10-F8: weld tolerance (two decades) uses a logarithmic slider.
        Assert.Contains("CadEdgeWeldToleranceScale = v, logarithmic: true", preferences);

        // S10-F9: section gizmo scale lives under Section Tools, not Navigation.
        Assert.Contains("AddFloatSlider(ctx, sections, \"Section gizmo scale\"", preferences);
        Assert.DoesNotContain("AddFloatSlider(ctx, nav, \"Section gizmo scale\"", preferences);

        // AddFloatSlider gained logarithmic mapping + a refresh handle.
        string addFloatSlider = ExtractMethod(preferences, "private FloatSliderControl AddFloatSlider");
        Assert.Contains("bool logarithmic", addFloatSlider);
        Assert.Contains("max / (double)min", addFloatSlider);
    }

    [Fact]
    public void MouseControls_AreSettingsDriven()
    {
        string settings = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AppSettings.cs"));
        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        string interaction = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Input.Gestures.Android\ViewportInteractionAdapter.cs"));
        string pointerSource = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Input.Gestures.Android\AndroidPointerSource.cs"));

        Assert.Contains("public static float MouseOrbitSpeed", settings);
        Assert.Contains("public static float MousePanSpeed", settings);
        Assert.Contains("public static float MouseWheelZoomSpeed", settings);
        Assert.Contains("public static float MouseDragThresholdDip", settings);
        Assert.Contains("public static bool MouseHoverEnabled", settings);
        Assert.Contains("public static bool MouseInvertWheelZoom", settings);
        Assert.Contains("var mouse = AddSection(ctx, root, \"Mouse\"", preferences);
        Assert.Contains("\"Right-drag orbit speed\"", preferences);
        Assert.Contains("\"Middle-drag pan speed\"", preferences);
        Assert.Contains("_interaction.MouseOrbitSpeedMultiplier = AppSettings.MouseOrbitSpeed;", mainActivity);
        Assert.Contains("_interaction.MousePanSpeedMultiplier = AppSettings.MousePanSpeed;", mainActivity);
        Assert.Contains("_interaction.MouseWheelZoomSpeedMultiplier = AppSettings.MouseWheelZoomSpeed;", mainActivity);
        Assert.Contains("_pointerSource.MouseClickDragThresholdDip = AppSettings.MouseDragThresholdDip;", mainActivity);
        Assert.Contains("isMouseHover && !AppSettings.MouseHoverEnabled", mainActivity);
        Assert.Contains("MouseOrbitSpeedMultiplier", interaction);
        Assert.Contains("MousePanSpeedMultiplier", interaction);
        Assert.Contains("MouseWheelZoomSpeedMultiplier", interaction);
        Assert.Contains("MouseClickDragThresholdDip", pointerSource);
    }

    [Fact]
    public void SectionCapBuilds_AreNotCanceledByCameraInteraction()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int propertyStart = renderer.IndexOf("public bool InteractiveNavigationActive", StringComparison.Ordinal);
        Assert.True(propertyStart >= 0, "InteractiveNavigationActive was not found.");
        int propertyEnd = renderer.IndexOf("/// <summary>", propertyStart + 1, StringComparison.Ordinal);
        Assert.True(propertyEnd > propertyStart, "InteractiveNavigationActive property end was not found.");
        string property = renderer[propertyStart..propertyEnd];

        Assert.DoesNotContain("CancelPendingSectionCapGeometryBuilds", property);

        int interactionBranch = renderer.IndexOf("if (InteractiveNavigationActive)", renderer.IndexOf("GetSectionCapGeometries", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(interactionBranch >= 0, "Section cap interaction branch was not found.");
        int branchEnd = renderer.IndexOf("if (TryStartBackgroundSectionCapGeometryBuild", interactionBranch, StringComparison.Ordinal);
        Assert.True(branchEnd > interactionBranch, "Section cap interaction branch end was not found.");
        string branch = renderer[interactionBranch..branchEnd];

        Assert.Contains("Cap build blocked by camera interaction", branch);
        Assert.DoesNotContain("CancelPendingSectionCapGeometryBuilds", branch);
    }

    [Fact]
    public void ScreenSpaceSilhouetteOverlay_IsSkippedDuringSectionClipping()
    {
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        int silhouetteStart = renderer.IndexOf("bool silhouetteOverlayActive = normalDepthRanThisFrame", StringComparison.Ordinal);
        Assert.True(silhouetteStart >= 0, "silhouetteOverlayActive assignment was not found.");
        int silhouetteEnd = renderer.IndexOf("if (silhouetteOverlayActive)", silhouetteStart, StringComparison.Ordinal);
        Assert.True(silhouetteEnd > silhouetteStart, "silhouetteOverlayActive assignment end was not found.");
        string assignment = renderer[silhouetteStart..silhouetteEnd];

        Assert.Contains("SectionPlanes.Count == 0", assignment);
    }

    [Fact]
    public void AndroidRenderTargets_Require32BitDepthWhileBackbufferUsesCompatibleDepth()
    {
        string msaa = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\MsaaSceneFramebuffer.cs"));
        Assert.Contains("PreferredDepthStencilFormat = InternalFormat.Depth32fStencil8", msaa);
        Assert.DoesNotContain("Depth24Stencil8", msaa);
        Assert.DoesNotContain("FallbackDepthStencilFormat", msaa);
        Assert.Contains("SampleFallbackOrder(clamped)", msaa);
        Assert.Contains("int[] candidates = [clampedSamples, 8, 4, 2, 1];", msaa);
        Assert.Contains("!result.Contains(candidate)", msaa);
        Assert.Contains("TryAllocate(width, height, actualSamples, PreferredDepthStencilFormat", msaa);
        Assert.Contains("Scene framebuffer depth=", msaa);
        Assert.DoesNotContain("LogDepthFallback", msaa);

        string normalDepth = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesNormalDepthRenderer.cs"));
        Assert.Contains("PreferredDepthFormat = InternalFormat.DepthComponent32f", normalDepth);
        Assert.DoesNotContain("DepthComponent24", normalDepth);
        Assert.DoesNotContain("FallbackDepthFormat", normalDepth);
        Assert.Contains("PixelType.Float", normalDepth);
        Assert.DoesNotContain("PixelType.UnsignedInt", normalDepth);
        Assert.Contains("Normal/depth framebuffer depth=", normalDepth);
        Assert.DoesNotContain("LogDepthFallback", normalDepth);

        string pick = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesPickRenderer.cs"));
        Assert.Contains("PreferredDepthFormat = InternalFormat.DepthComponent32f", pick);
        Assert.DoesNotContain("DepthComponent24", pick);
        Assert.DoesNotContain("FallbackDepthFormat", pick);
        Assert.Contains("TryAllocateFramebuffer(width, height, PreferredDepthFormat", pick);
        Assert.Contains("Pick framebuffer depth=", pick);
        Assert.DoesNotContain("LogDepthFallback", pick);

        string chooser = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Views\MultisampleConfigChooser.cs"));
        Assert.Contains("BackbufferDepthSize = 24", chooser);
        Assert.Contains("required D32FS8 offscreen FBO", chooser);
        Assert.Contains("IEGL10.EglDepthSize, BackbufferDepthSize", chooser);
        Assert.Contains("GetConfigAttrib(egl, display, config, IEGL10.EglDepthSize) >= BackbufferDepthSize", chooser);

        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));
        Assert.DoesNotContain("DepthBits < 32", renderer);
        Assert.Contains("depth remains D32FS8", renderer);
    }

    [Fact]
    public void MsaaAllocationFailure_TrackedByDedicatedFlagNotZeroSampleSentinel()
    {
        // S6-1: the failure guard must not key on _failedMsaaSamples == 0, because
        // 0 is also a legitimate "MSAA Off" request. A dedicated bool distinguishes
        // "no failure recorded" from "a 0-sample (MSAA Off) allocation that failed".
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        Assert.Contains("private bool _msaaAllocationFailed;", renderer);

        string prepare = ExtractMethod(renderer, "private bool TryPrepareMsaaFramebuffer");
        // Guard gates on the explicit failure flag (not on the 0 sentinel).
        Assert.Contains("if (_msaaAllocationFailed", prepare);
        // Success clears the flag; failure records it.
        Assert.Contains("_msaaAllocationFailed = false;", prepare);
        Assert.Contains("_msaaAllocationFailed = true;", prepare);

        // A resolve failure that destroys the FBO is also a recorded failure.
        string resolve = ExtractMethod(renderer, "private bool TryResolveMsaaFramebuffer");
        Assert.Contains("_msaaAllocationFailed = true;", resolve);

        // The flag is cleared both on the success path and when the offscreen FBO
        // is (re)created on context init, so it must be set false in >= 2 places.
        int clearedCount = renderer.Split("_msaaAllocationFailed = false;").Length - 1;
        Assert.True(clearedCount >= 2,
            $"Expected _msaaAllocationFailed cleared on success and on init, found {clearedCount}.");
    }

    [Fact]
    public void MsaaResolveFallback_IsDocumentedAsAcceptedOneFrameSceneRedraw()
    {
        // S6-F3: when the MSAA resolve blit fails the loop re-renders the whole
        // scene directly to FBO 0 for that frame. This is an accepted one-frame
        // cost and must be documented so it is not mistaken for a bug.
        string renderer = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.Rendering.Gles\GlesViewportRenderer.cs"));

        Assert.Contains("duplicate scene draw that frame", renderer);
    }

    [Fact]
    public void RenderAndMeasureBusyChips_ShareOneViewBuilder()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        // The chip view construction is centralised in one helper.
        string helper = ExtractMethod(mainActivity, "private FrameLayout CreateBusyChip");
        Assert.Contains("new ProgressBar(this) { Indeterminate = true }", helper);
        Assert.Contains("GravityFlags.Bottom | GravityFlags.CenterHorizontal", helper);
        Assert.Contains("return overlay;", helper);

        // The render overlay is built through the shared helper (no inline views).
        string renderOverlay = ExtractMethod(mainActivity, "private void CreateRenderBusyOverlay");
        Assert.Contains("CreateBusyChip(container, \"Updating render...\", out _renderBusyDetail)", renderOverlay);
        Assert.DoesNotContain("new ProgressBar", renderOverlay);
    }

    [Fact]
    public void MeasureBusyChip_IsCreatedDelayedAndCleanedUp()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        // Built through the shared helper next to the render chip.
        string createMeasure = ExtractMethod(mainActivity, "private void CreateMeasureBusyOverlay");
        Assert.Contains("CreateBusyChip(container, \"Computing bounding box...\", out _measureBusyDetail)", createMeasure);
        Assert.Contains("CreateMeasureBusyOverlay(container);", mainActivity);

        // 180 ms anti-flicker gate, guarded by the version token.
        Assert.Contains("private const int MeasureBusyShowDelayMs = 180;", mainActivity);
        string delayed = ExtractMethod(mainActivity, "private async Task ShowMeasureBusyAfterDelayAsync");
        Assert.Contains("await Task.Delay(MeasureBusyShowDelayMs)", delayed);
        Assert.Contains("token != Volatile.Read(ref _measureBusyVersion)", delayed);

        // Hide bumps the version (cancels a pending show) and lives in the loop.
        string hide = ExtractMethod(mainActivity, "private void HideMeasureBusy");
        Assert.Contains("Interlocked.Increment(ref _measureBusyVersion);", hide);
        Assert.Contains("_measureBusyOverlay.Visibility = ViewStates.Gone;", hide);

        // Lifecycle: hidden on pause, removed on teardown.
        string onPause = ExtractMethod(mainActivity, "protected override void OnPause");
        Assert.Contains("HideMeasureBusy();", onPause);
        string cleanup = ExtractMethod(mainActivity, "private void RemoveOwnedOverlayViews");
        Assert.Contains("RemoveFromParent(_measureBusyOverlay);", cleanup);
    }

    [Fact]
    public void BoundingBoxCommit_ShowsAndHidesMeasureBusyChip()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        string commit = ExtractMethod(mainActivity, "private async Task CommitBoundingBoxForNodesAsync");
        Assert.Contains("ShowMeasureBusyDelayed(\"Computing bounding box...\");", commit);

        // The hide must run in the finally so the chip is dismissed even if the
        // boundary calculation throws or times out.
        int finallyIndex = commit.IndexOf("finally", StringComparison.Ordinal);
        int hideIndex = commit.IndexOf("HideMeasureBusy();", StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0, "CommitBoundingBoxForNodesAsync must have a finally block.");
        Assert.True(hideIndex > finallyIndex, "HideMeasureBusy() must be called inside the finally block.");
    }

    [Fact]
    public void AdditiveBoundingBox_ArmsSelectionAndKeepsAccumulatorUntilToggle()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        string toggle = ExtractMethod(mainActivity, "private void OnMeasureBoundingBoxAdditiveCheckedChanged");
        Assert.Contains("BeginBoundingBoxSelectionMode(\"additive enabled\")", toggle);
        Assert.Contains("ResetBoundingBoxAdditiveState(\"additive toggle changed\")", toggle);

        string begin = ExtractMethod(mainActivity, "private void BeginBoundingBoxSelectionMode");
        Assert.Contains("if (!IsBoundingBoxAdditiveEnabled())", begin);
        Assert.Contains("ResetBoundingBoxAdditiveState(\"bbox session started\")", begin);

        string commit = ExtractMethod(mainActivity, "private async Task CommitBoundingBoxForNodesAsync");
        Assert.Contains("TryQueuePendingAdditiveBoundingBoxSelection(nodeIds, selectionVersion, reason)", commit);
        Assert.Contains("TakePendingAdditiveBoundingBoxSelection()", commit);

        string reset = ExtractMethod(mainActivity, "private void ResetBoundingBoxAdditiveState");
        Assert.Contains("_measureBoundingBoxPendingAdditiveNodeIds.Clear();", reset);
    }

    [Fact]
    public void CloudRememberPassword_UsesEncryptedOptInPasswordStorage()
    {
        string secureStore = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\CloudSecureStore.cs"));
        Assert.Contains("LoadRememberedPassword(string serverUrl, string email)", secureStore);
        Assert.Contains("SaveRememberedPassword(string serverUrl, string email, string password)", secureStore);
        Assert.Contains("AES/GCM/NoPadding", secureStore);
        Assert.Contains("AndroidKeyStore", secureStore);
        Assert.Contains("server_url", secureStore);
        Assert.Contains("email", secureStore);

        string settings = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AppSettings.cs"));
        Assert.Contains("CloudRememberPassword", settings);
        Assert.Contains("cloud_remember_password", settings);

        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        Assert.Contains("Text = \"Remember my password\"", mainActivity);
        Assert.Contains("LoadRememberedPassword(serverUrl, initialEmail)", mainActivity);
        Assert.Contains("UpdateCloudRememberedPassword(outcome.ServerUrl, outcome.Email, password, rememberPassword)", mainActivity);
        Assert.Contains("UpdateCloudRememberedPassword(challenge.ServerUrl, challenge.Email, passwordToRemember, rememberPassword)", mainActivity);

        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));
        Assert.Contains("\"Remember password\"", preferences);
        Assert.Contains("cloudSecureStore.ClearRememberedPassword();", preferences);
    }

    [Fact]
    public void DiagnosticsLogRefresh_PreservesSettingsScrollAndAvoidsFocusScroll()
    {
        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));

        string update = ExtractMethod(preferences, "private void UpdateLogPanelText");
        Assert.Contains("FindSettingsScrollForLogPanel()", update);
        Assert.Contains("settingsScroll.ScrollTo(settingsScrollX, settingsScrollY);", update);
        Assert.Contains("ScrollLogPanelToBottom", update);
        Assert.DoesNotContain("FullScroll", update);

        string addPanel = ExtractMethod(preferences, "private void AddLogPanel");
        Assert.Contains("_logText.Focusable = false;", addPanel);
        Assert.Contains("_logText.FocusableInTouchMode = false;", addPanel);
        Assert.Contains("_logHorizontalScroll = new HorizontalScrollView", addPanel);
        Assert.Contains("_logText.SetHorizontallyScrolling(true);", addPanel);
        Assert.DoesNotContain("SetTextIsSelectable(true)", addPanel);

        Assert.Contains("DisposeDiagnosticsLogTooltips();", preferences);
        string disposeLogTooltips = ExtractMethod(preferences, "private void DisposeDiagnosticsLogTooltips");
        Assert.Contains("_logText.TooltipText = null;", disposeLogTooltips);
        Assert.Contains("_logText.ContentDescription = null;", disposeLogTooltips);

        string feed = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\AndroidLogcatFeed.cs"));
        Assert.Contains("MaxLineCharacters", feed);
        Assert.Contains("[truncated", feed);
        Assert.Contains("Showing FA.* and crash/error logs only", feed);
        Assert.Contains("ShouldIncludeLogcatLine", feed);
        Assert.Contains("tag.StartsWith(\"FA.\"", feed);
        Assert.Contains("\"AndroidRuntime\"", feed);
        Assert.Contains("\"Fatal signal\"", feed);
        Assert.Contains("synthetic ? timestamp + \" \" + normalizedLine : normalizedLine", feed);
    }

    [Fact]
    public void DialogInputs_UseEnterKeyToClickTheirPrimaryAction()
    {
        string helper = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\DialogKeyboard.cs"));
        Assert.Contains("ConfirmOnEnter", helper);
        Assert.Contains("SetOnEditorActionListener", helper);
        Assert.Contains("ImeAction.Done", helper);
        Assert.Contains("Keycode.Enter", helper);
        Assert.Contains("confirmView.PerformClick();", helper);

        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(emailInput, signIn);", mainActivity);
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(passwordInput, signIn);", mainActivity);
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(codeInput, verify);", mainActivity);
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(confirmPasswordInput, next);", mainActivity);
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(codeInput, reset);", mainActivity);
        Assert.Contains("dialog.GetButton((int)global::Android.Content.DialogButtonType.Positive)", mainActivity);
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(input, ok);", mainActivity);

        string preferences = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\PreferencesBottomSheet.cs"));
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(hexInput, positive);", preferences);

        string scanner = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Tools\AndroidQrScannerDialog.cs"));
        Assert.Contains("DialogKeyboard.ConfirmOnEnter(_manualInput, submit);", scanner);
    }

    [Fact]
    public void BottomToolbarButtons_DoNotKeepFocusBetweenTaps()
    {
        string mainActivity = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\MainActivity.cs"));

        Assert.Contains("ConfigureBottomToolbarButtons();", mainActivity);
        string configure = ExtractMethod(mainActivity, "private void ConfigureBottomToolbarButtons");
        Assert.Contains("button.Focusable = false;", configure);
        Assert.Contains("button.FocusableInTouchMode = false;", configure);
        Assert.Contains("button.DefaultFocusHighlightEnabled = false;", configure);
        Assert.Contains("button.ClearFocus();", configure);

        string buttons = ExtractMethod(mainActivity, "private IEnumerable<MaterialButton?> BottomToolbarButtons");
        Assert.Contains("yield return _toolMeasureButton;", buttons);
        Assert.Contains("yield return _toolFullscreenButton;", buttons);
        Assert.Contains("yield return _sectionCustomButton;", buttons);
        Assert.Contains("yield return _renderModeClayButton;", buttons);

        string visibility = ExtractMethod(mainActivity, "private void UpdateBottomToolbarVisibility");
        Assert.Contains("ClearBottomToolbarTransientButtonState();", visibility);
    }

    [Fact]
    public void GlesSourceAndShaders_AreAsciiOnly()
    {
        string root = ResolveRepoPath(@"..\FabricationAssistant.Rendering.Gles");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Contains(".gles.", StringComparison.OrdinalIgnoreCase)));

        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int offset = Array.FindIndex(bytes, value => value > 0x7F);
            if (offset >= 0)
                Assert.Fail($"Non-ASCII byte 0x{bytes[offset]:X2} in {Path.GetRelativePath(root, file)} at byte {offset}.");
        }
    }

    [Fact]
    public void MeasureIntegration_WiresNodePoseLookup_SoDimensionsFollowBodies()
    {
        string src = File.ReadAllText(ResolveRepoPath(
            @"..\FabricationAssistant.App.Android\Measurement\AndroidMeasureIntegration.cs"));

        // Lookup resolves each node's live effective transform.
        Assert.Contains("_nodePoseLookup", src);
        Assert.Contains("EffectiveWorldTransform", src);

        // Passed to the session (capture at commit) and the presenter (follow at render).
        Assert.Contains(
            "new MeasurementSession(_store, _units, MeasurementTolerances.Default, undoService, _nodePoseLookup)",
            src);
        Assert.Contains("_nodePoseLookup);", src); // last arg of _presenter.Build(...)

        // Bounding box anchors to the primary movable (leaf) node.
        Assert.Contains("BodyMoveSelection.ResolveMovableNodes", src);
        Assert.Contains("_tool.CommitComputedBoundingBox(boundingBox, primaryNodeId)", src);
    }

    [Fact]
    public void BomConsolidatedFilters_AreWired()
    {
        string panel = File.ReadAllText(ResolveRepoPath(@"..\FabricationAssistant.App.Android\AndroidBomPanel.cs"));
        Assert.Contains("BomConsolidatedFilterEngine<BomPanelRow>", panel);
        Assert.Contains("ApplyFilterRequested", panel);

        string main = File.ReadAllText(ResolveRepoPath(@"..\FabricationAssistant.App.Android\MainActivity.cs"));
        Assert.Contains("_bomFilterStateAccess", main);
        Assert.Contains("new SceneContext(", main);
        Assert.Contains("BomFilterVisibilityPlanner.HiddenOccurrenceIds", main);
        Assert.Contains("AndroidScenePackageState.ApplyVisibilityState(scene, _packageSession, hidden", main);
        Assert.Contains("new FabricationAssistant.Core.UndoRedo.BomFilterChange(", main);
        Assert.Contains("_bomFilterVisibilityDirty = true", main);
    }

    private static string ResolveRepoPath(string relativeToTestProject, [CallerFilePath] string caller = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, relativeToTestProject));

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");

        int braceStart = source.IndexOf('{', start);
        Assert.True(braceStart > start, $"{signature} body was not found.");

        int depth = 0;
        for (int i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        Assert.Fail($"{signature} body was not closed.");
        return string.Empty;
    }
}
