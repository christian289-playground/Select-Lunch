namespace SelectLunch.Shared.Entities;

public sealed class LunchPoll
{
    public long Id { get; set; }

    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>발송된 슬랙 메시지의 ts. 집계 갱신에 쓴다.</summary>
    public string? MessageTs { get; set; }

    public DateTimeOffset OpensAt { get; set; }

    public DateTimeOffset ClosesAt { get; set; }

    public PollStatus Status { get; set; } = PollStatus.Open;

    public long? WinnerRestaurantId { get; set; }

    public long? RecommendedRestaurantId { get; set; }

    /// <summary>추천 근거(카테고리별 점수 스냅샷)의 JSON. 나중에 재현할 수 있게 남긴다.</summary>
    public string? RationaleJson { get; set; }

    public ICollection<PollCandidate> Candidates { get; set; } = [];

    public ICollection<PollVote> Votes { get; set; } = [];
}
