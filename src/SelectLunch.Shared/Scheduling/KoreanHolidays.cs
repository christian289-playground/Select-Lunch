using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;

namespace SelectLunch.Shared.Scheduling;

/// <summary>
/// 대한민국 관공서 공휴일을 코드로 계산한다. 외부 API를 쓰지 않는다 —
/// 서버가 사설망이고 런타임 외부 의존을 늘리지 않기 위함이다.
///
/// 순수 함수다. DB·네트워크·<see cref="DateTime.Now"/>를 보지 않으며,
/// 같은 연도에는 언제나 같은 집합을 돌려준다. 연도별 캐시가 있지만
/// 계산 결과만 담으므로 관측 가능한 부수효과가 없다.
///
/// **계산할 수 없는 것**: 임시공휴일(2025-01-27 등)과 공직선거법상 선거일
/// (2026-06-03 지방선거 등)은 그때그때 법으로 정해지므로 여기서 나오지 않는다.
/// 그런 날은 <c>LunchOptions.Holidays</c> 수동 목록으로 넣어야 한다.
/// </summary>
public static class KoreanHolidays
{
    /// <summary>
    /// 음력 변환에 쓰는 달력. 상태를 바꾸지 않는 조회만 하므로 공유해도 안전하다.
    /// </summary>
    static readonly KoreanLunisolarCalendar Lunar = new();

    static readonly ConcurrentDictionary<int, FrozenSet<DateOnly>> Cache = new();

    /// <summary>
    /// 계산이 유효한 첫 연도. 2023-05 개정으로 대체공휴일 대상이 지금 범위
    /// (삼일절·광복절·개천절·한글날·성탄절까지)로 넓어졌고, 여기서는 그 규칙
    /// 하나만 구현한다. 그 이전 연도에 적용하면 실제와 달라지므로 받지 않는다.
    /// </summary>
    public const int MinSupportedYear = 2024;

    /// <summary>
    /// 계산이 유효한 마지막 연도. <see cref="KoreanLunisolarCalendar"/>의 지원
    /// 범위가 2051-02-10에서 끝나 2051년 설날을 구할 수 없다. 그래서 그 앞 해까지다.
    /// </summary>
    public static readonly int MaxSupportedYear = new KoreanLunisolarCalendar().MaxSupportedDateTime.Year - 1;

    /// <summary>
    /// 지원 범위를 벗어나면 <c>false</c>. 예외를 던지지 않는 이유는 스케줄러가
    /// 매 주기 이걸 부르기 때문이다 — 2051년에 봇이 죽는 것보다 공휴일을 모르는
    /// 쪽이 낫다(그때는 수동 <c>Holidays</c>로 메울 수 있다).
    /// </summary>
    public static bool IsHoliday(DateOnly date) =>
        IsSupported(date.Year) && ForYear(date.Year).Contains(date);

    public static bool IsSupported(int year) => year >= MinSupportedYear && year <= MaxSupportedYear;

    /// <summary>해당 연도의 공휴일 전부(대체공휴일 포함).</summary>
    /// <exception cref="ArgumentOutOfRangeException">지원 범위 밖의 연도.</exception>
    public static IReadOnlySet<DateOnly> ForYear(int year)
    {
        if (!IsSupported(year))
        {
            throw new ArgumentOutOfRangeException(
                nameof(year),
                year,
                $"공휴일 계산은 {MinSupportedYear}~{MaxSupportedYear}년만 지원한다.");
        }

        return Cache.GetOrAdd(year, static y => Compute(y));
    }

    /// <summary>
    /// 대체공휴일 규칙이 다른 종류의 공휴일. 열거 순서가 같은 날짜에 겹친
    /// 공휴일들의 처리 순서를 정한다(랜덤 금지 — 결과가 항상 같아야 한다).
    /// </summary>
    enum Kind
    {
        /// <summary>신정·현충일. 대체공휴일이 없다.</summary>
        NoSubstitute,

        /// <summary>설날·추석 연휴. 일요일 또는 다른 공휴일과 겹칠 때 대체한다(토요일은 제외).</summary>
        LunarBreak,

        /// <summary>삼일절·부처님오신날·광복절·개천절·한글날·성탄절. 토·일에 대체한다.</summary>
        Weekend,

        /// <summary>어린이날. 토·일에 더해 다른 공휴일과 겹쳐도 대체한다.</summary>
        ChildrensDay,
    }

    readonly record struct BaseHoliday(DateOnly Date, Kind Kind, string Name);

    static FrozenSet<DateOnly> Compute(int year)
    {
        var bases = BaseHolidays(year);

        // 기준 공휴일 집합. 어린이날 중복 판정과 대체일 탐색이 모두 이 집합을 본다.
        var holidays = new HashSet<DateOnly>(bases.Select(h => h.Date));

        // 날짜 순, 같은 날이면 Kind 순으로 처리한다. 처리 순서가 대체일을 바꾸므로
        // (앞선 대체일이 뒤의 탐색에서 "기존 공휴일"이 된다) 유일 키까지 정렬한다.
        foreach (var holiday in bases
            .OrderBy(h => h.Date)
            .ThenBy(h => (int)h.Kind)
            .ThenBy(h => h.Name, StringComparer.Ordinal))
        {
            if (!NeedsSubstitute(holiday, bases))
                continue;

            holidays.Add(NextSubstituteDay(holiday.Date, holidays));
        }

        return holidays.ToFrozenSet();
    }

