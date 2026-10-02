using SelectLunch.Shared.Search;

namespace SelectLunch.Shared.Tests;

/// <summary>
/// 이 태스크의 출발점이 된 실제 보고: 슬랙 기본 <c>static_select</c>는 **단어 앞부분만**
/// 매칭해서 "옛날경성순대국 광교중앙역점"을 "순대국"으로 찾을 수 없었다.
/// 사람들은 브랜드가 아니라 먹은 음식("순대국"·"감자탕")으로 찾는다.
/// </summary>
public class RestaurantSearchTests
{
    static RestaurantOption Option(
        long id, string name, string category = "한식", string? address = null, int order = 0) =>
        new(id, name, category, address, order);

    static readonly RestaurantOption 순대국 = Option(1, "옛날경성순대국 광교중앙역점", "순대국", "수원시 영통구 광교중앙로 123");
    static readonly RestaurantOption 감자탕 = Option(2, "뼈대있는집", "감자탕", "성남시 분당구 판교역로 7");
    static readonly RestaurantOption 초밥 = Option(3, "스시로", "일식", "수원시 영통구 광교로 99");

    static readonly RestaurantOption[] Pool = [순대국, 감자탕, 초밥];

    static long[] Ids(IEnumerable<RestaurantOption> options) => [.. options.Select(o => o.RestaurantId)];

    [Fact]
    public void 이름_중간에_있는_말로도_찾힌다()
    {
        // 이 태스크가 존재하는 이유 그 자체. 앞부분만 매칭하는 구현은 빈 목록을 준다.
        var result = RestaurantSearch.Filter(Pool, "순대국");

        Assert.Equal([순대국.RestaurantId], Ids(result));
    }

    [Fact]
    public void 카테고리_이름으로_찾힌다()
    {
        // 이번 개선의 핵심. 이름에 "감자탕"이 없는 가게를 "감자탕"으로 찾을 수 있어야 한다.
        var result = RestaurantSearch.Filter(Pool, "감자탕");

        Assert.Equal([감자탕.RestaurantId], Ids(result));
        Assert.DoesNotContain("감자탕", 감자탕.Name);
    }

    [Fact]
    public void 주소로도_찾힌다()
    {
        var result = RestaurantSearch.Filter(Pool, "분당");

        Assert.Equal([감자탕.RestaurantId], Ids(result));
    }

    [Fact]
    public void 주소가_없는_식당도_다른_조건으로_찾힌다()
    {
        // 주소 null에 "".Contains(needle)이 true가 되는 구현이면 전부 일치해 버린다.
        RestaurantOption[] pool = [Option(10, "김밥천국", "분식", address: null)];

        Assert.Single(RestaurantSearch.Filter(pool, "분식"));
        Assert.Empty(RestaurantSearch.Filter(pool, "분당"));
    }

    [Fact]
    public void 일치하는_것이_없으면_빈_목록이다()
    {
        // "검색 결과가 없으면 전체를 보여준다" 같은 친절은 고르는 사람을 속인다.
        Assert.Empty(RestaurantSearch.Filter(Pool, "없는가게이름"));
    }

    [Fact]
    public void 검색어가_비면_전체가_기본_순서로_나온다()
    {
        // 드롭다운을 열자마자(아직 아무것도 안 친 상태) 보이는 화면이다.
        RestaurantOption[] pool =
        [
            Option(1, "다식당", order: 2),
            Option(2, "가식당", order: 0),
            Option(3, "나식당", order: 1),
        ];

        Assert.Equal([2, 3, 1], Ids(RestaurantSearch.Filter(pool, "")));
        Assert.Equal([2, 3, 1], Ids(RestaurantSearch.Filter(pool, null)));
        Assert.Equal([2, 3, 1], Ids(RestaurantSearch.Filter(pool, "   ")));
    }

    [Fact]
    public void 공백과_대소문자와_유니코드_조합형을_무시한다()
    {
        // Restaurant.Normalize가 NFC → 공백 제거 → 소문자 순으로 처리한다.
        // 새 정규화를 따로 만들면 여기서 어긋난다.
        RestaurantOption[] pool = [Option(1, "서브 웨이 Station", "양식")];

        Assert.Single(RestaurantSearch.Filter(pool, "서브웨이"));
        Assert.Single(RestaurantSearch.Filter(pool, "서브 웨이"));
        Assert.Single(RestaurantSearch.Filter(pool, "STATION"));

        // 조합형(U+110B U+1167 U+11AB) 한글도 완성형 "연"과 같게 봐야 한다.
        RestaurantOption[] 조합형 = [Option(2, "연남돈까스", "일식")];
        Assert.Single(RestaurantSearch.Filter(조합형, "연남"));
    }

