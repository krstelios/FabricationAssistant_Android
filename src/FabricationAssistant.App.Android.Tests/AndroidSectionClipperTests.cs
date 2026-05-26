using System.Numerics;
using FabricationAssistant.App.Android.Measurement;
using FabricationAssistant.Core.Math;
using FabricationAssistant.Core.Sections;
using Xunit;

namespace FabricationAssistant.App.Android.Tests;

public sealed class AndroidSectionClipperTests
{
    [Fact]
    public void IsPointVisible_KeepsNormalSide()
    {
        var plane = new SectionPlane(
            Guid.NewGuid(),
            new Vector3(0, 0, 5),
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ);

        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(0, 0, 6), [plane]));
        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(0, 0, 5), [plane]));
        Assert.False(AndroidSectionClipper.IsPointVisible(new Vector3d(0, 0, 4), [plane]));
    }

    [Fact]
    public void IsPointVisible_UsesFirstEightPlanesLikeRenderer()
    {
        SectionPlane[] planes = Enumerable.Range(0, 9)
            .Select(i => new SectionPlane(
                Guid.NewGuid(),
                new Vector3(i, 0, 0),
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.UnitX))
            .ToArray();

        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(7.5, 0, 0), planes));
        Assert.False(AndroidSectionClipper.IsPointVisible(new Vector3d(6.5, 0, 0), planes));
    }

    [Fact]
    public void IsPointVisible_NormalizesNonUnitPlaneNormal()
    {
        var plane = new SectionPlane(
            Guid.NewGuid(),
            new Vector3(0, 0, 5),
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ * 2f);

        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(0, 0, 5.0000001), [plane]));
        Assert.False(AndroidSectionClipper.IsPointVisible(new Vector3d(0, 0, 4.9), [plane]));
    }

    [Fact]
    public void IsPointVisible_IgnoresInvalidPlaneNormal()
    {
        var plane = new SectionPlane(
            Guid.NewGuid(),
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            new Vector3(float.NaN, 0, 0));

        Assert.True(AndroidSectionClipper.IsPointVisible(new Vector3d(0, 0, -100), [plane]));
    }
}
