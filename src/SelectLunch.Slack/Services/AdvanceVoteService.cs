using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Slack.Services;

/// <summary>사전투표 접수 가능 여부와 저장·취소 결과.</summary>
public enum AdvanceVoteStatus
{
    /// <summary>지금 접수·변경·취소할 수 있다.</summary>
    Ready,

    /// <summary>표(또는 기권)를 저장했다.</summary>
    Saved,

    /// <summary>사전투표를 취소했다.</summary>
    Cancelled,

    /// <summary>현재 사전투표 대상이 아닌 날짜다.</summary>
    NotTargetDate,

    /// <summary>주말·공휴일이라 투표가 열리지 않는 날이다.</summary>
    NonBusinessDay,

    /// <summary>정규 투표가 시작되어 조회만 가능하다.</summary>
    VotingStarted,

    /// <summary>존재하지 않거나 Active가 아닌 식당이다.</summary>
    InvalidRestaurant,

    Unchanged,
    LimitReached,
    Conflict,
}

public enum AdvanceVoteChangeKind { Selected, Abstained, Cancelled }

public sealed record AdvanceVoteTally(long RestaurantId, string Name, int Count);
public sealed record AdvanceVoteSnapshot(DateOnly Date, IReadOnlyList<AdvanceVoteTally> Tallies, int Abstentions);
public sealed record AdvanceVoteChange(AdvanceVoteChangeKind Kind, string? RestaurantName, DateTimeOffset ChangedAt, AdvanceVoteSnapshot Snapshot);
public readonly record struct AdvanceVoteResult(AdvanceVoteStatus Status, AdvanceVoteChange? Change = null);

