namespace FabricationAssistant.App.Android.Bom;

/// <summary>One distinct value in a column-filter checklist.</summary>
public sealed class BomColumnFilterValue
{
    public BomColumnFilterValue(string value, bool isChecked)
    {
        Value = value;
        IsChecked = isChecked;
    }

    /// <summary>Raw cell value (case preserved); "" for blank cells.</summary>
    public string Value { get; }

    /// <summary>UI text; blank cells render as "(blank)".</summary>
    public string Display => Value.Length == 0 ? "(blank)" : Value;

    public bool IsChecked { get; set; }
}
