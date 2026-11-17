using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

public sealed class AdvanceVoteHandoffConcurrencyTests
{
    static readonly DateOnly Today = new(2026, 11, 16);
    static readonly DateTimeOffset Opens = new(2026, 11, 16, 11, 0, 0, TimeSpan.FromHours(9));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Database : IAsyncDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), $"handoff-{Guid.NewGuid():N}");
        string ConnectionString => $"Data Source={Path.Combine(directory, "votes.db")};Pooling=False;Default Timeout=30";
        public long PollId { get; private set; }
        public long First { get; private set; }
        public long Second { get; private set; }
        public LunchDbContext Open() => new(new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(ConnectionString).Options);

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(directory);
            await using var db = Open();
            await db.Database.EnsureCreatedAsync(Ct);
            var service = new LunchService(db, "C1");
            First = (await service.SaveRestaurantAsync(new(null, "한식", 1, null, null, null), "admin", Ct)).Id;
            Second = (await service.SaveRestaurantAsync(new(null, "일식", 3, null, null, null), "admin", Ct)).Id;
            db.AdvanceVotes.Add(new AdvanceVote { ChannelId = "C1", Date = Today, SlackUserId = "U1",
                RestaurantId = First, VotedAt = Opens.AddHours(-1), ChangeCount = 2 });
            await db.SaveChangesAsync(Ct);
            PollId = (await service.OpenPollAsync(Today, Opens, Opens.AddMinutes(30), Ct)).Id;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    static async Task RaceAsync(Database fixture, Func<LunchService, Task> left, Func<LunchService, Task> right)
    {
        var ct = Ct;
        using var start = new Barrier(3);
        async Task Run(Func<LunchService, Task> action)
        {
            await using var db = fixture.Open();
            start.SignalAndWait(ct);
            await action(new LunchService(db, "C1"));
        }
        var a = Task.Run(() => Run(left), ct);
        var b = Task.Run(() => Run(right), ct);
        start.SignalAndWait(ct);
        await Task.WhenAll(a, b);
    }

    [Fact]
    public async Task 동시_본투표는_한_표와_무효화된_사전기록을_남긴다()
    {
        await using var fixture = new Database();
        await fixture.InitializeAsync();
        var ct = Ct;
        await RaceAsync(fixture,
            s => s.CastAbstentionAsync(fixture.PollId, "U1", ct),
            s => s.CastVoteAsync(fixture.PollId, "U1", fixture.Second, ct));
        await using var db = fixture.Open();
        var vote = Assert.Single(await db.PollVotes.AsNoTracking().ToListAsync(ct));
        var advance = Assert.Single(await db.AdvanceVotes.AsNoTracking().ToListAsync(ct));
        Assert.Contains(vote.RestaurantId, new long?[] { null, fixture.Second });
        Assert.False(vote.IsFromAdvanceVote);
        Assert.True(advance.IsSupersededByMainVote);
        Assert.Equal(2, advance.ChangeCount);
    }

    [Fact]
    public async Task 별도_연결의_표_저장_실패는_사전효력도_롤백한다()
    {
        await using var fixture = new Database();
        await fixture.InitializeAsync();
        var ct = Ct;
        await using (var setup = fixture.Open())
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_vote_update BEFORE UPDATE ON PollVotes BEGIN SELECT RAISE(ABORT, '표 저장 실패'); END;", ct);
        await using (var writer = fixture.Open())
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new LunchService(writer, "C1").CastVoteAsync(fixture.PollId, "U1", fixture.Second, ct));
        await using var reader = fixture.Open();
        var advance = Assert.Single(await reader.AdvanceVotes.AsNoTracking().ToListAsync(ct));
        var vote = Assert.Single(await reader.PollVotes.AsNoTracking().ToListAsync(ct));
        Assert.False(advance.IsSupersededByMainVote);
        Assert.Equal(2, advance.ChangeCount);
        Assert.Equal(fixture.First, vote.RestaurantId);
        Assert.True(vote.IsFromAdvanceVote);
    }

    [Fact]
    public async Task 별도_연결의_초기표_저장_실패는_새_풀과_후보를_롤백한다()
    {
        await using var fixture = new Database();
        await fixture.InitializeAsync();
        var ct = Ct;
        var nextDate = Today.AddDays(1);
        await using (var setup = fixture.Open())
        {
            setup.AdvanceVotes.Add(new AdvanceVote
            {
                ChannelId = "C1", Date = nextDate, SlackUserId = "U2",
                RestaurantId = fixture.First, VotedAt = Opens, ChangeCount = 2,
            });
            await setup.SaveChangesAsync(ct);
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER reject_vote_insert BEFORE INSERT ON PollVotes BEGIN SELECT RAISE(ABORT, '초기표 저장 실패'); END;", ct);
        }
        await using (var writer = fixture.Open())
            await Assert.ThrowsAsync<DbUpdateException>(() => new LunchService(writer, "C1")
                .OpenPollAsync(nextDate, Opens.AddDays(1), Opens.AddDays(1).AddMinutes(30), ct));
        await using var reader = fixture.Open();
        Assert.False(await reader.Polls.AnyAsync(p => p.Date == nextDate, ct));
        Assert.Equal(2, (await reader.AdvanceVotes.SingleAsync(v => v.Date == nextDate, ct)).ChangeCount);
        Assert.Equal(1, await reader.PollVotes.CountAsync(ct));
    }

    [Fact]
    public async Task 마감과_변경_경합은_마감_집계와_저장된_표가_일치한다()
    {
        await using var fixture = new Database();
        await fixture.InitializeAsync();
        var ct = Ct;
        await RaceAsync(fixture,
            async s => { await s.ClosePollAsync(fixture.PollId, Today, new(), ct); },
            s => s.CastVoteAsync(fixture.PollId, "U1", fixture.Second, ct));
        await using var db = fixture.Open();
        var service = new LunchService(db, "C1");
        var vote = Assert.Single(await db.PollVotes.AsNoTracking().ToListAsync(ct));
        var advance = Assert.Single(await db.AdvanceVotes.AsNoTracking().ToListAsync(ct));
        var outcome = await service.GetClosedOutcomeAsync(fixture.PollId, ct);
        Assert.Equal(vote.RestaurantId, outcome.Winner!.RestaurantId);
        Assert.Equal(vote.RestaurantId == fixture.Second, advance.IsSupersededByMainVote);
        Assert.Equal(!advance.IsSupersededByMainVote, vote.IsFromAdvanceVote);
        Assert.Equal(2, advance.ChangeCount);
        await service.CastAbstentionAsync(fixture.PollId, "U1", ct);
        db.ChangeTracker.Clear();
        Assert.Equal(vote.RestaurantId, (await db.PollVotes.SingleAsync(ct)).RestaurantId);
        Assert.Equal(advance.IsSupersededByMainVote, (await db.AdvanceVotes.SingleAsync(ct)).IsSupersededByMainVote);
    }
}
