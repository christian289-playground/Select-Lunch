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
public class MealActionHandlerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);

    static async Task<(TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, MealActionHandler Handler)>
        SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        var handler = new MealActionHandler(fixture.Db, service, announcer, slack);
        return (fixture, service, slack, handler);
    }

    static BlockActionRequest Request(BlockAction action) => new()
    {
        TriggerId = "T1",
        User = new User { Id = "U1" },
        Actions = [action],
    };

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
    public async Task 존재하지_않는_식당ID여도_예외없이_처리된다()
    {
        var (fixture, _, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        // Restaurants.RestaurantId는 MealRecords에 FK(Restrict)가 걸려 있어, 정상
        // 흐름에서는 존재하지 않는 restaurantId로 기록 자체가 불가능하다(SQLite가
        // FOREIGN KEY constraint failed로 막는다). 위조되거나 경합으로 사라진
        // restaurantId가 "기록은 됐지만 이름 조회가 실패"하는 시나리오를 재현하려면
        // 이 제약을 일부러 끈다 — 핸들러의 방어 코드(이름 없으면 조용히 건너뜀)가
        // 실제로 예외를 삼키는지만 검증하면 된다.
        await fixture.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;", ct);

        var exception = await Record.ExceptionAsync(() =>
            handler.Handle(Request(new ButtonAction { ActionId = ActionIds.Meal(Today, 999) })));

        Assert.Null(exception);
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
