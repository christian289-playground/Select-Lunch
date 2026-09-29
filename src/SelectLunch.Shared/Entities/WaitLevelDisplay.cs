namespace SelectLunch.Shared.Entities;

/// <summary>대기 수준의 한글 표시. 저장(열거형)과 표시 문자열을 분리한다.</summary>
public static class WaitLevelDisplay
{
    public static string ToDisplay(this WaitLevel level) => level switch
    {
        WaitLevel.None => "대기 없음",
        WaitLevel.Slight => "대기 약간 있음",
        WaitLevel.Moderate => "대기 있음",
        WaitLevel.Severe => "당장 출발하세요 (대기 엄청김)",
        _ => level.ToString(),
    };

    /// <summary>null(모름)이면 null — 값을 지어내지 않는다.</summary>
    public static string? ToDisplay(this WaitLevel? level) => level?.ToDisplay();
}
