using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 휴가 명단 수집 테스트용 가짜 Slack 워크스페이스. <c>ISlackApiClient</c>의 auth.test / conversations.members /
/// users.info 세 호출만 동작하고 나머지는 호출되면 즉시 실패한다. 페이지 경계는 슬랙 규칙대로 마지막 페이지의
/// NextCursor가 빈 문자열이다. 모든 이름·ID는 합성 값이다.
/// </summary>
sealed class LeaveSlackWorkspace
{
    public string? TeamId { get; set; } = "T1";

    /// <summary>conversations.members가 돌려줄 페이지들.</summary>
    public List<string[]> Pages { get; } = [];

    /// <summary>여기 없는 사용자는 users.info에서 평범한 활성 계정이 아니라 <c>user_not_found</c>다.</summary>
    public Dictionary<string, User> Users { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Exception> InfoFailures { get; } = new(StringComparer.Ordinal);

    public Exception? AuthFailure { get; set; }

    /// <summary>이 페이지 번호(0부터)를 읽을 때 던진다.</summary>
    public Dictionary<int, Exception> MembersFailures { get; } = new();

    /// <summary>true면 다음 페이지 커서가 항상 같은 값이라 끝나지 않는다.</summary>
    public bool RepeatCursorForever { get; set; }

    public List<string> InfoRequests { get; } = [];

    public List<(string ChannelId, string? Cursor)> MemberRequests { get; } = [];

    public ISlackApiClient Client { get; }

    public LeaveSlackWorkspace()
    {
        var auth = StubApi.Of<IAuthApi>(
            ("Test", (Func<CancellationToken, Task<AuthTestResponse>>)AuthAsync));
        var conversations = StubApi.Of<IConversationsApi>(
            ("Members", (Func<string, int, string?, CancellationToken, Task<ConversationMembersResponse>>)MembersAsync));
        var users = StubApi.Of<IUsersApi>(
            ("Info", (Func<string, bool, CancellationToken, Task<User>>)InfoAsync));

        Client = StubApi.Of<ISlackApiClient>(
            ("get_Auth", (Func<IAuthApi>)(() => auth)),
            ("get_Conversations", (Func<IConversationsApi>)(() => conversations)),
            ("get_Users", (Func<IUsersApi>)(() => users)));
    }

    public static User Human(string id, string realName, string displayName = "") => new()
    {
        Id = id,
        Name = id.ToLowerInvariant() + "_login",
        Profile = new UserProfile { RealName = realName, DisplayName = displayName },
    };

    /// <summary>한 페이지에 모두 담고 사용자 프로필을 등록한다.</summary>
    public LeaveSlackWorkspace AddHumans(params User[] humans)
    {
        Pages.Add([.. humans.Select(h => h.Id)]);
        foreach (var human in humans)
            Users[human.Id] = human;

        return this;
    }

    Task<AuthTestResponse> AuthAsync(CancellationToken ct)
    {
        if (AuthFailure is { } failure)
            throw failure;

        return Task.FromResult(new AuthTestResponse { TeamId = TeamId!, Team = "Test Company" });
    }

    Task<ConversationMembersResponse> MembersAsync(string channelId, int limit, string? cursor, CancellationToken ct)
    {
        MemberRequests.Add((channelId, cursor));

        var index = cursor is null ? 0 : int.Parse(cursor["page".Length..]);
        if (MembersFailures.TryGetValue(index, out var failure))
            throw failure;

        var next = RepeatCursorForever ? "page1" : index + 1 < Pages.Count ? $"page{index + 1}" : "";
        return Task.FromResult(new ConversationMembersResponse
        {
            Members = Pages[Math.Min(index, Pages.Count - 1)],
            ResponseMetadata = new ResponseMetadata { NextCursor = next },
        });
    }

    Task<User> InfoAsync(string userId, bool includeLocale, CancellationToken ct)
    {
        InfoRequests.Add(userId);

        if (InfoFailures.TryGetValue(userId, out var failure))
            throw failure;

        return Users.TryGetValue(userId, out var user)
            ? Task.FromResult(user)
            : throw new SlackException(new ErrorResponse { Error = "user_not_found" });
    }
}
