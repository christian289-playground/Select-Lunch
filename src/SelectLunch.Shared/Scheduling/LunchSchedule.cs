using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Scheduling;

/// <summary>
/// 스펙 §9 스케줄 판정. 시계를 인자로 받는 순수 함수이며 DB도 Slack도 모른다.
/// </summary>
public static class LunchSchedule
{
    public static bool IsBusinessDay(DateOnly date, LunchOptions options)
    {
        if (options.WeekdaysOnly && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        return !options.Holidays.Contains(date);
    }
}
