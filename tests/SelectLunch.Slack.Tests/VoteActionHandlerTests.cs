using Microsoft.EntityFrameworkCore;
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
/// action_id 라우팅이 이 태스크의 핵심 위험이다 — 잘못 라우팅된 id는
/// 눌러도 아무 반응이 없는 버튼이 되고, 조용히 실패하므로 알아채기 어렵다.
/// </summary>
public class VoteActionHandlerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    static async Task<(TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, VoteActionHandler Handler)>
        SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        var handler = new VoteActionHandler(service, announcer);
        return (fixture, service, slack, handler);
    }

    static BlockActionRequest Request(string actionId, BlockAction? action = null) => new()
    {
        User = new User { Id = "U1" },
        Actions = [action ?? new ButtonAction { ActionId = actionId }],
    };

    [Fact]
    public async Task 버튼_action_id는_투표를_기록하고_메시지를_갱신한다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);

        await handler.Handle(Request(ActionIds.Vote(poll.Id, 식당.Id), new ButtonAction { ActionId = ActionIds.Vote(poll.Id, 식당.Id) }));

        var vote = await fixture.Db.PollVotes.SingleAsync(v => v.PollId == poll.Id && v.SlackUserId == "U1", ct);
        Assert.Equal(식당.Id, vote.RestaurantId);
        Assert.Equal(1, slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 드롭다운_action_id는_선택된_식당으로_투표를_기록한다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "국밥집", 1, null, null, null), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        var action = new StaticSelectAction
        {
            ActionId = ActionIds.VoteSelect(poll.Id),
            SelectedOption = new Option { Value = 식당.Id.ToString() },
        };
        await handler.Handle(Request(action.ActionId, action));

        var vote = await fixture.Db.PollVotes.SingleAsync(v => v.PollId == poll.Id && v.SlackUserId == "U1", ct);
        Assert.Equal(식당.Id, vote.RestaurantId);
    }

    [Fact]
    public async Task 선택값이_없는_드롭다운_action_id는_투표를_기록하지_않는다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        var action = new StaticSelectAction { ActionId = ActionIds.VoteSelect(poll.Id), SelectedOption = null };
        await handler.Handle(Request(action.ActionId, action));

        Assert.False(await fixture.Db.PollVotes.AnyAsync(v => v.PollId == poll.Id, ct));
    }

    [Fact]
    public async Task 존재하지_않는_풀이어도_예외없이_처리된다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U1", ct);
        // PollVotes는 PollId·RestaurantId 모두 FK가 걸려 있어, 정상 흐름에서는
        // 존재하지 않는 pollId로 투표 자체가 불가능하다. 위조되거나 경합으로
        // 사라진 풀에 대한 투표(기록은 됐지만 집계 갱신 조회가 실패)를
        // 재현하려면 이 제약을 일부러 끈다 — LunchAnnouncer.RefreshPollAsync의
        // 방어 코드(풀을 못 찾으면 조용히 건너뜀)가 실제로 예외를 삼키는지만 검증한다.
        await fixture.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", ct);

        var exception = await Record.ExceptionAsync(() =>
            handler.Handle(Request(ActionIds.Vote(999, 식당.Id), new ButtonAction { ActionId = ActionIds.Vote(999, 식당.Id) })));

        Assert.Null(exception);
        Assert.Equal(0, slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 알수없는_action_id는_아무것도_하지_않는다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await handler.Handle(Request("meal_new:20260918"));

        Assert.False(await fixture.Db.PollVotes.AnyAsync(v => v.PollId == poll.Id, ct));
        Assert.Equal(0, slack.ChatFake.UpdateCallCount);
    }
}
