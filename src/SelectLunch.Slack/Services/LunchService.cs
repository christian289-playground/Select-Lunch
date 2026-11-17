using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Services;

/// <summary>
/// 비밀 투표 접수증. <see cref="Revision"/>은 접수한 표의 <c>VotedAt.UtcTicks</c>로, 확인 버튼이
/// 가리키는 표가 아직 그 표인지 가르는 데만 쓴다 — 식당 정보는 담지 않는다.
/// </summary>
public readonly record struct SecretVoteReceipt(long Revision);

/// <summary>랜덤 투표·테스트 모드가 고른 식당. 안내 문구에 쓸 이름과 카테고리만 담는다.</summary>
public sealed record PickedRestaurant(long RestaurantId, string Name, string CategoryName);

/// <summary>
/// DB를 바꾸는 동작을 모은다. 슬랙 타입을 전혀 모른다.
/// <paramref name="random"/>은 랜덤 투표에만 쓴다(테스트에서 고정 시드를 주입) — 기본은 <see cref="Random.Shared"/>.
/// </summary>
public sealed class LunchService(LunchDbContext db, string channelId, Random? random = null)
{
    readonly Random rng = random ?? Random.Shared;

    /// <summary>
    /// 풀과 후보를 한 트랜잭션으로 커밋한다. 나눠서 커밋하면 두 번째
    /// SaveChangesAsync가 실패했을 때 후보 없는 Open 풀만 남고, 스케줄러는
    /// "그 날 이미 처리됨"으로 보고 다시 열지 않는다 — 그 상태를 막는다.
    /// </summary>
    public async Task<LunchPoll> OpenPollAsync(
        DateOnly date, DateTimeOffset opensAt, DateTimeOffset closesAt, CancellationToken ct)
    {
        var poll = new LunchPoll
        {
            ChannelId = channelId, Date = date,
            OpensAt = opensAt, ClosesAt = closesAt, Status = PollStatus.Open,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Polls.Add(poll);
        await db.SaveChangesAsync(ct);

        var candidates = await db.GetActiveRestaurantsAsync(ct);
        var order = 0;
        foreach (var candidate in candidates.OrderBy(c => c.CategoryName, StringComparer.Ordinal)
                                            .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            db.PollCandidates.Add(new PollCandidate
            {
                PollId = poll.Id, RestaurantId = candidate.RestaurantId, DisplayOrder = order++,
            });
        }

        await db.SaveChangesAsync(ct);

        var advanceVotes = await db.AdvanceVotes.AsNoTracking()
            .Where(v => v.ChannelId == channelId && v.Date == date
                && !v.IsCancelled && !v.IsSupersededByMainVote
                && (v.RestaurantId == null || db.PollCandidates.Any(c =>
                    c.PollId == poll.Id && c.RestaurantId == v.RestaurantId)))
            .Select(v => new { v.SlackUserId, v.RestaurantId, v.VotedAt })
            .ToListAsync(ct);
        foreach (var vote in advanceVotes)
            db.PollVotes.Add(new PollVote
            {
                PollId = poll.Id, SlackUserId = vote.SlackUserId,
                RestaurantId = vote.RestaurantId, VotedAt = vote.VotedAt, IsFromAdvanceVote = true,
            });

        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        return poll;
    }

    /// <summary>1인 1표. 이미 투표했으면 대상만 바꾼다. 마감된 풀은 조용히 무시한다 —
    /// 며칠 지난 메시지의 버튼을 눌러도 집계가 바뀌면 안 된다.</summary>
    public Task CastVoteAsync(long pollId, string slackUserId, long restaurantId, CancellationToken ct) =>
        CastPublicVoteAsync(pollId, slackUserId, restaurantId, ct);

    /// <summary>
    /// "나 오늘 따로 먹어요". 무응답과 동일하게 취급되며 알고리즘 어디에도 영향을 주지
    /// 않는다 — RestaurantId를 null로 기록해 둘 뿐이다. CastVoteAsync와 같은 행을
    /// 공유하므로(1인 1의사표시) 투표↔기권 전환은 이 upsert 하나로 끝난다.
    /// </summary>
    public Task CastAbstentionAsync(long pollId, string slackUserId, CancellationToken ct) =>
        CastPublicVoteAsync(pollId, slackUserId, null, ct);

    async Task CastPublicVoteAsync(long pollId, string userId, long? restaurantId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var date = await OpenDateAsync(pollId, ct);
        if (date is null)
            return;
        await SaveVoteInTransactionAsync(pollId, date.Value, userId, restaurantId, false, ct);
        await transaction.CommitAsync(ct);
    }

    // 호출자가 쓰기 트랜잭션을 보유한다. 상태 조회부터 사전표 무효화·표 저장까지 같은 경계다.
    async Task<PollVote> SaveVoteInTransactionAsync(
        long pollId, DateOnly date, string userId, long? restaurantId, bool secret, CancellationToken ct)
    {
        var vote = await db.PollVotes.SingleOrDefaultAsync(
            v => v.PollId == pollId && v.SlackUserId == userId, ct);
        if (vote is not null)
            await db.Entry(vote).ReloadAsync(ct);
        var changed = vote is null || vote.RestaurantId != restaurantId || vote.IsSecret != secret;
        if (!changed && !secret)
            return vote!;
        if (vote is null)
        {
            vote = new PollVote { PollId = pollId, SlackUserId = userId };
            db.PollVotes.Add(vote);
        }
        if (changed)
        {
            var advance = await db.AdvanceVotes.SingleOrDefaultAsync(v =>
                v.ChannelId == channelId && v.Date == date && v.SlackUserId == userId
                && !v.IsCancelled && !v.IsSupersededByMainVote, ct);
            if (advance is not null)
            {
                await db.Entry(advance).ReloadAsync(ct);
                if (!advance.IsCancelled && !advance.IsSupersededByMainVote)
                    advance.IsSupersededByMainVote = true;
            }
            vote.IsFromAdvanceVote = false;
        }
        var now = DateTimeOffset.UtcNow;
        if (secret && now <= vote.VotedAt)
            now = vote.VotedAt.AddTicks(1);
        vote.RestaurantId = restaurantId;
        vote.IsSecret = secret;
        vote.VotedAt = now;
        await db.SaveChangesAsync(ct);
        return vote;
    }

    Task<DateOnly?> OpenDateAsync(long pollId, CancellationToken ct) =>
        db.Polls.Where(p => p.Id == pollId && p.ChannelId == channelId && p.Status == PollStatus.Open)
            .Select(p => (DateOnly?)p.Date).SingleOrDefaultAsync(ct);

    /// <summary>
    /// 랜덤 투표. 이 풀의 후보(스냅샷) 중 하나를 균등 확률로 골라 그 식당에 투표한다.
    /// <paramref name="categoryId"/>가 있으면 그 카테고리 후보 안에서만 고른다.
    /// 마감됐거나 고를 후보가 없으면 <c>null</c> — 표는 기록하지 않는다.
    ///
    /// 추천 알고리즘은 랜덤을 쓰지 않는다. 이쪽은 사용자가 명시적으로 요청한 "아무거나" 기능이다.
    /// </summary>
    public async Task<PickedRestaurant?> CastRandomVoteAsync(
        long pollId, string slackUserId, long? categoryId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var date = await OpenDateAsync(pollId, ct);
        if (date is null)
            return null;

        var pool = await db.PollCandidates
            .Where(c => c.PollId == pollId
                        && (categoryId == null || c.Restaurant!.CategoryId == categoryId))
            .OrderBy(c => c.DisplayOrder)
            .Select(c => new PickedRestaurant(
                c.RestaurantId, c.Restaurant!.Name,
                c.Restaurant!.Category != null ? c.Restaurant!.Category!.Name : "미분류"))
            .ToListAsync(ct);

        if (pool.Count == 0)
            return null;

        var picked = pool[rng.Next(pool.Count)];
        await SaveVoteInTransactionAsync(pollId, date.Value, slackUserId, picked.RestaurantId, false, ct);
        await transaction.CommitAsync(ct);
        return picked;
    }

    /// <summary>
    /// 후보를 지금 추첨해 비밀 표로 저장하고 그 표의 접수증(revision)을 돌려준다. 식당별 집계에는 마감 시 반영한다.
    /// 마감됐거나 고를 후보가 없으면 <c>null</c> — 표는 기록하지 않는다.
    /// 어느 식당인지는 돌려주지 않는다. 본인이 확인을 요청하면 <see cref="FindSecretVoteAsync"/>가 읽어 준다.
    /// </summary>
    public async Task<SecretVoteReceipt?> CastSecretRandomVoteAsync(
        long pollId, string slackUserId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var date = await OpenDateAsync(pollId, ct);
        if (date is null)
            return null;

        var candidates = await db.PollCandidates
            .Where(c => c.PollId == pollId)
            .OrderBy(c => c.DisplayOrder)
            .Select(c => c.RestaurantId)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return null;

        var vote = await SaveVoteInTransactionAsync(
            pollId, date.Value, slackUserId, candidates[rng.Next(candidates.Count)], true, ct);
        await transaction.CommitAsync(ct);
        return new SecretVoteReceipt(vote.VotedAt.UtcTicks);
    }

    /// <summary>
    /// 본인의 비밀 표를 읽는다. 쓰지도 다시 뽑지도 않으며 마감 뒤에도 읽힌다.
    /// 그 사람의 표가 아직 같은 비밀 표(<paramref name="revision"/> 일치)일 때만 식당을 돌려주고,
    /// 다른 선택으로 바뀌었거나 다시 뽑았거나 남의 표면 <c>null</c>이다.
    /// <c>UtcTicks</c> 비교는 한 행을 읽은 뒤 메모리에서 처리해 SQLite 쿼리 변환에 의존하지 않는다.
    /// </summary>
    public async Task<PickedRestaurant?> FindSecretVoteAsync(
        long pollId, string slackUserId, long revision, CancellationToken ct)
    {
        var vote = await db.PollVotes
            .AsNoTracking()
            .Where(v => v.PollId == pollId && v.SlackUserId == slackUserId
                        && v.IsSecret && v.RestaurantId != null)
            .Select(v => new { v.RestaurantId, v.VotedAt })
            .SingleOrDefaultAsync(ct);

        return vote is { RestaurantId: { } restaurantId } && vote.VotedAt.UtcTicks == revision
            ? await FindRestaurantAsync(restaurantId, ct)
            : null;
    }

    public Task<int> GetSecretVoteCountAsync(long pollId, CancellationToken ct) =>
        db.PollVotes.CountAsync(v => v.PollId == pollId && v.IsSecret
            && v.RestaurantId != null && v.Poll!.Status == PollStatus.Open, ct);

    /// <summary>
    /// 테스트 모드용 랜덤 선택. 표를 기록하지 않고 현재 Active 식당(카테고리 지정 시 그 안)에서만 고른다.
    /// </summary>
    public async Task<PickedRestaurant?> PickRandomActiveAsync(long? categoryId, CancellationToken ct)
    {
        var pool = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Active && r.CategoryId != null
                        && (categoryId == null || r.CategoryId == categoryId))
            .OrderBy(r => r.Id)
            .Select(r => new PickedRestaurant(r.Id, r.Name, r.Category!.Name))
            .ToListAsync(ct);

        return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
    }

