using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// Aggregates per-column <see cref="BomColumnFilterModel"/>s for the consolidated
/// BOM and produces the filtered + sorted row list. Generic over the row type with
/// an injected cell accessor so it stays framework-free and host-testable.
/// </summary>
public sealed class BomConsolidatedFilterEngine<TRow>
{
    private readonly Func<TRow, string, string> _cell;
    private readonly Dictionary<string, BomColumnFilterModel> _columns;
    private readonly List<string> _columnKeys;
    private readonly HashSet<string> _numericColumns;

    public BomConsolidatedFilterEngine(
        IReadOnlyList<string> filterableColumnKeys,
        Func<TRow, string, string> cell,
        IReadOnlyCollection<string>? numericColumnKeys = null)
    {
        _cell = cell ?? throw new ArgumentNullException(nameof(cell));
        _columnKeys = filterableColumnKeys.ToList();
        _columns = _columnKeys.ToDictionary(k => k, k => new BomColumnFilterModel(k), StringComparer.Ordinal);
        _numericColumns = new HashSet<string>(numericColumnKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    public IReadOnlyList<string> ColumnKeys => _columnKeys;
    public BomColumnFilterModel Column(string key) => _columns[key];
    public bool AnyActive => _columns.Values.Any(c => c.IsActive);

    public string? SortColumnKey { get; private set; }
    public bool SortDescending { get; private set; }

    public void SetSort(string columnKey, SortDirection direction)
    {
        SortColumnKey = columnKey;
        SortDescending = direction == SortDirection.Descending;
    }

    public void ClearSort()
    {
        SortColumnKey = null;
        SortDescending = false;
    }

    public void SetSortRaw(string? columnKey, bool descending)
    {
        SortColumnKey = columnKey;
        SortDescending = descending;
    }

    public void RebuildValueLists(IReadOnlyList<TRow> allRows)
    {
        foreach (string key in _columnKeys)
            _columns[key].SetValues(allRows.Select(r => _cell(r, key)));
    }

    public void ClearAll()
    {
        foreach (BomColumnFilterModel c in _columns.Values) c.SelectAll();
        ClearSort();
    }

    /// <summary>Filtered (AND across columns) then stably sorted by the active sort.</summary>
    public IReadOnlyList<TRow> Apply(IReadOnlyList<TRow> allRows)
    {
        IEnumerable<TRow> filtered = allRows.Where(Passes);
        if (SortColumnKey is not { } sortKey)
            return filtered.ToList();

        // Numeric columns (e.g. occurrence count) must sort by value, not as text,
        // so "10" comes after "2" rather than before it.
        IComparer<string> comparer = _numericColumns.Contains(sortKey)
            ? NumericThenTextComparer.Instance
            : StringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<TRow> ordered = SortDescending
            ? filtered.OrderByDescending(r => _cell(r, sortKey), comparer)
            : filtered.OrderBy(r => _cell(r, sortKey), comparer);
        return ordered.ToList();
    }

    public bool Passes(TRow row)
    {
        foreach (string key in _columnKeys)
            if (!_columns[key].Allows(_cell(row, key)))
                return false;
        return true;
    }

    // -- snapshot bridge (undo) --
    public IReadOnlyDictionary<string, IReadOnlyList<string>> UncheckedByColumn()
        => _columnKeys.ToDictionary(
            k => k,
            k => (IReadOnlyList<string>)_columns[k].UncheckedValues.ToList(),
            StringComparer.Ordinal);

    public void RestoreUnchecked(IReadOnlyDictionary<string, IReadOnlyList<string>> uncheckedByColumn)
    {
        foreach (string key in _columnKeys)
            _columns[key].RestoreUnchecked(
                uncheckedByColumn.TryGetValue(key, out IReadOnlyList<string>? v) ? v : Array.Empty<string>());
    }

    /// <summary>
    /// Orders parseable numbers by value, then all non-numeric values (including
    /// blanks) after them by ordinal text. A total order, so it is stable under
    /// OrderBy and safe for mixed columns.
    /// </summary>
    private sealed class NumericThenTextComparer : IComparer<string>
    {
        public static readonly NumericThenTextComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            bool xn = double.TryParse(x, NumberStyles.Any, CultureInfo.InvariantCulture, out double dx);
            bool yn = double.TryParse(y, NumberStyles.Any, CultureInfo.InvariantCulture, out double dy);
            if (xn && yn) return dx.CompareTo(dy);
            if (xn) return -1;
            if (yn) return 1;
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}
