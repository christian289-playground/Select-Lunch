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

namespace SelectLunch.Slack.Tests;

/// <summary>
/// action_id 라우팅이 이 태스크의 핵심 위험이다 — 잘못 라우팅된 id는
/// 눌러도 아무 반응이 없는 버튼이 되고, 조용히 실패하므로 알아채기 어렵다.
/// </summary>
public class MealActionHandlerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);

    static async Task<(TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, MealActionHandler Handler)>
        SetupAsync(TestTimeProvider? clock = null, LunchOptions? options = null,
            EarlyDepartureNoticeTracker? notices = null)
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);
        var configuredOptions = options ?? new LunchOptions();
        clock ??= new TestTimeProvider(new DateTimeOffset(2026, 9, 18, 3, 0, 0, TimeSpan.Zero));
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => configuredOptions)));
        var handler = new MealActionHandler(fixture.Db, service, announcer, slack, monitor, clock,
            notices ?? new EarlyDepartureNoticeTracker());
        return (fixture, service, slack, handler);
    }

    static BlockActionRequest Request(BlockAction action) => new()
    {
        TriggerId = "T1",
        User = new User { Id = "U1" },
        Container = new Container { ChannelId = Channel, Type = "message" },
        Actions = [action],
    };

    [Fact]
    public async Task 식사_기록이_완료되면_그날_미리출발_알림을_종료하고_다음날_다시_알린다()
    {
        var notices = new EarlyDepartureNoticeTracker();
        var (fixture, service, _, handler) = await SetupAsync(notices: notices);
        await using var disposal = fixture;
        var restaurant = await service.SaveRestaurantAsync(
            new(null, "미리출발식당", 1, null, null, null), "U1", TestContext.Current.CancellationToken);
        Assert.True(notices.TryClaim(Today, restaurant.Id, "U1"));

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(Today, restaurant.Id) }));

        Assert.False(notices.TryClaim(Today, restaurant.Id, "U2"));
        Assert.True(notices.TryClaim(Today.AddDays(1), restaurant.Id, "U1"));
    }

    [Fact]
    public async Task 거절된_과거_식사기록은_오늘_미리출발_알림을_초기화하지_않는다()
    {
        var notices = new EarlyDepartureNoticeTracker();
        var (fixture, service, _, handler) = await SetupAsync(notices: notices);
        await using var disposal = fixture;
        var restaurant = await service.SaveRestaurantAsync(
            new(null, "미리출발식당", 1, null, null, null), "U1", TestContext.Current.CancellationToken);
        Assert.True(notices.TryClaim(Today, restaurant.Id, "U1"));

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(Today.AddDays(-1), restaurant.Id) }));

        Assert.False(notices.TryClaim(Today, restaurant.Id, "U1"));
        Assert.True(notices.TryClaim(Today, restaurant.Id, "U2"));
    }

    [Fact]
    public async Task 지난_기록_선택은_과거_식사기록을_덮어쓰지_않는다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero));
        var (fixture, service, slack, handler) = await SetupAsync(clock);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pastDate = new DateOnly(2026, 10, 2);
        var currentDate = new DateOnly(2026, 10, 6);
        var original = await service.SaveRestaurantAsync(
            new(null, "옛날경성순대국 광교중앙역점", 1, null, null, null), "ORIGINAL", ct);
        var selected = await service.SaveRestaurantAsync(
            new(null, "긴자료코 광교도청점", 3, null, null, null), "U1", ct);
        await service.RecordMealAsync(pastDate, original.Id, "ORIGINAL", MealSource.Prompt, ct);
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
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        var feedback = Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", feedback.UserId);
        Assert.Equal(Channel, feedback.Message.Channel);
    }

    [Theory]
    [InlineData("button", -1)]
    [InlineData("static", -1)]
    [InlineData("external", 1)]
    public async Task 현재_날짜가_아닌_선택은_식사기록을_생성하지_않는다(string kind, int dayOffset)
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await service.SaveRestaurantAsync(new(null, "검증식당", 1, null, null, null), "U1", ct);
        var requestedDate = Today.AddDays(dayOffset);
        BlockAction action = kind switch
        {
            "button" => new ButtonAction { ActionId = ActionIds.Meal(requestedDate, restaurant.Id) },
            "static" => new StaticSelectAction
            {
                ActionId = ActionIds.Meal(requestedDate, 0),
                SelectedOption = new Option { Value = restaurant.Id.ToString() },
            },
            _ => new ExternalSelectAction
            {
                ActionId = ActionIds.Meal(requestedDate, 0),
                SelectedOption = new Option { Value = restaurant.Id.ToString() },
            },
        };

        await handler.Handle(Request(action));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
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
    public async Task 한국_날짜의_오늘_선택은_UTC가_전날이어도_저장된다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero));
        var (fixture, service, _, handler) = await SetupAsync(clock);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await service.SaveRestaurantAsync(new(null, "자정식당", 1, null, null, null), "U1", ct);

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(new DateOnly(2026, 10, 6), restaurant.Id) }));

        var record = await fixture.Db.MealRecords.SingleAsync(ct);
        Assert.Equal(new DateOnly(2026, 10, 6), record.Date);
        Assert.Equal(restaurant.Id, record.RestaurantId);
    }

    [Fact]
    public async Task 시간대_설정이_바뀌면_변경된_오늘_날짜를_사용한다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero));
        var options = new LunchOptions { TimeZone = "UTC" };
        var (fixture, service, slack, handler) = await SetupAsync(clock, options);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await service.SaveRestaurantAsync(new(null, "시간대식당", 1, null, null, null), "U1", ct);
        var action = new ButtonAction { ActionId = ActionIds.Meal(new DateOnly(2026, 10, 6), restaurant.Id) };

        await handler.Handle(Request(action));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));

        options.TimeZone = "Asia/Seoul";
        await handler.Handle(Request(action));
        Assert.Equal(new DateOnly(2026, 10, 6), (await fixture.Db.MealRecords.SingleAsync(ct)).Date);
        Assert.Single(slack.ChatFake.EphemeralAttempts);
        Assert.Equal(1, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task meal_new_action_id는_등록_모달을_연다()
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.MealNew(Today) }));

        Assert.Equal(1, slack.ViewsFake.OpenCallCount);
        Assert.Equal("T1", slack.ViewsFake.LastTriggerId);
        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.Equal(ModalContext.ForRecord(Today).Serialize(), view.PrivateMetadata);
    }

    [Fact]
    public async Task 버튼_action_id는_식사를_기록하고_메시지를_보낸다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "스시로", 3, null, null, null), "U1", ct);

        await handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(Today, 식당.Id) }));

        var record = await fixture.Db.MealRecords.SingleAsync(m => m.ChannelId == Channel && m.Date == Today, ct);
        Assert.Equal(식당.Id, record.RestaurantId);
        Assert.Equal(1, slack.ChatFake.PostCallCount);
        Assert.Contains("스시로", slack.ChatFake.PostedMessage!.Text);
    }

    [Fact]
    public async Task 드롭다운_선택값이_자리표시자_0을_실제_식당ID로_대체한다()
    {
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "국밥집", 1, null, null, null), "U1", ct);

        var action = new StaticSelectAction
        {
            ActionId = ActionIds.Meal(Today, 0),
            SelectedOption = new Option { Value = 식당.Id.ToString() },
        };
        await handler.Handle(Request(action));

        var record = await fixture.Db.MealRecords.SingleAsync(m => m.ChannelId == Channel && m.Date == Today, ct);
        Assert.Equal(식당.Id, record.RestaurantId);
    }

    [Fact]
    public async Task 외부선택_드롭다운도_같은_방식으로_기록한다()
    {
        // 드롭다운이 external_select로 바뀌면 payload 타입도 ExternalSelectAction이 된다.
        // StaticSelectAction만 보던 코드는 선택을 통째로 무시해 기록이 조용히 사라진다.
        var (fixture, service, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(new(null, "국밥집", 1, null, null, null), "U1", ct);

        var action = new ExternalSelectAction
        {
            ActionId = ActionIds.Meal(Today, 0),
            SelectedOption = new Option { Value = 식당.Id.ToString() },
        };
        await handler.Handle(Request(action));

        var record = await fixture.Db.MealRecords.SingleAsync(m => m.ChannelId == Channel && m.Date == Today, ct);
        Assert.Equal(식당.Id, record.RestaurantId);
    }

    [Fact]
    public async Task 선택값_없는_외부선택도_기록하지_않는다()
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var action = new ExternalSelectAction { ActionId = ActionIds.Meal(Today, 0), SelectedOption = null };
        await handler.Handle(Request(action));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(m => m.ChannelId == Channel && m.Date == Today, ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }


    [Fact]
    public async Task 선택값_없는_드롭다운_자리표시자는_기록하지_않는다()
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var action = new StaticSelectAction { ActionId = ActionIds.Meal(Today, 0), SelectedOption = null };
        await handler.Handle(Request(action));

        Assert.False(await fixture.Db.MealRecords.AnyAsync(m => m.ChannelId == Channel && m.Date == Today, ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
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
}
