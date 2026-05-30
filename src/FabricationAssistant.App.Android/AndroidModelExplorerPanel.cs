using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using FabricationAssistant.Core.SceneGraph;
using Google.Android.Material.Button;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace FabricationAssistant.App.Android;

internal sealed class AndroidModelExplorerPanel : IDisposable
{
    private readonly Func<Scene?> _sceneAccessor;
    private readonly List<AndroidModelExplorerRow> _visibleRows = new();
    private readonly HashSet<int> _highlightedPresentedIds = new();
    private readonly HashSet<int> _pathPresentedIds = new();
    private readonly List<MaterialButton> _actionButtons = new();
    private readonly StyledTooltipRegistry _tooltips = new();

    private Scene? _scene;
    private AndroidModelExplorerTree? _tree;
    private ListView? _list;
    private TextView? _status;
    private TextView? _summary;
    private ModelExplorerAdapter? _adapter;
    private bool _packDuplicates;
    private int? _selectedPresentedId;
    private int _rowVersion;
    private bool _disposed;

    public AndroidModelExplorerPanel(Func<Scene?> sceneAccessor)
    {
        _sceneAccessor = sceneAccessor ?? throw new ArgumentNullException(nameof(sceneAccessor));
    }

    public Action<AndroidModelExplorerNode>? NodeSelected { get; set; }

    public Action<AndroidModelExplorerNode, bool>? VisibilityChanged { get; set; }

    /// <summary>
    /// Raised after a Pack/Unpack rebuild (which calls SetScene and clears the
    /// tree's highlight/selection state) so the host can re-apply the current
    /// viewport selection onto the repacked tree. Not raised for the initial
    /// scene attach (SetScene called directly by the host).
    /// </summary>
    public Action? SelectionResyncRequested { get; set; }

    public View CreateView(Context ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DisposeViewContent();

        int pad = Dp(ctx, 14);
        var root = new LinearLayout(ctx)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
        };
        root.SetBackgroundResource(Resource.Color.fa_app_background);
        root.SetPadding(pad, pad, pad, pad);

        AddHeader(ctx, root);

