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

    /// <summary>
    /// host.RunAsync()의 연속(continuation)은 스레드풀의 다른 스레드에서 재개될 수
    /// 있다 — Dispose()를 획득한 스레드가 아닌 스레드에서 불러도 예외가 나지 않아야
    /// 정상 종료가 크래시로 보이지 않는다.
    /// </summary>
    [Fact]
    public async Task 다른_스레드에서_Dispose해도_예외가_나지_않는다()
    {
        var name = $"test-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var guard));

        var exception = await Task.Run(() => Record.Exception(() => guard!.Dispose()));

        Assert.Null(exception);
    }
}
