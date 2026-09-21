using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// "나 오늘 따로 먹어요" 버튼. VoteActionHandlerTests와 마찬가지로 action_id
/// 라우팅이 핵심 위험이다 — 잘못 라우팅되면 조용히 아무 일도 하지 않는다.
/// </summary>
public class AbstainActionHandlerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    static async Task<(TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, AbstainActionHandler Handler)>
        SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        var handler = new AbstainActionHandler(service, announcer);
        return (fixture, service, slack, handler);
    }

    static BlockActionRequest Request(string actionId) => new()
    {
        User = new User { Id = "U1" },
        Actions = [new ButtonAction { ActionId = actionId }],
    };

    [Fact]
    public async Task 기권_버튼은_RestaurantId가_null인_행을_남기고_메시지를_갱신한다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);

        await handler.Handle(Request(ActionIds.Abstain(poll.Id)));

        var vote = await fixture.Db.PollVotes.SingleAsync(v => v.PollId == poll.Id && v.SlackUserId == "U1", ct);
        Assert.Null(vote.RestaurantId);
        Assert.Equal(1, slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 투표한_사람이_기권_버튼을_누르면_기권으로_바뀐다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 식당.Id, ct);

        await handler.Handle(Request(ActionIds.Abstain(poll.Id)));

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).ToListAsync(ct);
        Assert.Null(Assert.Single(votes).RestaurantId);
    }

    [Fact]
    public async Task 알수없는_action_id는_아무것도_하지_않는다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await handler.Handle(Request(ActionIds.Vote(poll.Id, 999)));

        Assert.False(await fixture.Db.PollVotes.AnyAsync(v => v.PollId == poll.Id, ct));
        Assert.Equal(0, slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 존재하지_않는_풀이어도_예외없이_처리된다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        // PollVotes.PollId는 FK가 걸려 있어, 정상 흐름에서는 존재하지 않는
        // pollId로 기권 자체가 불가능하다. 위조되거나 경합으로 사라진 풀에 대한
        // 요청을 재현하려면 제약을 일부러 끈다.
        await fixture.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", ct);

        var exception = await Record.ExceptionAsync(() => handler.Handle(Request(ActionIds.Abstain(999))));

        Assert.Null(exception);
        Assert.Equal(0, slack.ChatFake.UpdateCallCount);
    }
}
