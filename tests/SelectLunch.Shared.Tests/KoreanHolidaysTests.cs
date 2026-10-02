using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Shared.Tests;

/// <summary>
/// 공휴일 계산의 핵심 검증. 규칙을 다시 글로 옮겨 적는 테스트는 구현과 같이 틀리므로,
/// **실제 연도별 공휴일 표를 통째로 하드코딩하고 집합이 정확히 같은지** 본다.
/// 빠진 날과 남는 날이 모두 실패로 잡힌다.
///
/// 표의 범위: 「관공서의 공휴일에 관한 규정」의 **날짜가 정해져 있는** 공휴일과
/// 그 대체공휴일만이다. 아래 두 가지는 계산으로 나올 수 없어 표에서 **뺐다** —
/// 빼지 않으면 "계산이 못 맞히는 것"을 계산에게 묻는 테스트가 된다.
///
/// - 임시공휴일: 2024-10-01(국군의 날), 2025-01-27
/// - 공직선거법상 선거일: 2024-04-10(제22대 국회의원), 2025-06-03(제21대 대통령),
///   2026-06-03(제9회 지방선거)
///
/// 이 날들은 <c>LunchOptions.Holidays</c> 수동 목록이 담당한다
/// (<see cref="LunchScheduleTests"/>에 그 합집합 동작을 보는 테스트가 있다).
/// </summary>
public class KoreanHolidaysTests
{
    /// <summary>
    /// 2024년. 대체공휴일 2건: 2/12(설 연휴 2/11 일요일), 5/6(어린이날 5/5 일요일).
    /// </summary>
    static readonly DateOnly[] Y2024 =
    [
        new(2024, 1, 1),    // 신정
        new(2024, 2, 9),    // 설 연휴
        new(2024, 2, 10),   // 설날(토)
        new(2024, 2, 11),   // 설 연휴(일)
        new(2024, 2, 12),   // 대체공휴일
        new(2024, 3, 1),    // 삼일절(금)
        new(2024, 5, 5),    // 어린이날(일)
        new(2024, 5, 6),    // 대체공휴일
        new(2024, 5, 15),   // 부처님오신날
        new(2024, 6, 6),    // 현충일
        new(2024, 8, 15),   // 광복절
        new(2024, 9, 16),   // 추석 연휴
        new(2024, 9, 17),   // 추석
        new(2024, 9, 18),   // 추석 연휴
        new(2024, 10, 3),   // 개천절
        new(2024, 10, 9),   // 한글날
        new(2024, 12, 25),  // 성탄절
    ];

    /// <summary>
    /// 2025년. 대체공휴일 3건: 3/3(삼일절 토), 5/6(어린이날이 부처님오신날과 겹침),
    /// 10/8(추석 연휴 10/5 일요일). 음력 윤6월이 있는 해라 윤달 보정이 틀리면
    /// 추석이 통째로 한 달 어긋난다.
    /// </summary>
    static readonly DateOnly[] Y2025 =
    [
        new(2025, 1, 1),    // 신정
        new(2025, 1, 28),   // 설 연휴
        new(2025, 1, 29),   // 설날
        new(2025, 1, 30),   // 설 연휴
        new(2025, 3, 1),    // 삼일절(토)
        new(2025, 3, 3),    // 대체공휴일
        new(2025, 5, 5),    // 어린이날 = 부처님오신날
        new(2025, 5, 6),    // 대체공휴일(중복)
        new(2025, 6, 6),    // 현충일
        new(2025, 8, 15),   // 광복절
        new(2025, 10, 3),   // 개천절
        new(2025, 10, 5),   // 추석 연휴(일)
        new(2025, 10, 6),   // 추석
        new(2025, 10, 7),   // 추석 연휴
        new(2025, 10, 8),   // 대체공휴일
        new(2025, 10, 9),   // 한글날
        new(2025, 12, 25),  // 성탄절
    ];

