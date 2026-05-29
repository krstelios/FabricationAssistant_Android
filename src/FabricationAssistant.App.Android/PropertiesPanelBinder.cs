using System.Globalization;
using System.Text;
using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.App.Android;

internal sealed class PropertiesPanelBinder
{
    private readonly Context _context;
    private readonly TextView _emptyState;
    private readonly ScrollView _scroll;
    private readonly LinearLayout _content;
    private readonly global::Android.Graphics.Color _primaryColor;
    private readonly global::Android.Graphics.Color _secondaryColor;
    private readonly global::Android.Graphics.Color _borderColor;
    private readonly global::Android.Graphics.Color _accentColor;
    private readonly float _titleSizePx;
    private readonly float _bodySizePx;
    private readonly float _captionSizePx;

    public PropertiesPanelBinder(Context context, View panelRoot)
    {
        _context = context;
        _emptyState = panelRoot.FindViewById<TextView>(Resource.Id.propertiesEmptyState)
            ?? throw new InvalidOperationException("propertiesEmptyState not found.");
        _scroll = panelRoot.FindViewById<ScrollView>(Resource.Id.propertiesScroll)
            ?? throw new InvalidOperationException("propertiesScroll not found.");
        _content = panelRoot.FindViewById<LinearLayout>(Resource.Id.propertiesContent)
            ?? throw new InvalidOperationException("propertiesContent not found.");

        _primaryColor = GetColor(context, Resource.Color.fa_text_primary);
        _secondaryColor = GetColor(context, Resource.Color.fa_text_secondary);
        _borderColor = GetColor(context, Resource.Color.fa_border);
        _accentColor = GetColor(context, Resource.Color.fa_accent_500);
        _titleSizePx = context.Resources!.GetDimension(Resource.Dimension.fa_text_size_subtitle);
        _bodySizePx = context.Resources!.GetDimension(Resource.Dimension.fa_text_size_body);
        _captionSizePx = context.Resources!.GetDimension(Resource.Dimension.fa_text_size_caption);
    }