        _status = new TextView(ctx)
        {
            Gravity = GravityFlags.Center,
        };
        _status.SetTextSize(ComplexUnitType.Sp, 14f);
        _status.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_secondary));
        _status.SetPadding(Dp(ctx, 24), Dp(ctx, 24), Dp(ctx, 24), Dp(ctx, 24));
        root.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f));

        _adapter = new ModelExplorerAdapter(
            ctx,
            _visibleRows,
            () => _selectedPresentedId,
            () => _highlightedPresentedIds,
            () => _pathPresentedIds,
            ToggleNodeExpansion,
            ToggleNodeVisibility,
            _tooltips);

        _list = new ListView(ctx)
        {
            Adapter = _adapter,
            ChoiceMode = ChoiceMode.Single,
            DividerHeight = 1,
            FastScrollEnabled = true,
        };
        _list.SetBackgroundColor(ColorRes(ctx, Resource.Color.fa_app_background));
        _list.ItemClick += OnListItemClick;
        root.AddView(_list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f));

        SetScene(_sceneAccessor());
        _tooltips.AttachTree(ctx, root, includeStaticText: true);
        return root;
    }

    public IDisposable CreateViewDisposer()
        => new ModelExplorerViewDisposer(this);

    public void DisposeView()
        => DisposeViewContent();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeViewContent();
        NodeSelected = null;
        VisibilityChanged = null;
        _visibleRows.Clear();
        _highlightedPresentedIds.Clear();
        _pathPresentedIds.Clear();
        _tree = null;
        _scene = null;
    }

    private void DisposeViewContent()
    {
        if (_list is not null)
        {
            _list.ItemClick -= OnListItemClick;
            _list.Adapter = null;
        }

        foreach (MaterialButton button in _actionButtons)
            button.SetOnClickListener(null);
        _actionButtons.Clear();

        _adapter?.Dispose();
        _adapter = null;
        _tooltips.Dispose();
        _list = null;
        _status = null;
        _summary = null;
    }

    private void OnListItemClick(object? sender, AdapterView.ItemClickEventArgs e)
    {
        if (e.Position < 0 || e.Position >= _visibleRows.Count)
            return;

        AndroidModelExplorerNode node = _visibleRows[e.Position].Node;
        _selectedPresentedId = node.Id;
        NodeSelected?.Invoke(node);
        _adapter?.NotifyDataSetChanged();
    }

    public void SetScene(Scene? scene)
    {
        IReadOnlyDictionary<int, bool>? expansionState = _tree?.CaptureExpansionState();
        _scene = scene;
        _tree = scene is null
            ? null
            : AndroidModelExplorerTree.Build(scene, _packDuplicates, expansionState);
        _selectedPresentedId = null;
        _highlightedPresentedIds.Clear();
        _pathPresentedIds.Clear();
        RefreshRows(scrollToSelection: false);
    }

    public void SetSelection(
        Scene? scene,
        IEnumerable<int> selectedSceneNodeIds,
        int? explicitPresentedId,
        bool expandSelectedPath,
        bool scrollToSelection)
    {
        _scene = scene;
        _highlightedPresentedIds.Clear();
        _pathPresentedIds.Clear();
        _selectedPresentedId = null;

        AndroidModelExplorerTree? tree = _tree;
        if (tree is null || scene is null)
        {
            RefreshRows(scrollToSelection: false);
            return;
        }

        foreach (int nodeId in selectedSceneNodeIds.Distinct())
        {
            int presentedId = tree.ResolvePresentedNodeId(nodeId);
            if (tree.TryGetNode(presentedId, out AndroidModelExplorerNode? selectedNode)
                && selectedNode is not null)
            {
                _highlightedPresentedIds.Add(selectedNode.Id);
                AddPath(selectedNode, _pathPresentedIds);
            }
        }

        if (explicitPresentedId.HasValue
            && tree.TryGetNode(explicitPresentedId.Value, out AndroidModelExplorerNode? explicitNode)
            && explicitNode is not null)
        {
            _selectedPresentedId = explicitNode.Id;
            _highlightedPresentedIds.Add(explicitNode.Id);
            _pathPresentedIds.Add(explicitNode.Id);
            AddPath(explicitNode, _pathPresentedIds);
        }
        else if (_highlightedPresentedIds.Count > 0)
        {
            _selectedPresentedId = _highlightedPresentedIds.OrderBy(id => id).First();
            if (tree.TryGetNode(_selectedPresentedId.Value, out AndroidModelExplorerNode? selectedNode)
                && selectedNode is not null)
                _pathPresentedIds.Add(selectedNode.Id);
        }

        if (expandSelectedPath)
        {
            foreach (int presentedId in _pathPresentedIds)
            {
                if (tree.TryGetNode(presentedId, out AndroidModelExplorerNode? pathNode)
                    && pathNode is not null)
                    pathNode.ExpandToRoot();
            }
        }

        RefreshRows(scrollToSelection);
    }

    public void RefreshVisibility()
    {
        _adapter?.NotifyDataSetChanged();
        UpdateStatus();
    }

    public int[] GetSceneNodeIdsForSelection(Scene scene, AndroidModelExplorerNode node)
        => _tree?.GetSceneNodeIdsForTreeSelection(scene, node) ?? Array.Empty<int>();

    public int[] GetSceneNodeIdsForVisibility(Scene scene, AndroidModelExplorerNode node)
        => _tree?.GetSceneNodeIdsForTreeVisibility(scene, node) ?? Array.Empty<int>();

    public string[] GetOccurrenceIdsForSelection(Scene scene, AndroidModelExplorerNode node)
        => _tree?.GetOccurrenceIdsForTreeSelection(scene, node) ?? Array.Empty<string>();

    public string[] GetOccurrenceIdsForVisibility(Scene scene, AndroidModelExplorerNode node)
        => _tree?.GetOccurrenceIdsForTreeVisibility(scene, node) ?? Array.Empty<string>();

    private void AddHeader(Context ctx, LinearLayout root)
    {
        var title = new TextView(ctx)
        {
            Text = "Model Explorer",
        };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextSize(ComplexUnitType.Sp, 18f);
        title.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_primary));
        root.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var subtitle = new TextView(ctx)
        {
            Text = "Scene hierarchy",
        };
        subtitle.SetTextSize(ComplexUnitType.Sp, 12f);
        subtitle.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_secondary));
        subtitle.SetPadding(0, Dp(ctx, 2), 0, Dp(ctx, 6));
        root.AddView(subtitle, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        _summary = new TextView(ctx);
        _summary.SetTextSize(ComplexUnitType.Sp, 12f);
        _summary.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_secondary));
        _summary.SetPadding(0, 0, 0, Dp(ctx, 8));
        root.AddView(_summary, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var actions = new LinearLayout(ctx)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
        };
        actions.SetGravity(GravityFlags.CenterVertical);
        actions.SetPadding(0, 0, 0, Dp(ctx, 10));
        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        actions.AddView(CreateActionButton(ctx, "Expand", () =>
        {
            _tree?.SetExpandedRecursive(true);
            RefreshRows(scrollToSelection: false);
        }));
        actions.AddView(CreateActionButton(ctx, "Collapse", () =>
        {
            _tree?.SetExpandedRecursive(false);
            RefreshRows(scrollToSelection: false);
        }));
        actions.AddView(CreateActionButton(ctx, "Pack", () =>
        {
            if (_packDuplicates)
                return;
            _packDuplicates = true;
            SetScene(_sceneAccessor());
            SelectionResyncRequested?.Invoke();
        }));
        actions.AddView(CreateActionButton(ctx, "Unpack", () =>
        {
            if (!_packDuplicates)
                return;
            _packDuplicates = false;
            SetScene(_sceneAccessor());
            SelectionResyncRequested?.Invoke();
        }));
    }

    private MaterialButton CreateActionButton(Context ctx, string text, Action action)
    {
        var button = new MaterialButton(ctx)
        {
            Text = text,
            ContentDescription = text,
        };
        button.SetAllCaps(false);
        button.SetMinHeight(0);
        button.SetMinimumHeight(0);
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetTextSize(ComplexUnitType.Sp, 12f);
        button.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_primary));
        button.BackgroundTintList = ColorStateList.ValueOf(ColorRes(ctx, Resource.Color.fa_control_background));
        button.StrokeColor = ColorStateList.ValueOf(ColorRes(ctx, Resource.Color.fa_border));
        button.StrokeWidth = Dp(ctx, 1);
        button.CornerRadius = Dp(ctx, 6);
        button.SetPadding(Dp(ctx, 10), 0, Dp(ctx, 10), 0);
        button.SetOnClickListener(new ActionClickListener(action));
        _actionButtons.Add(button);

        var lp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            Dp(ctx, 32));
        lp.RightMargin = Dp(ctx, 6);
        button.LayoutParameters = lp;
        return button;
    }

    private void ToggleNodeExpansion(AndroidModelExplorerNode node)
    {
        if (node.Children.Count == 0)
            return;

        node.IsExpanded = !node.IsExpanded;
        RefreshRows(scrollToSelection: false);
    }

    private void ToggleNodeVisibility(AndroidModelExplorerNode node)
    {
        bool targetVisible = !node.IsVisible;
        VisibilityChanged?.Invoke(node, targetVisible);
        RefreshVisibility();
    }

    private void RefreshRows(bool scrollToSelection)
    {
        _rowVersion++;
        _visibleRows.Clear();
        _tree?.AppendVisibleRows(_visibleRows);
        _adapter?.NotifyDataSetChanged();
        UpdateStatus();

        if (scrollToSelection)
            ScrollToSelected();
    }

    private void UpdateStatus()
    {
        int rowCount = _tree?.PresentedNodeCount ?? 0;
        int visibleCount = _visibleRows.Count;

        if (_summary is not null)
        {
            _summary.Text = rowCount == 0
                ? string.Empty
                : $"{rowCount} items, {visibleCount} shown";
        }

        if (_status is null || _list is null)
            return;

        bool hasRows = _visibleRows.Count > 0;
        _status.Visibility = hasRows ? ViewStates.Gone : ViewStates.Visible;
        _list.Visibility = hasRows ? ViewStates.Visible : ViewStates.Gone;
        _status.Text = _scene is null ? "No model loaded." : "No hierarchy rows.";
    }

    private void ScrollToSelected()
    {
        if (_list is null || !_selectedPresentedId.HasValue)
            return;

        int index = _visibleRows.FindIndex(row => row.Node.Id == _selectedPresentedId.Value);
        if (index < 0)
            return;

        int version = _rowVersion;
        int selectedId = _selectedPresentedId.Value;
        _list.Post(() =>
        {
            if (_list is null || version != _rowVersion || _selectedPresentedId != selectedId)
                return;

            int currentIndex = _visibleRows.FindIndex(row => row.Node.Id == selectedId);
            if (currentIndex < 0)
                return;

            _list.SetSelection(Math.Max(0, currentIndex - 2));
        });
    }

    private static void AddPath(AndroidModelExplorerNode node, HashSet<int> target)
    {
        for (AndroidModelExplorerNode? current = node.Parent; current is not null; current = current.Parent)
            target.Add(current.Id);
    }

    private static int Dp(Context ctx, float dp)
    {
        float density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
        return (int)MathF.Round(dp * density);
    }

    private static Color ColorRes(Context ctx, int resId) => new(ctx.GetColor(resId));

    private sealed class ModelExplorerAdapter : BaseAdapter<AndroidModelExplorerRow>
    {
        private readonly Context _ctx;
        private readonly IReadOnlyList<AndroidModelExplorerRow> _rows;
        private readonly Func<int?> _selectedIdAccessor;
        private readonly Func<IReadOnlySet<int>> _highlightedAccessor;
        private readonly Func<IReadOnlySet<int>> _pathAccessor;
        private readonly Action<AndroidModelExplorerNode> _toggleExpansion;
        private readonly Action<AndroidModelExplorerNode> _toggleVisibility;
        private readonly StyledTooltipRegistry _tooltips;

        public ModelExplorerAdapter(
            Context ctx,
            IReadOnlyList<AndroidModelExplorerRow> rows,
            Func<int?> selectedIdAccessor,
            Func<IReadOnlySet<int>> highlightedAccessor,
            Func<IReadOnlySet<int>> pathAccessor,
            Action<AndroidModelExplorerNode> toggleExpansion,
            Action<AndroidModelExplorerNode> toggleVisibility,
            StyledTooltipRegistry tooltips)
        {
            _ctx = ctx;
            _rows = rows;
            _selectedIdAccessor = selectedIdAccessor;
            _highlightedAccessor = highlightedAccessor;
            _pathAccessor = pathAccessor;
            _toggleExpansion = toggleExpansion;
            _toggleVisibility = toggleVisibility;
            _tooltips = tooltips;
        }

        public override AndroidModelExplorerRow this[int position] => _rows[position];

        public override int Count => _rows.Count;

        public override long GetItemId(int position) => _rows[position].Node.Id;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            AndroidModelExplorerRow row = _rows[position];
            AndroidModelExplorerNode node = row.Node;
            int? selectedId = _selectedIdAccessor();
            IReadOnlySet<int> highlightedIds = _highlightedAccessor();
            IReadOnlySet<int> pathIds = _pathAccessor();
            bool selected = selectedId.HasValue && selectedId.Value == node.Id;
            bool highlighted = selected || highlightedIds.Contains(node.Id);
            bool onPath = pathIds.Contains(node.Id);

            var root = new LinearLayout(_ctx)
            {
                Orientation = global::Android.Widget.Orientation.Horizontal,
                BaselineAligned = false,
            };
            root.SetGravity(GravityFlags.CenterVertical);
            root.SetMinimumHeight(Dp(_ctx, 36));
            root.SetPadding(Dp(_ctx, 2), 0, Dp(_ctx, 4), 0);
            root.ContentDescription = "Select " + node.DisplayName;
            root.Background = CreateRowBackground(_ctx, selected, highlighted, onPath);

            var indent = new Space(_ctx);
            root.AddView(indent, new LinearLayout.LayoutParams(
                Math.Min(Dp(_ctx, 160), Dp(_ctx, row.Depth * 16)),
                1));

            var expand = new TextView(_ctx)
            {
                Gravity = GravityFlags.Center,
                Text = node.Children.Count == 0 ? string.Empty : node.IsExpanded ? "-" : "+",
            };
            expand.SetTextSize(ComplexUnitType.Sp, 15f);
            expand.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            expand.SetTextColor(ColorRes(_ctx, Resource.Color.fa_text_secondary));
            expand.Clickable = node.Children.Count > 0;
            expand.Focusable = false;
            expand.ContentDescription = node.Children.Count == 0
                ? null
                : node.IsExpanded ? "Collapse " + node.DisplayName : "Expand " + node.DisplayName;
            expand.SetOnClickListener(node.Children.Count > 0
                ? new NodeActionClickListener(_toggleExpansion, node)
                : null);
            root.AddView(expand, new LinearLayout.LayoutParams(Dp(_ctx, 28), Dp(_ctx, 36)));

            var icon = new TextView(_ctx)
            {
                Gravity = GravityFlags.Center,
                Text = IconText(node),
            };
            icon.SetTextSize(ComplexUnitType.Sp, 11f);
            icon.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
            icon.SetTextColor(ColorRes(_ctx, Resource.Color.fa_text_secondary));
            icon.ContentDescription = node.NodeType.ToString();
            root.AddView(icon, new LinearLayout.LayoutParams(Dp(_ctx, 24), Dp(_ctx, 36)));

            var label = new TextView(_ctx)
            {
                Text = node.DisplayName,
                Gravity = GravityFlags.CenterVertical,
                Ellipsize = TextUtils.TruncateAt.End,
            };
            label.SetSingleLine(true);
            label.SetTextSize(ComplexUnitType.Sp, 14f);
            label.SetTextColor(ColorRes(_ctx, selected
                ? Resource.Color.fa_text_primary
                : Resource.Color.fa_text_primary));
            label.Alpha = node.IsEffectivelyVisible ? 1f : 0.42f;
            root.AddView(label, new LinearLayout.LayoutParams(
                0,
                Dp(_ctx, 36),
                1f));

            var visibility = new ImageButton(_ctx)
            {
                Background = null,
                ContentDescription = node.IsVisible ? "Hide item" : "Show item",
                Focusable = false,
                Clickable = true,
            };
            visibility.SetImageResource(node.IsVisible
                ? Resource.Drawable.ic_tool_show_all
                : Resource.Drawable.ic_tool_hide);
            visibility.SetColorFilter(ColorRes(_ctx, node.IsVisible
                ? Resource.Color.fa_text_secondary
                : Resource.Color.fa_text_disabled));
            visibility.SetOnClickListener(new NodeActionClickListener(_toggleVisibility, node));
            root.AddView(visibility, new LinearLayout.LayoutParams(Dp(_ctx, 40), Dp(_ctx, 36)));

            _tooltips.AttachTree(_ctx, root, includeStaticText: true);
            return root;
        }

        private static string IconText(AndroidModelExplorerNode node)
        {
            if (node.IsVirtualGroup || node.NodeType is SceneNodeType.Root or SceneNodeType.Assembly)
                return node.IsVirtualGroup ? "G" : "A";

            return node.NodeType switch
            {
                SceneNodeType.Part => "P",
                SceneNodeType.Instance => "I",
                SceneNodeType.Shape => "B",
                _ => ".",
            };
        }

        private static GradientDrawable CreateRowBackground(
            Context ctx,
            bool selected,
            bool highlighted,
            bool onPath)
        {
            var background = new GradientDrawable();
            background.SetCornerRadius(Dp(ctx, 5));
            int colorRes = selected
                ? Resource.Color.fa_highlight
                : highlighted
                    ? Resource.Color.fa_control_background
                    : onPath
                        ? Resource.Color.fa_surface_background
                        : global::Android.Resource.Color.Transparent;
            background.SetColor(ColorRes(ctx, colorRes));
            return background;
        }
    }

    private sealed class ActionClickListener(Action action) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => action();
    }

    private sealed class NodeActionClickListener(
        Action<AndroidModelExplorerNode> action,
        AndroidModelExplorerNode node) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? v) => action(node);
    }

    private sealed class ModelExplorerViewDisposer(AndroidModelExplorerPanel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.DisposeView();
        }
    }
}

