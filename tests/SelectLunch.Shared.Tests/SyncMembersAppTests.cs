using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Members;
using SyncMembers;

namespace SelectLunch.Shared.Tests;

/// <summary>
/// 도우미 CLI 전체(인수 해석 → 설정 → Slack → 실제 SQLite 파일). 파일 DB는 마이그레이션으로 만든다 —
/// 운영 경로(Migrate)를 그대로 타므로 마이그레이션이 생성돼 있어야 통과한다.
/// </summary>
public class SyncMembersAppTests
{
    const string Token = "xoxb-test-secret";
    static readonly DateTimeOffset Now = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly Func<HttpClient> NoNetwork =
        () => throw new InvalidOperationException("이 경로에서는 네트워크를 만들면 안 된다.");

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed record RunResult(int ExitCode, string Out, string Err);

    static async Task<RunResult> RunAsync(
        string[] args, Func<HttpClient>? http = null, DateTimeOffset? now = null, CancellationToken? ct = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new SyncMembersApp(stdout, stderr, http ?? NoNetwork, new FixedTimeProvider(now ?? Now));
        var code = await app.RunAsync(args, ct ?? Ct);
        return new RunResult(code, stdout.ToString(), stderr.ToString());
    }

    static string WriteConfig(TempDir dir, string token = Token, string channel = "C01") =>
        dir.Write("appsettings.Local.json",
            $$"""{ "Slack": { "BotToken": "{{token}}", "AppToken": "xapp-ignored", "ChannelId": "{{channel}}" } }""");

    static FakeSlackWorkspace StandardWorkspace() =>
        new FakeSlackWorkspace().AddHuman("U1", "이상준", "sj").AddHuman("U2", "김철수").AddBot("U3");

    static string ConnectionString(string dbPath) =>
        new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();

    static LunchDbContext OpenDb(string path) =>
        new(new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(ConnectionString(path)).Options);

    static async Task CreateMigratedDbAsync(string path, ChannelRoster? seed = null, DateTimeOffset? at = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var db = OpenDb(path);
        await db.Database.MigrateAsync(Ct);
        if (seed is not null)
            await db.ApplyChannelRosterAsync(seed, at ?? Now, Ct);
    }

    static async Task ApplyAsync(string path, ChannelRoster roster, DateTimeOffset at)
    {
        await using var db = OpenDb(path);
        await db.ApplyChannelRosterAsync(roster, at, Ct);
    }

