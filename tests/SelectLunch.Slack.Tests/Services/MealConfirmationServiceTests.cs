using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using MealRecord = SelectLunch.Shared.Entities.MealRecord;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 식사 기록의 정책과 저장 경계. 실제 SQLite와 고정 시계 위에서 서비스를 그대로 돌리고, 소비자(모달·액션 핸들러)가
/// 보는 결과만 확인한다 — 언제 받고 언제 거절하는가, 거절·실패 뒤 무엇이 남는가.
/// 시계는 고정한 한국 시각이며 모든 날짜는 <see cref="Today"/>에서 파생한다.
/// </summary>
public sealed class MealConfirmationServiceTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 11, 20);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DateTimeOffset Kst(DateOnly date, int hour, int minute = 0) =>
        new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(9)).ToUniversalTime();

    static IOptionsMonitor<LunchOptions> Monitor(LunchOptions options) =>
        StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => options)));

    /// <summary>A 한식 · B 일식 · C 중식 · D 한식(A와 같은 카테고리). 카테고리는 마이그레이션 시드다.</summary>
    static async Task<Restaurant[]> SeedRestaurantsAsync(LunchService lunch) =>
    [
        await lunch.SaveRestaurantAsync(new(null, "김밥천국", 1, null, null, null), "U0", Ct),
        await lunch.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U0", Ct),
        await lunch.SaveRestaurantAsync(new(null, "짜장면집", 2, null, null, null), "U0", Ct),
        await lunch.SaveRestaurantAsync(new(null, "국밥집", 1, null, null, null), "U0", Ct),
    ];

    sealed class Harness : IAsyncDisposable
    {
        readonly TestDb fixture;

        Harness(TestDb fixture, Restaurant[] restaurants, LunchOptions options)
        {
            this.fixture = fixture;
            R = restaurants;
            Options = options;
            Lunch = new LunchService(fixture.Db, Channel);
            Service = new MealConfirmationService(fixture.Db, Lunch, Channel, Monitor(options), Clock);
        }

        public static async Task<Harness> CreateAsync(Action<LunchOptions>? configure, DateTimeOffset? now)
        {
            var fixture = await TestDb.CreateAsync();
            var options = new LunchOptions();
            configure?.Invoke(options);
            var harness = new Harness(fixture, await SeedRestaurantsAsync(new LunchService(fixture.Db, Channel)), options);
            harness.Clock.UtcNow = now ?? Kst(Today, 14);
            return harness;
        }

        Restaurant[] R { get; }
        public LunchDbContext Db => fixture.Db;
        public LunchOptions Options { get; }
        public TestTimeProvider Clock { get; } = new(Kst(Today, 14));
        public LunchService Lunch { get; }
        public MealConfirmationService Service { get; }
        public Restaurant A => R[0];
        public Restaurant B => R[1];
        public Restaurant C => R[2];
        public Restaurant D => R[3];

        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }

    static Task<Harness> SetupAsync(Action<LunchOptions>? configure = null, DateTimeOffset? now = null) =>
        Harness.CreateAsync(configure, now);

    sealed record Vote(long? RestaurantId, bool Secret = false, bool FromAdvance = false);

    static Vote[] Votes(int count, long? restaurantId) =>
        [.. Enumerable.Range(0, count).Select(_ => new Vote(restaurantId))];

    static async Task SeedPollAsync(
        LunchDbContext db, DateOnly date, PollStatus status, string channel, params Vote[] votes)
    {
        db.Polls.Add(new LunchPoll
        {
            ChannelId = channel, Date = date, Status = status,
            OpensAt = Kst(date, 11), ClosesAt = Kst(date, 11, 30),
            Votes = [.. votes.Select((v, i) => new PollVote
            {
                SlackUserId = $"V{i}", RestaurantId = v.RestaurantId, IsSecret = v.Secret,
                IsFromAdvanceVote = v.FromAdvance, VotedAt = Kst(date, 11, 5),
            })],
        });
        await db.SaveChangesAsync(Ct);
    }

    sealed record Row(
        long Id, string ChannelId, DateOnly Date, long RestaurantId,
        string RecordedBy, DateTimeOffset RecordedAt, MealSource Source);

    static async Task<List<Row>> RowsAsync(LunchDbContext db) =>
        [.. (await db.MealRecords.AsNoTracking().OrderBy(m => m.Id).ToListAsync(Ct)).Select(m => new Row(
            m.Id, m.ChannelId, m.Date, m.RestaurantId, m.RecordedBySlackUserId, m.RecordedAt, m.Source))];

    /// <summary>식당·카테고리·식사 기록 전체의 직렬화. 거절·실패 뒤 아무것도 달라지지 않았는지 한 번에 비교한다.</summary>
    static async Task<string> WorldAsync(LunchDbContext db) => JsonSerializer.Serialize(new
    {
        Restaurants = await db.Restaurants.AsNoTracking().OrderBy(r => r.Id).Select(r => new
        {
            r.Id, r.Name, r.CategoryId, r.Note, r.WalkMinutes, r.PriceLevel, r.Status, r.UpdatedAt,
        }).ToListAsync(Ct),
        Categories = await db.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Name).ToListAsync(Ct),
        Meals = await RowsAsync(db),
    });

    /// <summary>거절되어야 하는 입력에 얹는 등록 초안 — 새 카테고리·메모·도보 시간이 새면 바로 드러난다.</summary>
    static RestaurantDraft LeakyDraft(string name, long? restaurantId = null) =>
        new(restaurantId, name, null, 10, 2, "바뀐 메모", NewCategoryName: "새종류");

    static long[] Set(IEnumerable<long> ids) => [.. ids.Order()];

    // ───────────── 한도 ─────────────

    [Theory]
    [InlineData(5, -1, 1)]   // 마감된 투표 없음
    [InlineData(5, 0, 1)]
    [InlineData(5, 5, 1)]
    [InlineData(5, 6, 2)]
    [InlineData(5, 10, 2)]
    [InlineData(5, 11, 3)]
    [InlineData(4, 4, 1)]
    [InlineData(4, 5, 2)]
    public async Task 조회한_한도까지만_저장되고_하나라도_넘으면_아무것도_저장하지_않는다(int perRestaurant, int voters, int limit)
    {
        await using var h = await SetupAsync(o => o.MealParticipantsPerRestaurant = perRestaurant);
        if (voters >= 0)
            await SeedPollAsync(h.Db, Today, PollStatus.Closed, Channel, Votes(voters, h.A.Id));
        long[] all = [h.A.Id, h.B.Id, h.C.Id, h.D.Id];

        var state = await h.Service.GetStateAsync(Today, Ct);
        var over = await h.Service.SaveAsync(Today, all[..(limit + 1)], "U1", Ct);

        Assert.Equal((true, true, limit, 0), (state.Allowed, state.IsToday, state.Limit, state.Recorded.Count));
        Assert.NotNull(over.Error);
        Assert.Empty(over.Places);
        Assert.False(await h.Db.MealRecords.AnyAsync(Ct));

        var saved = await h.Service.SaveAsync(Today, all[..limit], "U1", Ct);

        Assert.Null(saved.Error);
        Assert.Equal(Set(all[..limit]), Set(saved.Places.Select(p => p.Id)));
        Assert.Equal(Set(all[..limit]), Set((await h.Service.GetStateAsync(Today, Ct)).Recorded.Select(p => p.Id)));
    }

    [Theory]
    [InlineData("counted", 2)]
    [InlineData("open", 1)]
    [InlineData("cancelled", 1)]
    [InlineData("otherChannel", 1)]
    [InlineData("otherDate", 1)]
    public async Task 한도는_같은_채널_날짜의_마감된_본투표에서_식당을_고른_사람만_센다(string scenario, int expected)
    {
        await using var h = await SetupAsync();
        var (status, channel, date) = scenario switch
        {
            "open" => (PollStatus.Open, Channel, Today),
            "cancelled" => (PollStatus.Cancelled, Channel, Today),
            "otherChannel" => (PollStatus.Closed, "C2", Today),
            "otherDate" => (PollStatus.Closed, Channel, Today.AddDays(-1)),
            _ => (PollStatus.Closed, Channel, Today),
        };
        // 11명이면 3곳이므로, 무시되어야 하는 투표가 새면 기대값 1과 어긋난다.
        // counted: 식당표 4 + 비밀 1 + 사전표 반영 1 = 6명이 2곳. 비밀표를 빼면 1, 기권 5명이 섞이면 3,
        // 사전투표 행 12개가 합산되면 4가 된다.
        Vote[] votes = scenario == "counted"
            ? [.. Votes(4, h.A.Id), new Vote(h.B.Id, Secret: true), new Vote(h.C.Id, FromAdvance: true), .. Votes(5, null)]
            : Votes(11, h.A.Id);
        await SeedPollAsync(h.Db, date, status, channel, votes);
        if (scenario == "counted")
        {
            for (var i = 0; i < 12; i++)
                h.Db.AdvanceVotes.Add(new AdvanceVote
                {
                    ChannelId = Channel, Date = date, SlackUserId = $"V{i}", RestaurantId = h.A.Id,
                    VotedAt = Kst(date, 10), ChangeCount = 1, IsCancelled = i % 3 == 1, IsSupersededByMainVote = i % 3 == 2,
                });
            await h.Db.SaveChangesAsync(Ct);
        }

        Assert.Equal(expected, (await h.Service.GetStateAsync(Today, Ct)).Limit);
    }

    [Fact]
    public async Task 한도가_줄어_기존_목록이_넘쳐도_일부만_잘라_저장하지_않는다()
    {
        await using var h = await SetupAsync();
        await SeedPollAsync(h.Db, Today, PollStatus.Closed, Channel, Votes(11, h.A.Id));
        Assert.Null((await h.Service.SaveAsync(Today, [h.A.Id, h.B.Id, h.C.Id], "U1", Ct)).Error);
        var rows = await RowsAsync(h.Db);
        var world = await WorldAsync(h.Db);
        h.Options.MealParticipantsPerRestaurant = 10;

        var state = await h.Service.GetStateAsync(Today, Ct);
        var resubmitted = await h.Service.SaveAsync(Today, [h.A.Id, h.B.Id, h.C.Id], "U2", Ct);

        Assert.Equal((true, 2, 3), (state.Allowed, state.Limit, state.Recorded.Count));
        Assert.NotNull(resubmitted.Error);
        Assert.Equal(world, await WorldAsync(h.Db));

        var trimmed = await h.Service.SaveAsync(Today, [h.A.Id, h.B.Id], "U2", Ct);

        Assert.Null(trimmed.Error);
        Assert.Equal(rows.Where(r => r.RestaurantId != h.C.Id), await RowsAsync(h.Db));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task 기준이_양수가_아니면_조회와_저장이_예외를_던지고_아무것도_쓰지_않는다(int perRestaurant)
    {
        await using var h = await SetupAsync(o => o.MealParticipantsPerRestaurant = perRestaurant);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.GetStateAsync(Today, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SaveAsync(Today, [h.A.Id], "U1", Ct));

        Assert.False(await h.Db.MealRecords.AnyAsync(Ct));
    }

    // ───────────── 당일 정정 ─────────────

    [Fact]
    public async Task 같은_날_두_곳에서_한_곳으로_정정하면_남긴_행을_보존하고_잘못된_입력은_전체를_바꾸지_않는다()
    {
        await using var h = await SetupAsync();
        await SeedPollAsync(h.Db, Today, PollStatus.Closed, Channel, Votes(6, h.A.Id));
        var pending = await h.Lunch.SaveRestaurantAsync(new(null, "미분류식당", null, null, null, null), "U0", Ct);
        var archived = await h.Lunch.SaveRestaurantAsync(new(null, "보관식당", 1, null, null, null), "U0", Ct);
        archived.Status = RestaurantStatus.Archived;
        await h.Db.SaveChangesAsync(Ct);
        Assert.Null((await h.Service.SaveAsync(Today, [h.A.Id, h.D.Id], "U1", Ct)).Error);
        // 같은 카테고리의 서로 다른 두 식당은 그 카테고리 방문 2회다.
        Assert.Equal(2, (await h.Db.GetCategoryStatsAsync(Today, Ct)).Single(s => s.CategoryId == 1).Count7d);
        var rows = await RowsAsync(h.Db);
        var world = await WorldAsync(h.Db);

        long[][] invalid = [[], [h.A.Id, h.A.Id], [h.A.Id, h.D.Id, h.B.Id], [h.A.Id, 9999], [h.A.Id, pending.Id], [h.A.Id, archived.Id]];
        foreach (var ids in invalid)
        {
            var rejected = await h.Service.SaveAsync(Today, ids, "U2", Ct);

            Assert.NotNull(rejected.Error);
            Assert.Empty(rejected.Places);
            Assert.Equal(world, await WorldAsync(h.Db));
        }

        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(10);
        var corrected = await h.Service.SaveAsync(Today, [h.D.Id], "U2", Ct);

        Assert.Null(corrected.Error);
        Assert.Equal(new[] { h.D.Id }, corrected.Places.Select(p => p.Id));
        // 남긴 식당의 행은 행 ID·기록자·시각·출처가 그대로고, 뺀 식당의 행만 사라진다.
        Assert.Equal(rows.Single(r => r.RestaurantId == h.D.Id), Assert.Single(await RowsAsync(h.Db)));
        Assert.Equal(1, (await h.Db.GetCategoryStatsAsync(Today, Ct)).Single(s => s.CategoryId == 1).Count7d);
    }

    [Fact]
    public async Task 카테고리_없는_신규_등록도_기록되고_그_식당과_이미_기록된_보관_식당은_정정에서_유지하되_새로_고를_수는_없다()
    {
        await using var h = await SetupAsync();
        await SeedPollAsync(h.Db, Today, PollStatus.Closed, Channel, Votes(6, h.A.Id));
        var first = await h.Service.SaveAsync(
            Today, [h.A.Id], "U1", Ct, new RestaurantDraft(null, "이름만식당", null, null, null, null), "홍길동");
        Assert.Null(first.Error);
        Assert.NotNull(first.Registered);
        var pendingId = first.Registered.Id;
        Assert.Equal((RestaurantStatus.Pending, (long?)null), (first.Registered.Status, first.Registered.CategoryId));
        h.A.Status = RestaurantStatus.Archived;   // 기록된 뒤 보관된다
        await h.Db.SaveChangesAsync(Ct);
        var rows = await RowsAsync(h.Db);
        Assert.Equal(
            new Dictionary<long, MealSource> { [pendingId] = MealSource.NewRegistration, [h.A.Id] = MealSource.Prompt },
            rows.ToDictionary(r => r.RestaurantId, r => r.Source));

        var kept = await h.Service.SaveAsync(Today, [pendingId, h.A.Id], "U2", Ct);

        Assert.Null(kept.Error);
        Assert.Equal(rows, await RowsAsync(h.Db));

        var otherPending = await h.Lunch.SaveRestaurantAsync(new(null, "다른미분류", null, null, null, null), "U0", Ct);
        var otherArchived = await h.Lunch.SaveRestaurantAsync(new(null, "다른보관", 1, null, null, null), "U0", Ct);
        otherArchived.Status = RestaurantStatus.Archived;
        await h.Db.SaveChangesAsync(Ct);
        foreach (var forged in new[] { otherPending.Id, otherArchived.Id })
        {
            Assert.NotNull((await h.Service.SaveAsync(Today, [pendingId, forged], "U2", Ct)).Error);
            Assert.Equal(rows, await RowsAsync(h.Db));
        }

        var swapped = await h.Service.SaveAsync(Today, [pendingId, h.B.Id], "U2", Ct);

        Assert.Null(swapped.Error);
        var swappedRows = await RowsAsync(h.Db);
        Assert.Equal(Set([pendingId, h.B.Id]), Set(swappedRows.Select(r => r.RestaurantId)));
        Assert.Equal(rows.Single(r => r.RestaurantId == pendingId), swappedRows.Single(r => r.RestaurantId == pendingId));
    }

    // ───────────── 날짜 ─────────────

    [Theory]
    [InlineData("past")]
    [InlineData("future")]
    [InlineData("otherCarryoverDay")]
    [InlineData("carryoverFuture")]
    [InlineData("noHistory")]
    [InlineData("otherChannelHistory")]
    [InlineData("alreadyRecorded")]
    public async Task 열리지_않는_날짜는_조회도_저장도_거절하고_등록도_남기지_않는다(string scenario)
    {
        var yesterday = Today.AddDays(-1);
        var date = scenario switch
        {
            "future" or "carryoverFuture" => Today.AddDays(1),
            "otherCarryoverDay" => Today.AddDays(-2),
            _ => yesterday,
        };
        DateOnly? carryover = scenario switch
        {
            "past" or "future" => null,
            "carryoverFuture" => date,
            _ => yesterday,
        };
        await using var h = await SetupAsync(o => o.CarryoverMealPromptDate = carryover);
        // 마감된 투표가 있어도 아래 한 가지가 어긋나면 열리지 않는다: 지정일 아님·미래·게시 이력 없음·기록 이미 있음.
        switch (scenario)
        {
            case "noHistory":
                break;
            case "otherChannelHistory":
                h.Db.ChannelDays.Add(new ChannelDay { ChannelId = "C2", Date = date, MealPromptPostedAt = Kst(date, 13) });
                h.Db.ChannelDays.Add(new ChannelDay { ChannelId = Channel, Date = date });
                break;
            default:
                await h.Lunch.MarkMealPromptPostedAsync(date, Kst(date, 13), Ct);
                break;
        }
        if (scenario == "alreadyRecorded")
            h.Db.MealRecords.Add(new MealRecord
            {
                ChannelId = Channel, Date = date, RestaurantId = h.A.Id,
                RecordedBySlackUserId = "ORIGINAL", RecordedAt = Kst(date, 13, 40), Source = MealSource.Prompt,
            });
        await SeedPollAsync(h.Db, date, PollStatus.Closed, Channel, Votes(11, h.A.Id));
        var world = await WorldAsync(h.Db);

        var state = await h.Service.GetStateAsync(date, Ct);
        var result = await h.Service.SaveAsync(date, [h.A.Id], "U1", Ct, LeakyDraft("새식당"), "홍길동");

        Assert.Equal((false, false, 0, 0), (state.Allowed, state.IsToday, state.Limit, state.Recorded.Count));
        Assert.NotNull(result.Error);
        Assert.Empty(result.Places);
        Assert.Null(result.Registered);
        Assert.False(result.IsToday);
        Assert.Equal(world, await WorldAsync(h.Db));
    }

    [Fact]
    public async Task 지정된_과거_날짜는_먼저_제출한_한_건만_저장되고_늦은_제출은_등록도_남기지_않는다()
    {
        var yesterday = Today.AddDays(-1);
        await using var h = await SetupAsync(o => o.CarryoverMealPromptDate = yesterday);
        await h.Lunch.MarkMealPromptPostedAsync(yesterday, Kst(yesterday, 13), Ct);
        await SeedPollAsync(h.Db, yesterday, PollStatus.Closed, Channel, Votes(6, h.A.Id));
        // 다른 채널의 같은 날 기록은 이 채널의 최초 확정을 막지 않는다.
        h.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C2", Date = yesterday, RestaurantId = h.A.Id,
            RecordedBySlackUserId = "OTHER", RecordedAt = Kst(yesterday, 13, 40), Source = MealSource.Prompt,
        });
        await h.Db.SaveChangesAsync(Ct);
        // 두 사람이 같은 창을 연다.
        var opened = await h.Service.GetStateAsync(yesterday, Ct);
        Assert.Equal((true, false, 2, 0), (opened.Allowed, opened.IsToday, opened.Limit, opened.Recorded.Count));

        var winner = await h.Service.SaveAsync(
            yesterday, [h.A.Id], "U2", Ct, new RestaurantDraft(null, "신규식당", 1, null, null, null));
        var late = await h.Service.SaveAsync(
            yesterday, [], "U1", Ct, new RestaurantDraft(null, "늦은등록", 1, null, null, null));

        Assert.Null(winner.Error);
        Assert.False(winner.IsToday);
        Assert.NotNull(winner.Registered);
        Assert.Equal(RestaurantStatus.Active, winner.Registered.Status);
        Assert.NotNull(late.Error);
        Assert.False(await h.Db.Restaurants.AnyAsync(r => r.Name == "늦은등록", Ct));
        var rows = (await RowsAsync(h.Db)).Where(r => r.ChannelId == Channel).ToList();
        Assert.Equal(Set([h.A.Id, winner.Registered.Id]), Set(rows.Select(r => r.RestaurantId)));
        Assert.All(rows, r => Assert.Equal("U2", r.RecordedBy));
        Assert.False((await h.Service.GetStateAsync(yesterday, Ct)).Allowed);
    }

    [Fact]
    public async Task 오늘은_설정_시간대의_현지_날짜이고_창을_연_뒤_자정이_지나면_제출_시점의_날짜로_다시_검사한다()
    {
        // 현지 자정 직후 — UTC로는 아직 전날이다.
        await using var h = await SetupAsync(now: Kst(Today, 0, 30));
        Assert.Equal(Today.AddDays(-1), DateOnly.FromDateTime(h.Clock.UtcNow.UtcDateTime));

        var open = await h.Service.GetStateAsync(Today, Ct);
        var previous = await h.Service.GetStateAsync(Today.AddDays(-1), Ct);

        Assert.Equal((true, true, false), (open.Allowed, open.IsToday, previous.Allowed));

        h.Clock.UtcNow = Kst(Today.AddDays(1), 0, 10);
        var late = await h.Service.SaveAsync(Today, [h.A.Id], "U1", Ct);

        Assert.NotNull(late.Error);
        Assert.False(late.IsToday);
        Assert.False(await h.Db.MealRecords.AnyAsync(Ct));
        Assert.Null((await h.Service.SaveAsync(Today.AddDays(1), [h.A.Id], "U1", Ct)).Error);
    }

    // ───────────── 새 식당 등록과 함께 확정 ─────────────

    [Fact]
    public async Task 지난_요청의_동명_등록이_기존_식당의_출발_설정을_바꾸지_않는다()
    {
        var past = Today.AddDays(-1);
        await using var h = await SetupAsync(o => o.CarryoverMealPromptDate = past);
        h.A.EarlyDepartureSeconds = 480;
        h.A.DepartureTime = new TimeOnly(11, 20);
        await h.Lunch.MarkMealPromptPostedAsync(past, Kst(past, 13), Ct);

        var result = await h.Service.SaveAsync(past, [], "U1", Ct,
            new RestaurantDraft(null, "김밥 천국", null, null, null, null));

        Assert.NotNull(result.Error);
        Assert.False(await h.Db.MealRecords.AnyAsync(Ct));
        var original = await h.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Id == h.A.Id, Ct);
        Assert.Equal(480, original.EarlyDepartureSeconds);
        Assert.Equal(new TimeOnly(11, 20), original.DepartureTime);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("pending")]
    [InlineData("sameName")]
    [InlineData("edit")]
    public async Task 선택이_거절되면_등록한_식당도_카테고리도_기존_식당_변경도_남지_않는다(string reason)
    {
        await using var h = await SetupAsync();
        await SeedPollAsync(h.Db, Today, PollStatus.Closed, Channel, Votes(11, h.A.Id));   // 11명 → 3곳
        var pending = await h.Lunch.SaveRestaurantAsync(new(null, "미분류식당", null, null, null, null), "U0", Ct);
        // limit: 등록 한 곳을 더해 4곳. sameName: 등록 이름이 선택한 식당과 같다. edit: 등록으로 기존 식당을 고치려 한다.
        long[] ids = reason switch
        {
            "limit" => [h.A.Id, h.B.Id, h.C.Id],
            "duplicate" => [h.B.Id, h.B.Id],
            "invalid" => [h.B.Id, 9999],
            "pending" => [pending.Id],
            "edit" => [h.B.Id],
            _ => [h.A.Id],
        };
        var draft = reason switch
        {
            "sameName" => LeakyDraft("김밥천국"),
            "edit" => LeakyDraft("수정시도", h.A.Id),
            _ => LeakyDraft("새식당"),
        };
        var world = await WorldAsync(h.Db);

        var result = await h.Service.SaveAsync(Today, ids, "U1", Ct, draft, "홍길동");

        Assert.NotNull(result.Error);
        Assert.Null(result.Registered);
        Assert.Empty(result.Places);
        Assert.Equal(world, await WorldAsync(h.Db));
    }

    [Fact]
    public async Task 기록_저장이_실패하면_식당도_카테고리도_기존_기록도_그대로이고_같은_컨텍스트로_다시_저장할_수_있다()
    {
        await using var h = await SetupAsync();
        await SeedPollAsync(h.Db, Today, PollStatus.Closed, Channel, Votes(6, h.A.Id));
        Assert.Null((await h.Service.SaveAsync(Today, [h.A.Id, h.B.Id], "U1", Ct)).Error);
        var world = await WorldAsync(h.Db);
        var draft = new RestaurantDraft(null, "새식당", null, null, null, null, NewCategoryName: "새종류");
        long cId = h.C.Id;
        // 식당·카테고리 등록과 옛 행 삭제까지 끝난 뒤, 새 행을 넣는 순간 실패시킨다.
        await h.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_meal_insert BEFORE INSERT ON MealRecords BEGIN SELECT RAISE(ABORT, 'injected'); END;", Ct);

        await Assert.ThrowsAsync<DbUpdateException>(() => h.Service.SaveAsync(Today, [cId], "U2", Ct, draft));

        Assert.Equal(world, await WorldAsync(h.Db));

        // 되돌려진 식당이 변경 추적기에 남아 있으면 같은 컨텍스트의 재시도가 키 충돌로 실패한다.
        await h.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_meal_insert;", Ct);
        var retry = await h.Service.SaveAsync(Today, [cId], "U2", Ct, draft);

        Assert.Null(retry.Error);
        Assert.NotNull(retry.Registered);
        Assert.Equal(Set([retry.Registered.Id, cId]), Set((await RowsAsync(h.Db)).Select(r => r.RestaurantId)));
    }

    // ───────────── 다른 연결의 동시 제출 ─────────────

    [Fact]
    public async Task 지정된_과거_날짜의_동시_첫_제출은_하나만_저장된다()
    {
        var yesterday = Today.AddDays(-1);
        var directory = Path.Combine(Path.GetTempPath(), $"meal-confirmation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var options = new LunchOptions { CarryoverMealPromptDate = yesterday };
            var clock = new TestTimeProvider(Kst(Today, 14));
            var connection = $"Data Source={Path.Combine(directory, "meals.db")};Pooling=False;Default Timeout=30";
            LunchDbContext Open() => new(new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options);
            MealConfirmationService ServiceOn(LunchDbContext db) =>
                new(db, new LunchService(db, Channel), Channel, Monitor(options), clock);

            long[] ids;
            await using (var setup = Open())
            {
                await setup.Database.EnsureCreatedAsync(Ct);
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", Ct);
                var lunch = new LunchService(setup, Channel);
                ids = [.. (await SeedRestaurantsAsync(lunch)).Select(r => r.Id)];
                await lunch.MarkMealPromptPostedAsync(yesterday, Kst(yesterday, 13), Ct);
                await SeedPollAsync(setup, yesterday, PollStatus.Closed, Channel, Votes(6, ids[0]));
            }

            using var start = new Barrier(3);
            async Task<MealSaveResult> Submit(string user, long[] selection)
            {
                await using var db = Open();
                start.SignalAndWait(Ct);
                return await ServiceOn(db).SaveAsync(yesterday, selection, user, Ct);
            }
            var left = Task.Run(() => Submit("U1", [ids[0]]), Ct);
            var right = Task.Run(() => Submit("U2", [ids[1], ids[2]]), Ct);
            start.SignalAndWait(Ct);
            var results = await Task.WhenAll(left, right);

            var winner = Assert.Single(results, r => r.Error is null);
            Assert.Single(results, r => r.Error is not null);
            await using var verify = Open();
            var rows = await RowsAsync(verify);
            Assert.Equal(Set(winner.Places.Select(p => p.Id)), Set(rows.Select(r => r.RestaurantId)));
            Assert.Single(rows.Select(r => r.RecordedBy).Distinct());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
