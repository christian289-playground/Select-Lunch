using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Tests;

public class LunchOptionsTests
{
    [Fact]
    public void 기본값은_배포되는_설정과_일치한다()
    {
        // 기준은 README의 "하루 흐름"과 배포되는 appsettings.json이다.
        // docs/superpowers/specs의 설계 문서(2026-09-18)는 10:30·30초로 적혀 있으나
        // 그건 그 시점의 기록이고 더 이상 기준이 아니다 — 메뉴 게시가 10:42~10:49라
        // 10:30 개시로는 메뉴가 존재하지도 않는 시각에 투표가 열린다.
        // 코드 기본값과 설정 파일이 어긋나면 설정이 빠졌을 때 조용히 틀린 시각으로 돈다.
        var options = new LunchOptions();

        Assert.Equal("Asia/Seoul", options.TimeZone);
        Assert.Equal(new TimeOnly(11, 0), options.VoteOpenAt);
        Assert.Equal(30, options.VoteDurationMinutes);
        Assert.Equal(new TimeOnly(13, 30), options.MealRecordAt);
        Assert.True(options.WeekdaysOnly);
        Assert.Equal(180, options.CatchUpGraceMinutes);
        Assert.Equal(60, options.PollIntervalSeconds);
        Assert.Equal(5, options.MenuCollectionLeadMinutes);
        Assert.Equal(new TimeOnly(11, 30), options.VoteCloseAt);
        Assert.Equal(new TimeOnly(10, 55), options.MenuCollectionStart);
    }

    [Fact]
    public void 메뉴_수집_시작은_투표_개시에서_선행_시간을_뺀_값이다()
    {
        var options = new LunchOptions
        {
            VoteOpenAt = new TimeOnly(11, 0),
            MenuCollectionLeadMinutes = 5,
        };

        Assert.Equal(new TimeOnly(10, 55), options.MenuCollectionStart);
    }

    [Fact]
    public void 투표_개시를_바꾸면_메뉴_수집_시작도_따라_바뀐다()
    {
        // 파생값이 아니라 별도 설정이면 여기서 깨진다 — 두 시계가 조용히 어긋나는 것을 막는다.
        var options = new LunchOptions { VoteOpenAt = new TimeOnly(9, 30) };

        Assert.Equal(new TimeOnly(9, 25), options.MenuCollectionStart);

        options.VoteOpenAt = new TimeOnly(12, 0);

        Assert.Equal(new TimeOnly(11, 55), options.MenuCollectionStart);
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
