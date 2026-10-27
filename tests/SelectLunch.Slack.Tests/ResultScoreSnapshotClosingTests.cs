using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 마감이 남기는 점수 상세가 그 뒤의 설정·식사 기록·식당 이름·새 DbContext·재마감에 흔들리지 않는지 —
/// 모달이 읽는 값(<c>RationaleJson</c>의 상세 문장)만 본다.
/// </summary>
public class ResultScoreSnapshotClosingTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    static readonly RecommendationOptions CloseOptions = new();

    sealed record Scene(TestDb Fixture, LunchService Service, Restaurant 한식집, LunchPoll Poll);

    /// <summary>
    /// 카테고리(1 한식·2 중식·3 일식)는 마이그레이션 시드. 한식 6점(8일 전·30일내 2회)이 1위,
    /// 중식 1점, 일식 −7점이고 한식집이 표도 받는다.
    /// </summary>
    static async Task<Scene> OpenSceneAsync(CancellationToken ct)
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var 한식집 = await service.SaveRestaurantAsync(Draft(null, "김밥천국", 1), "U1", ct);
        var 중식집 = await service.SaveRestaurantAsync(Draft(null, "마라탕집", 2), "U1", ct);
        var 일식집 = await service.SaveRestaurantAsync(Draft(null, "스시로", 3), "U1", ct);
        fixture.Db.MealRecords.AddRange(
            Meal(한식집, -8), Meal(한식집, -20), Meal(중식집, -5), Meal(일식집, -1), Meal(일식집, -3));
        await fixture.Db.SaveChangesAsync(ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 한식집.Id, ct);
        return new Scene(fixture, service, 한식집, poll);
    }

    static RestaurantDraft Draft(long? id, string name, long categoryId) =>
        new(id, name, categoryId, null, null, null);

    static MealRecord Meal(Restaurant restaurant, int daysFromToday) => new()
    {
        ChannelId = Channel, Date = Today.AddDays(daysFromToday), RestaurantId = restaurant.Id,
        RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch, Source = MealSource.Prompt,
    };

    /// <summary>같은 연결을 쓰는 새 DbContext — 재시작 뒤 새 scope가 만나는 상태.</summary>
    static LunchDbContext NewContext(TestDb fixture) =>
        new(new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(fixture.Db.Database.GetDbConnection())
            .Options);

    static Task<LunchPoll> ReadPollAsync(LunchDbContext db, long pollId, CancellationToken ct) =>
        db.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId, ct);

    /// <summary>마감 뒤 벌어질 수 있는 변화: 식사 기록 추가, 식당 이름 변경.</summary>
    static async Task ChangeWorldAfterCloseAsync(Scene scene, CancellationToken ct)
    {
        scene.Fixture.Db.MealRecords.Add(Meal(scene.한식집, 0));
        await scene.Fixture.Db.SaveChangesAsync(ct);
        await scene.Service.SaveRestaurantAsync(Draft(scene.한식집.Id, "새이름집", 1), "U1", ct);
    }

    static readonly RecommendationOptions ChangedOptions = new() { DaysSinceCap = 10, Weight7d = 10, Weight30d = 2 };
    static readonly DateOnly LaterToday = Today.AddDays(3);

    [Fact]
    public async Task 마감_뒤_기록_이름_설정이_바뀌고_새_컨텍스트로_재마감해도_점수_상세는_마감_때_그대로다()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await OpenSceneAsync(ct);
        await using var _ = scene.Fixture;

        var closed = await scene.Service.ClosePollAsync(scene.Poll.Id, Today, CloseOptions, ct);

        // 닫힌 행에는 그 순간부터 상세가 함께 있다(마감과 같은 저장).
        var closedRow = await ReadPollAsync(scene.Fixture.Db, scene.Poll.Id, ct);
        Assert.Equal(PollStatus.Closed, closedRow.Status);
        var frozenJson = closedRow.RationaleJson;
        var frozenText = ResultScoreSnapshot.GetDetailsText(frozenJson);
        Assert.NotNull(frozenText);
        Assert.Contains("김밥천국", frozenText);
        Assert.Contains("한식", frozenText);
        Assert.Contains("중식", frozenText);
        Assert.Contains("일식", frozenText);

        await ChangeWorldAfterCloseAsync(scene, ct);

        // 이 테스트가 "다시 계산"을 잡아낼 수 있는지 — 지금 상태·바뀐 설정으로 풀면 문장이 달라야 한다.
        var recomputed = RecommendationEngine.Recommend(
            LaterToday,
            await scene.Fixture.Db.GetCategoryStatsAsync(LaterToday, ct),
            await scene.Fixture.Db.GetActiveRestaurantsAsync(ct),
            ChangedOptions);
        Assert.NotEqual(frozenText, ResultBlocks.Rationale(recomputed!, ChangedOptions));

        await using var freshDb = NewContext(scene.Fixture);
        var fresh = new LunchService(freshDb, Channel);
        var reclosed = await fresh.ClosePollAsync(scene.Poll.Id, LaterToday, ChangedOptions, ct);

        var reloaded = await ReadPollAsync(freshDb, scene.Poll.Id, ct);
        Assert.Equal(frozenJson, reloaded.RationaleJson);
        var text = ResultScoreSnapshot.GetDetailsText(reloaded.RationaleJson);
        Assert.Equal(frozenText, text);
        Assert.DoesNotContain("새이름집", text);

        // 재마감이 돌려준 추천도 마감 때의 것이다.
        Assert.Equal(closed.Recommendation!.Pick, reclosed.Recommendation!.Pick);
        Assert.Equal(closed.Recommendation.Winner, reclosed.Recommendation.Winner);
        Assert.Equal(closed.Recommendation.Others, reclosed.Recommendation.Others);
    }

    [Fact]
    public async Task 상세_문장이_없는_옛_마감_행은_저장된_점수만으로_풀이하고_재마감해도_다시_쓰지_않는다()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await OpenSceneAsync(ct);
        await using var _ = scene.Fixture;
        var closed = await scene.Service.ClosePollAsync(scene.Poll.Id, Today, CloseOptions, ct);
        var stored = closed.Recommendation!;

        // 이 기능 이전 릴리스가 남긴 행: 루트 Pick·Winner·Others만 있다.
        var legacyJson = JsonSerializer.Serialize(new { stored.Pick, stored.Winner, stored.Others });
        var tracked = await scene.Fixture.Db.Polls.SingleAsync(p => p.Id == scene.Poll.Id, ct);
        tracked.RationaleJson = legacyJson;
        await scene.Fixture.Db.SaveChangesAsync(ct);

        await ChangeWorldAfterCloseAsync(scene, ct);

        await using var freshDb = NewContext(scene.Fixture);
        var fresh = new LunchService(freshDb, Channel);
        var restored = await fresh.GetClosedOutcomeAsync(scene.Poll.Id, ct);
        await fresh.ClosePollAsync(scene.Poll.Id, LaterToday, ChangedOptions, ct);

        Assert.Equal(stored.Pick, restored.Recommendation!.Pick);
        Assert.Equal(stored.Winner, restored.Recommendation.Winner);
        var row = await ReadPollAsync(freshDb, scene.Poll.Id, ct);
        Assert.Equal(legacyJson, row.RationaleJson);

        var text = ResultScoreSnapshot.GetDetailsText(row.RationaleJson);
        Assert.Equal(ResultBlocks.Rationale(stored, null), text);
        Assert.Contains("김밥천국", text);
        Assert.DoesNotContain("새이름집", text);
        Assert.DoesNotContain("×", text);
    }

    [Fact]
    public async Task 추천할_식당이_없던_마감은_상세가_없고_식당이_생긴_뒤_재마감해도_만들어지지_않는다()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        var closed = await service.ClosePollAsync(poll.Id, Today, CloseOptions, ct);

        Assert.Null(closed.Recommendation);
        var row = await ReadPollAsync(fixture.Db, poll.Id, ct);
        Assert.Equal(PollStatus.Closed, row.Status);
        Assert.Null(row.RecommendedRestaurantId);
        Assert.Null(row.RationaleJson);
        Assert.Null(ResultScoreSnapshot.GetDetailsText(row.RationaleJson));

        await service.SaveRestaurantAsync(Draft(null, "뒤늦은식당", 1), "U1", ct);
        await using var freshDb = NewContext(fixture);
        var reclosed = await new LunchService(freshDb, Channel).ClosePollAsync(poll.Id, LaterToday, ChangedOptions, ct);

        Assert.Null(reclosed.Recommendation);
        Assert.Null((await ReadPollAsync(freshDb, poll.Id, ct)).RationaleJson);
    }
}