    /// <summary>
    /// 2026년. 대체공휴일 4건: 3/2(삼일절 일), 5/25(부처님오신날 일),
    /// 8/17(광복절 토 → 일요일을 건너뛴 월요일), 10/5(개천절 토 → 월).
    /// 현충일 6/6이 토요일이지만 대체 대상이 아니다.
    /// </summary>
    static readonly DateOnly[] Y2026 =
    [
        new(2026, 1, 1),    // 신정
        new(2026, 2, 16),   // 설 연휴
        new(2026, 2, 17),   // 설날
        new(2026, 2, 18),   // 설 연휴
        new(2026, 3, 1),    // 삼일절(일)
        new(2026, 3, 2),    // 대체공휴일
        new(2026, 5, 5),    // 어린이날
        new(2026, 5, 24),   // 부처님오신날(일)
        new(2026, 5, 25),   // 대체공휴일
        new(2026, 6, 6),    // 현충일(토) — 대체 없음
        new(2026, 8, 15),   // 광복절(토)
        new(2026, 8, 17),   // 대체공휴일
        new(2026, 9, 24),   // 추석 연휴
        new(2026, 9, 25),   // 추석
        new(2026, 9, 26),   // 추석 연휴(토) — 대체 없음
        new(2026, 10, 3),   // 개천절(토)
        new(2026, 10, 5),   // 대체공휴일
        new(2026, 10, 9),   // 한글날
        new(2026, 12, 25),  // 성탄절
    ];

    /// <summary>
    /// 2027년. 대체공휴일 5건: 2/9(설날 2/7 일), 8/16(광복절 일), 10/4(개천절 일),
    /// 10/11(한글날 토 → 월), 12/27(성탄절 토 → 월).
    /// 현충일 6/6이 일요일이지만 대체 대상이 아니다 — 가장 강한 반례다.
    /// </summary>
    static readonly DateOnly[] Y2027 =
    [
        new(2027, 1, 1),    // 신정
        new(2027, 2, 6),    // 설 연휴(토) — 대체 없음
        new(2027, 2, 7),    // 설날(일)
        new(2027, 2, 8),    // 설 연휴
        new(2027, 2, 9),    // 대체공휴일
        new(2027, 3, 1),    // 삼일절
        new(2027, 5, 5),    // 어린이날
        new(2027, 5, 13),   // 부처님오신날
        new(2027, 6, 6),    // 현충일(일) — 대체 없음
        new(2027, 8, 15),   // 광복절(일)
        new(2027, 8, 16),   // 대체공휴일
        new(2027, 9, 14),   // 추석 연휴
        new(2027, 9, 15),   // 추석
        new(2027, 9, 16),   // 추석 연휴
        new(2027, 10, 3),   // 개천절(일)
        new(2027, 10, 4),   // 대체공휴일
        new(2027, 10, 9),   // 한글날(토)
        new(2027, 10, 11),  // 대체공휴일
        new(2027, 12, 25),  // 성탄절(토)
        new(2027, 12, 27),  // 대체공휴일
    ];