    /// <summary>테스트 모드가 "이 식당을 골랐다"고 보여주기 위한 이름 조회. 없으면 <c>null</c>.</summary>
    public async Task<PickedRestaurant?> FindRestaurantAsync(long restaurantId, CancellationToken ct) =>
        await db.Restaurants
            .Where(r => r.Id == restaurantId)
            .Select(r => new PickedRestaurant(
                r.Id, r.Name, r.Category != null ? r.Category!.Name : "미분류"))
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// 열린 투표에 식당을 후보로 얹는다. 메시지 게시를 기다리는 동안 등록·승격된
    /// 식당도 실제 게시할 후보에 들어가야 한다. Active 식당만 대상이며,
    /// 열린 투표가 없거나 이미 후보면 <c>null</c>(변경 없음)을 돌려준다.
    /// 후보를 추가하면 풀 id를 돌려준다 — 게시된 메시지가 있으면 호출자가 갱신한다.
    /// </summary>
    public async Task<long?> AddCandidateToOpenPollAsync(long restaurantId, CancellationToken ct)
    {
        var poll = await db.Polls
            .SingleOrDefaultAsync(p => p.ChannelId == channelId && p.Status == PollStatus.Open, ct);
        if (poll is null)
            return null;

        var restaurant = await db.Restaurants.SingleAsync(r => r.Id == restaurantId, ct);
        if (restaurant.Status != RestaurantStatus.Active)
            return null;

        var candidates = db.PollCandidates.Where(c => c.PollId == poll.Id);
        if (await candidates.AnyAsync(c => c.RestaurantId == restaurantId, ct))
            return null;

        var nextOrder = await candidates.AnyAsync(ct)
            ? await candidates.MaxAsync(c => c.DisplayOrder, ct) + 1
            : 0;
        db.PollCandidates.Add(new PollCandidate
        {
            PollId = poll.Id, RestaurantId = restaurantId, DisplayOrder = nextOrder,
        });
        await db.SaveChangesAsync(ct);

        return poll.Id;
    }

