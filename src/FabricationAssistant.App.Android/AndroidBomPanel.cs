using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.Core.Widget;
using FabricationAssistant.App.Android.Bom;
using FabricationAssistant.Core.SceneGraph;
using FabricationAssistant.Import.Fa;
using Google.Android.Material.Button;

namespace FabricationAssistant.App.Android;

internal enum AndroidBomPanelKind
{
    Hierarchy,
    Consolidated,
}

internal enum AndroidBomPanelAction
{
    Select,
    Isolate,
    IsolateXray,
}

internal sealed record AndroidBomPanelTarget(
    AndroidBomPanelKind Kind,
    string Key,
    string Label,
    string PartKey,
    int PartOrdinal,
    int PartOccurrenceCount);

internal sealed class AndroidBomPanel : IDisposable
{
    private readonly FaPackageQueryService _queryService;
    private readonly Func<Scene?> _sceneAccessor;
    private readonly AndroidBomPanelKind _kind;
    private readonly ColumnSpec[] _columns;
    private readonly List<BomPanelRow> _roots = new();
    private readonly List<BomPanelRow> _allRows = new();
    private readonly List<BomPanelRow> _visibleRows = new();

    private HorizontalScrollView? _tableScroll;
    private LinearLayout? _tableRoot;
    private LinearLayout? _headerRow;
    private int[] _columnWidthsPx = Array.Empty<int>();
    private int[]? _measuredColumnWidthsPx;
    private float _measuredColumnWidthsDensity;
    private int _tableWidthPx;
    private TextView? _summary;
    private TextView? _status;
    private EditText? _search;
    private ListView? _list;
    private BomPanelAdapter? _adapter;
    private HorizontalTableScrollTouchListener? _headerScrollTouchListener;
    private HorizontalTableScrollTouchListener? _listScrollTouchListener;
    private EventHandler<View.LayoutChangeEventArgs>? _tableScrollLayoutChangeHandler;
    private BomPanelRow? _selectedRow;
    private string? _selectedRowKey;
    private readonly Dictionary<string, int> _occurrenceCountByPartKey = new(StringComparer.Ordinal);
    private MaterialButton? _selectButton;
    private MaterialButton? _isolateButton;
    private MaterialButton? _isolateXrayButton;
    private string _filterText = string.Empty;
    // Consolidated column filters (consolidated kind only).
    private BomConsolidatedFilterEngine<BomPanelRow>? _filterEngine;
    private Context? _lastContext; // captured in CreateView for later header rebuilds
    private bool _disposed;

    public AndroidBomPanel(
        FaPackageQueryService queryService,
        Func<Scene?> sceneAccessor,
        AndroidBomPanelKind kind)
    {
        _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
        _sceneAccessor = sceneAccessor ?? throw new ArgumentNullException(nameof(sceneAccessor));
        _kind = kind;
        _columns = Columns(kind);
    }

    public Action<AndroidBomPanelTarget, AndroidBomPanelAction>? ActionRequested { get; set; }

    /// <summary>Raised when the host should run the apply sequence (dirty/warning + visibility + undo), then call back into the panel.</summary>
    public Action? ApplyFilterRequested { get; set; }

    public IReadOnlyList<string> AllPartKeys => _allRows.Select(r => r.PartKey).Distinct().ToList();

    public IReadOnlyCollection<string> PassingPartKeys()
        => _filterEngine is null
            ? AllPartKeys.ToHashSet(StringComparer.Ordinal)
            : _filterEngine.Apply(_allRows).Select(r => r.PartKey).ToHashSet(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotEngineUnchecked()
        => _filterEngine?.UncheckedByColumn() ?? new Dictionary<string, IReadOnlyList<string>>();

    public string? SortColumnKey => _filterEngine?.SortColumnKey;
    public bool SortDescending => _filterEngine?.SortDescending ?? false;

    public void RestoreEngineState(IReadOnlyDictionary<string, IReadOnlyList<string>> uncheckedByColumn, string? sortColumnKey, bool sortDescending)
    {
        if (_filterEngine is null) return;
        _filterEngine.RestoreUnchecked(uncheckedByColumn);
        _filterEngine.SetSortRaw(sortColumnKey, sortDescending);
        if (_lastContext is { } ctx) RebuildHeader(ctx);
        ApplyFilter();
    }

    public void RefreshAfterFilter(Context ctx) { RebuildHeader(ctx); ApplyFilter(); }

    public View CreateView(Context ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastContext = ctx;

        int pad = Dp(ctx, 14);
        _columnWidthsPx = DefaultColumnWidths(ctx);
        _tableWidthPx = _columnWidthsPx.Sum();

        var root = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
        };
        root.SetBackgroundResource(Resource.Color.fa_app_background);
        root.SetPadding(pad, pad, pad, pad);

        AddHeader(ctx, root);
        AddSearch(ctx, root);

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

        var tableScroll = new HorizontalScrollView(ctx)
        {
            FillViewport = true,
            HorizontalScrollBarEnabled = true,
            OverScrollMode = OverScrollMode.IfContentScrolls,
            Visibility = ViewStates.Gone,
        };
        tableScroll.Background = CreatePanelBackground(ctx);
        _tableScrollLayoutChangeHandler = (_, _) => RefreshColumnWidths(ctx);
        tableScroll.LayoutChange += _tableScrollLayoutChangeHandler;
        _tableScroll = tableScroll;

        var tableRoot = new LinearLayout(ctx)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(
                _tableWidthPx,
                ViewGroup.LayoutParams.MatchParent),
        };
        _tableRoot = tableRoot;

