namespace SelectLunch.Slack.Leave;

/// <summary>
/// 휴가 조회 결과 상태.
/// <see cref="Disabled"/>: 기능이 꺼져 있어 조회하지 않았다(표시하지 않는다).
/// <see cref="Available"/>: 조회에 성공했다 — <see cref="LeaveRosterSnapshot.DisplayNames"/>가 비어 있으면 "휴가자 없음"이다.
/// <see cref="Partial"/>: 휴가로 확인된 사람(<see cref="LeaveRosterSnapshot.DisplayNames"/>, 항상 한 명 이상)은 있지만
/// 일부 메일·이름·날짜를 확정하지 못했다. 목록에 없는 사람이 더 있을 수 있으므로 "이것이 전부"처럼 표시하면 안 된다.
/// <see cref="Unavailable"/>: 조회 실패·잘림, 또는 확인된 사람이 없는 채로 이름/날짜 모호성이 남았다. "없음"과 구분해 표시해야 한다.
/// </summary>
public enum LeaveRosterStatus
{
    Disabled,
    Available,
    Unavailable,
    Partial,
}

/// <summary>
/// 메뉴 날짜 하루의 휴가자 표시용 결과. <paramref name="DisplayNames"/>는 현재 채널 활성 참여자 가운데
/// 메일에서 그날 휴가로 확인된 사람의 DB 표시 이름(없으면 실명) 그대로이며 정렬·중복 제거되어 있다.
/// 멘션이 아니라 일반 텍스트로만 표시해야 한다.
/// <paramref name="IncompleteReason"/>은 <see cref="LeaveRosterStatus.Partial"/>일 때 처음 만난 불완전 원인 분류이며
/// 이름·날짜·메일 내용은 담지 않는다. 로그에는 이 분류만 남긴다.
/// </summary>
public sealed record LeaveRosterSnapshot(
    LeaveRosterStatus Status,
    IReadOnlyList<string> DisplayNames,
    LeaveLookupFailure? IncompleteReason = null)
{
    public static LeaveRosterSnapshot Disabled { get; } = new(LeaveRosterStatus.Disabled, []);

    public static LeaveRosterSnapshot Unavailable { get; } = new(LeaveRosterStatus.Unavailable, []);
}

/// <summary>
/// 메뉴 게시일의 휴가자 명단. 구현은 가장 최근 (날짜, 투표) 하나의 결과만 RAM에 둔다.
/// 메일 원문·파싱 결과는 어디에도 남기지 않는다.
/// </summary>
public interface IPollLeaveRoster
{
    /// <summary>
    /// 같은 (<paramref name="date"/>, <paramref name="pollId"/>)는 한 번만 조회하고 결과(실패 포함)를 재사용한다.
    /// 호출자 취소는 이 호출의 대기만 끝내며 공유 조회는 앱 종료 때만 취소된다.
    /// 이미 더 최근 (날짜, 투표)가 조회돼 있으면 조회하지 않고 <see cref="LeaveRosterStatus.Unavailable"/>이다.
    /// </summary>
    Task<LeaveRosterSnapshot> GetAsync(long pollId, DateOnly date, CancellationToken ct);

    /// <summary>
    /// 기다리지 않고 지금 가진 결과만 본다. 꺼져 있으면 <see cref="LeaveRosterSnapshot.Disabled"/>,
    /// 같은 날짜의 조회가 성공적으로 끝나 있으면 그 결과, 아직 없거나 진행 중이면 null이다. 조회를 시작하지 않는다.
    /// </summary>
    LeaveRosterSnapshot? GetCached(DateOnly date);
}
