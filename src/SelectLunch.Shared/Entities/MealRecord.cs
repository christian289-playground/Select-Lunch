namespace SelectLunch.Shared.Entities;

/// <summary>채널 단위 하루 1건. 나중에 기록한 사람이 덮어쓴다.</summary>
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
