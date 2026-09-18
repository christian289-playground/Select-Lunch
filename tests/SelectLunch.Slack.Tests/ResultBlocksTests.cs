using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class ResultBlocksTests
{
    static readonly RecommendationOptions Options = new();

    static Recommendation Sample() => new(
        new RestaurantPick(20, "스시로", new DateOnly(2026, 8, 21)),
        new CategoryScore(3, "일식", new DateOnly(2026, 9, 4), 14, 0, 1, 13),
        [
            new CategoryScore(5, "분식", new DateOnly(2026, 9, 7), 11, 0, 0, 11),
            new CategoryScore(1, "한식", new DateOnly(2026, 9, 15), 3, 2, 5, -8),
        ]);

    static string TextOf(IList<Block> blocks) =>
        string.Join("\n", blocks.OfType<SectionBlock>().Select(s => (s.Text as Markdown)?.Text ?? "")
            .Concat(blocks.OfType<ContextBlock>().SelectMany(c => c.Elements.OfType<Markdown>().Select(m => m.Text))));

    [Fact]
    public void 추천_근거에_점수_계산_과정이_모두_드러난다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("13점", text);          // 최종 점수
        Assert.Contains("14일", text);          // 경과일 D
        Assert.Contains("최근 7일", text);       // N7
        Assert.Contains("최근 30일", text);      // N30
        Assert.Contains("14 −", text);          // 실제 뺄셈 식
    }

    [Fact]
    public void 경쟁_카테고리의_점수도_함께_보여준다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("분식", text);
        Assert.Contains("11점", text);
        Assert.Contains("한식", text);
    }

    [Fact]
    public void 투표_결과와_추천을_두_갈래로_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(10, "김밥천국", 4),
            [new VoteTally(10, "김밥천국", 4), new VoteTally(20, "스시로", 1)],
            Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("김밥천국", text);
        Assert.Contains("스시로", text);
        Assert.Contains("투표 1위", text);
        Assert.Contains("앱 추천", text);
    }

    [Fact]
    public void 투표_1위와_추천이_같으면_하나로_합쳐_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(20, "스시로", 3),
            [new VoteTally(20, "스시로", 3)],
            Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("투표와 추천이 일치", text);
        Assert.DoesNotContain("투표 1위", text);
    }

    [Fact]
    public void 아무도_투표하지_않으면_추천만_보여준다()
    {
        var outcome = new PollOutcome(null, [], Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("투표가 없었습니다", text);
        Assert.Contains("스시로", text);
    }

    [Fact]
    public void 추천할_식당이_없어도_투표_결과는_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(10, "김밥천국", 2),
            [new VoteTally(10, "김밥천국", 2)],
            Recommendation: null);

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("김밥천국", text);
    }
}
