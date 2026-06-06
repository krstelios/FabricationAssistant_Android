using System;
using System.Collections.Generic;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Card;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>Excel-style filter+sort dropdown for one consolidated-BOM column.</summary>
internal sealed class BomColumnFilterPopup
{
    private readonly Context _ctx;
    private readonly BomColumnFilterModel _model;
    private readonly Action<SortDirection> _onSort;
    private readonly Action _onApply;      // called on Done / Sort to commit the edited selection
    private readonly Action? _onDismiss;
    // The checklist is edited locally and only written back to the shared model on
    // Done/Sort, so dismissing the popup (tap-outside) cleanly discards the edits
    // and never leaves the engine half-changed without an apply/undo.
    private readonly HashSet<string> _workingUnchecked;
    private PopupWindow? _popup;
    private LinearLayout? _listContainer;
    private string _search = string.Empty;

    public BomColumnFilterPopup(
        Context ctx,
        BomColumnFilterModel model,
        Action<SortDirection> onSort,
        Action onApply,
        Action? onDismiss = null)
    {
        _ctx = ctx;
        _model = model;
        _onSort = onSort;
        _onApply = onApply;
        _onDismiss = onDismiss;
        _workingUnchecked = new HashSet<string>(model.UncheckedValues, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Writes the locally-edited selection back to the shared model.</summary>
    private void Commit() => _model.RestoreUnchecked(_workingUnchecked);

    public void Show(View anchor)
    {
        // Dismiss any popup still open from a prior tap so a double-tap on the
        // header can't leak a PopupWindow (and orphan the stale dismiss wiring).
        _popup?.Dismiss();

        var card = new MaterialCardView(_ctx)
        {
            Radius = Dp(12),
            CardElevation = Dp(12),
            StrokeWidth = Dp(1),
        };
        card.SetCardBackgroundColor(ColorInt(Resource.Color.fa_surface_background));
        card.StrokeColor = ColorInt(Resource.Color.fa_border);

        var col = new LinearLayout(_ctx) { Orientation = Orientation.Vertical };
        col.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));

        col.AddView(SortRow());
        col.AddView(SearchBox());
        col.AddView(SelectClearRow());

        var scroll = new ScrollView(_ctx);
        _listContainer = new LinearLayout(_ctx) { Orientation = Orientation.Vertical };
        scroll.AddView(_listContainer);
        col.AddView(scroll, new LinearLayout.LayoutParams(Dp(260), Dp(240)));
        RebuildList();

        col.AddView(DoneButton());

        card.AddView(col);
        _popup = new PopupWindow((View)card, ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, focusable: true);
        _popup.SetBackgroundDrawable(new ColorDrawable(global::Android.Graphics.Color.Transparent));
        _popup.DismissEvent += (_, _) =>
        {
            _popup = null;
            _listContainer = null;
            _onDismiss?.Invoke();
        };
        _popup.ShowAsDropDown(anchor, 0, 0, GravityFlags.Start);
    }

    public void Dismiss()
    {
        PopupWindow? popup = _popup;
        if (popup is null)
            return;

        popup.Dismiss();
        _popup = null;
        _listContainer = null;
    }

    private View SortRow()
    {
        var row = new LinearLayout(_ctx) { Orientation = Orientation.Horizontal };
        row.AddView(TextButton("Sort A→Z", () => { Commit(); _onSort(SortDirection.Ascending); _onApply(); _popup?.Dismiss(); }));
        row.AddView(TextButton("Sort Z→A", () => { Commit(); _onSort(SortDirection.Descending); _onApply(); _popup?.Dismiss(); }));
        return row;
    }

    private EditText SearchBox()
    {
        var box = new EditText(_ctx) { Hint = "Search", ImeOptions = global::Android.Views.InputMethods.ImeAction.Done };
        box.SetSingleLine(true);
        box.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
        box.SetHintTextColor(ColorRes(Resource.Color.fa_text_disabled));
        box.TextChanged += (_, e) => { _search = e.Text?.ToString() ?? string.Empty; RebuildList(); };
        return box;
    }

    private View SelectClearRow()
    {
        var row = new LinearLayout(_ctx) { Orientation = Orientation.Horizontal };
        row.AddView(TextButton("Select all", () => { _workingUnchecked.Clear(); RebuildList(); }));
        row.AddView(TextButton("Clear", () =>
        {
            _workingUnchecked.Clear();
            foreach (BomColumnFilterValue v in _model.Values) _workingUnchecked.Add(v.Value);
            RebuildList();
        }));
        return row;
    }

    private void RebuildList()
    {
        if (_listContainer is null) return;
        _listContainer.RemoveAllViews();
        foreach (BomColumnFilterValue value in _model.Values.Where(v =>
                     _search.Length == 0 || v.Display.Contains(_search, StringComparison.OrdinalIgnoreCase)))
        {
            var cb = new CheckBox(_ctx) { Text = value.Display, Checked = !_workingUnchecked.Contains(value.Value) };
            cb.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
            BomColumnFilterValue captured = value;
            cb.CheckedChange += (_, e) =>
            {
                if (e.IsChecked) _workingUnchecked.Remove(captured.Value);
                else _workingUnchecked.Add(captured.Value);
            };
            _listContainer.AddView(cb);
        }
    }

    private View DoneButton()
        => TextButton("Done", () => { Commit(); _onApply(); _popup?.Dismiss(); });

    private TextView TextButton(string text, Action onClick)
    {
        var tv = new TextView(_ctx) { Text = text, Clickable = true, Focusable = true };
        tv.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        tv.SetTextColor(ColorRes(Resource.Color.fa_text_primary));
        tv.Click += (_, _) => onClick();
        return tv;
    }

    /// <summary>Returns the color as an Android.Graphics.Color (for SetTextColor / SetHintTextColor).</summary>
    private global::Android.Graphics.Color ColorRes(int resId)
        => new(global::AndroidX.Core.Content.ContextCompat.GetColor(_ctx, resId));

    /// <summary>Returns the raw ARGB int (for SetCardBackgroundColor / StrokeColor).</summary>
    private int ColorInt(int resId)
        => global::AndroidX.Core.Content.ContextCompat.GetColor(_ctx, resId);

    private int Dp(float v) => (int)MathF.Round(v * (_ctx.Resources?.DisplayMetrics?.Density ?? 1f));
}
