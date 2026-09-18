using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class MealPromptBlocksTests
{
    static readonly DateOnly Date = new(2026, 9, 18);

    static IReadOnlyList<RestaurantInfo> Restaurants(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new RestaurantInfo(
            i, $"식당{i}", 1, "한식", null, DateTimeOffset.UnixEpoch))];

    static string TextOf(IList<Block> blocks) =>
        string.Join("\n", blocks.OfType<SectionBlock>().Select(s => (s.Text as Markdown)?.Text ?? ""));

    [Fact]
    public void 신규_등록_버튼이_항상_있다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(3), recordedName: null);

        var buttons = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>());
        Assert.Contains(buttons, b => ActionIds.TryParseMealNew(b.ActionId, out _));
    }

    [Fact]
    public void 식당이_하나도_없어도_신규_등록은_할_수_있다()
    {
        var blocks = MealPromptBlocks.Build(Date, [], recordedName: null);

        var buttons = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>());
        Assert.Contains(buttons, b => ActionIds.TryParseMealNew(b.ActionId, out _));
    }

    [Fact]
    public void 식당이_많으면_드롭다운으로_고른다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(20), recordedName: null);

        var menus = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<StaticSelectMenu>());
        Assert.Single(menus);
    }

    [Fact]
    public void 이미_기록되면_기록된_식당을_보여준다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(3), recordedName: "스시로");

        Assert.Contains("스시로", TextOf(blocks));
    }

    [Fact]
    public void 기록_후에도_정정할_수_있게_선택지를_남긴다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(3), recordedName: "스시로");

        Assert.NotEmpty(blocks.OfType<ActionsBlock>());
    }
}
