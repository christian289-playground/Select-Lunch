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

        // 후보 버튼 · 카테고리 드롭다운(카테고리 3개) · 랜덤+기권 버튼 — 기권은 후보 블록과 섞이지 않는다.
        var actionsBlocks = blocks.OfType<ActionsBlock>().ToList();
        Assert.Equal(3, actionsBlocks.Count);
        var button = AbstainActions(blocks).Elements.OfType<Button>()
            .Single(b => ActionIds.TryParseAbstain(b.ActionId, out _));
        Assert.True(ActionIds.TryParseAbstain(button.ActionId, out var pollId));
        Assert.Equal(1, pollId);
    }

    [Fact]
    public void 기권_버튼은_드롭다운으로_바뀌어도_보인다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], [], ClosesAt);

        var button = AbstainActions(blocks).Elements.OfType<Button>()
            .Single(b => ActionIds.TryParseAbstain(b.ActionId, out _));
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

        var button = AbstainActions(blocks).Elements.OfType<Button>()
            .Single(b => ActionIds.TryParseAbstain(b.ActionId, out _));
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

    [Theory]
    [InlineData(2, false)]
    [InlineData(11, false)]
    [InlineData(11, true)]
    public void Build_출발시각이마감전이면_투표방식과무관하게안내한다(int count, bool closed)
    {
        var candidates = Candidates(count).ToArray();
        candidates[0] = candidates[0] with { DepartureTime = new TimeOnly(11, 15) };

        var blocks = PollBlocks.Build(1, candidates, [], [],
            new DateTimeOffset(2026, 9, 18, 11, 30, 0, TimeSpan.FromHours(9)), closed);
        var lines = TextOf(blocks).Split('\n');

        Assert.Contains(lines, line => line.Contains("식당1") && line.Contains("11:15"));
        Assert.DoesNotContain(lines, line => line.Contains("식당2") && line.Contains("출발"));
    }

    [Fact]
    public void Build_긴이름의출발안내가많아도_슬랙본문상한을지킨다()
    {
        var candidates = Candidates(45)
            .Select(c => c with { Name = new string('가', 100), DepartureTime = new TimeOnly(11, 15) })
            .ToArray();

        var blocks = PollBlocks.Build(1, candidates, [], [], ClosesAt);
        var sections = blocks.OfType<SectionBlock>().Select(s => ((Markdown)s.Text).Text).ToArray();

        Assert.All(sections, text => Assert.True(text.Length <= 3000));
        Assert.Equal(45, sections.Sum(text => text.Split("11:15").Length - 1));
    }

    [Fact]
    public void 카테고리가_둘_이상이면_카테고리_드롭다운이_후보_카테고리를_옵션으로_갖는다()
    {
        var blocks = PollBlocks.Build(5, Candidates(6), [], [], ClosesAt);

        var menu = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements)
            .OfType<StaticSelectMenu>().Single(m => ActionIds.TryParseVoteCategory(m.ActionId, out _));
        Assert.True(ActionIds.TryParseVoteCategory(menu.ActionId, out var pollId));
        Assert.Equal(5, pollId);
        Assert.Equal(["카테고리1", "카테고리2", "카테고리3"], menu.Options.Select(o => o.Text.Text));
        Assert.Equal(["1", "2", "3"], menu.Options.Select(o => o.Value));
    }

    [Fact]
    public void 카테고리가_하나뿐이면_카테고리_드롭다운을_그리지_않는다()
    {
        var one = new[] { new RestaurantInfo(1, "식당1", 1, "한식", null, DateTimeOffset.UnixEpoch) };

        var blocks = PollBlocks.Build(1, one, [], [], ClosesAt);

        Assert.DoesNotContain(blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements).OfType<StaticSelectMenu>(),
            m => ActionIds.TryParseVoteCategory(m.ActionId, out _));
    }

    [Fact]
    public void 랜덤_투표_버튼은_기권_버튼과_함께_후보가_있을_때만_나온다()
    {
        var withCandidates = PollBlocks.Build(3, Candidates(3), [], [], ClosesAt);
        var empty = PollBlocks.Build(3, [], [], [], ClosesAt);

        var random = AbstainActions(withCandidates).Elements.OfType<Button>()
            .Single(b => ActionIds.TryParseVoteRandom(b.ActionId, out _));
        Assert.True(ActionIds.TryParseVoteRandom(random.ActionId, out var pollId));
        Assert.Equal(3, pollId);
        Assert.DoesNotContain(empty.OfType<ActionsBlock>().SelectMany(a => a.Elements).OfType<Button>(),
            b => ActionIds.TryParseVoteRandom(b.ActionId, out _));
    }

    [Fact]
    public void 비밀_선택은_랜덤_버튼_옆에_후보가_있을_때만_나온다()
    {
        var withCandidates = PollBlocks.Build(3, Candidates(3), [], [], ClosesAt);
        var empty = PollBlocks.Build(3, [], [], [], ClosesAt);

        var actions = AbstainActions(withCandidates);
        var secret = actions.Elements.OfType<StaticSelectMenu>()
            .Single(m => ActionIds.TryParseVoteSecret(m.ActionId, out _));
        Assert.True(ActionIds.TryParseVoteSecret(secret.ActionId, out var pollId));
        Assert.Equal(3, pollId);
        // 기존 랜덤 투표 버튼은 그대로 남는다.
        Assert.Single(actions.Elements.OfType<Button>(), b => ActionIds.TryParseVoteRandom(b.ActionId, out _));
        Assert.DoesNotContain(empty.OfType<ActionsBlock>().SelectMany(a => a.Elements).OfType<StaticSelectMenu>(),
            m => ActionIds.TryParseVoteSecret(m.ActionId, out _));
    }

    [Fact]
    public void 비밀_표만_있어도_투표_없음으로_보이지_않고_식당은_드러나지_않는다()
    {
        var candidates = Candidates(3);

        var blocks = PollBlocks.Build(1, candidates, [], [], ClosesAt, secretVotes: 2);

        var text = TextOf(blocks);
        Assert.Contains("2표", text);
        Assert.DoesNotContain("아직 투표가 없습니다", text);
        Assert.All(candidates, c => Assert.DoesNotContain(c.Name, text));
    }

    [Fact]
    public void 비밀_표는_일반_집계와_같은_목록에_함께_보인다()
    {
        VoteTally[] tallies = [new(1, "식당1", 3)];

        var blocks = PollBlocks.Build(1, Candidates(3), tallies, [], ClosesAt, secretVotes: 2);

        var tally = blocks.OfType<SectionBlock>().Select(s => ((Markdown)s.Text).Text)
            .Single(t => t.Contains("식당1"));
        Assert.Contains("3표", tally);
        Assert.Contains("2표", tally);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 마감되면_비밀_표_항목을_따로_그리지_않는다(bool hasTallies)
    {
        // 마감 집계(tallies)는 비밀 표를 실제 식당으로 풀어 담고 있다 — 따로 그리면 이중으로 보인다.
        var tallies = hasTallies ? new VoteTally[] { new(1, "식당1", 3) } : Array.Empty<VoteTally>();

        var without = PollBlocks.Build(1, Candidates(3), tallies, [], ClosesAt, closed: true);
        var with = PollBlocks.Build(1, Candidates(3), tallies, [], ClosesAt, closed: true, secretVotes: 2);

        Assert.Equal(TextOf(without), TextOf(with));
    }

    [Fact]
    public void 마감된_투표에는_랜덤_버튼도_카테고리_드롭다운도_없다()
    {
        var blocks = PollBlocks.Build(1, Candidates(6), [], [], ClosesAt, closed: true);

        Assert.Empty(blocks.OfType<ActionsBlock>());
    }

    [Fact]
    public void 테스트_모드는_모든_action_id가_test_접두라_실제_투표로_해석되지_않는다()
    {
        var blocks = PollBlocks.Build(0, Candidates(6), [], [], ClosesAt, testMode: true);

        var ids = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements)
            .Select(e => e switch
            {
                Button b => b.ActionId,
                StaticSelectMenu s => s.ActionId,
                ExternalSelectMenu x => x.ActionId,
                _ => throw new InvalidOperationException(e.GetType().Name),
            }).ToList();

        Assert.NotEmpty(ids);
        Assert.All(ids, id =>
        {
            Assert.True(ActionIds.TryUnwrapTest(id, out _));
            Assert.False(ActionIds.TryParseVote(id, out _, out _));
            Assert.False(ActionIds.TryParseVoteSelect(id, out _));
            Assert.False(ActionIds.TryParseVoteCategory(id, out _));
            Assert.False(ActionIds.TryParseVoteRandom(id, out _));
            Assert.False(ActionIds.TryParseVoteSecret(id, out _));
            Assert.False(ActionIds.TryParseAbstain(id, out _));
        });
        Assert.Contains(ids, id => ActionIds.TryUnwrapTest(id, out var inner)
            && ActionIds.TryParseVoteSecret(inner, out _));
        Assert.Contains("테스트", ((PlainText)blocks.OfType<HeaderBlock>().Single().Text).Text);
    }
}
