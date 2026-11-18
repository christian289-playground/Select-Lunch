using System.Text;
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

    const string WarningPrefix = "휴가 정보:";
    const string NamesPrefix = "오늘 휴가: ";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void 부분_확인은_확인된_별칭을_일반텍스트로_보이고_불완전_경고를_한_번만_덧붙인다(
        bool closed, bool testMode, bool emptyCandidates)
    {
        var snapshot = new LeaveRosterSnapshot(LeaveRosterStatus.Partial, ["개발팀_홍길동", "철수"]);
        var blocks = PollBlocks.Build(1, emptyCandidates ? [] : Candidates, [], [], ClosesAt,
            closed: closed, testMode: testMode, leaveRoster: snapshot);
        var sections = PlainSections(blocks);

        // 헤더 바로 뒤에 [명단, 경고] 순서로 정확히 두 덩어리만 온다.
        Assert.IsType<SectionBlock>(blocks[1]);
        Assert.Equal(2, sections.Length);
        Assert.Equal($"{NamesPrefix}개발팀_홍길동, 철수", sections[0].Text);
        Assert.StartsWith(WarningPrefix, sections[1].Text, StringComparison.Ordinal);
        Assert.Contains("일부", sections[1].Text);
        Assert.DoesNotContain("없", sections[1].Text);

        var text = string.Join("\n", sections.Select(s => s.Text));
        Assert.DoesNotContain("<@", text);
        Assert.DoesNotContain("<!", text);
    }

    [Fact]
    public void 확인이_끝난_결과에는_불완전_경고를_붙이지_않는다()
    {
        var blocks = PollBlocks.Build(1, Candidates, [], [], ClosesAt,
            leaveRoster: new(LeaveRosterStatus.Available, ["철수"]));
        var section = Assert.Single(PlainSections(blocks));
        Assert.Equal($"{NamesPrefix}철수", section.Text);
    }

    [Fact]
    public void 부분_확인의_긴_명단은_모든_이름을_나눠_보이고_경고는_한_번만_마지막에_둔다()
    {
        var aliases = Enumerable.Range(0, 30).Select(i => $"이름{i:D2}" + new string('가', 190)).ToArray();
        var sections = PlainSections(PollBlocks.Build(1, Candidates, [], [], ClosesAt,
            leaveRoster: new(LeaveRosterStatus.Partial, aliases)));

        Assert.All(sections, s => Assert.InRange(s.Text.Length, 1, 3000));
        Assert.True(sections.Length > 2, "명단이 여러 덩어리로 나뉘어야 한다");
        Assert.Equal(1, sections.Count(s => s.Text.StartsWith(WarningPrefix, StringComparison.Ordinal)));
        Assert.StartsWith(WarningPrefix, sections[^1].Text, StringComparison.Ordinal);
        var combined = string.Join("\n", sections.Take(sections.Length - 1).Select(s => s.Text));
        foreach (var alias in aliases) Assert.Contains(alias, combined);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 부분_확인이어도_보일_이름이_없으면_휴가자_없음이_아니라_확인불가로_보인다(int blankNames)
    {
        var names = Enumerable.Repeat(" \t", blankNames).Append("").ToArray();
        var blocks = PollBlocks.Build(1, Candidates, [], [], ClosesAt,
            leaveRoster: new(LeaveRosterStatus.Partial, names));
        var section = Assert.Single(PlainSections(blocks));
        Assert.Equal("휴가 정보: 확인하지 못했습니다", section.Text);
    }

    [Theory]
    [InlineData(LeaveRosterStatus.Available)]
    [InlineData(LeaveRosterStatus.Partial)]
    public void 한_이름이_슬랙_텍스트_상한을_혼자_넘어도_잘라서_상한_안에_둔다(LeaveRosterStatus status)
    {
        // 자르는 위치(2992)가 이모지 서로게이트 쌍의 한가운데에 걸리도록 채운다.
        var huge = new string('가', 2991) + "😀" + new string('나', 50);
        var sections = PlainSections(PollBlocks.Build(1, Candidates, [], [], ClosesAt,
            leaveRoster: new(status, ["앞사람", huge, "뒷사람"])));

        Assert.All(sections, s => Assert.InRange(s.Text.Length, 1, 3000));
        // 짝 없는 서로게이트가 남으면 UTF-8로 인코딩할 수 없어 슬랙이 거절한다.
        Assert.All(sections, s => Assert.Null(Record.Exception(() => StrictUtf8.GetBytes(s.Text))));
        var combined = string.Join("\n", sections.Select(s => s.Text));
        Assert.Contains("앞사람", combined);
        Assert.Contains("뒷사람", combined);
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
