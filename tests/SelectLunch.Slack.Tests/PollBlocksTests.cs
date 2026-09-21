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

    /// <summary>후보 버튼/드롭다운이 담긴 첫 ActionsBlock. 기권 버튼은 별도 블록이라 섞이지 않는다.</summary>
    static ActionsBlock CandidateActions(IList<Block> blocks) => blocks.OfType<ActionsBlock>().First();

    /// <summary>기권 버튼만 담긴 ActionsBlock.</summary>
    static ActionsBlock AbstainActions(IList<Block> blocks) => blocks.OfType<ActionsBlock>()
        .Single(a => a.Elements.OfType<Button>().Any(b => ActionIds.TryParseAbstain(b.ActionId, out _)));

    static string TextOf(IList<Block> blocks) => string.Join("\n", blocks.OfType<SectionBlock>()
        .Select(s => (s.Text as Markdown)?.Text ?? ""));

    [Fact]
    public void 식당이_적으면_버튼으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], [], ClosesAt);

        var actions = CandidateActions(blocks);
        Assert.Equal(3, actions.Elements.OfType<Button>().Count());
    }

    [Fact]
    public void 식당이_많으면_드롭다운으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], [], ClosesAt);

        var actions = CandidateActions(blocks);
        Assert.Empty(actions.Elements.OfType<Button>());
        Assert.Single(actions.Elements.OfType<StaticSelectMenu>());
    }

    [Fact]
    public void 드롭다운은_카테고리별로_묶인다()
    {
        var blocks = PollBlocks.Build(pollId: 77, Candidates(12), [], [], ClosesAt);

        var menu = CandidateActions(blocks).Elements.OfType<StaticSelectMenu>().Single();
        Assert.Equal(3, menu.OptionGroups.Count);

        Assert.True(ActionIds.TryParseVoteSelect(menu.ActionId, out var pollId));
        Assert.Equal(77, pollId);
    }

    [Fact]
    public void 버튼의_action_id는_투표_규약을_따른다()
    {
        var blocks = PollBlocks.Build(pollId: 42, Candidates(1), [], [], ClosesAt);

        var button = CandidateActions(blocks).Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseVote(button.ActionId, out var pollId, out var restaurantId));
        Assert.Equal(42, pollId);
        Assert.Equal(1, restaurantId);
    }

    [Fact]
    public void 집계가_있으면_득표수를_표시한다()
    {
        VoteTally[] tallies = [new(1, "식당1", 3), new(2, "식당2", 1)];

        var blocks = PollBlocks.Build(1, Candidates(3), tallies, [], ClosesAt);

        var text = TextOf(blocks);
        Assert.Contains("식당1", text);
        Assert.Contains("3표", text);
    }

    [Fact]
    public void 아무도_투표하지_않으면_안내_문구를_보여준다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], [], ClosesAt);

        var text = TextOf(blocks);
        Assert.Contains("아직 투표가 없습니다", text);
    }

    [Fact]
    public void 후보가_없으면_등록을_안내한다()
    {
        var blocks = PollBlocks.Build(1, [], [], [], ClosesAt);

        var text = TextOf(blocks);
        Assert.Contains("/lunch add", text);
    }

    [Fact]
    public void 경계값_ButtonThreshold_정확히_버튼으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold), [], [], ClosesAt);

        var actions = CandidateActions(blocks);
        Assert.Equal(PollBlocks.ButtonThreshold, actions.Elements.OfType<Button>().Count());
        Assert.Empty(actions.Elements.OfType<StaticSelectMenu>());
    }

    [Fact]
    public void 경계값_초과하면_드롭다운으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], [], ClosesAt);

        var actions = CandidateActions(blocks);
        Assert.Empty(actions.Elements.OfType<Button>());
        Assert.Single(actions.Elements.OfType<StaticSelectMenu>());
    }

    [Fact]
    public void 식당이_너무_많으면_설명_메시지를_보여준다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.MaxSelectOptions + 1), [], [], ClosesAt);

        Assert.Empty(blocks.OfType<ActionsBlock>());
        var text = TextOf(blocks);
        Assert.Contains((PollBlocks.MaxSelectOptions + 1).ToString(), text);
    }

    // --- 기권("나 오늘 따로 먹어요") ---

    [Fact]
    public void 기권_버튼은_후보가_있어도_별도_블록에_있다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], [], ClosesAt);

        var actionsBlocks = blocks.OfType<ActionsBlock>().ToList();
        Assert.Equal(2, actionsBlocks.Count);
        var button = AbstainActions(blocks).Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseAbstain(button.ActionId, out var pollId));
        Assert.Equal(1, pollId);
    }

    [Fact]
    public void 기권_버튼은_드롭다운으로_바뀌어도_보인다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], [], ClosesAt);

        var button = AbstainActions(blocks).Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseAbstain(button.ActionId, out _));
    }

    [Fact]
    public void 기권_버튼은_후보가_0곳이어도_보인다()
    {
        var blocks = PollBlocks.Build(1, [], [], [], ClosesAt);

        var button = AbstainActions(blocks).Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseAbstain(button.ActionId, out _));
    }

    [Fact]
    public void 후보가_너무_많아_안내_메시지로_대체되면_기권_버튼도_없다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.MaxSelectOptions + 1), [], [], ClosesAt);

        Assert.Empty(blocks.OfType<ActionsBlock>());
    }

    [Fact]
    public void 기권자가_없으면_명단_줄이_없다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], [], ClosesAt);

        Assert.DoesNotContain("따로 먹어요", TextOf(blocks));
    }

    [Fact]
    public void 기권자가_있으면_명단_줄에_인원수와_멘션을_보여준다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], ["U1", "U2"], ClosesAt);

        var text = TextOf(blocks);
        Assert.Contains("따로 먹어요 (2)", text);
        Assert.Contains("<@U1>", text);
        Assert.Contains("<@U2>", text);
    }
}
