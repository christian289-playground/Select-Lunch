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

    [Fact]
    public void 경과일_상한에_정확히_도달한_경우를_올바르게_계산한다()
    {
        // 정확히 30일 전 = DaysSinceCap 경계값 테스트
        // Today = 2026-09-18, 30일 전 = 2026-08-19
        CategoryStat[] stats = [new(1, "한식", new DateOnly(2026, 8, 19), 0, 0)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(30, scores[0].DaysSince);
        Assert.Equal(30, scores[0].Score);
    }

    [Fact]
    public void 미래_날짜는_경과일을_0으로_고정한다()
    {
        // LastEatenOn이 today보다 미래면 DaysSince = 0 (음수 방지)
        CategoryStat[] stats = [new(1, "한식", new DateOnly(2026, 9, 20), 0, 0)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(0, scores[0].DaysSince);
        Assert.Equal(0, scores[0].Score);
    }

    static RestaurantInfo R(
        long id, string name, long categoryId, string categoryName,
        DateOnly? lastEaten, int createdDay = 1) =>
        new(id, name, categoryId, categoryName, lastEaten,
            new DateTimeOffset(2026, 1, createdDay, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void 점수가_가장_높은_카테고리의_식당을_추천한다()
    {
        CategoryStat[] stats =
        [
            new(1, "한식", new DateOnly(2026, 9, 15), 2, 5),   // −8
            new(2, "일식", new DateOnly(2026, 9, 4), 0, 1),    // 13
        ];
        RestaurantInfo[] restaurants =
        [
            R(10, "김밥천국", 1, "한식", new DateOnly(2026, 9, 15)),
            R(20, "스시로", 2, "일식", new DateOnly(2026, 8, 21)),
            R(21, "스시노야", 2, "일식", new DateOnly(2026, 9, 4)),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.NotNull(result);
        Assert.Equal("일식", result.Winner.CategoryName);
        Assert.Equal(13, result.Winner.Score);
        Assert.Equal("스시로", result.Pick.Name);          // 일식 중 가장 오래됨
        Assert.Equal("한식", Assert.Single(result.Others).CategoryName);
    }

    [Fact]
    public void 카테고리_동점이면_더_오래_전에_먹은_쪽이_이긴다()
    {
        // 둘 다 D=10, 빈도 0 → 점수 10으로 동점
        CategoryStat[] stats =
        [
            new(1, "한식", new DateOnly(2026, 9, 8), 0, 0),
            new(2, "일식", new DateOnly(2026, 9, 8), 0, 0),
        ];
        // 마지막 식사일까지 같으면 이름 사전순 → "일식" < "한식" (ordinal)
        RestaurantInfo[] restaurants = [R(10, "김밥천국", 1, "한식", null), R(20, "스시로", 2, "일식", null)];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("일식", result!.Winner.CategoryName);
    }

    [Fact]
    public void 카테고리_안에서는_마지막_방문이_가장_오래된_식당을_고른다()
    {
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 4), 0, 1)];
        RestaurantInfo[] restaurants =
        [
            R(20, "스시노야", 2, "일식", new DateOnly(2026, 9, 4)),
            R(21, "스시로", 2, "일식", new DateOnly(2026, 8, 21)),
            R(22, "오마카세김", 2, "일식", new DateOnly(2026, 8, 30)),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("스시로", result!.Pick.Name);
    }

    [Fact]
    public void 한_번도_안_간_식당이_방문한_식당보다_우선한다()
    {
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 4), 0, 1)];
        RestaurantInfo[] restaurants =
        [
            R(20, "스시로", 2, "일식", new DateOnly(2026, 8, 21)),
            R(21, "신규스시", 2, "일식", null),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("신규스시", result!.Pick.Name);
    }

    [Fact]
    public void 식당_동점이면_먼저_등록된_쪽을_고른다()
    {
        CategoryStat[] stats = [new(2, "일식", null, 0, 0)];
        RestaurantInfo[] restaurants =
        [
            R(21, "나중등록", 2, "일식", null, createdDay: 5),
            R(20, "먼저등록", 2, "일식", null, createdDay: 2),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("먼저등록", result!.Pick.Name);
    }

    [Fact]
    public void Active_식당이_없는_카테고리는_후보에서_빠진다()
    {
        // 중식 점수가 가장 높지만 Active 식당이 하나도 없다
        CategoryStat[] stats =
        [
            new(3, "중식", null, 0, 0),                       // 30점
            new(2, "일식", new DateOnly(2026, 9, 4), 0, 1),   // 13점
        ];
        RestaurantInfo[] restaurants = [R(20, "스시로", 2, "일식", new DateOnly(2026, 8, 21))];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("일식", result!.Winner.CategoryName);
        Assert.Empty(result.Others);
    }

    [Fact]
    public void 이력이_없는_카테고리의_식당도_추천_대상이_된다()
    {
        // stats에 없지만 Active 식당은 있는 카테고리 — 미방문으로 간주해야 한다
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 17), 1, 1)];
        RestaurantInfo[] restaurants =
        [
            R(20, "스시로", 2, "일식", new DateOnly(2026, 9, 17)),
            R(30, "왕서방", 3, "중식", null),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("왕서방", result!.Pick.Name);
        Assert.Equal(30, result.Winner.Score);
    }

    [Fact]
    public void 추천할_식당이_없으면_null을_돌려준다()
    {
        var result = RecommendationEngine.Recommend(Today, [], [], Options);

        Assert.Null(result);
    }

    [Fact]
    public void 같은_입력이면_항상_같은_결과가_나온다()
    {
        CategoryStat[] stats =
        [
            new(1, "한식", new DateOnly(2026, 9, 8), 0, 0),
            new(2, "일식", new DateOnly(2026, 9, 8), 0, 0),
            new(3, "중식", new DateOnly(2026, 9, 8), 0, 0),
        ];
        RestaurantInfo[] restaurants =
        [
            R(10, "가게A", 1, "한식", null), R(20, "가게B", 2, "일식", null), R(30, "가게C", 3, "중식", null),
        ];

        var first = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        for (var i = 0; i < 20; i++)
        {
            var again = RecommendationEngine.Recommend(Today, stats, restaurants, Options);
            Assert.Equal(first!.Pick.RestaurantId, again!.Pick.RestaurantId);
        }
    }
}
