namespace SelectLunch.Shared.Options;

/// <summary>스펙 §7 추천 점수 수식의 조정 값.</summary>
public sealed class RecommendationOptions
{
    /// <summary>경과일 상한. 한 번도 먹지 않은 카테고리도 이 값을 쓴다.</summary>
    public int DaysSinceCap { get; set; } = 30;

    /// <summary>최근 7일 식사 1회당 차감치.</summary>
    public int Weight7d { get; set; } = 3;

    /// <summary>최근 30일 식사 1회당 차감치.</summary>
    public int Weight30d { get; set; } = 1;
}
