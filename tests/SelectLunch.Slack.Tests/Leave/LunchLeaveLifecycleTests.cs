using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Leave;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class LunchLeaveLifecycleTests
{
    static readonly DateOnly Date = new(2031, 3, 4);
    static readonly DateTimeOffset OpensAt = new(2031, 3, 4, 11, 0, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task 재시작후_휴가조회가_진행중이어도_표를_갱신한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, "C1");
        var restaurant = await service.SaveRestaurantAsync(new RestaurantDraft(null, "예제식당", 1, null, null, null), "U1", ct);
        var poll = await service.OpenPollAsync(Date, OpensAt, ClosesAt, ct);
        var slack = new FakeSlackApiClient();
        await new LunchAnnouncer(slack, fixture.Db, service, "C1", TestPollLeaveRoster.Disabled)
            .PostPollAsync(poll.Id, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", restaurant.Id, ct);
        var source = new PendingRoster();
        var restarted = new LunchAnnouncer(slack, fixture.Db, service, "C1", source);
        var refreshing = restarted.RefreshPollAsync(poll.Id, ct);
        try
        {
            await refreshing.WaitAsync(Deadline, ct);
            Assert.False(source.Completion.Task.IsCompleted);
            Assert.NotNull(slack.ChatFake.UpdatedMessage);
            Assert.Contains(slack.ChatFake.UpdatedMessage.Blocks.OfType<SectionBlock>(),
                b => b.Text is Markdown text && text.Text.Contains("1표", StringComparison.Ordinal));
        }
        finally
        {
            source.Complete();
            await refreshing.WaitAsync(Deadline, ct);
        }
    }

    [Fact]
    public async Task 최초_휴가조회_대기중에_등록한_식당도_게시할_후보에_포함한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, "C1");
        await service.SaveRestaurantAsync(new RestaurantDraft(null, "기존식당", 1, null, null, null), "U1", ct);
        var poll = await service.OpenPollAsync(Date, OpensAt, ClosesAt, ct);
        var source = new PendingRoster();
        var slack = new FakeSlackApiClient();
        var posting = new LunchAnnouncer(slack, fixture.Db, service, "C1", source).PostPollAsync(poll.Id, ClosesAt, ct);
        try
        {
            await source.Requested.Task.WaitAsync(Deadline, ct);
            var added = await service.SaveRestaurantAsync(new RestaurantDraft(null, "추가식당", 1, null, null, null), "U2", ct);
            await service.AddCandidateToOpenPollAsync(added.Id, ct);
            source.Complete();
            await posting.WaitAsync(Deadline, ct);

            Assert.True(await fixture.Db.PollCandidates.AnyAsync(c => c.PollId == poll.Id && c.RestaurantId == added.Id, ct));
            Assert.NotNull(slack.ChatFake.PostedMessage);
            Assert.Contains(slack.ChatFake.PostedMessage.Blocks.OfType<ActionsBlock>()
                .SelectMany(b => b.Elements).OfType<Button>(), button =>
                ActionIds.TryParseVote(button.ActionId, out var actualPoll, out var actualRestaurant)
                && actualPoll == poll.Id && actualRestaurant == added.Id);
        }
        finally
        {
            source.Complete();
            await posting.WaitAsync(Deadline, ct);
        }
    }

    sealed class PendingRoster : IPollLeaveRoster
    {
        public TaskCompletionSource<LeaveRosterSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LeaveRosterSnapshot> GetAsync(long pollId, DateOnly date, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requested.TrySetResult(true);
            return Completion.Task;
        }

        public LeaveRosterSnapshot? GetCached(DateOnly date) =>
            Completion.Task.IsCompletedSuccessfully ? Completion.Task.Result : null;

        public void Complete() => Completion.TrySetResult(LeaveRosterSnapshot.Disabled);
    }
}
