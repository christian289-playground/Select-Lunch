using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Members;

namespace SelectLunch.Shared.Tests;

public class ChannelRosterStoreTests
{
    const string Ws = "T01";
    const string Ch = "C01";
    static readonly DateTimeOffset T1 = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T2 = T1.AddDays(1);
    static readonly DateTimeOffset T3 = T1.AddDays(2);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static RosterPerson Person(string id, string realName, string displayName = "") =>
        new(id, realName, displayName);

    static ChannelRoster RosterOf(params RosterPerson[] people) => new(Ws, Ch, people);

    static async Task<ChannelMember> RowAsync(LunchDbContext db, string workspaceId, string channelId, string userId)
    {
        db.ChangeTracker.Clear();
        return await db.ChannelMembers.AsNoTracking().SingleAsync(
            m => m.WorkspaceId == workspaceId && m.ChannelId == channelId && m.SlackUserId == userId, Ct);
    }

    static async Task<List<ChannelMember>> AllRowsAsync(LunchDbContext db)
    {
        db.ChangeTracker.Clear();
        var rows = await db.ChannelMembers.AsNoTracking().ToListAsync(Ct);
        return rows
            .OrderBy(m => m.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(m => m.ChannelId, StringComparer.Ordinal)
            .ThenBy(m => m.SlackUserId, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task 처음_동기화하면_모든_사람이_활성_상태로_저장된다()
    {
        await using var fixture = await TestDb.CreateAsync();

        var result = await fixture.Db.ApplyChannelRosterAsync(
            RosterOf(Person("U1", "이상준", "sj"), Person("U2", "김철수")), T1, Ct);

        Assert.Equal(new RosterSyncResult(Added: 2, Updated: 0, Unchanged: 0, Deactivated: 0), result);
        var u1 = await RowAsync(fixture.Db, Ws, Ch, "U1");
        Assert.Equal("이상준", u1.RealName);
        Assert.Equal("sj", u1.DisplayName);
        Assert.True(u1.IsActive);
        Assert.Equal(T1, u1.LastSyncedAt);
        var u2 = await RowAsync(fixture.Db, Ws, Ch, "U2");
        Assert.Equal("", u2.DisplayName);
        Assert.True(u2.IsActive);
    }

    [Fact]
    public async Task 같은_명단을_다시_동기화해도_행이_늘지_않고_내용이_같다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var roster = RosterOf(Person("U1", "이상준", "sj"), Person("U2", "김철수"));
        await fixture.Db.ApplyChannelRosterAsync(roster, T1, Ct);

        var second = await fixture.Db.ApplyChannelRosterAsync(roster, T2, Ct);

        Assert.Equal(new RosterSyncResult(Added: 0, Updated: 0, Unchanged: 2, Deactivated: 0), second);
        var rows = await AllRowsAsync(fixture.Db);
        Assert.Equal(new[] { "U1", "U2" }, rows.Select(m => m.SlackUserId).ToArray());
        Assert.All(rows, m => Assert.True(m.IsActive));
        Assert.Equal("이상준", rows[0].RealName);
        Assert.Equal("sj", rows[0].DisplayName);
        // 확인된 시각만 최신으로 올라간다. 값은 그대로다.
        Assert.All(rows, m => Assert.Equal(T2, m.LastSyncedAt));
    }

    [Fact]
    public async Task 이름이_바뀌면_같은_행의_이름이_갱신된다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준", "sj")), T1, Ct);

        var result = await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준B", "sjb")), T2, Ct);

        Assert.Equal(new RosterSyncResult(Added: 0, Updated: 1, Unchanged: 0, Deactivated: 0), result);
        var rows = await AllRowsAsync(fixture.Db);
        var only = Assert.Single(rows);
        Assert.Equal("이상준B", only.RealName);
        Assert.Equal("sjb", only.DisplayName);
        Assert.Equal(T2, only.LastSyncedAt);
    }

    [Fact]
    public async Task 채널을_나간_사람은_삭제하지_않고_비활성으로_바꾼다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(
            RosterOf(Person("U1", "이상준"), Person("U2", "김철수", "kim")), T1, Ct);

        var result = await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준")), T2, Ct);

        Assert.Equal(new RosterSyncResult(Added: 0, Updated: 0, Unchanged: 1, Deactivated: 1), result);
        var left = await RowAsync(fixture.Db, Ws, Ch, "U2");
        Assert.False(left.IsActive);
        Assert.Equal("김철수", left.RealName);   // 이력은 보존된다
        Assert.Equal("kim", left.DisplayName);
        Assert.Equal(T2, left.LastSyncedAt);     // 비활성이 된 시각
        Assert.True((await RowAsync(fixture.Db, Ws, Ch, "U1")).IsActive);
    }

