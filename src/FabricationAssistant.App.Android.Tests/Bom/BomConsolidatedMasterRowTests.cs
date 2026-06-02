using System;
using System.Linq;
using FabricationAssistant.App.Android.Bom;
using Xunit;

namespace FabricationAssistant.App.Android.Tests.Bom;

/// <summary>
/// The NX flatten (Bom_Flatten.json → bom_flat) lists an assembly's components
/// but omits the top-level/master assembly itself. These tests pin the pure
/// decision that recovers the missing master row(s) from the hierarchy roots.
/// </summary>
public class BomConsolidatedMasterRowTests
{
    private sealed record Node(string Key, string? Parent);

    private static string[] Missing(Node[] nodes, string[] flat)
        => BomConsolidatedMasterRow
            .MissingRoots(nodes, n => n.Parent, n => n.Key, flat)
            .Select(n => n.Key)
            .ToArray();

    [Fact]
    public void Root_MissingFromFlat_IsReturned()
    {
        var nodes = new[] { new Node("ASM", null), new Node("p1", "ASM/0"), new Node("p2", "ASM/0") };
        Assert.Equal(new[] { "ASM" }, Missing(nodes, new[] { "p1", "p2" }));
    }

    [Fact]
    public void Root_PresentInFlat_IsNotReturned()
    {
        var nodes = new[] { new Node("ASM", null), new Node("p1", "ASM/0") };
        Assert.Empty(Missing(nodes, new[] { "ASM", "p1" }));
    }

    [Fact]
    public void NonRootNodes_AreIgnored_EvenWhenMissingFromFlat()
    {
        var nodes = new[] { new Node("ASM", null), new Node("sub", "ASM/0") };
        Assert.Empty(Missing(nodes, new[] { "ASM" })); // "sub" is not a root, so not injected
    }

    [Fact]
    public void MultipleRoots_OnlyMissingOnesReturned()
    {
        var nodes = new[] { new Node("ASM1", null), new Node("ASM2", null), new Node("p1", "ASM1/0") };
        Assert.Equal(new[] { "ASM1" }, Missing(nodes, new[] { "ASM2", "p1" }));
    }

    [Fact]
    public void DuplicateRootPartKeys_AreDeduped()
    {
        var nodes = new[] { new Node("ASM", null), new Node("ASM", null) };
        Assert.Equal(new[] { "ASM" }, Missing(nodes, Array.Empty<string>()));
    }

    [Fact]
    public void EmptyParentString_TreatedAsRoot()
    {
        var nodes = new[] { new Node("ASM", "") };
        Assert.Equal(new[] { "ASM" }, Missing(nodes, Array.Empty<string>()));
    }

    [Fact]
    public void BlankKey_IsSkipped()
    {
        var nodes = new[] { new Node("   ", null) };
        Assert.Empty(Missing(nodes, Array.Empty<string>()));
    }

    [Fact]
    public void EmptyNodes_ReturnsEmpty()
    {
        Assert.Empty(Missing(Array.Empty<Node>(), Array.Empty<string>()));
    }

    [Fact]
    public void FlatKeyMatch_IsOrdinalCaseSensitive()
    {
        // A case-different flat key is NOT the same part, so the root is still injected.
        var nodes = new[] { new Node("ASM", null) };
        Assert.Equal(new[] { "ASM" }, Missing(nodes, new[] { "asm" }));
    }
}
