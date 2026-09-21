using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

public class LunchServiceTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    static async Task<(TestDb Fixture, LunchService Service)> SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        // 카테고리(1 한식 · 3 일식)는 마이그레이션 시드로 이미 들어 있다.
        // 다시 넣으면 Categories.Id/Name UNIQUE 제약에 걸린다 — 시드된 Id를 그대로 참조한다.
        return (fixture, new LunchService(fixture.Db, Channel));
    }

    static RestaurantDraft Draft(string name, long categoryId) =>
        new(null, name, categoryId, null, null, null);

    [Fact]
    public async Task 투표를_열면_Active_식당이_후보로_들어간다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        var candidates = await fixture.Db.PollCandidates.CountAsync(c => c.PollId == poll.Id, ct);
        Assert.Equal(2, candidates);
        Assert.Equal(PollStatus.Open, poll.Status);
    }

    /// <summary>
    /// OpenPollAsync는 풀과 후보를 한 트랜잭션으로 커밋한다. 후보 삽입이 중간에
    /// 실패하는 경로를 이 테스트로 직접 재현하지는 못한다 — LunchService의
    /// 공개 API만으로는 두 번째 SaveChangesAsync를 실패시킬 결함 있는 데이터를
    /// 정상 흐름(SaveRestaurantAsync → OpenPollAsync)으로 주입할 수 없고, 실패를
    /// 강제하려면 테스트 전용 훅이나 TestDb 변경이 필요한데 이번 수정 범위(
    /// LunchService.cs·LunchServiceTests.cs)를 벗어난다. 대신 성공 경로에서
    /// 풀과 후보가 항상 함께(하나도 빠짐없이) 존재함을 확인해 원자적 커밋을
    /// 간접적으로 검증한다.
    /// </summary>
    [Fact]
    public async Task 투표_개설은_풀과_후보를_함께_커밋한다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        var savedPoll = await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct);
        var candidateCount = await fixture.Db.PollCandidates.CountAsync(c => c.PollId == poll.Id, ct);
        Assert.Equal(PollStatus.Open, savedPoll.Status);
        Assert.Equal(2, candidateCount);
    }

    [Fact]
    public async Task 같은_사람이_다시_투표하면_표가_바뀐다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var b = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastVoteAsync(poll.Id, "U1", a.Id, ct);
        await service.CastVoteAsync(poll.Id, "U1", b.Id, ct);

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).ToListAsync(ct);
        Assert.Equal(b.Id, Assert.Single(votes).RestaurantId);
    }

    [Fact]
    public async Task 투표_동점이면_추천_점수가_높은_쪽이_1위가_된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 한식집 = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var 일식집 = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        // 한식을 최근에 많이 먹었으므로 추천 점수는 일식이 높다
        fixture.Db.MealRecords.AddRange(
            new MealRecord { ChannelId = Channel, Date = Today.AddDays(-1), RestaurantId = 한식집.Id,
                RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch, Source = MealSource.Prompt },
            new MealRecord { ChannelId = Channel, Date = Today.AddDays(-3), RestaurantId = 한식집.Id,
                RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch, Source = MealSource.Prompt });
        await fixture.Db.SaveChangesAsync(ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 한식집.Id, ct);
        await service.CastVoteAsync(poll.Id, "U2", 일식집.Id, ct);   // 1:1 동점

        var outcome = await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        Assert.Equal(일식집.Id, outcome.Winner!.RestaurantId);
    }

    [Fact]
    public async Task 표수와_카테고리_점수까지_동점이면_이름_오름차순으로_결정된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        // 둘 다 같은 카테고리(3=일식)라 카테고리 점수도 항상 같다 —
        // 표수(1:1)와 점수가 모두 동점이라 이름 오름차순(Ordinal)만 남는다.
        var 나중식당 = await service.SaveRestaurantAsync(Draft("나중식당", 3), "U1", ct);
        var 가나다식당 = await service.SaveRestaurantAsync(Draft("가나다식당", 3), "U1", ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 나중식당.Id, ct);
        await service.CastVoteAsync(poll.Id, "U2", 가나다식당.Id, ct);

        var outcome = await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        Assert.Equal(가나다식당.Id, outcome.Winner!.RestaurantId);
    }

    [Fact]
    public async Task 마감하면_상태와_추천_근거가_저장된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        var saved = await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct);
        Assert.Equal(PollStatus.Closed, saved.Status);
        Assert.NotNull(saved.RecommendedRestaurantId);
        Assert.NotNull(saved.RationaleJson);
    }

    [Fact]
    public async Task 하루에_두_번_기록하면_나중_것이_덮어쓴다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var b = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        await service.RecordMealAsync(Today, a.Id, "U1", MealSource.Prompt, ct);
        await service.RecordMealAsync(Today, b.Id, "U2", MealSource.Prompt, ct);

        var record = await fixture.Db.MealRecords.SingleAsync(m => m.Date == Today, ct);
        Assert.Equal(b.Id, record.RestaurantId);
        Assert.Equal("U2", record.RecordedBySlackUserId);
    }

    [Fact]
    public async Task 카테고리가_있으면_Active로_등록된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;

        var restaurant = await service.SaveRestaurantAsync(
            Draft("스시로", 3), "U1", TestContext.Current.CancellationToken);

        Assert.Equal(RestaurantStatus.Active, restaurant.Status);
    }

    [Fact]
    public async Task 카테고리가_없으면_Pending으로_등록된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;

        var restaurant = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "이름만아는집", null, null, null, null),
            "U1", TestContext.Current.CancellationToken);

        Assert.Equal(RestaurantStatus.Pending, restaurant.Status);
    }

    [Fact]
    public async Task 같은_이름을_다시_등록하면_기존_식당을_갱신한다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pending = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "스시로", null, null, null, null), "U1", ct);

        var filled = await service.SaveRestaurantAsync(Draft("스시 로", 3), "U2", ct);

        Assert.Equal(pending.Id, filled.Id);
        Assert.Equal(RestaurantStatus.Active, filled.Status);
        Assert.Equal(1, await fixture.Db.Restaurants.CountAsync(ct));
    }

    [Fact]
    public async Task 메모를_비우면_실제로_지워진다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var withNote = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "스시로", 3, 5, 2, "점심 특선 있음"), "U1", ct);
        Assert.Equal("점심 특선 있음", withNote.Note);

        var cleared = await service.SaveRestaurantAsync(
            new RestaurantDraft(withNote.Id, "스시로", 3, null, null, null), "U2", ct);

        Assert.Null(cleared.Note);
        Assert.Null(cleared.WalkMinutes);
        Assert.Null(cleared.PriceLevel);
    }

    [Fact]
    public async Task 이미_Active인_식당을_카테고리_없이_수정해도_Active를_유지한다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        var edited = await service.SaveRestaurantAsync(
            new RestaurantDraft(restaurant.Id, "스시로", null, 5, null, null), "U2", ct);

        Assert.Equal(RestaurantStatus.Active, edited.Status);
        Assert.Equal(3, edited.CategoryId);
    }

    [Fact]
    public async Task 발송_이력을_남기면_오늘_상태에_반영된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        await service.MarkMealPromptPostedAsync(Today, ClosesAt, ct);

        var day = await fixture.Db.ChannelDays.SingleAsync(ct);
        Assert.NotNull(day.MealPromptPostedAt);
    }

    // --- 기권("나 오늘 따로 먹어요") ---

    [Fact]
    public async Task 기권하면_RestaurantId가_null인_행이_생긴다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastAbstentionAsync(poll.Id, "U1", ct);

        var vote = await fixture.Db.PollVotes.SingleAsync(v => v.PollId == poll.Id && v.SlackUserId == "U1", ct);
        Assert.Null(vote.RestaurantId);
    }

    [Fact]
    public async Task 이미_기권한_사람이_다시_기권해도_행은_하나이고_시각만_갱신된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastAbstentionAsync(poll.Id, "U1", ct);
        await service.CastAbstentionAsync(poll.Id, "U1", ct);

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).ToListAsync(ct);
        Assert.Null(Assert.Single(votes).RestaurantId);
    }

    [Fact]
    public async Task 식당_투표_기권_식당_전환은_한_행만_남긴다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var b = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastVoteAsync(poll.Id, "U1", a.Id, ct);
        await service.CastAbstentionAsync(poll.Id, "U1", ct);
        await service.CastVoteAsync(poll.Id, "U1", b.Id, ct);

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).ToListAsync(ct);
        var vote = Assert.Single(votes);
        Assert.Equal(b.Id, vote.RestaurantId);
    }

    [Fact]
    public async Task GetTalliesAsync는_기권을_집계에서_제외한다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastVoteAsync(poll.Id, "U1", a.Id, ct);
        await service.CastAbstentionAsync(poll.Id, "U2", ct);
        await service.CastAbstentionAsync(poll.Id, "U3", ct);

        var tallies = await service.GetTalliesAsync(poll.Id, ct);

        var tally = Assert.Single(tallies);
        Assert.Equal(a.Id, tally.RestaurantId);
        Assert.Equal(1, tally.Count);
    }

    [Fact]
    public async Task GetAbstainersAsync는_기권한_사람만_돌려준다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastVoteAsync(poll.Id, "U1", a.Id, ct);
        await service.CastAbstentionAsync(poll.Id, "U2", ct);

        var abstainers = await service.GetAbstainersAsync(poll.Id, ct);

        Assert.Equal(["U2"], abstainers);
    }

    [Fact]
    public async Task 전원_기권이면_마감_결과에_승자가_없고_추천은_그대로_계산된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastAbstentionAsync(poll.Id, "U1", ct);
        await service.CastAbstentionAsync(poll.Id, "U2", ct);

        var outcome = await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        Assert.Null(outcome.Winner);
        Assert.Equal(2, outcome.Abstainers.Count);
        Assert.NotNull(outcome.Recommendation);
    }

    // --- 마감된 풀은 조용히 무시한다(IMPORTANT 3) ---

    [Fact]
    public async Task 마감된_풀에_투표해도_집계가_바뀌지_않는다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        await service.CastVoteAsync(poll.Id, "U1", a.Id, ct);

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).CountAsync(ct);
        Assert.Equal(0, votes);
    }

    [Fact]
    public async Task 마감된_풀에_기권해도_행이_생기지_않는다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        await service.CastAbstentionAsync(poll.Id, "U1", ct);

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).CountAsync(ct);
        Assert.Equal(0, votes);
    }

    // --- 결과 발표 재시도(CRITICAL 2) ---

    [Fact]
    public async Task GetClosedOutcomeAsync는_커밋된_값에서_승자와_추천을_그대로_복원한다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 한식집 = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 한식집.Id, ct);
        var closed = await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        var restored = await service.GetClosedOutcomeAsync(poll.Id, ct);

        Assert.Equal(closed.Winner, restored.Winner);
        Assert.Equal(closed.Recommendation!.Pick, restored.Recommendation!.Pick);
        Assert.Equal(closed.Recommendation.Winner, restored.Recommendation.Winner);
    }

    [Fact]
    public async Task GetClosedOutcomeAsync는_풀_상태를_바꾸지_않는다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        await service.GetClosedOutcomeAsync(poll.Id, ct);

        var saved = await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct);
        Assert.Equal(PollStatus.Closed, saved.Status);
    }

    [Fact]
    public async Task MarkResultAnnouncedAsync는_발표_시각을_남긴다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);
        Assert.Null((await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct)).ResultAnnouncedAt);

        await service.MarkResultAnnouncedAsync(poll.Id, ClosesAt, ct);

        var saved = await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct);
        Assert.Equal(ClosesAt, saved.ResultAnnouncedAt);
    }
}
