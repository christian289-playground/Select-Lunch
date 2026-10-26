using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Leave;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class PollLeaveBlocksTests
{
    static readonly DateTimeOffset ClosesAt = new(2026, 10, 26, 11, 30, 0, TimeSpan.FromHours(9));
    static readonly IReadOnlyList<RestaurantInfo> Candidates =
        [new RestaurantInfo(1, "식당", 1, "한식", null, DateTimeOffset.UnixEpoch)];

    static PlainText[] PlainSections(IList<Block> blocks) =>
        blocks.OfType<SectionBlock>().Select(b => b.Text).OfType<PlainText>().ToArray();

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void 휴가자는_진행중_마감_미리보기_빈후보에서도_일반텍스트로_표시한다(
        bool closed, bool testMode, bool emptyCandidates)
    {
        var snapshot = new LeaveRosterSnapshot(LeaveRosterStatus.Available, ["개발팀_홍길동", "철수"]);
        var blocks = PollBlocks.Build(1, emptyCandidates ? [] : Candidates, [], [], ClosesAt,
            closed: closed, testMode: testMode, leaveRoster: snapshot);
        var text = string.Join("\n", PlainSections(blocks).Select(p => p.Text));
        Assert.Contains("개발팀_홍길동", text);
        Assert.Contains("철수", text);
        Assert.DoesNotContain("<@", text);
        Assert.DoesNotContain("<!", text);
    }

    [Fact]
    public void 별칭에_슬랙문법이_있어도_Markdown이나_멘션으로_해석시키지_않는다()
    {
        const string alias = "이름 <@U123> & @channel";
        var blocks = PollBlocks.Build(1, Candidates, [], [], ClosesAt,
            leaveRoster: new(LeaveRosterStatus.Available, [alias]));
        var section = Assert.Single(PlainSections(blocks));
        Assert.Contains(alias, section.Text);
        Assert.DoesNotContain(blocks.OfType<SectionBlock>().Select(b => b.Text).OfType<Markdown>(),
            m => m.Text.Contains(alias, StringComparison.Ordinal));
    }

    [Fact]
    public void 미확인이나_비활성_결과에서_붙어온_이름을_공개하지_않는다()
    {
        foreach (var status in new[] { LeaveRosterStatus.Disabled, LeaveRosterStatus.Unavailable })
        {
            var blocks = PollBlocks.Build(1, Candidates, [], [], ClosesAt,
                leaveRoster: new(status, ["공개하면안되는이름"]));
            Assert.DoesNotContain(PlainSections(blocks), p => p.Text.Contains("공개하면안되는이름", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void 긴_명단은_모든_이름을_보존하면서_슬랙_텍스트_상한_안으로_나눈다()
    {
        var aliases = Enumerable.Range(0, 30).Select(i => $"이름{i:D2}" + new string('가', 190)).ToArray();
        var sections = PlainSections(PollBlocks.Build(1, Candidates, [], [], ClosesAt,
            leaveRoster: new(LeaveRosterStatus.Available, aliases)));
        var combined = string.Join("\n", sections.Select(s => s.Text));
        Assert.All(sections, s => Assert.InRange(s.Text.Length, 1, 3000));
        foreach (var alias in aliases) Assert.Contains(alias, combined);
    }
}
