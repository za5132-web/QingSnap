using System.Drawing;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class LongCaptureControlPlacementTests
{
    [Fact]
    public void PrefersBelowCaptureAndKeepsPhysicalGap()
    {
        var capture = new Rectangle(100, 100, 800, 600);
        var placed = LongCaptureControlPlacement.FindOutside(
            capture, new Rectangle(0, 0, 1920, 1080), new Size(420, 72));

        Assert.Equal(new Rectangle(100, 708, 420, 72), placed);
    }

    [Fact]
    public void UsesAboveWhenBelowDoesNotFit()
    {
        var capture = new Rectangle(100, 400, 1500, 640);
        var placed = LongCaptureControlPlacement.FindOutside(
            capture, new Rectangle(0, 0, 1920, 1080), new Size(420, 72));

        Assert.Equal(new Rectangle(100, 320, 420, 72), placed);
    }

    [Fact]
    public void UsesRightWhenNoVerticalSpaceExists()
    {
        var capture = new Rectangle(100, 0, 800, 1080);
        var placed = LongCaptureControlPlacement.FindOutside(
            capture, new Rectangle(0, 0, 1920, 1080), new Size(420, 72));

        Assert.Equal(new Rectangle(908, 0, 420, 72), placed);
    }

    [Fact]
    public void UsesLeftWhenOtherSidesDoNotFit()
    {
        var capture = new Rectangle(600, 0, 1320, 1080);
        var placed = LongCaptureControlPlacement.FindOutside(
            capture, new Rectangle(0, 0, 1920, 1080), new Size(420, 72));

        Assert.Equal(new Rectangle(172, 0, 420, 72), placed);
    }

    [Fact]
    public void NegativeMonitorOriginIsNotClampedToPrimaryMonitor()
    {
        var capture = new Rectangle(-1800, -300, 1000, 600);
        var screen = new Rectangle(-1920, -400, 1920, 1080);
        var placed = LongCaptureControlPlacement.FindOutside(capture, screen, new Size(420, 72));

        Assert.Equal(new Rectangle(-1800, 308, 420, 72), placed);
        AssertSafe(placed, capture, screen, 8);
    }

    [Fact]
    public void ClampsHorizontalPositionInsideMonitorWithoutEnteringCapture()
    {
        var capture = new Rectangle(1700, 100, 220, 600);
        var screen = new Rectangle(0, 0, 1920, 1080);
        var placed = LongCaptureControlPlacement.FindOutside(capture, screen, new Size(420, 72));

        Assert.Equal(new Rectangle(1500, 708, 420, 72), placed);
        AssertSafe(placed, capture, screen, 8);
    }

    [Fact]
    public void FullScreenCaptureHasNoSafePlacementOnSameMonitor()
    {
        Assert.Null(LongCaptureControlPlacement.FindOutside(
            new Rectangle(0, 0, 1920, 1080),
            new Rectangle(0, 0, 1920, 1080), new Size(420, 72)));
    }

    [Fact]
    public void AdjacentMonitorCanHostControlOutsideFullScreenCapture()
    {
        var capture = new Rectangle(-1920, 0, 1920, 1080);
        var screen = new Rectangle(0, 0, 2560, 1440);
        var placed = LongCaptureControlPlacement.FindOutside(capture, screen, new Size(630, 108), 12);

        AssertSafe(placed, capture, screen, 12);
    }

    [Fact]
    public void CrossMonitorCaptureMustBeAvoidedInItsEntirety()
    {
        var capture = new Rectangle(-800, 0, 1800, 1000);
        var screen = new Rectangle(0, 0, 1920, 1080);
        var placed = LongCaptureControlPlacement.FindOutside(capture, screen, new Size(420, 72));

        Assert.Equal(new Rectangle(0, 1008, 420, 72), placed);
        AssertSafe(placed, capture, screen, 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void DpiScaledPhysicalControlSizeIsPreserved(double scale)
    {
        var capture = new Rectangle(-1800, -300, 1200, 800);
        var screen = new Rectangle(-1920, -400, 2560, 1440);
        var size = new Size((int)Math.Ceiling(420 * scale), (int)Math.Ceiling(72 * scale));
        var gap = (int)Math.Ceiling(8 * scale);
        var placed = LongCaptureControlPlacement.FindOutside(capture, screen, size, gap);

        AssertSafe(placed, capture, screen, gap);
        Assert.Equal(size, placed!.Value.Size);
    }

    [Theory]
    [InlineData(0, 72)]
    [InlineData(420, 0)]
    [InlineData(-1, 72)]
    [InlineData(2000, 72)]
    [InlineData(420, 1200)]
    public void InvalidOrOversizedControlHasNoPlacement(int width, int height)
    {
        Assert.Null(LongCaptureControlPlacement.FindOutside(
            new Rectangle(100, 100, 800, 600),
            new Rectangle(0, 0, 1920, 1080), new Size(width, height)));
    }

    [Fact]
    public void InsufficientGapReturnsNoPlacementRatherThanOverlap()
    {
        Assert.Null(LongCaptureControlPlacement.FindOutside(
            new Rectangle(0, 0, 1920, 1000),
            new Rectangle(0, 0, 1920, 1080), new Size(420, 72), 9));
    }

    private static void AssertSafe(Rectangle? placement, Rectangle capture, Rectangle screen, int gap)
    {
        Assert.NotNull(placement);
        var bounds = placement!.Value;
        Assert.True(screen.Contains(bounds));
        Assert.False(bounds.IntersectsWith(capture));
        Assert.True(bounds.Left >= capture.Right + gap || bounds.Right <= capture.Left - gap ||
                    bounds.Top >= capture.Bottom + gap || bounds.Bottom <= capture.Top - gap);
    }
}
