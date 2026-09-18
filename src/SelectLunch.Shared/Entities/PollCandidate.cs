namespace SelectLunch.Shared.Entities;

/// <summary>
/// 투표 시점의 후보 스냅샷. 식당이 나중에 수정·보관되어도
/// 과거 투표의 해석이 깨지지 않게 한다.
/// </summary>
public sealed class PollCandidate
{
    public long PollId { get; set; }

    public LunchPoll? Poll { get; set; }

    public long RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public int DisplayOrder { get; set; }
}
