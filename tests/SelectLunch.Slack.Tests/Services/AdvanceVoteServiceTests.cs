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
    static readonly DateOnly Today = new(2026, 11, 16);
    static readonly DateTimeOffset BeforeOpen = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(9));
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    sealed class Harness(TestDb fixture, TestTimeProvider clock, LunchOptions options, LunchService lunch, Restaurant a, Restaurant b)
        : IAsyncDisposable
    {
        public TestDb Fixture => fixture;
        public TestTimeProvider Clock => clock;
        public LunchService Lunch => lunch;
        public Restaurant A => a;
        public Restaurant B => b;
        public AdvanceVoteService Service => NewService(fixture.Db);
        public AdvanceVoteService NewService(LunchDbContext db, string channel = Channel) => new(db, channel,
            StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => options))), clock);
        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }

    static async Task<Harness> SetupAsync(DateTimeOffset now)
    {
        var fixture = await TestDb.CreateAsync();
        var lunch = new LunchService(fixture.Db, Channel);
        var a = await lunch.SaveRestaurantAsync(new(null, "김밥천국", 1, null, null, null), "U0", Ct);
        var b = await lunch.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U0", Ct);
        return new(fixture, new TestTimeProvider(now), new LunchOptions(), lunch, a, b);
    }

    [Fact]
    public async Task 취소와_재선택도_횟수를_차감하고_중복과_한도초과는_변경하지_않는다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        var service = h.Service;
        var first = await service.CastAsync(Today, "U1", h.A.Id, 0, Ct);
        Assert.Equal(AdvanceVoteStatus.Saved, first.Status);
        Assert.Equal(AdvanceVoteChangeKind.Selected, first.Change!.Kind);
        Assert.Equal("김밥천국", first.Change.RestaurantName);
        Assert.Equal(BeforeOpen, first.Change.ChangedAt);
        var duplicate = await service.CastAsync(Today, "U1", h.A.Id, 0, Ct);
        Assert.Equal(AdvanceVoteStatus.Unchanged, duplicate.Status);
        Assert.Null(duplicate.Change);
        var cancelled = await service.CancelAsync(Today, "U1", 1, Ct);
        Assert.Equal(AdvanceVoteStatus.Cancelled, cancelled.Status);
        Assert.Equal(AdvanceVoteChangeKind.Cancelled, cancelled.Change!.Kind);
        Assert.Empty(cancelled.Change.Snapshot.Tallies);
        var row = (await service.FindAsync(Today, "U1", Ct))!;
        Assert.True(row.IsCancelled);
        Assert.Null(row.RestaurantId);
        Assert.Equal(2, row.ChangeCount);
        Assert.Equal(AdvanceVoteStatus.Unchanged, (await service.CancelAsync(Today, "U1", 1, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.Saved, (await service.CastAsync(Today, "U1", null, 2, Ct)).Status);
        var rejected = await service.CastAsync(Today, "U1", h.B.Id, 3, Ct);
        Assert.Equal(AdvanceVoteStatus.LimitReached, rejected.Status);
        Assert.Null(rejected.Change);
        Assert.Equal(1, (await service.GetSnapshotAsync(Today, Ct)).Abstentions);
        Assert.Equal(3, (await service.FindAsync(Today, "U1", Ct))!.ChangeCount);
    }

    [Fact]
    public async Task 오래된_기대_횟수와_잘못된_식당은_차감하지_않는다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        var service = h.Service;
        Assert.Equal(AdvanceVoteStatus.Saved, (await service.CastAsync(Today, "U1", h.A.Id, 0, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.Conflict, (await service.CastAsync(Today, "U1", h.B.Id, 0, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.InvalidRestaurant, (await service.CastAsync(Today, "U1", 9999, 1, Ct)).Status);
        h.B.Status = RestaurantStatus.Archived;
        await h.Fixture.Db.SaveChangesAsync(Ct);
        Assert.Equal(AdvanceVoteStatus.InvalidRestaurant, (await service.CastAsync(Today, "U1", h.B.Id, 1, Ct)).Status);
        var pending = await h.Lunch.SaveRestaurantAsync(new(null, "미분류", null, null, null, null), "U0", Ct);
        Assert.Equal(AdvanceVoteStatus.InvalidRestaurant, (await service.CastAsync(Today, "U1", pending.Id, 1, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.Unchanged, (await service.CancelAsync(Today, "U2", 0, Ct)).Status);
        Assert.Equal(1, (await service.FindAsync(Today, "U1", Ct))!.ChangeCount);
        Assert.Null(await service.FindAsync(Today, "U2", Ct));
    }

    [Fact]
    public async Task 재시작해도_취소와_횟수가_남고_집계는_활성_식당과_기권만_포함한다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        var service = h.Service;
        await service.CastAsync(Today, "U1", h.A.Id, 0, Ct);
        await service.CancelAsync(Today, "U1", 1, Ct);
        await service.CastAsync(Today, "U2", h.A.Id, 0, Ct);
        await service.CastAsync(Today, "U3", h.B.Id, 0, Ct);
        await service.CastAsync(Today, "U4", null, 0, Ct);
        h.B.Status = RestaurantStatus.Archived;
        await h.Fixture.Db.SaveChangesAsync(Ct);
        await using var restarted = new LunchDbContext(new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(h.Fixture.Db.Database.GetDbConnection()).Options);
        var after = h.NewService(restarted);
        var cancelled = (await after.FindAsync(Today, "U1", Ct))!;
        Assert.True(cancelled.IsCancelled);
        Assert.Equal(2, cancelled.ChangeCount);
        var snapshot = await after.GetSnapshotAsync(Today, Ct);
        var tally = Assert.Single(snapshot.Tallies);
        Assert.Equal((h.A.Id, "김밥천국", 1), (tally.RestaurantId, tally.Name, tally.Count));
        Assert.Equal(1, snapshot.Abstentions);
        Assert.Equal(AdvanceVoteStatus.Saved, (await after.CastAsync(Today, "U1", h.A.Id, 2, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.LimitReached, (await after.CancelAsync(Today, "U1", 3, Ct)).Status);
    }

    [Theory]
    [InlineData("2026-11-16T10:59:59+09:00", "2026-11-16", AdvanceVoteStatus.Ready)]
    [InlineData("2026-11-16T11:00:00+09:00", "2026-11-16", AdvanceVoteStatus.VotingStarted)]
    [InlineData("2026-11-16T11:29:59+09:00", "2026-11-16", AdvanceVoteStatus.VotingStarted)]
    [InlineData("2026-11-16T11:30:00+09:00", "2026-11-17", AdvanceVoteStatus.Ready)]
    [InlineData("2026-11-16T16:29:00+09:00", "2026-11-17", AdvanceVoteStatus.Ready)]
    [InlineData("2026-11-21T10:00:00+09:00", "2026-11-23", AdvanceVoteStatus.Ready)]
    [InlineData("2026-12-24T16:00:00+09:00", "2026-12-28", AdvanceVoteStatus.Ready)]
    public async Task 투표가_없으면_개시와_예정마감_및_영업일로_회차를_판정한다(
        string now, string target, AdvanceVoteStatus expected)
    {
        await using var h = await SetupAsync(At(now));
        var date = DateOnly.Parse(target, CultureInfo.InvariantCulture);
        Assert.Equal(date, await h.Service.GetTargetDateAsync(Ct));
        Assert.Equal(expected, await h.Service.GetStatusAsync(date, Ct));
    }

    [Fact]
    public async Task 회차와_채널이_다르면_횟수와_집계를_공유하지_않는다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        var a = h.Service;
        var b = h.NewService(h.Fixture.Db, "C2");
        Assert.Equal(AdvanceVoteStatus.Saved, (await a.CastAsync(Today, "U1", h.A.Id, 0, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.Saved, (await b.CastAsync(Today, "U1", h.B.Id, 0, Ct)).Status);
        Assert.Equal(h.A.Id, Assert.Single((await a.GetSnapshotAsync(Today, Ct)).Tallies).RestaurantId);
        Assert.Equal(h.B.Id, Assert.Single((await b.GetSnapshotAsync(Today, Ct)).Tallies).RestaurantId);
        Assert.Equal(AdvanceVoteStatus.NotTargetDate, (await a.CastAsync(Today.AddDays(1), "U2", h.A.Id, 0, Ct)).Status);
        Assert.Equal(AdvanceVoteStatus.NonBusinessDay, (await a.CastAsync(Today.AddDays(5), "U2", h.A.Id, 0, Ct)).Status);
    }

    [Fact]
    public async Task 본투표는_사전표와_분리되고_Open은_조회전용_Closed는_다음회차다()
    {
        await using var h = await SetupAsync(BeforeOpen);
        var service = h.Service;
        Assert.Equal(AdvanceVoteStatus.Saved, (await service.CastAsync(Today, "U1", h.A.Id, 0, Ct)).Status);
        var poll = await h.Lunch.OpenPollAsync(Today, BeforeOpen.AddHours(1), BeforeOpen.AddMinutes(90), Ct);
        Assert.Empty(await h.Fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).ToListAsync(Ct));
        await h.Lunch.CastVoteAsync(poll.Id, "U1", h.B.Id, Ct);
        Assert.Equal(h.A.Id, (await service.FindAsync(Today, "U1", Ct))!.RestaurantId);
        Assert.Equal(1, (await service.FindAsync(Today, "U1", Ct))!.ChangeCount);
        h.Clock.UtcNow = BeforeOpen.AddHours(6);
        Assert.Equal(Today, await service.GetTargetDateAsync(Ct));
        Assert.Equal(AdvanceVoteStatus.VotingStarted, await service.GetStatusAsync(Today, Ct));
        Assert.Equal(AdvanceVoteStatus.VotingStarted, (await service.CancelAsync(Today, "U1", 1, Ct)).Status);
        poll.Status = PollStatus.Closed;
        await h.Fixture.Db.SaveChangesAsync(Ct);
        var tomorrow = Today.AddDays(1);
        Assert.Equal(tomorrow, await service.GetTargetDateAsync(Ct));
        Assert.Equal(AdvanceVoteStatus.Saved, (await service.CastAsync(tomorrow, "U1", h.B.Id, 0, Ct)).Status);
        h.Clock.UtcNow = At("2026-11-17T00:00:00+09:00");
        Assert.Equal(tomorrow, await service.GetTargetDateAsync(Ct));
        Assert.Equal(1, (await service.FindAsync(tomorrow, "U1", Ct))!.ChangeCount);
        h.Clock.UtcNow = At("2026-11-17T09:00:00+09:00");
        Assert.Equal(AdvanceVoteStatus.Saved, (await service.CastAsync(tomorrow, "U1", h.A.Id, 1, Ct)).Status);
        Assert.Equal(2, (await service.FindAsync(tomorrow, "U1", Ct))!.ChangeCount);
    }
}
