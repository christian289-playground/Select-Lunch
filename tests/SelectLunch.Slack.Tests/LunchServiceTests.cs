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
    public async Task 발송_이력을_남기면_오늘_상태에_반영된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        await service.MarkMealPromptPostedAsync(Today, ClosesAt, ct);

        var day = await fixture.Db.ChannelDays.SingleAsync(ct);
        Assert.NotNull(day.MealPromptPostedAt);
    }
}
