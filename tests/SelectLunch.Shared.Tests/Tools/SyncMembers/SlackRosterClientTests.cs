using System.Net;
using SyncMembers;

namespace SelectLunch.Shared.Tests;

public class SlackRosterClientTests
{
    const string Token = "xoxb-test-secret";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<CollectedRoster> CollectAsync(FakeSlackWorkspace workspace, string channelId = "C01")
    {
        using var http = workspace.CreateHttpClient();
        return await new SlackRosterClient(http, Token).CollectAsync(channelId, Ct);
    }

    static async Task<SyncException> FailureAsync(FakeSlackWorkspace workspace, string channelId = "C01")
    {
        using var http = workspace.CreateHttpClient();
        return await Assert.ThrowsAsync<SyncException>(
            () => new SlackRosterClient(http, Token).CollectAsync(channelId, Ct));
    }

    [Fact]
    public async Task 정상_응답이면_사람만_골라_워크스페이스와_채널이_붙은_명단을_만든다()
    {
        var ws = new FakeSlackWorkspace()
            .AddHuman("U1", "이상준", "sj")
            .AddBot("U2")
            .AddMemberOnly("USLACKBOT")
            .AddDeleted("U3")
            .AddAppUser("U4")
            .AddHuman("U5", "김철수");

        var collected = await CollectAsync(ws);

        Assert.Equal("T01", collected.Roster.WorkspaceId);
        Assert.Equal("C01", collected.Roster.ChannelId);
        Assert.Equal(new[] { "U1", "U5" }, collected.Roster.People.Select(p => p.SlackUserId).ToArray());
        Assert.Equal("이상준", collected.Roster.People[0].RealName);
        Assert.Equal("sj", collected.Roster.People[0].DisplayName);
        Assert.Equal("", collected.Roster.People[1].DisplayName);
        Assert.Equal("lunch", collected.ChannelName);
        Assert.Equal("테스트 회사", collected.WorkspaceName);
        Assert.Equal(4, collected.ExcludedCount);   // 봇, Slackbot, 삭제 계정, 앱 사용자
    }

    [Fact]
    public async Task Slackbot은_프로필_조회_없이_제외한다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준").AddMemberOnly("USLACKBOT");

        await CollectAsync(ws);

