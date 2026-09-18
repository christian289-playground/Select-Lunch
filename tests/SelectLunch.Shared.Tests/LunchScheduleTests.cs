using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Shared.Tests;

public class LunchScheduleTests
{
    // 2026-09-18은 금요일, 09-19 토, 09-20 일, 09-21 월
    static readonly DateOnly Friday = new(2026, 9, 18);
    static readonly DateOnly Saturday = new(2026, 9, 19);
    static readonly DateOnly Sunday = new(2026, 9, 20);
    static readonly DateOnly Monday = new(2026, 9, 21);

    [Fact]
    public void 평일은_영업일이다()
    {
        Assert.True(LunchSchedule.IsBusinessDay(Friday, new LunchOptions()));
        Assert.True(LunchSchedule.IsBusinessDay(Monday, new LunchOptions()));
    }

    [Fact]
    public void 주말은_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = true };

        Assert.False(LunchSchedule.IsBusinessDay(Saturday, options));
        Assert.False(LunchSchedule.IsBusinessDay(Sunday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼지면_주말도_영업일이다()
    {
        var options = new LunchOptions { WeekdaysOnly = false };

        Assert.True(LunchSchedule.IsBusinessDay(Saturday, options));
    }

    [Fact]
    public void 설정된_공휴일은_평일이어도_영업일이_아니다()
    {
        var options = new LunchOptions { Holidays = [Friday] };

        Assert.False(LunchSchedule.IsBusinessDay(Friday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼져도_공휴일_목록에_있으면_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = false, Holidays = [Friday] };

        Assert.False(LunchSchedule.IsBusinessDay(Friday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼져도_공휴일_목록의_주말은_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = false, Holidays = [Saturday] };

        Assert.False(LunchSchedule.IsBusinessDay(Saturday, options));
    }

    static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    static readonly LunchOptions Default = new();

    static DateTimeOffset At(DateOnly date, int hour, int minute) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, Kst);

    static TodayState State(
        DateOnly date,
        PollSnapshot? poll = null,
        bool mealPromptPosted = false,
        DateOnly? lastReminder = null,
        bool hasPending = false) =>
        new(date, poll, mealPromptPosted, lastReminder, hasPending);

    [Fact]
    public void 개시_시각_전에는_아무것도_하지_않는다()
    {
        var actions = LunchSchedule.GetDueActions(At(Friday, 9, 0), State(Friday), Default);

        Assert.Empty(actions);
    }

    [Fact]
    public void 개시_시각이_되고_투표가_없으면_연다()
    {
        var actions = LunchSchedule.GetDueActions(At(Friday, 10, 30), State(Friday), Default);

        var action = Assert.Single(actions);
        Assert.Equal(DueActionKind.OpenPoll, action.Kind);
        Assert.Equal(At(Friday, 10, 30), action.ScheduledFor);
    }

    [Fact]
    public void 이미_투표가_열려_있으면_다시_열지_않는다()
    {
        var poll = new PollSnapshot(1, PollStatus.Open, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 10, 45), State(Friday, poll), Default);

        Assert.Empty(actions);
    }

    [Fact]
    public void 마감_시각이_지난_열린_투표는_닫는다()
    {
        var poll = new PollSnapshot(1, PollStatus.Open, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 11, 0), State(Friday, poll), Default);

        Assert.Equal(DueActionKind.ClosePoll, Assert.Single(actions).Kind);
    }

    [Fact]
    public void 열린_투표는_아무리_늦어도_반드시_닫는다()
    {
        // 지각 유예(180분)를 한참 넘겼어도 닫기는 건너뛰지 않는다.
        // 투표가 열린 채로 영원히 남으면 다음 날 판정까지 오염된다.
        var poll = new PollSnapshot(1, PollStatus.Open, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 23, 30), State(Friday, poll), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.ClosePoll);
    }

    [Fact]
    public void 유예_시간을_넘겨_지난_투표_개시는_건너뛴다()
    {
        // 10:30 예정 + 180분 유예 → 13:30 이후엔 열지 않는다
        var actions = LunchSchedule.GetDueActions(At(Friday, 14, 0), State(Friday), Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.OpenPoll);
    }

    [Fact]
    public void 유예_시간_안이면_지난_투표_개시를_따라잡는다()
    {
        var actions = LunchSchedule.GetDueActions(At(Friday, 12, 0), State(Friday), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.OpenPoll);
    }

    [Fact]
    public void 기록_시각이_되면_기록을_요청한다()
    {
        var poll = new PollSnapshot(1, PollStatus.Closed, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 13, 30), State(Friday, poll), Default);

        Assert.Equal(DueActionKind.PostMealPrompt, Assert.Single(actions).Kind);
    }

    [Fact]
    public void 투표가_열리지_않았던_날에도_기록은_요청한다()
    {
        // 앱이 오전 내내 꺼져 있어 투표를 놓쳤어도 기록은 받아야 한다.
        // 기록이 비면 추천 알고리즘의 입력이 그만큼 낡는다.
        var actions = LunchSchedule.GetDueActions(At(Friday, 13, 30), State(Friday), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.PostMealPrompt);
    }

    [Fact]
    public void 이미_기록을_요청했으면_다시_요청하지_않는다()
    {
        var state = State(Friday, mealPromptPosted: true);

        var actions = LunchSchedule.GetDueActions(At(Friday, 15, 0), state, Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.PostMealPrompt);
    }

    [Fact]
    public void 주말에는_아무것도_하지_않는다()
    {
        var actions = LunchSchedule.GetDueActions(At(Saturday, 10, 30), State(Saturday), Default);

        Assert.Empty(actions);
    }

    [Fact]
    public void 공휴일에는_아무것도_하지_않는다()
    {
        var options = new LunchOptions { Holidays = [Friday] };

        var actions = LunchSchedule.GetDueActions(At(Friday, 10, 30), State(Friday), options);

        Assert.Empty(actions);
    }

    [Fact]
    public void 지정_요일에_Pending_식당이_있으면_보완을_요청한다()
    {
        // 기본 설정은 금요일 16:00
        var state = State(Friday, mealPromptPosted: true, hasPending: true);

        var actions = LunchSchedule.GetDueActions(At(Friday, 16, 0), state, Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.RemindPending);
    }

    [Fact]
    public void Pending_식당이_없으면_보완을_요청하지_않는다()
    {
        var state = State(Friday, mealPromptPosted: true, hasPending: false);

        var actions = LunchSchedule.GetDueActions(At(Friday, 16, 0), state, Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.RemindPending);
    }

    [Fact]
    public void 같은_날_보완_요청을_두_번_보내지_않는다()
    {
        var state = State(Friday, mealPromptPosted: true, lastReminder: Friday, hasPending: true);

        var actions = LunchSchedule.GetDueActions(At(Friday, 16, 30), state, Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.RemindPending);
    }

    [Fact]
    public void 오전에_기동하면_개시만_반환된다()
    {
        // 11:30에 기동 — 아직 투표가 없고 개시 유예 안이며, 개시 직후 마감 시각도 지났다.
        // 개시만 먼저 나오고, 다음 루프에서 마감이 잡힌다.
        var actions = LunchSchedule.GetDueActions(At(Friday, 11, 30), State(Friday), Default);

        Assert.Equal(DueActionKind.OpenPoll, Assert.Single(actions).Kind);
    }

    [Fact]
    public void 유예_시간_정확한_경계에서_따라잡기가_작동한다()
    {
        // 10:30 + 180분 = 13:30 정확히 — 아직 유예 안이므로 개시한다.
        var actions = LunchSchedule.GetDueActions(At(Friday, 13, 30), State(Friday), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.OpenPoll);
    }

    [Fact]
    public void 유예_시간_경계를_1초_넘으면_따라잡기를_건너뛴다()
    {
        // 13:30:01 — 유예를 벗어났으므로 개시하지 않는다.
        var now = new DateTimeOffset(Friday.Year, Friday.Month, Friday.Day, 13, 30, 1, Kst);
        var actions = LunchSchedule.GetDueActions(now, State(Friday), Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.OpenPoll);
    }

    [Fact]
    public void 전달된_오프셋을_그대로_사용하여_시각을_계산한다()
    {
        // 오프셋이 UTC+0이면 10:30에 개시하고, 다른 오프셋이면 다른 시각에 개시한다.
        // 이는 caller가 이미 LunchOptions.TimeZone으로 변환했다는 계약을 입증한다.
        var utcOffset = TimeSpan.FromHours(-8); // UTC-8 (예: 샌프란시스코)
        var now = new DateTimeOffset(Friday.Year, Friday.Month, Friday.Day, 10, 30, 0, utcOffset);
        var actions = LunchSchedule.GetDueActions(now, State(Friday), Default);

        // ScheduledFor는 now.Offset과 같은 UTC-8 오프셋으로 생성되므로,
        // now >= scheduledFor가 성립하고 OpenPoll이 반환된다.
        // 이는 GetDueActions가 now의 offset을 신뢰하고 직접 사용함을 보여준다.
        Assert.Contains(actions, a => a.Kind == DueActionKind.OpenPoll);
    }
}
