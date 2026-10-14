using Lasero.App;
using Xunit;

namespace Lasero.Tests;

public sealed class ForwardedLaunchQueueTests
{
    [Fact]
    public void LaunchesForwardedBeforeTheWindowIsReadyAreDeliveredInOrderOnAttach()
    {
        var queue = new ForwardedLaunchQueue();
        queue.Submit(@"C:\a.lasero");
        queue.Submit(@"C:\b.lasero");
        Assert.Equal(2, queue.PendingCount);

        var received = new List<string?>();
        queue.Attach(received.Add);

        Assert.Equal([@"C:\a.lasero", @"C:\b.lasero"], received);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void LaunchesAfterAttachAreDeliveredImmediately()
    {
        var queue = new ForwardedLaunchQueue();
        var received = new List<string?>();
        queue.Attach(received.Add);

        queue.Submit(@"C:\c.lasero");
        queue.Submit(null);

        Assert.Equal([@"C:\c.lasero", null], received);
    }

    [Fact]
    public void RepeatedActivateRequestsAndSamePathCollapseWhileWaiting()
    {
        var queue = new ForwardedLaunchQueue();
        queue.Submit(null);
        queue.Submit(null);
        queue.Submit(@"C:\a.lasero");
        queue.Submit(@"c:\A.lasero");

        Assert.Equal(2, queue.PendingCount);
    }

    [Fact]
    public void WaitingQueueIsBoundedAndKeepsTheNewest()
    {
        var queue = new ForwardedLaunchQueue();
        for (var i = 0; i < ForwardedLaunchQueue.MaxPending + 5; i++) queue.Submit($@"C:\{i}.lasero");

        var received = new List<string?>();
        queue.Attach(received.Add);

        Assert.Equal(ForwardedLaunchQueue.MaxPending, received.Count);
        Assert.Equal($@"C:\{ForwardedLaunchQueue.MaxPending + 4}.lasero", received[^1]);
    }

    [Theory]
    [InlineData(@"relative.lasero")]
    [InlineData(@"C:\a.txt")]
    [InlineData("")]
    public void ForwardedPathValidationStillRejectsNonAbsoluteOrNonProjectPaths(string line) =>
        Assert.Null(SingleInstanceGuard.ParseForwardedProjectPath(line));

    [Fact]
    public void ForwardedPathValidationAcceptsAnAbsoluteProjectPath() =>
        Assert.Equal(@"C:\work\a.LASERO", SingleInstanceGuard.ParseForwardedProjectPath(@" C:\work\a.LASERO "));
}
