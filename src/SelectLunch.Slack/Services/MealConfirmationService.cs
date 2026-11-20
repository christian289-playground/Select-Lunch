using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Slack.Blocks;
using MealRecord = SelectLunch.Shared.Entities.MealRecord;

namespace SelectLunch.Slack.Services;

/// <summary>확정된(또는 확정하려는) 식당 한 곳.</summary>
public sealed record MealPlace(long Id, string Name);

/// <summary>
/// 한 날짜의 식사 기록 상태. <paramref name="Allowed"/>가 false면 <paramref name="Limit"/>은 0이고
/// <paramref name="Recorded"/>는 비어 있다 — 거절된 날짜의 기록은 이 조회로 드러내지 않는다.
/// </summary>
public sealed record MealSelectionState(
    bool Allowed, bool IsToday, int Limit, IReadOnlyList<MealPlace> Recorded);

/// <summary>
/// 저장 결과. 거절이면 <paramref name="Error"/>(사용자에게 보여 줄 문장)만 채워지고 아무것도 쓰이지 않았다.
/// 성공이면 <paramref name="Places"/>가 저장된 그날의 전체 식당(저장 순서)이고,
/// 새 식당을 함께 등록했다면 <paramref name="Registered"/>가 그 식당이다.
/// </summary>
public sealed record MealSaveResult(
    string? Error, IReadOnlyList<MealPlace> Places, Restaurant? Registered, bool IsToday);

