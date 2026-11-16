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
    public async Task 개시는_당일_해당채널의_유효한표와_기권만_이동한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var active = await service.SaveRestaurantAsync(new(null, "선택식당", 1, null, null, null), "U0", Ct);
        var archived = await service.SaveRestaurantAsync(new(null, "보관식당", 3, null, null, null), "U0", Ct);
        archived.Status = RestaurantStatus.Archived;
        fixture.Db.AdvanceVotes.AddRange(
            Vote("U1", active.Id), Vote("U2", null), Vote("U3", archived.Id),
            Vote("U4", active.Id, "C2"), Vote("U5", active.Id, date: Today.AddDays(-1)));
        await fixture.Db.SaveChangesAsync(Ct);
        fixture.Db.ChangeTracker.Clear();

        var poll = await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct);

        var votes = await fixture.Db.PollVotes.OrderBy(v => v.SlackUserId).ToListAsync(Ct);
        Assert.Collection(votes,
            v => { Assert.Equal("U1", v.SlackUserId); Assert.Equal(active.Id, v.RestaurantId); Assert.False(v.IsSecret); },
            v => { Assert.Equal("U2", v.SlackUserId); Assert.Null(v.RestaurantId); });
        Assert.All(votes, v => Assert.Equal(poll.Id, v.PollId));
        Assert.Equal(new[] { "U4", "U5" }, await fixture.Db.AdvanceVotes.OrderBy(v => v.SlackUserId).Select(v => v.SlackUserId).ToArrayAsync(Ct));
        Assert.Equal(1, Assert.Single(await service.GetTalliesAsync(poll.Id, Ct)).Count);
    }

    [Fact]
    public async Task 표_이동에_실패하면_사전투표를_보존하고_빈_본투표도_남기지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = await new LunchService(fixture.Db, Channel)
            .SaveRestaurantAsync(new(null, "선택식당", 1, null, null, null), "U0", Ct);
        fixture.Db.AdvanceVotes.Add(Vote("U1", restaurant.Id));
        await fixture.Db.SaveChangesAsync(Ct);
        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(fixture.Db.Database.GetDbConnection())
            .AddInterceptors(new RejectVoteSave()).Options;
        await using var failing = new LunchDbContext(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LunchService(failing, Channel).OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct));

        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.Polls.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollCandidates.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal(restaurant.Id, Assert.Single(await fixture.Db.AdvanceVotes.ToListAsync(Ct)).RestaurantId);
    }

    sealed class RejectVoteSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PollVote>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("표 저장 실패");
            return ValueTask.FromResult(result);
        }
    }

    static AdvanceVote Vote(string user, long? restaurant, string channel = Channel, DateOnly? date = null) => new()
    {
        ChannelId = channel, Date = date ?? Today, SlackUserId = user,
        RestaurantId = restaurant, VotedAt = Opens.AddHours(-1),
    };
}