/// <summary>회차별 사전투표와 변경 한도를 저장하고 본투표 변경으로 무효화된 표는 집계에서 제외한다.</summary>
public sealed class AdvanceVoteService(
    LunchDbContext db, string channelId, IOptionsMonitor<LunchOptions> options, TimeProvider timeProvider)
{
    public Task<DateOnly> GetTargetDateAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        return NextPollDateAsync(settings, LunchClock.NowIn(settings.TimeZone, timeProvider), ct);
    }

    public Task<AdvanceVoteStatus> GetStatusAsync(DateOnly date, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        return CheckGateAsync(date, settings, LunchClock.NowIn(settings.TimeZone, timeProvider), ct);
    }

    public Task<AdvanceVote?> FindAsync(DateOnly date, string userId, CancellationToken ct) =>
        db.AdvanceVotes.AsNoTracking()
            .Include(v => v.Restaurant)
            .SingleOrDefaultAsync(
                v => v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId, ct);

    public Task<AdvanceVoteResult> CastAsync(
        DateOnly date, string userId, long? restaurantId, int expectedChangeCount, CancellationToken ct) =>
        ChangeAsync(date, userId, restaurantId, false, expectedChangeCount, ct);

    public Task<AdvanceVoteResult> CancelAsync(DateOnly date, string userId, int expectedChangeCount, CancellationToken ct) =>
        ChangeAsync(date, userId, null, true, expectedChangeCount, ct);

    public async Task<AdvanceVoteSnapshot> GetSnapshotAsync(DateOnly date, CancellationToken ct)
    {
        // 식당표와 기권을 같은 SELECT로 읽어 동시 변경 중 서로 다른 시점의 수를 합치지 않는다.
        var query = db.AdvanceVotes.AsNoTracking()
            .Where(v => v.ChannelId == channelId && v.Date == date && !v.IsCancelled && !v.IsSupersededByMainVote
                && (v.RestaurantId == null || v.Restaurant != null
                    && v.Restaurant.Status == RestaurantStatus.Active && v.Restaurant.CategoryId != null))
            .GroupBy(v => new { v.RestaurantId, Name = v.Restaurant == null ? null : v.Restaurant.Name })
            .Select(g => new ValueTuple<long?, string?, int>(g.Key.RestaurantId, g.Key.Name, g.Count()));
        var groups = new List<AdvanceVoteTally>();
        var abstentions = 0;
        await foreach (var (restaurantId, name, count) in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (restaurantId is { } id)
                groups.Add(new(id, name!, count));
            else
                abstentions = count;
        }
        groups.Sort(static (left, right) =>
        {
            var nameOrder = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return nameOrder != 0 ? nameOrder : left.RestaurantId.CompareTo(right.RestaurantId);
        });
        return new(date, groups, abstentions);
    }

    async Task<AdvanceVoteResult> ChangeAsync(
        DateOnly date, string userId, long? restaurantId, bool cancel, int expectedChangeCount, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        // Microsoft.Data.Sqlite의 기본 트랜잭션은 비지연(IMMEDIATE) 쓰기 잠금을 획득한다.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = LunchClock.NowIn(settings.TimeZone, timeProvider);
        var gate = await CheckGateAsync(date, settings, now, ct);
        if (gate != AdvanceVoteStatus.Ready)
            return new(gate);

        string? restaurantName = null;
        if (!cancel && restaurantId is { } id)
        {
            restaurantName = await db.Restaurants.AsNoTracking()
                .Where(r => r.Id == id && r.Status == RestaurantStatus.Active && r.CategoryId != null)
                .Select(r => r.Name).SingleOrDefaultAsync(ct);
            if (restaurantName is null)
                return new(AdvanceVoteStatus.InvalidRestaurant);
        }

        var existing = await db.AdvanceVotes.AsNoTracking().SingleOrDefaultAsync(
            v => v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId, ct);
        if (existing is { IsSupersededByMainVote: true })
            return new(AdvanceVoteStatus.VotingStarted);
        if (cancel ? existing is null || existing.IsCancelled
                   : existing is not null && !existing.IsCancelled && existing.RestaurantId == restaurantId)
            return new(AdvanceVoteStatus.Unchanged);
        if (existing?.ChangeCount >= AdvanceVote.MaxChanges)
            return new(AdvanceVoteStatus.LimitReached);
        if ((existing?.ChangeCount ?? 0) != expectedChangeCount)
            return new(AdvanceVoteStatus.Conflict);

        var utcNow = now.ToUniversalTime();
        if (existing is null)
        {
            var inserted = new AdvanceVote
            {
                ChannelId = channelId, Date = date, SlackUserId = userId,
                RestaurantId = restaurantId, VotedAt = utcNow, ChangeCount = 1,
            };
            db.AdvanceVotes.Add(inserted);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                db.Entry(inserted).State = EntityState.Detached;
            }
        }
        else
        {
            var updated = await db.AdvanceVotes
                .Where(v => v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId
                    && !v.IsSupersededByMainVote
                    && v.ChangeCount == expectedChangeCount && v.ChangeCount < AdvanceVote.MaxChanges)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(v => v.ChangeCount, v => v.ChangeCount + 1)
                    .SetProperty(v => v.RestaurantId, restaurantId)
                    .SetProperty(v => v.IsCancelled, cancel)
                    .SetProperty(v => v.VotedAt, utcNow), ct);
            if (updated != 1)
                return new(AdvanceVoteStatus.Conflict);
        }

        var snapshot = await GetSnapshotAsync(date, ct);
        await transaction.CommitAsync(ct);
        var kind = cancel ? AdvanceVoteChangeKind.Cancelled
            : restaurantId is null ? AdvanceVoteChangeKind.Abstained : AdvanceVoteChangeKind.Selected;
        return new(cancel ? AdvanceVoteStatus.Cancelled : AdvanceVoteStatus.Saved, new(kind, restaurantName, now, snapshot));
    }

    async Task<DateOnly> NextPollDateAsync(LunchOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(now.DateTime);
        var isBusinessDay = LunchSchedule.IsBusinessDay(date, settings);
        var pollStatus = isBusinessDay
            ? await db.Polls.AsNoTracking().Where(p => p.ChannelId == channelId && p.Date == date)
                .Select(p => (PollStatus?)p.Status).SingleOrDefaultAsync(ct)
            : null;
        if (!isBusinessDay || pollStatus == PollStatus.Closed
            || (pollStatus is null && TimeOnly.FromDateTime(now.DateTime) >= settings.VoteCloseAt))
        {
            do
            {
                date = date.AddDays(1);
            }
            while (!LunchSchedule.IsBusinessDay(date, settings));
        }
        return date;
    }

    async Task<AdvanceVoteStatus> CheckGateAsync(
        DateOnly date, LunchOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        if (date < today)
            return AdvanceVoteStatus.NotTargetDate;
        if (!LunchSchedule.IsBusinessDay(date, settings))
            return AdvanceVoteStatus.NonBusinessDay;
        if (date != await NextPollDateAsync(settings, now, ct))
            return AdvanceVoteStatus.NotTargetDate;
        if ((date == today && TimeOnly.FromDateTime(now.DateTime) >= settings.VoteOpenAt)
            || await db.Polls.AsNoTracking().AnyAsync(p => p.ChannelId == channelId && p.Date == date, ct))
            return AdvanceVoteStatus.VotingStarted;
        return AdvanceVoteStatus.Ready;
    }
}
