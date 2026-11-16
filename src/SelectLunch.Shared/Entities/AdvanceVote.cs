namespace SelectLunch.Shared.Entities;

/// <summary>
/// 정규 투표가 열리기 전에 접수한 사전투표. 그날 <see cref="LunchPoll"/>이 아직 없어
/// <see cref="PollVote"/>에 둘 수 없으므로 (채널, 날짜, 사용자)를 키로 따로 보관한다.
/// 행이 없으면 미선택, <see cref="RestaurantId"/>가 null이면 기권("따로 먹어요")이다.
/// 투표가 열릴 때 일반 표로 옮겨지고 이 행은 지워진다.
/// </summary>
public sealed class AdvanceVote
{
    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    public required string SlackUserId { get; set; }

    /// <summary>null이면 기권 — 투표가 열릴 때 <see cref="PollVote.RestaurantId"/>가 null인 표가 된다.</summary>
    public long? RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public DateTimeOffset VotedAt { get; set; }
}
