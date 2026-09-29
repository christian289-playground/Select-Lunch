using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
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
/// RestaurantModalHandler(뷰 제출)의 세 분기 — 이름 검증 실패, 기록 흐름
/// (ModalContext.RecordFor), 일반 등록(Active/Pending) — 를 커버한다.
/// </summary>
public class RestaurantModalHandlerTests
{
    const string Channel = "C1";
    const string InputSuffix = "_input";

    static async Task<(TestDb Fixture, FakeSlackApiClient Slack, RestaurantModalHandler Handler)> SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        var handler = new RestaurantModalHandler(service, announcer);
        return (fixture, slack, handler);
    }

    static ViewState StateWith(params (string BlockId, ElementValue Value)[] values)
    {
        var state = new ViewState { Values = [] };
        foreach (var (blockId, value) in values)
            state.Values[blockId] = new Dictionary<string, ElementValue> { [blockId + InputSuffix] = value };
        return state;
    }

    static ViewState NameAndCategory(string name, long? categoryId) => StateWith(
        (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = name }),
        (RestaurantModal.BlockIds.Category, categoryId is { } id
            ? new StaticSelectValue { SelectedOption = new Option { Value = id.ToString() } }
            : new StaticSelectValue { SelectedOption = null }));

    static ViewSubmission Submission(string? privateMetadata, ViewState state) => new()
    {
        User = new User { Id = "U1" },
        View = new ModalViewInfo { PrivateMetadata = privateMetadata ?? "", State = state },
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 이름이_비어있으면_에러를_반환하고_아무것도_저장하지_않는다(string name)
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(null, NameAndCategory(name, 1)));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.Equal("이름을 입력해 주세요.", errors.Errors[RestaurantModal.BlockIds.Name]);
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task RecordFor_컨텍스트면_등록과_동시에_그날_식사로_기록되고_기록_문구를_보낸다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(2026, 9, 18);

        var response = await handler.Handle(
            Submission(ModalContext.ForRecord(date).Serialize(), NameAndCategory("스시로", 3)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Null(response.ResponseAction);

        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "스시로", ct);
        var record = await fixture.Db.MealRecords.SingleAsync(m => m.ChannelId == Channel && m.Date == date, ct);
        Assert.Equal(restaurant.Id, record.RestaurantId);
        Assert.Equal(MealSource.NewRegistration, record.Source);

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        var text = slack.ChatFake.PostedMessage!.Text;
        Assert.Contains("스시로", text);
        Assert.Contains("등록하고 오늘 점심으로 기록했습니다", text);
    }

    [Fact]
    public async Task 컨텍스트없이_카테고리를_채워_등록하면_기록없이_Active를_알린다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(null, NameAndCategory("국밥집", 1)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Null(response.ResponseAction);

        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "국밥집", ct);
        Assert.Equal(RestaurantStatus.Active, restaurant.Status);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        var text = slack.ChatFake.PostedMessage!.Text;
        Assert.Contains("국밥집", text);
        Assert.Contains("추천 대상에 포함됩니다", text);
    }

    [Fact]
    public async Task 카테고리를_둘_다_비우면_카테고리_블록에_오류를_돌려주고_저장하지_않는다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(null, NameAndCategory("무명식당", null)));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Category));
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 카테고리를_둘_다_채우면_직접입력_블록에_오류를_돌려주고_저장하지_않는다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var state = NameAndCategory("국밥집", 1);
        state.Values[RestaurantModal.BlockIds.CategoryNew] = new Dictionary<string, ElementValue>
        {
            [RestaurantModal.BlockIds.CategoryNew + InputSuffix] = new PlainTextInputValue { Value = "순대국" },
        };

        var response = await handler.Handle(Submission(null, state));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.CategoryNew));
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.False(await fixture.Db.Categories.AnyAsync(c => c.Name == "순대국", ct));
    }

    [Fact]
    public async Task 새_카테고리를_입력하면_카테고리가_만들어지고_등록자_이름이_안내에_실린다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "옛날순대" }),
            (RestaurantModal.BlockIds.CategoryNew, new PlainTextInputValue { Value = "순대국" }),
            (RestaurantModal.BlockIds.Address, new PlainTextInputValue { Value = "수원시" }),
            (RestaurantModal.BlockIds.WaitLevel, new StaticSelectValue { SelectedOption = new Option { Value = "1" } }));
        var submission = Submission(null, state);
        submission.User = new User { Id = "U1", Name = "이상준" };

        var response = await handler.Handle(submission);

        Assert.IsNotType<ViewErrorsResponse>(response);
        var restaurant = await fixture.Db.Restaurants.Include(r => r.Category)
            .SingleAsync(r => r.Name == "옛날순대", ct);
        Assert.Equal(RestaurantStatus.Active, restaurant.Status);
        Assert.Equal("순대국", restaurant.Category!.Name);
        Assert.False(restaurant.Category.IsBuiltIn);
        Assert.Equal("수원시", restaurant.Address);
        Assert.Equal(WaitLevel.Slight, restaurant.WaitLevel);
        Assert.Equal("이상준", restaurant.CreatedByDisplayName);
        Assert.Contains("이상준", slack.ChatFake.PostedMessage!.Text);
    }

    // --- 카테고리 규칙 표: 흐름 x (둘 다 비움 / 하나 / 둘 다 채움) ---

    static readonly DateOnly RecordDate = new(2026, 9, 18);

    static ViewState WithNewCategory(ViewState state, string name)
    {
        state.Values[RestaurantModal.BlockIds.CategoryNew] = new Dictionary<string, ElementValue>
        {
            [RestaurantModal.BlockIds.CategoryNew + InputSuffix] = new PlainTextInputValue { Value = name },
        };
        return state;
    }

    [Fact]
    public async Task 기록_흐름에서_카테고리를_둘_다_비우면_Pending으로_등록되고_식사가_기록된다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(
            ModalContext.ForRecord(RecordDate).Serialize(), NameAndCategory("무명식당", null)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "무명식당", ct);
        Assert.Equal(RestaurantStatus.Pending, restaurant.Status);
        Assert.Null(restaurant.CategoryId);
        var record = await fixture.Db.MealRecords.SingleAsync(m => m.Date == RecordDate, ct);
        Assert.Equal(restaurant.Id, record.RestaurantId);
        Assert.Equal(MealSource.NewRegistration, record.Source);
        var text = slack.ChatFake.PostedMessage!.Text;
        Assert.Contains("오늘 점심으로 기록했습니다", text);
        Assert.Contains("추천에서는 제외", text);
    }

    [Fact]
    public async Task 기록_흐름에서_새_카테고리만_입력해도_Active로_등록되고_식사가_기록된다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(
            ModalContext.ForRecord(RecordDate).Serialize(), WithNewCategory(NameAndCategory("옛날순대", null), "순대국")));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal(RestaurantStatus.Active, (await fixture.Db.Restaurants.SingleAsync(r => r.Name == "옛날순대", ct)).Status);
        Assert.True(await fixture.Db.MealRecords.AnyAsync(m => m.Date == RecordDate, ct));
        Assert.DoesNotContain("추천에서는 제외", slack.ChatFake.PostedMessage!.Text);
    }

    [Fact]
    public async Task 기록_흐름에서도_둘_다_채우면_오류이고_식사도_기록되지_않는다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(
            ModalContext.ForRecord(RecordDate).Serialize(), WithNewCategory(NameAndCategory("국밥집", 1), "순대국")));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.CategoryNew));
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
    }

    [Fact]
    public async Task 수정_흐름에서는_카테고리를_둘_다_비우면_오류다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pending = await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, "이름만아는집", null, null, null, null), "U1", ct);

        var response = await handler.Handle(Submission(
            ModalContext.ForEdit(pending.Id).Serialize(), NameAndCategory("이름만아는집", null)));

        Assert.IsType<ViewErrorsResponse>(response);
        Assert.Equal(RestaurantStatus.Pending, (await fixture.Db.Restaurants.SingleAsync(ct)).Status);
    }

    // --- Pending 기능이 기록 흐름의 Pending 식당에 대해 실제로 동작하는지 ---

    [Fact]
    public async Task 기록_흐름의_Pending_식당은_pending_조회_금요일_알림_보완_모달에_모두_잡힌다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await handler.Handle(Submission(
            ModalContext.ForRecord(RecordDate).Serialize(), NameAndCategory("무명식당", null)));
        var restaurant = await fixture.Db.Restaurants.SingleAsync(ct);

        // /lunch pending 이 쓰는 조회와 같은 조건
        Assert.Equal(1, await fixture.Db.Restaurants.CountAsync(r => r.Status == RestaurantStatus.Pending, ct));

        // 금요일 알림 스케줄러의 입력
        var state = await fixture.Db.GetTodayStateAsync(Channel, RecordDate, ct);
        Assert.True(state.HasPendingRestaurants);

        // 알림 메시지: 식당 버튼이 실린다
        var announcer = new LunchAnnouncer(slack, fixture.Db, new LunchService(fixture.Db, Channel), Channel);
        await announcer.PostPendingReminderAsync(ct);
        var buttons = slack.ChatFake.PostedMessage!.Blocks.OfType<ActionsBlock>()
            .SelectMany(a => a.Elements.OfType<SlackNet.Blocks.Button>()).ToList();
        Assert.Contains(buttons, b => b.ActionId == ActionIds.RestaurantFill(restaurant.Id));

        // 버튼 -> 보완 모달
        var pendingHandler = new PendingActionHandler(fixture.Db, slack);
        await pendingHandler.Handle(new BlockActionRequest
        {
            TriggerId = "T1",
            User = new User { Id = "U1" },
            Actions = [new ButtonAction { ActionId = ActionIds.RestaurantFill(restaurant.Id) }],
        });
        Assert.Equal(1, slack.ViewsFake.OpenCallCount);
        Assert.Equal(ModalContext.ForEdit(restaurant.Id).Serialize(), ((ModalViewDefinition)slack.ViewsFake.LastView!).PrivateMetadata);
    }
}
