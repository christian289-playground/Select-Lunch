namespace SelectLunch.Shared.Entities;

public sealed class Category
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// 자유 입력 카테고리의 오타 분열("순대국"/"순대 국")을 막는 정규화 이름. UNIQUE.
    /// <see cref="Restaurant.Normalize"/>와 같은 방식이다.
    /// </summary>
    public required string NormalizedName { get; set; }

    /// <summary>시드로 들어간 기본 카테고리인지. 사용자 추가분과 구분한다.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public static string Normalize(string name) => Restaurant.Normalize(name);

    public ICollection<Restaurant> Restaurants { get; set; } = [];
}
