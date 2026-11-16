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

    /// <summary>사전투표를 취소했다. 취소할 행이 없었어도 같다.</summary>
    Cancelled,

    /// <summary>현지 오늘이 아닌 날짜다.</summary>
    NotToday,

    /// <summary>주말·공휴일이라 투표가 열리지 않는 날이다.</summary>
    NonBusinessDay,

    /// <summary>정규 투표 개시 시각이 지났거나 그날 투표가 이미 만들어졌다.</summary>
    VotingStarted,

    /// <summary>존재하지 않거나 Active가 아닌 식당이다.</summary>
    InvalidRestaurant,
}

/// <summary>
/// 당일 영업일 00:00부터 정규 투표 개시 전까지 받는 사전투표의 저장·조회·변경·취소.
/// 슬랙 타입을 모른다. 쓰기는 모두 SQLite 쓰기 트랜잭션 안에서 게이트를 다시 검사해,
/// 같은 DB에서 도는 <see cref="LunchService.OpenPollAsync"/>(개시 때 사전투표를 일반 표로 옮긴다)와
/// 직렬화한다 — 개시 직전에 통과한 접수가 옮겨진 뒤에 남거나 개시 후에 새로 생기지 않는다.
/// </summary>
public sealed class AdvanceVoteService(
    LunchDbContext db, string channelId, IOptionsMonitor<LunchOptions> options, TimeProvider timeProvider)
{
    /// <summary>
    /// 로컬 오늘이 <paramref name="date"/>이고 영업일이며 개시 시각 전이고 그 채널·날짜의 투표가 아직 없으면
    /// <see cref="AdvanceVoteStatus.Ready"/>. 아니면 처음 걸린 사유를 돌려준다.
    /// </summary>
    public Task<AdvanceVoteStatus> GetStatusAsync(DateOnly date, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        return CheckGateAsync(date, settings, LunchClock.NowIn(settings.TimeZone, timeProvider), ct);
    }

    /// <summary>본인 표 조회. 식당 이름을 함께 읽고, 읽기 전용이라 추적하지 않는다.</summary>
    public Task<AdvanceVote?> FindAsync(DateOnly date, string userId, CancellationToken ct) =>
        db.AdvanceVotes.AsNoTracking()
            .Include(v => v.Restaurant)
            .SingleOrDefaultAsync(
                v => v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId, ct);

    /// <summary>
    /// 접수·변경. <paramref name="restaurantId"/>가 null이면 기권("따로 먹어요")이다.
    /// 식당은 Active이고 카테고리가 있어야 한다 — 그렇지 않으면 기존 표를 그대로 두고
    /// <see cref="AdvanceVoteStatus.InvalidRestaurant"/>를 돌려준다.
    /// </summary>
    public async Task<AdvanceVoteStatus> CastAsync(
        DateOnly date, string userId, long? restaurantId, CancellationToken ct)
    {
        var settings = options.CurrentValue;

        // 쓰기 잠금을 먼저 잡은 뒤 시각과 개시 여부를 읽는다. 잠금을 기다리는 동안 개시 시각이 지나도 통과하지 못한다.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = LunchClock.NowIn(settings.TimeZone, timeProvider);
        var gate = await CheckGateAsync(date, settings, now, ct);
        if (gate != AdvanceVoteStatus.Ready)
            return gate;

        if (restaurantId is { } id
            && !await db.Restaurants.AnyAsync(
                r => r.Id == id && r.Status == RestaurantStatus.Active && r.CategoryId != null, ct))
            return AdvanceVoteStatus.InvalidRestaurant;

        var existing = await db.AdvanceVotes.SingleOrDefaultAsync(
            v => v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId, ct);

        if (existing is null)
        {
            db.AdvanceVotes.Add(new AdvanceVote
            {
                ChannelId = channelId, Date = date, SlackUserId = userId,
                RestaurantId = restaurantId, VotedAt = now.ToUniversalTime(),
            });
        }
        else
        {
            existing.RestaurantId = restaurantId;
            existing.VotedAt = now.ToUniversalTime();
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AdvanceVoteStatus.Saved;
    }

    /// <summary>본인 행만 지운다. 지울 행이 없어도 <see cref="AdvanceVoteStatus.Cancelled"/>다 — 취소는 멱등이다.</summary>
    public async Task<AdvanceVoteStatus> CancelAsync(DateOnly date, string userId, CancellationToken ct)
    {
        var settings = options.CurrentValue;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var gate = await CheckGateAsync(date, settings, LunchClock.NowIn(settings.TimeZone, timeProvider), ct);
        if (gate != AdvanceVoteStatus.Ready)
            return gate;

        var existing = await db.AdvanceVotes.SingleOrDefaultAsync(
            v => v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId, ct);

        if (existing is not null)
        {
            db.AdvanceVotes.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return AdvanceVoteStatus.Cancelled;
    }

    /// <summary>
    /// 날짜(로컬 오늘) → 영업일 → 개시 시각 → 기존 투표 순으로 본다. 개시 시각은 정각부터 닫힌다(<c>now &gt;= VoteOpenAt</c>) —
    /// 스케줄러가 같은 시각부터 투표를 여는 것과 경계를 맞춘다.
    /// </summary>
    async Task<AdvanceVoteStatus> CheckGateAsync(
        DateOnly date, LunchOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        if (date != DateOnly.FromDateTime(now.DateTime))
            return AdvanceVoteStatus.NotToday;

        if (!LunchSchedule.IsBusinessDay(date, settings))
            return AdvanceVoteStatus.NonBusinessDay;

        if (TimeOnly.FromDateTime(now.DateTime) >= settings.VoteOpenAt)
            return AdvanceVoteStatus.VotingStarted;

        return await db.Polls.AnyAsync(p => p.ChannelId == channelId && p.Date == date, ct)
            ? AdvanceVoteStatus.VotingStarted
            : AdvanceVoteStatus.Ready;
    }
}
