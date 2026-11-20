namespace SelectLunch.Shared.Entities;

/// <summary>행이 있는 채널만 서버 기본값 대신 지정한 식당 한 곳당 인원을 사용한다.</summary>
public sealed class ChannelMealSize
{
    public required string ChannelId { get; set; }
    public int ParticipantsPerRestaurant { get; set; }
}
