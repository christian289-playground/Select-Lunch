namespace SelectLunch.Shared.Recommendation;

/// <summary>한 카테고리의 식사 이력 집계. 조회 계층이 만들어 넘긴다.</summary>
public sealed record CategoryStat(
    long CategoryId,
    string CategoryName,
    DateOnly? LastEatenOn,
    int Count7d,
    int Count30d);

/// <summary>점수가 매겨진 카테고리. <see cref="LastEatenOn"/>은 동점 처리에 쓴다.</summary>
public sealed record CategoryScore(
    long CategoryId,
    string CategoryName,
    DateOnly? LastEatenOn,
    int DaysSince,
    int Count7d,
    int Count30d,
    int Score);

/// <summary>추천 후보가 될 수 있는 식당(Status가 Active인 것만).</summary>
public sealed record RestaurantInfo(
    long RestaurantId,
    string Name,
    long CategoryId,
    string CategoryName,
    DateOnly? LastEatenOn,
    DateTimeOffset CreatedAt);

public sealed record RestaurantPick(
    long RestaurantId,
    string Name,
    DateOnly? LastEatenOn);

/// <summary>추천 결과. <see cref="Others"/>는 점수 내림차순 경쟁 카테고리다.</summary>
public sealed record Recommendation(
    RestaurantPick Pick,
    CategoryScore Winner,
    IReadOnlyList<CategoryScore> Others);