    [Fact]
    public async Task 이미_비활성인_사람은_다시_동기화해도_시각과_집계가_바뀌지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준"), Person("U2", "김철수")), T1, Ct);
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준")), T2, Ct);

        var third = await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준")), T3, Ct);

        Assert.Equal(new RosterSyncResult(Added: 0, Updated: 0, Unchanged: 1, Deactivated: 0), third);
        var left = await RowAsync(fixture.Db, Ws, Ch, "U2");
        Assert.False(left.IsActive);
        Assert.Equal(T2, left.LastSyncedAt);
    }

    [Fact]
    public async Task 나갔다_돌아온_사람은_같은_행이_다시_활성화된다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준"), Person("U2", "김철수")), T1, Ct);
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준")), T2, Ct);

        var result = await fixture.Db.ApplyChannelRosterAsync(
            RosterOf(Person("U1", "이상준"), Person("U2", "김철수")), T3, Ct);

        Assert.Equal(new RosterSyncResult(Added: 0, Updated: 1, Unchanged: 1, Deactivated: 0), result);
        var rows = await AllRowsAsync(fixture.Db);
        Assert.Equal(new[] { "U1", "U2" }, rows.Select(m => m.SlackUserId).ToArray());
        var back = rows[1];
        Assert.True(back.IsActive);
        Assert.Equal(T3, back.LastSyncedAt);
    }

    [Fact]
    public async Task 같은_사람이_두_채널에_있으면_채널마다_행이_따로_있고_서로_영향이_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(
            new ChannelRoster(Ws, "C01", [Person("U1", "이상준"), Person("U2", "김철수")]), T1, Ct);
        await fixture.Db.ApplyChannelRosterAsync(
            new ChannelRoster(Ws, "C02", [Person("U1", "이상준"), Person("U3", "박영희")]), T1, Ct);

        // C01에서 U1만 남고 U2가 나간다.
        await fixture.Db.ApplyChannelRosterAsync(
            new ChannelRoster(Ws, "C01", [Person("U1", "이상준")]), T2, Ct);

        Assert.False((await RowAsync(fixture.Db, Ws, "C01", "U2")).IsActive);
        var otherChannelU1 = await RowAsync(fixture.Db, Ws, "C02", "U1");
        Assert.True(otherChannelU1.IsActive);
        Assert.Equal(T1, otherChannelU1.LastSyncedAt);   // C02 행은 건드리지 않았다
        var otherChannelU3 = await RowAsync(fixture.Db, Ws, "C02", "U3");
        Assert.True(otherChannelU3.IsActive);
        Assert.Equal(T1, otherChannelU3.LastSyncedAt);
    }

    [Fact]
    public async Task 다른_워크스페이스의_같은_채널_ID와_같은_사용자_ID_행은_영향을_받지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(
            new ChannelRoster("T01", Ch, [Person("U1", "이상준"), Person("U2", "김철수")]), T1, Ct);
        await fixture.Db.ApplyChannelRosterAsync(
            new ChannelRoster("T02", Ch, [Person("U1", "다른회사사람"), Person("U2", "다른회사사람2")]), T1, Ct);

        await fixture.Db.ApplyChannelRosterAsync(new ChannelRoster("T01", Ch, []), T2, Ct);

        Assert.False((await RowAsync(fixture.Db, "T01", Ch, "U1")).IsActive);
        Assert.False((await RowAsync(fixture.Db, "T01", Ch, "U2")).IsActive);
        var other = await RowAsync(fixture.Db, "T02", Ch, "U1");
        Assert.True(other.IsActive);
        Assert.Equal("다른회사사람", other.RealName);
        Assert.Equal(T1, other.LastSyncedAt);
        Assert.True((await RowAsync(fixture.Db, "T02", Ch, "U2")).IsActive);
    }

    [Fact]
    public async Task 사람이_한_명도_없는_스냅샷은_그_채널의_활성_구성원을_모두_비활성으로_바꾼다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준"), Person("U2", "김철수")), T1, Ct);

        var result = await fixture.Db.ApplyChannelRosterAsync(RosterOf(), T2, Ct);

        Assert.Equal(new RosterSyncResult(Added: 0, Updated: 0, Unchanged: 0, Deactivated: 2), result);
        var rows = await AllRowsAsync(fixture.Db);
        Assert.All(rows, m => Assert.False(m.IsActive));
        Assert.Equal(new[] { "이상준", "김철수" }, rows.Select(m => m.RealName).ToArray());
    }

    [Fact]
    public async Task 스냅샷에_같은_사용자가_두_번_있으면_거부하고_DB를_바꾸지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준")), T1, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Db.ApplyChannelRosterAsync(
            RosterOf(Person("U9", "새사람"), Person("U9", "새사람")), T2, Ct));

        var rows = await AllRowsAsync(fixture.Db);
        var only = Assert.Single(rows);
        Assert.Equal("U1", only.SlackUserId);
        Assert.True(only.IsActive);
        Assert.Equal(T1, only.LastSyncedAt);
    }

    [Theory]
    [InlineData("", "C01")]
    [InlineData(" ", "C01")]
    [InlineData("T01", "")]
    [InlineData("T01", "  ")]
    public async Task 워크스페이스나_채널이_비어_있으면_거부한다(string workspaceId, string channelId)
    {
        await using var fixture = await TestDb.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Db.ApplyChannelRosterAsync(
            new ChannelRoster(workspaceId, channelId, [Person("U1", "이상준")]), T1, Ct));

        Assert.Empty(await AllRowsAsync(fixture.Db));
    }

    [Fact]
    public async Task 저장_도중_실패하면_이름_갱신과_퇴장_처리까지_전부_되돌린다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(
            RosterOf(Person("U1", "이상준", "sj"), Person("U2", "김철수")), T1, Ct);
        // 신규 행 하나만 SQLite 엔진이 거부하게 만든다 — 앞선 변경이 함께 되돌아가야 한다.
        await fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER reject_bad_member BEFORE INSERT ON ChannelMembers " +
            "WHEN NEW.SlackUserId = 'UBAD' BEGIN SELECT RAISE(ABORT, 'boom'); END;", Ct);

        // U1 개명 + U2 퇴장 + UBAD 신규(실패)
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.ApplyChannelRosterAsync(
            RosterOf(Person("U1", "새이름", "new"), Person("UBAD", "누군가")), T2, Ct));

        var rows = await AllRowsAsync(fixture.Db);
        Assert.Equal(new[] { "U1", "U2" }, rows.Select(m => m.SlackUserId).ToArray());
        Assert.Equal("이상준", rows[0].RealName);
        Assert.Equal("sj", rows[0].DisplayName);
        Assert.All(rows, m => Assert.True(m.IsActive));
        Assert.All(rows, m => Assert.Equal(T1, m.LastSyncedAt));
    }

    [Fact]
    public async Task 이미_취소된_토큰이면_DB를_바꾸지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await fixture.Db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준")), T1, Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Db.ApplyChannelRosterAsync(RosterOf(), T2, cts.Token));

        var only = Assert.Single(await AllRowsAsync(fixture.Db));
        Assert.True(only.IsActive);
        Assert.Equal(T1, only.LastSyncedAt);
    }

    [Fact]
    public async Task 활성_명단_조회는_활성_구성원만_워크스페이스_채널_실명_순으로_돌려준다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var db = fixture.Db;
        await db.ApplyChannelRosterAsync(
            new ChannelRoster("T01", "C01", [Person("U1", "이", "i"), Person("U2", "김"), Person("U3", "박")]), T1, Ct);
        await db.ApplyChannelRosterAsync(new ChannelRoster("T01", "C02", [Person("U1", "이")]), T1, Ct);
        await db.ApplyChannelRosterAsync(new ChannelRoster("T02", "C01", [Person("U9", "최")]), T1, Ct);
        // U3는 C01을 나갔다.
        await db.ApplyChannelRosterAsync(
            new ChannelRoster("T01", "C01", [Person("U1", "이", "i"), Person("U2", "김")]), T2, Ct);

        var active = await db.GetActiveChannelMembersAsync(null, null, Ct);

        Assert.Equal(
            new[] { "T01/C01/U2", "T01/C01/U1", "T01/C02/U1", "T02/C01/U9" },
            active.Select(m => $"{m.WorkspaceId}/{m.ChannelId}/{m.SlackUserId}").ToArray());
    }

    [Fact]
    public async Task 활성_명단_조회는_워크스페이스와_채널로_좁힐_수_있다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var db = fixture.Db;
        await db.ApplyChannelRosterAsync(new ChannelRoster("T01", "C01", [Person("U1", "이"), Person("U2", "김")]), T1, Ct);
        await db.ApplyChannelRosterAsync(new ChannelRoster("T01", "C02", [Person("U1", "이")]), T1, Ct);
        await db.ApplyChannelRosterAsync(new ChannelRoster("T02", "C01", [Person("U9", "최")]), T1, Ct);

        var byBoth = await db.GetActiveChannelMembersAsync("T01", "C01", Ct);
        var byWorkspace = await db.GetActiveChannelMembersAsync("T02", null, Ct);
        var byChannel = await db.GetActiveChannelMembersAsync(null, "C02", Ct);

        Assert.Equal(new[] { "U2", "U1" }, byBoth.Select(m => m.SlackUserId).ToArray());
        Assert.Equal(new[] { "U9" }, byWorkspace.Select(m => m.SlackUserId).ToArray());
        Assert.Equal(new[] { "U1" }, byChannel.Select(m => m.SlackUserId).ToArray());
    }

    [Fact]
    public async Task 활성_명단_조회는_데이터가_없으면_빈_목록을_돌려준다()
    {
        await using var fixture = await TestDb.CreateAsync();

        Assert.Empty(await fixture.Db.GetActiveChannelMembersAsync(null, null, Ct));
        Assert.Empty(await fixture.Db.GetActiveChannelMembersAsync("T404", "C404", Ct));
    }

    [Fact]
    public async Task 같은_키의_행을_직접_두_번_넣을_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        static ChannelMember Row() => new()
        {
            WorkspaceId = Ws, ChannelId = Ch, SlackUserId = "U1",
            RealName = "이상준", DisplayName = "", IsActive = true, LastSyncedAt = T1,
        };
        fixture.Db.ChannelMembers.Add(Row());
        await fixture.Db.SaveChangesAsync(Ct);
        fixture.Db.ChangeTracker.Clear();

        fixture.Db.ChannelMembers.Add(Row());

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task 마이그레이션으로_만든_DB에도_명단을_저장하고_같은_키는_거부한다()
    {
        // 엔티티만 고치고 마이그레이션 생성을 잊으면 여기서 잡힌다.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);
        await db.Database.MigrateAsync(Ct);

        await db.ApplyChannelRosterAsync(RosterOf(Person("U1", "이상준", "sj")), T1, Ct);
        db.ChangeTracker.Clear();
        db.ChannelMembers.Add(new ChannelMember
        {
            WorkspaceId = Ws, ChannelId = Ch, SlackUserId = "U1",
            RealName = "중복", DisplayName = "", IsActive = true, LastSyncedAt = T2,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
