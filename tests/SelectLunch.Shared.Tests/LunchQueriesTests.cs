using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Tests;

public class LunchQueriesTests
{
    static readonly DateOnly Today = new(2026, 9, 18);
    const string Channel = "C1";

    static async Task<TestDb> SeedAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var db = fixture.Db;

        // 카테고리(1 한식 · 3 일식)는 마이그레이션 시드로 이미 들어 있다(Task 8).
        // 다시 넣으면 Categories.Name UNIQUE 제약에 걸린다 — 시드된 Id를 그대로 참조한다.
        db.Restaurants.AddRange(
            Restaurant(10, "김밥천국", 1, RestaurantStatus.Active),
            Restaurant(20, "스시로", 3, RestaurantStatus.Active),
            Restaurant(30, "이름만아는집", null, RestaurantStatus.Pending));

        await db.SaveChangesAsync();
        return fixture;
    }

    static Restaurant Restaurant(long id, string name, long? categoryId, RestaurantStatus status) => new()
    {
        Id = id, Name = name, NormalizedName = Entities.Restaurant.Normalize(name),
        CategoryId = categoryId, Status = status, CreatedBySlackUserId = "U1",
        CreatedAt = new DateTimeOffset(2026, 1, (int)(id / 10), 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static MealRecord Meal(DateOnly date, long restaurantId) => new()
    {
        ChannelId = Channel, Date = date, RestaurantId = restaurantId,
        RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch,
        Source = MealSource.Prompt,
    };

    [Fact]
    public async Task Active_식당만_추천_후보로_조회된다()
    {
        await using var fixture = await SeedAsync();

        var restaurants = await fixture.Db.GetActiveRestaurantsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, restaurants.Count);
        Assert.DoesNotContain(restaurants, r => r.Name == "이름만아는집");
        Assert.Contains(restaurants, r => r is { Name: "스시로", CategoryName: "일식" });
    }

    [Fact]
    public async Task 식당별_마지막_방문일이_채워진다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.Add(Meal(new DateOnly(2026, 8, 21), 20));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var restaurants = await fixture.Db.GetActiveRestaurantsAsync(TestContext.Current.CancellationToken);

        var 스시로 = restaurants.Single(r => r.Name == "스시로");
        Assert.Equal(new DateOnly(2026, 8, 21), 스시로.LastEatenOn);
        Assert.Null(restaurants.Single(r => r.Name == "김밥천국").LastEatenOn);
    }

    [Fact]
    public async Task 카테고리_통계는_7일과_30일_횟수를_센다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.AddRange(
            Meal(Today.AddDays(-2), 10),    // 한식, 7일 안
            Meal(Today.AddDays(-5), 10),    // 한식, 7일 안
            Meal(Today.AddDays(-20), 10),   // 한식, 30일 안
            Meal(Today.AddDays(-40), 10));  // 한식, 범위 밖
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        var 한식 = stats.Single(s => s.CategoryName == "한식");
        Assert.Equal(2, 한식.Count7d);
        Assert.Equal(3, 한식.Count30d);
        Assert.Equal(Today.AddDays(-2), 한식.LastEatenOn);
    }

    [Fact]
    public async Task Active_식당이_없는_카테고리는_통계에서_빠진다()
    {
        await using var fixture = await SeedAsync();

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.Count);   // 한식, 일식만
    }

    [Fact]
    public async Task Pending_식당의_기록은_카테고리_통계에_들어가지_않는다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.Add(Meal(Today.AddDays(-1), 30));   // Pending 식당
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        Assert.All(stats, s => Assert.Equal(0, s.Count7d));
    }

    [Fact]
    public async Task 오늘_상태는_투표와_발송_이력을_함께_읽는다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.Polls.Add(new LunchPoll
        {
            Id = 1, ChannelId = Channel, Date = Today,
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
            Status = PollStatus.Open,
        });
        fixture.Db.ChannelDays.Add(new ChannelDay
        {
            ChannelId = Channel, Date = Today,
            MealPromptPostedAt = DateTimeOffset.UnixEpoch,
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var state = await fixture.Db.GetTodayStateAsync(Channel, Today, TestContext.Current.CancellationToken);

        Assert.Equal(PollStatus.Open, state.Poll!.Status);
        Assert.True(state.MealPromptPosted);
        Assert.True(state.HasPendingRestaurants);   // "이름만아는집"
        Assert.Null(state.LastPendingReminderOn);
    }

    [Fact]
    public async Task 투표가_없는_날의_오늘_상태는_Poll이_null이다()
    {
        await using var fixture = await SeedAsync();

        var state = await fixture.Db.GetTodayStateAsync(Channel, Today, TestContext.Current.CancellationToken);

        Assert.Null(state.Poll);
        Assert.False(state.MealPromptPosted);
    }

    [Fact]
    public async Task 알림_발송_이력이_있으면_LastPendingReminderOn에_오늘_날짜가_채워진다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.ChannelDays.Add(new ChannelDay
        {
            ChannelId = Channel, Date = Today,
            PendingReminderSentAt = DateTimeOffset.UnixEpoch,
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var state = await fixture.Db.GetTodayStateAsync(Channel, Today, TestContext.Current.CancellationToken);

        Assert.Equal(Today, state.LastPendingReminderOn);
    }

    [Fact]
    public async Task Archived_식당의_과거_기록은_카테고리_통계에_남고_추천_후보에서는_빠진다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.Restaurants.Add(Restaurant(40, "옛날국밥집", 1, RestaurantStatus.Archived));
        fixture.Db.MealRecords.Add(Meal(Today.AddDays(-1), 40));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);
        var restaurants = await fixture.Db.GetActiveRestaurantsAsync(TestContext.Current.CancellationToken);

        var 한식 = stats.Single(s => s.CategoryName == "한식");
        Assert.Equal(1, 한식.Count7d);   // 폐업 전 먹은 기록은 그대로 카테고리 이력에 남는다
        Assert.DoesNotContain(restaurants, r => r.Name == "옛날국밥집");   // 투표·추천 후보에서는 제외
    }

    [Fact]
    public async Task 미래_날짜로_잘못_기록된_식사는_기간_집계에서_빠지지만_LastEatenOn엔_그대로_반영된다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.Add(Meal(Today.AddDays(1), 10));   // 잘못된 미래 날짜 — 방어 로직 없음(현재 동작 고정)
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        var 한식 = stats.Single(s => s.CategoryName == "한식");
        Assert.Equal(0, 한식.Count7d);    // 기간 집계는 오늘 이후 날짜를 포함하지 않는다
        Assert.Equal(0, 한식.Count30d);
        Assert.Equal(Today.AddDays(1), 한식.LastEatenOn);   // LastEatenOn은 상한이 없어 미래 날짜도 그대로 반영한다
    }
}
