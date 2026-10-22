using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Members;

/// <summary>채널 명단의 반영과 조회. Slack 패키지를 모르는 순수 DB 로직이다.</summary>
public static class ChannelRosterStore
{
    /// <summary>
    /// 스냅샷을 한 트랜잭션으로 반영한다.
    /// 스냅샷 속 사람은 추가·갱신·재활성화하고, 같은 (워크스페이스, 채널)의 DB 활성 행 중
    /// 스냅샷에 없는 사람만 비활성으로 바꾼다. 다른 채널·워크스페이스 행은 읽지도 않는다.
    /// 행은 절대 지우지 않는다. 도중에 실패하거나 취소되면 아무것도 반영되지 않는다.
    /// </summary>
    public static async Task<RosterSyncResult> ApplyChannelRosterAsync(
        this LunchDbContext db,
        ChannelRoster roster,
        DateTimeOffset syncedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        Validate(roster);
        ct.ThrowIfCancellationRequested();

        var workspaceId = roster.WorkspaceId;
        var channelId = roster.ChannelId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var departed = (await db.ChannelMembers
                .Where(m => m.WorkspaceId == workspaceId && m.ChannelId == channelId)
                .ToListAsync(ct))
            .ToDictionary(m => m.SlackUserId, StringComparer.Ordinal);

        int added = 0, updated = 0, unchanged = 0, deactivated = 0;

        foreach (var person in roster.People)
        {
            if (departed.Remove(person.SlackUserId, out var row))
            {
                var changed = !row.IsActive
                    || !string.Equals(row.RealName, person.RealName, StringComparison.Ordinal)
                    || !string.Equals(row.DisplayName, person.DisplayName, StringComparison.Ordinal);

                row.RealName = person.RealName;
                row.DisplayName = person.DisplayName;
                row.IsActive = true;
                row.LastSyncedAt = syncedAt;

                if (changed) updated++; else unchanged++;
            }
            else
            {
                db.ChannelMembers.Add(new ChannelMember
                {
                    WorkspaceId = workspaceId,
                    ChannelId = channelId,
                    SlackUserId = person.SlackUserId,
                    RealName = person.RealName,
                    DisplayName = person.DisplayName,
                    IsActive = true,
                    LastSyncedAt = syncedAt,
                });
                added++;
            }
        }

        // 남은 것이 이번 스냅샷에 없는 사람이다. 이미 비활성이면 시각도 건드리지 않는다.
        foreach (var gone in departed.Values)
        {
            if (!gone.IsActive)
                continue;

            gone.IsActive = false;
            gone.LastSyncedAt = syncedAt;
            deactivated++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new RosterSyncResult(added, updated, unchanged, deactivated);
    }

    /// <summary>
    /// 활성 구성원만 돌려준다. 두 필터는 선택이고 null/공백이면 걸지 않는다.
    /// 정렬은 (워크스페이스, 채널, 실명, 표시 이름, 사용자 ID) 서수 비교 — 유일 키까지 내려가므로 결정적이다.
    /// 읽기 전용(추적 안 함)이라 호출해도 DB가 바뀌지 않는다.
    /// </summary>
    public static async Task<List<ChannelMember>> GetActiveChannelMembersAsync(
        this LunchDbContext db,
        string? workspaceId,
        string? channelId,
        CancellationToken ct)
    {
        IQueryable<ChannelMember> query = db.ChannelMembers.AsNoTracking().Where(m => m.IsActive);

        if (!string.IsNullOrWhiteSpace(workspaceId))
            query = query.Where(m => m.WorkspaceId == workspaceId);
        if (!string.IsNullOrWhiteSpace(channelId))
            query = query.Where(m => m.ChannelId == channelId);

        var rows = await query.ToListAsync(ct);

        // 서버측 정렬 대신 메모리 정렬 — SQLite 정렬 규칙(BINARY)에 기대지 않고 서수 비교를 명시한다.
        rows.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.WorkspaceId, b.WorkspaceId);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ChannelId, b.ChannelId);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.RealName, b.RealName);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.DisplayName, b.DisplayName);
            if (c != 0) return c;
            return string.CompareOrdinal(a.SlackUserId, b.SlackUserId);
        });

        return rows;
    }

    static void Validate(ChannelRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentException.ThrowIfNullOrWhiteSpace(roster.WorkspaceId, nameof(roster));
        ArgumentException.ThrowIfNullOrWhiteSpace(roster.ChannelId, nameof(roster));
        ArgumentNullException.ThrowIfNull(roster.People, nameof(roster));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var person in roster.People)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(person.SlackUserId, nameof(roster));
            if (!seen.Add(person.SlackUserId))
                throw new ArgumentException($"명단에 같은 사용자 ID가 두 번 있습니다: {person.SlackUserId}", nameof(roster));
        }
    }
}
