using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// /lunch stats·today가 UTC 자정~09:00 구간에 하루 어긋난 날짜를 쓰던 버그의
/// 회귀 테스트. LunchClock은 실제 시계를 읽으므로 그 구간을 강제로 재현할 수는
/// 없다 — 대신 오프셋이 실제로 반영되는지를 확인한다.
/// </summary>
public class LunchClockTests
{
    [Fact]
    public void 한국_표준시_오프셋은_UTC보다_9시간_빠르다()
    {
        Assert.Equal(TimeSpan.FromHours(9), LunchClock.NowIn("Asia/Seoul").Offset);
    }

    [Fact]
    public void UTC_오프셋은_0이다()
    {
        Assert.Equal(TimeSpan.Zero, LunchClock.NowIn("UTC").Offset);
    }

    [Fact]
    public void TodayIn은_NowIn의_현지_날짜와_같다()
    {
        var expected = DateOnly.FromDateTime(LunchClock.NowIn("Asia/Seoul").DateTime);

        Assert.Equal(expected, LunchClock.TodayIn("Asia/Seoul"));
    }

    [Fact]
    public void 알수없는_타임존_ID는_예외를_던진다()
    {
        Assert.Throws<TimeZoneNotFoundException>(() => LunchClock.NowIn("Not/AZone"));
    }
}
