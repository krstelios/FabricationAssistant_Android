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

    [Fact]
    public void TryClipRayToVisibleInterval_StartsAtSectionPlaneWhenBoundsEntryIsHidden()
    {
        var plane = new SectionPlane(
            Guid.NewGuid(),
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ);

        bool clipped = AndroidSectionClipper.TryClipRayToVisibleInterval(
            new Vector3d(0, 0, -5),
            new Vector3d(0, 0, 1),
            intervalMin: 1.0,
            intervalMax: 10.0,
            [plane],
            sceneDiagonal: 10.0,
            out double visibleMin,
            out double visibleMax);

        Assert.True(clipped);
        Assert.InRange(visibleMin, 4.999, 5.001);
        Assert.Equal(10.0, visibleMax, precision: 6);
    }

    [Fact]
    public void TryClipRayToVisibleInterval_EndsAtSectionPlaneWhenRayLeavesVisibleSide()
    {
        var plane = new SectionPlane(
            Guid.NewGuid(),
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ);

        bool clipped = AndroidSectionClipper.TryClipRayToVisibleInterval(
            new Vector3d(0, 0, 5),
            new Vector3d(0, 0, -1),
            intervalMin: 1.0,
            intervalMax: 10.0,
            [plane],
            sceneDiagonal: 10.0,
            out double visibleMin,
            out double visibleMax);

        Assert.True(clipped);
        Assert.Equal(1.0, visibleMin, precision: 6);
        Assert.InRange(visibleMax, 4.999, 5.001);
    }

    [Fact]
    public void TryClipRayToVisibleInterval_RejectsIntervalFullyBehindSectionPlane()
    {
        var plane = new SectionPlane(
            Guid.NewGuid(),
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ);

        bool clipped = AndroidSectionClipper.TryClipRayToVisibleInterval(
            new Vector3d(0, 0, -5),
            new Vector3d(1, 0, 0),
            intervalMin: 1.0,
            intervalMax: 10.0,
            [plane],
            sceneDiagonal: 10.0,
            out _,
            out _);

        Assert.False(clipped);
    }
}
