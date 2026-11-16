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
    public async Task 명령어로_고른_식당은_본인에게만_응답하고_개시때_한표로_반영된다()
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
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, settings);
        var commands = new LunchSlashCommandHandler(fixture.Db, slack, monitor, TestPollLeaveRoster.Disabled, settings, actions);

        var screen = await commands.Handle(new SlashCommand { Text = "pre", ChannelId = Channel, UserId = "U1" });
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
        Assert.Null(slack.ChatFake.PostedMessage);
        Assert.Null(slack.ChatFake.UpdatedMessage);
        Assert.Equal(first.Id, (await advance.FindAsync(Today, "U1", Ct))!.RestaurantId);

        var opens = BeforeOpen.AddHours(1);
        var poll = await service.OpenPollAsync(Today, opens, opens.AddMinutes(30), Ct);
        Assert.Equal(first.Id, Assert.Single(await fixture.Db.PollVotes.ToListAsync(Ct)).RestaurantId);
        Assert.Empty(await fixture.Db.AdvanceVotes.ToListAsync(Ct));
        await service.CastVoteAsync(poll.Id, "U1", second.Id, Ct);
        Assert.Equal(second.Id, Assert.Single(await fixture.Db.PollVotes.ToListAsync(Ct)).RestaurantId);
        Assert.Null(slack.ChatFake.PostedMessage);
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
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings());
        var request = Select(ActionIds.AdvanceSelect(Today.AddDays(-dayOffset), "U1"), restaurant.Id);
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
    public async Task 오늘_투표가_시작됐거나_마감됐으면_옛화면은_막고_명령어는_내일_투표를_연다(int hour, bool closed)
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = Monitor();
        var clock = new Clock(BeforeOpen.AddHours(hour - 10));
        var slack = new FakeSlackApiClient();
        var service = new LunchService(fixture.Db, Channel);
        var restaurant = await service.SaveRestaurantAsync(new(null, "식당", 1, null, null, null), "U1", Ct);
        var opens = BeforeOpen.AddHours(1);
        var poll = await service.OpenPollAsync(Today, opens, opens.AddMinutes(30), Ct);
        if (closed)
            poll.Status = PollStatus.Closed;
        poll.MessageTs = "123.456";
        await fixture.Db.SaveChangesAsync(Ct);
        var advance = new AdvanceVoteService(fixture.Db, Channel, monitor, clock);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings());

        await actions.Handle(Select(ActionIds.AdvanceSelect(Today, "U1"), restaurant.Id));
        var options = await new RestaurantOptionProvider(fixture.Db, advance, Settings()).GetOptions(new BlockOptionsRequest
        {
            ActionId = ActionIds.AdvanceSelect(Today, "U1"), Value = "", User = new User { Id = "U1" },
            Container = new Container { ChannelId = Channel },
        });
        var screen = await actions.OpenAsync(new SlashCommand { ChannelId = Channel, UserId = "U1" }, Ct);

        Assert.Empty(options.Options);
        Assert.Empty(await fixture.Db.AdvanceVotes.ToListAsync(Ct));
        Assert.Empty(await fixture.Db.PollVotes.ToListAsync(Ct));
        Assert.Equal(ResponseType.Ephemeral, screen.ResponseType);
        Assert.Contains(slack.ChatFake.PermalinkUrl, Assert.Single(slack.ChatFake.EphemeralAttempts).Message.Text);
        var picker = Assert.Single(screen.Message.Blocks.OfType<ActionsBlock>()
            .SelectMany(b => b.Elements).OfType<ExternalSelectMenu>());
        Assert.True(ActionIds.TryParseAdvanceSelect(picker.ActionId, out var target, out var owner));
        Assert.Equal(new DateOnly(2026, 11, 17), target);
        Assert.Equal("U1", owner);
        await actions.Handle(Select(picker.ActionId, restaurant.Id));
        var vote = Assert.Single(await fixture.Db.AdvanceVotes.ToListAsync(Ct));
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
        await advance.CastAsync(Today, "U1", null, Ct);
        await advance.CastAsync(Today, "U2", null, Ct);
        var actions = new AdvanceVoteActionHandler(fixture.Db, advance, slack, monitor, Settings());
        var screen = await actions.OpenAsync(new SlashCommand { ChannelId = Channel, UserId = "U1" }, Ct);
        var cancel = Assert.Single(screen.Message.Blocks.OfType<ActionsBlock>().SelectMany(b => b.Elements)
            .OfType<Button>(), b => ActionIds.TryParseAdvanceCancel(b.ActionId, out _, out _));

        await actions.Handle(new BlockActionRequest
        {
            User = new User { Id = "U1" }, Container = new Container { ChannelId = Channel },
            Actions = [new ButtonAction { ActionId = cancel.ActionId }],
        });

        Assert.Null(await advance.FindAsync(Today, "U1", Ct));
        Assert.Equal("U2", Assert.Single(await fixture.Db.AdvanceVotes.ToListAsync(Ct)).SlackUserId);
        Assert.Null(slack.ChatFake.PostedMessage);
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