    public static TheoryData<int, DateOnly[]> Tables => new()
    {
        { 2024, Y2024 },
        { 2025, Y2025 },
        { 2026, Y2026 },
        { 2027, Y2027 },
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void 연도별_공휴일_집합이_실제_표와_정확히_일치한다(int year, DateOnly[] expected)
    {
        var actual = KoreanHolidays.ForYear(year);

        // 빠진 날과 남는 날을 따로 뽑는다 — 개수만 보면 하나 빠지고 하나 남는
        // 맞교환을 놓친다. 둘 다 비어 있어야 통과다.
        var missing = expected.Except(actual).Order().ToArray();
        var extra = actual.Except(expected).Order().ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"{year}년 불일치 — 빠짐: [{Format(missing)}], 남음: [{Format(extra)}]");
        Assert.Equal(expected.Length, actual.Count);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void 표에_없는_날은_공휴일이_아니다(int year, DateOnly[] expected)
    {
        // 위 테스트는 ForYear만 본다. IsHoliday가 ForYear와 다른 경로로 샐 수 있어
        // 그 해 365일을 전부 훑는다 — "항상 true"를 돌려주는 구현도 여기서 걸린다.
        var table = expected.ToHashSet();

        for (var date = new DateOnly(year, 1, 1); date.Year == year; date = date.AddDays(1))
            Assert.Equal(table.Contains(date), KoreanHolidays.IsHoliday(date));
    }

    [Fact]
    public void 현충일은_주말과_겹쳐도_대체공휴일이_없다()
    {
        // 2026-06-06 토요일, 2027-06-06 일요일. 둘 다 대체 비대상이다.
        // "주말이면 전부 대체" 구현이 여기서 깨진다.
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2026, 6, 6)));
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2026, 6, 8)));

        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2027, 6, 6)));
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2027, 6, 7)));
    }

    [Fact]
    public void 신정은_주말과_겹쳐도_대체공휴일이_없다()
    {
        // 2034-01-01은 일요일이다. 표가 있는 2024~2027에는 이 경우가 없어
        // 전체 집합 비교만으로는 신정의 대체 비대상 여부를 전혀 검증하지 못한다.
        Assert.Equal(DayOfWeek.Sunday, new DateOnly(2034, 1, 1).DayOfWeek);
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2034, 1, 1)));
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2034, 1, 2)));
    }

    [Fact]
    public void 설날_추석_연휴는_토요일과_겹쳐도_대체공휴일이_없다()
    {
        // 2024-02-10 설날이 토요일이지만 대체는 일요일인 2/11 몫의 하루뿐이다.
        // 토요일까지 대체하면 2월 공휴일이 5일이 되어 여기서 깨진다.
        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2024, 2, 10).DayOfWeek);
        Assert.Equal(
            4,
            KoreanHolidays.ForYear(2024).Count(d => d.Month == 2));

        // 2026-09-26 추석 연휴 마지막날이 토요일. 일요일과 겹치지 않아 대체가 없다.
        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2026, 9, 26).DayOfWeek);
        Assert.Equal(
            3,
            KoreanHolidays.ForYear(2026).Count(d => d.Month == 9));
    }

    [Fact]
    public void 어린이날은_다른_공휴일과_겹치면_평일이어도_대체공휴일이_생긴다()
    {
        // 2025-05-05는 월요일인데 부처님오신날과 겹쳐 5/6이 대체공휴일이 됐다.
        // "토·일에만 대체" 구현은 5/6을 만들지 못해 여기서 깨진다.
        Assert.Equal(DayOfWeek.Monday, new DateOnly(2025, 5, 5).DayOfWeek);
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2025, 5, 6)));

        // 반대쪽: 겹치지 않는 평일 어린이날은 대체가 없다.
        Assert.Equal(DayOfWeek.Tuesday, new DateOnly(2026, 5, 5).DayOfWeek);
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2026, 5, 6)));
    }

    [Fact]
    public void 대체일은_일요일을_건너뛰고_토요일은_건너뛰지_않는다()
    {
        // 2026-08-15 광복절(토) → 8/16은 일요일이라 건너뛰고 8/17 월요일.
        // 8/16이 공휴일로 들어가면 "일요일도 대체일이 될 수 있다"는 뜻이라 틀렸다.
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2026, 8, 16)));
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2026, 8, 17)));

        // 2027-10-03 개천절(일) → 바로 다음 날 10/4 월요일. 한 칸만 민다.
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2027, 10, 4)));
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2027, 10, 5)));
    }

    [Fact]
    public void 대체일은_이미_공휴일인_날을_건너뛴다()
    {
        // 2025 추석: 10/5(일)이 걸려 대체가 생기는데 10/6·10/7이 이미 연휴라
        // 10/8이 되어야 한다. "그냥 다음 날"이면 10/6을 중복으로 집어 연휴가 3일로 끝난다.
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2025, 10, 8)));
        Assert.Equal(6, KoreanHolidays.ForYear(2025).Count(d => d.Month == 10));

        // 2027 설날: 2/7(일) → 2/8이 연휴라 2/9.
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2027, 2, 9)));
        Assert.Equal(4, KoreanHolidays.ForYear(2027).Count(d => d.Month == 2));
    }

    [Fact]
    public void 설날_추석_연휴가_다른_공휴일과_겹치면_평일이어도_대체공휴일이_생긴다()
    {
        // 브리프에는 "설날·추석 연휴는 일요일과 겹칠 때만"으로 적혀 있으나,
        // 실제 규정은 **다른 공휴일과 겹칠 때도** 대체한다. 선례: 2017년 추석 연휴
        // 첫날 10/3(화)이 개천절과 겹쳐 10/6이 대체공휴일이 됐다.
        //
        // 2024~2027에는 이 겹침이 없어 연도 표로는 검증되지 않는다. 범위 안에서
        // 처음 겹치는 해가 2028년이다 — 추석 10/3(화)이 개천절과 겹친다.
        // ※ 2028년 값은 공식 발표표가 아니라 규정에서 유도한 것이다(보고서에 명시).
        var y2028 = KoreanHolidays.ForYear(2028);

        Assert.Equal(DayOfWeek.Tuesday, new DateOnly(2028, 10, 3).DayOfWeek);
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2028, 10, 2)));  // 추석 연휴
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2028, 10, 3)));  // 추석 = 개천절
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2028, 10, 4)));  // 추석 연휴
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2028, 10, 5)));  // 대체공휴일

        // 겹침 대체를 빠뜨린 구현은 10/5가 없어 10월 공휴일이 4일(10/9 포함)로 끝난다.
        Assert.Equal(5, y2028.Count(d => d.Month == 10));
    }

    [Fact]
    public void 윤달이_있는_해에도_음력_공휴일이_밀리지_않는다()
    {
        // 2025년 음력에는 윤6월이 있다. 보정하지 않으면 ToDateTime의 8번째 달이
        // 음력 7월이 되어 추석이 2025-09-06으로 한 달 당겨진다.
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2025, 10, 6)));
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2025, 9, 6)));

        // 윤달보다 앞선 달(음 4/8 부처님오신날)은 밀리면 안 된다 — 과보정 방지.
        Assert.True(KoreanHolidays.IsHoliday(new DateOnly(2025, 5, 5)));
    }

    [Fact]
    public void 설_연휴_첫날은_섣달이_29일인_해에도_설날_하루_전이다()
    {
        // 음력 12월이 29일까지인 해에 "음 12/30"을 직접 변환하면 터지거나 엉뚱한
        // 날이 나온다. 양력에서 하루를 빼는 방식이라야 항상 설날 전날이 된다.
        foreach (var year in new[] { 2024, 2025, 2026, 2027 })
        {
            var lunarNewYearRun = KoreanHolidays.ForYear(year)
                .Where(d => d.Month <= 2)
                .Where(d => d != new DateOnly(year, 1, 1))
                .Order()
                .ToArray();

            // 연휴 3일이 하루씩 이어져야 한다(대체공휴일이 뒤에 더 붙을 수는 있다).
            Assert.True(lunarNewYearRun.Length >= 3);
            Assert.Equal(lunarNewYearRun[0].AddDays(1), lunarNewYearRun[1]);
            Assert.Equal(lunarNewYearRun[1].AddDays(1), lunarNewYearRun[2]);
        }
    }

    [Fact]
    public void 지원_범위를_벗어난_연도는_예외를_던진다()
    {
        // 2023-05 개정 이전 규칙은 구현하지 않았다. 조용히 틀린 답을 주느니 거절한다.
        Assert.Equal(2024, KoreanHolidays.MinSupportedYear);
        Assert.Throws<ArgumentOutOfRangeException>(() => KoreanHolidays.ForYear(2023));

        // KoreanLunisolarCalendar가 2051-02-10에서 끝난다 — 2051년 설날을 못 구한다.
        Assert.Equal(2050, KoreanHolidays.MaxSupportedYear);
        Assert.Throws<ArgumentOutOfRangeException>(() => KoreanHolidays.ForYear(2051));
    }

    [Fact]
    public void 지원_범위_밖_날짜는_예외_대신_공휴일이_아니라고_답한다()
    {
        // 스케줄러가 매 주기 부르는 경로라 예외가 나면 봇이 멈춘다.
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2023, 1, 1)));
        Assert.False(KoreanHolidays.IsHoliday(new DateOnly(2051, 1, 1)));
        Assert.False(KoreanHolidays.IsSupported(2023));
        Assert.True(KoreanHolidays.IsSupported(2024));
        Assert.True(KoreanHolidays.IsSupported(2050));
        Assert.False(KoreanHolidays.IsSupported(2051));
    }

    [Fact]
    public void 지원_범위_전체가_예외_없이_계산된다()
    {
        // 2024~2050 중 한 해라도 음력 변환이 터지면 그날 봇이 죽는다.
        // 규칙 검증이 아니라 "계산이 끝까지 돈다"만 보는 테스트다.
        for (var year = KoreanHolidays.MinSupportedYear; year <= KoreanHolidays.MaxSupportedYear; year++)
        {
            var holidays = KoreanHolidays.ForYear(year);

            // 고정 양력 8일 + 설·추석 6일 = 14일이 최소이고(겹침은 어린이날·부처님오신날뿐),
            // 대체공휴일이 아무리 붙어도 스물 몇 개를 넘지 않는다.
            Assert.InRange(holidays.Count, 14, 25);
            Assert.All(holidays, d => Assert.Equal(year, d.Year));
        }
    }

    [Fact]
    public void 같은_연도를_여러_번_물어도_같은_답이_나온다()
    {
        // 캐시가 경쟁 상태로 깨지거나 호출마다 달라지면 순수 함수가 아니다.
        var expected = KoreanHolidays.ForYear(2026).Order().ToArray();

        var results = new DateOnly[16][];
        Parallel.For(0, results.Length, i => results[i] = KoreanHolidays.ForYear(2026).Order().ToArray());

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    static string Format(DateOnly[] dates) =>
        string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")));
}
