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

    /// <summary>미방문은 상한값으로 본다. 미래 날짜가 섞여도 음수가 되지 않게 0에서 자른다.</summary>
    static int DaysSince(DateOnly today, DateOnly? lastEatenOn, int cap) =>
        lastEatenOn is { } last
            ? Math.Clamp(today.DayNumber - last.DayNumber, 0, cap)
            : cap;
}
