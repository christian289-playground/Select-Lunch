using SelectLunch.Shared.Members;
using SlackNet;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// Slack SDK로 설정 채널의 "사람 구성원 전체"를 모아 <see cref="ChannelRoster"/>로 만든다.
/// 호출: auth.test(워크스페이스) → conversations.members(페이지네이션) → 구성원마다 users.info.
/// 앱이 이미 쓰는 스코프(channels:read / groups:read / users:read)만 필요하다.
///
/// 하나라도 실패하면 <see cref="LeaveLookupException"/>으로 전체를 포기한다. 부분 명단은 만들지 않는다 —
/// 빠진 사람이 DB에서 퇴장 처리되기 때문이다. 슬랙봇·봇·앱·삭제 계정은 명단에서 뺀다.
/// </summary>
public sealed class SlackChannelRosterCollector
{
    /// <summary>conversations.members 한 페이지 크기(<c>VoteReminderPoster</c>와 같다).</summary>
    const int MembersPageSize = 200;

    /// <summary>슬랙봇은 users.info에서 is_bot=false로 나오므로 ID로 따로 거른다.</summary>
    const string SlackbotUserId = "USLACKBOT";

    readonly ISlackApiClient _slack;
    readonly string _channelId;

    public SlackChannelRosterCollector(ISlackApiClient slack, string channelId)
    {
        ArgumentNullException.ThrowIfNull(slack);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        _slack = slack;
        _channelId = channelId;
    }

    public async Task<ChannelRoster> CollectAsync(CancellationToken ct)
    {
        try
        {
            return await CollectCoreAsync(ct);
        }
        catch (Exception ex) when (ex is SlackException or HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // 응답 본문·예외 메시지에는 개인정보나 내부 세부가 섞일 수 있어 분류만 남긴다.
            throw new LeaveLookupException(LeaveLookupFailure.SlackDirectory);
        }
    }

    async Task<ChannelRoster> CollectCoreAsync(CancellationToken ct)
    {
        var auth = await _slack.Auth.Test(ct);
        var workspaceId = Clean(auth?.TeamId) ?? throw Failure();

        var memberIds = await ListMemberIdsAsync(ct);
        if (memberIds.Count == 0)
        {
            // 봇도 채널 구성원이라 정상이면 비어 있을 수 없다. 이 응답으로 퇴장 처리를 하지 않는다.
            throw Failure();
        }

        var people = new List<RosterPerson>(memberIds.Count);
        foreach (var userId in memberIds)
        {
            if (userId == SlackbotUserId)
                continue;

            var user = await _slack.Users.Info(userId, false, ct);
            if (user is null || !string.Equals(user.Id, userId, StringComparison.Ordinal))
                throw Failure();

            if (user.Deleted || user.IsBot || user.IsAppUser)
                continue;

            var realName = Clean(user.Profile?.RealName) ?? Clean(user.RealName) ?? Clean(user.Name) ?? "";
            var displayName = Clean(user.Profile?.DisplayName) ?? "";
            people.Add(new RosterPerson(userId, realName, displayName));
        }

        return new ChannelRoster(workspaceId, _channelId, people);
    }

    async Task<List<string>> ListMemberIdsAsync(CancellationToken ct)
    {
        var ids = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var cursorsSeen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var page = await _slack.Conversations.Members(_channelId, MembersPageSize, cursor, ct);

            foreach (var raw in page?.Members ?? [])
            {
                var id = Clean(raw) ?? throw Failure();
                if (known.Add(id))
                    ids.Add(id);
            }

            // 마지막 페이지는 NextCursor가 빈 문자열(또는 null)이다.
            cursor = page?.ResponseMetadata?.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !cursorsSeen.Add(cursor))
                throw Failure();   // 같은 커서를 되풀이하면 끝나지 않는다.
        }
        while (!string.IsNullOrEmpty(cursor));

        return ids;
    }

    static LeaveLookupException Failure() => new(LeaveLookupFailure.SlackDirectory);

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
