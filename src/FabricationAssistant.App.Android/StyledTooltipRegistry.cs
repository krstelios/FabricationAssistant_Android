using Android.Content;
using Android.Views;
using Android.Widget;

namespace FabricationAssistant.App.Android;

internal sealed class StyledTooltipRegistry : IDisposable
{
    private const int MaxTooltipTextLength = 240;

    private readonly Dictionary<View, StyledTooltipController> _tooltips = new();

    public bool TryGet(View view, out StyledTooltipController? tooltip)
        => _tooltips.TryGetValue(view, out tooltip);

    public void Attach(Context context, View? view, string? text, bool useLongClick = true)
    {
        if (view is null)
            return;

        text = NormalizeTooltipText(text);
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (view.ContentDescription is null)
            view.ContentDescription = text;

        if (_tooltips.TryGetValue(view, out StyledTooltipController? tooltip))
        {
            tooltip.UpdateText(text, useLongClick);
            return;
        }

        _tooltips[view] = new StyledTooltipController(context, view, text, DismissExcept, useLongClick);
    }

    public void AttachTree(Context context, View? root, bool includeStaticText = false, bool useLongClick = false)
    {
        if (root is null)
            return;

        string? text = ResolveTooltipText(root);
        if (ShouldAttach(root, text, includeStaticText))
            Attach(context, root, text, useLongClick);

        if (root is not ViewGroup group)
            return;

        for (int i = 0; i < group.ChildCount; i++)
            AttachTree(context, group.GetChildAt(i), includeStaticText, useLongClick);
    }

    public void Dismiss()
    {
        foreach (StyledTooltipController tooltip in _tooltips.Values)
            tooltip.Dismiss();
    }

    public void DisposeTree(View? root)
    {
        if (root is null)
            return;

        if (root is ViewGroup group)
        {
            for (int i = 0; i < group.ChildCount; i++)
                DisposeTree(group.GetChildAt(i));
        }

        if (_tooltips.Remove(root, out StyledTooltipController? tooltip))
            tooltip.Dispose();
    }

    public void Dispose()
    {
        foreach (StyledTooltipController tooltip in _tooltips.Values)
            tooltip.Dispose();

        _tooltips.Clear();
    }

    private void DismissExcept(StyledTooltipController activeTooltip)
    {
        foreach (StyledTooltipController tooltip in _tooltips.Values)
        {
            if (!ReferenceEquals(tooltip, activeTooltip))
                tooltip.Dismiss();
        }
    }

    private static bool ShouldAttach(View view, string? text, bool includeStaticText)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!string.IsNullOrWhiteSpace(view.ContentDescription?.ToString()))
            return true;

        if (includeStaticText && view is TextView)
            return true;

        return view.Clickable
               || view.LongClickable
               || view.Focusable
               || view is Button
               || view is EditText
               || view is SeekBar
               || view is Spinner
               || view is AdapterView
               || view is CompoundButton;
    }

    private static string? ResolveTooltipText(View view)
    {
        string? contentDescription = view.ContentDescription?.ToString();
        if (!string.IsNullOrWhiteSpace(contentDescription))
            return contentDescription;

        if (view is EditText editText)
        {
            string? hint = editText.Hint;
            if (!string.IsNullOrWhiteSpace(hint))
                return hint;
        }

        if (view is TextView textView)
        {
            string? text = textView.Text;
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        if (view is Spinner spinner)
            return spinner.SelectedItem?.ToString();

        return null;
    }

    private static string? NormalizeTooltipText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= MaxTooltipTextLength)
            return normalized;

        return normalized[..(MaxTooltipTextLength - 3)] + "...";
    }
}
