using Microsoft.EntityFrameworkCore;
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
    public async Task 컨텍스트없이_카테고리없이_등록하면_기록없이_Pending을_알린다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(Submission(null, NameAndCategory("무명식당", null)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Null(response.ResponseAction);

        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "무명식당", ct);
        Assert.Equal(RestaurantStatus.Pending, restaurant.Status);
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        var text = slack.ChatFake.PostedMessage!.Text;
        Assert.Contains("무명식당", text);
        Assert.Contains("카테고리가 비어 있어 추천에서 제외됩니다", text);
    }
}
