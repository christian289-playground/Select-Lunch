using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Shared.Tests;

public class LunchScheduleTests
{
    // 2026-09-18은 금요일, 09-19 토, 09-20 일, 09-21 월
    static readonly DateOnly Friday = new(2026, 9, 18);
    static readonly DateOnly Saturday = new(2026, 9, 19);
    static readonly DateOnly Sunday = new(2026, 9, 20);
    static readonly DateOnly Monday = new(2026, 9, 21);

    [Fact]
    public void 평일은_영업일이다()
    {
        Assert.True(LunchSchedule.IsBusinessDay(Friday, new LunchOptions()));
        Assert.True(LunchSchedule.IsBusinessDay(Monday, new LunchOptions()));
    }

    [Fact]
    public void 주말은_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = true };

        Assert.False(LunchSchedule.IsBusinessDay(Saturday, options));
        Assert.False(LunchSchedule.IsBusinessDay(Sunday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼지면_주말도_영업일이다()
    {
        var options = new LunchOptions { WeekdaysOnly = false };

        Assert.True(LunchSchedule.IsBusinessDay(Saturday, options));
    }

    [Fact]
    public void 설정된_공휴일은_평일이어도_영업일이_아니다()
    {
        var options = new LunchOptions { Holidays = [Friday] };

        Assert.False(LunchSchedule.IsBusinessDay(Friday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼져도_공휴일_목록에_있으면_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = false, Holidays = [Friday] };

        Assert.False(LunchSchedule.IsBusinessDay(Friday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼져도_공휴일_목록의_주말은_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = false, Holidays = [Saturday] };

        Assert.False(LunchSchedule.IsBusinessDay(Saturday, options));
    }
}
