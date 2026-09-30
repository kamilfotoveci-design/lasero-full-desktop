using System.Windows;
using Lasero.App.Views.Kamil;

namespace Lasero.Tests;

/// <summary>The open panel (QuickAsk or Expanded) never covers the head: not at any supported window
/// size, not at the largest user-resized size, and not when dragged over it.</summary>
public sealed class KamilHeadClearanceTests
{
    private const double Gap = 12;
    private const double SafeMargin = 16;

    // Host (canvas column) sizes for the supported windows: 1080x640 is the smallest, 1366x768 the
    // common laptop, 1920x1080 the reference. Height is the workspace row less the zoom cluster.
    public static TheoryData<double, double> Hosts => new()
    {
        { 720, 456 }, { 950, 570 }, { 1500, 880 },
    };

    private static Rect Bounds(double hostWidth, double hostHeight) => new(
        SafeMargin, KamilAssistantHost.ContextBarClearance,
        Math.Max(0, hostWidth - SafeMargin), Math.Max(0, hostHeight - KamilAssistantHost.ContextBarClearance));

    private static Rect HeadZone(Point head) => new(head.X - Gap, head.Y - Gap, 48 + 2 * Gap, 48 + 2 * Gap);

    [Theory]
    [MemberData(nameof(Hosts))]
    public void QuickAskAndExpandedEndAboveTheHeadAtEveryWindowSize(double hostWidth, double hostHeight)
    {
        var bounds = Bounds(hostWidth, hostHeight);
        var head = KamilAssistantHost.HeadOrigin(bounds.Right, bounds.Bottom);
        var room = Math.Max(0, bounds.Height - 48 - Gap);

        var sizes = new[]
        {
            new Size(400, Math.Min(200, room)),                       // QuickAsk
            new Size(420, Math.Min(640, room)),                       // Expanded, default
            new Size(420, Math.Min(500, room)),                       // Expanded, adaptive minimum
            new Size(Math.Min(720, bounds.Width), Math.Min(760, room)), // Expanded, user-resized to the maximum
            new Size(320, Math.Min(360, room)),                       // Expanded, user-resized to the minimum
        };

        foreach (var size in sizes)
        {
            var position = KamilAssistantHost.ComputePopoverPosition(size, head, bounds);
            var panel = new Rect(position, size);

            Assert.False(KamilAssistantHost.Overlaps(panel, HeadZone(head)), $"{size} at {position} covers the head at {head}");
            Assert.True(panel.Bottom <= head.Y - Gap + 0.001, $"panel bottom {panel.Bottom} must end above the head top {head.Y - Gap}");
            Assert.True(panel.Top >= bounds.Top - 0.001 && panel.Left >= bounds.Left - 0.001);
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void DraggingThePanelOverTheHeadPushesItClear(double hostWidth, double hostHeight)
    {
        var bounds = Bounds(hostWidth, hostHeight);
        var head = KamilAssistantHost.HeadOrigin(bounds.Right, bounds.Bottom);
        var room = Math.Max(0, bounds.Height - 48 - Gap);
        var size = new Size(420, Math.Min(500, room));

        for (var x = bounds.Left; x <= bounds.Right; x += 25)
        for (var y = bounds.Top; y <= bounds.Bottom; y += 25)
        {
            var clamped = KamilAssistantHost.ClampPosition(new Point(x, y), size, bounds);
            var placed = KamilAssistantHost.AvoidHead(clamped, size, head, bounds);

            Assert.False(KamilAssistantHost.Overlaps(new Rect(placed, size), HeadZone(head)), $"drop at {x},{y} ended at {placed} on the head");
        }
    }

    [Fact]
    public void ThePanelIsLeftAloneWhenItDoesNotTouchTheHead()
    {
        var bounds = Bounds(950, 570);
        var head = KamilAssistantHost.HeadOrigin(bounds.Right, bounds.Bottom);
        var position = new Point(bounds.Left + 10, bounds.Top + 10);

        Assert.Equal(position, KamilAssistantHost.AvoidHead(position, new Size(320, 200), head, bounds));
    }
}
