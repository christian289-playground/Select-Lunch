using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Button = SlackNet.Blocks.Button;

namespace SelectLunch.Slack.Tests;

public class SecretRevealFlowTests
{
    [Fact]
    public async Task 비밀_투표_접수_뒤_확인은_저장한_식당만_알리고_재추첨하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, "C1", new Random(7));
        await service.SaveRestaurantAsync(new(null, "국밥집", 1, null, null, null), "U0", ct);
        await service.SaveRestaurantAsync(new(null, "스시집", 3, null, null, null), "U0", ct);
        var opens = new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.FromHours(9));
        var poll = await service.OpenPollAsync(new(2026, 10, 26), opens, opens.AddMinutes(30), ct);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, "C1");
        await announcer.PostPollAsync(poll.Id, poll.ClosesAt, ct);
        var handler = new VoteActionHandler(service, announcer);

        await handler.Handle(Request("U1", ActionIds.VoteSecret(poll.Id)));

        var receipt = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", receipt.UserId);
        Assert.DoesNotContain("국밥집", receipt.Message.Text);
        Assert.DoesNotContain("스시집", receipt.Message.Text);
        var reveal = Assert.Single(receipt.Message.Blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements).OfType<Button>());
        var before = await fixture.Db.PollVotes.SingleAsync(ct);
        var restaurantId = before.RestaurantId;
        var votedAt = before.VotedAt;
        var pickedName = (await fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurantId, ct)).Name;
        var updates = slack.ChatFake.UpdateCallCount;

        await handler.Handle(Request("U1", reveal.ActionId));
        await handler.Handle(Request("U1", reveal.ActionId));

        Assert.Equal(3, slack.ChatFake.EphemeralAttempts.Count);
        Assert.All(slack.ChatFake.EphemeralAttempts.Skip(1), a =>
        {
            Assert.Equal("U1", a.UserId);
            Assert.Contains(pickedName, a.Message.Text);
        });
        Assert.Equal(updates, slack.ChatFake.UpdateCallCount);
        var after = Assert.Single(await fixture.Db.PollVotes.ToListAsync(ct));
        Assert.Equal(restaurantId, after.RestaurantId);
        Assert.Equal(votedAt, after.VotedAt);
        Assert.True(after.IsSecret);
        Assert.Equal(1, await service.GetSecretVoteCountAsync(poll.Id, ct));
        Assert.Empty(await service.GetTalliesAsync(poll.Id, ct));
    }

    static BlockActionRequest Request(string userId, string actionId) => new()
    {
        User = new User { Id = userId },
        Container = new Container { ChannelId = "C1", Type = "message" },
        Actions = [new ButtonAction { ActionId = actionId }],
    };
}
