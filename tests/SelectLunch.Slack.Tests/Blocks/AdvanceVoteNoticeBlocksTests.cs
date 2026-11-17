using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class AdvanceVoteNoticeBlocksTests
{
    static readonly DateOnly Date = new(2026, 11, 16);
    static readonly DateTimeOffset ChangedAt = new(2026, 11, 16, 9, 35, 0, TimeSpan.FromHours(9));

    [Theory]
    [InlineData(AdvanceVoteChangeKind.Selected, "국밥집", "국밥집")]
    [InlineData(AdvanceVoteChangeKind.Abstained, null, "기권")]
    [InlineData(AdvanceVoteChangeKind.Cancelled, null, "취소")]
    public void Build_변경별_공개_스냅샷을_익명_텍스트로_표시한다(AdvanceVoteChangeKind kind, string? name, string expected)
    {
        var change = new AdvanceVoteChange(kind, name, ChangedAt,
            new AdvanceVoteSnapshot(Date, [new(1, "<@U999> <!channel> *식당*", 2), new(2, "국밥집", 1)], 3));

        var blocks = AdvanceVoteNoticeBlocks.Build(change);
        var sections = blocks.Select(b => Assert.IsType<SectionBlock>(b)).ToArray();
        var texts = sections.Select(s => Assert.IsType<PlainText>(s.Text).Text).ToArray();
        var text = string.Join("\n", texts);
        Assert.Contains("2026", text);
        Assert.Contains("11", text);
        Assert.Contains("16", text);
        Assert.Contains(expected, text);
        Assert.Contains("<@U999> <!channel> *식당*", text);
        Assert.Contains("2표", text);
        Assert.Contains("3", text);
        Assert.Contains("기권", text);
        Assert.Contains("직후", text);
        Assert.Contains("09:35", text);
        Assert.All(sections, s => Assert.InRange(s.Text.Text.Length, 1, 3000));
        Assert.InRange(blocks.Count, 1, 50);
    }

    [Fact]
    public void Build_많은_식당을_섹션으로_나누되_누락하거나_스냅샷을_바꾸지_않는다()
    {
        var tallies = Enumerable.Range(1, 1200)
            .Select(i => new AdvanceVoteTally(i, $"식당{i:D4}_" + new string('가', 70), i)).ToList();
        var before = tallies.ToArray();
        var change = new AdvanceVoteChange(AdvanceVoteChangeKind.Cancelled, null, ChangedAt,
            new AdvanceVoteSnapshot(Date, tallies, 4));

        var blocks = AdvanceVoteNoticeBlocks.Build(change);
        var sections = blocks.Select(b => Assert.IsType<SectionBlock>(b)).ToArray();
        var text = string.Join("", sections.Select(s => Assert.IsType<PlainText>(s.Text).Text));
        Assert.InRange(blocks.Count, 2, 50);
        Assert.All(sections, s => Assert.InRange(s.Text.Text.Length, 1, 3000));
        Assert.All(before, tally => Assert.Contains($"{tally.Name} — {tally.Count}표", text));
        Assert.Equal(before, tallies);
    }
}