        Assert.DoesNotContain(ws.Calls, c => c.Method == "users.info" && c.Query["user"] == "USLACKBOT");
    }

    [Fact]
    public async Task 구성원이_여러_페이지면_커서를_따라_모두_모은다()
    {
        var ws = new FakeSlackWorkspace { PageSize = 2 };
        foreach (var n in Enumerable.Range(1, 5))
            ws.AddHuman($"U{n}", $"사람{n}");

        var collected = await CollectAsync(ws);

        Assert.Equal(
            new[] { "U1", "U2", "U3", "U4", "U5" },
            collected.Roster.People.Select(p => p.SlackUserId).ToArray());
        var memberCalls = ws.Calls.Where(c => c.Method == "conversations.members").ToArray();
        Assert.Equal(new[] { "", "c2", "c4" }, memberCalls.Select(c => c.Query.GetValueOrDefault("cursor", "")).ToArray());
        Assert.All(memberCalls, c => Assert.Equal("200", c.Query["limit"]));
    }

    [Fact]
    public async Task 토큰은_Authorization_헤더로만_보내고_URL에는_넣지_않는다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");

        await CollectAsync(ws);

        Assert.All(ws.Calls, c =>
        {
            Assert.Equal("Bearer " + Token, c.Authorization);
            Assert.DoesNotContain("xoxb", c.Url);
        });
    }

    [Fact]
    public async Task 호출은_문서화된_Slack_메서드와_HTTP_메서드를_쓴다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");

        await CollectAsync(ws);

        var seen = ws.Calls.Select(c => $"{c.Http.Method} {c.Method}").Distinct().ToArray();
        Assert.Equal(
            new[] { "POST auth.test", "GET conversations.info", "GET conversations.members", "GET users.info" },
            seen);
    }

    [Fact]
    public async Task 프로필_실명이_비어_있으면_사용자_실명_그다음_사용자명으로_대신하고_이름_앞뒤_공백을_뗀다()
    {
        var ws = new FakeSlackWorkspace()
            .AddUser("U1", new
            {
                id = "U1", name = "u1", deleted = false, is_bot = false, is_app_user = false,
                real_name = "홍길동", profile = new { real_name = "", display_name = "  gd  " },
            })
            .AddUser("U2", new
            {
                id = "U2", name = "gildong", deleted = false, is_bot = false, is_app_user = false,
                profile = new { real_name = "  ", display_name = "" },
            });

        var collected = await CollectAsync(ws);

        Assert.Equal("홍길동", collected.Roster.People[0].RealName);
        Assert.Equal("gd", collected.Roster.People[0].DisplayName);
        Assert.Equal("gildong", collected.Roster.People[1].RealName);
        Assert.Equal("", collected.Roster.People[1].DisplayName);
    }

    [Fact]
    public async Task 사람이_한_명도_없고_봇만_있는_채널은_빈_명단이다()
    {
        var ws = new FakeSlackWorkspace().AddBot("UBOT");

        var collected = await CollectAsync(ws);

        Assert.Empty(collected.Roster.People);
        Assert.Equal(1, collected.ExcludedCount);
    }

    [Fact]
    public async Task 인증에_실패하면_오류_코드만_알리고_토큰은_노출하지_않으며_더_호출하지_않는다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");
        ws.Override = c => c.Method == "auth.test" ? SlackReply.Error("invalid_auth") : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("auth.test", ex.Message);
        Assert.Contains("invalid_auth", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Equal(new[] { "auth.test" }, ws.Calls.Select(c => c.Method).ToArray());
    }

    [Fact]
    public async Task 알_수_없는_모양의_오류_코드는_그대로_출력하지_않는다()
    {
        var ws = new FakeSlackWorkspace();
        ws.Override = c => c.Method == "auth.test" ? SlackReply.Error("invalid auth <script>xoxb-leak</script>") : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("알 수 없는", ex.Message);
        Assert.DoesNotContain("<script>", ex.Message);
        Assert.DoesNotContain("xoxb-leak", ex.Message);
    }

    [Fact]
    public async Task 요청한_채널과_다른_채널이_돌아오면_중단한다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");
        ws.Override = c => c.Method == "conversations.info"
            ? SlackReply.Json(new { ok = true, channel = new { id = "C99", name = "elsewhere" } })
            : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("채널", ex.Message);
        Assert.DoesNotContain(ws.Calls, c => c.Method == "conversations.members");
    }

    [Fact]
    public async Task 채널_구성원_조회가_권한_오류면_명단을_만들지_않는다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");
        ws.Override = c => c.Method == "conversations.members" ? SlackReply.Error("missing_scope") : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("missing_scope", ex.Message);
        Assert.DoesNotContain(ws.Calls, c => c.Method == "users.info");
    }

    [Fact]
    public async Task 프로필_조회가_한_명이라도_실패하면_명단_전체를_포기한다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준").AddMemberOnly("U2").AddHuman("U3", "김철수");

        var ex = await FailureAsync(ws);

        Assert.Contains("users.info", ex.Message);
        Assert.Contains("user_not_found", ex.Message);
        Assert.Contains("U2", ex.Message);
    }

    [Fact]
    public async Task 구성원_목록이_비어_있으면_응답을_믿지_않고_중단한다()
    {
        var ws = new FakeSlackWorkspace();

        var ex = await FailureAsync(ws);

        Assert.Contains("비어", ex.Message);
        Assert.DoesNotContain(ws.Calls, c => c.Method == "users.info");
    }

    [Fact]
    public async Task 같은_커서가_다시_돌아오면_무한_반복하지_않고_중단한다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");
        ws.Override = c => c.Method == "conversations.members"
            ? SlackReply.Json(new { ok = true, members = new[] { "U1" }, response_metadata = new { next_cursor = "same" } })
            : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("커서", ex.Message);
        Assert.Equal(2, ws.Calls.Count(c => c.Method == "conversations.members"));
    }

    [Fact]
    public async Task HTTP_오류는_본문을_노출하지_않고_상태_코드만_알린다()
    {
        var ws = new FakeSlackWorkspace();
        ws.Override = c => c.Method == "auth.test"
            ? new SlackReply(HttpStatusCode.InternalServerError, "SECRET-BODY xoxb-leak")
            : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("HTTP 500", ex.Message);
        Assert.DoesNotContain("SECRET-BODY", ex.Message);
        Assert.DoesNotContain("xoxb-leak", ex.Message);
    }

    [Fact]
    public async Task JSON이_아닌_응답은_원문을_노출하지_않는다()
    {
        var ws = new FakeSlackWorkspace();
        ws.Override = c => c.Method == "auth.test"
            ? new SlackReply(HttpStatusCode.OK, "<html>xoxb-leak</html>")
            : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("해석", ex.Message);
        Assert.DoesNotContain("xoxb-leak", ex.Message);
    }

    [Fact]
    public async Task 요청_한도를_넘으면_재시도하지_않고_대기_시간을_알린다()
    {
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준");
        ws.Override = c => c.Method == "conversations.members"
            ? new SlackReply(HttpStatusCode.TooManyRequests, "", RetryAfterSeconds: 30)
            : null;

        var ex = await FailureAsync(ws);

        Assert.Contains("30", ex.Message);
        var memberCalls = ws.Calls.Count(c => c.Method == "conversations.members");
        Assert.Equal(1, memberCalls);
    }

    [Fact]
    public async Task 네트워크_예외는_내부_메시지_없이_일반_오류로_바꾼다()
    {
        var ws = new FakeSlackWorkspace();
        ws.Override = _ => throw new HttpRequestException("secret-detail xoxb-leak");

        var ex = await FailureAsync(ws);

        Assert.Contains("auth.test", ex.Message);
        Assert.DoesNotContain("secret-detail", ex.Message);
        Assert.DoesNotContain("xoxb-leak", ex.Message);
    }

    [Fact]
    public async Task 취소하면_남은_프로필_조회를_이어가지_않는다()
    {
        using var cts = new CancellationTokenSource();
        var ws = new FakeSlackWorkspace().AddHuman("U1", "이상준").AddHuman("U2", "김철수").AddHuman("U3", "박영희");
        ws.Override = c =>
        {
            if (c.Method == "users.info")
                cts.Cancel();
            return null;
        };
        using var http = ws.CreateHttpClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SlackRosterClient(http, Token).CollectAsync("C01", cts.Token));

        var profileCalls = ws.Calls.Count(c => c.Method == "users.info");
        Assert.Equal(1, profileCalls);
    }
}
