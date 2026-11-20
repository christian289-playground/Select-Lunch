using Microsoft.EntityFrameworkCore;
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
using Option = SlackNet.Blocks.Option;
using MealRecord = SelectLunch.Shared.Entities.MealRecord;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// action_id 라우팅이 이 태스크의 핵심 위험이다 — 잘못 라우팅된 id는
/// 눌러도 아무 반응이 없는 버튼이 되고, 조용히 실패하므로 알아채기 어렵다.
/// 기존 메시지의 버튼·선택은 확정 창을 열 뿐 식사 기록을 쓰지 않는다 — 기록은 창의 제출이 한다.
/// </summary>
public class MealActionHandlerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);

    static async Task<(TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, MealActionHandler Handler)>
        SetupAsync(TestTimeProvider? clock = null, LunchOptions? options = null)
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var configuredOptions = options ?? new LunchOptions();
        clock ??= new TestTimeProvider(new DateTimeOffset(2026, 9, 18, 3, 0, 0, TimeSpan.Zero));
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => configuredOptions)));
        var meals = new MealConfirmationService(fixture.Db, service, Channel, monitor, clock);
        var handler = new MealActionHandler(fixture.Db, meals, slack, monitor, clock);
        return (fixture, service, slack, handler);
    }

    static BlockActionRequest Request(BlockAction action, string channel = Channel) => new()
    {
        TriggerId = "T1",
        User = new User { Id = "U1" },
        Container = new Container { ChannelId = channel, Type = "message" },
        Actions = [action],
    };

    static BlockAction Pick(string kind, DateOnly date, long restaurantId) => kind switch
    {
        "button" => new ButtonAction { ActionId = ActionIds.Meal(date, restaurantId) },
        "static" => new StaticSelectAction
        {
            ActionId = ActionIds.Meal(date, 0),
            SelectedOption = new Option { Value = restaurantId.ToString() },
        },
        _ => new ExternalSelectAction
        {
            ActionId = ActionIds.Meal(date, 0),
            SelectedOption = new Option { Value = restaurantId.ToString() },
        },
    };

    static async Task<Restaurant> SeedAsync(LunchService service, string name, long? categoryId = 1) =>
        await service.SaveRestaurantAsync(
            new(null, name, categoryId, null, null, null), "U1", TestContext.Current.CancellationToken);

    /// <summary>마감된 본투표에서 식당을 고른 사람 <paramref name="voters"/>명을 심는다.</summary>
    static async Task SeedClosedPollAsync(TestDb fixture, DateOnly date, int voters, long restaurantId)
    {
        var ct = TestContext.Current.CancellationToken;
        var poll = new LunchPoll
        {
            ChannelId = Channel, Date = date, Status = PollStatus.Closed,
            OpensAt = DateTimeOffset.UnixEpoch, ClosesAt = DateTimeOffset.UnixEpoch.AddHours(1),
        };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync(ct);

        for (var i = 0; i < voters; i++)
        {
            fixture.Db.PollVotes.Add(new PollVote
            {
                PollId = poll.Id, SlackUserId = $"V{i}", RestaurantId = restaurantId,
                VotedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await fixture.Db.SaveChangesAsync(ct);
    }

    static async Task SeedMealAsync(TestDb fixture, DateOnly date, long restaurantId, string recordedBy)
    {
        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = Channel, Date = date, RestaurantId = restaurantId,
            RecordedBySlackUserId = recordedBy, RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    static ModalViewDefinition OpenedView(FakeSlackApiClient slack) =>
        Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);

    static ExternalMultiSelectMenu PlacesMenu(ModalViewDefinition view)
    {
        var block = Assert.Single(view.Blocks.OfType<InputBlock>(), b => b.BlockId == MealConfirmationModal.PlacesBlockId);
        return Assert.IsType<ExternalMultiSelectMenu>(block.Element);
    }

    static string[] InitialValues(ExternalMultiSelectMenu menu) =>
        [.. (menu.InitialOptions ?? []).Select(o => o.Value)];

    [Fact]
    public async Task 지난_기록_선택은_과거_식사기록을_덮어쓰지_않는다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero));
        var (fixture, service, slack, handler) = await SetupAsync(clock);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pastDate = new DateOnly(2026, 10, 2);
        var currentDate = new DateOnly(2026, 10, 6);
        var original = await SeedAsync(service, "옛날경성순대국 광교중앙역점");
        var selected = await SeedAsync(service, "긴자료코 광교도청점", 3);
        await SeedMealAsync(fixture, pastDate, original.Id, "ORIGINAL");
        var before = await fixture.Db.MealRecords.AsNoTracking().SingleAsync(ct);

        await handler.Handle(Request(new ExternalSelectAction
        {
            ActionId = ActionIds.Meal(pastDate, 0),
            SelectedOption = new Option { Value = selected.Id.ToString() },
        }));

        fixture.Db.ChangeTracker.Clear();
        var after = await fixture.Db.MealRecords.SingleAsync(ct);
        Assert.Equal(original.Id, after.RestaurantId);
        Assert.Equal(before.RecordedBySlackUserId, after.RecordedBySlackUserId);
        Assert.Equal(before.RecordedAt, after.RecordedAt);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(m => m.Date == currentDate, ct));
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        var feedback = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", feedback.UserId);
        Assert.Equal(Channel, feedback.Message.Channel);
    }

    [Theory]
    [InlineData("button", -1)]
    [InlineData("static", -1)]
    [InlineData("external", 1)]
    public async Task 현재_날짜가_아닌_선택은_확정창도_식사기록도_만들지_않는다(string kind, int dayOffset)
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await SeedAsync(service, "검증식당");

        await handler.Handle(Request(Pick(kind, Today.AddDays(dayOffset), restaurant.Id)));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        var feedback = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", feedback.UserId);
        Assert.Equal(Channel, feedback.Message.Channel);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task 다른_날짜의_신규등록_버튼은_모달을_열지_않는다(int dayOffset)
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.MealNew(Today.AddDays(dayOffset)) }));

        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.False(await fixture.Db.Restaurants.AnyAsync(TestContext.Current.CancellationToken));
        Assert.Single(slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 한국_날짜의_오늘_선택은_UTC가_전날이어도_확정창을_연다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero));
        var (fixture, service, slack, handler) = await SetupAsync(clock);
        await using var _ = fixture;
        var restaurant = await SeedAsync(service, "자정식당");
        var koreanToday = new DateOnly(2026, 10, 6);

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(koreanToday, restaurant.Id) }));

        var view = OpenedView(slack);
        Assert.True(MealConfirmationModal.TryDate(view.PrivateMetadata, Channel, "U1", out var date));
        Assert.Equal(koreanToday, date);
        Assert.Equal([restaurant.Id.ToString()], InitialValues(PlacesMenu(view)));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 시간대_설정이_바뀌면_변경된_오늘_날짜를_사용한다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero));
        var options = new LunchOptions { TimeZone = "UTC" };
        var (fixture, service, slack, handler) = await SetupAsync(clock, options);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await SeedAsync(service, "시간대식당");
        var action = new ButtonAction { ActionId = ActionIds.Meal(new DateOnly(2026, 10, 6), restaurant.Id) };

        await handler.Handle(Request(action));
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Single(slack.ChatFake.EphemeralAttempts);

        options.TimeZone = "Asia/Seoul";
        await handler.Handle(Request(action));
        Assert.Equal(1, slack.ViewsFake.OpenCallCount);
        Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task meal_new_action_id는_기록용_등록_모달을_연다()
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.MealNew(Today) }));

        Assert.Equal(1, slack.ViewsFake.OpenCallCount);
        Assert.Equal("T1", slack.ViewsFake.LastTriggerId);
        var view = OpenedView(slack);
        Assert.Equal(RestaurantModal.MealCallbackId, view.CallbackId);
        Assert.True(MealConfirmationModal.TryDate(view.PrivateMetadata, Channel, "U1", out var date));
        Assert.Equal(Today, date);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 기존_버튼은_확정창을_열고_식사는_아직_기록하지_않는다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await SeedAsync(service, "스시로", 3);

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(Today, 식당.Id) }));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(1, slack.ViewsFake.OpenCallCount);
        Assert.Equal("T1", slack.ViewsFake.LastTriggerId);
        var view = OpenedView(slack);
        Assert.Equal(MealConfirmationModal.CallbackId, view.CallbackId);
        Assert.True(MealConfirmationModal.TryDate(view.PrivateMetadata, Channel, "U1", out var date));
        Assert.Equal(Today, date);
        var menu = PlacesMenu(view);
        Assert.Equal([식당.Id.ToString()], InitialValues(menu));
        Assert.Equal("스시로", Assert.Single(menu.InitialOptions).Text.Text);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        Assert.Empty(slack.ChatFake.EphemeralAttempts);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("external")]
    public async Task 드롭다운_선택값이_자리표시자_0을_대체해_그_식당을_담은_확정창을_연다(string kind)
    {
        // 드롭다운이 external_select로 바뀌면 payload 타입도 ExternalSelectAction이 된다.
        // StaticSelectAction만 보던 코드는 선택을 통째로 무시해 창이 조용히 안 열린다.
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await SeedAsync(service, "국밥집");

        await handler.Handle(Request(Pick(kind, Today, 식당.Id)));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal([식당.Id.ToString()], InitialValues(PlacesMenu(OpenedView(slack))));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("external")]
    public async Task 선택값_없는_드롭다운은_창을_열지도_기록하지도_않는다(string kind)
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        BlockAction action = kind == "static"
            ? new StaticSelectAction { ActionId = ActionIds.Meal(Today, 0), SelectedOption = null }
            : new ExternalSelectAction { ActionId = ActionIds.Meal(Today, 0), SelectedOption = null };
        await handler.Handle(Request(action));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        Assert.Empty(slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 알수없는_action_id는_아무것도_하지_않는다()
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        await handler.Handle(Request(new ButtonAction { ActionId = $"vote:1:2" }));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
    }

    [Theory]
    [InlineData("pick")]
    [InlineData("new")]
    public async Task 구성_채널이_아닌_요청은_조용히_무시한다(string kind)
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var restaurant = await SeedAsync(service, "다른채널식당");
        BlockAction action = kind == "new"
            ? new ButtonAction { ActionId = ActionIds.MealNew(Today) }
            : Pick("button", Today, restaurant.Id);

        await handler.Handle(Request(action, channel: "C-OTHER"));

        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        Assert.Empty(slack.ChatFake.EphemeralAttempts);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("archived")]
    [InlineData("pending")]
    public async Task 없거나_기록할_수_없는_식당은_확정창을_열지_않고_본인에게_알린다(string kind)
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        long id;
        switch (kind)
        {
            case "archived":
                var archived = await SeedAsync(service, "보관식당");
                archived.Status = RestaurantStatus.Archived;
                await fixture.Db.SaveChangesAsync(ct);
                id = archived.Id;
                break;
            case "pending":
                id = (await SeedAsync(service, "분류전식당", categoryId: null)).Id;
                break;
            default:
                id = 9999;
                break;
        }

        await handler.Handle(Request(Pick("button", Today, id)));

        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        var feedback = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", feedback.UserId);
        Assert.Equal(Channel, feedback.Message.Channel);
    }

    [Fact]
    public async Task 당일_정정은_기록된_전체_목록에서_시작하고_클릭한_식당을_섞지_않는다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var first = await SeedAsync(service, "첫식당");
        var second = await SeedAsync(service, "둘째식당", 3);
        var clicked = await SeedAsync(service, "클릭식당", 5);
        await SeedClosedPollAsync(fixture, Today, voters: 6, first.Id);
        await SeedMealAsync(fixture, Today, first.Id, "U9");
        await SeedMealAsync(fixture, Today, second.Id, "U9");

        await handler.Handle(Request(Pick("button", Today, clicked.Id)));

        var menu = PlacesMenu(OpenedView(slack));
        Assert.Equal([first.Id.ToString(), second.Id.ToString()], InitialValues(menu));
        Assert.Equal(2, menu.MaxSelectedItems);
        Assert.Equal(2, await fixture.Db.MealRecords.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 한도를_낮춘_뒤_기존_기록이_더_많아도_자르지_않고_슬랙이_받을_수_있게_연다()
    {
        // 기준을 올리거나 투표 기록이 달라져 한도가 1이 됐는데 이미 두 곳이 기록된 경우(hot reload).
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var first = await SeedAsync(service, "첫식당");
        var second = await SeedAsync(service, "둘째식당", 3);
        await SeedMealAsync(fixture, Today, first.Id, "U9");
        await SeedMealAsync(fixture, Today, second.Id, "U9");

        await handler.Handle(Request(Pick("button", Today, first.Id)));

        var menu = PlacesMenu(OpenedView(slack));
        Assert.Equal([first.Id.ToString(), second.Id.ToString()], InitialValues(menu));
        // 초기 선택 수보다 작은 상한은 슬랙이 창 자체를 거부한다.
        Assert.True(menu.MaxSelectedItems >= menu.InitialOptions.Count);
        Assert.Equal(2, await fixture.Db.MealRecords.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 오늘_새_기록의_한도는_마감된_본투표_식당_인원으로_정한다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var restaurant = await SeedAsync(service, "투표식당");
        await SeedClosedPollAsync(fixture, Today, voters: 6, restaurant.Id);

        await handler.Handle(Request(Pick("button", Today, restaurant.Id)));

        Assert.Equal(2, PlacesMenu(OpenedView(slack)).MaxSelectedItems);
    }

    [Fact]
    public async Task 지정된_지난_회차는_7명_기준5일_때_두_곳까지_고르는_새_창을_연다()
    {
        var past = Today.AddDays(-1);
        var options = new LunchOptions { CarryoverMealPromptDate = past, MealParticipantsPerRestaurant = 5 };
        var (fixture, service, slack, handler) = await SetupAsync(options: options);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await SeedAsync(service, "지난회차식당");
        await service.MarkMealPromptPostedAsync(past, DateTimeOffset.UnixEpoch, ct);
        await SeedClosedPollAsync(fixture, past, voters: 7, restaurant.Id);

        await handler.Handle(Request(Pick("button", past, restaurant.Id)));

        Assert.Equal(1, slack.ViewsFake.OpenCallCount);
        var view = OpenedView(slack);
        Assert.True(MealConfirmationModal.TryDate(view.PrivateMetadata, Channel, "U1", out var date));
        Assert.Equal(past, date);
        var menu = PlacesMenu(view);
        Assert.Equal(2, menu.MaxSelectedItems);
        Assert.Equal([restaurant.Id.ToString()], InitialValues(menu));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Empty(slack.ChatFake.EphemeralAttempts);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 지정된_지난_회차의_신규등록도_같은_상태로_연다()
    {
        var past = Today.AddDays(-1);
        var options = new LunchOptions { CarryoverMealPromptDate = past };
        var (fixture, service, slack, handler) = await SetupAsync(options: options);
        await using var _ = fixture;
        await service.MarkMealPromptPostedAsync(past, DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.MealNew(past) }));

        var view = OpenedView(slack);
        Assert.Equal(RestaurantModal.MealCallbackId, view.CallbackId);
        Assert.True(MealConfirmationModal.TryDate(view.PrivateMetadata, Channel, "U1", out var date));
        Assert.Equal(past, date);
        Assert.Empty(slack.ChatFake.EphemeralAttempts);
    }

    [Theory]
    [InlineData("already-recorded")]
    [InlineData("not-posted")]
    [InlineData("other-date")]
    public async Task 지정된_지난_회차도_조건이_어긋나면_창을_열지_않는다(string reason)
    {
        var past = Today.AddDays(-1);
        var options = new LunchOptions
        {
            CarryoverMealPromptDate = reason == "other-date" ? Today.AddDays(-2) : past,
        };
        var (fixture, service, slack, handler) = await SetupAsync(options: options);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await SeedAsync(service, "지난회차식당");
        if (reason != "not-posted")
            await service.MarkMealPromptPostedAsync(past, DateTimeOffset.UnixEpoch, ct);
        if (reason == "already-recorded")
            await SeedMealAsync(fixture, past, restaurant.Id, "ORIGINAL");

        await handler.Handle(Request(Pick("button", past, restaurant.Id)));

        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        var feedback = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", feedback.UserId);
        Assert.Equal(reason == "already-recorded" ? 1 : 0, await fixture.Db.MealRecords.CountAsync(ct));
    }
}
