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

    public const string RestaurantEditSelect = "restaurant_edit_select";

    /// <summary>"나 오늘 따로 먹어요" 버튼. 투표가 아니라 기권 — 알고리즘에는 영향이 없다.</summary>
    public static string Abstain(long pollId) => $"abstain:{pollId}";

    public static bool TryParseVote(string actionId, out long pollId, out long restaurantId) =>
        TryParseTwoLongs(actionId, "vote", out pollId, out restaurantId);

    public static bool TryParseVoteSelect(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "vote_select", out pollId);

    public static bool TryParseAbstain(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "abstain", out pollId);

    /// <summary>
    /// 마감 결과의 "점수 자세히 보기" 버튼과 그 모달 안의 쪽 이동 버튼.
    /// <paramref name="page"/>는 0부터 시작하며 채널 메시지의 버튼은 항상 0쪽이다.
    /// </summary>
    public static string ResultDetails(long pollId, int page = 0) => $"result_details:{pollId}:{page}";

    /// <summary>
    /// 정식 표기(<see cref="ResultDetails"/>가 만드는 모양)만 받는다 — 부호·공백·앞자리 0이 붙은
    /// 값, 0 이하 pollId, 음수 쪽, 범위를 넘는 값은 모두 거절하고 출력값을 0으로 둔다.
    /// </summary>
    public static bool TryParseResultDetails(string actionId, out long pollId, out int page)
    {
        pollId = 0;
        page = 0;

        var parts = Split(actionId, "result_details", 3);
        if (parts is null
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPollId)
            || parsedPollId <= 0
            || parts[1] != parsedPollId.ToString(CultureInfo.InvariantCulture)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPage)
            || parts[2] != parsedPage.ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }

        pollId = parsedPollId;
        page = parsedPage;
        return true;
    }

    public static string VoteCategory(long pollId) => $"vote_category:{pollId}";

    public static string VoteRandom(long pollId) => $"vote_random:{pollId}";

    /// <summary>🤫 비밀 투표 버튼. 누르는 즉시 비밀 표 하나를 뽑아 저장한다.</summary>
    public static string VoteSecret(long pollId) => $"vote_secret:{pollId}";

    /// <summary>
    /// 비밀 투표 접수 안내(본인에게만 보이는 메시지)의 "내가 뽑은 식당 보기" 버튼.
    /// <paramref name="revision"/>은 접수 당시 표의 <c>VotedAt.UtcTicks</c> — 그 뒤 표가 바뀌면 옛 버튼은 무효다.
    /// </summary>
    public static string SecretReveal(long pollId, long revision) => $"secret_reveal:{pollId}:{revision}";

    /// <summary>
    /// 테스트 모드의 확인 버튼. 표가 없으니 접수 때 뽑아 둔 미리보기 식당 id를 그대로 싣는다 —
    /// 눌러도 다시 뽑지 않는다. 반드시 <see cref="Test"/>로 감싸서 쓴다.
    /// </summary>
    public static string SecretPreview(long restaurantId) => $"secret_preview:{restaurantId}";

    public static bool TryParseVoteCategory(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "vote_category", out pollId);

    public static bool TryParseVoteRandom(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "vote_random", out pollId);

    public static bool TryParseVoteSecret(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "vote_secret", out pollId);

    public static bool TryParseSecretReveal(string actionId, out long pollId, out long revision) =>
        TryParseTwoLongs(actionId, "secret_reveal", out pollId, out revision);

    public static bool TryParseSecretPreview(string actionId, out long restaurantId) =>
        TryParseOneLong(actionId, "secret_preview", out restaurantId);

    const string TestPrefix = "test:";

    /// <summary>
    /// 테스트 모드(`/lunch test`) 메시지의 action_id. 기존 action_id 앞에 <c>test:</c>를 붙여
    /// 실제 투표 핸들러가 집계에 반영하지 못하게 갈라 둔다 — 파서는 접두사가 있으면 일치하지 않는다.
    /// </summary>
    public static string Test(string actionId) => TestPrefix + actionId;

    /// <summary>테스트 모드 id면 접두사를 뗀 원래 id를 돌려준다.</summary>
    public static bool TryUnwrapTest(string actionId, out string inner)
    {
        var isTest = actionId.StartsWith(TestPrefix, StringComparison.Ordinal);
        inner = isTest ? actionId[TestPrefix.Length..] : "";
        return isTest;
    }

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
