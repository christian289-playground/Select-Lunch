using SelectLunch.Shared.Leave;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// 회사별 메일 형식을 읽어 사람 이름과 휴가 기간을 뽑는 어댑터. 구현은 순수 함수여야 한다:
/// 입력 메일을 어디에도 저장·기록하지 않고, 결과는 호출자가 RAM에서만 쓴다.
/// 날짜 범위는 양 끝을 포함하며 메뉴 날짜와의 교차 판정은 호출자가 한다.
/// </summary>
public interface ILeaveMailParser
{
    LeaveMailParseResult Parse(LeaveMailCandidate mail);
}
