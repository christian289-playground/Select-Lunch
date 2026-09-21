using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
}
