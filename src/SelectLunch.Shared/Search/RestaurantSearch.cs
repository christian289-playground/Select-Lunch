using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Search;

/// <summary>
/// 외부 선택(external_select) 드롭다운에 내려보낼 후보 한 줄.
/// <see cref="DisplayOrder"/>는 검색어가 없을 때의 기본 순서다 — 투표 후보는
/// <c>PollCandidate.DisplayOrder</c>를 그대로 쓰고, 그런 개념이 없는 쪽은 전부 0을 주면
/// 이름순으로 떨어진다.
/// </summary>
public sealed record RestaurantOption(
    long RestaurantId,
    string Name,
    string CategoryName,
    string? Address,
    int DisplayOrder);

/// <summary>
/// 식당 부분 일치 검색. 슬랙 기본 <c>static_select</c>는 클라이언트가 **단어 앞부분만**
/// 매칭해서 "옛날경성순대국"을 "순대국"으로 찾을 수 없었다. 외부 선택으로 바꿔
/// 이 함수가 직접 거른다.
///
/// 순수 함수다 — DB도 시계도 보지 않고, 같은 입력에 항상 같은 순서를 돌려준다.
/// 매 글자마다 호출되는 경로(block_suggestion은 3초 제한)라 무거운 일을 하지 않는다.
/// </summary>
public static class RestaurantSearch
{
    /// <summary>
    /// 한 번에 돌려줄 수 있는 옵션 수의 슬랙 상한. 외부 선택은 동적 로드라
    /// 전체 식당 수에는 제한이 없지만 **한 응답**은 여전히 100개까지다.
    /// </summary>
    public const int MaxOptions = 100;

    /// <summary>슬랙 옵션 텍스트 길이 상한. 넘기면 블록 전체가 거부된다.</summary>
    public const int MaxLabelLength = 75;

    /// <summary>
    /// <paramref name="query"/>를 포함하는 후보를 점수순으로 돌려준다.
    /// 검색어와 대상 모두 <see cref="Restaurant.Normalize"/>를 통과시킨다
    /// (NFC → 공백 제거 → 소문자). 검색어가 비면 전체가 기본 순서로 나온다.
    ///
    /// 일치 대상은 이름 · **카테고리 이름** · 주소다. 카테고리가 핵심인데,
    /// 사람들은 브랜드가 아니라 "순대국"·"감자탕"처럼 먹은 음식으로 찾기 때문이다.
    /// </summary>
    public static IReadOnlyList<RestaurantOption> Filter(
        IEnumerable<RestaurantOption> pool,
        string? query,
        int limit = MaxOptions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var needle = Restaurant.Normalize(query ?? string.Empty);

        return
        [
            .. pool
                .Select(option => (Option: option, Rank: RankOf(option, needle)))
                .Where(x => x.Rank < NoMatch)
                // 랜덤 금지 — 동점을 유일 키(RestaurantId)까지 끌고 가 순서를 고정한다.
                // 같은 글자를 다시 쳤을 때 목록이 흔들리면 고르는 중에 항목이 움직인다.
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Option.DisplayOrder)
                .ThenBy(x => x.Option.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Option.RestaurantId)
                .Take(limit)
                .Select(x => x.Option),
        ];
    }

    /// <summary>드롭다운에 보일 한 줄. 카테고리를 붙여 같은 이름의 다른 가게를 구분한다.</summary>
    public static string Label(RestaurantOption option) =>
        Truncate($"{option.Name} · {option.CategoryName}", MaxLabelLength);

    /// <summary>
    /// 슬랙은 75자를 넘는 옵션 텍스트를 받지 않는다 — 자르지 않으면 메시지가 아니라
    /// 제안 응답 전체가 조용히 실패해 드롭다운이 비어 보인다.
    /// </summary>
    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");

    /// <summary>일치하지 않음. 정렬 등급보다 큰 값이라 비교 하나로 걸러진다.</summary>
    const int NoMatch = 9;

    /// <summary>
    /// 0 = 이름 앞부분, 1 = 이름 포함, 2 = 카테고리·주소만 포함, <see cref="NoMatch"/> = 불일치.
    /// 검색어가 비면 모든 이름이 "앞부분 일치"라 전부 0이 되고, 결과는 기본 순서가 된다.
    /// </summary>
    static int RankOf(RestaurantOption option, string needle)
    {
        var name = Restaurant.Normalize(option.Name);

        if (name.StartsWith(needle, StringComparison.Ordinal))
            return 0;

        if (name.Contains(needle, StringComparison.Ordinal))
            return 1;

        var category = Restaurant.Normalize(option.CategoryName);
        if (category.Contains(needle, StringComparison.Ordinal))
            return 2;

        // 여기까지 왔으면 needle은 비어 있지 않다(빈 검색어는 위에서 0으로 끝난다).
        // 그래서 주소가 없는 식당이 빈 문자열로 전부 일치하는 사고가 나지 않는다.
        var address = Restaurant.Normalize(option.Address ?? string.Empty);
        return address.Contains(needle, StringComparison.Ordinal) ? 2 : NoMatch;
    }
}
