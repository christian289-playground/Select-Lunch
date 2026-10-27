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

        var menus = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<ExternalSelectMenu>());
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

        var buttons = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>());
        var menus = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<ExternalSelectMenu>());

        // "새 식당 등록" 버튼과는 별개로, 식당을 다시 고를 수 있는 버튼(meal:)이나 드롭다운이 남아 있어야 한다.
        Assert.True(
            buttons.Any(b => ActionIds.TryParseMeal(b.ActionId, out _, out _)) || menus.Any(),
            "식당을 다시 고를 수 있는 버튼(meal:) 또는 드롭다운이 있어야 한다.");
    }

    // --- 100곳 초과: 외부 선택으로 바뀌며 가드가 사라졌다 ---

    [Fact]
    public void 식당이_100곳을_넘어도_드롭다운이_나온다()
    {
        // 예전에는 안내문으로 대체했다 — 옵션을 메시지에 실어야 했기 때문이다.
        // 외부 선택은 옵션을 싣지 않으므로 몇 곳이든 드롭다운을 띄운다.
        var blocks = MealPromptBlocks.Build(Date, Restaurants(101), recordedName: null);

        var menus = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<ExternalSelectMenu>());
        Assert.Single(menus);
        Assert.DoesNotContain("표시할 수 없습니다", TextOf(blocks));
    }

    [Fact]
    public void 식당이_100곳을_넘어도_신규_등록_버튼은_남는다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(101), recordedName: null);

        var buttons = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>());
        Assert.Contains(buttons, b => ActionIds.TryParseMealNew(b.ActionId, out _));
    }

    [Fact]
    public void 기록_드롭다운은_옵션을_싣지_않는_외부_검색이다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(20), recordedName: null);

        var menu = blocks.OfType<ActionsBlock>()
            .SelectMany(a => a.Elements.OfType<ExternalSelectMenu>())
            .Single();

        Assert.Equal(0, menu.MinQueryLength);

        // 날짜는 action_id에 실려 제안 응답과 기록 처리가 같은 날을 본다.
        Assert.True(ActionIds.TryParseMeal(menu.ActionId, out var date, out var placeholder));
        Assert.Equal(Date, date);
        Assert.Equal(0, placeholder);
    }
}
