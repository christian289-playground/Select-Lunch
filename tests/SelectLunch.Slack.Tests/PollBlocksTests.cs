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
        Assert.Single(actions.Elements.OfType<ExternalSelectMenu>());
    }

    [Fact]
    public void 드롭다운은_옵션을_싣지_않는_외부_검색이다()
    {
        // 옵션을 메시지에 박아 넣으면 슬랙 클라이언트가 "단어 앞부분"으로만 검색해
        // "옛날경성순대국"을 "순대국"으로 찾을 수 없다. 외부 선택이라야 우리가 거른다.
        var blocks = PollBlocks.Build(pollId: 77, Candidates(12), [], [], ClosesAt);

        var menu = CandidateActions(blocks).Elements.OfType<ExternalSelectMenu>().Single();

        // 기본값 3이면 "순대"·"국밥" 같은 두 글자 검색이 막히고,
        // 0이라야 열자마자 전체 목록이 떠서 예전 동작과 이어진다.
        Assert.Equal(0, menu.MinQueryLength);

        // 어느 풀의 후보를 제안할지는 action_id의 pollId로 구분한다.
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
        Assert.Empty(actions.Elements.OfType<ExternalSelectMenu>());
    }

    [Fact]
    public void 경계값_초과하면_드롭다운으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], [], ClosesAt);

        var actions = CandidateActions(blocks);
        Assert.Empty(actions.Elements.OfType<Button>());
        Assert.Single(actions.Elements.OfType<ExternalSelectMenu>());
    }

    [Fact]
    public void 후보가_100곳을_넘어도_드롭다운으로_그린다()
    {
        // 예전에는 100곳을 넘으면 드롭다운을 포기하고 안내문만 남겼다(옵션을 메시지에
        // 실어야 했으므로). 외부 선택은 옵션을 싣지 않으므로 그 가드가 사라졌다.
        // 가드가 되살아나면 여기서 ActionsBlock이 비어 터진다.
        var blocks = PollBlocks.Build(1, Candidates(101), [], [], ClosesAt);

        Assert.Single(CandidateActions(blocks).Elements.OfType<ExternalSelectMenu>());
        Assert.DoesNotContain("담을 수 없습니다", TextOf(blocks));
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
    public void 후보가_100곳을_넘어도_기권_버튼이_남는다()
    {
        // 안내 메시지로 대체되던 시절에는 기권 버튼까지 같이 사라졌다.
        var blocks = PollBlocks.Build(1, Candidates(101), [], [], ClosesAt);

        var button = AbstainActions(blocks).Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseAbstain(button.ActionId, out _));
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

    // --- 마감된 풀은 버튼을 남기지 않는다(IMPORTANT 3) ---

    [Fact]
    public void 마감되면_후보_버튼도_기권_버튼도_없다()
    {
        VoteTally[] tallies = [new(1, "식당1", 3)];
        var blocks = PollBlocks.Build(1, Candidates(3), tallies, [], ClosesAt, closed: true);

        Assert.Empty(blocks.OfType<ActionsBlock>());
    }

    [Fact]
    public void 마감되면_드롭다운_후보여도_버튼이_없다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], [], ClosesAt, closed: true);

        Assert.Empty(blocks.OfType<ActionsBlock>());
    }

    [Fact]
    public void 마감되면_집계는_그대로_보인다()
    {
        VoteTally[] tallies = [new(1, "식당1", 3)];
        var blocks = PollBlocks.Build(1, Candidates(3), tallies, [], ClosesAt, closed: true);

        var text = TextOf(blocks);
        Assert.Contains("식당1", text);
        Assert.Contains("3표", text);
    }

    [Fact]
    public void 마감되면_마감_안내_문구로_바뀐다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], [], ClosesAt, closed: true);

        var context = blocks.OfType<ContextBlock>().Single();
        var text = string.Join("\n", context.Elements.OfType<Markdown>().Select(m => m.Text));
        Assert.Contains("마감되었습니다", text);
    }

    [Fact]
    public void 마감되고_후보가_없어도_기권_버튼이_없다()
    {
        var blocks = PollBlocks.Build(1, [], [], [], ClosesAt, closed: true);

        Assert.Empty(blocks.OfType<ActionsBlock>());
    }
}
