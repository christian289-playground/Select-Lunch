using SelectLunch.Shared.Leave;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// 메일 검색 전송 계층. 키워드로 후보 메일을 모아 RAM에서만 돌려준다.
/// 결과가 완전하다고 확신할 수 없으면(잘림·불완전·실행 실패·제한 시간·형식 오류) 일부만 돌려주지 않고
/// <see cref="LeaveLookupException"/>을 던진다. 호출자 취소는 <see cref="OperationCanceledException"/>이다.
/// </summary>
public interface ILeaveMailReader
{
    Task<IReadOnlyList<LeaveMailCandidate>> ReadAsync(IReadOnlyList<string> keywords, CancellationToken ct);
}
