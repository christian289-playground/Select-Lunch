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
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Tests;

public class RestaurantEditFlowTests
{
    const string Channel = "C1";
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static LunchSlashCommandHandler Commands(TestDb fixture, FakeSlackApiClient slack) => new(
        fixture.Db, slack,
        StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions()))),
        TestPollLeaveRoster.Disabled,
        Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = Channel }));

    static Task<Restaurant> AddAsync(TestDb fixture, string name = "기존식당") =>
        new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new(null, name, 1, 7, 2, "점심 메모", "수원시", WaitLevel.Moderate,
                DepartureTime: new TimeOnly(11, 15), EarlyDepartureSeconds: 600), "U1", Ct);

    [Fact]
    public async Task 상호_없는_수정은_등록_입력폼_대신_검색_선택창을_연다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await AddAsync(fixture);
        var slack = new FakeSlackApiClient();

        await Commands(fixture, slack).Handle(new SlashCommand { Text = "edit", TriggerId = "T1", UserId = "U1" });

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.Null(view.Submit);
        Assert.Single(view.Blocks.OfType<ActionsBlock>().SelectMany(b => b.Elements).OfType<ExternalSelectMenu>());
        Assert.Empty(view.Blocks.OfType<InputBlock>());
        Assert.Equal(new[] { "기존식당" }, await fixture.Db.Restaurants.Select(r => r.Name).ToArrayAsync(Ct));
    }

    [Fact]
    public async Task 등록된_식당이_없어도_수정창에서_새_식당을_저장할_수는_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var slack = new FakeSlackApiClient();

        await Commands(fixture, slack).Handle(new SlashCommand { Text = "edit", TriggerId = "T1", UserId = "U1" });

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.Null(view.Submit);
        Assert.Empty(view.Blocks.OfType<ActionsBlock>().SelectMany(b => b.Elements).OfType<ExternalSelectMenu>());
        Assert.Empty(await fixture.Db.Restaurants.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task 신규_등록은_기존_식당이_있어도_빈_등록폼을_연다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await AddAsync(fixture);
        var slack = new FakeSlackApiClient();

        await Commands(fixture, slack).Handle(new SlashCommand { Text = "add", TriggerId = "T1", UserId = "U1" });

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        var name = Assert.IsType<PlainTextInput>(view.Blocks.OfType<InputBlock>().Single(b => b.BlockId == RestaurantModal.BlockIds.Name).Element);
        Assert.True(string.IsNullOrEmpty(name.InitialValue));
        Assert.Null(ModalContext.Parse(view.PrivateMetadata).RestaurantId);
        Assert.NotNull(view.Submit);
    }

    [Fact]
    public async Task 선택한_기존_식당의_값을_불러와_같은_행을_수정한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var selected = await AddAsync(fixture);
        var other = await AddAsync(fixture, "다른식당");
        var beforeUpdated = selected.UpdatedAt;
        var slack = new FakeSlackApiClient();

        await new RestaurantEditActionHandler(fixture.Db, slack).Handle(Selection(selected.Id));

        var update = Assert.Single(slack.ViewsFake.Updates);
        Assert.Equal("V1", update.ViewId);
        Assert.Equal("hash1", update.Hash);
        var view = Assert.IsType<ModalViewDefinition>(update.View);
        Assert.Equal(selected.Id, ModalContext.Parse(view.PrivateMetadata).RestaurantId);
        Assert.Equal("기존식당", Input(view, RestaurantModal.BlockIds.Name).InitialValue);
        Assert.Equal("10", Input(view, RestaurantModal.BlockIds.EarlyDeparture).InitialValue);
        Assert.Equal("11:15", Input(view, RestaurantModal.BlockIds.DepartureTime).InitialValue);
        Assert.Equal(beforeUpdated, (await fixture.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Id == selected.Id, Ct)).UpdatedAt);

        var state = new ViewState { Values = [] };
        foreach (var block in view.Blocks.OfType<InputBlock>())
        {
            ElementValue value = block.Element switch
            {
                PlainTextInput input => new PlainTextInputValue { Value = input.InitialValue },
                StaticSelectMenu menu => new StaticSelectValue { SelectedOption = menu.InitialOption },
                _ => throw new InvalidOperationException("알 수 없는 식당 입력 형식"),
            };
            state.Values[block.BlockId] = new() { [block.Element.ActionId] = value };
        }
        state.Values[RestaurantModal.BlockIds.EarlyDeparture][RestaurantModal.BlockIds.EarlyDeparture + "_input"] =
            new PlainTextInputValue { Value = "15" };
        var service = new LunchService(fixture.Db, Channel);
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
        var handler = new RestaurantModalHandler(service,
            new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled),
            monitor, TimeProvider.System, new EarlyDepartureNoticeTracker());
        await handler.Handle(new ViewSubmission
        {
            User = new User { Id = "U1" },
            View = new ModalViewInfo { PrivateMetadata = view.PrivateMetadata, State = state },
        });

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Restaurants.SingleAsync(r => r.Id == selected.Id, Ct);
        Assert.Equal(900, stored.EarlyDepartureSeconds);
        Assert.Equal("수원시", stored.Address);
        Assert.Equal("점심 메모", stored.Note);
        Assert.Equal(7, stored.WalkMinutes);
        Assert.Equal(2, stored.PriceLevel);
        Assert.Equal(WaitLevel.Moderate, stored.WaitLevel);
        Assert.Equal(new TimeOnly(11, 15), stored.DepartureTime);
        Assert.Equal(600, (await fixture.Db.Restaurants.SingleAsync(r => r.Id == other.Id, Ct)).EarlyDepartureSeconds);
        Assert.Equal(new[] { selected.Id, other.Id }, await fixture.Db.Restaurants.OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync(Ct));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("archived")]
    public async Task 사라졌거나_보관된_식당은_새_등록폼으로_대체하지_않는다(string reason)
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = await AddAsync(fixture);
        if (reason == "archived")
        {
            restaurant.Status = RestaurantStatus.Archived;
            await fixture.Db.SaveChangesAsync(Ct);
        }
        var slack = new FakeSlackApiClient();

        await new RestaurantEditActionHandler(fixture.Db, slack)
            .Handle(Selection(reason == "missing" ? restaurant.Id + 100 : restaurant.Id));

        var view = Assert.IsType<ModalViewDefinition>(Assert.Single(slack.ViewsFake.Updates).View);
        Assert.Null(view.Submit);
        Assert.Empty(view.Blocks.OfType<InputBlock>());
        Assert.Equal(new[] { restaurant.Id }, await fixture.Db.Restaurants.Select(r => r.Id).ToArrayAsync(Ct));
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("owner")]
    [InlineData("view")]
    [InlineData("hash")]
    [InlineData("selection")]
    public async Task 오래되거나_다른_사용자의_선택_이벤트는_수정창을_바꾸지_않는다(string invalid)
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = await AddAsync(fixture);
        var request = Selection(restaurant.Id);
        switch (invalid)
        {
            case "callback": request.View.CallbackId = RestaurantModal.CallbackId; break;
            case "owner": request.View.PrivateMetadata = "U2"; break;
            case "view": request.View.Id = ""; break;
            case "hash": request.View.Hash = ""; break;
            case "selection": ((ExternalSelectAction)request.Action).SelectedOption.Value = "invalid"; break;
        }
        var slack = new FakeSlackApiClient();

        await new RestaurantEditActionHandler(fixture.Db, slack).Handle(request);

        Assert.Empty(slack.ViewsFake.Updates);
        Assert.Null(slack.ViewsFake.LastView);
    }

    static PlainTextInput Input(ModalViewDefinition view, string blockId) =>
        (PlainTextInput)view.Blocks.OfType<InputBlock>().Single(b => b.BlockId == blockId).Element;

    static BlockActionRequest Selection(long restaurantId) => new()
    {
        User = new User { Id = "U1" },
        View = new ModalViewInfo
        {
            Id = "V1", Hash = "hash1", CallbackId = RestaurantEditPicker.CallbackId, PrivateMetadata = "U1",
        },
        Actions =
        [
            new ExternalSelectAction
            {
                ActionId = ActionIds.RestaurantEditSelect,
                SelectedOption = new Option { Value = restaurantId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            },
        ],
    };
}