internal sealed class AndroidModelExplorerTree
{
    private readonly Dictionary<int, AndroidModelExplorerNode> _lookupByPresentedId;
    private readonly Dictionary<int, int> _presentedIdBySceneNodeId;

    private AndroidModelExplorerTree(
        AndroidModelExplorerNode root,
        Dictionary<int, AndroidModelExplorerNode> lookupByPresentedId,
        Dictionary<int, int> presentedIdBySceneNodeId)
    {
        Root = root;
        _lookupByPresentedId = lookupByPresentedId;
        _presentedIdBySceneNodeId = presentedIdBySceneNodeId;
    }

    public AndroidModelExplorerNode Root { get; }

    public int PresentedNodeCount => Math.Max(0, _lookupByPresentedId.Count - 1);

    public static AndroidModelExplorerTree Build(
        Scene scene,
        bool packDuplicates,
        IReadOnlyDictionary<int, bool>? expansionState)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var lookup = new Dictionary<int, AndroidModelExplorerNode>(scene.NodesById.Count);
        var presentedLookup = new Dictionary<int, int>(scene.NodesById.Count);
        int nextVirtualId = -1;

        AndroidModelExplorerNode BuildVisibleNode(SceneNode node)
        {
            var viewNode = AndroidModelExplorerNode.Create(node);
            lookup[node.Id] = viewNode;
            presentedLookup[node.Id] = node.Id;

            List<AndroidModelExplorerNode> childNodes = BuildProjectedChildren(node, node);
            IEnumerable<AndroidModelExplorerNode> children = packDuplicates
                ? PackDuplicateChildren(childNodes, lookup, ref nextVirtualId)
                : childNodes;

            foreach (AndroidModelExplorerNode child in children)
                viewNode.AddChild(child);

            return viewNode;
        }

