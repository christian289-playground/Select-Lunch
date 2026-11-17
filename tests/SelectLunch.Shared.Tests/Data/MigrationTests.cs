using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Tests;

public class MigrationTests
{
    [Fact]
    public async Task 마이그레이션으로_스키마와_기본_카테고리가_생성된다()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, categories.Count);
        Assert.Equal("한식", categories[0].Name);
        Assert.All(categories, c => Assert.True(c.IsBuiltIn));
    }

    [Fact]
    public async Task 적용되지_않은_모델_변경이_남아_있지_않다()
    {
        // 엔티티를 고치고 마이그레이션 생성을 잊으면 여기서 잡힌다.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task 기존_사용자_카테고리도_마이그레이션이_정규화_이름을_채운다()
    {
        // 메타데이터 마이그레이션 이전 상태의 DB에 사용자 카테고리가 이미 있어도
        // UNIQUE(NormalizedName) 생성이 깨지지 않아야 한다.
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        await migrator.MigrateAsync("AllowVoteAbstention", ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Categories (Name, IsBuiltIn, CreatedAt) VALUES ('Thai Food', 0, '2026-01-01')", ct);
        await migrator.MigrateAsync(null, ct);

        var custom = await db.Categories.SingleAsync(c => c.Name == "Thai Food", ct);
        Assert.Equal("thaifood", custom.NormalizedName);
        Assert.Equal("한식", (await db.Categories.SingleAsync(c => c.Id == 1, ct)).NormalizedName);
    }

    [Fact]
    public async Task 변경한도_추가는_기존_사전선택을_보존하고_공개변경_횟수는_영으로_시작한다()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("AddAdvanceVotes", ct);
        var restaurant = new Restaurant
        {
            Name = "기존선택식당", NormalizedName = Restaurant.Normalize("기존선택식당"),
            CategoryId = 1, Status = RestaurantStatus.Active, CreatedBySlackUserId = "U0",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        db.Restaurants.Add(restaurant);
        await db.SaveChangesAsync(ct);
        var date = new DateOnly(2026, 11, 17);
        var legacyOptions = new DbContextOptionsBuilder<LegacyAdvanceVoteDb>().UseSqlite(connection).Options;
        await using (var legacy = new LegacyAdvanceVoteDb(legacyOptions))
        {
            legacy.Set<AdvanceVote>().Add(new AdvanceVote
            {
                ChannelId = "C1", Date = date, SlackUserId = "U1",
                RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
            });
            await legacy.SaveChangesAsync(ct);
        }

        await migrator.MigrateAsync(null, ct);

        var vote = await db.AdvanceVotes.AsNoTracking().SingleAsync(ct);
        Assert.Equal(("C1", date, "U1", (long?)restaurant.Id),
            (vote.ChannelId, vote.Date, vote.SlackUserId, vote.RestaurantId));
        Assert.Equal(0, vote.ChangeCount);
        Assert.False(vote.IsCancelled);
        Assert.Equal(DateTimeOffset.UnixEpoch, vote.VotedAt);
    }

    [Fact]
    public async Task 본투표_연계_추가는_기존_표와_횟수를_보존하고_과거_본투표의_출처를_추정하지_않는다()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new LunchDbContext(new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("AddAdvanceVoteChangeLimit", ct);
        var date = new DateOnly(2026, 11, 17);
        var restaurant = new Restaurant
        {
            Name = "기존선택식당", NormalizedName = Restaurant.Normalize("기존선택식당"),
            CategoryId = 1, Status = RestaurantStatus.Active, CreatedBySlackUserId = "U0",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        var poll = new LunchPoll
        {
            ChannelId = "C1", Date = date, Status = PollStatus.Closed,
            OpensAt = DateTimeOffset.UnixEpoch, ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
        };
        db.Restaurants.Add(restaurant);
        db.Polls.Add(poll);
        await db.SaveChangesAsync(ct);
        var votedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        await using (var legacy = new LegacyMainHandoffDb(new DbContextOptionsBuilder<LegacyMainHandoffDb>().UseSqlite(connection).Options))
        {
            legacy.Set<AdvanceVote>().AddRange(
                new AdvanceVote
                {
                    ChannelId = "C1", Date = date, SlackUserId = "U1", RestaurantId = restaurant.Id,
                    VotedAt = DateTimeOffset.UnixEpoch, ChangeCount = 2,
                },
                new AdvanceVote
                {
                    ChannelId = "C1", Date = date, SlackUserId = "U2",
                    VotedAt = DateTimeOffset.UnixEpoch, ChangeCount = 3,
                },
                new AdvanceVote
                {
                    ChannelId = "C1", Date = date, SlackUserId = "U3",
                    VotedAt = DateTimeOffset.UnixEpoch, ChangeCount = 2, IsCancelled = true,
                });
            legacy.Set<PollVote>().AddRange(
                new PollVote { PollId = poll.Id, SlackUserId = "U1", RestaurantId = restaurant.Id, IsSecret = true, VotedAt = votedAt },
                new PollVote { PollId = poll.Id, SlackUserId = "U2", VotedAt = votedAt });
            await legacy.SaveChangesAsync(ct);
        }

        await migrator.MigrateAsync(null, ct);

        var advance = await db.AdvanceVotes.AsNoTracking().OrderBy(v => v.SlackUserId).ToListAsync(ct);
        Assert.Collection(advance,
            v => Assert.Equal(("U1", (long?)restaurant.Id, 2, false), (v.SlackUserId, v.RestaurantId, v.ChangeCount, v.IsCancelled)),
            v => Assert.Equal(("U2", (long?)null, 3, false), (v.SlackUserId, v.RestaurantId, v.ChangeCount, v.IsCancelled)),
            v => Assert.Equal(("U3", (long?)null, 2, true), (v.SlackUserId, v.RestaurantId, v.ChangeCount, v.IsCancelled)));
        Assert.All(advance, v =>
        {
            Assert.False(v.IsSupersededByMainVote);
            Assert.Equal(("C1", date, DateTimeOffset.UnixEpoch), (v.ChannelId, v.Date, v.VotedAt));
        });
        var main = await db.PollVotes.AsNoTracking().OrderBy(v => v.SlackUserId).ToListAsync(ct);
        Assert.Collection(main,
            v => Assert.Equal(("U1", (long?)restaurant.Id, true), (v.SlackUserId, v.RestaurantId, v.IsSecret)),
            v => Assert.Equal(("U2", (long?)null, false), (v.SlackUserId, v.RestaurantId, v.IsSecret)));
        Assert.All(main, v =>
        {
            Assert.False(v.IsFromAdvanceVote);
            Assert.Equal((poll.Id, votedAt), (v.PollId, v.VotedAt));
        });
        Assert.Equal(PollStatus.Closed, (await db.Polls.AsNoTracking().SingleAsync(ct)).Status);
    }

    sealed class LegacyAdvanceVoteDb(DbContextOptions<LegacyAdvanceVoteDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<AdvanceVote>(e =>
            {
                e.ToTable("AdvanceVotes");
                e.HasKey(v => new { v.ChannelId, v.Date, v.SlackUserId });
                e.Ignore(nameof(AdvanceVote.ChangeCount));
                e.Ignore(nameof(AdvanceVote.IsCancelled));
                e.Ignore(nameof(AdvanceVote.IsSupersededByMainVote));
                e.Ignore(nameof(AdvanceVote.Restaurant));
            });
    }

    sealed class LegacyMainHandoffDb(DbContextOptions<LegacyMainHandoffDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AdvanceVote>(e =>
            {
                e.ToTable("AdvanceVotes");
                e.HasKey(v => new { v.ChannelId, v.Date, v.SlackUserId });
                e.Ignore(nameof(AdvanceVote.IsSupersededByMainVote));
                e.Ignore(nameof(AdvanceVote.Restaurant));
            });
            modelBuilder.Entity<PollVote>(e =>
            {
                e.ToTable("PollVotes");
                e.Ignore(nameof(PollVote.IsFromAdvanceVote));
                e.Ignore(nameof(PollVote.Poll));
                e.Ignore(nameof(PollVote.Restaurant));
            });
        }
    }
}
