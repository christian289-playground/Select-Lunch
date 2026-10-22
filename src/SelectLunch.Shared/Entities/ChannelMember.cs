namespace SelectLunch.Shared.Entities;

/// <summary>
/// 슬랙 채널에 속한 사람 명단 한 줄. 휴가·메일 같은 개인 상태는 여기에 두지 않는다 —
/// 식별(워크스페이스·채널·사용자 ID)과 이름 키워드만 보관한다.
///
/// 키는 (WorkspaceId, ChannelId, SlackUserId)다. 이름은 검색 키워드일 뿐 정체성이 아니라서
/// 개명하면 같은 행이 갱신되고, 동명이인은 사용자 ID로 갈린다.
/// 채널을 나간 사람은 지우지 않고 <see cref="IsActive"/>만 내린다 — 이력이 남는다.
/// </summary>
public sealed class ChannelMember
{
    /// <summary>`auth.test`의 team_id(`T…`). 채널 ID는 워크스페이스 안에서만 유일하다고 보고 함께 키로 쓴다.</summary>
    public required string WorkspaceId { get; set; }

    public required string ChannelId { get; set; }

    /// <summary>슬랙 사용자 ID(`U…`/`W…`).</summary>
    public required string SlackUserId { get; set; }

    /// <summary>프로필 실명. 비어 있으면 사용자 실명, 그것도 없으면 사용자명으로 대신한다.</summary>
    public string RealName { get; set; } = "";

    /// <summary>프로필 표시 이름. 없으면 빈 문자열.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>마지막 성공한 동기화 스냅샷에 들어 있었는가. false면 채널을 나간 사람이다.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// 이 행을 마지막으로 처리한 성공한 동기화 시각. 활성 행은 "마지막으로 확인된 시각",
    /// 비활성 행은 "비활성이 된 시각"이다 — 이미 비활성인 행은 이후 동기화가 건드리지 않는다.
    /// </summary>
    public DateTimeOffset LastSyncedAt { get; set; }
}
