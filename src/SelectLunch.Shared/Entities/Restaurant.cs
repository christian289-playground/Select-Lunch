using System.Text;

namespace SelectLunch.Shared.Entities;

public sealed class Restaurant
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>중복 등록을 막기 위한 정규화 이름. UNIQUE 제약이 걸린다.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>null이면 반드시 <see cref="RestaurantStatus.Pending"/>이다.</summary>
    public long? CategoryId { get; set; }

    public Category? Category { get; set; }

    public RestaurantStatus Status { get; set; } = RestaurantStatus.Pending;

    public int? WalkMinutes { get; set; }

    /// <summary>가격대 1~4단계.</summary>
    public int? PriceLevel { get; set; }

    public string? Note { get; set; }

    public required string CreatedBySlackUserId { get; set; }

    /// <summary>주소. 표시 전용이다.</summary>
    public string? Address { get; set; }

    /// <summary>등록자 표시 이름. 슬랙 멘션이 아닌 DB 조회·목록 출력용이다.</summary>
    public string? CreatedByDisplayName { get; set; }

    /// <summary>대기 수준. 모르면 null이며 추천 점수에는 반영하지 않는다.</summary>
    public WaitLevel? WaitLevel { get; set; }

    /// <summary>오늘의 메뉴를 가져올 채널 API URL. 값이 있는 식당만 수집 대상이다.</summary>
    public string? MenuSourceUrl { get; set; }

    /// <summary>수집된 오늘의 메뉴 이미지 URL. <see cref="TodayMenuDate"/>가 오늘일 때만 유효하다.</summary>
    public string? TodayMenuImageUrl { get; set; }

    /// <summary>메뉴 이미지가 올라온 날짜(수집 기준일).</summary>
    public DateOnly? TodayMenuDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// 공백을 지우고 소문자로 바꿔 "서브 웨이"와 "서브웨이"를 같게 본다.
    /// 먼저 유니코드 정규화(NFC)를 적용해, 조합형/완성형처럼 바이트가 다른
    /// 같은 모양의 한글도 같은 식당으로 본다.
    /// </summary>
    public static string Normalize(string name) =>
        string.Concat(name.Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c)))
            .ToLowerInvariant();
}
