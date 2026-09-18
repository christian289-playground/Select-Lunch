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

    static DateTimeOffset At(DateOnly date, TimeOnly time, TimeSpan offset) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, offset);
}