/// <summary>
/// 실제로 간 식당의 기록 정책과 저장. 날짜·채널·대상 식당·한도 검사와 쓰기를 한 트랜잭션 안에서 하므로,
/// 확정 창을 연 뒤 상황이 바뀌어도(다른 사람이 먼저 저장, 자정 경과, 설정 변경) 제출 시점의 규칙이 적용된다.
/// 슬랙 타입을 전혀 모른다.
/// </summary>
public sealed class MealConfirmationService(
    LunchDbContext db, LunchService restaurants, string channelId,
    IOptionsMonitor<LunchOptions> options, TimeProvider clock)
{
    sealed record Selection(long Id, string Name, bool Active, bool MatchesRegistration);

    /// <summary>이 서비스가 기록을 다루는 구성 채널. 호출자는 요청이 온 채널과 대조한다.</summary>
    public string ChannelId { get; } = channelId;

    /// <summary>
    /// 날짜가 기록 가능한지와 그날의 한도·기록된 식당을 읽는다. 아무것도 쓰지 않는다.
    /// </summary>
    /// <exception cref="InvalidOperationException">식당 투표 인원 기준 설정이 양수가 아닐 때.</exception>
    public async Task<MealSelectionState> GetStateAsync(DateOnly date, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var today = LunchClock.ToLocalDate(clock.GetUtcNow(), settings.TimeZone);
        var isToday = date == today;

        if (await CheckDateAsync(date, today, settings, ct) is not null)
            return new MealSelectionState(false, isToday, 0, []);

        var limit = await LimitAsync(date, settings.MealParticipantsPerRestaurant, ct);
        return new MealSelectionState(true, isToday, limit, isToday ? await RecordedAsync(date, ct) : []);
    }

    /// <summary>
    /// 그날의 식사 기록 전체를 <paramref name="restaurantIds"/>(와 <paramref name="registration"/>의 새 식당)로 교체한다.
    /// 거절·실패하면 기록도 식당·카테고리 등록도 그대로다. 이미 기록된 식당을 유지한 행은 기록자·시각·출처를 보존한다.
    /// 새 식당은 목록의 맨 앞에 둔다.
    /// </summary>
    /// <exception cref="InvalidOperationException">식당 투표 인원 기준 설정이 양수가 아닐 때.</exception>
    public async Task<MealSaveResult> SaveAsync(
        DateOnly date, IReadOnlyList<long> restaurantIds, string userId, CancellationToken ct,
        RestaurantDraft? registration = null, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(restaurantIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (registration is { EarlyDepartureSeconds: < 0 })
            throw new ArgumentOutOfRangeException(nameof(registration), "미리 출발 시간은 음수일 수 없습니다.");

        // Microsoft.Data.Sqlite의 기본 트랜잭션은 시작과 동시에 쓰기 잠금을 잡는다. 아래 검사는 모두 그 잠금 안에서
        // 읽으므로, 검사와 쓰기 사이에 다른 제출이 끼어들 수 없다. 거절로 반환하면 커밋 없이 닫혀 아무것도 남지 않는다.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = options.CurrentValue;
        var now = clock.GetUtcNow();
        var today = LunchClock.ToLocalDate(now, settings.TimeZone);
        var isToday = date == today;

        MealSaveResult Rejected(string error) => new(error, [], null, isToday);

        if (await CheckDateAsync(date, today, settings, ct) is { } dateError)
            return Rejected(dateError);

        var total = (long)restaurantIds.Count + (registration is null ? 0 : 1);
        if (total == 0)
            return Rejected("식당을 하나 이상 선택해 주세요.");
        var limit = await LimitAsync(date, settings.MealParticipantsPerRestaurant, ct);
        if (total > limit)
            return Rejected(limit == 1
                ? "이 날은 식당을 한 곳만 확정할 수 있습니다."
                : $"이 날은 식당을 최대 {limit}곳까지 확정할 수 있습니다. {total - limit}곳을 줄여 주세요.");

        string? registrationName = null;
        if (registration is not null)
        {
            if (registration.RestaurantId is not null)
                return Rejected("새 식당 등록으로 기존 식당을 수정할 수 없습니다.");
            if (string.IsNullOrWhiteSpace(registration.Name))
                return Rejected("식당 이름을 입력해 주세요.");
            if (!isToday || restaurantIds.Count > 0)
                registrationName = Restaurant.Normalize(registration.Name);
            // 과거 기록에서 동명 식당을 등록하면 일반 등록의 갱신 규칙이 오늘의 출발 시각까지 바꾼다.
            if (!isToday && await db.Restaurants.AnyAsync(r => r.NormalizedName == registrationName, ct))
                return Rejected("이미 등록된 식당입니다. 창을 닫고 기록 요청에서 기존 식당을 선택해 주세요.");
        }

        Dictionary<long, Selection> selected = restaurantIds.Count == 0 ? [] : await db.Restaurants
            .Where(r => restaurantIds.Contains(r.Id))
            .Select(r => new Selection(r.Id, r.Name, r.Status == RestaurantStatus.Active && r.CategoryId != null,
                registrationName != null && r.NormalizedName == registrationName))
            .ToDictionaryAsync(r => r.Id, ct);
        if (selected.Count != restaurantIds.Count)
            return Rejected("선택한 식당이 중복되었거나 더 이상 존재하지 않습니다. 목록에서 다시 골라 주세요.");

        // 지난 요청은 같은 쓰기 잠금 안에서 이미 미기록임을 확인했다.
        Dictionary<long, MealRecord> existing = isToday ? await db.MealRecords
            .Where(m => m.ChannelId == ChannelId && m.Date == date)
            .ToDictionaryAsync(m => m.RestaurantId, ct) : [];
        foreach (var place in selected.Values)
        {
            if (place.MatchesRegistration)
                return Rejected("새로 등록하는 식당이 이미 선택한 식당과 같습니다.");
            if (!place.Active && !existing.ContainsKey(place.Id))
                return Rejected("기록할 수 없는 식당이 선택되었습니다. 목록에서 다시 선택해 주세요.");
        }

        // 여기서부터 쓴다. 중간에 예외가 나면 트랜잭션이 되돌려지고, 되돌려진 내용을 추적 중인 변경 추적기는 비운다.
        try
        {
            var registered = registration is null ? null
                : await restaurants.SaveRestaurantAsync(registration, userId, ct, displayName);
            foreach (var (id, row) in existing)
            {
                if (!selected.ContainsKey(id) && id != registered?.Id)
                    db.MealRecords.Remove(row);
            }

            var places = new MealPlace[(int)total];
            var next = 0;
            if (registered is not null)
                Include(registered.Id, registered.Name, MealSource.NewRegistration);
            for (var i = 0; i < restaurantIds.Count; i++)
            {
                var place = selected[restaurantIds[i]];
                Include(place.Id, place.Name, MealSource.Prompt);
            }

            void Include(long id, string name, MealSource source)
            {
                places[next++] = new MealPlace(id, name);
                if (!existing.ContainsKey(id))
                    db.MealRecords.Add(new MealRecord
                    {
                        ChannelId = ChannelId, Date = date, RestaurantId = id,
                        RecordedBySlackUserId = userId, RecordedAt = now, Source = source,
                    });
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new MealSaveResult(null, places, registered, isToday);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// 날짜 규칙. 오늘은 항상 가능하고 미래는 안 된다. 과거는 운영이 지정한 한 날짜(<see cref="LunchOptions.CarryoverMealPromptDate"/>)에
    /// 한해, 구성 채널에 기록 요청을 게시한 이력이 있고 그날 기록이 아직 없을 때만 최초 한 번 허용한다.
    /// 허용이면 null, 거절이면 사용자에게 보여 줄 문장이다.
    /// </summary>
    async Task<string?> CheckDateAsync(
        DateOnly date, DateOnly today, LunchOptions settings, CancellationToken ct)
    {
        if (date == today)
            return null;
        if (date > today)
            return $"{Format(date)} 은(는) 아직 오지 않은 날짜라 식사를 기록할 수 없습니다.";
        if (settings.CarryoverMealPromptDate != date)
            return $"{Format(date)} 은(는) 지난 날짜라 식사 기록을 바꿀 수 없습니다. " +
                $"{Format(today)} 날짜의 식사 기록 메시지를 사용해 주세요.";

        var posted = await db.ChannelDays.AsNoTracking().AnyAsync(
            d => d.ChannelId == ChannelId && d.Date == date && d.MealPromptPostedAt != null, ct);
        if (!posted)
            return $"{Format(date)} 은(는) 식사 기록 요청을 게시한 이력이 없어 기록할 수 없습니다.";

        return await db.MealRecords.AsNoTracking().AnyAsync(m => m.ChannelId == ChannelId && m.Date == date, ct)
            ? $"{Format(date)} 식사는 이미 기록되어 지난 날짜의 기록은 다시 바꿀 수 없습니다."
            : null;
    }

    /// <summary>
    /// 확정 가능한 최대 식당 수. 같은 채널·날짜의 마감된 본투표에서 식당을 고른 사람 수로 센다 —
    /// 비밀·랜덤·사전표에서 넘어온 표도 한 사람씩이고, 기권·미응답과 사전투표 행은 세지 않는다.
    /// </summary>
    async Task<int> LimitAsync(DateOnly date, int serverDefault, CancellationToken ct)
    {
        var votes = db.PollVotes.Where(v => v.RestaurantId != null && v.Poll!.ChannelId == ChannelId
            && v.Poll.Date == date && v.Poll.Status == PollStatus.Closed);
        var rule = await db.EffectiveMealSize(ChannelId, serverDefault)
            .Select(size => new { Size = size, Voters = votes.Count() }).SingleAsync(ct);
        if (rule.Size <= 0)
            throw new InvalidOperationException($"식당 한 곳당 기준 인원은 1 이상이어야 합니다 (현재 {rule.Size}).");
        return rule.Voters == 0 ? 1 : 1 + (rule.Voters - 1) / rule.Size;
    }

    Task<List<MealPlace>> RecordedAsync(DateOnly date, CancellationToken ct) =>
        db.MealRecords.AsNoTracking()
            .Where(m => m.ChannelId == ChannelId && m.Date == date)
            .OrderBy(m => m.Id)
            .Select(m => new MealPlace(m.RestaurantId, m.Restaurant!.Name))
            .ToListAsync(ct);

    static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
