namespace SelectLunch.Shared.Options;

/// <summary>정보가 덜 찬 식당(Pending) 보완 요청 설정.</summary>
public sealed class PendingReminderOptions
{
    public bool Enabled { get; set; } = true;

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Friday;

    public TimeOnly At { get; set; } = new(16, 0);
}
