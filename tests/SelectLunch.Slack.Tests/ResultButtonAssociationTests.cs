using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Button = SlackNet.Blocks.Button;

namespace SelectLunch.Slack.Tests;

public class ResultButtonAssociationTests
{
    [Fact]
    public async Task 결과의_상세버튼을_누르면_다른투표가_아닌_그_결과의_저장점수를_보여준다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(2031, 3, 4);
        var recommendation = Recommendation("이결과의점수");
        var selected = new LunchPoll
        {
            ChannelId = "C1", Date = date, Status = PollStatus.Closed,
            RationaleJson = ResultScoreSnapshot.Create(recommendation, new RecommendationOptions()),
        };
        var unrelated = new LunchPoll
        {
            ChannelId = "C1", Date = date.AddDays(-1), Status = PollStatus.Closed,
            RationaleJson = ResultScoreSnapshot.Create(Recommendation("다른결과의점수"), new RecommendationOptions()),
        };
        fixture.Db.Polls.AddRange(unrelated, selected);
        await fixture.Db.SaveChangesAsync(ct);
        fixture.Db.ChangeTracker.Clear();

        var tally = new VoteTally(7, "선정식당", 2);
        var blocks = ResultBlocks.Build(selected.Id, new PollOutcome(tally, [tally], recommendation, []));
        var button = Assert.Single(blocks.OfType<ActionsBlock>()
            .SelectMany(block => block.Elements).OfType<Button>());
        var slack = new FakeSlackApiClient();
        var handler = new ResultDetailsActionHandler(fixture.Db, slack,
            Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = "C1" }));

        await handler.Handle(new BlockActionRequest
        {
            TriggerId = "T-selected",
            User = new User { Id = "U-clicker" },
            Container = new Container { Type = "message", ChannelId = "C1" },
            Actions = [new ButtonAction { ActionId = button.ActionId }],
        });

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        var text = string.Join('\n', view.Blocks.OfType<SectionBlock>()
            .Select(block => block.Text).OfType<Markdown>().Select(markdown => markdown.Text));
        Assert.Contains("이결과의점수", text);
        Assert.DoesNotContain("다른결과의점수", text);
    }

    static Recommendation Recommendation(string categoryName) => new(
        new RestaurantPick(7, "선정식당", null),
        new CategoryScore(1, categoryName, null, 30, 0, 0, 30), []);
}
