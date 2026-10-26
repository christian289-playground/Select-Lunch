using SelectLunch.Shared.Data;
using SelectLunch.Shared.Leave;
using SelectLunch.Shared.Members;
using SlackNet;

namespace SelectLunch.Slack.Leave;

/// <summary>한 날짜의 휴가자 표시용 결과를 새로 조회한다. 매 호출이 실제 조회이며 캐시는 <see cref="PollLeaveRoster"/>가 맡는다.</summary>
public interface ILeaveRosterLoader
{
    /// <summary>성공하면 <see cref="LeaveRosterStatus.Available"/> 결과이고, 확신할 수 없으면 <see cref="LeaveLookupException"/>이다.</summary>
    Task<LeaveRosterSnapshot> LoadAsync(DateOnly date, CancellationToken ct);
}

/// <summary>
/// 휴가자 조회 한 번. 순서가 곧 계약이다.
/// <list type="number">
/// <item>Slack SDK로 설정 채널의 사람 명단 전체를 모은다. 하나라도 실패하면 여기서 끝낸다(DB를 건드리지 않는다).</item>
/// <item>그 명단을 기존 <see cref="ChannelRosterStore.ApplyChannelRosterAsync"/>로 DB에 반영한다.</item>
/// <item>DB에서 같은 (워크스페이스, 채널)의 <b>활성</b> 구성원을 다시 읽는다 — 이름 대조의 유일한 기준이다.</item>
/// <item>메일 도구로 후보 메일을 받아 회사 파서로 읽고, 메뉴 날짜·현재 채널 명단과 대조해 표시 이름을 만든다.</item>
/// </list>
/// 메일 원문·파싱 결과는 이 메서드의 지역 변수로만 존재하고 어디에도 저장하거나 로그에 쓰지 않는다.
/// DB에 쓰는 것은 기존 채널 명단뿐이다(휴가 엔티티·컬럼은 없다).
/// DbContext를 쥐므로 요청(스코프)마다 만든다 — 싱글톤이 붙들지 않는다.
/// </summary>
public sealed class LeaveRosterLoader : ILeaveRosterLoader
{
    readonly ISlackApiClient _slack;
    readonly LunchDbContext _db;
    readonly string _channelId;
    readonly LeaveMailProvider _provider;
    readonly ILeaveMailReader _reader;
    readonly TimeProvider _clock;

    public LeaveRosterLoader(
        ISlackApiClient slack,
        LunchDbContext db,
        string channelId,
        LeaveMailProvider provider,
        ILeaveMailReader reader,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(slack);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clock);

        _slack = slack;
        _db = db;
        _channelId = channelId;
        _provider = provider;
        _reader = reader;
        _clock = clock;
    }

    public async Task<LeaveRosterSnapshot> LoadAsync(DateOnly date, CancellationToken ct)
    {
        var roster = await new SlackChannelRosterCollector(_slack, _channelId).CollectAsync(ct);
        await _db.ApplyChannelRosterAsync(roster, _clock.GetUtcNow(), ct);

        var rows = await _db.GetActiveChannelMembersAsync(roster.WorkspaceId, roster.ChannelId, ct);
        var members = rows
            .Select(m => new RosterPerson(m.SlackUserId, m.RealName, m.DisplayName))
            .ToList();

        // 이 채널에 사람이 없으면 대조할 대상이 없다. 메일을 읽을 이유가 없다.
        if (members.Count == 0)
            return new LeaveRosterSnapshot(LeaveRosterStatus.Available, []);

        var mails = await _reader.ReadAsync(_provider.Keywords, ct);

        var parsed = new List<LeaveMailParseResult>(mails.Count);
        foreach (var mail in mails)
            parsed.Add(_provider.Parser.Parse(mail));

        return LeaveDisplayResolver.Resolve(members, parsed, date);
    }
}