    /// <summary>기권을 제외한다. 열린 투표의 비밀 표는 식당별 득표에서 숨긴다.</summary>
    public Task<IReadOnlyList<VoteTally>> GetTalliesAsync(long pollId, CancellationToken ct) =>
        GetTalliesCoreAsync(pollId, includeSecret: false, ct);

    async Task<IReadOnlyList<VoteTally>> GetTalliesCoreAsync(
        long pollId, bool includeSecret, CancellationToken ct) =>
        await db.PollVotes
            .Where(v => v.PollId == pollId && v.RestaurantId != null
                && (includeSecret || !v.IsSecret || v.Poll!.Status == PollStatus.Closed))
            .GroupBy(v => new { v.RestaurantId, v.Restaurant!.Name, v.Restaurant!.DepartureTime })
            .Select(g => new VoteTally(g.Key.RestaurantId!.Value, g.Key.Name, g.Count(), g.Key.DepartureTime))
            .ToListAsync(ct);

    /// <summary>사전 기권은 사용자 ID 없이 인원수로만 반환한다.</summary>
    public async Task<AbstentionSummary> GetAbstainersAsync(long pollId, CancellationToken ct)
    {
        var abstainers = await db.PollVotes.AsNoTracking()
            .Where(v => v.PollId == pollId && v.RestaurantId == null)
            .Select(v => new { UserId = v.IsFromAdvanceVote ? null : v.SlackUserId, v.VotedAt })
            .ToListAsync(ct);
        abstainers.Sort((a, b) =>
        {
            var comparison = a.VotedAt.CompareTo(b.VotedAt);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.UserId, b.UserId);
        });
        var named = new List<string>(abstainers.Count);
        var anonymous = 0;
        foreach (var abstainer in abstainers)
        {
            if (abstainer.UserId is { } userId)
                named.Add(userId);
            else
                anonymous++;
        }
        return new AbstentionSummary(named, anonymous);
    }

    /// <summary>
    /// 마감하고 결과를 낸다. 투표 동점은 추천 점수가 높은 쪽으로 푼다 —
    /// 랜덤을 쓰지 않으면서 결정적으로 해소하는 방법이다.
    /// </summary>
    public async Task<PollOutcome> ClosePollAsync(
        long pollId, DateOnly today, RecommendationOptions options, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        await db.Entry(poll).ReloadAsync(ct);
        if (poll.Status == PollStatus.Closed)
            return await GetClosedOutcomeAsync(pollId, ct);

        var tallies = await GetTalliesCoreAsync(pollId, includeSecret: true, ct);
        var abstainers = await GetAbstainersAsync(pollId, ct);

        var stats = await db.GetCategoryStatsAsync(today, ct);
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        var recommendation = RecommendationEngine.Recommend(today, stats, restaurants, options);

        var scoreByRestaurant = ScoreLookup(stats, restaurants, today, options);
        var winner = tallies
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => scoreByRestaurant.GetValueOrDefault(t.RestaurantId, int.MinValue))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        poll.Status = PollStatus.Closed;
        poll.WinnerRestaurantId = winner?.RestaurantId;
        poll.RecommendedRestaurantId = recommendation?.Pick.RestaurantId;
        // Pick도 함께 남긴다 — 없으면 발표(슬랙 게시)가 실패했을 때 재시도 경로
        // (GetClosedOutcomeAsync)가 추천을 완전히 복원하지 못한다. 마감 때의 점수 상세 문장도
        // 같은 저장에 얼려 둔다 — 마감 뒤 설정·식사 기록이 바뀌어도 "그때의 이유"가 변하지 않게,
        // 보는 쪽은 이 값만 읽고 다시 계산하지 않는다.
        poll.RationaleJson = recommendation is null
            ? null
            : ResultScoreSnapshot.Create(recommendation, options);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new PollOutcome(winner, tallies, recommendation, abstainers.NamedUserIds, abstainers.AnonymousCount);
    }

    /// <summary>
    /// 이미 닫힌 풀의 결과를 다시 만든다 — 결과 발표(슬랙 게시)가 실패해 재시도할 때
    /// 쓴다. ClosePollAsync와 달리 상태를 바꾸지 않고, 커밋되어 있는 값
    /// (WinnerRestaurantId·RationaleJson)에서 그대로 복원한다.
    /// </summary>
    public async Task<PollOutcome> GetClosedOutcomeAsync(long pollId, CancellationToken ct)
    {
        var poll = await db.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId, ct);
        var tallies = await GetTalliesAsync(pollId, ct);
        var abstainers = await GetAbstainersAsync(pollId, ct);

        var winner = poll.WinnerRestaurantId is { } winnerId
            ? tallies.SingleOrDefault(t => t.RestaurantId == winnerId)
            : null;

        var recommendation = poll.RationaleJson is null
            ? null
            : JsonSerializer.Deserialize<Recommendation>(poll.RationaleJson);

        return new PollOutcome(winner, tallies, recommendation, abstainers.NamedUserIds, abstainers.AnonymousCount);
    }

    /// <summary>결과 발표가 성공적으로 끝났음을 남긴다. 발표가 실패하면 이 호출까지
    /// 도달하지 못해 null로 남고, 스케줄러가 다음 주기에 발표만 다시 시도한다.</summary>
    public async Task MarkResultAnnouncedAsync(long pollId, DateTimeOffset at, CancellationToken ct)
    {
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        poll.ResultAnnouncedAt = at;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>식당 → 소속 카테고리 점수. 투표 동점 처리에 쓴다.</summary>
    static Dictionary<long, int> ScoreLookup(
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        DateOnly today,
        RecommendationOptions options)
    {
        var byCategory = RecommendationEngine.ScoreCategories(today, stats, options)
            .ToDictionary(s => s.CategoryId, s => s.Score);

        // 카테고리 점수가 없는 경우 0을 주면 무난한 값처럼 보이지만, 실제로는
        // 많이 먹어 크게 감점된(음수) 카테고리보다 유리해져 동점 처리에서 부당하게
        // 이긴다. 알 수 없는 식당(scoreByRestaurant에 아예 없는 경우)과 같은
        // int.MinValue로 맞춰 절대 동점 우승을 못 하게 한다.
        return restaurants.ToDictionary(
            r => r.RestaurantId,
            r => byCategory.GetValueOrDefault(r.CategoryId, int.MinValue));
    }

    /// <summary>채널 단위 하루 1건. 나중에 기록한 사람이 덮어쓴다.</summary>
    public async Task RecordMealAsync(
        DateOnly date, long restaurantId, string slackUserId, MealSource source, CancellationToken ct)
    {
        var existing = await db.MealRecords
            .SingleOrDefaultAsync(m => m.ChannelId == channelId && m.Date == date, ct);

        if (existing is null)
        {
            db.MealRecords.Add(new MealRecord
            {
                ChannelId = channelId, Date = date, RestaurantId = restaurantId,
                RecordedBySlackUserId = slackUserId, RecordedAt = DateTimeOffset.UtcNow,
                Source = source,
            });
        }
        else
        {
            existing.RestaurantId = restaurantId;
            existing.RecordedBySlackUserId = slackUserId;
            existing.RecordedAt = DateTimeOffset.UtcNow;
            existing.Source = source;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 등록 또는 수정. 정규화된 이름이 같으면 기존 행을 갱신한다 —
    /// 기록 중 이름만 들어와 Pending이 된 식당을 나중에 Active로 승격시키는 경로다.
    /// </summary>
    public async Task<Restaurant> SaveRestaurantAsync(
        RestaurantDraft draft, string slackUserId, CancellationToken ct,
        string? createdByDisplayName = null)
    {
        if (draft.EarlyDepartureSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(draft), "미리 출발 시간은 음수일 수 없습니다.");

        var normalized = Restaurant.Normalize(draft.Name);
        var now = DateTimeOffset.UtcNow;

        var restaurant = draft.RestaurantId is { } id
            ? await db.Restaurants.SingleAsync(r => r.Id == id, ct)
            : await db.Restaurants.SingleOrDefaultAsync(r => r.NormalizedName == normalized, ct);

        if (restaurant is null)
        {
            restaurant = new Restaurant
            {
                Name = draft.Name, NormalizedName = normalized,
                CreatedBySlackUserId = slackUserId, CreatedAt = now, UpdatedAt = now,
                CreatedByDisplayName = string.IsNullOrWhiteSpace(createdByDisplayName)
                    ? null
                    : createdByDisplayName.Trim(),
            };
            db.Restaurants.Add(restaurant);
        }

        restaurant.Name = draft.Name;
        restaurant.NormalizedName = normalized;
        // CategoryId만 ??로 보존한다 — null이 "카테고리 없음 확정"이 아니라
        // Active/Pending 불변조건을 지키기 위한 값이기 때문이다(의도치 않은 강등 방지).
        // 나머지 세 필드는 자유 선택 항목이라 null이 "비움"이라는 사용자 의도이므로
        // 그대로 대입해야 모달에서 값을 지웠을 때 실제로 지워진다.
        restaurant.CategoryId = draft.CategoryId ?? restaurant.CategoryId;

        // 새 카테고리 직접 입력. 정규화 이름이 같은 기존 카테고리가 있으면 재사용한다 —
        // "순대국"/"순대 국"이 갈라지면 두 곳 모두 이력이 쌓이지 않아 추천이 망가진다.
        // 새로 만들 때는 같은 SaveChanges에 실어 식당 저장 실패 시 고아 카테고리가 남지 않게 한다.
        Category? newCategory = null;
        if (!string.IsNullOrWhiteSpace(draft.NewCategoryName))
        {
            var categoryName = draft.NewCategoryName.Trim();
            var categoryNormalized = Category.Normalize(categoryName);
            var matched = await db.Categories
                .SingleOrDefaultAsync(c => c.NormalizedName == categoryNormalized, ct);

            if (matched is not null)
            {
                restaurant.CategoryId = matched.Id;
            }
            else
            {
                newCategory = new Category
                {
                    Name = categoryName, NormalizedName = categoryNormalized,
                    IsBuiltIn = false, CreatedAt = now,
                };
                db.Categories.Add(newCategory);
                restaurant.Category = newCategory;
            }
        }
        restaurant.WalkMinutes = draft.WalkMinutes;
        restaurant.PriceLevel = draft.PriceLevel;
        restaurant.Note = draft.Note;
        restaurant.Address = string.IsNullOrWhiteSpace(draft.Address) ? null : draft.Address.Trim();
        restaurant.WaitLevel = draft.WaitLevel;
        // 출발 시각도 자유 선택 항목이라 null이 "지정 해제"다. 그대로 대입한다.
        restaurant.DepartureTime = draft.DepartureTime;
        restaurant.EarlyDepartureSeconds = draft.EarlyDepartureSeconds;
        restaurant.UpdatedAt = now;

        // 불변 조건: Active ⟺ CategoryId != null
        restaurant.Status = restaurant.CategoryId is null && newCategory is null
            ? RestaurantStatus.Pending
            : RestaurantStatus.Active;

        await db.SaveChangesAsync(ct);
        return restaurant;
    }

    public Task MarkMealPromptPostedAsync(DateOnly date, DateTimeOffset at, CancellationToken ct) =>
        MarkDayAsync(date, day => day.MealPromptPostedAt = at, ct);

    public Task MarkPendingReminderSentAsync(DateOnly date, DateTimeOffset at, CancellationToken ct) =>
        MarkDayAsync(date, day => day.PendingReminderSentAt = at, ct);

    async Task MarkDayAsync(DateOnly date, Action<ChannelDay> update, CancellationToken ct)
    {
        var day = await db.ChannelDays
            .SingleOrDefaultAsync(d => d.ChannelId == channelId && d.Date == date, ct);

        if (day is null)
        {
            day = new ChannelDay { ChannelId = channelId, Date = date };
            db.ChannelDays.Add(day);
        }

        update(day);
        await db.SaveChangesAsync(ct);
    }
}
