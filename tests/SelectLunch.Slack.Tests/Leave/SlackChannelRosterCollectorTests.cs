using SelectLunch.Shared.Members;
using SelectLunch.Slack.Leave;
using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// SDK로 채널 명단을 모으는 경계. 핵심은 하나다 — <b>완전히 모았을 때만</b> 명단을 돌려준다.
/// 일부만 담은 명단은 DB 반영 때 빠진 사람을 퇴장으로 만들기 때문에, 어떤 실패도 부분 결과 대신 예외다.
/// </summary>
public class SlackChannelRosterCollectorTests
{
    const string Channel = "C1";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<ChannelRoster> CollectAsync(LeaveSlackWorkspace workspace) =>
        await new SlackChannelRosterCollector(workspace.Client, Channel).CollectAsync(Ct);

    [Fact]
    public async Task Collects_every_page_once_and_takes_workspace_from_auth_test()
    {
        var workspace = new LeaveSlackWorkspace { TeamId = "T9" };
        workspace.Pages.Add(["U1", "U2"]);
        workspace.Pages.Add(["U2", "U3"]);   // 페이지 경계에서 겹쳐도 한 번만
        workspace.Users["U1"] = LeaveSlackWorkspace.Human("U1", "Alpha Tester", "alpha");
        workspace.Users["U2"] = LeaveSlackWorkspace.Human("U2", "Beta Tester");
        workspace.Users["U3"] = LeaveSlackWorkspace.Human("U3", "Gamma Tester", "gamma");

        var roster = await CollectAsync(workspace);

        Assert.Equal("T9", roster.WorkspaceId);
        Assert.Equal(Channel, roster.ChannelId);
        Assert.Equal(
            [new RosterPerson("U1", "Alpha Tester", "alpha"), new RosterPerson("U2", "Beta Tester", ""),
             new RosterPerson("U3", "Gamma Tester", "gamma")],
            roster.People);
        Assert.Equal(["U1", "U2", "U3"], workspace.InfoRequests);
        Assert.Equal([(Channel, (string?)null), (Channel, "page1")], workspace.MemberRequests);
    }

    [Fact]
    public async Task Leaves_out_slackbot_bots_apps_and_deleted_accounts()
    {
        var workspace = new LeaveSlackWorkspace();
        workspace.Pages.Add(["USLACKBOT", "U1", "UBOT", "UAPP", "UDEL"]);
        workspace.Users["U1"] = LeaveSlackWorkspace.Human("U1", "Alpha Tester");
        workspace.Users["UBOT"] = new User { Id = "UBOT", Name = "robot", IsBot = true };
        workspace.Users["UAPP"] = new User { Id = "UAPP", Name = "app", IsAppUser = true };
        workspace.Users["UDEL"] = new User { Id = "UDEL", Name = "gone", Deleted = true };

        var roster = await CollectAsync(workspace);

        Assert.Equal(["U1"], roster.People.Select(p => p.SlackUserId));
        // 슬랙봇은 users.info로 확인할 필요도 없다.
        Assert.DoesNotContain("USLACKBOT", workspace.InfoRequests);
    }

    [Fact]
    public async Task Real_name_falls_back_to_user_real_name_then_login_name()
    {
        var workspace = new LeaveSlackWorkspace();
        workspace.Pages.Add(["U1", "U2", "U3"]);
        workspace.Users["U1"] = new User
        {
            Id = "U1", Name = "login1", RealName = "User Real", Profile = new UserProfile { RealName = " ", DisplayName = " dn " },
        };
        workspace.Users["U2"] = new User { Id = "U2", Name = "login2", RealName = "", Profile = new UserProfile() };
        workspace.Users["U3"] = new User { Id = "U3", Name = "login3", RealName = "  Spaced Name  " };

        var roster = await CollectAsync(workspace);

        Assert.Equal(
            [new RosterPerson("U1", "User Real", "dn"), new RosterPerson("U2", "login2", ""),
             new RosterPerson("U3", "Spaced Name", "")],
            roster.People);
    }

    [Theory]
    [InlineData("auth-error")]
    [InlineData("auth-without-team")]
    [InlineData("first-page-error")]
    [InlineData("second-page-error")]
    [InlineData("repeated-cursor")]
    [InlineData("no-members")]
    [InlineData("blank-member-id")]
    [InlineData("info-error")]
    [InlineData("info-user-not-visible")]
    [InlineData("info-mismatched-user")]
    public async Task Any_failure_is_an_exception_never_a_partial_roster(string failure)
    {
        var workspace = new LeaveSlackWorkspace();
        workspace.AddHumans(
            LeaveSlackWorkspace.Human("U1", "Alpha Tester"),
            LeaveSlackWorkspace.Human("U2", "Beta Tester"));
        workspace.Pages.Add(["U3"]);
        workspace.Users["U3"] = LeaveSlackWorkspace.Human("U3", "Gamma Tester");

        switch (failure)
        {
            case "auth-error":
                workspace.AuthFailure = new SlackException(new ErrorResponse { Error = "invalid_auth" });
                break;
            case "auth-without-team":
                workspace.TeamId = " ";
                break;
            case "first-page-error":
                workspace.MembersFailures[0] = new HttpRequestException("network");
                break;
            case "second-page-error":
                workspace.MembersFailures[1] = new SlackException(new ErrorResponse { Error = "ratelimited" });
                break;
            case "repeated-cursor":
                workspace.RepeatCursorForever = true;
                break;
            case "no-members":
                workspace.Pages.Clear();
                workspace.Pages.Add([]);
                break;
            case "blank-member-id":
                workspace.Pages[0] = ["U1", " "];
                break;
            case "info-error":
                workspace.InfoFailures["U2"] = new SlackException(new ErrorResponse { Error = "missing_scope" });
                break;
            case "info-user-not-visible":
                // 한 명만 못 봐도 그 사람이 빠진 명단을 만들지 않는다 — VoteReminderPoster와 달리 여기서는 부분 명단이 퇴장 처리를 부른다.
                workspace.InfoFailures["U3"] = new SlackException(new ErrorResponse { Error = "user_not_visible" });
                break;
            case "info-mismatched-user":
                workspace.Users["U2"] = LeaveSlackWorkspace.Human("UOTHER", "Beta Tester");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, null);
        }

        var ex = await Assert.ThrowsAsync<LeaveLookupException>(() => CollectAsync(workspace));

        Assert.Equal(LeaveLookupFailure.SlackDirectory, ex.Reason);
    }

    [Fact]
    public async Task Caller_cancellation_stays_a_cancellation()
    {
        var workspace = new LeaveSlackWorkspace();
        workspace.AddHumans(LeaveSlackWorkspace.Human("U1", "Alpha Tester"));
        workspace.AuthFailure = new OperationCanceledException();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SlackChannelRosterCollector(workspace.Client, Channel).CollectAsync(cts.Token));
    }

    [Fact]
    public async Task Non_slack_bug_is_not_disguised_as_a_slack_outage()
    {
        var workspace = new LeaveSlackWorkspace
        {
            AuthFailure = new InvalidOperationException("bug"),
        };
        workspace.AddHumans(LeaveSlackWorkspace.Human("U1", "Alpha Tester"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(workspace));
    }
}
