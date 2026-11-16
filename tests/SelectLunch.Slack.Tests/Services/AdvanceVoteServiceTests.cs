using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 사전투표의 저장 경계. 실제 SQLite와 고정 시계 위에서 서비스를 그대로 돌리고, 소비자(슬래시 명령·액션 핸들러)가
/// 보는 결과만 확인한다 — 언제 받고 언제 거절하는가, 누구의 표가 어떻게 남는가.
/// </summary>
public sealed class AdvanceVoteServiceTests
{
    const string Channel = "C1";

    /// <summary>2026-11-16은 월요일이고 공휴일이 아니다. 투표 개시 기본값은 11:00(KST).</summary>
    static readonly DateOnly Today = new(2026, 11, 16);

    static readonly DateTimeOffset BeforeOpen = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(9));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    sealed class Harness(
        TestDb fixture, TestTimeProvider clock, LunchOptions options, LunchService lunch, Restaurant a, Restaurant b)
        : IAsyncDisposable
    {
        public TestDb Fixture => fixture;
        public TestTimeProvider Clock => clock;
        public LunchService Lunch => lunch;
        public Restaurant A => a;
        public Restaurant B => b;
        public AdvanceVoteService Service { get; } = NewService(fixture, Channel, options, clock);

        public AdvanceVoteService ServiceFor(string channel) => NewService(fixture, channel, options, clock);

        public ValueTask DisposeAsync() => fixture.DisposeAsync();

        static AdvanceVoteService NewService(TestDb fixture, string channel, LunchOptions options, TimeProvider clock) =>
            new(fixture.Db, channel,
                StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => options))),
                clock);
    }

    static async Task<Harness> SetupAsync(DateTimeOffset now, Action<LunchOptions>? configure = null)
    {
        var fixture = await TestDb.CreateAsync();
        var options = new LunchOptions();
        configure?.Invoke(options);
        var lunch = new LunchService(fixture.Db, Channel);
        var a = await lunch.SaveRestaurantAsync(new(null, "김밥천국", 1, null, null, null), "U0", Ct);
        var b = await lunch.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U0", Ct);
        return new(fixture, new TestTimeProvider(now), options, lunch, a, b);
    }

    /// <summary>
    /// 같은 사유가 상태 조회와 접수 양쪽에 똑같이 나오는지, 그리고 거절이면 표가 남지 않는지 본다.
    /// </summary>
    static async Task AssertGateAsync(Harness h, DateOnly date, AdvanceVoteStatus expected)
    {
        var ready = expected == AdvanceVoteStatus.Ready;

        Assert.Equal(expected, await h.Service.GetStatusAsync(date, Ct));
        Assert.Equal(ready ? AdvanceVoteStatus.Saved : expected, await h.Service.CastAsync(date, "U1", h.A.Id, Ct));
        Assert.Equal(ready, await h.Service.FindAsync(date, "U1", Ct) is not null);
    }

    [Theory]
    [InlineData("2026-11-16T10:59:59+09:00", "2026-11-16")]
    [InlineData("2026-11-16T11:00:00+09:00", "2026-11-17")]
    [InlineData("2026-11-16T16:29:00+09:00", "2026-11-17")]
    [InlineData("2026-11-17T00:00:00+09:00", "2026-11-17")]
    [InlineData("2026-11-20T16:00:00+09:00", "2026-11-23")]
    [InlineData("2026-11-21T10:00:00+09:00", "2026-11-23")]
    [InlineData("2026-12-24T16:00:00+09:00", "2026-12-28")]
    public async Task 다음_투표일을_열고_그날의_선택을_저장한다(string now, string target)
    {
        await using var h = await SetupAsync(At(now));
        var expected = DateOnly.Parse(target, CultureInfo.InvariantCulture);

        Assert.Equal(expected, await h.Service.GetTargetDateAsync(Ct));
        await AssertGateAsync(h, expected, AdvanceVoteStatus.Ready);
        Assert.Equal(AdvanceVoteStatus.NotTargetDate,
            await h.Service.CastAsync(expected.AddDays(1), "U2", h.A.Id, Ct));
    }

    [Fact]
    public async Task 다음_투표일도_수동_공휴일이면_그_다음_영업일로_넘어간다()
    {
        await using var h = await SetupAsync(BeforeOpen.AddHours(6), o => o.Holidays.Add(Today.AddDays(1)));
        Assert.Equal(Today.AddDays(2), await h.Service.GetTargetDateAsync(Ct));
        await AssertGateAsync(h, Today.AddDays(2), AdvanceVoteStatus.Ready);
    }

    [Fact]
    public async Task 내일_사전투표는_오늘_투표와_분리되고_자정_이후에도_변경과_취소가_가능하다()
    {
        await using var h = await SetupAsync(BeforeOpen.AddHours(6));
        var tomorrow = Today.AddDays(1);
        await h.Service.CastAsync(tomorrow, "U1", h.A.Id, Ct);
        await h.Service.CastAsync(tomorrow, "U2", null, Ct);
        var todayPoll = await h.Lunch.OpenPollAsync(Today, BeforeOpen.AddHours(1), BeforeOpen.AddMinutes(90), Ct);
        Assert.Empty(await h.Fixture.Db.PollVotes.Where(v => v.PollId == todayPoll.Id).ToListAsync(Ct));

        h.Clock.UtcNow = BeforeOpen.AddDays(1).AddHours(-10);
        Assert.Equal(tomorrow, await h.Service.GetTargetDateAsync(Ct));
        Assert.Equal(h.A.Id, (await h.Service.FindAsync(tomorrow, "U1", Ct))!.RestaurantId);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(tomorrow, "U1", h.B.Id, Ct));
        Assert.Equal(AdvanceVoteStatus.Cancelled, await h.Service.CancelAsync(tomorrow, "U2", Ct));
        var opens = BeforeOpen.AddDays(1).AddHours(1);
        var poll = await h.Lunch.OpenPollAsync(tomorrow, opens, opens.AddMinutes(30), Ct);

        var vote = Assert.Single(await h.Fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal((poll.Id, "U1", (long?)h.B.Id), (vote.PollId, vote.SlackUserId, vote.RestaurantId));
        Assert.Empty(await h.Fixture.Db.AdvanceVotes.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("2026-11-16T00:00:00+09:00", "11:00", true)]
    [InlineData("2026-11-16T10:59:59+09:00", "11:00", true)]
    [InlineData("2026-11-16T11:00:00+09:00", "11:00", false)]
    [InlineData("2026-11-16T11:00:00+09:00", "11:30", true)]
    [InlineData("2026-11-16T11:30:00+09:00", "11:30", false)]
    public async Task 개시_정각부터는_접수하지_않고_개시_시각은_설정을_따른다(string now, string voteOpenAt, bool open)
    {
        await using var h = await SetupAsync(
            At(now), o => o.VoteOpenAt = TimeOnly.Parse(voteOpenAt, CultureInfo.InvariantCulture));

        await AssertGateAsync(h, Today, open ? AdvanceVoteStatus.Ready : AdvanceVoteStatus.VotingStarted);
    }

    [Theory]
    [InlineData("2026-11-15T15:30:00Z", "2026-11-16", AdvanceVoteStatus.Ready)]          // UTC는 15일, KST는 16일 00:30
    [InlineData("2026-11-15T15:30:00Z", "2026-11-15", AdvanceVoteStatus.NotTargetDate)]       // UTC 날짜로는 열리지 않는다
    [InlineData("2026-11-16T15:30:00Z", "2026-11-16", AdvanceVoteStatus.NotTargetDate)]       // UTC는 16일, KST는 17일 00:30
    [InlineData("2026-11-16T15:30:00Z", "2026-11-17", AdvanceVoteStatus.Ready)]
    public async Task 오늘은_UTC가_아니라_설정_타임존의_날짜다(string utcNow, string date, AdvanceVoteStatus expected)
    {
        await using var h = await SetupAsync(At(utcNow));

        await AssertGateAsync(h, DateOnly.Parse(date, CultureInfo.InvariantCulture), expected);
    }

    [Theory]
    [InlineData("2026-11-14", false)]   // 토요일
    [InlineData("2026-12-25", false)]   // 성탄절(계산되는 법정 공휴일)
    [InlineData("2026-11-17", true)]    // 수동 공휴일 목록
    public async Task 주말과_공휴일에는_접수하지_않는다(string date, bool manualHoliday)
    {
        var day = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        await using var h = await SetupAsync(
            At($"{date}T10:00:00+09:00"), o => { if (manualHoliday) o.Holidays.Add(day); });

        await AssertGateAsync(h, day, AdvanceVoteStatus.NonBusinessDay);
    }

    [Fact]
    public async Task 그날_투표가_이미_있으면_개시_시각_전이어도_접수와_취소를_거절하고_표를_건드리지_않는다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.A.Id, Ct));
        h.Fixture.Db.Polls.Add(new LunchPoll
        {
            ChannelId = Channel, Date = Today, OpensAt = BeforeOpen, ClosesAt = BeforeOpen.AddMinutes(30),
        });
        await h.Fixture.Db.SaveChangesAsync(Ct);

        Assert.Equal(AdvanceVoteStatus.VotingStarted, await h.Service.GetStatusAsync(Today, Ct));
        Assert.Equal(AdvanceVoteStatus.VotingStarted, await h.Service.CastAsync(Today, "U2", h.A.Id, Ct));
        Assert.Equal(AdvanceVoteStatus.VotingStarted, await h.Service.CancelAsync(Today, "U1", Ct));

        Assert.Null(await h.Service.FindAsync(Today, "U2", Ct));
        Assert.Equal(h.A.Id, (await h.Service.FindAsync(Today, "U1", Ct))!.RestaurantId);
        Assert.Equal(Today.AddDays(1), await h.Service.GetTargetDateAsync(Ct));
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today.AddDays(1), "U1", h.B.Id, Ct));
    }

    [Fact]
    public async Task 채널과_사용자가_다르면_표와_개시_여부가_서로_영향을_주지_않는다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        var other = h.ServiceFor("C2");
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.A.Id, Ct));
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U2", h.A.Id, Ct));
        Assert.Equal(AdvanceVoteStatus.Saved, await other.CastAsync(Today, "U1", h.B.Id, Ct));

        Assert.Equal(AdvanceVoteStatus.Cancelled, await h.Service.CancelAsync(Today, "U1", Ct));

        Assert.Null(await h.Service.FindAsync(Today, "U1", Ct));
        Assert.Equal(h.A.Id, (await h.Service.FindAsync(Today, "U2", Ct))!.RestaurantId);
        Assert.Equal(h.B.Id, (await other.FindAsync(Today, "U1", Ct))!.RestaurantId);

        h.Fixture.Db.Polls.Add(new LunchPoll
        {
            ChannelId = "C2", Date = Today, OpensAt = BeforeOpen, ClosesAt = BeforeOpen.AddMinutes(30),
        });
        await h.Fixture.Db.SaveChangesAsync(Ct);

        Assert.Equal(AdvanceVoteStatus.VotingStarted, await other.GetStatusAsync(Today, Ct));
        Assert.Equal(AdvanceVoteStatus.Ready, await h.Service.GetStatusAsync(Today, Ct));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("archived")]
    [InlineData("missing")]
    public async Task 활성이_아닌_식당은_거절하고_기존_표를_그대로_둔다(string kind)
    {
        await using var h = await SetupAsync(BeforeOpen);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.A.Id, Ct));

        long target;
        switch (kind)
        {
            case "pending":
                target = (await h.Lunch.SaveRestaurantAsync(
                    new(null, "이름만아는집", null, null, null, null), "U0", Ct)).Id;
                break;
            case "archived":
                h.B.Status = RestaurantStatus.Archived;
                await h.Fixture.Db.SaveChangesAsync(Ct);
                target = h.B.Id;
                break;
            default:
                target = 9999;
                break;
        }

        Assert.Equal(AdvanceVoteStatus.InvalidRestaurant, await h.Service.CastAsync(Today, "U1", target, Ct));
        Assert.Equal(AdvanceVoteStatus.InvalidRestaurant, await h.Service.CastAsync(Today, "U2", target, Ct));

        Assert.Equal(h.A.Id, (await h.Service.FindAsync(Today, "U1", Ct))!.RestaurantId);
        Assert.Null(await h.Service.FindAsync(Today, "U2", Ct));
    }

    [Fact]
    public async Task 다시_접수하면_같은_행을_갱신하고_접수_시각은_주입한_시계를_따른다()
    {
        await using var h = await SetupAsync(BeforeOpen);

        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.A.Id, Ct));
        h.Clock.UtcNow = BeforeOpen.AddMinutes(5);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.B.Id, Ct));
        h.Clock.UtcNow = BeforeOpen.AddMinutes(9);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", null, Ct));
        h.Clock.UtcNow = BeforeOpen.AddMinutes(12);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.A.Id, Ct));

        var row = Assert.Single(await h.Fixture.Db.AdvanceVotes.AsNoTracking().ToListAsync(Ct));
        Assert.Equal((Channel, Today, "U1"), (row.ChannelId, row.Date, row.SlackUserId));
        Assert.Equal(h.A.Id, row.RestaurantId);
        Assert.Equal(BeforeOpen.AddMinutes(12), row.VotedAt);
    }

    [Fact]
    public async Task 기권은_선택으로_남고_취소는_미선택으로_되돌리며_없는_표의_취소도_성공한다()
    {
        await using var h = await SetupAsync(BeforeOpen);

        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", null, Ct));
        var abstained = await h.Service.FindAsync(Today, "U1", Ct);
        Assert.NotNull(abstained);
        Assert.Null(abstained.RestaurantId);

        Assert.Equal(AdvanceVoteStatus.Cancelled, await h.Service.CancelAsync(Today, "U1", Ct));
        Assert.Null(await h.Service.FindAsync(Today, "U1", Ct));
        Assert.Empty(await h.Fixture.Db.AdvanceVotes.AsNoTracking().ToListAsync(Ct));

        Assert.Equal(AdvanceVoteStatus.Cancelled, await h.Service.CancelAsync(Today, "U1", Ct));
        Assert.Equal(AdvanceVoteStatus.Cancelled, await h.Service.CancelAsync(Today, "U9", Ct));
    }

    [Fact]
    public async Task 저장한_표는_새_DbContext에서도_식당과_함께_조회된다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U1", h.A.Id, Ct));
        Assert.Equal(AdvanceVoteStatus.Saved, await h.Service.CastAsync(Today, "U2", null, Ct));

        // 재시작을 흉내 낸다: 추적 상태가 전혀 없는 새 컨텍스트가 같은 DB를 읽는다.
        await using var restarted = new LunchDbContext(new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(h.Fixture.Db.Database.GetDbConnection()).Options);
        var service = new AdvanceVoteService(
            restarted, Channel,
            StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions()))),
            h.Clock);

        var voted = await service.FindAsync(Today, "U1", Ct);
        Assert.NotNull(voted);
        Assert.Equal(h.A.Id, voted.RestaurantId);
        Assert.Equal("김밥천국", voted.Restaurant!.Name);
        Assert.Equal(BeforeOpen, voted.VotedAt);

        var abstained = await service.FindAsync(Today, "U2", Ct);
        Assert.NotNull(abstained);
        Assert.Null(abstained.RestaurantId);
        Assert.Null(abstained.Restaurant);

        Assert.Null(await service.FindAsync(Today, "U3", Ct));

        // 투표 개시 전에 식당이 보관돼도 표는 남고, 조회가 보관 상태를 알려준다.
        (await restarted.Restaurants.SingleAsync(r => r.Id == h.A.Id, Ct)).Status = RestaurantStatus.Archived;
        await restarted.SaveChangesAsync(Ct);
        Assert.Equal(RestaurantStatus.Archived, (await service.FindAsync(Today, "U1", Ct))!.Restaurant!.Status);
    }
}
