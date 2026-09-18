using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Tests;

public class LunchDbContextTests
{
    static Restaurant NewRestaurant(string name, long? categoryId = null) => new()
    {
        Name = name,
        NormalizedName = Restaurant.Normalize(name),
        CategoryId = categoryId,
        Status = categoryId is null ? RestaurantStatus.Pending : RestaurantStatus.Active,
        CreatedBySlackUserId = "U1",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task 같은_이름의_식당을_두_번_등록할_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        fixture.Db.Restaurants.Add(NewRestaurant("서브웨이"));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.Restaurants.Add(NewRestaurant("서브웨이"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 공백만_다른_이름은_같은_식당으로_본다()
    {
        Assert.Equal(Restaurant.Normalize("서브웨이"), Restaurant.Normalize("서브 웨이"));
    }

    [Fact]
    public void 조합형과_완성형_한글_표기는_같은_식당으로_본다()
    {
        // "서브웨이"를 NFD(조합형: 자모가 분해된 형태)로 표현한 것과
        // NFC(완성형)로 표현한 것은 바이트가 다르지만 같은 식당을 가리켜야 한다.
        var precomposed = "서브웨이".Normalize(System.Text.NormalizationForm.FormC);
        var decomposed = "서브웨이".Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual(precomposed, decomposed); // 전제: 두 표기는 실제로 바이트가 다르다.

        Assert.Equal(Restaurant.Normalize(precomposed), Restaurant.Normalize(decomposed));
    }

    [Fact]
    public async Task 같은_이름의_카테고리를_두_번_등록할_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        fixture.Db.Categories.Add(new Category { Name = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.Categories.Add(new Category { Name = "일식", IsBuiltIn = false, CreatedAt = DateTimeOffset.UnixEpoch });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 한_사람은_한_투표에_한_표만_가진다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var category = new Category { Name = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch };
        fixture.Db.Categories.Add(category);
        var restaurant = NewRestaurant("스시로");
        restaurant.Category = category;
        fixture.Db.Restaurants.Add(restaurant);
        var poll = new LunchPoll
        {
            ChannelId = "C1",
            Date = new DateOnly(2026, 9, 18),
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
        };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.PollVotes.Add(new PollVote
        {
            PollId = poll.Id, SlackUserId = "U1",
            RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.PollVotes.Add(new PollVote
        {
            PollId = poll.Id, SlackUserId = "U1",
            RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 같은_채널_같은_날짜의_투표를_두_번_열_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var date = new DateOnly(2026, 9, 18);
        fixture.Db.Polls.Add(new LunchPoll
        {
            ChannelId = "C1",
            Date = date,
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.Polls.Add(new LunchPoll
        {
            ChannelId = "C1",
            Date = date,
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 같은_투표에_같은_식당을_후보로_두_번_넣을_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = NewRestaurant("스시로");
        fixture.Db.Restaurants.Add(restaurant);
        var poll = new LunchPoll
        {
            ChannelId = "C1",
            Date = new DateOnly(2026, 9, 18),
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
        };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.PollCandidates.Add(new PollCandidate { PollId = poll.Id, RestaurantId = restaurant.Id, DisplayOrder = 0 });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // 변경 추적기가 동일 키를 이미 추적 중이면 SaveChanges 전, Add 시점에
        // InvalidOperationException을 던져버려 실제 DB 제약을 검증하지 못한다.
        // 추적을 비워서 두 번째 삽입이 진짜로 SQLite까지 도달하게 한다.
        fixture.Db.ChangeTracker.Clear();

        fixture.Db.PollCandidates.Add(new PollCandidate { PollId = poll.Id, RestaurantId = restaurant.Id, DisplayOrder = 1 });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 한_채널의_하루_식사_기록은_하나뿐이다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = NewRestaurant("스시로");
        fixture.Db.Restaurants.Add(restaurant);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var date = new DateOnly(2026, 9, 18);
        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = date, RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = date, RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U2", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 식사_기록이_참조하는_식당은_삭제할_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = NewRestaurant("스시로");
        fixture.Db.Restaurants.Add(restaurant);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = new DateOnly(2026, 9, 18), RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // MealRecord가 계속 추적 중인 상태로 Restaurant를 지우면, EF 변경 추적기가
        // Remove() 호출 시점에 (DB까지 가지도 않고) InvalidOperationException을 먼저
        // 던져버린다. 추적을 비우고 다시 불러와서, 삭제가 실제로 SQLite까지 가서
        // 진짜 FK 제약에 부딪히게 한다.
        fixture.Db.ChangeTracker.Clear();
        var reloaded = await fixture.Db.Restaurants.FindAsync(
            [restaurant.Id], TestContext.Current.CancellationToken);
        fixture.Db.Restaurants.Remove(reloaded!);

        // FK가 Cascade로 방치되면 이 삭제가 식사 기록까지 조용히 지워버린다 —
        // 추천 알고리즘의 유일한 입력인 기록을 잃을 수 없으므로 Restrict로
        // 막고 DbUpdateException을 던져야 한다.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 채널_하루_상태를_같은_채널_같은_날짜로_두_번_등록할_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var date = new DateOnly(2026, 9, 18);
        fixture.Db.ChannelDays.Add(new ChannelDay { ChannelId = "C1", Date = date });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // PollCandidate와 같은 이유로, 복합 기본 키의 진짜 DB 제약을 보려면
        // 추적기 안에 남아있는 첫 번째 엔터티를 먼저 비워야 한다.
        fixture.Db.ChangeTracker.Clear();

        fixture.Db.ChannelDays.Add(new ChannelDay { ChannelId = "C1", Date = date });

        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