    [Fact]
    public void 이름_앞부분_일치가_이름_포함보다_먼저_나온다()
    {
        RestaurantOption[] pool =
        [
            Option(1, "원조순대국", "한식"),       // 포함
            Option(2, "순대국의전설", "한식"),     // 앞부분
        ];

        Assert.Equal([2, 1], Ids(RestaurantSearch.Filter(pool, "순대국")));
    }

    [Fact]
    public void 이름_포함이_카테고리_주소_일치보다_먼저_나온다()
    {
        RestaurantOption[] pool =
        [
            Option(1, "뼈대있는집", "감자탕"),               // 카테고리만
            Option(2, "역전감자탕", "한식"),                 // 이름 포함
            Option(3, "우리집", "한식", "감자탕로 1"),       // 주소만
        ];

        var result = Ids(RestaurantSearch.Filter(pool, "감자탕"));

        Assert.Equal(2, result[0]);
        // 카테고리와 주소는 같은 등급이라 그 안에서는 기본 순서(이름)로 갈린다.
        Assert.Equal([1L, 3L], result.Skip(1).Order().ToArray());
    }

    [Fact]
    public void 같은_등급_안에서는_표시순서_이름_id_순으로_고정된다()
    {
        // 랜덤 금지. 같은 글자를 다시 쳤을 때 목록이 흔들리면 고르는 중에 항목이 움직인다.
        RestaurantOption[] pool =
        [
            Option(3, "국밥집", order: 1),
            Option(1, "국밥집", order: 1),   // 이름까지 같다 — id가 마지막 결정자
            Option(2, "국밥마을", order: 1),
            Option(4, "국밥천하", order: 0),
        ];

        Assert.Equal([4, 2, 1, 3], Ids(RestaurantSearch.Filter(pool, "국밥")));
    }

    [Fact]
    public void 여러_번_불러도_같은_순서가_나온다()
    {
        var first = Ids(RestaurantSearch.Filter(Pool, "수원"));

        for (var i = 0; i < 5; i++)
            Assert.Equal(first, Ids(RestaurantSearch.Filter(Pool, "수원")));
    }

    [Fact]
    public void 백개를_넘으면_상위_백개만_남는다()
    {
        // 슬랙은 한 응답에 100개까지만 받는다. 넘겨 보내면 제안 응답 전체가 거부돼
        // 드롭다운이 통째로 비어 보인다.
        RestaurantOption[] pool = [.. Enumerable.Range(1, 150).Select(i => Option(i, $"국밥{i:D3}"))];

        var result = RestaurantSearch.Filter(pool, "국밥");

        Assert.Equal(RestaurantSearch.MaxOptions, result.Count);
    }

    [Fact]
    public void 백개를_자를_때_순위가_높은_쪽을_남긴다()
    {
        // 앞에서 100개를 그냥 끊는 구현은 이름 앞부분 일치를 떨어뜨리고
        // 카테고리만 걸린 식당을 남긴다 — 정렬 뒤에 잘라야 한다.
        RestaurantOption[] weak =
            [.. Enumerable.Range(1, 120).Select(i => Option(i, $"기타{i:D3}", "국밥"))];
        var strong = Option(999, "국밥대장", "한식");

        // 약한 일치(카테고리)를 먼저 넣어 입력 순서가 결과를 좌우하지 않음을 본다.
        var result = RestaurantSearch.Filter([.. weak, strong], "국밥");

        Assert.Equal(RestaurantSearch.MaxOptions, result.Count);
        Assert.Equal(strong.RestaurantId, result[0].RestaurantId);
    }

    [Fact]
    public void 상한은_인자로_줄일_수_있고_음수는_거부한다()
    {
        Assert.Equal(2, RestaurantSearch.Filter(Pool, "", limit: 2).Count);
        Assert.Empty(RestaurantSearch.Filter(Pool, "", limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RestaurantSearch.Filter(Pool, "", limit: -1));
    }

    [Fact]
    public void 라벨은_이름과_카테고리를_함께_보여준다()
    {
        // 평평한 목록이라 카테고리 그룹 머리글이 없다. 라벨에 안 붙이면
        // 같은 이름의 다른 가게를 구분할 단서가 사라진다.
        var label = RestaurantSearch.Label(순대국);

        Assert.Contains("옛날경성순대국 광교중앙역점", label);
        Assert.Contains("순대국", label);
    }

    [Fact]
    public void 라벨은_슬랙_상한인_75자를_넘지_않는다()
    {
        // 넘기면 블록이 거부돼 드롭다운이 통째로 비어 보인다 — 조용한 실패다.
        var long이름 = Option(1, new string('가', 200), new string('나', 50));

        var label = RestaurantSearch.Label(long이름);

        Assert.Equal(RestaurantSearch.MaxLabelLength, label.Length);
        Assert.EndsWith("…", label);
    }

    [Fact]
    public void 짧은_라벨은_자르지_않는다()
    {
        var label = RestaurantSearch.Label(Option(1, "김밥천국", "분식"));

        Assert.Equal("김밥천국 · 분식", label);
    }
}
