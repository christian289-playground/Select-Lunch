using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Recommendation;

/// <summary>
/// 스펙 §7 추천 알고리즘. 전 구간이 결정적이며 랜덤을 쓰지 않는다.
/// DB도 Slack도 호출하지 않는 순수 함수의 모음이다.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>score = D − (W7 × N7 + W30 × N30)</summary>
    public static IReadOnlyList<CategoryScore> ScoreCategories(
        DateOnly today,
        IReadOnlyList<CategoryStat> stats,
        RecommendationOptions options)
    {
        var scores = new List<CategoryScore>(stats.Count);

        foreach (var stat in stats)
        {
            var daysSince = DaysSince(today, stat.LastEatenOn, options.DaysSinceCap);
            var penalty = options.Weight7d * stat.Count7d + options.Weight30d * stat.Count30d;

            scores.Add(new CategoryScore(
                stat.CategoryId,
                stat.CategoryName,
                stat.LastEatenOn,
                daysSince,
                stat.Count7d,
                stat.Count30d,
                daysSince - penalty));
        }

        return scores;
    }

    /// <summary>
    /// 최종 추천을 낸다. 추천 가능한 식당이 하나도 없으면 null.
    /// </summary>
    /// <param name="restaurants">Status가 Active인 식당만 넘긴다.</param>
    public static Recommendation? Recommend(
        DateOnly today,
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        RecommendationOptions options)
    {
        if (restaurants.Count == 0)
            return null;

        var ranked = RankCategories(today, stats, restaurants, options);
        if (ranked.Count == 0)
            return null;

        var winner = ranked[0];
        var pick = restaurants
            .Where(r => r.CategoryId == winner.CategoryId)
            .OrderBy(r => r.LastEatenOn ?? DateOnly.MinValue)   // 미방문 최우선
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.RestaurantId)
            .First();

        return new Recommendation(
            new RestaurantPick(pick.RestaurantId, pick.Name, pick.LastEatenOn),
            winner,
            [.. ranked.Skip(1)]);
    }

    /// <summary>
    /// Active 식당을 가진 카테고리만 점수 내림차순으로 정렬한다.
    /// 이력이 없는 카테고리는 미방문 통계를 만들어 채운다.
    /// </summary>
    static List<CategoryScore> RankCategories(
        DateOnly today,
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        RecommendationOptions options)
    {
        var byId = stats.ToDictionary(s => s.CategoryId);

        var eligible = restaurants
            .Select(r => r.CategoryId)
            .Distinct()
            .Select(id => byId.TryGetValue(id, out var stat)
                ? stat
                : new CategoryStat(id, CategoryNameOf(restaurants, id), null, 0, 0))
            .ToList();

        return [.. RecommendationEngine
            .ScoreCategories(today, eligible, options)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.LastEatenOn ?? DateOnly.MinValue)
            .ThenBy(s => s.CategoryName, StringComparer.Ordinal)];
    }

    /// <summary>이력이 아직 없는 카테고리의 이름은 소속 식당에서 가져온다.</summary>
    static string CategoryNameOf(IReadOnlyList<RestaurantInfo> restaurants, long categoryId) =>
        restaurants.First(r => r.CategoryId == categoryId).CategoryName;

    /// <summary>미방문은 상한값으로 본다. 미래 날짜가 섞여도 음수가 되지 않게 0에서 자른다.</summary>
    static int DaysSince(DateOnly today, DateOnly? lastEatenOn, int cap) =>
        lastEatenOn is { } last
            ? Math.Clamp(today.DayNumber - last.DayNumber, 0, cap)
            : cap;
}
