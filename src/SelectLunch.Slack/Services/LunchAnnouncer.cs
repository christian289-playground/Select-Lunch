using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Services;

/// <summary>블록을 슬랙에 실제로 보낸다. 도메인 판단은 하지 않는다.</summary>
public sealed class LunchAnnouncer(
    ISlackApiClient slack,
    LunchDbContext db,
    LunchService service,
    string channelId)
{
    public async Task<string> PostPollAsync(long pollId, DateTimeOffset closesAt, CancellationToken ct)
    {
        var candidates = await db.GetPollCandidatesAsync(pollId, ct);
        var blocks = PollBlocks.Build(pollId, candidates, [], [], closesAt);

        var ts = await PostAsync(blocks, "오늘 점심 뭐 먹지?", ct);

        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        poll.MessageTs = ts;
        await db.SaveChangesAsync(ct);

        return ts;
    }

    /// <summary>투표 후 집계를 메시지에 되비춘다.</summary>
    public async Task RefreshPollAsync(long pollId, CancellationToken ct)
    {
        // 투표는 이미 커밋된 뒤 호출된다 — 풀을 못 찾아도 예외를 던지면 안 되고
        // 그냥 갱신을 건너뛴다(사용자에게는 방금 누른 표가 이미 반영된 상태다).
        // 마감된 풀도 건너뛴다 — 그렇지 않으면 버튼이 제거된 메시지에 버튼을
        // 되살려 놓게 된다(IMPORTANT 3).
        var poll = await db.Polls.SingleOrDefaultAsync(p => p.Id == pollId, ct);
        if (poll?.MessageTs is null || poll.Status != PollStatus.Open)
            return;

        var candidates = await db.GetPollCandidatesAsync(pollId, ct);
        var tallies = await service.GetTalliesAsync(pollId, ct);
        var abstainers = await service.GetAbstainersAsync(pollId, ct);

        await slack.Chat.Update(new MessageUpdate
        {
            ChannelId = channelId,
            Ts = poll.MessageTs,
            Text = "오늘 점심 뭐 먹지?",
            Blocks = PollBlocks.Build(pollId, candidates, tallies, abstainers, poll.ClosesAt),
        }, ct);
    }

    /// <summary>
    /// 마감 시 투표 메시지의 버튼/드롭다운을 없앤다. 집계는 그대로 남기되 더 이상
    /// 누를 수 없게 한다 — 그렇지 않으면 며칠 지난 메시지의 버튼이 계속 살아있는
    /// 것처럼 보인다(IMPORTANT 3).
    /// </summary>
    public async Task ClosePollMessageAsync(long pollId, CancellationToken ct)
    {
        var poll = await db.Polls.SingleOrDefaultAsync(p => p.Id == pollId, ct);
        if (poll?.MessageTs is null)
            return;

        var candidates = await db.GetPollCandidatesAsync(pollId, ct);
        var tallies = await service.GetTalliesAsync(pollId, ct);
        var abstainers = await service.GetAbstainersAsync(pollId, ct);

        await slack.Chat.Update(new MessageUpdate
        {
            ChannelId = channelId,
            Ts = poll.MessageTs,
            Text = "오늘 점심 뭐 먹지?",
            Blocks = PollBlocks.Build(pollId, candidates, tallies, abstainers, poll.ClosesAt, closed: true),
        }, ct);
    }

    public Task PostResultAsync(PollOutcome outcome, RecommendationOptions options, CancellationToken ct) =>
        PostAsync(ResultBlocks.Build(outcome, options), "오늘 점심 투표 결과", ct);

    public async Task PostMealPromptAsync(DateOnly date, CancellationToken ct)
    {
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        var recorded = await db.MealRecords
            .Where(m => m.ChannelId == channelId && m.Date == date)
            .Select(m => m.Restaurant!.Name)
            .SingleOrDefaultAsync(ct);

        await PostAsync(MealPromptBlocks.Build(date, restaurants, recorded), "오늘 뭐 드셨어요?", ct);
    }

    public async Task PostPendingReminderAsync(CancellationToken ct)
    {
        // SQLite 프로바이더는 DateTimeOffset을 ORDER BY 절로 번역하지 못한다
        // (NotSupportedException) — 먼저 받아온 뒤 메모리에서 정렬한다.
        var pending = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Pending)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        pending = [.. pending.OrderBy(r => r.CreatedAt)];

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(
                    $"ℹ️ 정보가 덜 찬 식당이 {pending.Count}곳 있습니다. " +
                    "카테고리를 채워 주시면 추천에 반영됩니다."),
            },
        };

        var actions = new ActionsBlock();
        foreach (var restaurant in pending.Take(5))
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.RestaurantFill(restaurant.Id),
                Text = new PlainText(restaurant.Name),
            });
        }
        blocks.Add(actions);

        await PostAsync(blocks, "정보가 덜 찬 식당이 있습니다", ct);
    }

    public Task PostTextAsync(string markdown, CancellationToken ct) =>
        PostAsync([new SectionBlock { Text = new Markdown(markdown) }], markdown, ct);

    async Task<string> PostAsync(IList<Block> blocks, string fallbackText, CancellationToken ct)
    {
        var response = await slack.Chat.PostMessage(new Message
        {
            Channel = channelId,
            Text = fallbackText,   // 알림·접근성용 대체 텍스트
            Blocks = blocks,
        }, ct);

        return response.Ts;
    }
}