    public void ShowSelection(SceneNode? node, Scene? scene, int selectionCount = 1)
    {
        _content.RemoveAllViews();

        if (node is null)
        {
            _emptyState.Visibility = ViewStates.Visible;
            _scroll.Visibility = ViewStates.Gone;
            _emptyState.Text = _context.GetString(Resource.String.properties_empty);
            return;
        }

        node = ResolvePresentedNode(node);
        _emptyState.Visibility = ViewStates.Gone;
        _scroll.Visibility = ViewStates.Visible;

        // S15#1: the panel can only detail one node, but multi-select is a
        // supported state. Make that explicit instead of silently showing one
        // arbitrary part; the part shown is now deterministic (ResolveSelectedNode
        // orders by id).
        if (selectionCount > 1)
            AddSection("Selection", new[] { new PropertyRow("Selected", $"{selectionCount} items (showing first)") });

        AddSection("Component", BuildComponentRows(node));
        AddSection("Geometry", BuildGeometryRows(node, scene));

        IReadOnlyList<PropertyRow> packageRows = BuildPackageRows(scene);
        if (packageRows.Count > 0)
            AddSection("Package", packageRows);

        IReadOnlyList<PropertyRow> sourceRows = BuildSourceRows(node.Metadata);
        if (sourceRows.Count > 0)
            AddSection("Source", sourceRows);

        if (node.Metadata?.Properties is { Count: > 0 } properties)
        {
            AddPropertyGroup(
                "Attributes",
                properties
                    .Where(property => string.Equals(property.Namespace, "attribute", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(property => property.SourceOrdinal));

            AddPropertyGroup(
                "Expressions",
                properties
                    .Where(property => string.Equals(property.Namespace, "expression", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(property => property.SourceOrdinal));

            AddPropertyGroup(
                "Other Properties",
                properties
                    .Where(property =>
                        !string.Equals(property.Namespace, "attribute", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(property.Namespace, "expression", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(property => property.Namespace, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(property => property.SourceOrdinal));
        }

        _scroll.ScrollTo(0, 0);
    }

    private IReadOnlyList<PropertyRow> BuildComponentRows(SceneNode node)
    {
        return new[]
        {
            new PropertyRow("Name", GetUserFacingDisplayName(node)),
            new PropertyRow("Type", node.NodeType.ToString()),
            new PropertyRow("Visibility", node.Visible ? "Visible" : "Hidden"),
        };
    }

    private static IReadOnlyList<PropertyRow> BuildGeometryRows(SceneNode node, Scene? scene)
    {
        string meshReference;
        string triangleCount;
        string vertexCount;
        string bounds;

        if (node.MeshId.HasValue)
        {
            meshReference = "Direct geometry";
            MeshDto? mesh = scene?.GetMesh(node.MeshId.Value);
            if (mesh is not null)
            {
                triangleCount = mesh.TriangleCount.ToString("N0", CultureInfo.CurrentCulture);
                vertexCount = mesh.VertexCount.ToString("N0", CultureInfo.CurrentCulture);
                bounds = mesh.Bounds.IsValid ? FormatBounds(mesh.Bounds) : "(not computed)";
            }
            else
            {
                triangleCount = "0";
                vertexCount = "0";
                bounds = "N/A";
            }
        }
        else
        {
            meshReference = node.Children.Count > 0 ? "Contains child geometry" : "(none)";
            triangleCount = "0";
            vertexCount = "0";
            bounds = node.Bounds is { IsValid: true } nodeBounds ? FormatBounds(nodeBounds) : "N/A";
        }

        return new[]
        {
            new PropertyRow("Mesh", meshReference),
            new PropertyRow("Triangles", triangleCount),
            new PropertyRow("Vertices", vertexCount),
            new PropertyRow("Bounds", bounds),
        };
    }

    private static IReadOnlyList<PropertyRow> BuildPackageRows(Scene? scene)
    {
        ScenePackageInfoDto? packageInfo = scene?.PackageInfo;
        if (packageInfo is null)
            return Array.Empty<PropertyRow>();

        var rows = new List<PropertyRow>(2);
        AddIfNotEmpty(rows, "Format", packageInfo.SourceFormat);
        AddIfNotEmpty(rows, "Package ID", packageInfo.PackageId);
        return rows;
    }

    private static IReadOnlyList<PropertyRow> BuildSourceRows(SceneNodeMetadataDto? metadata)
    {
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.SourceKey))
            return Array.Empty<PropertyRow>();

        var rows = new List<PropertyRow>(8);
        AddIfNotEmpty(rows, "Source key", metadata.SourceKey);
        AddIfNotEmpty(rows, "Source type", metadata.SourceType);
        AddIfNotEmpty(rows, "Source path", metadata.SourceFullPath);
        AddIfNotEmpty(rows, "Occurrence", SanitizeGeneratedLabel(metadata.OccurrenceNodeName, metadata.SourceKey, "Geometry"));
        AddIfNotEmpty(rows, "Occurrence path", metadata.OccurrencePath);
        AddIfNotEmpty(rows, "Match", metadata.MatchStrategy);
        rows.Add(new PropertyRow("Instances", metadata.InstanceCount.ToString(CultureInfo.CurrentCulture)));
        return rows;
    }

    private const int MaxPropertyRowsPerGroup = 50;

    private void AddPropertyGroup(string title, IEnumerable<ScenePropertyDto> properties)
    {
        ScenePropertyDto[] items = properties.ToArray();
        if (items.Length == 0)
            return;

        AddSectionTitle($"{title} ({items.Length:N0})");

        // S15#2: cap inflated rows per group. Metadata-heavy CAD parts can carry
        // thousands of properties; inflating ~4-5 Views each synchronously into a
        // non-virtualized ScrollView janks or ANRs the UI thread. Show the first
        // K and summarize the rest (the section title already shows the total).
        int shown = System.Math.Min(items.Length, MaxPropertyRowsPerGroup);
        for (int i = 0; i < shown; i++)
            AddPropertyItem(items[i]);

        if (items.Length > shown)
            AddRow("More", $"{items.Length - shown:N0} not shown");
    }

    private void AddSection(string title, IReadOnlyList<PropertyRow> rows)
    {
        if (rows.Count == 0)
            return;

        AddSectionTitle(title);
        foreach (PropertyRow row in rows)
        {
            AddRow(row.Name, row.Value);
        }
    }

    private void AddSectionTitle(string title)
    {
        var text = new TextView(_context)
        {
            Text = title,
        };
        text.SetTextColor(_accentColor);
        text.SetTextSize(ComplexUnitType.Px, _titleSizePx);
        text.SetTypeface(text.Typeface, global::Android.Graphics.TypefaceStyle.Bold);

        var lp = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = Dp(10),
            BottomMargin = Dp(4),
        };
        text.LayoutParameters = lp;
        _content.AddView(text);
    }

    private void AddRow(string name, string value)
    {
        var row = new LinearLayout(_context)
        {
            Orientation = Orientation.Vertical,
        };
        row.SetPadding(0, Dp(5), 0, Dp(5));

        var nameView = new TextView(_context)
        {
            Text = name,
        };
        nameView.SetTextColor(_secondaryColor);
        nameView.SetTextSize(ComplexUnitType.Px, _captionSizePx);
        row.AddView(nameView);

        var valueView = new TextView(_context)
        {
            Text = string.IsNullOrWhiteSpace(value) ? "(empty)" : value,
        };
        valueView.SetTextColor(_primaryColor);
        valueView.SetTextSize(ComplexUnitType.Px, _bodySizePx);
        valueView.SetTextIsSelectable(true);
        row.AddView(valueView);

        _content.AddView(row);
        AddDivider();
    }

    private void AddPropertyItem(ScenePropertyDto property)
    {
        var row = new LinearLayout(_context)
        {
            Orientation = Orientation.Vertical,
        };
        row.SetPadding(0, Dp(5), 0, Dp(6));

        var nameView = new TextView(_context)
        {
            Text = property.Name,
        };
        nameView.SetTextColor(_secondaryColor);
        nameView.SetTextSize(ComplexUnitType.Px, _captionSizePx);
        nameView.SetTextIsSelectable(true);
        row.AddView(nameView);

        var valueView = new TextView(_context)
        {
            Text = FormatPropertyValue(property),
        };
        valueView.SetTextColor(_primaryColor);
        valueView.SetTextSize(ComplexUnitType.Px, _bodySizePx);
        valueView.SetTextIsSelectable(true);
        row.AddView(valueView);

        string details = CreatePropertyDetails(property);
        if (!string.IsNullOrWhiteSpace(details))
        {
            var detailsView = new TextView(_context)
            {
                Text = details,
            };
            detailsView.SetTextColor(_secondaryColor);
            detailsView.SetTextSize(ComplexUnitType.Px, _captionSizePx);
            detailsView.SetTextIsSelectable(true);
            detailsView.SetPadding(0, Dp(3), 0, 0);
            row.AddView(detailsView);
        }

        _content.AddView(row);
        AddDivider();
    }

    private void AddDivider()
    {
        var divider = new View(_context);
        divider.SetBackgroundColor(_borderColor);
        divider.LayoutParameters = new LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            Math.Max(1, Dp(1)));
        _content.AddView(divider);
    }

    private int Dp(float dp)
        => (int)(dp * (_context.Resources?.DisplayMetrics?.Density ?? 1.0f));

    private static global::Android.Graphics.Color GetColor(Context context, int resId)
        => new(context.GetColor(resId));

    private static void AddIfNotEmpty(List<PropertyRow> rows, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            rows.Add(new PropertyRow(name, value));
    }

    private static string CreatePropertyDetails(ScenePropertyDto property)
    {
        var details = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(property.TypeName))
            details.Append($"Type: {property.TypeName}");

        if (!string.IsNullOrWhiteSpace(property.Unit))
        {
            if (details.Length > 0)
                details.Append("  |  ");

            details.Append($"Unit: {property.Unit}");
        }

        if (!string.IsNullOrWhiteSpace(property.Formula))
        {
            if (details.Length > 0)
                details.AppendLine();

            details.Append($"Formula: {property.Formula}");
        }

        if (!string.IsNullOrWhiteSpace(property.Description))
        {
            if (details.Length > 0)
                details.AppendLine();

            details.Append($"Description: {property.Description}");
        }

        return details.ToString();
    }

    private static string FormatPropertyValue(ScenePropertyDto property)
    {
        if (!string.IsNullOrWhiteSpace(property.ValueText))
            return property.ValueText;

        if (property.ValueInteger.HasValue)
            return property.ValueInteger.Value.ToString(CultureInfo.CurrentCulture);

        if (property.ValueNumber.HasValue)
            // S15#3: use CurrentCulture to match the count/instance formatting
            // elsewhere in the panel (so all numbers share one decimal separator).
            return property.ValueNumber.Value.ToString("G", CultureInfo.CurrentCulture);

        if (property.ValueBoolean.HasValue)
            return property.ValueBoolean.Value ? "true" : "false";

        if (!string.IsNullOrWhiteSpace(property.ValueDateTime))
            return property.ValueDateTime;

        return "(empty)";
    }

    private static string GetUserFacingDisplayName(SceneNode node)
    {
        if (!IsGeneratedNodeLabel(node.DisplayName, node.SourceNodeName))
            return node.DisplayName;

        if (!string.IsNullOrWhiteSpace(node.Metadata?.SourceKey))
            return node.Metadata.SourceKey;

        for (SceneNode? current = node.Parent; current != null; current = current.Parent)
        {
            if (!IsGeneratedNodeLabel(current.DisplayName, current.SourceNodeName))
                return current.DisplayName;
        }

        return node.MeshId.HasValue ? "Geometry" : "Component";
    }

    private static SceneNode ResolvePresentedNode(SceneNode node)
    {
        SceneNode presented = node;
        while (presented.Parent is { } parent)
        {
            if (!parent.Children.Contains(presented) || !ShouldHideFromExplorer(presented, parent))
                break;

            presented = parent;
        }

        return presented;
    }

    private static bool ShouldHideFromExplorer(SceneNode node, SceneNode visibleOwner)
    {
        if (node.Parent is null)
            return false;

        if (string.IsNullOrWhiteSpace(node.SourceNodeName)
            && IsGeneratedNodeLabel(node.DisplayName))
        {
            return true;
        }

        string? ownerSourceKey = visibleOwner.Metadata?.SourceKey;
        return !string.IsNullOrWhiteSpace(ownerSourceKey)
               && SubtreeBelongsToSourceKey(node, ownerSourceKey);
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

    private static string SanitizeGeneratedLabel(
        string? value,
        string? preferredFallback,
        string defaultFallback)
    {
        if (!string.IsNullOrWhiteSpace(value) && !IsGeneratedNodeLabel(value))
            return value;

        if (!string.IsNullOrWhiteSpace(preferredFallback))
            return preferredFallback;

        return defaultFallback;
    }

    private static bool IsGeneratedNodeLabel(string? displayName)
        => IsGeneratedNodeLabel(displayName, sourceNodeName: null);

    private static bool IsGeneratedNodeLabel(string? displayName, string? sourceNodeName)
    {
        if (!string.IsNullOrWhiteSpace(sourceNodeName))
            return false;

        if (string.IsNullOrWhiteSpace(displayName)
            || !displayName.StartsWith("Node ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

    private static string FormatBounds(BoundingBox bounds)
    {
        Vector3d size = bounds.Size;
        return $"Min: ({bounds.Min.X:F2}, {bounds.Min.Y:F2}, {bounds.Min.Z:F2})\n"
             + $"Max: ({bounds.Max.X:F2}, {bounds.Max.Y:F2}, {bounds.Max.Z:F2})\n"
             + $"Size: {size.X:F2} x {size.Y:F2} x {size.Z:F2}";
    }

    private sealed record PropertyRow(string Name, string Value);
}
