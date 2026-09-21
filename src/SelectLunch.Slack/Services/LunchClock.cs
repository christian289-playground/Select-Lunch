namespace SelectLunch.Slack.Services;

/// <summary>
/// UTC 현재 시각을 설정된 IANA 타임존으로 변환해 "오늘"을 정의한다.
/// 슬래시 커맨드와 스케줄러(Task 17)가 같은 정의를 공유해야 자정~09:00 KST
/// 구간에서 UTC 날짜와 KST 날짜가 하루 어긋나는 문제를 양쪽 다 피할 수 있다.
/// </summary>
public static class LunchClock
{
    public static DateTimeOffset NowIn(string timeZoneId) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

    public static DateOnly TodayIn(string timeZoneId) => DateOnly.FromDateTime(NowIn(timeZoneId).DateTime);
}
