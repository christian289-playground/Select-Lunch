using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Button = SlackNet.Blocks.Button;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Tests;

public sealed class AdvanceVoteFlowTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 11, 16);
    static readonly DateTimeOffset BeforeOpen = new(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(9));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 명령어의_선택은_익명으로_공개하지만_본투표로_이관하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var clock = new Clock(BeforeOpen);
        var slack = new FakeSlackApiClient();
        var monitor = Monitor();
        var settings = Settings();
        var service = new LunchService(fixture.Db, Channel);
        var first = await service.SaveRestaurantAsync(new(null, "첫식당", 1, null, null, null), "U1", Ct);
        var second = await service.SaveRestaurantAsync(new(null, "다른식당", 3, null, null, null), "U1", Ct);
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, settings, AdvanceVoteTestSupport.Poster(slack));
        var commands = new LunchSlashCommandHandler(fixture.Db, slack, monitor, TestPollLeaveRoster.Disabled, settings, actions);

        var screen = await commands.Handle(new SlashCommand { Command = "/lunchpre", ChannelId = Channel, UserId = "U1" });
        Assert.Equal(ResponseType.Ephemeral, screen.ResponseType);
        var picker = Assert.Single(screen.Message.Blocks.OfType<ActionsBlock>()
            .SelectMany(b => b.Elements).OfType<ExternalSelectMenu>());
        var suggestions = await new RestaurantOptionProvider(fixture.Db, advance, settings).GetOptions(new BlockOptionsRequest
        {
            ActionId = picker.ActionId, Value = "첫식", User = new User { Id = "U1" },
            Container = new Container { ChannelId = Channel },
        });
        Assert.Equal(first.Id.ToString(), Assert.Single(Assert.Single(suggestions.OptionGroups).Options).Value);
        await actions.Handle(Select(picker.ActionId, first.Id));
        Assert.Empty(await fixture.Db.Polls.ToListAsync(Ct));
        var receipt = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", receipt.UserId);
        Assert.Equal(Channel, receipt.Message.Channel);
        var published = Assert.Single(slack.ChatFake.PostAttempts);
        Assert.Equal(Channel, published.Channel);
        Assert.Null(published.ThreadTs);
        Assert.Null(published.Username);
        Assert.Null(published.MetadataJson);
        Assert.Null(published.MetadataObject);
        Assert.False(published.LinkNames);
        Assert.Empty(published.Blocks.OfType<ActionsBlock>());
        var publicText = string.Join("\n", published.Blocks.OfType<SectionBlock>()
            .Select(b => Assert.IsType<PlainText>(b.Text).Text));
        Assert.Contains(first.Name, publicText);
        Assert.DoesNotContain("U1", publicText);
        Assert.DoesNotContain("<@", publicText);
        Assert.Null(slack.ChatFake.UpdatedMessage);
        Assert.Equal(first.Id, (await advance.FindAsync(Today, "U1", Ct))!.RestaurantId);

        var opens = BeforeOpen.AddHours(1);
        var poll = await service.OpenPollAsync(Today, opens, opens.AddMinutes(30), Ct);
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal(1, (await advance.FindAsync(Today, "U1", Ct))!.ChangeCount);
        await service.CastVoteAsync(poll.Id, "U1", second.Id, Ct);
        Assert.Equal(second.Id, Assert.Single(await fixture.Db.PollVotes.ToListAsync(Ct)).RestaurantId);
        Assert.Equal(first.Id, (await advance.FindAsync(Today, "U1", Ct))!.RestaurantId);
        Assert.Single(slack.ChatFake.PostAttempts);
    }

    [Theory]
    [InlineData("U2", "C1", 0)]
    [InlineData("U1", "C2", 0)]
    [InlineData("U1", "C1", 1)]
    public async Task 다른_사용자_채널_지난날의_선택은_저장하지_않는다(string user, string channel, int dayOffset)
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new Clock(BeforeOpen);
        var slack = new FakeSlackApiClient();
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        var restaurant = await new LunchService(fixture.Db, Channel)
            .SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "U1", Ct);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));
        var request = Select(ActionIds.AdvanceSelect(Today.AddDays(-dayOffset), "U1", 0), restaurant.Id);
        request.User.Id = user;
        request.Container.ChannelId = channel;

        await actions.Handle(request);

        Assert.Empty(await fixture.Db.AdvanceVotes.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Null(slack.ChatFake.PostedMessage);
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(16, true)]
    [InlineData(16, false)]
    public async Task 본투표_진행중에는_조회만_허용하고_확정후_다음회차를_연다(int hour, bool closed)
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new Clock(BeforeOpen.AddHours(hour - 10));
        var slack = new FakeSlackApiClient();
        var service = new LunchService(fixture.Db, Channel);
        var restaurant = await service.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "U1", Ct);
        fixture.Db.AdvanceVotes.Add(new AdvanceVote
        {
            ChannelId = Channel, Date = Today, SlackUserId = "U1",
            RestaurantId = restaurant.Id, ChangeCount = 2, VotedAt = BeforeOpen,
        });
        await fixture.Db.SaveChangesAsync(Ct);
        var opens = BeforeOpen.AddHours(1);
        var poll = await service.OpenPollAsync(Today, opens, opens.AddMinutes(30), Ct);
        if (closed)
            poll.Status = PollStatus.Closed;
        poll.MessageTs = "123.456";
        await fixture.Db.SaveChangesAsync(Ct);
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));

        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 2), restaurant.Id));
        var options = await new RestaurantOptionProvider(fixture.Db, advance, Settings()).GetOptions(new BlockOptionsRequest
        {
            ActionId = ActionIds.AdvanceSelect(Today, "U1", 2), Value = "", User = new User { Id = "U1" },
            Container = new Container { ChannelId = Channel },
        });
        var screen = await actions.OpenAsync(new SlashCommand { ChannelId = Channel, UserId = "U1" }, Ct);

        Assert.Empty(options.Options);
        Assert.Equal(2, (await advance.FindAsync(Today, "U1", Ct))!.ChangeCount);
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal(ResponseType.Ephemeral, screen.ResponseType);
        Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Empty(slack.ChatFake.PostAttempts);
        if (!closed)
        {
            Assert.Equal(Today, await advance.GetTargetDateAsync(Ct));
            Assert.NotEmpty(screen.Message.Blocks.OfType<SectionBlock>());
            Assert.Empty(screen.Message.Blocks.OfType<ActionsBlock>());
            return;
        }
        var picker = Assert.Single(screen.Message.Blocks.OfType<ActionsBlock>()
            .SelectMany(b => b.Elements).OfType<ExternalSelectMenu>());
        Assert.True(ActionIds.TryParseAdvanceSelect(picker.ActionId, out var target, out var owner, out var revision));
        Assert.Equal(new DateOnly(2026, 11, 17), target);
        Assert.Equal("U1", owner);
        Assert.Equal(0, revision);
        await actions.Handle(Select(picker.ActionId, restaurant.Id));
        var vote = (await advance.FindAsync(target, "U1", Ct))!;
        Assert.Equal(1, vote.ChangeCount);
        Assert.Equal((target, "U1", (long?)restaurant.Id), (vote.Date, vote.SlackUserId, vote.RestaurantId));
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
    }

    [Fact]
    public async Task 명령어_재진입과_취소는_다른사람의_사전투표를_바꾸지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new Clock(BeforeOpen);
        var slack = new FakeSlackApiClient();
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        await advance.CastAsync(Today, "U1", null, 0, Ct);
        await advance.CastAsync(Today, "U2", null, 0, Ct);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));
        var screen = await actions.OpenAsync(new SlashCommand { ChannelId = Channel, UserId = "U1" }, Ct);
        var cancel = Assert.Single(screen.Message.Blocks.OfType<ActionsBlock>().SelectMany(b => b.Elements)
            .OfType<Button>(), b => ActionIds.TryParseAdvanceCancel(b.ActionId, out _, out _, out _));

        await actions.Handle(new BlockActionRequest
        {
            User = new User { Id = "U1" }, Container = new Container { ChannelId = Channel },
            Actions = [new ButtonAction { ActionId = cancel.ActionId }],
        });

        var cancelled = (await advance.FindAsync(Today, "U1", Ct))!;
        Assert.True(cancelled.IsCancelled);
        Assert.Equal(2, cancelled.ChangeCount);
        var other = (await advance.FindAsync(Today, "U2", Ct))!;
        Assert.False(other.IsCancelled);
        Assert.Equal(1, other.ChangeCount);
        Assert.Equal(1, (await advance.GetSnapshotAsync(Today, Ct)).Abstentions);
        Assert.Single(slack.ChatFake.PostAttempts);
    }

    [Fact]
    public async Task 중복과_오래된화면은_차감하지_않고_취소를_포함한_세번만_공개한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new Clock(BeforeOpen);
        var slack = new FakeSlackApiClient();
        var service = new LunchService(fixture.Db, Channel);
        var a = await service.SaveRestaurantAsync(new(null, "첫식당", 1, null, null, null), "U0", Ct);
        var b = await service.SaveRestaurantAsync(new(null, "다른식당", 3, null, null, null), "U0", Ct);
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));

        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 0), a.Id));
        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 0), a.Id));
        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 0), b.Id));
        Assert.Single(slack.ChatFake.PostAttempts);
        Assert.Equal(1, (await advance.FindAsync(Today, "U1", Ct))!.ChangeCount);

        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 1), b.Id));
        await actions.Handle(new BlockActionRequest
        {
            User = new User { Id = "U1" }, Container = new Container { ChannelId = Channel },
            Actions = [new ButtonAction { ActionId = ActionIds.AdvanceCancel(Today, "U1", 2) }],
        });
        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 3), a.Id));

        var vote = (await advance.FindAsync(Today, "U1", Ct))!;
        Assert.Equal(3, vote.ChangeCount);
        Assert.True(vote.IsCancelled);
        Assert.Equal(3, slack.ChatFake.PostAttempts.Count);
        var screen = await actions.OpenAsync(new SlashCommand { ChannelId = Channel, UserId = "U1" }, Ct);
        Assert.Empty(screen.Message.Blocks.OfType<ActionsBlock>());
        var suggestions = await new RestaurantOptionProvider(fixture.Db, advance, Settings()).GetOptions(new BlockOptionsRequest
        {
            ActionId = ActionIds.AdvanceSelect(Today, "U1", 3), Value = "",
            User = new User { Id = "U1" }, Container = new Container { ChannelId = Channel },
        });
        Assert.Empty(suggestions.Options);
    }

    [Fact]
    public async Task 공개_전송_실패에도_표와_횟수를_유지하며_중복클릭으로_재전송하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new Clock(BeforeOpen);
        var slack = new FakeSlackApiClient();
        slack.ChatFake.OnPostMessage = (_, _) => throw new InvalidOperationException("공개하면 안 되는 오류 원문");
        var restaurant = await new LunchService(fixture.Db, Channel)
            .SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "U0", Ct);
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));
        var request = Select(ActionIds.AdvanceSelect(Today, "U1", 0), restaurant.Id);

        await actions.Handle(request);
        await actions.Handle(request);

        var vote = (await advance.FindAsync(Today, "U1", Ct))!;
        Assert.Equal(restaurant.Id, vote.RestaurantId);
        Assert.Equal(1, vote.ChangeCount);
        Assert.Single(slack.ChatFake.PostAttempts);
        var lastView = slack.ChatFake.EphemeralAttempts[^1].Message;
        var picker = Assert.Single(lastView.Blocks.OfType<ActionsBlock>().SelectMany(b => b.Elements).OfType<ExternalSelectMenu>());
        Assert.True(ActionIds.TryParseAdvanceSelect(picker.ActionId, out _, out _, out var revision));
        Assert.Equal(1, revision);
    }

    [Fact]
    public async Task 공개_전송중_회차가_끝나도_저장완료와_공개실패를_본인에게_알린다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new TestTimeProvider(BeforeOpen);
        var slack = new FakeSlackApiClient();
        var lunch = new LunchService(fixture.Db, Channel);
        var restaurant = await lunch.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "U0", Ct);
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        slack.ChatFake.OnPostMessage = async (_, token) =>
        {
            clock.UtcNow = BeforeOpen.AddHours(2);
            var opens = BeforeOpen.AddHours(1);
            var poll = await lunch.OpenPollAsync(Today, opens, opens.AddMinutes(30), token);
            await lunch.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), token);
            throw new InvalidOperationException("외부 전송 실패 원문");
        };
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));

        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1", 0), restaurant.Id));

        Assert.Equal(Today.AddDays(1), await advance.GetTargetDateAsync(Ct));
        Assert.Equal(1, (await advance.FindAsync(Today, "U1", Ct))!.ChangeCount);
        Assert.Single(slack.ChatFake.PostAttempts);
        var receipt = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", receipt.UserId);
        Assert.Contains("저장", receipt.Message.Text);
        Assert.Contains("공개 안내 전송에 실패", receipt.Message.Text);
        Assert.DoesNotContain("외부 전송 실패 원문", receipt.Message.Text);
    }

    [Fact]
    public async Task 버전_없는_이전_화면은_저장하지_않고_본인에게만_재진입을_안내한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var slack = new FakeSlackApiClient();
        var monitor = Monitor();
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, new Clock(BeforeOpen));
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings(), AdvanceVoteTestSupport.Poster(slack));

        await actions.Handle(Select("pre_select:20261116:U1", 123));

        Assert.Empty(await fixture.Db.AdvanceVotes.ToListAsync(Ct));
        Assert.Empty(slack.ChatFake.PostAttempts);
        Assert.Equal("U1", Assert.Single(slack.ChatFake.EphemeralAttempts).UserId);
    }

    static BlockActionRequest Select(string actionId, long restaurantId) => new()
    {
        User = new User { Id = "U1" }, Container = new Container { ChannelId = Channel },
        Actions = [new ExternalSelectAction { ActionId = actionId, SelectedOption = new Option { Value = restaurantId.ToString() } }],
    };

    static IOptionsMonitor<LunchOptions> Monitor() => StubApi.Of<IOptionsMonitor<LunchOptions>>(
        ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
    static IOptions<SlackOptions> Settings() => Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = Channel });
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
