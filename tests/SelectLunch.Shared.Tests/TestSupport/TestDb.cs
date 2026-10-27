using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;

namespace SelectLunch.Shared.Tests;

/// <summary>
/// in-memory SQLite. 연결을 열어둔 채로 유지해야 DB가 살아 있다 — 연결이 닫히면 사라진다.
/// 실제 SQLite 엔진을 쓰므로 UNIQUE 제약 같은 것이 진짜로 검증된다.
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    readonly SqliteConnection _connection;

    public LunchDbContext Db { get; }

    TestDb(SqliteConnection connection, LunchDbContext db)
    {
        _connection = connection;
        Db = db;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new LunchDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return new TestDb(connection, db);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
