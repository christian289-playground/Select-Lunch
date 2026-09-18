using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Tests;

public class ActionIdsTests
{
    [Fact]
    public void 투표_action_id를_왕복_변환한다()
    {
        var id = ActionIds.Vote(pollId: 12, restaurantId: 34);

        Assert.True(ActionIds.TryParseVote(id, out var pollId, out var restaurantId));
        Assert.Equal(12, pollId);
        Assert.Equal(34, restaurantId);
    }

    [Fact]
    public void 기록_action_id를_왕복_변환한다()
    {
        var date = new DateOnly(2026, 9, 18);

        var id = ActionIds.Meal(date, restaurantId: 7);

        Assert.True(ActionIds.TryParseMeal(id, out var parsedDate, out var restaurantId));
        Assert.Equal(date, parsedDate);
        Assert.Equal(7, restaurantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vote")]
    [InlineData("vote:abc:34")]
    [InlineData("meal:2026-09-18:7")]
    public void 형식이_다르면_투표_파싱에_실패한다(string id)
    {
        Assert.False(ActionIds.TryParseVote(id, out _, out _));
    }

    [Fact]
    public void 서로_다른_종류의_action_id는_섞이지_않는다()
    {
        var mealNew = ActionIds.MealNew(new DateOnly(2026, 9, 18));

        Assert.False(ActionIds.TryParseMeal(mealNew, out _, out _));
        Assert.True(ActionIds.TryParseMealNew(mealNew, out _));
    }

    [Fact]
    public void 투표선택_action_id를_왕복_변환한다()
    {
        var id = ActionIds.VoteSelect(pollId: 99);

        Assert.True(ActionIds.TryParseVoteSelect(id, out var pollId));
        Assert.Equal(99, pollId);
    }

    [Fact]
    public void 식당추가_action_id를_왕복_변환한다()
    {
        var id = ActionIds.RestaurantFill(restaurantId: 77);

        Assert.True(ActionIds.TryParseRestaurantFill(id, out var restaurantId));
        Assert.Equal(77, restaurantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vote_select")]
    [InlineData("vote_select:abc")]
    public void 형식이_다르면_투표선택_파싱에_실패한다(string id)
    {
        Assert.False(ActionIds.TryParseVoteSelect(id, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("meal")]
    [InlineData("meal:2026-09-18")]
    [InlineData("meal:2026-13-01:7")]
    public void 형식이_다르면_기록_파싱에_실패한다(string id)
    {
        Assert.False(ActionIds.TryParseMeal(id, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("meal_new")]
    [InlineData("meal_new:2026-13-01")]
    public void 형식이_다르면_신규기록_파싱에_실패한다(string id)
    {
        Assert.False(ActionIds.TryParseMealNew(id, out _));
    }

    [Fact]
    public void 드롭다운의_action_id는_선택_규약을_따른다()
    {
        var id = ActionIds.VoteSelect(pollId: 55);

        Assert.True(ActionIds.TryParseVoteSelect(id, out var pollId));
        Assert.Equal(55, pollId);
    }
}
