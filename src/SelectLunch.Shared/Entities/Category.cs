namespace SelectLunch.Shared.Entities;

public sealed class Category
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>시드로 들어간 기본 카테고리인지. 사용자 추가분과 구분한다.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Restaurant> Restaurants { get; set; } = [];
}
