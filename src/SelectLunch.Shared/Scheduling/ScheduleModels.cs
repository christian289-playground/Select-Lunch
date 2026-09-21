using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Scheduling;

public sealed record PollSnapshot(
    long PollId,
    PollStatus Status,
    DateTimeOffset ClosesAt,
    string? MessageTs = null,
    DateTimeOffset? ResultAnnouncedAt = null);

/// <summary>스케줄 판정에 필요한 오늘치 상태. 조회 계층이 DB에서 읽어 만든다.</summary>
public sealed record TodayState(
    DateOnly Date,
    PollSnapshot? Poll,
    bool MealPromptPosted,
    DateOnly? LastPendingReminderOn,
    bool HasPendingRestaurants);

public enum DueActionKind
{
    OpenPoll,
    ClosePoll,
    PostMealPrompt,
    RemindPending,
}

/// <param name="ScheduledFor">원래 예정 시각. 지각 판정과 로그에 쓴다.</param>
public sealed record DueAction(
    DueActionKind Kind,
    DateTimeOffset ScheduledFor);