        _headerRow = CreateHeaderRow(ctx);
        _headerScrollTouchListener = new HorizontalTableScrollTouchListener(ctx, tableScroll);
        _headerRow.SetOnTouchListener(_headerScrollTouchListener);
        tableRoot.AddView(_headerRow, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(ctx, 34)));

        _adapter = new BomPanelAdapter(
            ctx,
            _visibleRows,
            _kind,
            () => _selectedRow,
            ToggleRow,
            () => _columnWidthsPx,
            () => _tableWidthPx);
        _list = new ListView(ctx)
        {
            Adapter = _adapter,
            ChoiceMode = ChoiceMode.Single,
            DividerHeight = 1,
            FastScrollEnabled = true,
        };
        _listScrollTouchListener = new HorizontalTableScrollTouchListener(ctx, tableScroll);
        _list.SetOnTouchListener(_listScrollTouchListener);
        _list.SetBackgroundColor(ColorRes(ctx, Resource.Color.fa_app_background));
        _list.ItemClick += OnListItemClick;
        tableRoot.AddView(_list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f));

        tableScroll.AddView(tableRoot);
        root.AddView(tableScroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f));

        AddActions(ctx, root);
        LoadRows(ctx);
        RefreshColumnWidths(ctx);
        ApplyFilter();

        if (_allRows.Count > 0)
        {
            _status.Visibility = ViewStates.Gone;
            tableScroll.Visibility = ViewStates.Visible;
        }
        else
        {
            tableScroll.Visibility = ViewStates.Gone;
        }

        tableScroll.Post(() => RefreshColumnWidths(ctx));
        tableScroll.PostDelayed(() => RefreshColumnWidths(ctx), 120);
        return root;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ActionRequested = null;

        if (_tableScroll is not null && _tableScrollLayoutChangeHandler is not null)
            _tableScroll.LayoutChange -= _tableScrollLayoutChangeHandler;
        _tableScrollLayoutChangeHandler = null;

        if (_search is not null)
            _search.TextChanged -= OnSearchTextChanged;

        if (_headerRow is not null)
            _headerRow.SetOnTouchListener(null);

        if (_list is not null)
        {
            _list.ItemClick -= OnListItemClick;
            _list.SetOnTouchListener(null);
        }

        _selectButton?.SetOnClickListener(null);
        _isolateButton?.SetOnClickListener(null);
        _isolateXrayButton?.SetOnClickListener(null);

        _headerScrollTouchListener?.Dispose();
        _listScrollTouchListener?.Dispose();
        _headerScrollTouchListener = null;
        _listScrollTouchListener = null;
    }

    private void AddHeader(Context ctx, LinearLayout root)
    {
        var title = new TextView(ctx)
        {
            Text = _kind == AndroidBomPanelKind.Hierarchy
                ? "Bill of Materials"
                : "BOM Consolidated",
        };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextSize(ComplexUnitType.Sp, 18f);
        title.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_primary));
        root.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));

        var subtitle = new TextView(ctx)
        {
            Text = _kind == AndroidBomPanelKind.Hierarchy
                ? "Hierarchical BOM from Bom.json"
                : "Flat part totals from Bom_Flatten.json",
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
        root.AddView(_summary, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
    }

    private void AddSearch(Context ctx, LinearLayout root)
    {
        _search = new EditText(ctx)
        {
            Hint = "Search BOM",
            ContentDescription = "Search BOM",
            ImeOptions = ImeAction.Done,
        };
        _search.SetTextSize(ComplexUnitType.Sp, 14f);
        _search.SetSingleLine(true);
        _search.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_primary));
        _search.SetHintTextColor(ColorRes(ctx, Resource.Color.fa_text_disabled));
        _search.Background = CreateInputBackground(ctx);
        _search.SetPadding(Dp(ctx, 12), 0, Dp(ctx, 12), 0);
        _search.TextChanged += OnSearchTextChanged;

        var lp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            Dp(ctx, 42));
        lp.SetMargins(0, Dp(ctx, 12), 0, Dp(ctx, 10));
        root.AddView(_search, lp);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_disposed)
            return;

        _filterText = e.Text?.ToString() ?? string.Empty;
        ApplyFilter();
    }

    private LinearLayout CreateHeaderRow(Context ctx)
    {
        var row = CreateRowContainer(ctx);
        row.SetBackgroundColor(ColorRes(ctx, Resource.Color.fa_surface_background));

        for (int i = 0; i < _columns.Length; i++)
        {
            int columnIndex = i;
            TextView cell = CreateCell(ctx, HeaderTitle(i), GetColumnWidth(i), bold: true);
            cell.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_secondary));
            ApplyHeaderChevron(cell, columnIndex);
            if (_kind == AndroidBomPanelKind.Consolidated)
            {
                cell.Clickable = true;
                cell.Focusable = true;
                cell.Click += (_, _) => OpenColumnFilter(ctx, columnIndex, cell);
            }
            row.AddView(cell);
        }

        return row;
    }

    private void RebuildHeader(Context ctx)
    {
        if (_headerRow is null)
            return;

        _headerRow.RemoveAllViews();
        for (int i = 0; i < _columns.Length; i++)
        {
            int columnIndex = i;
            TextView cell = CreateCell(ctx, HeaderTitle(i), GetColumnWidth(i), bold: true);
            cell.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_secondary));
            ApplyHeaderChevron(cell, columnIndex);
            if (_kind == AndroidBomPanelKind.Consolidated)
            {
                cell.Clickable = true;
                cell.Focusable = true;
                cell.Click += (_, _) => OpenColumnFilter(ctx, columnIndex, cell);
            }
            _headerRow.AddView(cell);
        }
    }

    private string HeaderTitle(int columnIndex)
    {
        ColumnSpec spec = _columns[columnIndex];
        if (_kind != AndroidBomPanelKind.Consolidated || _filterEngine is null)
            return spec.Title;
        bool active = _filterEngine.Column(spec.Key).IsActive;
        bool sorted = string.Equals(_filterEngine.SortColumnKey, spec.Key, StringComparison.Ordinal);
        string arrow = sorted ? (_filterEngine.SortDescending ? " ▼" : " ▲") : string.Empty;
        // Persistent filter affordance on EVERY consolidated header (Excel-style),
        // so every column visibly reads as filterable/sortable. The dot marks an
        // active filter; the arrow marks the (single) sort column.
        string filterGlyph = active ? " ●▾" : " ▾";
        return spec.Title + arrow + filterGlyph;
    }

    // Enlarges the filter/sort glyph (everything after the column title) so the
    // affordance reads clearly at a glance. Consolidated headers only.
    private void ApplyHeaderChevron(TextView cell, int columnIndex)
    {
        if (_kind != AndroidBomPanelKind.Consolidated || _filterEngine is null)
            return;
        string full = HeaderTitle(columnIndex);
        int titleLen = _columns[columnIndex].Title.Length;
        if (full.Length <= titleLen)
            return;
        var span = new global::Android.Text.SpannableString(full);
        span.SetSpan(
            new global::Android.Text.Style.RelativeSizeSpan(1.6f),
            titleLen,
            full.Length,
            global::Android.Text.SpanTypes.ExclusiveExclusive);
        cell.TextFormatted = span;
    }

    private void OpenColumnFilter(Context ctx, int columnIndex, View anchor)
    {
        if (_filterEngine is null) return;
        ColumnSpec spec = _columns[columnIndex];
        global::Android.Util.Log.Info("FA.BOM", $"Column filter opened: {spec.Key}. col={columnIndex}, anchor={anchor.GetHashCode()}.");
        var popup = new BomColumnFilterPopup(
            ctx,
            _filterEngine.Column(spec.Key),
            onSort: dir => _filterEngine.SetSort(spec.Key, dir),
            onApply: () => ApplyFilterRequested?.Invoke(),
            onChanged: () => { });
        popup.Show(anchor);
    }

    private void AddActions(Context ctx, LinearLayout root)
    {
        var actions = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
        };
        actions.SetGravity(GravityFlags.CenterVertical);
        actions.SetPadding(0, Dp(ctx, 10), 0, 0);

        _selectButton = CreateActionButton(ctx, "Select", AndroidBomPanelAction.Select);
        _isolateButton = CreateActionButton(ctx, "Isolate", AndroidBomPanelAction.Isolate);
        _isolateXrayButton = CreateActionButton(ctx, "X-Ray", AndroidBomPanelAction.IsolateXray);

        actions.AddView(_selectButton, ActionButtonLayout(ctx, first: true));
        actions.AddView(_isolateButton, ActionButtonLayout(ctx, first: false));
        actions.AddView(_isolateXrayButton, ActionButtonLayout(ctx, first: false));

        if (_kind == AndroidBomPanelKind.Consolidated)
        {
            var clearFiltersButton = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
            {
                Text = "Clear filters",
                ContentDescription = "Clear filters",
                InsetTop = 0,
                InsetBottom = 0,
            };
            clearFiltersButton.SetTextSize(ComplexUnitType.Sp, 12f);
            clearFiltersButton.SetAllCaps(false);
            clearFiltersButton.SetMinHeight(0);
            clearFiltersButton.SetPadding(Dp(ctx, 8), 0, Dp(ctx, 8), 0);
            clearFiltersButton.Click += (_, _) =>
            {
                _filterEngine?.ClearAll();
                ApplyFilterRequested?.Invoke();
            };
            actions.AddView(clearFiltersButton, ActionButtonLayout(ctx, first: false));
        }

        root.AddView(actions, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        UpdateActionButtons();
    }

    private MaterialButton CreateActionButton(Context ctx, string text, AndroidBomPanelAction action)
    {
        var button = new MaterialButton(ctx, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = text,
            ContentDescription = text,
            InsetTop = 0,
            InsetBottom = 0,
        };
        button.SetTextSize(ComplexUnitType.Sp, 12f);
        button.SetAllCaps(false);
        button.SetMinHeight(0);
        button.SetPadding(Dp(ctx, 8), 0, Dp(ctx, 8), 0);
        button.SetOnClickListener(new BomActionClickListener(this, action));
        return button;
    }

    private static LinearLayout.LayoutParams ActionButtonLayout(Context ctx, bool first)
    {
        var lp = new LinearLayout.LayoutParams(0, Dp(ctx, 36), 1f);
        if (!first)
            lp.SetMargins(Dp(ctx, 8), 0, 0, 0);
        return lp;
    }

    private void LoadRows(Context ctx)
    {
        _roots.Clear();
        _allRows.Clear();
        _visibleRows.Clear();
        _selectedRow = null;
        _selectedRowKey = null;
        _occurrenceCountByPartKey.Clear();
        _measuredColumnWidthsPx = null;

        Scene? scene = _sceneAccessor();
        if (scene?.PackageInfo is null
            || !string.Equals(scene.PackageInfo.SourceFormat, "fa", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(ctx, _kind == AndroidBomPanelKind.Hierarchy
                ? "Open an FA package to view its bill of materials."
                : "Open an FA package to view its consolidated bill of materials.");
            return;
        }

        if (string.IsNullOrWhiteSpace(scene.PackageInfo.CacheDatabasePath))
        {
            SetStatus(ctx, "BOM data is not available for the current package.");
            return;
        }

        try
        {
            if (_kind == AndroidBomPanelKind.Hierarchy)
                LoadHierarchyRows(scene);
            else
                LoadConsolidatedRows(scene);
        }
        catch (Exception ex)
        {
            SetStatus(ctx, "Failed to load BOM: " + ex.Message);
            return;
        }

        if (_allRows.Count == 0)
            SetStatus(ctx, "This package contains no BOM entries.");
        RebuildOccurrenceCounts();
    }

    private void LoadHierarchyRows(Scene scene)
    {
        IReadOnlyList<FaBomNodeRowResult> rows = _queryService.GetBomHierarchy(scene.PackageInfo!);
        IReadOnlyDictionary<string, string> names = _queryService.GetDefinitionAttribute(scene.PackageInfo!, "db_part_name");
        IReadOnlyDictionary<string, string> revNames = _queryService.GetDefinitionAttribute(scene.PackageInfo!, "rev_name");
        IReadOnlyDictionary<string, string> categories = _queryService.GetDefinitionAttribute(scene.PackageInfo!, "model_category");

        var byPath = new Dictionary<string, BomPanelRow>(rows.Count, StringComparer.Ordinal);
        foreach (FaBomNodeRowResult row in rows)
        {
            string partNumber = FirstNonEmpty(row.DisplayName, row.ComponentName, row.PartName, row.PartKey);
            names.TryGetValue(row.PartKey, out string? name);
            revNames.TryGetValue(row.PartKey, out string? revName);
            categories.TryGetValue(row.PartKey, out string? category);

            byPath[row.OccurrencePath] = new BomPanelRow(
                Kind: AndroidBomPanelKind.Hierarchy,
                Key: row.OccurrencePath,
                PartKey: row.PartKey,
                Level: row.Level,
                PartNumber: partNumber,
                Name: name ?? string.Empty,
                RevName: revName ?? string.Empty,
                ModelCategory: category ?? string.Empty,
                RevisionId: row.RevisionId,
                Quantity: FormatQuantity(row.QuantityValueText, row.QuantityUnits),
                OccurrenceCount: string.Empty,
                TotalQuantity: string.Empty,
                Units: row.QuantityUnits,
                QuantityTypes: row.QuantityType,
                ReferenceSets: row.ReferenceSet,
                SourceType: row.SourceType);
        }

        foreach (FaBomNodeRowResult row in rows)
        {
            BomPanelRow current = byPath[row.OccurrencePath];
            _allRows.Add(current);
            if (row.ParentOccurrencePath is not null
                && byPath.TryGetValue(row.ParentOccurrencePath, out BomPanelRow? parent))
            {
                current.Parent = parent;
                parent.Children.Add(current);
            }
            else
            {
                _roots.Add(current);
            }
        }
    }

    private void LoadConsolidatedRows(Scene scene)
    {
        IReadOnlyList<FaBomFlatRowResult> rows = _queryService.GetBomFlat(scene.PackageInfo!);
        IReadOnlyDictionary<string, string> names = _queryService.GetDefinitionAttribute(scene.PackageInfo!, "db_part_name");
        IReadOnlyDictionary<string, string> revNames = _queryService.GetDefinitionAttribute(scene.PackageInfo!, "rev_name");
        IReadOnlyDictionary<string, string> categories = _queryService.GetDefinitionAttribute(scene.PackageInfo!, "model_category");

        // The NX flatten (Bom_Flatten.json) lists an assembly's components but omits
        // the top-level/master assembly itself, so it is absent from the flat rows
        // above. Recover it from the hierarchy root(s) and surface it first, so the
        // consolidated table shows the master alongside its sub-assemblies (occurrence
        // count 1 — the master occurs once).
        var flatKeys = rows.Select(r => r.PartKey).ToHashSet(StringComparer.Ordinal);
        foreach (FaBomNodeRowResult master in BomConsolidatedMasterRow.MissingRoots(
                     _queryService.GetBomHierarchy(scene.PackageInfo!),
                     node => node.ParentOccurrencePath,
                     node => node.PartKey,
                     flatKeys))
        {
            string masterPartNumber = FirstNonEmpty(master.DisplayName, master.ComponentName, master.PartName, master.PartKey);
            names.TryGetValue(master.PartKey, out string? masterName);
            revNames.TryGetValue(master.PartKey, out string? masterRevName);
            categories.TryGetValue(master.PartKey, out string? masterCategory);

            _allRows.Add(new BomPanelRow(
                Kind: AndroidBomPanelKind.Consolidated,
                Key: master.PartKey,
                PartKey: master.PartKey,
                Level: 0,
                PartNumber: masterPartNumber,
                Name: masterName ?? string.Empty,
                RevName: masterRevName ?? string.Empty,
                ModelCategory: masterCategory ?? string.Empty,
                RevisionId: master.RevisionId,
                Quantity: string.Empty,
                OccurrenceCount: "1",
                TotalQuantity: string.Empty,
                Units: master.QuantityUnits,
                QuantityTypes: master.QuantityType,
                ReferenceSets: master.ReferenceSet,
                SourceType: master.SourceType));
        }

        foreach (FaBomFlatRowResult row in rows)
        {
            string partNumber = FirstNonEmpty(row.DisplayName, row.PartName, row.PartKey);
            names.TryGetValue(row.PartKey, out string? name);
            revNames.TryGetValue(row.PartKey, out string? revName);
            categories.TryGetValue(row.PartKey, out string? category);

            _allRows.Add(new BomPanelRow(
                Kind: AndroidBomPanelKind.Consolidated,
                Key: row.PartKey,
                PartKey: row.PartKey,
                Level: 0,
                PartNumber: partNumber,
                Name: name ?? string.Empty,
                RevName: revName ?? string.Empty,
                ModelCategory: category ?? string.Empty,
                RevisionId: row.RevisionId,
                Quantity: string.Empty,
                OccurrenceCount: row.OccurrenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TotalQuantity: FormatTotal(row.TotalQuantity),
                Units: row.QuantityUnits,
                QuantityTypes: row.QuantityTypesCsv,
                ReferenceSets: row.ReferenceSetsCsv,
                SourceType: row.SourceType));
        }

        _roots.AddRange(_allRows);
        _filterEngine = new BomConsolidatedFilterEngine<BomPanelRow>(
            _columns.Select(c => c.Key).ToList(),
            (row, key) => CellText(row, key));
        _filterEngine.RebuildValueLists(_allRows);
    }

    private void ApplyFilter()
    {
        _visibleRows.Clear();
        string filter = _filterText.Trim();

        if (_kind == AndroidBomPanelKind.Hierarchy)
        {
            foreach (BomPanelRow root in _roots)
            {
                if (filter.Length == 0)
                    AppendExpanded(root);
                else
                    AppendFiltered(root, filter);
            }
        }
        else
        {
            IReadOnlyList<BomPanelRow> rows = _filterEngine?.Apply(_allRows) ?? _allRows;
            foreach (BomPanelRow row in rows)
                if (filter.Length == 0 || row.Matches(filter))
                    _visibleRows.Add(row);
            global::Android.Util.Log.Info("FA.BOM",
                $"Consolidated filter: {_visibleRows.Count}/{_allRows.Count} rows, sort={_filterEngine?.SortColumnKey ?? "none"}/{((_filterEngine?.SortDescending ?? false) ? "desc" : "asc")}, anyActive={_filterEngine?.AnyActive ?? false}.");
        }

        _selectedRow = ResolveSelectedRow();
        if (_selectedRowKey is not null && _selectedRow is null)
        {
            _selectedRowKey = null;
            if (_status is not null)
                _status.Text = "Selection hidden by the current filter.";
        }

        _adapter?.NotifyDataSetChanged();
        UpdateSummary();
        UpdateActionButtons();
    }

    private void AppendExpanded(BomPanelRow row)
    {
        _visibleRows.Add(row);
        if (!row.IsExpanded)
            return;

        foreach (BomPanelRow child in row.Children)
            AppendExpanded(child);
    }

    private bool AppendFiltered(BomPanelRow row, string filter)
    {
        bool rowMatches = row.Matches(filter);
        bool childMatches = false;
        int insertAt = _visibleRows.Count;
        _visibleRows.Add(row);

        foreach (BomPanelRow child in row.Children)
        {
            if (AppendFiltered(child, filter))
                childMatches = true;
        }

        if (!rowMatches && !childMatches)
        {
            _visibleRows.RemoveRange(insertAt, _visibleRows.Count - insertAt);
            return false;
        }

        return true;
    }

    private void ToggleRow(BomPanelRow row)
    {
        if (_disposed)
            return;

        if (row.Children.Count == 0)
            return;

        row.IsExpanded = !row.IsExpanded;
        ApplyFilter();
    }

    private void RequestAction(AndroidBomPanelAction action)
    {
        if (_disposed)
            return;

        BomPanelRow? selectedRow = ResolveSelectedRow();
        if (selectedRow is null)
            return;

        ActionRequested?.Invoke(new AndroidBomPanelTarget(
            selectedRow.Kind,
            selectedRow.Key,
            selectedRow.PartNumber,
            selectedRow.PartKey,
            GetPartOrdinal(selectedRow),
            GetPartOccurrenceCount(selectedRow)), action);
    }

    private BomPanelRow? ResolveSelectedRow()
        => _selectedRowKey is null
            ? null
            : _visibleRows.FirstOrDefault(row => string.Equals(RowIdentity(row), _selectedRowKey, StringComparison.Ordinal));

    private static string RowIdentity(BomPanelRow row)
        => $"{row.Kind}\u001F{row.Key}\u001F{row.PartKey}";

    private int GetPartOrdinal(BomPanelRow row)
    {
        if (row.Kind != AndroidBomPanelKind.Hierarchy || string.IsNullOrWhiteSpace(row.PartKey))
            return -1;

        int ordinal = 0;
        foreach (BomPanelRow candidate in _allRows
                     .Where(candidate => candidate.Kind == AndroidBomPanelKind.Hierarchy
                                         && string.Equals(candidate.PartKey, row.PartKey, StringComparison.Ordinal))
                     .OrderBy(candidate => candidate.Key, AndroidPathComparer.Instance))
        {
            if (ReferenceEquals(candidate, row))
                return ordinal;
            ordinal++;
        }

        return -1;
    }

    private int GetPartOccurrenceCount(BomPanelRow row)
        => row.Kind == AndroidBomPanelKind.Hierarchy
           && !string.IsNullOrWhiteSpace(row.PartKey)
           && _occurrenceCountByPartKey.TryGetValue(row.PartKey, out int count)
            ? count
            : 0;

    private void UpdateSummary()
    {
        if (_summary is null)
            return;

        int total = _allRows.Count;
        int visible = _visibleRows.Count;
        string noun = _kind == AndroidBomPanelKind.Hierarchy ? "occurrence" : "part";
        _summary.Text = total == 0
            ? "No BOM data"
            : visible == total
                ? $"{total} {noun}{(total == 1 ? string.Empty : "s")}"
                : $"{visible} of {total} {noun}{(total == 1 ? string.Empty : "s")}";
    }

    private void UpdateActionButtons()
    {
        bool enabled = _selectedRow is not null;
        SetActionEnabled(_selectButton, enabled);
        SetActionEnabled(_isolateButton, enabled);
        SetActionEnabled(_isolateXrayButton, enabled);
    }

    private int[] DefaultColumnWidths(Context ctx)
        => _columns.Select(column => Dp(ctx, column.MinWidthDp)).ToArray();

    private int GetColumnWidth(int index)
        => index >= 0 && index < _columnWidthsPx.Length
            ? _columnWidthsPx[index]
            : Dp(_tableRoot?.Context ?? _tableScroll?.Context ?? throw new InvalidOperationException("BOM table context is unavailable."), _columns[index].MinWidthDp);

    private void RefreshColumnWidths(Context ctx)
    {
        if (_disposed || _tableRoot is null || _columns.Length == 0)
            return;

        int viewportWidth = _tableScroll?.Width ?? 0;
        if (viewportWidth <= 0)
            viewportWidth = _tableRoot.Width;
        if (viewportWidth <= 0)
            viewportWidth = DefaultColumnWidths(ctx).Sum();

        int[] widths = MeasureColumnWidths(ctx);
        int measuredTotal = widths.Sum();
        if (measuredTotal < viewportWidth)
            ExpandColumnsToFill(widths, viewportWidth - measuredTotal);

        int tableWidth = Math.Max(widths.Sum(), viewportWidth);
        if (_tableWidthPx == tableWidth && SameWidths(_columnWidthsPx, widths))
        {
            ClampHorizontalScroll(tableWidth, viewportWidth);
            return;
        }

        _columnWidthsPx = widths;
        _tableWidthPx = tableWidth;

        if (_tableRoot.LayoutParameters is ViewGroup.LayoutParams lp)
        {
            lp.Width = tableWidth;
            _tableRoot.LayoutParameters = lp;
        }
        _tableRoot.SetMinimumWidth(tableWidth);
        _list?.SetMinimumWidth(tableWidth);

        ClampHorizontalScroll(tableWidth, viewportWidth);

        RebuildHeader(ctx);
        _adapter?.NotifyDataSetChanged();
    }

    private int[] MeasureColumnWidths(Context ctx)
    {
        float density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
        if (_measuredColumnWidthsPx is not null && Math.Abs(_measuredColumnWidthsDensity - density) < 0.001f)
            return (int[])_measuredColumnWidthsPx.Clone();

        int[] widths = DefaultColumnWidths(ctx);
        using var paint = new Paint(PaintFlags.AntiAlias)
        {
            TextSize = Dp(ctx, 12),
        };

        int cellPadding = Dp(ctx, 20);
        for (int i = 0; i < _columns.Length; i++)
            widths[i] = Math.Max(widths[i], MeasureTextWidth(paint, _columns[i].Title, cellPadding));

        foreach (BomPanelRow row in _allRows)
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                int padding = cellPadding;
                if (_kind == AndroidBomPanelKind.Hierarchy && string.Equals(_columns[i].Key, ColumnKeyPart, StringComparison.Ordinal))
                    padding += Dp(ctx, row.Level * 18 + 30);
                widths[i] = Math.Max(widths[i], MeasureTextWidth(paint, CellText(row, _columns[i].Key), padding));
            }
        }

        for (int i = 0; i < _columns.Length; i++)
        {
            int maxWidth = Dp(ctx, _columns[i].MaxWidthDp);
            if (maxWidth > 0)
                widths[i] = Math.Min(widths[i], maxWidth);
        }

        _measuredColumnWidthsPx = (int[])widths.Clone();
        _measuredColumnWidthsDensity = density;
        return widths;
    }

    private void RebuildOccurrenceCounts()
    {
        _occurrenceCountByPartKey.Clear();
        foreach (BomPanelRow row in _allRows)
        {
            if (row.Kind != AndroidBomPanelKind.Hierarchy || string.IsNullOrWhiteSpace(row.PartKey))
                continue;

            _occurrenceCountByPartKey.TryGetValue(row.PartKey, out int count);
            _occurrenceCountByPartKey[row.PartKey] = count + 1;
        }
    }

    private static int MeasureTextWidth(Paint paint, string text, int padding)
        => (int)MathF.Ceiling(paint.MeasureText(text ?? string.Empty)) + padding;

    private static void ExpandColumnsToFill(int[] widths, int extra)
    {
        if (extra <= 0 || widths.Length == 0)
            return;

        int originalTotal = widths.Sum();
        if (originalTotal <= 0)
        {
            widths[^1] += extra;
            return;
        }

        int remaining = extra;
        if (remaining >= widths.Length)
        {
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i]++;
                remaining--;
            }
        }

        int distributed = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            int add = remaining * widths[i] / originalTotal;
            widths[i] += add;
            distributed += add;
        }

        int remainder = remaining - distributed;
        for (int i = 0; i < widths.Length && remainder > 0; i++)
        {
            widths[i]++;
            remainder--;
        }
    }

    private void ClampHorizontalScroll(int tableWidth, int viewportWidth)
    {
        if (_tableScroll is null)
            return;

        int maxScrollX = Math.Max(0, tableWidth - viewportWidth);
        _tableScroll.HorizontalScrollBarEnabled = maxScrollX > 0;
        int targetX = Math.Clamp(_tableScroll.ScrollX, 0, maxScrollX);
        if (_tableScroll.ScrollX != targetX)
            _tableScroll.ScrollTo(targetX, _tableScroll.ScrollY);
    }

    private static bool SameWidths(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
                return false;
        }
        return true;
    }

    private static void SetActionEnabled(MaterialButton? button, bool enabled)
    {
        if (button is null)
            return;
        button.Enabled = enabled;
        button.Alpha = enabled ? 1f : 0.55f;
    }

    private void SetStatus(Context ctx, string message)
    {
        if (_status is not null)
            _status.Text = message;
        if (_summary is not null)
            _summary.Text = "No BOM data";
    }

    private static LinearLayout CreateRowContainer(Context ctx)
    {
        var row = new LinearLayout(ctx)
        {
            Orientation = Orientation.Horizontal,
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(0, 0, 0, 0);
        return row;
    }

    private static TextView CreateCell(Context ctx, string text, int widthPx, bool bold = false)
    {
        var cell = new TextView(ctx)
        {
            Text = text,
            Gravity = GravityFlags.CenterVertical,
            Ellipsize = TextUtils.TruncateAt.End,
        };
        cell.SetTextSize(ComplexUnitType.Sp, 12f);
        cell.SetSingleLine(true);
        if (bold)
            cell.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        cell.SetPadding(Dp(ctx, 8), 0, Dp(ctx, 8), 0);
        cell.SetTextColor(ColorRes(ctx, Resource.Color.fa_text_primary));
        cell.LayoutParameters = new LinearLayout.LayoutParams(
            widthPx,
            ViewGroup.LayoutParams.MatchParent);
        return cell;
    }

    private static ColumnSpec[] Columns(AndroidBomPanelKind kind)
        => kind == AndroidBomPanelKind.Hierarchy
            ?
            [
                new(ColumnKeyLevel, "Level", TinyColumnMinWidthDp, 56),
                new(ColumnKeyPart, "Part", TinyColumnMinWidthDp, 260),
                new(ColumnKeyName, "Name", TinyColumnMinWidthDp, 260),
                new(ColumnKeyRevName, "Rev Name", TinyColumnMinWidthDp, 240),
                new(ColumnKeyModelCategory, "Model Category", TinyColumnMinWidthDp, 150),
                new(ColumnKeyRevision, "Rev", TinyColumnMinWidthDp, 58),
                new(ColumnKeyQuantity, "Quantity", TinyColumnMinWidthDp, 86),
                new(ColumnKeyReferenceSets, "Reference Set", TinyColumnMinWidthDp, 150),
                new(ColumnKeySource, "Source", TinyColumnMinWidthDp, 96),
            ]
            :
            [
                new(ColumnKeyPart, "Part", TinyColumnMinWidthDp, 230),
                new(ColumnKeyName, "Name", TinyColumnMinWidthDp, 260),
                new(ColumnKeyRevName, "Rev Name", TinyColumnMinWidthDp, 240),
                new(ColumnKeyModelCategory, "Model Category", TinyColumnMinWidthDp, 150),
                new(ColumnKeyRevision, "Rev", TinyColumnMinWidthDp, 58),
                new(ColumnKeyOccurrenceCount, "Qty", TinyColumnMinWidthDp, 58),
                new(ColumnKeyUnits, "Units", TinyColumnMinWidthDp, 86),
                new(ColumnKeySource, "Source", TinyColumnMinWidthDp, 96),
            ];

    private static string CellText(BomPanelRow row, string columnKey)
        => columnKey switch
        {
            ColumnKeyLevel => row.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ColumnKeyPart => row.PartNumber,
            ColumnKeyName => row.Name,
            ColumnKeyRevName => row.RevName,
            ColumnKeyModelCategory => row.ModelCategory,
            ColumnKeyRevision => row.RevisionId,
            ColumnKeyQuantity => row.Quantity,
            ColumnKeyOccurrenceCount => row.OccurrenceCount,
            ColumnKeyTotalQuantity => row.TotalQuantity,
            ColumnKeyUnits => row.Units,
            ColumnKeyQuantityTypes => row.QuantityTypes,
            ColumnKeyReferenceSets => row.ReferenceSets,
            ColumnKeySource => row.SourceType,
            _ => string.Empty,
        };

    private const string ColumnKeyLevel = "level";
    private const string ColumnKeyPart = "part";
    private const string ColumnKeyName = "name";
    private const string ColumnKeyRevName = "revName";
    private const string ColumnKeyModelCategory = "modelCategory";
    private const string ColumnKeyRevision = "revision";
    private const string ColumnKeyQuantity = "quantity";
    private const string ColumnKeyOccurrenceCount = "occurrenceCount";
    private const string ColumnKeyTotalQuantity = "totalQuantity";
    private const string ColumnKeyUnits = "units";
    private const string ColumnKeyQuantityTypes = "quantityTypes";
    private const string ColumnKeyReferenceSets = "referenceSets";
    private const string ColumnKeySource = "source";
    private const int TinyColumnMinWidthDp = 8;

    private static GradientDrawable CreateInputBackground(Context ctx)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(ColorRes(ctx, Resource.Color.fa_surface_background));
        drawable.SetCornerRadius(Dp(ctx, 6));
        drawable.SetStroke(Dp(ctx, 1), ColorRes(ctx, Resource.Color.fa_border));
        return drawable;
    }

    private static GradientDrawable CreatePanelBackground(Context ctx)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(ColorRes(ctx, Resource.Color.fa_app_background));
        drawable.SetCornerRadius(Dp(ctx, 6));
        drawable.SetStroke(Dp(ctx, 1), ColorRes(ctx, Resource.Color.fa_border));
        return drawable;
    }

    private static void HideKeyboard(Context ctx)
    {
        if (ctx.GetSystemService(Context.InputMethodService) is InputMethodManager input)
            input.HideSoftInputFromWindow((ctx as global::Android.App.Activity)?.Window?.DecorView?.WindowToken, HideSoftInputFlags.None);
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FormatQuantity(string text, string units)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.IsNullOrWhiteSpace(units) ? text : $"{text} {units}";

    private static string FormatTotal(double? value)
        => value is null
            ? string.Empty
            : value.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static Color ColorRes(Context ctx, int resId)
        => new(global::AndroidX.Core.Content.ContextCompat.GetColor(ctx, resId));

    private static int Dp(Context ctx, float value)
        => (int)MathF.Round(value * (ctx.Resources?.DisplayMetrics?.Density ?? 1f));

    private void OnListItemClick(object? sender, AdapterView.ItemClickEventArgs e)
    {
        if (_disposed || e.Position < 0 || e.Position >= _visibleRows.Count)
            return;

        BomPanelRow row = _visibleRows[e.Position];
        _selectedRow = row;
        _selectedRowKey = RowIdentity(row);
        _adapter?.NotifyDataSetChanged();
        UpdateActionButtons();

        if (_list?.Context is { } ctx)
            HideKeyboard(ctx);
    }

    private readonly record struct ColumnSpec(string Key, string Title, int MinWidthDp, int MaxWidthDp);

    private sealed class BomActionClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly AndroidBomPanel _owner;
        private readonly AndroidBomPanelAction _action;

        public BomActionClickListener(AndroidBomPanel owner, AndroidBomPanelAction action)
        {
            _owner = owner;
            _action = action;
        }

        public void OnClick(View? v) => _owner.RequestAction(_action);
    }

    private sealed class BomPanelRow
    {
        public BomPanelRow(
            AndroidBomPanelKind Kind,
            string Key,
            string PartKey,
            int Level,
            string PartNumber,
            string Name,
            string RevName,
            string ModelCategory,
            string RevisionId,
            string Quantity,
            string OccurrenceCount,
            string TotalQuantity,
            string Units,
            string QuantityTypes,
            string ReferenceSets,
            string SourceType)
        {
            this.Kind = Kind;
            this.Key = Key;
            this.PartKey = PartKey;
            this.Level = Level;
            this.PartNumber = PartNumber;
            this.Name = Name;
            this.RevName = RevName;
            this.ModelCategory = ModelCategory;
            this.RevisionId = RevisionId;
            this.Quantity = Quantity;
            this.OccurrenceCount = OccurrenceCount;
            this.TotalQuantity = TotalQuantity;
            this.Units = Units;
            this.QuantityTypes = QuantityTypes;
            this.ReferenceSets = ReferenceSets;
            this.SourceType = SourceType;
        }

        public AndroidBomPanelKind Kind { get; }
        public string Key { get; }
        public string PartKey { get; }
        public int Level { get; }
        public string PartNumber { get; }
        public string Name { get; }
        public string RevName { get; }
        public string ModelCategory { get; }
        public string RevisionId { get; }
        public string Quantity { get; }
        public string OccurrenceCount { get; }
        public string TotalQuantity { get; }
        public string Units { get; }
        public string QuantityTypes { get; }
        public string ReferenceSets { get; }
        public string SourceType { get; }
        public bool IsExpanded { get; set; }
        public BomPanelRow? Parent { get; set; }
        public List<BomPanelRow> Children { get; } = new();

        public bool Matches(string filter)
            => Contains(PartNumber, filter)
               || Contains(Name, filter)
               || Contains(RevName, filter)
               || Contains(ModelCategory, filter)
               || Contains(RevisionId, filter)
               || Contains(Quantity, filter)
               || Contains(OccurrenceCount, filter)
               || Contains(TotalQuantity, filter)
               || Contains(Units, filter)
               || Contains(QuantityTypes, filter)
               || Contains(ReferenceSets, filter)
               || Contains(SourceType, filter);

        private static bool Contains(string value, string filter)
            => value.Length > 0 && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BomPanelAdapter : BaseAdapter<BomPanelRow>
    {
        private readonly Context _ctx;
        private readonly IReadOnlyList<BomPanelRow> _rows;
        private readonly AndroidBomPanelKind _kind;
        private readonly Func<BomPanelRow?> _selectedAccessor;
        private readonly Action<BomPanelRow> _toggle;
        private readonly Func<int[]> _columnWidthsAccessor;
        private readonly Func<int> _tableWidthAccessor;

        public BomPanelAdapter(
            Context ctx,
            IReadOnlyList<BomPanelRow> rows,
            AndroidBomPanelKind kind,
            Func<BomPanelRow?> selectedAccessor,
            Action<BomPanelRow> toggle,
            Func<int[]> columnWidthsAccessor,
            Func<int> tableWidthAccessor)
        {
            _ctx = ctx;
            _rows = rows;
            _kind = kind;
            _selectedAccessor = selectedAccessor;
            _toggle = toggle;
            _columnWidthsAccessor = columnWidthsAccessor;
            _tableWidthAccessor = tableWidthAccessor;
        }

        public override int Count => _rows.Count;

        public override BomPanelRow this[int position] => _rows[position];

        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            BomPanelRow row = _rows[position];
            var root = CreateRowContainer(_ctx);
            // S15-F10: wrap the row height (with a 24dp floor) so 12sp cell text
            // grows the row at large system font scales instead of clipping inside
            // a fixed 24dp box (sp scales with the user's font size, dp does not).
            root.LayoutParameters = new AbsListView.LayoutParams(
                _tableWidthAccessor(),
                ViewGroup.LayoutParams.WrapContent);
            root.SetMinimumWidth(_tableWidthAccessor());
            root.SetMinimumHeight(Dp(_ctx, 24));
            root.ContentDescription = "Select BOM row " + RowTooltip(row);
            bool selected = ReferenceEquals(row, _selectedAccessor());
            root.SetBackgroundColor(selected
                ? ColorRes(_ctx, Resource.Color.fa_highlight)
                : position % 2 == 0
                    ? ColorRes(_ctx, Resource.Color.fa_app_background)
                    : ColorRes(_ctx, Resource.Color.fa_surface_background));

            if (_kind == AndroidBomPanelKind.Hierarchy)
                AddHierarchyCells(root, row);
            else
                AddConsolidatedCells(root, row);

            return root;
        }

        private static string RowTooltip(BomPanelRow row)
            => string.IsNullOrWhiteSpace(row.PartNumber)
                ? row.Name
                : string.IsNullOrWhiteSpace(row.Name)
                    ? row.PartNumber
                    : row.PartNumber + " - " + row.Name;

        private void AddHierarchyCells(LinearLayout root, BomPanelRow row)
        {
            int[] widths = _columnWidthsAccessor();
            root.AddView(CreateCell(_ctx, row.Level.ToString(System.Globalization.CultureInfo.InvariantCulture), WidthAt(widths, 0)));
            root.AddView(CreateHierarchyPartCell(row, WidthAt(widths, 1)));
            root.AddView(CreateCell(_ctx, row.Name, WidthAt(widths, 2)));
            root.AddView(CreateCell(_ctx, row.RevName, WidthAt(widths, 3)));
            root.AddView(CreateCell(_ctx, row.ModelCategory, WidthAt(widths, 4)));
            root.AddView(CreateCell(_ctx, row.RevisionId, WidthAt(widths, 5)));
            root.AddView(CreateCell(_ctx, row.Quantity, WidthAt(widths, 6)));
            root.AddView(CreateCell(_ctx, row.ReferenceSets, WidthAt(widths, 7)));
            root.AddView(CreateCell(_ctx, row.SourceType, WidthAt(widths, 8)));
        }

        private void AddConsolidatedCells(LinearLayout root, BomPanelRow row)
        {
            int[] widths = _columnWidthsAccessor();
            root.AddView(CreateCell(_ctx, row.PartNumber, WidthAt(widths, 0)));
            root.AddView(CreateCell(_ctx, row.Name, WidthAt(widths, 1)));
            root.AddView(CreateCell(_ctx, row.RevName, WidthAt(widths, 2)));
            root.AddView(CreateCell(_ctx, row.ModelCategory, WidthAt(widths, 3)));
            root.AddView(CreateCell(_ctx, row.RevisionId, WidthAt(widths, 4)));
            root.AddView(CreateCell(_ctx, row.OccurrenceCount, WidthAt(widths, 5)));
            root.AddView(CreateCell(_ctx, row.Units, WidthAt(widths, 6)));
            root.AddView(CreateCell(_ctx, row.SourceType, WidthAt(widths, 7)));
        }

        private View CreateHierarchyPartCell(BomPanelRow row, int widthPx)
        {
            var container = new LinearLayout(_ctx)
            {
                Orientation = Orientation.Horizontal,
            };
            container.SetGravity(GravityFlags.CenterVertical);
            container.SetPadding(Dp(_ctx, 8 + row.Level * 18), 0, Dp(_ctx, 8), 0);
            container.LayoutParameters = new LinearLayout.LayoutParams(
                widthPx,
                ViewGroup.LayoutParams.MatchParent);

            int disclosureWidth = Dp(_ctx, 24);
            if (row.Children.Count > 0)
            {
                var disclosure = new TextView(_ctx)
                {
                    Text = row.IsExpanded ? "-" : "+",
                    Gravity = GravityFlags.Center,
                    ContentDescription = row.IsExpanded ? "Collapse BOM row" : "Expand BOM row",
                    Clickable = true,
                    Focusable = true,
                };
                disclosure.SetTextSize(ComplexUnitType.Sp, 14f);
                disclosure.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
                disclosure.SetTextColor(ColorRes(_ctx, Resource.Color.fa_text_secondary));
                disclosure.SetOnClickListener(new RowToggleClickListener(row, _toggle));
                container.AddView(disclosure, new LinearLayout.LayoutParams(
                    disclosureWidth,
                    ViewGroup.LayoutParams.MatchParent));
            }
            else
            {
                container.AddView(new Space(_ctx), new LinearLayout.LayoutParams(
                    disclosureWidth,
                    ViewGroup.LayoutParams.MatchParent));
            }

            TextView part = CreateCell(_ctx, row.PartNumber, 0);
            part.SetPadding(Dp(_ctx, 4), 0, 0, 0);
            part.LayoutParameters = new LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.MatchParent,
                1f);
            container.AddView(part);
            return container;
        }

        private static int WidthAt(IReadOnlyList<int> widths, int index)
            => index >= 0 && index < widths.Count ? widths[index] : 80;

        private sealed class RowToggleClickListener : Java.Lang.Object, View.IOnClickListener
        {
            private readonly BomPanelRow _row;
            private readonly Action<BomPanelRow> _toggle;

            public RowToggleClickListener(BomPanelRow row, Action<BomPanelRow> toggle)
            {
                _row = row;
                _toggle = toggle;
            }

            public void OnClick(View? v) => _toggle(_row);
        }
    }

    private sealed class HorizontalTableScrollTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly HorizontalScrollView _scrollView;
        private readonly int _slopPx;
        private float _downX;
        private float _downY;
        private int _startScrollX;
        private int _activePointerId = -1;
        private bool _dragging;

        public HorizontalTableScrollTouchListener(Context ctx, HorizontalScrollView scrollView)
        {
            _scrollView = scrollView;
            _slopPx = ViewConfiguration.Get(ctx)?.ScaledTouchSlop ?? 8;
        }

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (e is null)
                return false;

            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                {
                    int pointerIndex = AndroidMotionEvents.PreferredPointerIndex(e);
                    if (pointerIndex < 0)
                        return false;

                    _activePointerId = e.GetPointerId(pointerIndex);
                    _downX = AndroidMotionEvents.RawX(e, pointerIndex);
                    _downY = AndroidMotionEvents.RawY(e, pointerIndex);
                    _startScrollX = _scrollView.ScrollX;
                    _dragging = false;
                    return false;
                }

                case MotionEventActions.PointerDown:
                {
                    int pointerIndex = e.ActionIndex;
                    if (!AndroidMotionEvents.IsPointerStylusOrEraser(e, pointerIndex))
                        return _dragging;

                    _activePointerId = e.GetPointerId(pointerIndex);
                    _downX = AndroidMotionEvents.RawX(e, pointerIndex);
                    _downY = AndroidMotionEvents.RawY(e, pointerIndex);
                    _startScrollX = _scrollView.ScrollX;
                    return _dragging;
                }

                case MotionEventActions.Move:
                {
                    if (!AndroidMotionEvents.TryFindPointerIndex(e, _activePointerId, out int pointerIndex))
                        return _dragging;

                    float rawX = AndroidMotionEvents.RawX(e, pointerIndex);
                    float rawY = AndroidMotionEvents.RawY(e, pointerIndex);
                    float dx = rawX - _downX;
                    float dy = rawY - _downY;
                    if (!_dragging
                        && Math.Abs(dx) > _slopPx
                        && Math.Abs(dx) > Math.Abs(dy) * 1.15f)
                    {
                        _dragging = true;
                        v?.Parent?.RequestDisallowInterceptTouchEvent(true);
                    }

                    if (!_dragging)
                        return false;

                    int maxScrollX = Math.Max(0, (_scrollView.GetChildAt(0)?.Width ?? 0) - _scrollView.Width);
                    int targetX = Math.Clamp(_startScrollX - (int)Math.Round(dx), 0, maxScrollX);
                    _scrollView.ScrollTo(targetX, _scrollView.ScrollY);
                    return true;
                }

                case MotionEventActions.PointerUp:
                {
                    int actionIndex = e.ActionIndex;
                    if (actionIndex >= 0
                        && actionIndex < e.PointerCount
                        && e.GetPointerId(actionIndex) == _activePointerId)
                    {
                        bool wasDragging = _dragging;
                        _activePointerId = -1;
                        _dragging = false;
                        v?.Parent?.RequestDisallowInterceptTouchEvent(false);
                        return wasDragging;
                    }

                    return _dragging;
                }

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (_dragging)
                    {
                        _dragging = false;
                        _activePointerId = -1;
                        v?.Parent?.RequestDisallowInterceptTouchEvent(false);
                        return true;
                    }
                    _activePointerId = -1;
                    return false;

                default:
                    return false;
            }
        }
    }
}
