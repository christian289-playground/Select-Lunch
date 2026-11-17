using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

public sealed class AdvanceVoteTransferTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 11, 16);
    static readonly DateTimeOffset Opens = new(2026, 11, 16, 11, 0, 0, TimeSpan.FromHours(9));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 개시는_유효한_사전표만_복사하고_원본과_횟수를_보존한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var active = await service.SaveRestaurantAsync(new(null, "선택식당", 1, null, null, null), "U0", Ct);
        var other = await service.SaveRestaurantAsync(new(null, "본투표식당", 3, null, null, null), "U0", Ct);
        var archived = await service.SaveRestaurantAsync(new(null, "보관식당", 3, null, null, null), "U0", Ct);
        archived.Status = RestaurantStatus.Archived;
        var legacy = Vote("U6", active.Id); legacy.ChangeCount = 0;
        var cancelled = Vote("U7", active.Id); cancelled.IsCancelled = true;
        var superseded = Vote("U8", active.Id); superseded.IsSupersededByMainVote = true;
        fixture.Db.AdvanceVotes.AddRange(
            Vote("U1", active.Id), Vote("U2", null), Vote("U3", archived.Id),
            Vote("U4", active.Id, "C2"), Vote("U5", active.Id, date: Today.AddDays(-1)),
            legacy, cancelled, superseded);
        await fixture.Db.SaveChangesAsync(Ct);
        fixture.Db.ChangeTracker.Clear();

        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);

        var copied = await fixture.Db.PollVotes.OrderBy(v => v.SlackUserId).ToListAsync(Ct);
        Assert.Equal(new[] { "U1", "U2", "U6" }, copied.Select(v => v.SlackUserId));
        Assert.All(copied, v => Assert.True(v.IsFromAdvanceVote));
        Assert.All(copied, v => Assert.Equal(Opens.AddHours(-1), v.VotedAt));
        Assert.Equal(2, Assert.Single(await service.GetTalliesAsync(poll.Id, Ct)).Count);
        var summary = await service.GetAbstainersAsync(poll.Id, Ct);
        Assert.Empty(summary.NamedUserIds);
        Assert.Equal(1, summary.AnonymousCount);
        var advanceVotes = await fixture.Db.AdvanceVotes.OrderBy(v => v.SlackUserId).ToListAsync(Ct);
        Assert.Equal(0, advanceVotes.Single(v => v.SlackUserId == "U6").ChangeCount);
        Assert.Equal(2, advanceVotes.Single(v => v.SlackUserId == "U1").ChangeCount);

        await service.CastVoteAsync(poll.Id, "U1", other.Id, Ct);
        await service.CastVoteAsync(poll.Id, "U1", active.Id, Ct);
        var changed = await fixture.Db.PollVotes.SingleAsync(v => v.SlackUserId == "U1", Ct);
        Assert.Equal(active.Id, changed.RestaurantId);
        Assert.False(changed.IsFromAdvanceVote);
        var original = await fixture.Db.AdvanceVotes.AsNoTracking()
            .SingleAsync(v => v.ChannelId == Channel && v.Date == Today && v.SlackUserId == "U1", Ct);
        Assert.Equal(active.Id, original.RestaurantId);
        Assert.True(original.IsSupersededByMainVote);
        Assert.Equal(2, original.ChangeCount);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("abstain")]
    [InlineData("random")]
    [InlineData("random-all")]
    [InlineData("secret")]
    public async Task 본투표_실제변경은_방식에_관계없이_사전표를_무효화한다(string kind)
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel, new Random(1));
        var first = await service.SaveRestaurantAsync(new(null, "한식", 1, null, null, null), "admin", Ct);
        var second = await service.SaveRestaurantAsync(new(null, "일식", 3, null, null, null), "admin", Ct);
        fixture.Db.AdvanceVotes.Add(Vote("U1", kind == "random-all" ? null : first.Id));
        await fixture.Db.SaveChangesAsync(Ct);
        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);
        switch (kind)
        {
            case "ordinary": await service.CastVoteAsync(poll.Id, "U1", second.Id, Ct); break;
            case "abstain": await service.CastAbstentionAsync(poll.Id, "U1", Ct); break;
            case "random": await service.CastRandomVoteAsync(poll.Id, "U1", 3, Ct); break;
            case "random-all": await service.CastRandomVoteAsync(poll.Id, "U1", null, Ct); break;
            case "secret": await service.CastSecretRandomVoteAsync(poll.Id, "U1", Ct); break;
        }
        var vote = Assert.Single(await fixture.Db.PollVotes.AsNoTracking().ToListAsync(Ct));
        var advance = Assert.Single(await fixture.Db.AdvanceVotes.AsNoTracking().ToListAsync(Ct));
        Assert.False(vote.IsFromAdvanceVote);
        Assert.True(advance.IsSupersededByMainVote);
        Assert.Equal(2, advance.ChangeCount);
    }

    [Fact]
    public async Task 동일_선택과_기권은_출처를_보존하고_변경후_기권은_실명이다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var restaurant = await service.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "admin", Ct);
        fixture.Db.AdvanceVotes.AddRange(Vote("U1", restaurant.Id), Vote("U2", null));
        await fixture.Db.SaveChangesAsync(Ct);
        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);
        await service.CastVoteAsync(poll.Id, "U1", restaurant.Id, Ct);
        await service.CastAbstentionAsync(poll.Id, "U2", Ct);
        Assert.All(await fixture.Db.PollVotes.ToListAsync(Ct), v => Assert.True(v.IsFromAdvanceVote));
        Assert.All(await fixture.Db.PollVotes.ToListAsync(Ct), v => Assert.Equal(Opens.AddHours(-1), v.VotedAt));
        Assert.All(await fixture.Db.AdvanceVotes.ToListAsync(Ct), v => Assert.False(v.IsSupersededByMainVote));
        await service.CastVoteAsync(poll.Id, "U2", restaurant.Id, Ct);
        await service.CastAbstentionAsync(poll.Id, "U2", Ct);
        var summary = await service.GetAbstainersAsync(poll.Id, Ct);
        Assert.Equal(new[] { "U2" }, summary.NamedUserIds);
        Assert.Equal(0, summary.AnonymousCount);
    }

    [Fact]
    public async Task 마감과_다른_채널의_요청은_사전표를_무효화하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var restaurant = await service.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "admin", Ct);
        fixture.Db.AdvanceVotes.AddRange(Vote("U1", restaurant.Id), Vote("U2", null));
        await fixture.Db.SaveChangesAsync(Ct);
        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);
        await new LunchService(fixture.Db, "C2").CastAbstentionAsync(poll.Id, "U1", Ct);
        var outcome = await service.ClosePollAsync(poll.Id, Today, new(), Ct);
        var restored = await service.GetClosedOutcomeAsync(poll.Id, Ct);
        Assert.Empty(outcome.Abstainers);
        Assert.Equal(1, outcome.AnonymousAbstentionCount);
        Assert.Equal(1, restored.AnonymousAbstentionCount);
        await service.CastAbstentionAsync(poll.Id, "U1", Ct);
        Assert.All(await fixture.Db.AdvanceVotes.ToListAsync(Ct), v => Assert.False(v.IsSupersededByMainVote));
        Assert.Equal(restaurant.Id, (await fixture.Db.PollVotes.SingleAsync(v => v.SlackUserId == "U1", Ct)).RestaurantId);
    }

    [Fact]
    public async Task 초기표_저장_실패는_풀과_후보도_롤백한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var restaurant = await service.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "admin", Ct);
        fixture.Db.AdvanceVotes.Add(Vote("U1", restaurant.Id));
        await fixture.Db.SaveChangesAsync(Ct);
        await fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_vote_insert BEFORE INSERT ON PollVotes BEGIN SELECT RAISE(ABORT, '초기표 저장 실패'); END;", Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct));
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.Polls.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollCandidates.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal(2, (await fixture.Db.AdvanceVotes.SingleAsync(Ct)).ChangeCount);
    }

    [Fact]
    public async Task 본투표_저장_실패시_사전효력도_롤백된다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var restaurant = await service.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "admin", Ct);
        fixture.Db.AdvanceVotes.Add(Vote("U1", restaurant.Id));
        await fixture.Db.SaveChangesAsync(Ct);
        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);
        await fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_vote_update BEFORE UPDATE ON PollVotes BEGIN SELECT RAISE(ABORT, '표 저장 실패'); END;", Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.CastAbstentionAsync(poll.Id, "U1", Ct));
        fixture.Db.ChangeTracker.Clear();
        Assert.False((await fixture.Db.AdvanceVotes.SingleAsync(Ct)).IsSupersededByMainVote);
        var vote = await fixture.Db.PollVotes.SingleAsync(Ct);
        Assert.True(vote.IsFromAdvanceVote);
        Assert.Equal(restaurant.Id, vote.RestaurantId);
    }


    [Fact]
    public async Task 본투표_후보저장에_실패해도_사전선택과_횟수를_보존한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = await new LunchService(fixture.Db, Channel)
            .SaveRestaurantAsync(new(null, "선택식당", 1, null, null, null), "U0", Ct);
        fixture.Db.AdvanceVotes.Add(Vote("U1", restaurant.Id));
        await fixture.Db.SaveChangesAsync(Ct);
        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(fixture.Db.Database.GetDbConnection())
            .AddInterceptors(new RejectCandidateSave()).Options;
        await using var failing = new LunchDbContext(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LunchService(failing, Channel).OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct));

        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.Polls.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollCandidates.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal(restaurant.Id, Assert.Single(await fixture.Db.AdvanceVotes.ToListAsync(Ct)).RestaurantId);
        Assert.Equal(2, Assert.Single(await fixture.Db.AdvanceVotes.ToListAsync(Ct)).ChangeCount);
    }

    sealed class RejectCandidateSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PollCandidate>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("후보 저장 실패");
            return ValueTask.FromResult(result);
        }
    }

    static AdvanceVote Vote(string user, long? restaurant, string channel = Channel, DateOnly? date = null) => new()
    {
        ChannelId = channel, Date = date ?? Today, SlackUserId = user,
        RestaurantId = restaurant, VotedAt = Opens.AddHours(-1), ChangeCount = 2,
    };
}
