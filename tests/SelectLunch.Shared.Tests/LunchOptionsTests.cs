using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Tests;

public class LunchOptionsTests
{
    [Fact]
    public void 기본값은_스펙과_일치한다()
    {
        var options = new LunchOptions();

        Assert.Equal("Asia/Seoul", options.TimeZone);
        Assert.Equal(new TimeOnly(10, 30), options.VoteOpenAt);
        Assert.Equal(30, options.VoteDurationMinutes);
        Assert.Equal(new TimeOnly(13, 30), options.MealRecordAt);
        Assert.True(options.WeekdaysOnly);
        Assert.Equal(180, options.CatchUpGraceMinutes);
        Assert.Equal(30, options.PollIntervalSeconds);
    }

    [Fact]
    public void 추천_가중치_기본값은_스펙과_일치한다()
    {
        var options = new RecommendationOptions();

        Assert.Equal(30, options.DaysSinceCap);
        Assert.Equal(3, options.Weight7d);
        Assert.Equal(1, options.Weight30d);
    }

    [Fact]
    public void 투표_마감_시각은_개시_시각에_진행시간을_더한_값이다()
    {
        var options = new LunchOptions
        {
            VoteOpenAt = new TimeOnly(10, 30),
            VoteDurationMinutes = 30,
        };

        Assert.Equal(new TimeOnly(11, 0), options.VoteCloseAt);
    }

    [Fact]
    public void 펜딩리마인더_기본값은_스펙과_일치한다()
    {
        var options = new PendingReminderOptions();

        Assert.True(options.Enabled);
        Assert.Equal(DayOfWeek.Friday, options.DayOfWeek);
        Assert.Equal(new TimeOnly(16, 0), options.At);
    }

    [Fact]
    public void 휴일_기본값은_빈_집합이다()
    {
        var options = new LunchOptions();

        Assert.NotNull(options.Holidays);
        Assert.Empty(options.Holidays);
    }

    [Fact]
    public void 섹션이름_상수는_Lunch이다()
    {
        Assert.Equal("Lunch", LunchOptions.SectionName);
    }
}
