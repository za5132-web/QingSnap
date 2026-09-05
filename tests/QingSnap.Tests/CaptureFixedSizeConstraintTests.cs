using System.Drawing;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class CaptureFixedSizeConstraintTests
{
    [Fact]
    public void EightHundredSquareNeverChangesSizeWhileMoving()
    {
        var bounds = new Rectangle(0, 0, 1920, 1080);
        var selection = CaptureFixedSizeConstraint.Place(
            new Point(100, 100),
            new Size(800, 800),
            bounds);

        selection = CaptureFixedSizeConstraint.Move(selection, 400, 100, bounds);
        selection = CaptureFixedSizeConstraint.Move(selection, 5000, 5000, bounds);
        selection = CaptureFixedSizeConstraint.Move(selection, -5000, -5000, bounds);

        Assert.Equal(new Size(800, 800), selection.Size);
        Assert.True(bounds.Contains(selection));
    }

    [Fact]
    public void OversizedSelectionIsLimitedInsteadOfProducingInvalidCoordinates()
    {
        var bounds = new Rectangle(0, 0, 1366, 768);
        var requested = new Size(1920, 1080);

        var limited = CaptureFixedSizeConstraint.LimitSize(requested, bounds);
        var placed = CaptureFixedSizeConstraint.Place(new Point(1200, 700), requested, bounds);

        Assert.Equal(new Size(1366, 768), limited);
        Assert.Equal(bounds, placed);
    }

    [Fact]
    public void PositionIsClampedWithoutShrinkingLockedSize()
    {
        var bounds = new Rectangle(0, 0, 1920, 1080);
        var placed = CaptureFixedSizeConstraint.Place(
            new Point(1800, 900),
            new Size(750, 1000),
            bounds);

        Assert.Equal(new Rectangle(1170, 80, 750, 1000), placed);
    }

    [Fact]
    public void NegativeVirtualDesktopCoordinatesRemainValid()
    {
        var bounds = new Rectangle(-1920, -240, 4480, 1680);
        var placed = CaptureFixedSizeConstraint.Place(
            new Point(-900, -100),
            new Size(1920, 1080),
            bounds);
        var moved = CaptureFixedSizeConstraint.Move(placed, 1600, 300, bounds);

        Assert.Equal(new Size(1920, 1080), moved.Size);
        Assert.True(bounds.Contains(moved));
        Assert.True(placed.Left < 0 && placed.Right > 0);
    }

    [Fact]
    public void BuiltInPresetDataIsReadyWithoutAddingPresetManagementUi()
    {
        Assert.Contains(CaptureSizePresets.BuiltIn, item => item.Width == 800 && item.Height == 800);
        Assert.Contains(CaptureSizePresets.BuiltIn, item => item.Width == 1920 && item.Height == 1080);
        Assert.Contains(CaptureSizePresets.BuiltIn, item => item.Width == 750 && item.Height == 1000);
    }
}
