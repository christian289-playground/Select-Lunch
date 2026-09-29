using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Slack.Tests;

public class SchedulerWindowTests
{
    static DateTimeOffset Kst(int d, int h, int m) => new(2026, 9, d, h, m, 0, TimeSpan.FromHours(9));

    // 2026-09-29 화요일, 26 토요일
    [Theory]
    [InlineData(29, 7, 59, false)]
    [InlineData(29, 8, 0, true)]
    [InlineData(29, 10, 42, true)]
    [InlineData(29, 13, 29, true)]
    [InlineData(29, 13, 30, false)]   // 식사 기록 시각부터는 수집하지 않는다
    [InlineData(29, 23, 0, false)]
    [InlineData(26, 10, 42, false)]   // 토요일
    public void 메뉴_수집은_영업일_아침부터_식사_기록_전까지만_한다(int day, int hour, int minute, bool expected)
    {
        Assert.Equal(expected, LunchSchedule.IsMenuCollectionWindow(Kst(day, hour, minute), new LunchOptions()));
    }

    [Fact]
    public void 휴일에는_수집하지_않는다()
    {
        var options = new LunchOptions { Holidays = [new DateOnly(2026, 9, 29)] };

        Assert.False(LunchSchedule.IsMenuCollectionWindow(Kst(29, 10, 42), options));
    }
}
