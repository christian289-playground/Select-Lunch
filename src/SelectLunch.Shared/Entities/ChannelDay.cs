namespace SelectLunch.Shared.Entities;

/// <summary>
/// 채널의 하루치 발송 이력. "이미 보냈는가"를 여기서 판정한다.
/// 투표가 열리지 않은 날에도 기록 요청은 나가야 하므로,
/// 이 상태를 <see cref="LunchPoll"/>에 둘 수 없다 — 그날 Poll이 없을 수 있다.
/// </summary>
public sealed class ChannelDay
{
    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    public DateTimeOffset? MealPromptPostedAt { get; set; }

    public DateTimeOffset? PendingReminderSentAt { get; set; }
}
