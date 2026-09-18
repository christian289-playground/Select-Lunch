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

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>공백을 지우고 소문자로 바꿔 "서브 웨이"와 "서브웨이"를 같게 본다.</summary>
    public static string Normalize(string name) =>
        string.Concat(name.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
}
