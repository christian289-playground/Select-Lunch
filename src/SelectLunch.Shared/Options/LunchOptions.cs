namespace SelectLunch.Shared.Options;

public sealed class LunchOptions
{
    public const string SectionName = "Lunch";

    /// <summary>IANA 타임존 ID. 모든 시각 판정의 기준이 된다.</summary>
    public string TimeZone { get; set; } = "Asia/Seoul";

    /// <summary>
    /// 투표 개시 시각. 기본값은 배포되는 <c>appsettings.json</c>과 같아야 한다 —
    /// 설정이 없거나 바인딩에 실패했을 때 조용히 다른 시각으로 도는 것을 막는다.
    /// 10:30이 아닌 이유: 메뉴 게시가 10:42~10:49라 그 시각엔 오늘 메뉴가 존재하지 않는다.
    /// </summary>
    public TimeOnly VoteOpenAt { get; set; } = new(11, 0);

    public int VoteDurationMinutes { get; set; } = 30;

    /// <summary>
    /// 투표 개시 몇 분 전부터 오늘의 메뉴를 수집할지. 수집 시작을 별도의 시각 설정으로
    /// 두지 않고 <see cref="VoteOpenAt"/>에서 역산한다 — 시계 값이 둘이면 한쪽만 바꿨을 때
    /// 조용히 어긋나고, 그 어긋남은 "그날 메뉴가 안 붙는다"로만 드러나 알아차리기 어렵다.
    /// </summary>
    public int MenuCollectionLeadMinutes { get; set; } = 5;

    public TimeOnly MealRecordAt { get; set; } = new(13, 30);

    public bool WeekdaysOnly { get; set; } = true;

    /// <summary>
    /// 예정 시각을 이만큼 넘겨 지난 작업은 건너뛴다. 앱이 오래 꺼져 있다가
    /// 늦게 올라왔을 때 철 지난 메시지를 쏟아내지 않기 위함이다.
    /// </summary>
    public int CatchUpGraceMinutes { get; set; } = 180;

    public int PollIntervalSeconds { get; set; } = 60;

    public HashSet<DateOnly> Holidays { get; set; } = [];

    public PendingReminderOptions PendingReminder { get; set; } = new();

    public RecommendationOptions Recommendation { get; set; } = new();

    public TimeOnly VoteCloseAt => VoteOpenAt.AddMinutes(VoteDurationMinutes);

    /// <summary>
    /// 메뉴 수집을 시작하는 시각. 투표 개시 <see cref="MenuCollectionLeadMinutes"/>분 전이다.
    /// 수집 종료는 <see cref="MealRecordAt"/>이 맡는다.
    /// </summary>
    public TimeOnly MenuCollectionStart => VoteOpenAt.AddMinutes(-MenuCollectionLeadMinutes);
}
