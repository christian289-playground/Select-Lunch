namespace SelectLunch.Shared.Members;

/// <summary>스냅샷 속 사람 한 명. 이름은 검색 키워드이고 정체성은 <see cref="SlackUserId"/>다.</summary>
public sealed record RosterPerson(string SlackUserId, string RealName, string DisplayName);

/// <summary>
/// 한 채널의 "지금 이 순간 사람 구성원 전체". 일부만 담은 스냅샷을 넘기면
/// 빠진 사람이 퇴장으로 처리되므로, 전체를 성공적으로 모았을 때만 만들어야 한다.
/// </summary>
public sealed record ChannelRoster(string WorkspaceId, string ChannelId, IReadOnlyList<RosterPerson> People);

/// <summary>동기화 결과 집계. Added + Updated + Unchanged 는 스냅샷 인원과 같다.</summary>
public sealed record RosterSyncResult(int Added, int Updated, int Unchanged, int Deactivated)
{
    /// <summary>동기화 뒤 이 채널의 활성 구성원 수.</summary>
    public int Active => Added + Updated + Unchanged;
}
