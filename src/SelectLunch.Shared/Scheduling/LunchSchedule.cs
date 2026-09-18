using SelectLunch.Shared.Entities;
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

    /// <summary>
    /// 지금 실행해야 할 작업을 돌려준다. 호출자는 <paramref name="now"/>를
    /// 설정된 타임존으로 변환해 넘겨야 한다.
    ///
    /// 중요: <paramref name="now"/>는 <see cref="LunchOptions.TimeZone"/>으로 이미 변환되어야 한다.
    /// <see cref="At"/> 메서드는 <paramref name="now"/>의 <see cref="DateTimeOffset.Offset"/>을
    /// 그대로 사용하므로, 정확한 시각 비교를 위해 이미 올바른 오프셋을 가져야 한다.
    /// DST가 없는 지역(한국 등)에서는 오프셋이 상수이지만, DST가 있는 지역으로 이동하려면
    /// <see cref="At"/>을 <see cref="System.TimeZoneInfo"/>로 대체해야 한다.
    /// </summary>
    public static IReadOnlyList<DueAction> GetDueActions(
        DateTimeOffset now,
        TodayState state,
        LunchOptions options)
    {
        if (!IsBusinessDay(state.Date, options))
            return [];

        var actions = new List<DueAction>();
        var grace = TimeSpan.FromMinutes(options.CatchUpGraceMinutes);

        var voteOpenAt = At(state.Date, options.VoteOpenAt, now.Offset);
        if (state.Poll is null && IsDue(now, voteOpenAt, grace))
            actions.Add(new DueAction(DueActionKind.OpenPoll, voteOpenAt));

        // 마감에는 유예를 두지 않는다. 열린 채 남은 투표는 언제든 닫아야 한다.
        if (state.Poll is { Status: PollStatus.Open } poll && now >= poll.ClosesAt)
            actions.Add(new DueAction(DueActionKind.ClosePoll, poll.ClosesAt));

        var mealPromptAt = At(state.Date, options.MealRecordAt, now.Offset);
        if (!state.MealPromptPosted && IsDue(now, mealPromptAt, grace))
            actions.Add(new DueAction(DueActionKind.PostMealPrompt, mealPromptAt));

        if (ShouldRemindPending(state, options))
        {
            var remindAt = At(state.Date, options.PendingReminder.At, now.Offset);
            if (IsDue(now, remindAt, grace))
                actions.Add(new DueAction(DueActionKind.RemindPending, remindAt));
        }

        return actions;
    }

    static bool ShouldRemindPending(TodayState state, LunchOptions options) =>
        options.PendingReminder.Enabled
        && state.HasPendingRestaurants
        && state.Date.DayOfWeek == options.PendingReminder.DayOfWeek
        && state.LastPendingReminderOn != state.Date;

    /// <summary>예정 시각을 지났고, 유예 시간 안에 있으면 실행 대상이다.</summary>
    static bool IsDue(DateTimeOffset now, DateTimeOffset scheduledFor, TimeSpan grace) =>
        now >= scheduledFor && now - scheduledFor <= grace;

    /// <summary>
    /// <paramref name="date"/>와 <paramref name="time"/>으로 지정된 날짜-시간을,
    /// <paramref name="offset"/>을 포함한 <see cref="DateTimeOffset"/>로 변환한다.
    ///
    /// 이 메서드는 호출자가 이미 <see cref="LunchOptions.TimeZone"/>으로 변환한
    /// <paramref name="offset"/>을 그대로 사용한다. DST 전환이 없는 지역(한국 등)에서는
    /// <paramref name="offset"/>이 상수이므로 안전하다. DST가 있는 지역으로 이동하려면
    /// <see cref="System.TimeZoneInfo.ConvertTime(System.DateTime, System.TimeZoneInfo)"/>를
    /// 사용하여 <paramref name="offset"/>을 동적으로 계산해야 한다.
    /// </summary>
    static DateTimeOffset At(DateOnly date, TimeOnly time, TimeSpan offset) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, offset);
}
