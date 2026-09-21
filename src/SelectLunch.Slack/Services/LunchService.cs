using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Services;

/// <summary>DB를 바꾸는 동작을 모은다. 슬랙 타입을 전혀 모른다.</summary>
public sealed class LunchService(LunchDbContext db, string channelId)
{
    /// <summary>
    /// 풀과 후보를 한 트랜잭션으로 커밋한다. 나눠서 커밋하면 두 번째
    /// SaveChangesAsync가 실패했을 때 후보 없는 Open 풀만 남고, 스케줄러는
    /// "그 날 이미 처리됨"으로 보고 다시 열지 않는다 — 그 상태를 막는다.
    /// </summary>
    public async Task<LunchPoll> OpenPollAsync(
        DateOnly date, DateTimeOffset opensAt, DateTimeOffset closesAt, CancellationToken ct)
    {
        var poll = new LunchPoll
        {
            ChannelId = channelId, Date = date,
            OpensAt = opensAt, ClosesAt = closesAt, Status = PollStatus.Open,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Polls.Add(poll);
        await db.SaveChangesAsync(ct);

        var candidates = await db.GetActiveRestaurantsAsync(ct);
        var order = 0;
        foreach (var candidate in candidates.OrderBy(c => c.CategoryName, StringComparer.Ordinal)
                                            .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            db.PollCandidates.Add(new PollCandidate
            {
                PollId = poll.Id, RestaurantId = candidate.RestaurantId, DisplayOrder = order++,
            });
        }
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        return poll;
    }

    /// <summary>1인 1표. 이미 투표했으면 대상만 바꾼다.</summary>
    public async Task CastVoteAsync(long pollId, string slackUserId, long restaurantId, CancellationToken ct)
    {
        var existing = await db.PollVotes
            .SingleOrDefaultAsync(v => v.PollId == pollId && v.SlackUserId == slackUserId, ct);

        if (existing is null)
        {
            db.PollVotes.Add(new PollVote
            {
                PollId = pollId, SlackUserId = slackUserId,
                RestaurantId = restaurantId, VotedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.RestaurantId = restaurantId;
            existing.VotedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// "나 오늘 따로 먹어요". 무응답과 동일하게 취급되며 알고리즘 어디에도 영향을 주지
    /// 않는다 — RestaurantId를 null로 기록해 둘 뿐이다. CastVoteAsync와 같은 행을
    /// 공유하므로(1인 1의사표시) 투표↔기권 전환은 이 upsert 하나로 끝난다.
    /// </summary>
    public async Task CastAbstentionAsync(long pollId, string slackUserId, CancellationToken ct)
    {
        var existing = await db.PollVotes
            .SingleOrDefaultAsync(v => v.PollId == pollId && v.SlackUserId == slackUserId, ct);

        if (existing is null)
        {
            db.PollVotes.Add(new PollVote
            {
                PollId = pollId, SlackUserId = slackUserId,
                RestaurantId = null, VotedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.RestaurantId = null;
            existing.VotedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>기권(RestaurantId == null) 행은 집계에서 제외한다 — 무응답과 동일하게 취급.</summary>
    public async Task<IReadOnlyList<VoteTally>> GetTalliesAsync(long pollId, CancellationToken ct) =>
        await db.PollVotes
            .Where(v => v.PollId == pollId && v.RestaurantId != null)
            .GroupBy(v => new { v.RestaurantId, v.Restaurant!.Name })
            .Select(g => new VoteTally(g.Key.RestaurantId!.Value, g.Key.Name, g.Count()))
            .ToListAsync(ct);

    /// <summary>
    /// 기권자의 Slack user id 목록. VotedAt 순으로 정렬한다 — DateTimeOffset은
    /// SQLite 프로바이더가 ORDER BY로 번역하지 못하므로(NotSupportedException)
    /// 먼저 받아온 뒤 메모리에서 정렬한다.
    /// </summary>
    public async Task<List<string>> GetAbstainersAsync(long pollId, CancellationToken ct)
    {
        var abstainers = await db.PollVotes
            .Where(v => v.PollId == pollId && v.RestaurantId == null)
            .ToListAsync(ct);

        return [.. abstainers.OrderBy(v => v.VotedAt).Select(v => v.SlackUserId)];
    }

    /// <summary>
    /// 마감하고 결과를 낸다. 투표 동점은 추천 점수가 높은 쪽으로 푼다 —
    /// 랜덤을 쓰지 않으면서 결정적으로 해소하는 방법이다.
    /// </summary>
    public async Task<PollOutcome> ClosePollAsync(
        long pollId, DateOnly today, RecommendationOptions options, CancellationToken ct)
    {
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        var tallies = await GetTalliesAsync(pollId, ct);
        var abstainers = await GetAbstainersAsync(pollId, ct);

        var stats = await db.GetCategoryStatsAsync(today, ct);
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        var recommendation = RecommendationEngine.Recommend(today, stats, restaurants, options);

        var scoreByRestaurant = ScoreLookup(stats, restaurants, today, options);
        var winner = tallies
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => scoreByRestaurant.GetValueOrDefault(t.RestaurantId, int.MinValue))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        poll.Status = PollStatus.Closed;
        poll.WinnerRestaurantId = winner?.RestaurantId;
        poll.RecommendedRestaurantId = recommendation?.Pick.RestaurantId;
        poll.RationaleJson = recommendation is null
            ? null
            : JsonSerializer.Serialize(new { recommendation.Winner, recommendation.Others });
        await db.SaveChangesAsync(ct);

        return new PollOutcome(winner, tallies, recommendation, abstainers);
    }

    /// <summary>식당 → 소속 카테고리 점수. 투표 동점 처리에 쓴다.</summary>
    static Dictionary<long, int> ScoreLookup(
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        DateOnly today,
        RecommendationOptions options)
    {
        var byCategory = RecommendationEngine.ScoreCategories(today, stats, options)
            .ToDictionary(s => s.CategoryId, s => s.Score);

        // 카테고리 점수가 없는 경우 0을 주면 무난한 값처럼 보이지만, 실제로는
        // 많이 먹어 크게 감점된(음수) 카테고리보다 유리해져 동점 처리에서 부당하게
        // 이긴다. 알 수 없는 식당(scoreByRestaurant에 아예 없는 경우)과 같은
        // int.MinValue로 맞춰 절대 동점 우승을 못 하게 한다.
        return restaurants.ToDictionary(
            r => r.RestaurantId,
            r => byCategory.GetValueOrDefault(r.CategoryId, int.MinValue));
    }

    /// <summary>채널 단위 하루 1건. 나중에 기록한 사람이 덮어쓴다.</summary>
    public async Task RecordMealAsync(
        DateOnly date, long restaurantId, string slackUserId, MealSource source, CancellationToken ct)
    {
        var existing = await db.MealRecords
            .SingleOrDefaultAsync(m => m.ChannelId == channelId && m.Date == date, ct);

        if (existing is null)
        {
            db.MealRecords.Add(new MealRecord
            {
                ChannelId = channelId, Date = date, RestaurantId = restaurantId,
                RecordedBySlackUserId = slackUserId, RecordedAt = DateTimeOffset.UtcNow,
                Source = source,
            });
        }
        else
        {
            existing.RestaurantId = restaurantId;
            existing.RecordedBySlackUserId = slackUserId;
            existing.RecordedAt = DateTimeOffset.UtcNow;
            existing.Source = source;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 등록 또는 수정. 정규화된 이름이 같으면 기존 행을 갱신한다 —
    /// 기록 중 이름만 들어와 Pending이 된 식당을 나중에 Active로 승격시키는 경로다.
    /// </summary>
    public async Task<Restaurant> SaveRestaurantAsync(
        RestaurantDraft draft, string slackUserId, CancellationToken ct)
    {
        var normalized = Restaurant.Normalize(draft.Name);
        var now = DateTimeOffset.UtcNow;

        var restaurant = draft.RestaurantId is { } id
            ? await db.Restaurants.SingleAsync(r => r.Id == id, ct)
            : await db.Restaurants.SingleOrDefaultAsync(r => r.NormalizedName == normalized, ct);

        if (restaurant is null)
        {
            restaurant = new Restaurant
            {
                Name = draft.Name, NormalizedName = normalized,
                CreatedBySlackUserId = slackUserId, CreatedAt = now, UpdatedAt = now,
            };
            db.Restaurants.Add(restaurant);
        }

        restaurant.Name = draft.Name;
        restaurant.NormalizedName = normalized;
        // CategoryId만 ??로 보존한다 — null이 "카테고리 없음 확정"이 아니라
        // Active/Pending 불변조건을 지키기 위한 값이기 때문이다(의도치 않은 강등 방지).
        // 나머지 세 필드는 자유 선택 항목이라 null이 "비움"이라는 사용자 의도이므로
        // 그대로 대입해야 모달에서 값을 지웠을 때 실제로 지워진다.
        restaurant.CategoryId = draft.CategoryId ?? restaurant.CategoryId;
        restaurant.WalkMinutes = draft.WalkMinutes;
        restaurant.PriceLevel = draft.PriceLevel;
        restaurant.Note = draft.Note;
        restaurant.UpdatedAt = now;

        // 불변 조건: Active ⟺ CategoryId != null
        restaurant.Status = restaurant.CategoryId is null
            ? RestaurantStatus.Pending
            : RestaurantStatus.Active;

        await db.SaveChangesAsync(ct);
        return restaurant;
    }

    public Task MarkMealPromptPostedAsync(DateOnly date, DateTimeOffset at, CancellationToken ct) =>
        MarkDayAsync(date, day => day.MealPromptPostedAt = at, ct);

    public Task MarkPendingReminderSentAsync(DateOnly date, DateTimeOffset at, CancellationToken ct) =>
        MarkDayAsync(date, day => day.PendingReminderSentAt = at, ct);

    async Task MarkDayAsync(DateOnly date, Action<ChannelDay> update, CancellationToken ct)
    {
        var day = await db.ChannelDays
            .SingleOrDefaultAsync(d => d.ChannelId == channelId && d.Date == date, ct);

        if (day is null)
        {
            day = new ChannelDay { ChannelId = channelId, Date = date };
            db.ChannelDays.Add(day);
        }

        update(day);
        await db.SaveChangesAsync(ct);
    }
}
