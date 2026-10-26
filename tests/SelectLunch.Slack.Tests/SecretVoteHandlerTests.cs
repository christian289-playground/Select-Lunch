using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 비밀 투표의 본인 공개 옵션과 비공개 옵션이 안내에서만 달라지고 집계에서는 동일하게 숨겨지는지 검증한다.
/// </summary>
public class SecretVoteHandlerTests
{
    const string Channel = "C1";
    const long 한식 = 1;
    const long 일식 = 3;
    static readonly string[] CandidateNames = ["국밥집", "백반집", "스시로"];
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    sealed record Harness(
        TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, VoteActionHandler Handler,
        long PollId);

    static async Task<Harness> SetupAsync(bool withCandidates = true)
    {
        var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, Channel, new Random(7));
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);

        if (withCandidates)
        {
            await service.SaveRestaurantAsync(new(null, "국밥집", 한식, null, null, null), "U0", ct);
            await service.SaveRestaurantAsync(new(null, "백반집", 한식, null, null, null), "U0", ct);
            await service.SaveRestaurantAsync(new(null, "스시로", 일식, null, null, null), "U0", ct);
        }

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);

        return new Harness(fixture, service, slack, new VoteActionHandler(service, announcer), poll.Id);
    }

    static BlockActionRequest Request(string userId, string actionId, string visibility = "hidden") => new()
    {
        User = new User { Id = userId },
        Container = new Container { ChannelId = Channel, Type = "message" },
        Actions = [new StaticSelectAction
        {
            ActionId = actionId,
            SelectedOption = new SlackNet.Blocks.Option { Value = visibility },
        }],
    };

    static void AssertNoCandidateNamed(string? text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.All(CandidateNames, name => Assert.DoesNotContain(name, text));
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("hidden")]
    public async Task 비밀_옵션은_집계를_숨기고_선택에_따라_본인에게만_식당을_알린다(string visibility)
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Request("U1", ActionIds.VoteSecret(h.PollId), visibility));

        var vote = await h.Fixture.Db.PollVotes.SingleAsync(v => v.PollId == h.PollId && v.SlackUserId == "U1", ct);
        Assert.True(vote.IsSecret);
        Assert.NotNull(vote.RestaurantId);
        Assert.Equal(1, await h.Service.GetSecretVoteCountAsync(h.PollId, ct));
        Assert.Equal(updatesBefore + 1, h.Slack.ChatFake.UpdateCallCount);

        var receipt = Assert.Single(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", receipt.UserId);
        Assert.Equal(Channel, receipt.Message.Channel);
        var pickedName = (await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == vote.RestaurantId, ct)).Name;
        if (visibility == "reveal")
            Assert.Contains(pickedName, receipt.Message.Text);
        else
            AssertNoCandidateNamed(receipt.Message.Text);
        Assert.Empty(await h.Service.GetTalliesAsync(h.PollId, ct));
        var publicText = string.Join("\n", h.Slack.ChatFake.UpdatedMessage!.Blocks
            .OfType<SectionBlock>().Select(b => ((Markdown)b.Text).Text));
        AssertNoCandidateNamed(publicText);
        var outcome = await h.Service.ClosePollAsync(h.PollId, Today, new(), ct);
        Assert.Equal(vote.RestaurantId, outcome.Winner!.RestaurantId);
        Assert.Equal(1, outcome.Winner.Count);
    }

    [Fact]
    public async Task 마감된_투표의_비밀_버튼은_집계도_화면도_바꾸지_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await h.Fixture.Db.Polls.SingleAsync(p => p.Id == h.PollId, ct);
        poll.Status = PollStatus.Closed;
        await h.Fixture.Db.SaveChangesAsync(ct);
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Request("U1", ActionIds.VoteSecret(h.PollId)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 후보가_없는_투표의_비밀_버튼은_표도_성공_안내도_남기지_않는다()
    {
        var h = await SetupAsync(withCandidates: false);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Request("U1", ActionIds.VoteSecret(h.PollId)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("hidden")]
    public async Task 테스트_모드는_공개_옵션을_반영하지만_표를_기록하지_않는다(string visibility)
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        // 테스트 메시지의 자리표시자 id(0)와, 실제 투표 id를 단 경우 모두 집계를 건드리면 안 된다.
        await h.Handler.Handle(Request("U1", ActionIds.Test(ActionIds.VoteSecret(0)), visibility));
        await h.Handler.Handle(Request("U1", ActionIds.Test(ActionIds.VoteSecret(h.PollId)), visibility));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(0, await h.Service.GetSecretVoteCountAsync(h.PollId, ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Equal(2, h.Slack.ChatFake.EphemeralAttempts.Count);
        Assert.All(h.Slack.ChatFake.EphemeralAttempts, a =>
        {
            Assert.Equal("U1", a.UserId);
            Assert.Equal(Channel, a.Message.Channel);
            if (visibility == "reveal")
                Assert.Single(CandidateNames, name => a.Message.Text.Contains(name));
            else
                AssertNoCandidateNamed(a.Message.Text);
        });
    }
}
