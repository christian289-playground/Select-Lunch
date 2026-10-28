using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Services;

/// <summary>공개 투표가 기준에 이른 식당의 투표자에게만 비공개로 안내한다.</summary>
public sealed class EarlyDeparturePoster(
    ISlackApiClient slack,
    LunchDbContext db,
    string channelId,
    IOptionsMonitor<LunchOptions> options,
    TimeProvider clock,
    EarlyDepartureNoticeTracker tracker,
    ILogger<EarlyDeparturePoster> logger)
{
    internal bool IsConfiguredChannel(string? requestChannel) => requestChannel == channelId;

    public async Task<int> PostAsync(long pollId, CancellationToken ct)
    {
        var sent = 0;
        try
        {
            var settings = options.CurrentValue;
            var participantCount = settings.EarlyDepartureParticipantCount;
            if (participantCount <= 0)
            {
                logger.LogWarning("미리 출발 안내 참여 인원 설정이 올바르지 않습니다.");
                return 0;
            }

            var poll = await ReadPollAsync(pollId, ct);
            var now = clock.GetUtcNow();
            if (!Eligible(poll, now, settings.TimeZone))
                return 0;

            using var deadline = new CancellationTokenSource(poll!.ClosesAt - now, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var token = linked.Token;
            try
            {
                if (await CompleteIfRecordedAsync(poll.Date, token))
                    return 0;

                var candidates = await db.PollCandidates.AsNoTracking()
                    .Where(c => c.PollId == pollId && c.Restaurant!.Status == RestaurantStatus.Active
                        && c.Restaurant.EarlyDepartureSeconds > 0)
                    .OrderBy(c => c.RestaurantId)
                    .Select(c => c.RestaurantId)
                    .ToListAsync(token);

                foreach (var restaurantId in candidates)
                {
                    var users = await db.PollVotes.AsNoTracking()
                        .Where(v => v.PollId == pollId && v.RestaurantId == restaurantId && !v.IsSecret)
                        .OrderBy(v => v.SlackUserId)
                        .Select(v => v.SlackUserId)
                        .ToListAsync(token);
                    if ((long)users.Count * 100 < (long)participantCount * 30)
                        continue;

                    foreach (var user in users)
                    {
                        token.ThrowIfCancellationRequested();
                        if (tracker.HasAttempted(poll.Date, restaurantId, user))
                            continue;
                        var currentPoll = await ReadPollAsync(pollId, token);
                        if (!Eligible(currentPoll, clock.GetUtcNow(), settings.TimeZone))
                            return sent;

                        var restaurant = await db.PollCandidates.AsNoTracking()
                            .Where(c => c.PollId == pollId && c.RestaurantId == restaurantId
                                && c.Restaurant!.Status == RestaurantStatus.Active
                                && c.Restaurant.EarlyDepartureSeconds > 0)
                            .Select(c => new { c.Restaurant!.Name, c.Restaurant.EarlyDepartureSeconds, c.Restaurant.DepartureTime })
                            .SingleOrDefaultAsync(token);
                        if (restaurant is null)
                            continue;
                        var currentUsers = await db.PollVotes.AsNoTracking()
                            .Where(v => v.PollId == pollId && v.RestaurantId == restaurantId && !v.IsSecret)
                            .Select(v => v.SlackUserId)
                            .ToListAsync(token);
                        if ((long)currentUsers.Count * 100 < (long)participantCount * 30
                            || !currentUsers.Contains(user, StringComparer.Ordinal))
                            continue;
                        if (!Eligible(currentPoll, clock.GetUtcNow(), settings.TimeZone))
                            return sent;
                        if (await CompleteIfRecordedAsync(currentPoll!.Date, token))
                            return sent;
                        if (!Eligible(currentPoll, clock.GetUtcNow(), settings.TimeZone))
                            return sent;
                        token.ThrowIfCancellationRequested();
                        if (!tracker.TryClaim(currentPoll!.Date, restaurantId, user))
                            continue;

                        var departure = restaurant.DepartureTime is { } time
                            ? $"\n권장 출발 시각: {time.ToString("HH:mm", CultureInfo.InvariantCulture)}"
                            : string.Empty;
                        var text = $"{restaurant.Name}\n현재 공개 투표 {currentUsers.Count}/{participantCount}명 — " +
                            $"평소보다 {EarlyDepartureDuration.FormatMinutes(restaurant.EarlyDepartureSeconds)}분 미리 출발을 준비해 주세요. " +
                            $"아직 투표 중이므로 결과는 바뀔 수 있어요.{departure}";
                        var message = new Message
                        {
                            Channel = channelId,
                            Text = "미리 출발 준비 안내 (현재 투표 기준)",
                            Blocks = [new SectionBlock { Text = new PlainText(text) }],
                        };
                        try
                        {
                            await slack.Chat.PostEphemeral(user, message, token);
                            sent++;
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            // 요청이 전달됐는지 알 수 없으므로 다시 시도하지 않는다. 개인정보는 기록하지 않는다.
                            logger.LogWarning("미리 출발 비공개 안내 전송에 실패했습니다 ({ExceptionType}). 다시 시도하지 않습니다.", ex.GetType().Name);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                logger.LogInformation("투표가 마감되어 미리 출발 안내를 멈춥니다.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("미리 출발 안내를 처리하지 못했습니다 ({ExceptionType}).", ex.GetType().Name);
        }
        return sent;
    }

    async Task<bool> CompleteIfRecordedAsync(DateOnly date, CancellationToken token)
    {
        if (!await db.MealRecords.AsNoTracking().AnyAsync(
                m => m.ChannelId == channelId && m.Date == date, token))
            return false;
        tracker.CompleteDay(date);
        return true;
    }

    async Task<PollState?> ReadPollAsync(long pollId, CancellationToken token) =>
        await db.Polls.AsNoTracking().Where(p => p.Id == pollId)
            .Select(p => new PollState(p.ChannelId, p.Date, p.Status, p.MessageTs, p.ClosesAt))
            .SingleOrDefaultAsync(token);

    bool Eligible(PollState? poll, DateTimeOffset now, string timeZone) =>
        poll is not null && poll.ChannelId == channelId && poll.Status == PollStatus.Open
        && !string.IsNullOrEmpty(poll.MessageTs) && now < poll.ClosesAt
        && poll.Date == LunchClock.ToLocalDate(now, timeZone);

    sealed record PollState(string ChannelId, DateOnly Date, PollStatus Status, string? MessageTs, DateTimeOffset ClosesAt);
}
