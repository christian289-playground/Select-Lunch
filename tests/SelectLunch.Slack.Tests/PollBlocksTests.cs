using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class PollBlocksTests
{
    static readonly DateTimeOffset ClosesAt =
        new(2026, 9, 18, 11, 0, 0, TimeSpan.FromHours(9));

    static IReadOnlyList<RestaurantInfo> Candidates(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new RestaurantInfo(
            i, $"식당{i}", 1 + i % 3, $"카테고리{1 + i % 3}", null,
            DateTimeOffset.UnixEpoch))];

    [Fact]
    public void 식당이_적으면_버튼으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], ClosesAt);

        var actions = blocks.OfType<ActionsBlock>().Single();
        Assert.Equal(3, actions.Elements.OfType<Button>().Count());
    }

    [Fact]
    public void 식당이_많으면_드롭다운으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], ClosesAt);

        var actions = blocks.OfType<ActionsBlock>().Single();
        Assert.Empty(actions.Elements.OfType<Button>());
        Assert.Single(actions.Elements.OfType<StaticSelectMenu>());
    }

    [Fact]
    public void 드롭다운은_카테고리별로_묶인다()
    {
        var blocks = PollBlocks.Build(1, Candidates(12), [], ClosesAt);

        var menu = blocks.OfType<ActionsBlock>().Single().Elements.OfType<StaticSelectMenu>().Single();
        Assert.Equal(3, menu.OptionGroups.Count);
    }

    [Fact]
    public void 버튼의_action_id는_투표_규약을_따른다()
    {
        var blocks = PollBlocks.Build(pollId: 42, Candidates(1), [], ClosesAt);

        var button = blocks.OfType<ActionsBlock>().Single().Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseVote(button.ActionId, out var pollId, out var restaurantId));
        Assert.Equal(42, pollId);
        Assert.Equal(1, restaurantId);
    }

    [Fact]
    public void 집계가_있으면_득표수를_표시한다()
    {
        VoteTally[] tallies = [new(1, "식당1", 3), new(2, "식당2", 1)];

        var blocks = PollBlocks.Build(1, Candidates(3), tallies, ClosesAt);

        var text = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? ""));
        Assert.Contains("식당1", text);
        Assert.Contains("3표", text);
    }

    [Fact]
    public void 아무도_투표하지_않으면_안내_문구를_보여준다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], ClosesAt);

        var text = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? ""));
        Assert.Contains("아직 투표가 없습니다", text);
    }

    [Fact]
    public void 후보가_없으면_등록을_안내한다()
    {
        var blocks = PollBlocks.Build(1, [], [], ClosesAt);

        Assert.Empty(blocks.OfType<ActionsBlock>());
        var text = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? ""));
        Assert.Contains("/lunch add", text);
    }
}
