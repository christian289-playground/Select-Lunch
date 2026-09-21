using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;
using SelectLunch.Slack.Blocks;
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
                    // 풀 행이 이미 있는데 메시지만 없는 경우(OpenPollAsync 커밋 직후·
                    // PostPollAsync 전에 죽은 재시작 복구, IMPORTANT 4)는 새로 만들지
                    // 않고 기존 행 그대로 메시지만 올린다 — 안 그러면 그 날 풀이 두 개
                    // 생겨 집계가 갈라진다.
                    long pollId;
                    DateTimeOffset closesAt;
                    if (state.Poll is { } existing)
                    {
                        pollId = existing.PollId;
                        closesAt = existing.ClosesAt;
                    }
                    else
                    {
                        // OpenPollAsync가 먼저 행을 커밋해 id를 확보해야 PostPollAsync가
                        // 그 id로 메시지를 올릴 수 있다 — 그래서 먼저 저장하고 나중에
                        // 게시하는 순서가 강제된다(다른 액션들과 마크/게시 순서가 다른
                        // 이유).
                        var poll = await service.OpenPollAsync(
                            today, action.ScheduledFor,
                            action.ScheduledFor.AddMinutes(options.VoteDurationMinutes), ct);
                        pollId = poll.Id;
                        closesAt = poll.ClosesAt;
                    }
                    await announcer.PostPollAsync(pollId, closesAt, ct);
                    break;

                case DueActionKind.ClosePoll:
                    var closingPollId = state.Poll!.PollId;
                    PollOutcome outcome;

                    if (state.Poll.Status == PollStatus.Open)
                    {
                        // 아직 열려 있던 풀을 실제로 마감한다 — 집계를 확정하고, 이미
                        // 나가 있는 투표 메시지에서 버튼/드롭다운을 없애 더 이상 눌러도
                        // 반영되지 않게 만든다(IMPORTANT 3).
                        outcome = await service.ClosePollAsync(closingPollId, today, options.Recommendation, ct);
                        await announcer.ClosePollMessageAsync(closingPollId, ct);
                    }
                    else
                    {
                        // 이미 닫혔지만 발표(슬랙 게시)만 실패했던 경우 — 다시 마감하지
                        // 않고 커밋된 값에서 결과만 복원해 발표를 재시도한다(CRITICAL 2).
                        outcome = await service.GetClosedOutcomeAsync(closingPollId, ct);
                    }

                    await announcer.PostResultAsync(outcome, options.Recommendation, ct);
                    await service.MarkResultAnnouncedAsync(closingPollId, now, ct);
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

    /// <summary>설정이 바뀌면 로그에 한 줄 남긴다. 채널에는 올리지 않는다 — 설정을
    /// 고칠 때마다 채널에 메시지가 뜨면 소음만 늘어난다. 조용한 변경은 추적을
    /// 어렵게 하므로 로그로만 남긴다.</summary>
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
