using SelectLunch.Shared.Leave;
using SelectLunch.Shared.Members;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// 파서가 뽑은 휴가 기간·이름을 메뉴 날짜와 현재 채널 활성 참여자 명단에 대조해 표시 이름으로 바꾼다. I/O가 없는 순수 함수다.
/// <list type="bullet">
/// <item>휴가 기간(양 끝 포함)이 메뉴 날짜를 덮는 것만 본다. 메일 수신일은 쓰지 않는다.</item>
/// <item>이름이 명단 사람과 유일하게 맞아야 표시한다. 명단 사람이 아닌 이름은 이 채널과 무관하므로 건너뛴다.</item>
/// <item>메뉴일을 덮는 휴가 이름이 서로 다른 두 사람과 맞거나, 현재 채널 사람으로 보이는 이름의 기간을 확정하지
/// 못했으면 "휴가자 없음"으로 속이지 않고 <see cref="LeaveLookupException"/>(알 수 없음)을 던진다.</item>
/// </list>
/// 표시 이름은 DB의 표시 이름(비어 있으면 실명) 원문이다. 멘션으로 바꾸지 않는다.
/// </summary>
public static class LeaveDisplayResolver
{
    /// <param name="members">현재 채널의 활성 사람 구성원. 호출자가 이미 걸러 넘긴다.</param>
    /// <exception cref="LeaveLookupException">이름이나 날짜가 모호해 휴가 여부를 확정할 수 없을 때.</exception>
    public static LeaveRosterSnapshot Resolve(
        IReadOnlyList<RosterPerson> members,
        IEnumerable<LeaveMailParseResult> results,
        DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(results);

        var matcher = new MemberNameMatcher(members);
        var onLeave = new Dictionary<string, RosterPerson>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            // 휴가 양식인데 해석하지 못한 메일이 있으면 그 안에 누가 있는지 알 수 없다(이 채널 사람일 수 있다).
            if (result.HasUnresolvedContent)
                throw new LeaveLookupException(LeaveLookupFailure.MailUnparsed);

            foreach (var period in result.Periods)
            {
                if (period.EndDate < period.StartDate)
                {
                    // 끝이 시작보다 빠른 기간은 날짜를 믿을 수 없다. 채널 사람이면 모른다고 해야 한다.
                    if (matcher.Match(period.PersonName).Status != MemberNameMatchStatus.None)
                        throw new LeaveLookupException(LeaveLookupFailure.DatesUnresolved);

                    continue;
                }

                if (date < period.StartDate || date > period.EndDate)
                    continue;

                var match = matcher.Match(period.PersonName);
                switch (match.Status)
                {
                    case MemberNameMatchStatus.Ambiguous:
                        throw new LeaveLookupException(LeaveLookupFailure.NameAmbiguous);
                    case MemberNameMatchStatus.Unique:
                        onLeave.TryAdd(match.Member!.SlackUserId, match.Member);
                        break;
                }
            }

            foreach (var name in result.UnresolvedNames)
            {
                if (matcher.Match(name).Status != MemberNameMatchStatus.None)
                    throw new LeaveLookupException(LeaveLookupFailure.DatesUnresolved);
            }
        }

        var names = new List<string>(onLeave.Count);
        foreach (var person in onLeave.Values)
        {
            var alias = !string.IsNullOrWhiteSpace(person.DisplayName) ? person.DisplayName.Trim() : person.RealName?.Trim();
            if (string.IsNullOrEmpty(alias))
                throw new LeaveLookupException(LeaveLookupFailure.AliasUnusable);

            names.Add(alias);
        }

        names.Sort(StringComparer.Ordinal);
        return new LeaveRosterSnapshot(LeaveRosterStatus.Available, names);
    }
}
