using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;

namespace SelectLunch.Shared.Tests;

public class RecommendationEngineTests
{
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly RecommendationOptions Options = new();

    [Fact]
    public void 경과일에서_최근_빈도를_차감해_점수를_낸다()
    {
        // 일식: 9/4에 먹음 → D=14, 최근 7일 0회, 최근 30일 1회
        //       14 − (3×0 + 1×1) = 13
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 4), 0, 1)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        var 일식 = Assert.Single(scores);
        Assert.Equal(14, 일식.DaysSince);
        Assert.Equal(13, 일식.Score);
    }

    [Fact]
    public void 최근에_자주_먹은_카테고리는_점수가_낮다()
    {
        // 한식: 9/15에 먹음 → D=3, 최근 7일 2회, 최근 30일 5회
        //       3 − (3×2 + 1×5) = −8
        CategoryStat[] stats = [new(1, "한식", new DateOnly(2026, 9, 15), 2, 5)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(-8, scores[0].Score);
    }

    [Fact]
    public void 한_번도_먹지_않은_카테고리는_경과일_상한을_쓴다()
    {
        CategoryStat[] stats = [new(9, "태국식", null, 0, 0)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(Options.DaysSinceCap, scores[0].DaysSince);
        Assert.Equal(30, scores[0].Score);
    }

    [Fact]
    public void 아주_오래_전에_먹었어도_경과일은_상한을_넘지_않는다()
    {
        CategoryStat[] stats = [new(5, "분식", new DateOnly(2025, 1, 1), 0, 0)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(30, scores[0].DaysSince);
    }

    [Fact]
    public void 가중치를_바꾸면_점수가_따라_바뀐다()
    {
        CategoryStat[] stats = [new(1, "한식", new DateOnly(2026, 9, 15), 2, 5)];
        var options = new RecommendationOptions { Weight7d = 1, Weight30d = 0 };

        var scores = RecommendationEngine.ScoreCategories(Today, stats, options);

        Assert.Equal(1, scores[0].Score);   // 3 − (1×2 + 0×5)
    }
}
