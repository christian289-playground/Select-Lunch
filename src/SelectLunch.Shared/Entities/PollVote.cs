namespace SelectLunch.Shared.Entities;

public sealed class PollVote
{
    public long Id { get; set; }

    public long PollId { get; set; }

    public LunchPoll? Poll { get; set; }

    /// <summary>1인 1표. (PollId, SlackUserId)에 UNIQUE가 걸려 변경만 가능하다.</summary>
    public required string SlackUserId { get; set; }

    public long RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public DateTimeOffset VotedAt { get; set; }
}
