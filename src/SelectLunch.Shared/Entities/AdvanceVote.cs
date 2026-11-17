namespace SelectLunch.Shared.Entities;

/// <summary>
/// 채널·날짜·사용자별 사전투표. 행이 없으면 미선택, 취소되지 않은 행에서
/// <see cref="RestaurantId"/>가 null이면 기권이다. 취소된 행도 변경 횟수를 보존한다.
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
}
