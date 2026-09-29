using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SelectLunch.Shared.Data;

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
}
