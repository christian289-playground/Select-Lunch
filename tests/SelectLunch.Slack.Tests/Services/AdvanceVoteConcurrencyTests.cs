using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

public sealed class AdvanceVoteConcurrencyTests
{
    static readonly DateOnly Today = new(2026, 11, 16);
    static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(9));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 조회_도중_식당표가_기권으로_바뀌어도_한_사람을_두번_집계하지_않는다()
    {
        await using var fixture = new Database();
        await fixture.InitializeAsync();
        await using (var setup = fixture.Open())
            await fixture.Service(setup).CastAsync(Today, "U1", fixture.A, 0, Ct);
        var change = new ChangeAfterFirstRead(async () =>
        {
            await using var writer = fixture.Open();
            Assert.Equal(AdvanceVoteStatus.Saved,
                (await fixture.Service(writer).CastAsync(Today, "U1", null, 1, Ct)).Status);
        });
        await using var reader = fixture.Open(change);

        var snapshot = await fixture.Service(reader).GetSnapshotAsync(Today, Ct);

        Assert.Equal(1, snapshot.Tallies.Sum(t => t.Count) + snapshot.Abstentions);
        await using var after = fixture.Open();
        var vote = await fixture.Service(after).FindAsync(Today, "U1", Ct);
        Assert.NotNull(vote);
        Assert.Null(vote.RestaurantId);
        Assert.Equal(2, vote.ChangeCount);
    }

    sealed class ChangeAfterFirstRead(Func<Task> change) : DbCommandInterceptor
    {
        bool changed;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!changed)
            {
                changed = true;
                await change();
            }
            return result;
        }
    }

    sealed class Database : IAsyncDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), $"advance-vote-{Guid.NewGuid():N}");
        public long A { get; private set; }
        public long B { get; private set; }
        public TestTimeProvider Clock { get; } = new(Now);
        string ConnectionString => $"Data Source={Path.Combine(directory, "votes.db")};Pooling=False;Default Timeout=30";

        public LunchDbContext Open(params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(ConnectionString).AddInterceptors(interceptors).Options);
        public AdvanceVoteService Service(LunchDbContext db) => new(db, "C1",
            StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions()))), Clock);

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(directory);
            await using var db = Open();
            await db.Database.EnsureCreatedAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", Ct);
            var lunch = new LunchService(db, "C1");
            A = (await lunch.SaveRestaurantAsync(new(null, "김밥", 1, null, null, null), "admin", Ct)).Id;
            B = (await lunch.SaveRestaurantAsync(new(null, "초밥", 3, null, null, null), "admin", Ct)).Id;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    static async Task<AdvanceVoteResult[]> RaceAsync(Database fixture,
        Func<AdvanceVoteService, Task<AdvanceVoteResult>> left,
        Func<AdvanceVoteService, Task<AdvanceVoteResult>> right)
    {
        using var start = new Barrier(3);
        async Task<AdvanceVoteResult> Run(Func<AdvanceVoteService, Task<AdvanceVoteResult>> action)
        {
            await using var db = fixture.Open();
            start.SignalAndWait(Ct);
            return await action(fixture.Service(db));
        }
        var a = Task.Run(() => Run(left), Ct);
        var b = Task.Run(() => Run(right), Ct);
        start.SignalAndWait(Ct);
        return await Task.WhenAll(a, b);
    }

    [Fact]
    public async Task 동시_최초_선택은_하나만_저장된다()
    {
        await using var f = new Database();
        await f.InitializeAsync();
        var results = await RaceAsync(f,
            s => s.CastAsync(Today, "U1", f.A, 0, Ct),
            s => s.CastAsync(Today, "U1", f.B, 0, Ct));
        Assert.Single(results, r => r.Status == AdvanceVoteStatus.Saved);
        Assert.Single(results, r => r.Status == AdvanceVoteStatus.Conflict);
        await using var db = f.Open();
        var row = Assert.Single(await db.AdvanceVotes.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(1, row.ChangeCount);
        Assert.Contains(row.RestaurantId, new long?[] { f.A, f.B });
    }

    [Fact]
    public async Task 마지막_한_번의_동시_변경은_하나만_성공한다()
    {
        await using var f = new Database();
        await f.InitializeAsync();
        await using (var db = f.Open())
        {
            var s = f.Service(db);
            Assert.Equal(AdvanceVoteStatus.Saved, (await s.CastAsync(Today, "U1", f.A, 0, Ct)).Status);
            Assert.Equal(AdvanceVoteStatus.Saved, (await s.CastAsync(Today, "U1", f.B, 1, Ct)).Status);
        }
        var results = await RaceAsync(f,
            s => s.CastAsync(Today, "U1", f.A, 2, Ct),
            s => s.CancelAsync(Today, "U1", 2, Ct));
        Assert.Single(results, r => r.Status is AdvanceVoteStatus.Saved or AdvanceVoteStatus.Cancelled);
        Assert.Single(results, r => r.Status is AdvanceVoteStatus.LimitReached or AdvanceVoteStatus.Conflict);
        await using var verify = f.Open();
        Assert.Equal(3, (await f.Service(verify).FindAsync(Today, "U1", Ct))!.ChangeCount);
    }

    [Fact]
    public async Task 다른_사용자의_동일_식당_동시_선택은_두_표가_된다()
    {
        await using var f = new Database();
        await f.InitializeAsync();
        var results = await RaceAsync(f,
            s => s.CastAsync(Today, "U1", f.A, 0, Ct),
            s => s.CastAsync(Today, "U2", f.A, 0, Ct));
        Assert.All(results, r => Assert.Equal(AdvanceVoteStatus.Saved, r.Status));
        Assert.Equal(new[] { 1, 2 }, results.Select(r => Assert.Single(r.Change!.Snapshot.Tallies).Count).Order().ToArray());
        await using var db = f.Open();
        var tally = Assert.Single((await f.Service(db).GetSnapshotAsync(Today, Ct)).Tallies);
        Assert.Equal((f.A, 2), (tally.RestaurantId, tally.Count));
    }

    [Fact]
    public async Task 동일_요청의_동시_중복은_한번만_차감한다()
    {
        await using var f = new Database();
        await f.InitializeAsync();
        var results = await RaceAsync(f,
            s => s.CastAsync(Today, "U1", f.A, 0, Ct),
            s => s.CastAsync(Today, "U1", f.A, 0, Ct));
        Assert.Single(results, r => r.Status == AdvanceVoteStatus.Saved);
        Assert.Single(results, r => r.Status == AdvanceVoteStatus.Unchanged && r.Change is null);
        await using var db = f.Open();
        Assert.Equal(1, (await f.Service(db).FindAsync(Today, "U1", Ct))!.ChangeCount);
    }
}
