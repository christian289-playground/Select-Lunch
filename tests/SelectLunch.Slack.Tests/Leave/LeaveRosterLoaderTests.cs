using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Leave;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Leave;
using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 휴가자 조회 한 번의 통합: Slack 명단 수집 → 기존 DB 반영 → DB의 현재 채널 활성 명단으로 이름 대조.
/// 실제 SQLite 위에서 돌리고 Slack SDK와 메일 전송만 가짜다. 확인하는 것은 "범위"와 "부분 반영 금지"다 —
/// 다른 채널·워크스페이스 사람이나 이미 나간 사람이 휴가자로 나오지 않고, 수집이 도중에 실패해도 DB가 바뀌지 않는다.
/// </summary>
public class LeaveRosterLoaderTests
{
    const string Workspace = "T1";
    const string Channel = "C1";

    static readonly DateOnly Day = new(2031, 3, 12);
    static readonly DateTimeOffset Now = new(2031, 3, 12, 2, 0, 0, TimeSpan.Zero);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class FakeMailReader : ILeaveMailReader
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public IReadOnlyList<LeaveMailCandidate> Mails { get; set; } = [];

        public Task<IReadOnlyList<LeaveMailCandidate>> ReadAsync(IReadOnlyList<string> keywords, CancellationToken ct)
        {
            Requests.Add(keywords);
            return Task.FromResult(Mails);
        }
    }

    /// <summary>메일 Text가 곧 조회 키인 파서. 회사 형식 규칙은 파서 쪽 테스트의 몫이다.</summary>
    sealed class TableParser(Dictionary<string, LeaveMailParseResult> table) : ILeaveMailParser
    {
        public LeaveMailParseResult Parse(LeaveMailCandidate mail) => table[mail.Text];
    }

    static LeaveMailParseResult Leave(string name, DateOnly start, DateOnly end) =>
        new([new LeavePeriod(name, start, end)], []);

    sealed class Harness(TestDb db, LeaveSlackWorkspace slack, FakeMailReader reader, LeaveRosterLoader loader)
        : IAsyncDisposable
    {
        public TestDb Db { get; } = db;

        public LeaveSlackWorkspace Slack { get; } = slack;

        public FakeMailReader Reader { get; } = reader;

        public LeaveRosterLoader Loader { get; } = loader;

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public async Task<Dictionary<string, bool>> ActiveFlagsAsync(string workspaceId, string channelId) =>
            await Db.Db.ChannelMembers.AsNoTracking()
                .Where(m => m.WorkspaceId == workspaceId && m.ChannelId == channelId)
                .ToDictionaryAsync(m => m.SlackUserId, m => m.IsActive, Ct);
    }

    /// <param name="mails">메일 Text → 파서 결과. 키 순서대로 후보 메일이 된다.</param>
    static async Task<Harness> CreateAsync(Dictionary<string, LeaveMailParseResult>? mails = null)
    {
        var db = await TestDb.CreateAsync();
        var slack = new LeaveSlackWorkspace { TeamId = Workspace };
        mails ??= new Dictionary<string, LeaveMailParseResult>();
        var reader = new FakeMailReader
        {
            Mails = [.. mails.Keys.Select(text => new LeaveMailCandidate(text, "body"))],
        };
        var provider = new LeaveMailProvider(new TableParser(mails), ["휴가", "연차"]);
        var loader = new LeaveRosterLoader(slack.Client, db.Db, Channel, provider, reader, new TestTimeProvider(Now));
        return new Harness(db, slack, reader, loader);
    }

    static async Task SeedAsync(
        Harness h, string workspaceId, string channelId, string userId, string realName, bool active = true)
    {
        h.Db.Db.ChannelMembers.Add(new ChannelMember
        {
            WorkspaceId = workspaceId,
            ChannelId = channelId,
            SlackUserId = userId,
            RealName = realName,
            DisplayName = "",
            IsActive = active,
            LastSyncedAt = Now.AddDays(-30),
        });
        await h.Db.Db.SaveChangesAsync(Ct);
        h.Db.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Shows_only_current_channel_people_whose_leave_covers_the_menu_day()
    {
        await using var h = await CreateAsync(new Dictionary<string, LeaveMailParseResult>
        {
            ["today"] = Leave("Alpha Tester", Day, Day),
            ["tomorrow"] = Leave("Beta Tester", Day.AddDays(1), Day.AddDays(1)),
            ["stranger"] = Leave("Someone Elsewhere", Day, Day),
        });
        h.Slack.AddHumans(
            LeaveSlackWorkspace.Human("U1", "Alpha Tester", "alpha"),
            LeaveSlackWorkspace.Human("U2", "Beta Tester"),
            LeaveSlackWorkspace.Human("U3", "Gamma Tester"));
        h.Slack.Pages.Add(["USLACKBOT", "UBOT"]);
        h.Slack.Users["UBOT"] = new User { Id = "UBOT", Name = "robot", IsBot = true };

        var snapshot = await h.Loader.LoadAsync(Day, Ct);

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Equal(["alpha"], snapshot.DisplayNames);
        Assert.Equal(["휴가", "연차"], Assert.Single(h.Reader.Requests));

        // 수집한 명단이 기존 테이블에 그대로 반영됐다(봇·슬랙봇은 들어가지 않는다).
        var active = await h.ActiveFlagsAsync(Workspace, Channel);
        Assert.Equal(["U1", "U2", "U3"], active.Keys.Order(StringComparer.Ordinal));
        Assert.All(active.Values, isActive => Assert.True(isActive));
    }

    [Fact]
    public async Task People_who_left_the_channel_are_deactivated_and_never_shown()
    {
        await using var h = await CreateAsync(new Dictionary<string, LeaveMailParseResult>
        {
            ["old"] = Leave("Old Member", Day, Day),
        });
        h.Slack.AddHumans(LeaveSlackWorkspace.Human("U1", "Alpha Tester"));
        await SeedAsync(h, Workspace, Channel, "U9", "Old Member");

        var snapshot = await h.Loader.LoadAsync(Day, Ct);

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Empty(snapshot.DisplayNames);

        var active = await h.ActiveFlagsAsync(Workspace, Channel);
        Assert.False(active["U9"]);
        Assert.True(active["U1"]);
    }

    [Fact]
    public async Task Same_names_in_another_channel_or_workspace_never_match()
    {
        await using var h = await CreateAsync(new Dictionary<string, LeaveMailParseResult>
        {
            ["alpha"] = Leave("Alpha Tester", Day, Day),
        });
        h.Slack.AddHumans(LeaveSlackWorkspace.Human("U1", "Beta Tester"));
        await SeedAsync(h, Workspace, "C2", "U7", "Alpha Tester");
        await SeedAsync(h, "T2", Channel, "U8", "Alpha Tester");

        var snapshot = await h.Loader.LoadAsync(Day, Ct);

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Empty(snapshot.DisplayNames);

        // 다른 범위의 행은 읽지도 건드리지도 않는다.
        Assert.True((await h.ActiveFlagsAsync(Workspace, "C2"))["U7"]);
        Assert.True((await h.ActiveFlagsAsync("T2", Channel))["U8"]);
    }

    [Fact]
    public async Task Failed_collection_leaves_the_database_untouched_and_reads_no_mail()
    {
        await using var h = await CreateAsync(new Dictionary<string, LeaveMailParseResult>
        {
            ["alpha"] = Leave("Alpha Tester", Day, Day),
        });
        h.Slack.AddHumans(
            LeaveSlackWorkspace.Human("U1", "Alpha Tester"),
            LeaveSlackWorkspace.Human("U2", "Beta Tester"));
        h.Slack.InfoFailures["U2"] = new SlackException(new ErrorResponse { Error = "ratelimited" });
        await SeedAsync(h, Workspace, Channel, "U9", "Old Member");

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => h.Loader.LoadAsync(Day, Ct));

        Assert.Equal(LeaveLookupFailure.SlackDirectory, ex.Reason);
        Assert.Empty(h.Reader.Requests);

        // 부분 명단(U1만)이 반영됐다면 U9가 퇴장 처리됐을 것이다.
        var rows = await h.ActiveFlagsAsync(Workspace, Channel);
        Assert.Equal(["U9"], rows.Keys.ToArray());
    }

    [Fact]
    public async Task Channel_without_any_person_skips_the_mail_search()
    {
        await using var h = await CreateAsync();
        h.Slack.Pages.Add(["USLACKBOT", "UBOT"]);
        h.Slack.Users["UBOT"] = new User { Id = "UBOT", Name = "robot", IsBot = true };

        var snapshot = await h.Loader.LoadAsync(Day, Ct);

        Assert.Equal(LeaveRosterStatus.Available, snapshot.Status);
        Assert.Empty(snapshot.DisplayNames);
        Assert.Empty(h.Reader.Requests);
    }

    [Fact]
    public async Task Unresolvable_name_of_a_current_channel_person_fails_instead_of_showing_nobody()
    {
        await using var h = await CreateAsync(new Dictionary<string, LeaveMailParseResult>
        {
            ["vague"] = new LeaveMailParseResult([], ["Alpha Tester"]),
        });
        h.Slack.AddHumans(LeaveSlackWorkspace.Human("U1", "Alpha Tester"));

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => h.Loader.LoadAsync(Day, Ct));

        Assert.Equal(LeaveLookupFailure.DatesUnresolved, ex.Reason);
    }
}
