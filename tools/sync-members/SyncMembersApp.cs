using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Members;

namespace SyncMembers;

/// <summary>
/// `sync`(Slack 수집 → DB 반영)와 `list`(DB 조회)를 실행한다. 출력 스트림·HttpClient·시계를 주입받아
/// 테스트가 실제 SQLite 파일과 가짜 Slack으로 끝까지 돌릴 수 있다.
///
/// 순서가 안전성의 핵심이다: 설정 검증 → DB 파일 확인 → <b>Slack 수집 전체</b> → 그다음에야 DB를 연다.
/// 수집이 실패하면 DB는 열리지도(생성되지도, 마이그레이션되지도) 않는다.
/// </summary>
public sealed class SyncMembersApp(
    TextWriter stdout,
    TextWriter stderr,
    Func<HttpClient> createHttpClient,
    TimeProvider time)
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>`list --json`의 한 항목. 필드 이름과 순서는 소비하는 스크립트와의 계약이다.</summary>
    sealed record MemberJson(
        string WorkspaceId,
        string ChannelId,
        string SlackUserId,
        string RealName,
        string DisplayName,
        DateTimeOffset LastSyncedAt);

    /// <returns>0 성공, 1 실패, 2 인수 오류, 130 취소.</returns>
    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                stdout.Write(UsageText.Text);
                return 0;
            }

            return options.Command == CliCommand.List
                ? await ListAsync(options, ct)
                : await SyncAsync(options, ct);
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"사용법 오류: {ex.Message}");
            stderr.WriteLine("도움말: -Help (SyncMembers --help)");
            return 2;
        }
        catch (SyncException ex)
        {
            stderr.WriteLine($"오류: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stderr.WriteLine("취소되었습니다. 명단은 부분적으로 반영되지 않습니다.");
            return 130;
        }
        catch (Exception ex)
        {
            // 예외 메시지에는 경로·SQL 같은 세부가 섞일 수 있어 형식 이름만 남긴다.
            stderr.WriteLine($"예기치 않은 오류가 발생했습니다 ({ex.GetType().Name}). 명단은 부분적으로 반영되지 않습니다.");
            return 1;
        }
    }

    async Task<int> SyncAsync(CliOptions options, CancellationToken ct)
    {
        var configPath = options.Config!;
        var slack = SyncSettings.LoadSlack(configPath);
        var dbPath = options.Database is not null
            ? Path.GetFullPath(options.Database)
            : SyncSettings.ResolveDatabasePath(configPath);

        // 오타 난 경로에 빈 DB가 조용히 생기지 않게, 새로 만드는 것은 명시했을 때만 허용한다.
        var exists = File.Exists(dbPath);
        if (!exists && !options.CreateDatabase)
        {
            throw new SyncException(
                $"DB 파일이 없습니다: {dbPath}. 경로를 확인하거나, 새 DB를 만들려면 --create-db(스크립트는 -CreateDatabase)를 지정하세요.");
        }

        CollectedRoster collected;
        using (var http = createHttpClient())
        {
            collected = await new SlackRosterClient(http, slack.BotToken).CollectAsync(slack.ChannelId, ct);
        }

        if (!exists)
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var db = OpenDb(dbPath, exists ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate);
        await db.Database.MigrateAsync(ct);
        var result = await db.ApplyChannelRosterAsync(collected.Roster, time.GetUtcNow(), ct);

        var workspace = Describe(collected.Roster.WorkspaceId, collected.WorkspaceName, "");
        var channel = Describe(collected.Roster.ChannelId, collected.ChannelName, "#");
        stdout.WriteLine($"워크스페이스: {workspace}");
        stdout.WriteLine($"채널: {channel}");
        stdout.WriteLine($"DB: {dbPath}");
        stdout.WriteLine(
            $"동기화 완료: 현재 구성원 {result.Active}명 (신규 {result.Added}, 변경 {result.Updated}, 변동 없음 {result.Unchanged}), " +
            $"퇴장 처리 {result.Deactivated}명. 봇·앱·삭제 계정 {collected.ExcludedCount}개는 제외했습니다.");

        return 0;
    }

    async Task<int> ListAsync(CliOptions options, CancellationToken ct)
    {
        // 조회 전용: Slack 요청도, 토큰 검증도, DB 생성·마이그레이션도 하지 않는다.
        var dbPath = options.Database is not null
            ? Path.GetFullPath(options.Database)
            : SyncSettings.ResolveDatabasePath(options.Config!);

        if (!File.Exists(dbPath))
            throw new SyncException($"DB 파일이 없습니다: {dbPath}");

        List<ChannelMember> members;
        await using (var db = OpenDb(dbPath, SqliteOpenMode.ReadWrite))   // ReadWrite지만 쓰지 않는다. 파일이 없으면 만들지 않는 것이 목적이다.
        {
            try
            {
                members = await db.GetActiveChannelMembersAsync(options.WorkspaceId, options.ChannelId, ct);
            }
            catch (SqliteException ex) when (IsMissingTable(ex))
            {
                throw new SyncException(
                    "명단 테이블(ChannelMembers)이 없습니다. DB가 최신 스키마가 아닙니다 — sync를 한 번 실행하거나 앱을 기동해 마이그레이션하세요.");
            }
        }

        if (options.Json)
        {
            var items = members
                .Select(m => new MemberJson(m.WorkspaceId, m.ChannelId, m.SlackUserId, m.RealName, m.DisplayName, m.LastSyncedAt))
                .ToList();
            // 기본 인코더가 비ASCII를 \uXXXX로 이스케이프한다 — 콘솔 코드 페이지와 무관하게 안전하다.
            stdout.WriteLine(JsonSerializer.Serialize(items, JsonOptions));
            return 0;
        }

        stdout.WriteLine($"# 활성 구성원 {members.Count}명");
        if (members.Count > 0)
        {
            stdout.WriteLine("WorkspaceId\tChannelId\tSlackUserId\tRealName\tDisplayName\tLastSyncedAt");
            foreach (var m in members)
            {
                stdout.WriteLine(string.Join(
                    '\t',
                    m.WorkspaceId,
                    m.ChannelId,
                    m.SlackUserId,
                    Plain(m.RealName),
                    Plain(m.DisplayName),
                    m.LastSyncedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)));
            }
        }

        return 0;
    }

    static LunchDbContext OpenDb(string path, SqliteOpenMode mode)
    {
        // Pooling을 끄면 연결을 닫는 즉시 파일 핸들이 풀려, 짧게 도는 도구가 DB 파일을 붙잡고 있지 않는다.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();

        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connectionString).Options;
        return new LunchDbContext(options);
    }

    static bool IsMissingTable(SqliteException ex) =>
        ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    static string Describe(string id, string name, string namePrefix) =>
        name.Length == 0 ? id : $"{id} ({namePrefix}{Plain(name)})";

    /// <summary>탭·줄바꿈 같은 제어 문자를 공백으로 바꿔 한 줄 출력이 깨지지 않게 한다.</summary>
    static string Plain(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));
}