    static async Task<List<ChannelMember>> ReadRowsAsync(string path)
    {
        await using var db = OpenDb(path);
        var rows = await db.ChannelMembers.AsNoTracking().ToListAsync(Ct);
        return rows
            .OrderBy(m => m.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(m => m.ChannelId, StringComparer.Ordinal)
            .ThenBy(m => m.SlackUserId, StringComparer.Ordinal)
            .ToList();
    }

    // ───────────────────────── 인수 해석 / 도움말 ─────────────────────────

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task 도움말만_요청해도_사용법을_출력하고_성공한다(string flag)
    {
        var result = await RunAsync([flag]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("사용법", result.Out);
        Assert.Equal("", result.Err);
    }

    [Fact]
    public async Task 도움말은_다른_인수가_있어도_설정과_DB와_네트워크를_건드리지_않는다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("none.db");

        var result = await RunAsync(["sync", "--config", dir.Combine("missing.json"), "--db", db, "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("사용법", result.Out);
        Assert.Equal("", result.Err);
        Assert.False(File.Exists(db));
    }

    public static TheoryData<string[]> UsageErrors => new()
    {
        Array.Empty<string>(),
        new[] { "frobnicate" },
        new[] { "sync", "--bogus" },
        new[] { "sync" },
        new[] { "sync", "--config" },
        new[] { "sync", "--config", "a.json", "--config", "b.json" },
        new[] { "sync", "--config", "a.json", "--json" },
        new[] { "sync", "--config", "a.json", "--workspace", "T01" },
        new[] { "sync", "--config", "a.json", "--channel", "C01" },
        new[] { "list" },
        new[] { "list", "--db" },
        new[] { "list", "--db", "a.db", "--create-db" },
        new[] { "list", "--db", "a.db", "--workspace", "" },
    };

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task 잘못된_인수는_사용법_오류_코드_2로_끝나고_아무것도_출력하지_않는다(string[] args)
    {
        var result = await RunAsync(args);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Out);
        Assert.NotEqual("", result.Err);
    }

    [Fact]
    public async Task 알_수_없는_옵션은_옵션_이름을_알린다()
    {
        var result = await RunAsync(["sync", "--bogus"]);

        Assert.Contains("--bogus", result.Err);
    }

    [Fact]
    public async Task 알_수_없는_값이_섞여도_그_값을_에코하지_않는다()
    {
        var result = await RunAsync(["list", "--db", "a.db", "xoxb-pasted-by-mistake"]);

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("xoxb-pasted-by-mistake", result.Err);
    }

    // ───────────────────────── list (조회 전용) ─────────────────────────

    [Fact]
    public async Task list는_활성_명단만_JSON_배열로_출력하고_ASCII만_쓴다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", "sj"), new("U2", "김철수", "")]));
        await ApplyAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", "sj")]), Now.AddDays(1));   // U2 퇴장

        var result = await RunAsync(["list", "--db", db, "--json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Err);
        Assert.All(result.Out, c => Assert.True(c < 128, "JSON은 코드 페이지와 무관하도록 ASCII로 이스케이프한다."));
        using var doc = JsonDocument.Parse(result.Out);
        var item = Assert.Single(doc.RootElement.EnumerateArray().ToArray());
        Assert.Equal(
            new[] { "WorkspaceId", "ChannelId", "SlackUserId", "RealName", "DisplayName", "LastSyncedAt" },
            item.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("T01", item.GetProperty("WorkspaceId").GetString());
        Assert.Equal("C01", item.GetProperty("ChannelId").GetString());
        Assert.Equal("U1", item.GetProperty("SlackUserId").GetString());
        Assert.Equal("이상준", item.GetProperty("RealName").GetString());
        Assert.Equal("sj", item.GetProperty("DisplayName").GetString());
        Assert.Equal(Now.AddDays(1), item.GetProperty("LastSyncedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task list는_명단이_비어_있으면_빈_JSON_배열을_출력한다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db);

        var result = await RunAsync(["list", "--db", db, "--json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[]", result.Out.Trim());
    }

    [Fact]
    public async Task list는_워크스페이스와_채널_필터를_적용한다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db);
        await ApplyAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", ""), new("U2", "김철수", "")]), Now);
        await ApplyAsync(db, new ChannelRoster("T01", "C02", [new("U3", "박영희", "")]), Now);
        await ApplyAsync(db, new ChannelRoster("T02", "C01", [new("U9", "최민수", "")]), Now);

        static string[] Ids(RunResult r) =>
            [.. JsonDocument.Parse(r.Out).RootElement.EnumerateArray().Select(e => e.GetProperty("SlackUserId").GetString()!)];

        Assert.Equal(new[] { "U2", "U1" }, Ids(await RunAsync(["list", "--db", db, "--json", "--workspace", "T01", "--channel", "C01"])));
        Assert.Equal(new[] { "U9" }, Ids(await RunAsync(["list", "--db", db, "--json", "--workspace", "T02"])));
        Assert.Equal(new[] { "U3" }, Ids(await RunAsync(["list", "--db", db, "--json", "--channel", "C02"])));
        Assert.Equal("[]", (await RunAsync(["list", "--db", db, "--json", "--workspace", "T404"])).Out.Trim());
    }

    [Fact]
    public async Task list는_JSON_옵션이_없으면_사람이_읽는_표를_출력한다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", "sj")]));

        var result = await RunAsync(["list", "--db", db]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("T01\tC01\tU1\t이상준\tsj\t", result.Out);
        Assert.Contains("활성 구성원 1명", result.Out);
    }

    [Fact]
    public async Task list는_토큰과_채널_설정_없이_DB_경로만_설정에서_읽는다()
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json", """{ "Database": { "Path": "data/x.db" } }""");
        await CreateMigratedDbAsync(dir.Combine("data", "x.db"), new ChannelRoster("T01", "C01", [new("U1", "이상준", "")]));

        var result = await RunAsync(["list", "--config", config, "--json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("U1", result.Out);
    }

    [Fact]
    public async Task list는_DB를_직접_주면_설정_파일을_읽지_않는다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db);

        var result = await RunAsync(["list", "--config", dir.Combine("missing.json"), "--db", db, "--json"]);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task list는_DB_파일이_없으면_만들지_않고_실패하며_표준_출력은_비어_있다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("none.db");

        var result = await RunAsync(["list", "--db", db, "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Out);
        Assert.Contains("DB 파일이 없습니다", result.Err);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public async Task list는_명단_테이블이_없는_낡은_DB를_마이그레이션_안내와_함께_거부한다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("old.db");
        await using (var connection = new SqliteConnection(ConnectionString(db)))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Unrelated (Id INTEGER)";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var result = await RunAsync(["list", "--db", db, "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Out);
        Assert.Contains("마이그레이션", result.Err);
    }

    [Fact]
    public async Task list는_DB를_바꾸지_않는다()
    {
        using var dir = new TempDir();
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", "sj")]));
        var before = await ReadRowsAsync(db);

        await RunAsync(["list", "--db", db, "--json"]);

        var after = await ReadRowsAsync(db);
        var row = Assert.Single(after);
        Assert.Equal(Assert.Single(before).LastSyncedAt, row.LastSyncedAt);
        Assert.True(row.IsActive);
    }

    // ───────────────────────── sync ─────────────────────────

    [Fact]
    public async Task sync는_DB_파일이_없으면_Slack을_부르기_전에_실패하고_파일을_만들지_않는다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");

        var result = await RunAsync(["sync", "--config", config, "--db", db]);   // 네트워크 팩토리는 호출되면 예외

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("DB 파일이 없습니다", result.Err);
        Assert.Contains("--create-db", result.Err);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public async Task sync는_create_db를_주면_DB를_만들고_명단을_저장하며_이름과_토큰은_출력하지_않는다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("data", "lunch.db");
        var ws = StandardWorkspace();

        var result = await RunAsync(["sync", "--config", config, "--db", db, "--create-db"], ws.CreateHttpClient);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Err);
        Assert.Contains("T01", result.Out);
        Assert.Contains("C01", result.Out);
        Assert.Contains("lunch", result.Out);
        Assert.Contains("현재 구성원 2명", result.Out);
        Assert.DoesNotContain("이상준", result.Out);
        Assert.DoesNotContain("김철수", result.Out);
        Assert.DoesNotContain(Token, result.Out);
        var rows = await ReadRowsAsync(db);
        Assert.Equal(new[] { "U1", "U2" }, rows.Select(m => m.SlackUserId).ToArray());
        Assert.All(rows, m =>
        {
            Assert.Equal("T01", m.WorkspaceId);
            Assert.Equal("C01", m.ChannelId);
            Assert.True(m.IsActive);
            Assert.Equal(Now, m.LastSyncedAt);
        });
        Assert.Equal("이상준", rows[0].RealName);
        Assert.Equal("sj", rows[0].DisplayName);
    }

    [Fact]
    public async Task sync를_같은_명단으로_다시_실행하면_행이_늘지_않고_변동_없음으로_집계한다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");
        var ws = StandardWorkspace();
        await RunAsync(["sync", "--config", config, "--db", db, "--create-db"], ws.CreateHttpClient);

        var second = await RunAsync(["sync", "--config", config, "--db", db], ws.CreateHttpClient, Now.AddDays(1));

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("변동 없음 2", second.Out);
        Assert.Contains("퇴장 처리 0명", second.Out);
        var rows = await ReadRowsAsync(db);
        Assert.Equal(new[] { "U1", "U2" }, rows.Select(m => m.SlackUserId).ToArray());
        Assert.All(rows, m => Assert.Equal(Now.AddDays(1), m.LastSyncedAt));
    }

    [Fact]
    public async Task sync는_채널을_나간_사람을_삭제하지_않고_비활성으로_반영한다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");
        await RunAsync(["sync", "--config", config, "--db", db, "--create-db"], StandardWorkspace().CreateHttpClient);
        var afterDeparture = new FakeSlackWorkspace().AddHuman("U1", "이상준", "sj");

        var result = await RunAsync(["sync", "--config", config, "--db", db], afterDeparture.CreateHttpClient, Now.AddDays(1));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("퇴장 처리 1명", result.Out);
        var rows = await ReadRowsAsync(db);
        Assert.Equal(new[] { "U1", "U2" }, rows.Select(m => m.SlackUserId).ToArray());
        Assert.True(rows[0].IsActive);
        Assert.False(rows[1].IsActive);
        Assert.Equal("김철수", rows[1].RealName);
    }

    [Fact]
    public async Task sync는_같은_DB의_다른_채널과_워크스페이스_행을_건드리지_않는다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db);
        await ApplyAsync(db, new ChannelRoster("T01", "C02", [new("U1", "이상준", ""), new("U8", "다른채널", "")]), Now);
        await ApplyAsync(db, new ChannelRoster("T99", "C01", [new("U1", "다른회사", "")]), Now);

        var result = await RunAsync(["sync", "--config", config, "--db", db], StandardWorkspace().CreateHttpClient, Now.AddDays(1));

        Assert.Equal(0, result.ExitCode);
        var rows = await ReadRowsAsync(db);
        var untouched = rows.Where(m => (m.WorkspaceId, m.ChannelId) != ("T01", "C01")).ToArray();
        Assert.Equal(3, untouched.Length);
        Assert.All(untouched, m =>
        {
            Assert.True(m.IsActive);
            Assert.Equal(Now, m.LastSyncedAt);
        });
    }

    [Fact]
    public async Task sync가_Slack_오류로_실패하면_기존_명단은_그대로이고_비활성_처리도_없다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");
        // U9는 이번 스냅샷에 없으니 성공했다면 퇴장 처리됐을 사람이다.
        await CreateMigratedDbAsync(db,
            new ChannelRoster("T01", "C01", [new("U1", "이상준", "sj"), new("U2", "김철수", ""), new("U9", "예전사람", "")]));
        var ws = StandardWorkspace();
        ws.Override = c => c.Method == "users.info" && c.Query["user"] == "U2" ? SlackReply.Error("ratelimited") : null;

        var result = await RunAsync(["sync", "--config", config, "--db", db], ws.CreateHttpClient, Now.AddDays(1));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ratelimited", result.Err);
        Assert.DoesNotContain(Token, result.Err);
        Assert.Equal("", result.Out);
        var rows = await ReadRowsAsync(db);
        Assert.Equal(new[] { "U1", "U2", "U9" }, rows.Select(m => m.SlackUserId).ToArray());
        Assert.All(rows, m =>
        {
            Assert.True(m.IsActive);
            Assert.Equal(Now, m.LastSyncedAt);
        });
        Assert.Equal("이상준", rows[0].RealName);
    }

    [Fact]
    public async Task sync가_인증에_실패해도_토큰은_출력하지_않고_DB는_그대로다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", "")]));
        var ws = StandardWorkspace();
        ws.Override = c => c.Method == "auth.test" ? SlackReply.Error("invalid_auth") : null;

        var result = await RunAsync(["sync", "--config", config, "--db", db], ws.CreateHttpClient, Now.AddDays(1));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("invalid_auth", result.Err);
        Assert.DoesNotContain(Token, result.Err);
        Assert.DoesNotContain(Token, result.Out);
        var only = Assert.Single(await ReadRowsAsync(db));
        Assert.Equal(Now, only.LastSyncedAt);
    }

    [Fact]
    public async Task sync는_취소되면_종료_코드_130으로_끝나고_DB를_바꾸지_않는다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir);
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db, new ChannelRoster("T01", "C01", [new("U1", "이상준", "")]));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await RunAsync(["sync", "--config", config, "--db", db], StandardWorkspace().CreateHttpClient,
            Now.AddDays(1), cts.Token);

        Assert.Equal(130, result.ExitCode);
        var only = Assert.Single(await ReadRowsAsync(db));
        Assert.True(only.IsActive);
        Assert.Equal(Now, only.LastSyncedAt);
    }

    [Fact]
    public async Task sync는_설정이_잘못되면_Slack을_부르기_전에_값을_노출하지_않고_실패한다()
    {
        using var dir = new TempDir();
        var config = WriteConfig(dir, token: "not-a-bot-token-123");
        var db = dir.Combine("lunch.db");
        await CreateMigratedDbAsync(db);

        var result = await RunAsync(["sync", "--config", config, "--db", db]);   // 네트워크 팩토리는 호출되면 예외

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("BotToken", result.Err);
        Assert.DoesNotContain("not-a-bot-token-123", result.Err);
    }

    [Fact]
    public async Task sync는_설정_파일이_없으면_경로를_알리고_실패한다()
    {
        using var dir = new TempDir();
        var missing = dir.Combine("missing.json");

        var result = await RunAsync(["sync", "--config", missing, "--db", dir.Combine("lunch.db")]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(missing, result.Err);
    }
}