        List<AndroidModelExplorerNode> BuildProjectedChildren(SceneNode parentNode, SceneNode visibleOwner)
        {
            var children = new List<AndroidModelExplorerNode>(parentNode.Children.Count);
            foreach (SceneNode child in parentNode.Children)
                AppendProjectedNode(child, visibleOwner, children);
            return children;
        }

        void AppendProjectedNode(
            SceneNode node,
            SceneNode visibleOwner,
            ICollection<AndroidModelExplorerNode> output)
        {
            if (ShouldHideFromExplorer(node, visibleOwner))
            {
                presentedLookup[node.Id] = visibleOwner.Id;
                foreach (SceneNode child in node.Children)
                    AppendProjectedNode(child, visibleOwner, output);
                return;
            }

            output.Add(BuildVisibleNode(node));
        }

        AndroidModelExplorerNode root = BuildVisibleNode(scene.Root);
        var tree = new AndroidModelExplorerTree(root, lookup, presentedLookup);
        if (expansionState is not null && expansionState.Count > 0)
            tree.RestoreExpansionState(expansionState);
        else
            tree.ExpandInitialNodes();

        return tree;
    }

    public IReadOnlyDictionary<int, bool> CaptureExpansionState()
        => Root
            .EnumerateSelfAndDescendants()
            .Where(node => node.Children.Count > 0)
            .ToDictionary(node => node.Id, node => node.IsExpanded);

    public bool TryGetNode(int presentedId, out AndroidModelExplorerNode? node)
        => _lookupByPresentedId.TryGetValue(presentedId, out node);

    public int ResolvePresentedNodeId(int sceneNodeId)
        => _presentedIdBySceneNodeId.TryGetValue(sceneNodeId, out int presentedId)
            ? presentedId
            : sceneNodeId;

    public void AppendVisibleRows(List<AndroidModelExplorerRow> rows)
    {
        foreach (AndroidModelExplorerNode child in Root.Children)
            AppendVisibleRows(child, depth: 0, rows);
    }

    public void SetExpandedRecursive(bool isExpanded)
    {
        Root.IsExpanded = true;
        foreach (AndroidModelExplorerNode child in Root.Children)
            child.SetExpandedRecursive(isExpanded);
    }

    public int[] GetSceneNodeIdsForTreeVisibility(Scene scene, AndroidModelExplorerNode treeNode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(treeNode);

        if (treeNode.IsVirtualGroup)
        {
            return treeNode
                .EnumerateSelfAndDescendants()
                .Where(node => node.SceneNodeId.HasValue)
                .SelectMany(node => GetVisualGroupSceneNodeIds(scene, node.SceneNodeId!.Value))
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        return treeNode.SceneNodeId.HasValue
            ? GetVisualGroupSceneNodeIds(scene, treeNode.SceneNodeId.Value)
            : Array.Empty<int>();
    }

    public string[] GetOccurrenceIdsForTreeVisibility(Scene scene, AndroidModelExplorerNode treeNode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(treeNode);

        return GetSceneNodeIdsForTreeVisibility(scene, treeNode)
            .Select(nodeId => scene.GetNode(nodeId))
            .Select(node => AndroidScenePackageState.TryGetOccurrenceId(scene, node, out string occurrenceId)
                ? occurrenceId
                : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public string[] GetOccurrenceIdsForTreeSelection(Scene scene, AndroidModelExplorerNode? treeNode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (treeNode is null)
            return Array.Empty<string>();

        IEnumerable<AndroidModelExplorerNode> nodesToInspect = treeNode.IsVirtualGroup
            ? treeNode.EnumerateSelfAndDescendants().Where(node => node.SceneNodeId.HasValue)
            : treeNode.SceneNodeId.HasValue
                ? [treeNode]
                : Array.Empty<AndroidModelExplorerNode>();

        return nodesToInspect
            .Select(node => node.SceneNodeId.HasValue ? scene.GetNode(node.SceneNodeId.Value) : null)
            .Select(node => AndroidScenePackageState.TryGetOccurrenceId(scene, node, out string occurrenceId)
                ? occurrenceId
                : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public int[] GetSceneNodeIdsForTreeSelection(Scene scene, AndroidModelExplorerNode? treeNode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (treeNode is null)
            return Array.Empty<int>();

        if (treeNode.IsVirtualGroup)
        {
            return treeNode
                .EnumerateSelfAndDescendants()
                .Where(node => node.SceneNodeId.HasValue)
                .SelectMany(node => GetVisualGroupSceneNodeIds(scene, node.SceneNodeId!.Value))
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }

        return treeNode.SceneNodeId.HasValue
            ? [treeNode.SceneNodeId.Value]
            : Array.Empty<int>();
    }

    private int[] GetVisualGroupSceneNodeIds(Scene scene, int sceneNodeId)
    {
        int presentedNodeId = ResolvePresentedNodeId(sceneNodeId);
        int[] presentedGroupSeedNodeIds = scene.NodesById.Values
            .Where(node => ResolvePresentedNodeId(node.Id) == presentedNodeId)
            .Select(node => node.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var visualGroupNodeIds = new HashSet<int>();
        foreach (int seedNodeId in presentedGroupSeedNodeIds)
        {
            visualGroupNodeIds.Add(seedNodeId);
            SceneNode? seedNode = scene.GetNode(seedNodeId);
            if (seedNode is not null)
                CollectDescendantIds(seedNode, visualGroupNodeIds);
        }

        if (visualGroupNodeIds.Count > 1)
            return visualGroupNodeIds.OrderBy(id => id).ToArray();

        SceneNode? node = scene.GetNode(sceneNodeId);
        if (node is not null
            && AndroidScenePackageState.TryGetOccurrenceId(scene, node, out string occurrenceId))
        {
            foreach (int occurrenceNodeId in AndroidScenePackageState.GetNodeIdsForOccurrences(scene, [occurrenceId]))
            {
                visualGroupNodeIds.Add(occurrenceNodeId);
                SceneNode? occurrenceNode = scene.GetNode(occurrenceNodeId);
                if (occurrenceNode is not null)
                    CollectDescendantIds(occurrenceNode, visualGroupNodeIds);
            }

            if (visualGroupNodeIds.Count > 0)
                return visualGroupNodeIds.OrderBy(id => id).ToArray();
        }

        return presentedGroupSeedNodeIds.Length == 1
            ? presentedGroupSeedNodeIds
            : [sceneNodeId];
    }

    private void RestoreExpansionState(IReadOnlyDictionary<int, bool> expansionState)
    {
        Root.IsExpanded = true;
        foreach (AndroidModelExplorerNode node in Root.EnumerateSelfAndDescendants())
        {
            if (ReferenceEquals(node, Root))
                continue;

            if (expansionState.TryGetValue(node.Id, out bool isExpanded))
                node.IsExpanded = isExpanded;
        }
    }

    private void ExpandInitialNodes()
    {
        Root.IsExpanded = true;
        foreach (AndroidModelExplorerNode topLevelNode in Root.Children)
        {
            AndroidModelExplorerNode? current = topLevelNode;
            while (current is not null)
            {
                current.IsExpanded = true;
                if (current.Children.Count != 1)
                    break;
                current = current.Children[0];
            }
        }
    }

    private static void AppendVisibleRows(
        AndroidModelExplorerNode node,
        int depth,
        List<AndroidModelExplorerRow> rows)
    {
        rows.Add(new AndroidModelExplorerRow(node, depth));
        if (!node.IsExpanded)
            return;

        foreach (AndroidModelExplorerNode child in node.Children)
            AppendVisibleRows(child, depth + 1, rows);
    }

    private static IReadOnlyList<AndroidModelExplorerNode> PackDuplicateChildren(
        IReadOnlyList<AndroidModelExplorerNode> children,
        Dictionary<int, AndroidModelExplorerNode> lookup,
        ref int nextVirtualId)
    {
        if (children.Count < 2)
            return children;

        var countsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (AndroidModelExplorerNode child in children)
        {
            string key = NormalizeGroupingKey(child.DisplayName);
            countsByName[key] = countsByName.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        var emittedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packed = new List<AndroidModelExplorerNode>(children.Count);

        foreach (AndroidModelExplorerNode child in children)
        {
            string key = NormalizeGroupingKey(child.DisplayName);
            if (!countsByName.TryGetValue(key, out int count) || count <= 1)
            {
                packed.Add(child);
                continue;
            }

            if (!emittedGroups.Add(key))
                continue;

            var group = AndroidModelExplorerNode.CreateVirtual(
                nextVirtualId--,
                $"{FormatDisplayName(child.DisplayName)} ({count})",
                SceneNodeType.Assembly);
            lookup[group.Id] = group;

            foreach (AndroidModelExplorerNode match in children.Where(candidate =>
                         string.Equals(
                             NormalizeGroupingKey(candidate.DisplayName),
                             key,
                             StringComparison.OrdinalIgnoreCase)))
            {
                group.AddChild(match);
            }

            packed.Add(group);
        }

        return packed;
    }

    private static bool ShouldHideFromExplorer(SceneNode node, SceneNode visibleOwner)
    {
        if (node.Parent is null)
            return false;

        if (IsSplitMaterialPrimitiveShape(node, visibleOwner))
            return true;

        if (string.IsNullOrWhiteSpace(node.SourceNodeName)
            && IsAutoGeneratedNodeLabel(node.DisplayName))
        {
            return true;
        }

        string? ownerSourceKey = visibleOwner.Metadata?.SourceKey;
        if (string.IsNullOrWhiteSpace(ownerSourceKey))
            return false;

        return SubtreeBelongsToSourceKey(node, ownerSourceKey);
    }

    private static bool IsSplitMaterialPrimitiveShape(SceneNode node, SceneNode visibleOwner)
    {
        return ReferenceEquals(node.Parent, visibleOwner)
               && node.NodeType == SceneNodeType.Shape
               && visibleOwner.NodeType == SceneNodeType.Part
               && visibleOwner.MeshId is null
               && node.MeshId.HasValue;
    }

    private static bool SubtreeBelongsToSourceKey(SceneNode node, string ownerSourceKey)
    {
        string? nodeSourceKey = node.Metadata?.SourceKey;
        if (!string.IsNullOrWhiteSpace(nodeSourceKey)
            && !string.Equals(nodeSourceKey, ownerSourceKey, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (SceneNode child in node.Children)
        {
            if (!SubtreeBelongsToSourceKey(child, ownerSourceKey))
                return false;
        }

        return true;
    }

    private static bool IsAutoGeneratedNodeLabel(string displayName)
    {
        if (!displayName.StartsWith("Node ", StringComparison.OrdinalIgnoreCase))
            return false;

        ReadOnlySpan<char> suffix = displayName.AsSpan(5).Trim();
        if (suffix.IsEmpty)
            return false;

        foreach (char ch in suffix)
        {
            if (!char.IsAsciiDigit(ch))
                return false;
        }

        return true;
    }

    private static string NormalizeGroupingKey(string displayName)
        => FormatDisplayName(displayName).Trim();

    private static string FormatDisplayName(string displayName)
        => string.IsNullOrWhiteSpace(displayName) ? "Unnamed" : displayName;

    private static void CollectDescendantIds(SceneNode node, HashSet<int> ids)
    {
        foreach (SceneNode child in node.Children)
        {
            if (ids.Add(child.Id))
                CollectDescendantIds(child, ids);
        }
    }
}

internal sealed class AndroidModelExplorerNode
{
    private AndroidModelExplorerNode(
        int id,
        int? sceneNodeId,
        SceneNode? node,
        string displayName,
        SceneNodeType nodeType,
        bool isVirtualGroup,
        bool isExpanded)
    {
        Id = id;
        SceneNodeId = sceneNodeId;
        Node = node;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed" : displayName;
        NodeType = nodeType;
        IsVirtualGroup = isVirtualGroup;
        IsExpanded = isExpanded;
    }

    public int Id { get; }

    public int? SceneNodeId { get; }

    public SceneNode? Node { get; }

    public string DisplayName { get; }

    public SceneNodeType NodeType { get; }

    public bool IsVirtualGroup { get; }

    public bool IsExpanded { get; set; }

    public AndroidModelExplorerNode? Parent { get; private set; }

    public List<AndroidModelExplorerNode> Children { get; } = new();

    public bool IsVisible => IsVirtualGroup
        ? Children.All(child => child.IsVisible)
        : Node?.Visible ?? true;

    public bool IsEffectivelyVisible
    {
        get
        {
            for (AndroidModelExplorerNode? current = this; current is not null; current = current.Parent)
            {
                if (!current.IsVisible)
                    return false;
            }

            return true;
        }
    }

    public static AndroidModelExplorerNode Create(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new AndroidModelExplorerNode(
            node.Id,
            node.Id,
            node,
            node.DisplayName,
            node.NodeType,
            isVirtualGroup: false,
            isExpanded: node.NodeType == SceneNodeType.Root);
    }

    public static AndroidModelExplorerNode CreateVirtual(int id, string displayName, SceneNodeType nodeType)
        => new(
            id,
            sceneNodeId: null,
            node: null,
            displayName,
            nodeType,
            isVirtualGroup: true,
            isExpanded: false);

    public void AddChild(AndroidModelExplorerNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
        Children.Add(child);
    }

    public void ExpandToRoot()
    {
        for (AndroidModelExplorerNode? current = this; current is not null; current = current.Parent)
            current.IsExpanded = true;
    }

    public void SetExpandedRecursive(bool isExpanded)
    {
        IsExpanded = isExpanded;
        foreach (AndroidModelExplorerNode child in Children)
            child.SetExpandedRecursive(isExpanded);
    }

    public IEnumerable<AndroidModelExplorerNode> EnumerateSelfAndDescendants()
    {
        yield return this;
        foreach (AndroidModelExplorerNode child in Children)
        {
            foreach (AndroidModelExplorerNode descendant in child.EnumerateSelfAndDescendants())
                yield return descendant;
        }
    }
}

internal readonly record struct AndroidModelExplorerRow(AndroidModelExplorerNode Node, int Depth);
