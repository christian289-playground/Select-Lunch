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
    public async Task 개시는_사전표와_횟수를_보존하고_본투표를_빈_표로_시작한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var active = await service.SaveRestaurantAsync(new(null, "선택식당", 1, null, null, null), "U0", Ct);
        var other = await service.SaveRestaurantAsync(new(null, "본투표식당", 3, null, null, null), "U0", Ct);
        var archived = await service.SaveRestaurantAsync(new(null, "보관식당", 3, null, null, null), "U0", Ct);
        archived.Status = RestaurantStatus.Archived;
        fixture.Db.AdvanceVotes.AddRange(
            Vote("U1", active.Id), Vote("U2", null), Vote("U3", archived.Id),
            Vote("U4", active.Id, "C2"), Vote("U5", active.Id, date: Today.AddDays(-1)));
        await fixture.Db.SaveChangesAsync(Ct);
        fixture.Db.ChangeTracker.Clear();

        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);

        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Empty(await service.GetTalliesAsync(poll.Id, Ct));
        var advanceVotes = await fixture.Db.AdvanceVotes.OrderBy(v => v.SlackUserId).ToListAsync(Ct);
        Assert.Equal(new[] { "U1", "U2", "U3", "U4", "U5" }, advanceVotes.Select(v => v.SlackUserId));
        Assert.All(advanceVotes, v => Assert.Equal(2, v.ChangeCount));

        await service.CastVoteAsync(poll.Id, "U1", other.Id, Ct);
        Assert.Equal(other.Id, Assert.Single(await fixture.Db.PollVotes.ToListAsync(Ct)).RestaurantId);
        var original = await fixture.Db.AdvanceVotes.AsNoTracking()
            .SingleAsync(v => v.ChannelId == Channel && v.Date == Today && v.SlackUserId == "U1", Ct);
        Assert.Equal(active.Id, original.RestaurantId);
        Assert.Equal(2, original.ChangeCount);
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
