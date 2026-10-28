using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

public class EarlyDeparturePosterTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 10, 28);
    static readonly DateTimeOffset Now = new(2026, 10, 28, 11, 5, 0, TimeSpan.FromHours(9));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Harness(TestDb fixture, LunchService service, FakeSlackApiClient slack, LunchPoll poll,
        Restaurant a, Restaurant b, EarlyDepartureNoticeTracker tracker, TestTimeProvider clock) : IAsyncDisposable
    {
        public TestDb Fixture => fixture;
        public LunchService Service => service;
        public FakeSlackApiClient Slack => slack;
        public LunchPoll Poll => poll;
        public Restaurant A => a;
        public Restaurant B => b;
        public EarlyDepartureNoticeTracker Tracker => tracker;
        public TestTimeProvider Clock => clock;
        public EarlyDeparturePoster Poster(string channel = Channel) => CreatePoster(slack, fixture, channel, clock, tracker);
        public Task Vote(string user, long restaurant) => service.CastVoteAsync(poll.Id, user, restaurant, Ct);
        public ValueTask DisposeAsync() => fixture.DisposeAsync();
    }

    internal static EarlyDeparturePoster CreatePoster(FakeSlackApiClient slack, TestDb fixture, string channel,
        TimeProvider? clock = null, EarlyDepartureNoticeTracker? tracker = null)
    {
        var options = new LunchOptions();
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => options)));
        return new EarlyDeparturePoster(slack, fixture.Db, channel, monitor, clock ?? TimeProvider.System,
            tracker ?? new EarlyDepartureNoticeTracker(), NullLogger<EarlyDeparturePoster>.Instance);
    }

    static async Task<Harness> SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var a = await service.SaveRestaurantAsync(new(null, "<@everyone> 국밥", 1, null, null, null), "U0", Ct);
        var b = await service.SaveRestaurantAsync(new(null, "스시", 3, null, null, null), "U0", Ct);
        a.EarlyDepartureSeconds = 630;
        a.DepartureTime = new TimeOnly(11, 20);
        b.EarlyDepartureSeconds = 300;
        await fixture.Db.SaveChangesAsync(Ct);
        var poll = await service.OpenPollAsync(Today, Now.AddMinutes(-5), Now.AddMinutes(25), Ct);
        poll.MessageTs = "123.456";
        await fixture.Db.SaveChangesAsync(Ct);
        return new(fixture, service, new FakeSlackApiClient(), poll, a, b,
            new EarlyDepartureNoticeTracker(), new TestTimeProvider(Now));
    }

    static string[] Targets(Harness h) => h.Slack.ChatFake.EphemeralAttempts.Select(a => a.UserId).ToArray();

    [Fact]
    public async Task PostAsync_ThresholdAndNewVoter_OnlySendsNewRecipients()
    {
        await using var h = await SetupAsync();
        var poster = h.Poster();
        await h.Vote("U1", h.A.Id);
        Assert.Equal(0, await poster.PostAsync(h.Poll.Id, Ct));
        await h.Vote("U2", h.A.Id);
        Assert.Equal(2, await poster.PostAsync(h.Poll.Id, Ct));
        await h.Vote("U3", h.A.Id);
        Assert.Equal(1, await poster.PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1", "U2", "U3" }, Targets(h));
        var msg = h.Slack.ChatFake.EphemeralAttempts[0].Message;
        Assert.Equal(Channel, msg.Channel);
        Assert.True(string.IsNullOrEmpty(msg.ThreadTs));
        var text = Assert.IsType<PlainText>(Assert.Single(msg.Blocks.OfType<SectionBlock>()).Text).Text;
        Assert.Contains("<@everyone> 국밥", text);
        Assert.Contains("10.5분", text);
        Assert.Contains("2/6", text);
        Assert.Contains("11:20", text);
        Assert.DoesNotContain("<@everyone>", msg.Text);
    }

    [Fact]
    public async Task PostAsync_ChangedChoicesAndMultipleRestaurants_ClaimsPerUserAndRestaurant()
    {
        await using var h = await SetupAsync();
        var poster = h.Poster();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        await poster.PostAsync(h.Poll.Id, Ct);
        await h.Vote("U3", h.B.Id); await h.Vote("U4", h.B.Id);
        await h.Vote("U1", h.B.Id);
        await poster.PostAsync(h.Poll.Id, Ct);
        await h.Vote("U1", h.A.Id);
        await poster.PostAsync(h.Poll.Id, Ct);
        Assert.Equal(new[] { "U1", "U2", "U1", "U3", "U4" }, Targets(h));
    }

    [Fact]
    public async Task PostAsync_SecretAbstentionAndZeroLead_AreExcluded()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id);
        await h.Vote("U2", h.A.Id);
        var secret = await h.Fixture.Db.PollVotes.SingleAsync(v => v.SlackUserId == "U2", Ct);
        secret.IsSecret = true;
        await h.Service.CastAbstentionAsync(h.Poll.Id, "U3", Ct);
        await h.Fixture.Db.SaveChangesAsync(Ct);
        Assert.Equal(0, await h.Poster().PostAsync(h.Poll.Id, Ct));
        await h.Vote("U4", h.A.Id);
        Assert.Equal(2, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1", "U4" }, Targets(h));
        h.B.EarlyDepartureSeconds = 0;
        await h.Fixture.Db.SaveChangesAsync(Ct);
        await h.Vote("U5", h.B.Id); await h.Vote("U6", h.B.Id);
        Assert.Equal(0, await h.Poster().PostAsync(h.Poll.Id, Ct));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("expired")]
    [InlineData("old")]
    [InlineData("channel")]
    [InlineData("unposted")]
    [InlineData("inactive")]
    [InlineData("not-candidate")]
    public async Task PostAsync_IneligiblePollOrRestaurant_DoesNotSend(string reason)
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        switch (reason)
        {
            case "closed": h.Poll.Status = PollStatus.Closed; break;
            case "expired": h.Poll.ClosesAt = Now; break;
            case "old": h.Poll.Date = Today.AddDays(-1); break;
            case "channel": h.Poll.ChannelId = "OTHER"; break;
            case "unposted": h.Poll.MessageTs = null; break;
            case "inactive": h.A.Status = RestaurantStatus.Archived; break;
            case "not-candidate":
                var candidate = await h.Fixture.Db.PollCandidates.SingleAsync(
                    c => c.PollId == h.Poll.Id && c.RestaurantId == h.A.Id, Ct);
                h.Fixture.Db.PollCandidates.Remove(candidate);
                break;
        }
        await h.Fixture.Db.SaveChangesAsync(Ct);
        Assert.Equal(0, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Empty(Targets(h));
    }

    [Fact]
    public async Task PostAsync_SendFailure_IsNeverRetried()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        h.Slack.ChatFake.OnPostEphemeral = (_, _, _) => throw new InvalidOperationException("sensitive");
        Assert.Equal(0, await h.Poster().PostAsync(h.Poll.Id, Ct));
        h.Slack.ChatFake.OnPostEphemeral = null;
        Assert.Equal(0, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1", "U2" }, Targets(h));
    }

    [Fact]
    public async Task PostAsync_VoteChangesDuringSend_RechecksChoiceAndThreshold()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        h.Slack.ChatFake.OnPostEphemeral = async (user, _, _) =>
        {
            if (user == "U1") await h.Vote("U2", h.B.Id);
        };
        Assert.Equal(1, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1" }, Targets(h));
    }

    [Fact]
    public async Task Tracker_CompletionOldDateAndConcurrentClaims_AreSuppressed()
    {
        var tracker = new EarlyDepartureNoticeTracker();
        var claims = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => tracker.TryClaim(Today, 1, "U1"))));
        Assert.Single(claims, x => x);
        tracker.CompleteDay(Today);
        Assert.False(tracker.TryClaim(Today, 1, "U2"));
        Assert.False(tracker.TryClaim(Today.AddDays(-1), 1, "U1"));
        Assert.True(tracker.TryClaim(Today.AddDays(1), 1, "U1"));
        tracker.CompleteDay(Today);
        Assert.False(tracker.TryClaim(Today.AddDays(1), 1, "U1"));
        Assert.True(tracker.TryClaim(Today.AddDays(1), 1, "U2"));
    }

    [Fact]
    public void Tracker_HasAttempted_DoesNotRotateAndSuppressesPastOrCompletedDays()
    {
        var tracker = new EarlyDepartureNoticeTracker();
        Assert.False(tracker.HasAttempted(Today, 1, "U1"));
        Assert.True(tracker.TryClaim(Today, 1, "U1"));
        Assert.True(tracker.HasAttempted(Today, 1, "U1"));
        Assert.False(tracker.HasAttempted(Today, 1, "U2"));
        Assert.True(tracker.HasAttempted(Today.AddDays(-1), 1, "U2"));
        Assert.False(tracker.HasAttempted(Today.AddDays(1), 1, "U1"));
        Assert.True(tracker.HasAttempted(Today, 1, "U1"));
        tracker.CompleteDay(Today);
        Assert.True(tracker.HasAttempted(Today, 1, "U2"));
        Assert.False(tracker.HasAttempted(Today.AddDays(1), 1, "U1"));
    }

    [Fact]
    public void Tracker_StartDay_OnlyRotatesForwardAndPreservesCompletion()
    {
        var tracker = new EarlyDepartureNoticeTracker();
        tracker.StartDay(Today);
        Assert.True(tracker.TryClaim(Today, 1, "U1"));
        tracker.StartDay(Today.AddDays(-1));
        Assert.False(tracker.TryClaim(Today, 1, "U1"));
        tracker.CompleteDay(Today);
        tracker.StartDay(Today);
        Assert.False(tracker.TryClaim(Today, 1, "U2"));
        tracker.StartDay(Today.AddDays(1));
        Assert.True(tracker.TryClaim(Today.AddDays(1), 1, "U1"));
    }

    [Fact]
    public async Task PostAsync_AlreadyRecordedMeal_SuppressesEvenWithFreshTracker()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        await h.Service.RecordMealAsync(Today, h.A.Id, "U1", MealSource.Prompt, Ct);
        Assert.Equal(0, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Empty(Targets(h));
        Assert.False(h.Tracker.TryClaim(Today, h.A.Id, "U1"));
    }

    [Fact]
    public async Task PostAsync_RecordedDuringSend_SuppressesRemainingRecipients()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        h.Slack.ChatFake.OnPostEphemeral = async (user, _, _) =>
        {
            if (user == "U1") await h.Service.RecordMealAsync(Today, h.A.Id, user, MealSource.Prompt, Ct);
        };
        Assert.Equal(1, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1" }, Targets(h));
        Assert.False(h.Tracker.TryClaim(Today, h.A.Id, "U2"));
    }

    [Fact]
    public async Task PostAsync_DeadlinePassesDuringSend_StopsBeforeNextClaim()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        h.Slack.ChatFake.OnPostEphemeral = (user, _, _) =>
        {
            if (user == "U1") h.Clock.UtcNow = h.Poll.ClosesAt.ToUniversalTime();
            return Task.CompletedTask;
        };
        Assert.Equal(1, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1" }, Targets(h));
        Assert.True(h.Tracker.TryClaim(Today, h.A.Id, "U2"));
    }

    [Fact]
    public async Task Handler_FailedNotice_KeepsPublicVote()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id);
        h.Slack.ChatFake.OnPostEphemeral = (_, _, _) => throw new InvalidOperationException("sensitive");
        var announcer = new LunchAnnouncer(h.Slack, h.Fixture.Db, h.Service, Channel, TestPollLeaveRoster.Disabled);
        var handler = new VoteActionHandler(h.Service, announcer, h.Poster());
        await handler.Handle(new BlockActionRequest
        {
            User = new User { Id = "U2" },
            Container = new Container { ChannelId = Channel, Type = "message" },
            Actions = [new ButtonAction { ActionId = ActionIds.Vote(h.Poll.Id, h.A.Id) }],
        });
        Assert.Equal(h.A.Id, (await h.Fixture.Db.PollVotes.SingleAsync(v => v.SlackUserId == "U2", Ct)).RestaurantId);
        Assert.Equal(new[] { "U1", "U2" }, Targets(h));
    }

    [Fact]
    public async Task PostAsync_PollClosedDuringSend_DoesNotNotifyRemainingVoters()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        h.Slack.ChatFake.OnPostEphemeral = async (user, _, _) =>
        {
            if (user == "U1")
            {
                h.Poll.Status = PollStatus.Closed;
                await h.Fixture.Db.SaveChangesAsync(Ct);
            }
        };
        Assert.Equal(1, await h.Poster().PostAsync(h.Poll.Id, Ct));
        Assert.Equal(new[] { "U1" }, Targets(h));
    }

    [Fact]
    public async Task PostAsync_Cancelled_PropagatesAndDoesNotClaim()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Poster().PostAsync(h.Poll.Id, source.Token));
        Assert.Empty(Targets(h));
        Assert.True(h.Tracker.TryClaim(Today, h.A.Id, "U1"));
    }

    [Fact]
    public async Task PostAsync_InvalidParticipantCount_DoesNotSend()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        var config = new LunchOptions { EarlyDepartureParticipantCount = 0 };
        var options = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => config)));
        var poster = new EarlyDeparturePoster(h.Slack, h.Fixture.Db, Channel, options, h.Clock,
            h.Tracker, NullLogger<EarlyDeparturePoster>.Instance);
        Assert.Equal(0, await poster.PostAsync(h.Poll.Id, Ct));
        Assert.Empty(Targets(h));
    }

    [Fact]
    public async Task Handler_SecretVote_DoesNotNotifyOrClaim()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        var announcer = new LunchAnnouncer(h.Slack, h.Fixture.Db, h.Service, Channel, TestPollLeaveRoster.Disabled);
        var handler = new VoteActionHandler(h.Service, announcer, h.Poster());
        await handler.Handle(new BlockActionRequest
        {
            User = new User { Id = "U3" },
            Container = new Container { ChannelId = Channel, Type = "message" },
            Actions = [new ButtonAction { ActionId = ActionIds.VoteSecret(h.Poll.Id) }],
        });
        Assert.Single(h.Slack.ChatFake.EphemeralAttempts);
        Assert.True(h.Tracker.TryClaim(Today, h.A.Id, "U1"));
    }

    [Fact]
    public async Task Handler_PublicButtonSelectAndCategoryRandom_SendAfterRefresh()
    {
        await using var h = await SetupAsync();
        var announcer = new LunchAnnouncer(h.Slack, h.Fixture.Db, h.Service, Channel, TestPollLeaveRoster.Disabled);
        var handler = new VoteActionHandler(h.Service, announcer, h.Poster());
        BlockActionRequest Request(string user, BlockAction action) => new()
        {
            User = new User { Id = user }, Container = new Container { ChannelId = Channel, Type = "message" },
            Actions = [action],
        };
        await handler.Handle(Request("U1", new ButtonAction { ActionId = ActionIds.Vote(h.Poll.Id, h.A.Id) }));
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        await handler.Handle(Request("U2", new StaticSelectAction
        {
            ActionId = ActionIds.VoteSelect(h.Poll.Id),
            SelectedOption = new SlackNet.Blocks.Option { Value = h.A.Id.ToString() },
        }));
        await handler.Handle(Request("U3", new StaticSelectAction
        {
            ActionId = ActionIds.VoteCategory(h.Poll.Id),
            SelectedOption = new SlackNet.Blocks.Option { Value = "1" },
        }));
        var notices = h.Slack.ChatFake.EphemeralAttempts
            .Where(a => a.Message.Text == "미리 출발 준비 안내 (현재 투표 기준)")
            .Select(a => a.UserId).ToArray();
        Assert.Equal(new[] { "U1", "U2", "U3" }, notices);
        Assert.Equal(3, h.Slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task Handler_TestModeAndOtherChannel_DoNotTriggerNotices()
    {
        await using var h = await SetupAsync();
        await h.Vote("U1", h.A.Id); await h.Vote("U2", h.A.Id);
        var announcer = new LunchAnnouncer(h.Slack, h.Fixture.Db, h.Service, Channel, TestPollLeaveRoster.Disabled);
        var handler = new VoteActionHandler(h.Service, announcer, h.Poster());
        BlockActionRequest Request(string action, string channel) => new()
        {
            User = new User { Id = "U3" }, Container = new Container { ChannelId = channel, Type = "message" },
            Actions = [new ButtonAction { ActionId = action }],
        };
        await handler.Handle(Request(ActionIds.Test(ActionIds.Vote(h.Poll.Id, h.A.Id)), Channel));
        h.Slack.ChatFake.EphemeralAttempts.Clear();
        await handler.Handle(Request(ActionIds.Vote(h.Poll.Id, h.A.Id), "OTHER"));
        Assert.Empty(Targets(h));
        Assert.True(h.Tracker.TryClaim(Today, h.A.Id, "U1"));
    }
}
