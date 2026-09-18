namespace SelectLunch.Slack.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void 두_번째_획득은_실패한다()
    {
        var name = $"test-{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(name, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void 해제_후에는_다시_획득할_수_있다()
    {
        var name = $"test-{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        first!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var second));
        second!.Dispose();
    }
}
