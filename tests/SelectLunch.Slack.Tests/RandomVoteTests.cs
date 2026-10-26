using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 랜덤 투표(카테고리 드롭다운·🎲 버튼)와 테스트 모드. 랜덤이라 값 하나를 못 박지 않고
/// "절대 벗어나면 안 되는 범위"를 반복 시행으로 본다.
/// </summary>
public class RandomVoteTests
{
    const string Channel = "C1";
    const long 한식 = 1;
    const long 일식 = 3;
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    sealed record Harness(
        TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, VoteActionHandler Handler,
        long PollId, long[] 한식Ids, long 일식Id);

    static async Task<Harness> SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);

        var 국밥 = await service.SaveRestaurantAsync(new(null, "국밥집", 한식, null, null, null), "U0", ct);
        var 백반 = await service.SaveRestaurantAsync(new(null, "백반집", 한식, null, null, null), "U0", ct);
        var 스시 = await service.SaveRestaurantAsync(new(null, "스시로", 일식, null, null, null), "U0", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);

        return new Harness(fixture, service, slack, new VoteActionHandler(service, announcer),
            poll.Id, [국밥.Id, 백반.Id], 스시.Id);
    }

    static BlockActionRequest Request(string userId, BlockAction action) => new()
    {
        User = new User { Id = userId },
        Container = new Container { ChannelId = Channel, Type = "message" },
        Actions = [action],
    };

    static StaticSelectAction CategoryPick(string actionId, long categoryId) => new()
    {
        ActionId = actionId, SelectedOption = new Option { Value = categoryId.ToString() },
    };

    [Fact]
    public async Task 비밀_투표는_식당을_저장하되_마감_전_득표에서_숨긴다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.VoteSecret(h.PollId) }));

        var vote = Assert.Single(await h.Fixture.Db.PollVotes.ToListAsync(ct));
        Assert.Contains(vote.RestaurantId!.Value, new[] { h.한식Ids[0], h.한식Ids[1], h.일식Id });
        Assert.Empty(await h.Service.GetTalliesAsync(h.PollId, ct));
        Assert.Empty(await h.Service.GetAbstainersAsync(h.PollId, ct));

        var outcome = await h.Service.ClosePollAsync(h.PollId, Today, new(), ct);
        Assert.Equal(vote.RestaurantId, outcome.Winner!.RestaurantId);
        Assert.Equal(1, Assert.Single(outcome.Tallies).Count);
        var restored = await h.Service.GetClosedOutcomeAsync(h.PollId, ct);
        Assert.Equal(outcome.Winner, restored.Winner);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("random")]
    [InlineData("abstain")]
    public async Task 비밀_표를_다른_선택으로_바꾸면_비밀_집계에서_빠진다(string next)
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await h.Service.CastSecretRandomVoteAsync(h.PollId, "U1", ct);
        await h.Service.CastSecretRandomVoteAsync(h.PollId, "U1", ct);
        Assert.Equal(1, await h.Service.GetSecretVoteCountAsync(h.PollId, ct));

        switch (next)
        {
            case "ordinary":
                await h.Service.CastVoteAsync(h.PollId, "U1", h.일식Id, ct);
                break;
            case "random":
                await h.Service.CastRandomVoteAsync(h.PollId, "U1", 일식, ct);
                break;
            default:
                await h.Service.CastAbstentionAsync(h.PollId, "U1", ct);
                break;
        }

        Assert.Equal(0, await h.Service.GetSecretVoteCountAsync(h.PollId, ct));
        var vote = Assert.Single(await h.Fixture.Db.PollVotes.ToListAsync(ct));
        Assert.False(vote.IsSecret);
        if (next == "abstain")
        {
            Assert.Null(vote.RestaurantId);
            Assert.Empty(await h.Service.GetTalliesAsync(h.PollId, ct));
            Assert.Equal(["U1"], await h.Service.GetAbstainersAsync(h.PollId, ct));
        }
        else
        {
            Assert.Equal(h.일식Id, vote.RestaurantId);
            Assert.Equal(1, Assert.Single(await h.Service.GetTalliesAsync(h.PollId, ct)).Count);
        }
    }

    [Fact]
    public async Task 비밀_표는_재조회와_재마감에도_선택과_득표수를_유지한다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await h.Service.CastVoteAsync(h.PollId, "U1", h.일식Id, ct);
        await h.Service.CastSecretRandomVoteAsync(h.PollId, "U1", ct);
        var picked = (await h.Fixture.Db.PollVotes.SingleAsync(ct)).RestaurantId;
        h.Fixture.Db.ChangeTracker.Clear();

        var service = new LunchService(h.Fixture.Db, Channel, new Random(7));
        Assert.Equal(1, await service.GetSecretVoteCountAsync(h.PollId, ct));
        Assert.Empty(await service.GetTalliesAsync(h.PollId, ct));
        var outcome = await service.ClosePollAsync(h.PollId, Today, new(), ct);
        Assert.Equal(picked, outcome.Winner!.RestaurantId);
        Assert.Null(await service.CastSecretRandomVoteAsync(h.PollId, "U2", ct));
        Assert.Equal(0, await service.GetSecretVoteCountAsync(h.PollId, ct));

        var again = await service.ClosePollAsync(h.PollId, Today.AddDays(1), new(), ct);
        Assert.Equal(outcome.Winner, again.Winner);
        Assert.Equal(1, Assert.Single(again.Tallies).Count);
        Assert.Single(await h.Fixture.Db.PollVotes.ToListAsync(ct));
    }

    [Fact]
    public async Task 카테고리를_고르면_그_카테고리_식당에만_투표된다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        // 사용자마다 한 표라 20명이 각자 눌러 20번 시행한다.
        for (var i = 0; i < 20; i++)
            await h.Handler.Handle(Request($"U{i}", CategoryPick(ActionIds.VoteCategory(h.PollId), 한식)));

        var voted = await h.Fixture.Db.PollVotes.Where(v => v.PollId == h.PollId)
            .Select(v => v.RestaurantId!.Value).ToListAsync(ct);
        Assert.Equal(20, voted.Count);
        Assert.All(voted, id => Assert.Contains(id, h.한식Ids));
        Assert.Equal(20, h.Slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 랜덤_버튼은_후보_중_하나에_투표하고_본인에게_결과를_알린다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.VoteRandom(h.PollId) }));

        var vote = await h.Fixture.Db.PollVotes.SingleAsync(v => v.PollId == h.PollId && v.SlackUserId == "U1", ct);
        Assert.Contains(vote.RestaurantId!.Value, new[] { h.한식Ids[0], h.한식Ids[1], h.일식Id });
        var told = Assert.Single(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", told.UserId);
        Assert.Contains("랜덤", told.Message.Text);
    }

    [Fact]
    public async Task 같은_사람이_다시_누르면_표가_하나로_유지된다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 5; i++)
            await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.VoteRandom(h.PollId) }));

        Assert.Equal(1, await h.Fixture.Db.PollVotes.CountAsync(v => v.PollId == h.PollId, ct));
    }

    [Fact]
    public async Task 마감된_투표의_랜덤_버튼은_집계를_바꾸지_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await h.Fixture.Db.Polls.SingleAsync(p => p.Id == h.PollId, ct);
        poll.Status = PollStatus.Closed;
        await h.Fixture.Db.SaveChangesAsync(ct);

        await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.VoteRandom(h.PollId) }));
        await h.Handler.Handle(Request("U1", CategoryPick(ActionIds.VoteCategory(h.PollId), 한식)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 후보가_없는_카테고리는_투표하지_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        await h.Handler.Handle(Request("U1", CategoryPick(ActionIds.VoteCategory(h.PollId), 999)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
    }

    [Fact]
    public async Task 테스트_모드_버튼은_아무것도_기록하지_않고_본인에게만_결과를_보여준다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.Test(ActionIds.VoteRandom(0)) }));
        await h.Handler.Handle(Request("U1", CategoryPick(ActionIds.Test(ActionIds.VoteCategory(0)), 한식)));
        await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.Test(ActionIds.Vote(0, h.일식Id)) }));
        await h.Handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.Test(ActionIds.Abstain(0)) }));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(0, h.Slack.ChatFake.UpdateCallCount);
        Assert.Equal(4, h.Slack.ChatFake.EphemeralAttempts.Count);
        Assert.All(h.Slack.ChatFake.EphemeralAttempts, a =>
        {
            Assert.Equal("U1", a.UserId);
            Assert.Equal(Channel, a.Message.Channel);
            Assert.Contains("테스트", a.Message.Text);
        });
        Assert.Contains("스시로", h.Slack.ChatFake.EphemeralAttempts[2].Message.Text);
    }

    [Fact]
    public async Task 테스트_모드_카테고리_선택은_그_카테고리_식당만_보여준다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;

        for (var i = 0; i < 20; i++)
            await h.Handler.Handle(Request("U1", CategoryPick(ActionIds.Test(ActionIds.VoteCategory(0)), 일식)));

        Assert.All(h.Slack.ChatFake.EphemeralAttempts, a => Assert.Contains("스시로", a.Message.Text));
    }
}
