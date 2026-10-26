using SelectLunch.Shared.Members;

namespace SelectLunch.Shared.Leave;

/// <summary>
/// 메일 검색 결과 후보 하나. <paramref name="Text"/>는 목록 행에 보이던 글(보낸이·제목·미리보기·시각),
/// <paramref name="Body"/>는 읽기 창 본문(읽지 못했거나 요청하지 않았으면 null).
/// RAM에서 파서에 넘기는 입력일 뿐이며 어디에도 저장하지 않는다.
/// </summary>
public sealed record LeaveMailCandidate(string Text, string? Body);

/// <summary>
/// 메일에서 추출한 휴가 기간 하나. 날짜는 메일에 적힌 실제 휴가일이며 <paramref name="StartDate"/>와
/// <paramref name="EndDate"/>를 모두 포함한다(하루면 같다). 메일 수신일이 아니다.
/// </summary>
public sealed record LeavePeriod(string PersonName, DateOnly StartDate, DateOnly EndDate);

/// <summary>
/// 파서 결과. <paramref name="Periods"/>는 이름과 휴가 기간이 모두 확정된 항목,
/// <paramref name="UnresolvedNames"/>는 이름은 뽑혔지만 휴가 기간을 확정하지 못한 사람(중복 없음).
/// <paramref name="HasUnresolvedContent"/>는 회사 휴가 양식으로 확인되는 메일인데 사람 식별 자체를 못 했거나
/// 내용을 해석하지 못한 경우가 하나라도 있다는 신호다. 날짜를 못 읽은 사람이나 식별 못 한 양식을
/// "휴가 없음"으로 속이지 않고 호출자가 "확인하지 못했습니다" 처리를 할 수 있게 한다.
/// 휴가 양식이 아닌 메일(양식 표지가 없는 공지·잡담 등)은 이 신호를 켜지 않고 무시한다.
/// </summary>
public sealed record LeaveMailParseResult(
    IReadOnlyList<LeavePeriod> Periods,
    IReadOnlyList<string> UnresolvedNames,
    bool HasUnresolvedContent = false);

/// <summary>이름 매칭 결과 상태. <see cref="MemberNameMatchStatus.Ambiguous"/>는 서로 다른 SlackUserId 둘 이상이 같은 단계에서 맞았다는 뜻.</summary>
public enum MemberNameMatchStatus
{
    None,
    Unique,
    Ambiguous,
}

/// <summary><see cref="MemberNameMatch.Status"/>가 <see cref="MemberNameMatchStatus.Unique"/>일 때만 <see cref="MemberNameMatch.Member"/>가 있다.</summary>
public readonly record struct MemberNameMatch(MemberNameMatchStatus Status, RosterPerson? Member);
