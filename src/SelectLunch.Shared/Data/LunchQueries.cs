using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Scheduling;
using SelectLunch.Shared.Search;

namespace SelectLunch.Shared.Data;

/// <summary>순수 함수들이 먹을 입력을 DB에서 만들어 주는 조회 모음.</summary>
public static class LunchQueries
{
    /// <summary>
    /// 식사 기록 드롭다운의 검색 모집단. Active 식당 전부다.
    ///
    /// <see cref="GetActiveRestaurantsAsync"/>와 달리 식사 이력을 집계하지 않는다 —
    /// 이 조회는 사용자가 **글자를 칠 때마다** 불리고 슬랙이 3초 안에 응답을 요구하므로,
    /// 쓰지도 않는 MealRecords 전체 GroupBy를 매번 돌릴 이유가 없다.
    /// </summary>
    public static async Task<List<RestaurantOption>> GetActiveRestaurantOptionsAsync(
        this LunchDbContext db,
        CancellationToken ct) =>
        await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Active && r.CategoryId != null)
            // 표시 순서 개념이 없는 쪽이라 DisplayOrder는 전부 0 — 이름순으로 떨어진다.
            .Select(r => new RestaurantOption(
                r.Id, r.Name, r.Category!.Name, r.Address, 0))
            .ToListAsync(ct);

    /// <summary>
    /// 투표 후보 드롭다운의 검색 모집단. 반드시 <see cref="Entities.PollCandidate"/>
    /// 스냅샷에서 뽑는다 — Active 전체에서 뽑으면 투표가 열린 뒤 등록된 식당이
    /// 제안 목록에 끼어들고, 그걸 고르면 후보에 없는 식당에 표가 들어간다.
    /// </summary>
    public static async Task<List<RestaurantOption>> GetPollCandidateOptionsAsync(
        this LunchDbContext db,
        long pollId,
        CancellationToken ct) =>
        await db.PollCandidates
            .Where(c => c.PollId == pollId)
            .Select(c => new RestaurantOption(
                c.RestaurantId,
                c.Restaurant!.Name,
                c.Restaurant!.Category != null ? c.Restaurant!.Category!.Name : "미분류",
                c.Restaurant!.Address,
                c.DisplayOrder))
            .ToListAsync(ct);

    /// <summary>추천 후보가 되는 Active 식당과 각각의 마지막 방문일.</summary>
    public static async Task<List<RestaurantInfo>> GetActiveRestaurantsAsync(
        this LunchDbContext db,
        CancellationToken ct)
    {
        var lastEaten = await db.MealRecords
            .GroupBy(m => m.RestaurantId)
            .Select(g => new { RestaurantId = g.Key, Last = g.Max(m => m.Date) })
            .ToDictionaryAsync(x => x.RestaurantId, x => x.Last, ct);

        var restaurants = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Active && r.CategoryId != null)
            .Select(r => new
            {
                r.Id, r.Name, CategoryId = r.CategoryId!.Value,
                CategoryName = r.Category!.Name, r.CreatedAt,
            })
            .ToListAsync(ct);

        return [.. restaurants.Select(r => new RestaurantInfo(
            r.Id, r.Name, r.CategoryId, r.CategoryName,
            lastEaten.TryGetValue(r.Id, out var last) ? last : null,
            r.CreatedAt))];
    }

    /// <summary>
    /// 특정 풀이 열릴 때 스냅샷된 후보 목록(<see cref="PollCandidate"/>)을 그대로 읽는다.
    /// 투표가 진행되는 동안 식당이 보관(Archived)되거나 카테고리가 바뀌어도
    /// 투표 메시지·집계는 처음 연 시점의 후보 구성을 그대로 유지해야 한다 —
    /// 여기서 다시 Active 조건을 묻으면 후보가 사라지고 이미 들어온 표까지
    /// 화면에서 증발한다.
    /// </summary>
    public static async Task<List<RestaurantInfo>> GetPollCandidatesAsync(
        this LunchDbContext db,
        long pollId,
        CancellationToken ct)
    {
        var lastEaten = await db.MealRecords
            .GroupBy(m => m.RestaurantId)
            .Select(g => new { RestaurantId = g.Key, Last = g.Max(m => m.Date) })
            .ToDictionaryAsync(x => x.RestaurantId, x => x.Last, ct);

        var candidates = await db.PollCandidates
            .Where(c => c.PollId == pollId)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new
            {
                c.RestaurantId,
                c.Restaurant!.Name,
                CategoryId = c.Restaurant!.CategoryId ?? 0,
                CategoryName = c.Restaurant!.Category != null ? c.Restaurant!.Category!.Name : "미분류",
                c.Restaurant!.CreatedAt,
            })
            .ToListAsync(ct);

        return [.. candidates.Select(c => new RestaurantInfo(
            c.RestaurantId, c.Name, c.CategoryId, c.CategoryName,
            lastEaten.TryGetValue(c.RestaurantId, out var last) ? last : null,
            c.CreatedAt))];
    }

    /// <summary>
    /// Active 식당을 가진 카테고리별 식사 이력 집계.
    /// Pending 식당의 기록은 카테고리가 없으므로 자연히 빠진다.
    /// </summary>
    public static async Task<List<CategoryStat>> GetCategoryStatsAsync(
        this LunchDbContext db,
        DateOnly today,
        CancellationToken ct)
    {
        var from7 = today.AddDays(-6);     // 오늘 포함 7일
        var from30 = today.AddDays(-29);   // 오늘 포함 30일

        var categories = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Active && r.CategoryId != null)
            .Select(r => new { Id = r.CategoryId!.Value, Name = r.Category!.Name })
            .Distinct()
            .ToListAsync(ct);

        var history = await db.MealRecords
            .Where(m => m.Restaurant!.CategoryId != null)
            .Select(m => new { CategoryId = m.Restaurant!.CategoryId!.Value, m.Date })
            .ToListAsync(ct);

        return [.. categories.Select(c =>
        {
            var rows = history.Where(h => h.CategoryId == c.Id).ToList();
            return new CategoryStat(
                c.Id,
                c.Name,
                rows.Count == 0 ? null : rows.Max(h => h.Date),
                rows.Count(h => h.Date >= from7 && h.Date <= today),
                rows.Count(h => h.Date >= from30 && h.Date <= today));
        })];
    }

    /// <summary>스케줄 판정에 필요한 오늘치 상태.</summary>
    public static async Task<TodayState> GetTodayStateAsync(
        this LunchDbContext db,
        string channelId,
        DateOnly today,
        CancellationToken ct)
    {
        var poll = await db.Polls
            .Where(p => p.ChannelId == channelId && p.Date == today)
            .Select(p => new PollSnapshot(p.Id, p.Status, p.ClosesAt, p.MessageTs, p.ResultAnnouncedAt))
            .SingleOrDefaultAsync(ct);

        var day = await db.ChannelDays
            .SingleOrDefaultAsync(d => d.ChannelId == channelId && d.Date == today, ct);

        var hasPending = await db.Restaurants
            .AnyAsync(r => r.Status == RestaurantStatus.Pending, ct);

        return new TodayState(
            today,
            poll,
            day?.MealPromptPostedAt is not null,
            day?.PendingReminderSentAt is null ? null : today,
            hasPending);
    }
}
