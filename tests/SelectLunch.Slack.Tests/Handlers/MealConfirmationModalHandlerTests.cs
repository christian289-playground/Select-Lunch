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
using MealRecord = SelectLunch.Shared.Entities.MealRecord;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 확정 창의 제출만 식사 기록을 쓴다. 날짜·채널·사용자 메타데이터와 선택을 서버가 다시 검증하고,
/// 선택 목록 전체를 원자적으로 저장하며, 성공했을 때만 채널에 알린다.
/// </summary>
public class MealConfirmationModalHandlerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);

    sealed record Harness(
        TestDb Fixture,
        LunchService Service,
        FakeSlackApiClient Slack,
        EarlyDepartureNoticeTracker Notices,
        TestViewRequest Handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    static async Task<Harness> SetupAsync(LunchOptions? options = null)
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);
        var configuredOptions = options ?? new LunchOptions();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 18, 3, 0, 0, TimeSpan.Zero));
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => configuredOptions)));
        var meals = new MealConfirmationService(fixture.Db, service, Channel, monitor, clock);
        var notices = new EarlyDepartureNoticeTracker();
        var publisher = new MealConfirmationPublisher(service, announcer, notices, NullLogger<MealConfirmationPublisher>.Instance);
        return new Harness(fixture, service, slack, notices, new TestViewRequest(new MealConfirmationModalHandler(meals, publisher)));
    }

    static ViewSubmission Submission(string? metadata, IEnumerable<string> optionValues, string userId = "U1")
    {
        var selection = new ExternalMultiSelectValue
        {
            SelectedOptions = [.. optionValues.Select(value => new Option { Value = value })],
        };
        var state = new ViewState { Values = [] };
        state.Values[MealConfirmationModal.PlacesBlockId] = new Dictionary<string, ElementValue>
        {
            [MealConfirmationModal.PlacesActionId] = selection,
        };

        return new ViewSubmission
        {
            User = new User { Id = userId },
            View = new ModalViewInfo { CallbackId = MealConfirmationModal.CallbackId, PrivateMetadata = metadata ?? "", State = state },
        };
    }

    static ViewSubmission Submit(DateOnly date, params long[] ids) =>
        Submission(MealConfirmationModal.Metadata(date, Channel, "U1"), ids.Select(id => id.ToString()));

    static async Task<Restaurant> SeedAsync(LunchService service, string name, long categoryId = 1) =>
        await service.SaveRestaurantAsync(
            new(null, name, categoryId, null, null, null), "U0", TestContext.Current.CancellationToken);

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

    static async Task<long[]> RecordedIdsAsync(TestDb fixture, DateOnly date)
    {
        fixture.Db.ChangeTracker.Clear();
        return await fixture.Db.MealRecords
            .Where(m => m.ChannelId == Channel && m.Date == date)
            .OrderBy(m => m.Id)
            .Select(m => m.RestaurantId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    static string RejectionOf(ViewSubmissionResponse response)
    {
        var errors = Assert.IsType<ViewErrorsResponse>(response);
        var error = Assert.Single(errors.Errors);
        Assert.Equal(MealConfirmationModal.PlacesBlockId, error.Key);
        return error.Value;
    }

    static IEnumerable<SectionBlock> PlainSections(FakeSlackApiClient slack) =>
        slack.ChatFake.PostedMessage!.Blocks.OfType<SectionBlock>().Where(b => b.Text is PlainText);

    [Fact]
    public async Task 저장하면_고른_식당_전체를_한_번에_기록하고_전체를_공지한다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAsync(h.Service, "첫식당");
        var second = await SeedAsync(h.Service, "둘째식당", 3);
        await SeedClosedPollAsync(h.Fixture, Today, voters: 6, first.Id);

        var response = await h.Handler.Handle(Submit(Today, first.Id, second.Id),
            () => Assert.Equal(0, h.Slack.ChatFake.PostCallCount));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal([first.Id, second.Id], await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(1, h.Slack.ChatFake.PostCallCount);
        var posted = h.Slack.ChatFake.PostedMessage!;
        Assert.Equal(Channel, posted.Channel);
        var names = string.Join("\n", PlainSections(h.Slack).Select(b => b.Text.Text));
        Assert.Contains("첫식당", names);
        Assert.Contains("둘째식당", names);
        Assert.True(h.Notices.HasAttempted(Today, 0, "U1"));
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Equal(2, await h.Fixture.Db.MealRecords.CountAsync(ct));
    }

    [Fact]
    public async Task 이름의_슬랙_마크업은_평문으로만_표시하고_알림용_본문에는_넣지_않는다()
    {
        await using var h = await SetupAsync();
        const string hostile = "<!channel> *굵게* <@U2> `코드`";
        var restaurant = await SeedAsync(h.Service, hostile);

        await h.Handler.Handle(Submit(Today, restaurant.Id));

        var posted = h.Slack.ChatFake.PostedMessage!;
        Assert.DoesNotContain(hostile, posted.Text);
        var holders = posted.Blocks.OfType<SectionBlock>().Where(b => b.Text.Text.Contains(hostile)).ToList();
        Assert.NotEmpty(holders);
        Assert.All(holders, b => Assert.IsType<PlainText>(b.Text));
        Assert.DoesNotContain(posted.Blocks.OfType<SectionBlock>(), b => b.Text is Markdown && b.Text.Text.Contains(hostile));
    }

    [Fact]
    public async Task 당일_정정은_기록된_전체_목록을_새_선택으로_교체한다()
    {
        await using var h = await SetupAsync();
        var first = await SeedAsync(h.Service, "첫식당");
        var second = await SeedAsync(h.Service, "둘째식당", 3);
        await SeedClosedPollAsync(h.Fixture, Today, voters: 6, first.Id);
        await SeedMealAsync(h.Fixture, Today, first.Id, "U9");
        await SeedMealAsync(h.Fixture, Today, second.Id, "U9");

        var response = await h.Handler.Handle(Submit(Today, second.Id));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal([second.Id], await RecordedIdsAsync(h.Fixture, Today));
        var names = string.Join("\n", PlainSections(h.Slack).Select(b => b.Text.Text));
        Assert.Contains("둘째식당", names);
        Assert.DoesNotContain("첫식당", names);
    }

    [Fact]
    public async Task 한도를_넘는_선택은_일부도_저장하지_않고_창에_오류를_돌려준다()
    {
        await using var h = await SetupAsync();
        var first = await SeedAsync(h.Service, "첫식당");
        var second = await SeedAsync(h.Service, "둘째식당", 3);

        var response = await h.Handler.Handle(Submit(Today, first.Id, second.Id));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Empty(await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.False(h.Notices.HasAttempted(Today, 0, "U1"));
    }

    [Fact]
    public async Task 한도를_낮춘_뒤_기존_기록_그대로_저장하려_해도_줄이기_전에는_거절하고_기록을_보존한다()
    {
        await using var h = await SetupAsync();
        var first = await SeedAsync(h.Service, "첫식당");
        var second = await SeedAsync(h.Service, "둘째식당", 3);
        await SeedMealAsync(h.Fixture, Today, first.Id, "U9");
        await SeedMealAsync(h.Fixture, Today, second.Id, "U9");

        var response = await h.Handler.Handle(Submit(Today, first.Id, second.Id));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Equal([first.Id, second.Id], await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
    }

    [Theory]
    [InlineData("other-user")]
    [InlineData("other-channel")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("date-only")]
    public async Task 메타데이터가_이_사용자와_채널의_창이_아니면_저장하지_않는다(string kind)
    {
        await using var h = await SetupAsync();
        var restaurant = await SeedAsync(h.Service, "검증식당");
        var metadata = kind switch
        {
            "other-user" => MealConfirmationModal.Metadata(Today, Channel, "U2"),
            "other-channel" => MealConfirmationModal.Metadata(Today, "C-OTHER", "U1"),
            "malformed" => "meal:not-a-date:C1:U1",
            "empty" => "",
            _ => $"date:{Today:yyyyMMdd}",
        };

        var response = await h.Handler.Handle(Submission(metadata, [restaurant.Id.ToString()]));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Empty(await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.False(h.Notices.HasAttempted(Today, 0, "U1"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("not-a-number")]
    [InlineData("zero")]
    [InlineData("duplicate")]
    [InlineData("missing-restaurant")]
    public async Task 비었거나_잘못된_선택은_저장하지_않는다(string kind)
    {
        await using var h = await SetupAsync();
        var restaurant = await SeedAsync(h.Service, "검증식당");
        await SeedClosedPollAsync(h.Fixture, Today, voters: 6, restaurant.Id);
        var values = kind switch
        {
            "empty" => [],
            "not-a-number" => new[] { "abc" },
            "zero" => ["0"],
            "duplicate" => [restaurant.Id.ToString(), restaurant.Id.ToString()],
            _ => [restaurant.Id.ToString(), "9999"],
        };

        var response = await h.Handler.Handle(
            Submission(MealConfirmationModal.Metadata(Today, Channel, "U1"), values));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Empty(await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.False(h.Notices.HasAttempted(Today, 0, "U1"));
    }

    [Fact]
    public async Task 활성이_아닌_식당은_새로_고를_수_없다()
    {
        await using var h = await SetupAsync();
        var archived = await SeedAsync(h.Service, "보관식당");
        archived.Status = RestaurantStatus.Archived;
        await h.Fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var response = await h.Handler.Handle(Submit(Today, archived.Id));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Empty(await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 지정된_지난_회차의_최초_확정은_저장하되_오늘_미리출발_안내는_건드리지_않는다()
    {
        var past = Today.AddDays(-1);
        await using var h = await SetupAsync(new LunchOptions { CarryoverMealPromptDate = past });
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAsync(h.Service, "첫식당");
        var second = await SeedAsync(h.Service, "둘째식당", 3);
        await h.Service.MarkMealPromptPostedAsync(past, DateTimeOffset.UnixEpoch, ct);
        await SeedClosedPollAsync(h.Fixture, past, voters: 7, first.Id);

        var response = await h.Handler.Handle(Submit(past, first.Id, second.Id));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal([first.Id, second.Id], await RecordedIdsAsync(h.Fixture, past));
        Assert.Equal(1, h.Slack.ChatFake.PostCallCount);
        Assert.False(h.Notices.HasAttempted(past, 0, "U1"));
        Assert.False(h.Notices.HasAttempted(Today, 0, "U1"));
    }

    [Fact]
    public async Task 지난_회차는_첫_저장_뒤_다시_제출하면_거절하고_기록과_공지를_바꾸지_않는다()
    {
        var past = Today.AddDays(-1);
        await using var h = await SetupAsync(new LunchOptions { CarryoverMealPromptDate = past });
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAsync(h.Service, "첫식당");
        var second = await SeedAsync(h.Service, "둘째식당", 3);
        await h.Service.MarkMealPromptPostedAsync(past, DateTimeOffset.UnixEpoch, ct);
        await SeedClosedPollAsync(h.Fixture, past, voters: 7, first.Id);
        Assert.IsNotType<ViewErrorsResponse>(await h.Handler.Handle(Submit(past, first.Id)));

        // 같은 창을 열어 둔 다른 사람이 나중에 제출한다.
        var response = await h.Handler.Handle(Submit(past, second.Id));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Equal([first.Id], await RecordedIdsAsync(h.Fixture, past));
        Assert.Equal(1, h.Slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 기록할_수_없는_날짜의_제출은_거절하고_미리출발_안내도_종료하지_않는다()
    {
        await using var h = await SetupAsync();
        var restaurant = await SeedAsync(h.Service, "검증식당");
        var past = Today.AddDays(-1);

        var response = await h.Handler.Handle(Submit(past, restaurant.Id));

        Assert.NotEmpty(RejectionOf(response));
        Assert.Empty(await RecordedIdsAsync(h.Fixture, past));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.False(h.Notices.HasAttempted(past, 0, "U1"));
        Assert.False(h.Notices.HasAttempted(Today, 0, "U1"));
    }

    [Fact]
    public async Task 창을_닫으면_기록도_공지도_미리출발_종료도_없다()
    {
        await using var h = await SetupAsync();
        await SeedAsync(h.Service, "닫기식당");

        await h.Handler.HandleClose(new ViewClosed());

        Assert.Empty(await RecordedIdsAsync(h.Fixture, Today));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.False(h.Notices.HasAttempted(Today, 0, "U1"));
    }
}
