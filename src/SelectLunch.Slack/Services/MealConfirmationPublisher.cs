using SlackNet;

namespace SelectLunch.Slack.Services;

/// <summary>확정된 방문만 알린다. 소켓 승인 전에는 Slack API를 기다리지 않는다.</summary>
public sealed class MealConfirmationPublisher(
    LunchService lunch, LunchAnnouncer announcer, EarlyDepartureNoticeTracker notices,
    ILogger<MealConfirmationPublisher> logger)
{
    public void AfterSave(DateOnly date, string userId, MealSaveResult saved)
    {
        if (saved.IsToday)
            notices.CompleteDay(date);

        // SlackNet 0.18: 소켓 응답을 보낸 뒤 요청 종료 콜백을 역순 실행한다. 이 콜백은 DI scope 해제보다 먼저다.
        SlackRequestContext.Current.OnComplete(() => PublishAsync(date, userId, saved));
    }

    async Task PublishAsync(DateOnly date, string userId, MealSaveResult saved)
    {
        if (saved.IsToday && saved.Registered is { } registered)
        {
            try
            {
                if (await lunch.AddCandidateToOpenPollAsync(registered.Id, CancellationToken.None) is { } pollId)
                    await announcer.RefreshPollAsync(pollId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning("식사 기록 후 투표 후보 갱신 실패 ({ErrorType}).", ex.GetType().Name);
            }
        }

        try
        {
            await announcer.PostMealConfirmationAsync(date, userId, saved, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 이미 확정된 방문을 되돌리거나 사용자의 재제출을 유도하지 않는다. 요청의 나머지 정리도 계속한다.
            logger.LogWarning("확정된 식사 기록의 채널 공지 실패 ({ErrorType}).", ex.GetType().Name);
        }
    }
}
