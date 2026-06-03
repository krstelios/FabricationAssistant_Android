using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// One consolidated-BOM column's filter: a checklist of the column's distinct
/// values. <see cref="Allows"/> uses an O(1) unchecked-set cache so filtering a
/// large BOM is cheap. Framework-free and host-testable. Values compare
/// case-insensitively (OrdinalIgnoreCase).
/// </summary>
public sealed class BomColumnFilterModel
{
    private readonly List<BomColumnFilterValue> _values = new();
    private readonly HashSet<string> _unchecked = new(StringComparer.OrdinalIgnoreCase);

    public BomColumnFilterModel(string columnKey) => ColumnKey = columnKey;

    public string ColumnKey { get; }

    public IReadOnlyList<BomColumnFilterValue> Values => _values;

    /// <summary>Any value unchecked ⇒ this column is filtering.</summary>
    public bool IsActive => _unchecked.Count > 0;

    public IReadOnlyCollection<string> UncheckedValues => _unchecked;

    /// <summary>Allows a cell value through iff its value is checked.</summary>
    public bool Allows(string? value)
        => _unchecked.Count == 0 || !_unchecked.Contains(value ?? string.Empty);

    /// <summary>
    /// Rebuilds the distinct value list from the full dataset, preserving the
    /// unchecked state of any value that still exists.
    /// </summary>
    public void SetValues(IEnumerable<string> allValues)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in allValues)
        {
            string v = raw ?? string.Empty;
            if (seen.Add(v))
                distinct.Add(v);
        }

        _unchecked.IntersectWith(distinct); // drop unchecked values that vanished
        _values.Clear();
        foreach (string v in distinct)
            _values.Add(new BomColumnFilterValue(v, isChecked: !_unchecked.Contains(v)));
    }

    public void SetChecked(string value, bool isChecked)
    {
        string v = value ?? string.Empty;
        if (isChecked) _unchecked.Remove(v);
        else _unchecked.Add(v);
        foreach (BomColumnFilterValue item in _values)
            if (string.Equals(item.Value, v, StringComparison.OrdinalIgnoreCase))
                item.IsChecked = isChecked;
    }

    public void SelectAll()
    {
        _unchecked.Clear();
        foreach (BomColumnFilterValue item in _values) item.IsChecked = true;
    }

    public void Clear()
    {
        _unchecked.Clear();
        foreach (BomColumnFilterValue item in _values)
        {
            item.IsChecked = false;
            _unchecked.Add(item.Value);
        }
    }

    /// <summary>Restore unchecked state from a snapshot (undo/redo).</summary>
    public void RestoreUnchecked(IEnumerable<string> unchecked_)
    {
        _unchecked.Clear();
        foreach (string v in unchecked_) _unchecked.Add(v ?? string.Empty);
        // Drop any restored value that no longer exists in the current value list
        // (mirrors SetValues), so a stale snapshot can't leave a phantom "active"
        // filter — IsActive true while every visible value reads as checked.
        _unchecked.IntersectWith(_values.Select(item => item.Value));
        foreach (BomColumnFilterValue item in _values)
            item.IsChecked = !_unchecked.Contains(item.Value);
    }
}
