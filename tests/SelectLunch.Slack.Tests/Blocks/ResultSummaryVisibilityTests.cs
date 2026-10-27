using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class ResultSummaryVisibilityTests
{
    [Fact]
    public void 결과_본문에는_선정식당을_유지하고_경쟁항목의_상세점수는_노출하지_않는다()
    {
        var recommendation = new Recommendation(
            new RestaurantPick(1, "선정식당", null),
            new CategoryScore(1, "선정카테고리", null, 30, 0, 0, 30),
            Enumerable.Range(2, 100).Select(id =>
                new CategoryScore(id, $"상세창전용경쟁항목{id}", null, 25, 1, 1, 21)).ToArray());
        var tally = new VoteTally(1, "선정식당", 2, new TimeOnly(11, 20));
        var outcome = new PollOutcome(tally, [tally], recommendation, []);

        var blocks = ResultBlocks.Build(73, outcome);
        var text = string.Join('\n', blocks.OfType<SectionBlock>()
            .Select(block => block.Text).OfType<Markdown>().Select(markdown => markdown.Text));

        Assert.Contains("선정식당", text);
        Assert.Contains("11:20", text);
        Assert.DoesNotContain("상세창전용경쟁항목", text);
        Assert.Contains(blocks.OfType<ActionsBlock>().SelectMany(block => block.Elements).OfType<Button>(),
            button => button.ActionId.StartsWith("result_details:", StringComparison.Ordinal));
    }

    [Fact]
    public void 저장할_추천점수가_없으면_열수없는_상세버튼을_노출하지_않는다()
    {
        var tally = new VoteTally(1, "투표식당", 2);
        var blocks = ResultBlocks.Build(73, new PollOutcome(tally, [tally], null, []));

        Assert.DoesNotContain(blocks.OfType<ActionsBlock>().SelectMany(block => block.Elements).OfType<Button>(),
            button => button.ActionId.StartsWith("result_details:", StringComparison.Ordinal));
    }
}
