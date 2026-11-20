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

    /// <summary>미리 출발 알림의 전체 인원 기준. 공개 투표가 이 인원의 30% 이상 모이면 알린다.</summary>
    public int EarlyDepartureParticipantCount { get; set; } = 6;

    /// <summary>
    /// 식사 기록에서 식당 한 곳을 더 확정할 수 있게 하는 식당 투표 인원 기준(양의 정수).
    /// 같은 채널·날짜의 마감된 본투표에서 식당을 고른 사람 수가 이 값을 넘을 때마다 확정 가능한 식당이 하나 늘어난다
    /// (<c>max(1, ceil(투표 인원 / 기준))</c>). 미리 출발 알림의
    /// <see cref="EarlyDepartureParticipantCount"/>와는 별개다.
    /// </summary>
    public int MealParticipantsPerRestaurant { get; set; } = 5;

    /// <summary>
    /// 지난 식사 기록 요청 한 건의 최초 확정을 한시적으로 허용하는 날짜. 기본 미지정이며 운영 배포에서만 지정한다.
    /// 구성 채널에 기록 요청을 게시한 이력이 있고 그날 식사 기록이 아직 없을 때만 효력이 있다.
    /// 일반적인 과거 기록 잠금은 이 날짜 하나를 빼고 그대로 유지된다.
    /// </summary>
    public DateOnly? CarryoverMealPromptDate { get; set; }

    /// <summary>
    /// 수동 공휴일 목록. 임시공휴일(2025-01-27)이나 선거일(2026-06-03 지방선거)처럼
    /// 그때그때 법으로 정해져 계산할 수 없는 날을 넣는 자리다.
    /// <see cref="UseKoreanHolidays"/>로 계산한 공휴일과 **합집합**으로 쓰인다.
    /// </summary>
    public HashSet<DateOnly> Holidays { get; set; } = [];

    /// <summary>
    /// 대한민국 법정 공휴일을 <see cref="Scheduling.KoreanHolidays"/>로 계산해
    /// 영업일 판정에 반영할지. 끄면 <see cref="Holidays"/> 수동 목록만 쓴다.
    /// 다른 나라에서 돌리거나 계산이 틀렸을 때 빠져나갈 구멍이다.
    /// </summary>
    public bool UseKoreanHolidays { get; set; } = true;

    public PendingReminderOptions PendingReminder { get; set; } = new();

    public RecommendationOptions Recommendation { get; set; } = new();

    public TimeOnly VoteCloseAt => VoteOpenAt.AddMinutes(VoteDurationMinutes);

    /// <summary>
    /// 메뉴 수집을 시작하는 시각. 투표 개시 <see cref="MenuCollectionLeadMinutes"/>분 전이다.
    /// 수집 종료는 <see cref="MealRecordAt"/>이 맡는다.
    /// </summary>
    public TimeOnly MenuCollectionStart => VoteOpenAt.AddMinutes(-MenuCollectionLeadMinutes);
}
