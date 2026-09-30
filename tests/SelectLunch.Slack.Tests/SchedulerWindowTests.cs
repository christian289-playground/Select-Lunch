using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Slack.Tests;

public class SchedulerWindowTests
{
    static DateTimeOffset Kst(int d, int h, int m) => new(2026, 9, d, h, m, 0, TimeSpan.FromHours(9));

    // 2026-09-29 화요일, 26 토요일
    // 기본값은 투표 11:00 · 선행 5분 → 수집은 10:55부터 13:30(MealRecordAt) 직전까지.
    [Theory]
    [InlineData(29, 8, 0, false)]     // 예전 고정 시작(08:00)에는 더 이상 수집하지 않는다
    [InlineData(29, 10, 54, false)]
    [InlineData(29, 10, 55, true)]
    [InlineData(29, 11, 0, true)]
    [InlineData(29, 13, 29, true)]
    [InlineData(29, 13, 30, false)]   // 식사 기록 시각부터는 수집하지 않는다
    [InlineData(29, 23, 0, false)]
    [InlineData(26, 11, 0, false)]    // 토요일
    public void 메뉴_수집은_영업일_투표_직전부터_식사_기록_전까지만_한다(int day, int hour, int minute, bool expected)
    {
        Assert.Equal(expected, LunchSchedule.IsMenuCollectionWindow(Kst(day, hour, minute), new LunchOptions()));
    }

    [Fact]
    public void 휴일에는_수집하지_않는다()
    {
        var options = new LunchOptions { Holidays = [new DateOnly(2026, 9, 29)] };

        Assert.False(LunchSchedule.IsMenuCollectionWindow(Kst(29, 11, 0), options));
    }

    [Fact]
    public void 투표_시각을_옮기면_수집_창도_따라_움직인다()
    {
        // 투표를 09:30으로 당기면 수집은 09:25부터다 — 기본 창(10:55~)에서는 닫혀 있던
        // 시각이 열려야 한다. 시작 시각을 별도 설정으로 박아 두면 여기서 깨진다.
        var options = new LunchOptions { VoteOpenAt = new TimeOnly(9, 30) };

        Assert.False(LunchSchedule.IsMenuCollectionWindow(Kst(29, 9, 24), options));
        Assert.True(LunchSchedule.IsMenuCollectionWindow(Kst(29, 9, 25), options));
        Assert.True(LunchSchedule.IsMenuCollectionWindow(Kst(29, 10, 0), options));
    }

    [Fact]
    public void 선행_시간을_늘리면_수집이_그만큼_일찍_시작한다()
    {
        var options = new LunchOptions { MenuCollectionLeadMinutes = 30 };   // 투표 11:00 → 수집 10:30

        Assert.False(LunchSchedule.IsMenuCollectionWindow(Kst(29, 10, 29), options));
        Assert.True(LunchSchedule.IsMenuCollectionWindow(Kst(29, 10, 30), options));
    }

    [Fact]
    public void 수집_종료는_선행_시간과_무관하게_식사_기록_시각이다()
    {
        // 선행 시간이 수집 창의 양끝을 함께 움직이는 구현(창 길이를 고정으로 잡는 등)이면
        // 여기서 잡힌다.
        var options = new LunchOptions { MenuCollectionLeadMinutes = 30 };

        Assert.True(LunchSchedule.IsMenuCollectionWindow(Kst(29, 13, 29), options));
        Assert.False(LunchSchedule.IsMenuCollectionWindow(Kst(29, 13, 30), options));
    }
}
