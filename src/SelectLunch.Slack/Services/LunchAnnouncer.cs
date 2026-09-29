using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    string channelId,
    ILogger<LunchAnnouncer>? logger = null)
{
    public async Task<string> PostPollAsync(long pollId, DateTimeOffset closesAt, CancellationToken ct)
    {
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        var candidates = await db.GetPollCandidatesAsync(pollId, ct);
        var menus = await GetMenuImagesAsync(pollId, poll.Date, ct);

        var ts = "";
        await WithMenuFallbackAsync(menus, async images =>
        {
            var blocks = PollBlocks.Build(pollId, candidates, [], [], closesAt, menuImages: images);
            ts = await PostAsync(blocks, "오늘 점심 뭐 먹지?", ct);
        }, ct);

        poll.MessageTs = ts;
        await db.SaveChangesAsync(ct);

        return ts;
    }

    /// <summary>
    /// 후보 식당 중 <paramref name="pollDate"/>(투표 날짜)자 메뉴 이미지가 있는 것만 돌려준다.
    /// TodayMenuDate가 그날이 아니면 URL이 남아 있어도 절대 쓰지 않는다 — 지난 메뉴를
    /// 오늘 것처럼 보여주는 것이 아예 안 보여주는 것보다 나쁘다.
    /// </summary>
    async Task<IReadOnlyList<MenuImage>> GetMenuImagesAsync(long pollId, DateOnly pollDate, CancellationToken ct)
    {
        var rows = await db.PollCandidates
            .Where(c => c.PollId == pollId
                        && c.Restaurant!.TodayMenuDate == pollDate
                        && c.Restaurant.TodayMenuImageUrl != null)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new { c.Restaurant!.Name, Url = c.Restaurant.TodayMenuImageUrl! })
            .ToListAsync(ct);

        return [.. rows.Select(r => new MenuImage(r.Name, r.Url))];
    }

    /// <summary>
    /// 메뉴 이미지가 든 블록을 보내 보고, 어떤 이유로든 실패하면 이미지 없이 한 번 더 보낸다.
    /// Slack은 메시지를 받을 때 이미지 URL을 가져와 보므로 CDN URL이 만료·차단되면
    /// invalid_blocks 등으로 발송 자체가 거절될 수 있다 — 투표는 메뉴 없이도 나가야 한다.
    /// 이미지 없는 재시도가 성공하면 이미지가 원인이라는 뜻이므로 그 URL을 버려(날짜는 유지해
    /// 재수집도 막는다) 다음 tick에서 같은 URL로 계속 실패하지 않게 한다.
    /// 재시도도 실패하면 이미지 탓이 아니므로 그대로 던진다(호출자의 기존 재시도 경로).
    /// </summary>
    async Task WithMenuFallbackAsync(
        IReadOnlyList<MenuImage> menus, Func<IReadOnlyList<MenuImage>, Task> send, CancellationToken ct)
    {
        try
        {
            await send(menus);
            return;
        }
        catch (Exception ex) when (menus.Count > 0 && !ct.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "메뉴 이미지가 든 메시지 전송에 실패했습니다. 이미지 없이 다시 보냅니다.");
        }

        await send([]);

        // 여기부터는 뒷정리다 — 메시지는 이미 나갔다. 정리가 실패해 예외가 밖으로 나가면
        // PostPollAsync가 MessageTs를 저장하기 전에 끊겨 풀이 "메시지 없음"으로 남고,
        // 스케줄러가 같은 날 투표를 또 올린다. 그래서 어떤 실패도 여기서 삼킨다.
        try
        {
            var urls = menus.Select(m => m.ImageUrl).ToList();
            var bad = await db.Restaurants.Where(r => urls.Contains(r.TodayMenuImageUrl!)).ToListAsync(ct);
            foreach (var restaurant in bad)
                restaurant.TodayMenuImageUrl = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 실패한 변경이 추적기에 남으면 뒤이은 SaveChanges(MessageTs 저장)에 다시 실려 같이 실패한다.
            foreach (var entry in db.ChangeTracker.Entries<Restaurant>()
                         .Where(e => e.State == EntityState.Modified).ToList())
                entry.State = EntityState.Detached;

            logger?.LogWarning(ex, "거절된 메뉴 이미지 URL 정리에 실패했습니다. 메시지는 이미 이미지 없이 전송되었습니다.");
        }
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

        await WithMenuFallbackAsync(await GetMenuImagesAsync(pollId, poll.Date, ct), images =>
            slack.Chat.Update(new MessageUpdate
            {
                ChannelId = channelId,
                Ts = poll.MessageTs,
                Text = "오늘 점심 뭐 먹지?",
                Blocks = PollBlocks.Build(pollId, candidates, tallies, abstainers, poll.ClosesAt,
                    menuImages: images),
            }, ct), ct);
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

        await WithMenuFallbackAsync(await GetMenuImagesAsync(pollId, poll.Date, ct), images =>
            slack.Chat.Update(new MessageUpdate
            {
                ChannelId = channelId,
                Ts = poll.MessageTs,
                Text = "오늘 점심 뭐 먹지?",
                Blocks = PollBlocks.Build(pollId, candidates, tallies, abstainers, poll.ClosesAt, closed: true,
                    menuImages: images),
            }, ct), ct);
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
