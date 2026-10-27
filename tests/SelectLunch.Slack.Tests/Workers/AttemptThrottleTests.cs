using SelectLunch.Slack.Workers;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 메뉴 수집과 메뉴 스레드 업로드가 함께 쓰는 시도 스로틀. 시각을 주입받는 순수 상태
/// 기계라 실제 시간을 기다리지 않고 경계를 그대로 확인할 수 있다.
/// </summary>
public class AttemptThrottleTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 29, 11, 0, 0, TimeSpan.FromHours(9));

    static AttemptThrottle FiveMinutes() => new(TimeSpan.FromMinutes(5));

    [Fact]
    public void 첫_시도는_언제나_허용한다()
    {
        Assert.True(FiveMinutes().TryAttempt(Start));
    }

    [Fact]
    public void 간격_안의_시도는_막고_간격에_닿으면_허용한다()
    {
        var throttle = FiveMinutes();

        Assert.True(throttle.TryAttempt(Start));
        Assert.False(throttle.TryAttempt(Start.AddMinutes(1)));
        Assert.False(throttle.TryAttempt(Start.AddMinutes(5).AddSeconds(-1)));
        Assert.True(throttle.TryAttempt(Start.AddMinutes(5)));
    }

    [Fact]
    public void 막힌_시도는_기준_시각을_갱신하지_않는다()
    {
        // 막힌 호출이 시각을 갱신하면 tick마다 불릴 때 영원히 막힌다 — 굶는 버그다.
        var throttle = FiveMinutes();
        Assert.True(throttle.TryAttempt(Start));

        for (var seconds = 60; seconds < 300; seconds += 60)
            Assert.False(throttle.TryAttempt(Start.AddSeconds(seconds)));

        Assert.True(throttle.TryAttempt(Start.AddMinutes(5)));
    }

    [Fact]
    public void 허용된_시도마다_기준_시각이_새로_잡힌다()
    {
        var throttle = FiveMinutes();

        Assert.True(throttle.TryAttempt(Start));
        Assert.True(throttle.TryAttempt(Start.AddMinutes(5)));
        Assert.False(throttle.TryAttempt(Start.AddMinutes(9)));
        Assert.True(throttle.TryAttempt(Start.AddMinutes(10)));
    }

    [Fact]
    public void 스로틀은_서로_독립이다()
    {
        // 수집이 스로틀을 소진했다고 업로드까지 미뤄지면 안 된다.
        var collect = FiveMinutes();
        var post = FiveMinutes();

        Assert.True(collect.TryAttempt(Start));

        Assert.True(post.TryAttempt(Start));
    }
}
