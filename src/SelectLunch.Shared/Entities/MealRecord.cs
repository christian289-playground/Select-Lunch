namespace SelectLunch.Shared.Entities;

/// <summary>
/// 채널·날짜·식당 단위 방문 1건. 같은 날 여러 식당을 기록할 수 있고, 각 행이 해당 식당의 그날 방문 1회다.
/// 당일 정정은 그날의 행 전체를 새 선택으로 교체한다.
/// </summary>
public sealed class MealRecord
{
    public long Id { get; set; }

    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    public long RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public required string RecordedBySlackUserId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public MealSource Source { get; set; }
}