    static List<BaseHoliday> BaseHolidays(int year)
    {
        var seollal = LunarToSolar(year, 1, 1);
        var chuseok = LunarToSolar(year, 8, 15);

        return
        [
            new(new DateOnly(year, 1, 1), Kind.NoSubstitute, "신정"),
            // 설 연휴 첫날은 음력 섣달그믐(12/29 또는 12/30)이라 달마다 다르다.
            // 음력 12월이 며칠까지 있는지 따지지 않고 양력에서 하루를 빼는 쪽이 안전하다.
            new(seollal.AddDays(-1), Kind.LunarBreak, "설날 연휴 첫날"),
            new(seollal, Kind.LunarBreak, "설날"),
            new(seollal.AddDays(1), Kind.LunarBreak, "설날 연휴 마지막날"),
            new(new DateOnly(year, 3, 1), Kind.Weekend, "삼일절"),
            new(LunarToSolar(year, 4, 8), Kind.Weekend, "부처님오신날"),
            new(new DateOnly(year, 5, 5), Kind.ChildrensDay, "어린이날"),
            new(new DateOnly(year, 6, 6), Kind.NoSubstitute, "현충일"),
            new(new DateOnly(year, 8, 15), Kind.Weekend, "광복절"),
            new(chuseok.AddDays(-1), Kind.LunarBreak, "추석 연휴 첫날"),
            new(chuseok, Kind.LunarBreak, "추석"),
            new(chuseok.AddDays(1), Kind.LunarBreak, "추석 연휴 마지막날"),
            new(new DateOnly(year, 10, 3), Kind.Weekend, "개천절"),
            new(new DateOnly(year, 10, 9), Kind.Weekend, "한글날"),
            new(new DateOnly(year, 12, 25), Kind.Weekend, "성탄절"),
        ];
    }

    static bool NeedsSubstitute(BaseHoliday holiday, List<BaseHoliday> all) => holiday.Kind switch
    {
        Kind.NoSubstitute => false,

        // 설날·추석 연휴는 토요일과 겹쳐도 대체가 없지만, 일요일 **또는 다른 공휴일**과
        // 겹치면 대체한다. 뒤쪽(다른 공휴일)을 빠뜨리기 쉬운데, 실제 선례가 있다 —
        // 2017년 추석 연휴 첫날(10/3 화)이 개천절과 겹쳐 10/6이 대체공휴일이 됐다.
        // 2024~2027에는 이 겹침이 없어 그 연도 표만으로는 검증되지 않는다.
        Kind.LunarBreak => holiday.Date.DayOfWeek == DayOfWeek.Sunday
            || OverlapsAnother(holiday, all),

        Kind.Weekend => IsWeekend(holiday.Date),

        // 어린이날은 토·일에 더해 다른 공휴일과 겹쳐도 대체한다. 2025-05-05가
        // 부처님오신날과 겹쳐 5/6이 대체공휴일이 된 것이 그 사례다.
        Kind.ChildrensDay => IsWeekend(holiday.Date) || OverlapsAnother(holiday, all),

        _ => false,
    };

    /// <summary>
    /// 같은 날에 놓인 다른 공휴일이 있는지. 이름이 유일하므로 이름으로 자기 자신을 가린다.
    /// 같은 연휴 안의 다른 날(설날 ↔ 설 연휴 첫날)은 날짜가 달라 걸리지 않는다.
    /// </summary>
    static bool OverlapsAnother(BaseHoliday holiday, List<BaseHoliday> all) =>
        all.Any(other => other.Date == holiday.Date && other.Name != holiday.Name);

    static bool IsWeekend(DateOnly date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// 공휴일 다음 날부터 하루씩 나아가며 일요일도 아니고 이미 공휴일도 아닌 첫 날.
    /// 토요일은 법정 공휴일이 아니라 건너뛰지 않는다.
    /// </summary>
    static DateOnly NextSubstituteDay(DateOnly holiday, HashSet<DateOnly> taken)
    {
        var candidate = holiday.AddDays(1);

        // 연휴가 아무리 길어도 2주 안에 빈 날이 나온다. 무한 루프를 막는 상한이다.
        for (var i = 0; i < 14; i++)
        {
            if (candidate.DayOfWeek != DayOfWeek.Sunday && !taken.Contains(candidate))
                return candidate;

            candidate = candidate.AddDays(1);
        }

        throw new InvalidOperationException($"{holiday:yyyy-MM-dd}의 대체공휴일을 찾지 못했다.");
    }

    /// <summary>
    /// 음력 <paramref name="month"/>월 <paramref name="day"/>일을 양력으로 바꾼다.
    /// 윤달이 있는 해에는 그 뒤의 달 번호가 하나씩 밀리므로 보정한다 —
    /// <see cref="KoreanLunisolarCalendar.GetLeapMonth(int)"/>가 돌려주는 값은
    /// "윤달이 몇 번째 달 자리인지"이고, 0이면 윤달이 없는 해다.
    /// </summary>
    static DateOnly LunarToSolar(int year, int month, int day)
    {
        var leapMonth = Lunar.GetLeapMonth(year);
        var index = leapMonth != 0 && month >= leapMonth ? month + 1 : month;

        return DateOnly.FromDateTime(Lunar.ToDateTime(year, index, day, 0, 0, 0, 0));
    }
}
