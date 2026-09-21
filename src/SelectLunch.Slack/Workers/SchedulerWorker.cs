using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Workers;

/// <summary>
/// DB 상태를 기준으로 할 일을 찾아 실행한다. 타이머가 아니라 상태 기반이라
/// 앱이 꺼져 있던 구간을 재기동 시 따라잡고, 중복 발송도 누락도 생기지 않는다.
/// </summary>
public sealed class SchedulerWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<LunchOptions> lunchOptions,
    IOptions<SlackOptions> slackOptions,
    ILogger<SchedulerWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NotifyOnOptionsChange();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 한 번의 실패로 루프가 죽으면 그날 점심이 통째로 날아간다
                logger.LogError(ex, "스케줄 처리 중 오류. 다음 주기에 다시 시도합니다.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(lunchOptions.CurrentValue.PollIntervalSeconds),
                stoppingToken);
        }
    }

    async Task TickAsync(CancellationToken ct)
    {
        var options = lunchOptions.CurrentValue;
        var channelId = slackOptions.Value.ChannelId;
        var now = LunchClock.NowIn(options.TimeZone);
        var today = DateOnly.FromDateTime(now.DateTime);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LunchDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<LunchService>();
        var announcer = scope.ServiceProvider.GetRequiredService<LunchAnnouncer>();

        var state = await db.GetTodayStateAsync(channelId, today, ct);

        foreach (var action in LunchSchedule.GetDueActions(now, state, options))
        {
            logger.LogInformation("{Kind} 실행 (예정 {ScheduledFor:HH:mm})", action.Kind, action.ScheduledFor);

            switch (action.Kind)
            {
                case DueActionKind.OpenPoll:
                    var poll = await service.OpenPollAsync(
                        today, action.ScheduledFor,
                        action.ScheduledFor.AddMinutes(options.VoteDurationMinutes), ct);
                    await announcer.PostPollAsync(poll.Id, poll.ClosesAt, ct);
                    break;

                case DueActionKind.ClosePoll:
                    var outcome = await service.ClosePollAsync(
                        state.Poll!.PollId, today, options.Recommendation, ct);
                    await announcer.PostResultAsync(outcome, options.Recommendation, ct);
                    break;

                case DueActionKind.PostMealPrompt:
                    await announcer.PostMealPromptAsync(today, ct);
                    await service.MarkMealPromptPostedAsync(today, now, ct);
                    break;

                case DueActionKind.RemindPending:
                    await announcer.PostPendingReminderAsync(ct);
                    await service.MarkPendingReminderSentAsync(today, now, ct);
                    break;
            }
        }
    }

    /// <summary>설정이 바뀌면 채널에 한 줄 남긴다. 조용한 변경은 추적을 어렵게 한다.</summary>
    void NotifyOnOptionsChange()
    {
        var previous = Snapshot(lunchOptions.CurrentValue);

        lunchOptions.OnChange(options =>
        {
            var current = Snapshot(options);
            if (current == previous)
                return;

            logger.LogInformation("설정 변경 감지: {Previous} → {Current}", previous, current);
            previous = current;
        });
    }

    static string Snapshot(LunchOptions o) =>
        $"투표 {o.VoteOpenAt:HH\\:mm}(+{o.VoteDurationMinutes}분) · 기록 {o.MealRecordAt:HH\\:mm} · " +
        $"가중치 {o.Recommendation.Weight7d}/{o.Recommendation.Weight30d}";
}
