using System.Globalization;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// 스펙 §8의 action_id 규약. 슬랙은 문자열만 돌려주므로
/// 생성과 해석을 한곳에 모아 오탈자를 막는다.
/// </summary>
public static class ActionIds
{
    const string DateFormat = "yyyyMMdd";

    public static string Vote(long pollId, long restaurantId) => $"vote:{pollId}:{restaurantId}";

    public static string VoteSelect(long pollId) => $"vote_select:{pollId}";

    public static string Meal(DateOnly date, long restaurantId) =>
        $"meal:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}:{restaurantId}";

    public static string MealNew(DateOnly date) =>
        $"meal_new:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}";

    public static string RestaurantFill(long restaurantId) => $"restaurant_fill:{restaurantId}";

    /// <summary>"나 오늘 따로 먹어요" 버튼. 투표가 아니라 기권 — 알고리즘에는 영향이 없다.</summary>
    public static string Abstain(long pollId) => $"abstain:{pollId}";

    public static bool TryParseVote(string actionId, out long pollId, out long restaurantId) =>
        TryParseTwoLongs(actionId, "vote", out pollId, out restaurantId);

    public static bool TryParseVoteSelect(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "vote_select", out pollId);

    public static bool TryParseAbstain(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "abstain", out pollId);

    public static bool TryParseRestaurantFill(string actionId, out long restaurantId) =>
        TryParseOneLong(actionId, "restaurant_fill", out restaurantId);

    public static bool TryParseMeal(string actionId, out DateOnly date, out long restaurantId)
    {
        date = default;
        restaurantId = 0;

        var parts = Split(actionId, "meal", 3);
        return parts is not null
            && TryDate(parts[1], out date)
            && long.TryParse(parts[2], CultureInfo.InvariantCulture, out restaurantId);
    }

    public static bool TryParseMealNew(string actionId, out DateOnly date)
    {
        date = default;

        var parts = Split(actionId, "meal_new", 2);
        return parts is not null && TryDate(parts[1], out date);
    }

    static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    static string[]? Split(string actionId, string prefix, int expectedParts)
    {
        var parts = actionId.Split(':');
        return parts.Length == expectedParts && parts[0] == prefix ? parts : null;
    }

    static bool TryParseOneLong(string actionId, string prefix, out long value)
    {
        value = 0;
        var parts = Split(actionId, prefix, 2);
        return parts is not null && long.TryParse(parts[1], CultureInfo.InvariantCulture, out value);
    }

    static bool TryParseTwoLongs(string actionId, string prefix, out long first, out long second)
    {
        first = 0;
        second = 0;
        var parts = Split(actionId, prefix, 3);
        return parts is not null
            && long.TryParse(parts[1], CultureInfo.InvariantCulture, out first)
            && long.TryParse(parts[2], CultureInfo.InvariantCulture, out second);
    }
}
