namespace SelectLunch.Shared.Options;

public sealed class LunchOptions
{
    public const string SectionName = "Lunch";

    /// <summary>IANA 타임존 ID. 모든 시각 판정의 기준이 된다.</summary>
    public string TimeZone { get; set; } = "Asia/Seoul";

    public TimeOnly VoteOpenAt { get; set; } = new(10, 30);

    public int VoteDurationMinutes { get; set; } = 30;

    public TimeOnly MealRecordAt { get; set; } = new(13, 30);

    public bool WeekdaysOnly { get; set; } = true;

    /// <summary>
    /// 예정 시각을 이만큼 넘겨 지난 작업은 건너뛴다. 앱이 오래 꺼져 있다가
    /// 늦게 올라왔을 때 철 지난 메시지를 쏟아내지 않기 위함이다.
    /// </summary>
    public int CatchUpGraceMinutes { get; set; } = 180;

    public int PollIntervalSeconds { get; set; } = 30;

    public HashSet<DateOnly> Holidays { get; set; } = [];

    public PendingReminderOptions PendingReminder { get; set; } = new();

    public RecommendationOptions Recommendation { get; set; } = new();

    public TimeOnly VoteCloseAt => VoteOpenAt.AddMinutes(VoteDurationMinutes);
}
