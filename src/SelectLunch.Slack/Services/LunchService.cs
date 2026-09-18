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
    public async Task<LunchPoll> OpenPollAsync(
        DateOnly date, DateTimeOffset opensAt, DateTimeOffset closesAt, CancellationToken ct)
    {
        var poll = new LunchPoll
        {
            ChannelId = channelId, Date = date,
            OpensAt = opensAt, ClosesAt = closesAt, Status = PollStatus.Open,
        };
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

    public async Task<IReadOnlyList<VoteTally>> GetTalliesAsync(long pollId, CancellationToken ct) =>
        await db.PollVotes
            .Where(v => v.PollId == pollId)
            .GroupBy(v => new { v.RestaurantId, v.Restaurant!.Name })
            .Select(g => new VoteTally(g.Key.RestaurantId, g.Key.Name, g.Count()))
            .ToListAsync(ct);

    /// <summary>
    /// 마감하고 결과를 낸다. 투표 동점은 추천 점수가 높은 쪽으로 푼다 —
    /// 랜덤을 쓰지 않으면서 결정적으로 해소하는 방법이다.
    /// </summary>
    public async Task<PollOutcome> ClosePollAsync(
        long pollId, DateOnly today, RecommendationOptions options, CancellationToken ct)
    {
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        var tallies = await GetTalliesAsync(pollId, ct);

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

        return new PollOutcome(winner, tallies, recommendation);
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

        return restaurants.ToDictionary(
            r => r.RestaurantId,
            r => byCategory.GetValueOrDefault(r.CategoryId, 0));
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
        restaurant.CategoryId = draft.CategoryId ?? restaurant.CategoryId;
        restaurant.WalkMinutes = draft.WalkMinutes ?? restaurant.WalkMinutes;
        restaurant.PriceLevel = draft.PriceLevel ?? restaurant.PriceLevel;
        restaurant.Note = draft.Note ?? restaurant.Note;
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
