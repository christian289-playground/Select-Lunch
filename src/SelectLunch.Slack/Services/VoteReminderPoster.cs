using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Scheduling;
using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Services;

/// <summary>
/// 투표 마감 5분 전에, 아직 투표도 기권도 하지 않은 채널 멤버에게 **본인에게만 보이는**
/// 비공개(ephemeral) 알림을 보낸다. 채널 공개 메시지·DM·스레드 답글은 쓰지 않는다.
///
/// 핵심 제약은 "한 번만"이다. 비공개 메시지는 보낸 뒤 취소할 수 없고 재시도가 곧 중복
/// 알림이라, 이 클래스는 **최대 한 번(at-most-once)** 을 보장하도록 짜여 있다.
/// <list type="bullet">
/// <item>보내기 전에 <see cref="LunchPoll.VoteReminderAttemptedAt"/>을 원자적으로 선점한다.
/// 선점은 한 번만 성공하므로 같은 풀에서 배치가 둘 나가지 않는다(재기동·동시 호출 포함).</item>
/// <item>선점한 뒤에는 어떤 실패도 다시 시도하지 않는다. 한 명에게 실패해도 로그만 남기고 다음
/// 사람으로 넘어가며, 재기동 뒤에도 표시가 남아 있어 아무도 다시 받지 않는다.</item>
/// <item>선점 **전** 단계(멤버·프로필·퍼머링크 조회)는 아무것도 보내지 않았으므로 실패하면
/// 그대로 던지고, 표시가 없으니 다음 tick에 처음부터 다시 한다.</item>
/// </list>
///
/// 마감 시각(<see cref="LunchPoll.ClosesAt"/>)이 되면 조회·선점·전송 어느 단계에서든 멈춘다.
/// 시각은 주입받은 <see cref="TimeProvider"/>로만 읽는다.
/// </summary>
/// <param name="slack">
/// 이 서비스 전용 클라이언트여야 한다. SlackNet은 429(<c>SlackRateLimitException</c>)에서 기본적으로
/// <c>Retry-After</c>만큼 기다린 뒤 같은 요청을 **자동으로 다시 보낸다** — 비공개 메시지에는 곧
/// 중복 전송이라, <c>SlackApiClient.DisableRetryOnRateLimit = true</c>로 만든 인스턴스를 넘겨야 한다.
/// </param>
public sealed class VoteReminderPoster(
    ISlackApiClient slack,
    LunchDbContext db,
    string channelId,
    TimeProvider clock,
    ILogger<VoteReminderPoster> logger)
{
    /// <summary>conversations.members 한 페이지 크기. 이 값보다 멤버가 많으면 cursor로 이어 읽는다.</summary>
    const int MembersPageSize = 200;

    /// <summary>슬랙봇은 users.info에서 is_bot=false로 나오므로 ID로 따로 걸러야 한다.</summary>
    const string SlackbotUserId = "USLACKBOT";

    /// <returns>슬랙이 받아 준 비공개 알림 수. 보낼 대상이 없거나 보내면 안 되는 상태면 0.</returns>
    public async Task<int> PostAsync(long pollId, CancellationToken ct)
    {
        var poll = await db.Polls
            .AsNoTracking()
            .Where(p => p.Id == pollId)
            .Select(p => new { p.ChannelId, p.Status, p.MessageTs, p.ClosesAt, p.VoteReminderAttemptedAt })
            .SingleOrDefaultAsync(ct);

        if (poll is null
            || poll.ChannelId != channelId
            || poll.Status != PollStatus.Open
            || string.IsNullOrEmpty(poll.MessageTs)
            || poll.VoteReminderAttemptedAt is not null)
            return 0;

        // 비교는 메모리에서 한다 — SQLite는 DateTimeOffset를 서버에서 비교하지 못한다.
        // 기준은 옵션의 마감 시각이 아니라 이 풀에 저장된 ClosesAt이다.
        var closesAt = poll.ClosesAt;
        var now = clock.GetUtcNow();
        if (now < closesAt - LunchSchedule.VoteReminderLeadTime || now >= closesAt)
            return 0;

        // 마감 시각에 스스로 취소되는 토큰. 이후의 모든 슬랙·DB 호출이 이 토큰을 쓰므로
        // 느린 조회나 멈춘 전송이 마감 뒤까지 이어지지 않는다. 호스트 종료(ct)는 따로 전파한다.
        using var deadline = new CancellationTokenSource(closesAt - now, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;

        var sent = 0;
        var failed = 0;
        var skipped = 0;
        var total = 0;

        try
        {
            var recipients = await FindRecipientsAsync(pollId, token);
            total = recipients.Count;

            // 퍼머링크도 선점 전에 얻는다 — 실패해도 아무것도 보내지 않았으니 다음 tick에 다시 할 수 있다.
            // 대상이 없으면 호출 자체를 하지 않는다.
            var permalink = recipients.Count == 0
                ? string.Empty
                : (await slack.Chat.GetPermalink(channelId, poll.MessageTs, token)).Permalink;

            // 대상이 없어도 표시는 남긴다 — 그래야 이후 tick이 멤버 목록을 다시 훑지 않는다.
            if (!await TryClaimAsync(pollId, closesAt, token))
                return 0;

            if (recipients.Count == 0)
            {
                logger.LogInformation("투표·기권하지 않은 멤버가 없어 비공개 알림을 보내지 않습니다.");
                return 0;
            }

            var message = new Message
            {
                Channel = channelId,
                // ThreadTs는 일부러 비운다. 비공개 메시지가 스레드 안에서는 보이지 않는 경우가 있다.
                Text = ReminderText(permalink),
            };

            foreach (var userId in recipients)
            {
                token.ThrowIfCancellationRequested();

                // 조회하는 사이 투표했을 수 있다. 보내기 직전에 다시 확인한다.
                if (!await IsStillPendingAsync(pollId, userId, token))
                {
                    skipped++;
                    continue;
                }

                if (clock.GetUtcNow() >= closesAt)
                {
                    logger.LogInformation("투표가 마감되어 남은 비공개 알림을 보내지 않습니다.");
                    break;
                }

                try
                {
                    await slack.Chat.PostEphemeral(userId, message, token);
                    sent++;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // 이미 선점했다. 다시 보내면 중복 알림이 될 수 있으므로 재시도하지 않는다.
                    failed++;
                    logger.LogWarning(ex, "{UserId}: 비공개 투표 알림 전송에 실패했습니다. 다시 시도하지 않습니다.", userId);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // 마감 시각이 되어 멈춘 것이다(호스트 종료는 위 조건에서 제외되어 그대로 전파된다).
            logger.LogInformation("투표가 마감되어 비공개 알림 처리를 멈춥니다. 전송 {Sent}명.", sent);
            return sent;
        }

        logger.LogInformation(
            "미참여자 {Total}명 중 {Sent}명에게 비공개 투표 알림을 보냈습니다 (실패 {Failed}명, 건너뜀 {Skipped}명).",
            total, sent, failed, skipped);
        return sent;
    }

    /// <summary>
    /// 알림 대상: 채널 멤버 중 투표·기권 행이 없는 사람. 슬랙봇·봇·앱·비활성 계정은 뺀다.
    /// 반환 순서는 ID 서수 정렬이라 같은 입력이면 항상 같은 순서다.
    /// </summary>
    async Task<List<string>> FindRecipientsAsync(long pollId, CancellationToken token)
    {
        var members = await ListMemberIdsAsync(token);

        // 기권은 RestaurantId가 null인 행이라 이 한 번의 조회로 투표·기권을 함께 걸러 낸다.
        var responded = await db.PollVotes
            .AsNoTracking()
            .Where(v => v.PollId == pollId)
            .Select(v => v.SlackUserId)
            .ToHashSetAsync(StringComparer.Ordinal, token);

        var recipients = new List<string>();
        foreach (var userId in members.Order(StringComparer.Ordinal))
        {
            if (userId == SlackbotUserId || responded.Contains(userId))
                continue;

            // 프로필 조회는 아직 응답 안 한 사람에게만 한다.
            if (await IsPersonAsync(userId, token))
                recipients.Add(userId);
        }

        return recipients;
    }

    async Task<HashSet<string>> ListMemberIdsAsync(CancellationToken token)
    {
        // HashSet이 페이지 경계의 중복 ID도 걸러 준다.
        var members = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            var page = await slack.Conversations.Members(channelId, MembersPageSize, cursor, token);

            foreach (var id in page.Members ?? [])
            {
                if (!string.IsNullOrEmpty(id))
                    members.Add(id);
            }

            // 마지막 페이지는 NextCursor가 빈 문자열(또는 null)이다.
            cursor = page.ResponseMetadata?.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return members;
    }

    /// <summary>
    /// 사람 계정인가. 슬랙 커넥트 외부 멤버처럼 프로필을 볼 수 없는 계정은
    /// 확인할 수 없으니 보내지 않고 그 사람만 뺀다 — 한 명 때문에 전원의 알림이 막히면 안 된다.
    /// 그 밖의 오류(권한 스코프 누락, 429, 네트워크)는 모든 사용자에게 똑같이 일어날 수 있으므로 삼키지 않고
    /// 던진다. 삼키면 전원이 제외된 채 표시만 남아 알림이 조용히 사라진다. 선점 전이라 다음 tick에 처음부터 다시 한다.
    /// </summary>
    async Task<bool> IsPersonAsync(string userId, CancellationToken token)
    {
        try
        {
            var user = await slack.Users.Info(userId, false, token);
            return !(user.Deleted || user.IsBot || user.IsAppUser);
        }
        catch (SlackException ex) when (ex.ErrorCode is "user_not_found" or "user_not_visible")
        {
            logger.LogWarning(ex, "{UserId}: 사용자 정보를 확인할 수 없어 알림 대상에서 제외합니다.", userId);
            return false;
        }
    }

    /// <summary>
    /// 풀 행에서 알림 표시를 선점한다. id·채널·진행 중·미표시를 한 UPDATE의 조건으로 걸어
    /// 정확히 한 호출만 성공한다. 마감 시각은 SQLite에서 비교할 수 없어 메모리에서 먼저 본다.
    /// </summary>
    /// <returns>선점에 성공했으면 true. 이미 누가 표시했거나 닫혔으면 false(보내지 않는다).</returns>
    async Task<bool> TryClaimAsync(long pollId, DateTimeOffset closesAt, CancellationToken token)
    {
        // 조회가 길어져 이미 마감 시각을 넘었다면 표시도, 전송도 하지 않는다.
        DateTimeOffset? attemptedAt = clock.GetUtcNow();
        if (attemptedAt >= closesAt)
        {
            logger.LogInformation("조회하는 사이 투표가 마감되어 비공개 알림을 보내지 않습니다.");
            return false;
        }

        var claimed = await db.Polls
            .Where(p => p.Id == pollId
                        && p.ChannelId == channelId
                        && p.Status == PollStatus.Open
                        && p.VoteReminderAttemptedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.VoteReminderAttemptedAt, attemptedAt), token);

        if (claimed == 0)
            logger.LogInformation("다른 경로에서 이미 알림을 처리했거나 풀이 닫혀 비공개 알림을 보내지 않습니다.");

        return claimed > 0;
    }

    /// <summary>풀이 아직 열려 있고 이 사람의 투표·기권 행이 없는가. 추적하지 않고 DB에서 새로 읽는다.</summary>
    Task<bool> IsStillPendingAsync(long pollId, string userId, CancellationToken token) =>
        db.Polls
            .AsNoTracking()
            .AnyAsync(p => p.Id == pollId
                           && p.Status == PollStatus.Open
                           && !p.Votes.Any(v => v.SlackUserId == userId), token);

    /// <summary>
    /// 정확히 5분이 아닐 수 있다(스케줄러 지연·재기동) — 그래서 시간을 박지 않고 "곧 마감"으로 쓴다.
    /// </summary>
    static string ReminderText(string permalink) =>
        $"점심 투표 마감이 곧 다가옵니다. 아직 투표하지 않으셨다면 <{permalink}|투표 메시지>에서 " +
        "식당을 고르거나 '나 오늘 따로 먹어요'를 눌러 주세요.";
}
