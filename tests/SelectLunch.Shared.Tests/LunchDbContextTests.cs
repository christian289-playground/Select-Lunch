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
        await fixture.Db.SaveChangesAsync();

        fixture.Db.Restaurants.Add(NewRestaurant("서브웨이"));

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 공백만_다른_이름은_같은_식당으로_본다()
    {
        Assert.Equal(Restaurant.Normalize("서브웨이"), Restaurant.Normalize("서브 웨이"));
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
        await fixture.Db.SaveChangesAsync();

        fixture.Db.PollVotes.Add(new PollVote
        {
            PollId = poll.Id, SlackUserId = "U1",
            RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Db.PollVotes.Add(new PollVote
        {
            PollId = poll.Id, SlackUserId = "U1",
            RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 한_채널의_하루_식사_기록은_하나뿐이다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = NewRestaurant("스시로");
        fixture.Db.Restaurants.Add(restaurant);
        await fixture.Db.SaveChangesAsync();

        var date = new DateOnly(2026, 9, 18);
        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = date, RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = date, RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U2", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 채널_하루_상태는_채널과_날짜로_유일하다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var date = new DateOnly(2026, 9, 18);
        fixture.Db.ChannelDays.Add(new ChannelDay { ChannelId = "C1", Date = date });
        await fixture.Db.SaveChangesAsync();

        var found = await fixture.Db.ChannelDays.FindAsync("C1", date);

        Assert.NotNull(found);
    }
}
