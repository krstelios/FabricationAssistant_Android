using System;
using System.Collections.Generic;
using System.Linq;
using FabricationAssistant.Core.SceneGraph;

namespace FabricationAssistant.App.Android;

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

    /// <summary>
    /// This node's own scene visibility, ignoring children. Virtual (packed)
    /// groups have no scene node, so they are transparent containers — they never
    /// gate visibility on their own.
    /// </summary>
    private bool OwnVisible => Node?.Visible ?? true;

    /// <summary>
    /// Whether the row's visibility toggle reads as ON. A node with children
    /// (assembly or packed group) is ON when AT LEAST ONE descendant part is
    /// visible — partial visibility still reads ON, and it goes OFF only when
    /// everything beneath it is hidden — so the toggle answers "is anything under
    /// here showing?" rather than tracking the container's own flag (which parts
    /// hidden individually never clear). A leaf uses its own scene visibility.
    /// </summary>
    public bool IsVisible => Children.Count > 0
        ? Children.Any(child => child.IsVisible)
        : OwnVisible;

    /// <summary>
    /// Whether this node actually renders — its own flag AND every ancestor's own
    /// flag (mirrors the hierarchical pruning in Scene.CollectVisible). Drives
    /// label dimming, so it must reflect real rendering, not the aggregate toggle.
    /// </summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            for (AndroidModelExplorerNode? current = this; current is not null; current = current.Parent)
            {
                if (!current.OwnVisible)
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
