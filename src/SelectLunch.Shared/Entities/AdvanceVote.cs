namespace SelectLunch.Shared.Entities;

/// <summary>
/// 채널·날짜·사용자별 사전투표. 유효한 선택은 본투표의 초기표로 반영하며,
/// 취소되거나 본투표 변경으로 무효화된 행도 선택 기록과 변경 횟수를 보존한다.
/// </summary>
public sealed class AdvanceVote
{
    public const int MaxChanges = 3;

    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    public required string SlackUserId { get; set; }

    /// <summary>취소되지 않은 행에서 null이면 기권이다.</summary>
    public long? RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public DateTimeOffset VotedAt { get; set; }

    public int ChangeCount { get; set; }

    public bool IsCancelled { get; set; }

    /// <summary>본투표에서 실제 선택이 바뀌어 더 이상 유효하지 않은 사전표. 변경 횟수를 소모하지 않는다.</summary>
    public bool IsSupersededByMainVote { get; set; }
}
