using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
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
/// RestaurantModalHandler(뷰 제출)의 분기 — 일반 등록·수정, 식사 기록용 등록(<see cref="RestaurantModal.MealCallbackId"/>),
/// 배포 전에 열린 구형 날짜 전용 창 — 를 커버한다.
/// </summary>
public class RestaurantModalHandlerTests
{
    const string Channel = "C1";
    const string InputSuffix = "_input";

    sealed record Harness(
        TestDb Fixture, FakeSlackApiClient Slack, TestViewRequest Handler,
        MealConfirmationService Meals, EarlyDepartureNoticeTracker Notices);

    static async Task<Harness> CreateAsync(
        TestTimeProvider? clock = null, EarlyDepartureNoticeTracker? notices = null, LunchOptions? options = null)
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);
        clock ??= new TestTimeProvider(new DateTimeOffset(2026, 9, 18, 3, 0, 0, TimeSpan.Zero));
        var settings = options ?? new LunchOptions();
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => settings)));
        var meals = new MealConfirmationService(fixture.Db, service, Channel, monitor, clock);
        notices ??= new EarlyDepartureNoticeTracker();
        var publisher = new MealConfirmationPublisher(service, announcer, notices, NullLogger<MealConfirmationPublisher>.Instance);
        var handler = new RestaurantModalHandler(service, meals, announcer, publisher);
        return new Harness(fixture, slack, new TestViewRequest(handler), meals, notices);
    }

    static async Task<(TestDb Fixture, FakeSlackApiClient Slack, TestViewRequest Handler)> SetupAsync(
        TestTimeProvider? clock = null, EarlyDepartureNoticeTracker? notices = null)
    {
        var harness = await CreateAsync(clock, notices);
        return (harness.Fixture, harness.Slack, harness.Handler);
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

    static ViewSubmission SubmissionOf(ModalViewDefinition view, ViewState state, string userId = "U1") => new()
    {
        User = new User { Id = userId },
        View = new ModalViewInfo { CallbackId = view.CallbackId, PrivateMetadata = view.PrivateMetadata, State = state },
    };

    /// <summary>식사 기록용 등록 창(<see cref="RestaurantModal.BuildForMeal"/>)이 만든 것과 같은 callback·메타데이터의 제출.</summary>
    static ViewSubmission MealSubmission(DateOnly date, ViewState state, string userId = "U1", string channel = Channel) => new()
    {
        User = new User { Id = userId },
        View = new ModalViewInfo
        {
            CallbackId = RestaurantModal.MealCallbackId,
            PrivateMetadata = MealConfirmationModal.Metadata(date, channel, userId),
            State = state,
        },
    };

    /// <summary>배포 전에 열린 구형 창: 일반 callback에 날짜만 실린 메타데이터.</summary>
    static ViewSubmission LegacyMealSubmission(DateOnly date, ViewState state)
    {
        var submission = Submission($"date:{date:yyyyMMdd}", state);
        submission.View.CallbackId = RestaurantModal.CallbackId;
        return submission;
    }

    static ViewState WithPlaces(ViewState state, params long[] restaurantIds) =>
        WithPlaceOptions(state, [.. restaurantIds.Select(id => id.ToString())]);

    static ViewState WithPlaceOptions(ViewState state, params string[] values)
    {
        state.Values[MealConfirmationModal.PlacesBlockId] = new Dictionary<string, ElementValue>
        {
            [MealConfirmationModal.PlacesActionId] = new ExternalMultiSelectValue
            {
                SelectedOptions = [.. values.Select(value => new Option { Value = value })],
            },
        };
        return state;
    }

    /// <summary>화면에 그려지는 섹션 글자. 식당 이름은 대체 텍스트가 아니라 여기에 있어야 한다.</summary>
    static string Shown(SlackNet.WebApi.Message message) =>
        string.Join("\n", message.Blocks.OfType<SectionBlock>().Select(section => section.Text.Text));

    static async Task<Restaurant> SeedAsync(TestDb fixture, string name, long? categoryId, CancellationToken ct) =>
        await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, name, categoryId, null, null, null), "U0", ct);

    static async Task SeedMealAsync(TestDb fixture, DateOnly date, long restaurantId, CancellationToken ct)
    {
        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = Channel, Date = date, RestaurantId = restaurantId,
            RecordedBySlackUserId = "ORIGINAL", RecordedAt = new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero),
            Source = MealSource.Prompt,
        });
        await fixture.Db.SaveChangesAsync(ct);
    }

    /// <summary>마감된 본투표에서 식당을 고른 사람이 <paramref name="voters"/>명인 날을 만든다(확정 한도 계산의 입력).</summary>
    static async Task SeedClosedVotesAsync(
        TestDb fixture, DateOnly date, long restaurantId, int voters, CancellationToken ct)
    {
        var poll = new LunchPoll { ChannelId = Channel, Date = date, Status = PollStatus.Closed };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync(ct);
        for (var i = 0; i < voters; i++)
        {
            fixture.Db.PollVotes.Add(new PollVote
            {
                PollId = poll.Id, SlackUserId = $"V{i}", RestaurantId = restaurantId, VotedAt = DateTimeOffset.UnixEpoch,
            });
        }
        await fixture.Db.SaveChangesAsync(ct);
        fixture.Db.ChangeTracker.Clear();
    }

    /// <summary>게시까지 끝난 열린 투표. 후보를 얹거나 메시지를 갱신하면 호출 횟수·후보 목록에 드러난다.</summary>
    static async Task SeedOpenPollAsync(Harness harness, DateOnly date, CancellationToken ct)
    {
        var service = new LunchService(harness.Fixture.Db, Channel);
        var announcer = new LunchAnnouncer(
            harness.Slack, harness.Fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);
        var poll = await service.OpenPollAsync(
            date,
            new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 18, 3, 0, 0, TimeSpan.Zero), ct);
        await announcer.PostPollAsync(poll.Id, poll.ClosesAt, ct);
    }

    /// <summary>식당·카테고리·식사 기록·투표 후보 전체의 문자열 스냅샷. 같으면 어떤 행도 바뀌지 않은 것이다.</summary>
    static async Task<string> StateOfAsync(TestDb fixture, CancellationToken ct)
    {
        var restaurants = (await fixture.Db.Restaurants.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct))
            .Select(r => $"{r.Id}|{r.Name}|{r.CategoryId}|{r.Status}|{r.Address}|{r.UpdatedAt.UtcTicks}");
        var categories = (await fixture.Db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct))
            .Select(c => $"{c.Id}|{c.Name}");
        var meals = (await fixture.Db.MealRecords.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct))
            .Select(m => $"{m.Id}|{m.Date:yyyyMMdd}|{m.RestaurantId}|{m.RecordedBySlackUserId}|{m.RecordedAt.UtcTicks}|{m.Source}");
        var candidates = (await fixture.Db.PollCandidates.AsNoTracking()
                .OrderBy(c => c.PollId).ThenBy(c => c.RestaurantId).ToListAsync(ct))
            .Select(c => $"{c.PollId}|{c.RestaurantId}|{c.DisplayOrder}");
        return string.Join(
            "\n", restaurants.Concat(["--"]).Concat(categories).Concat(["--"]).Concat(meals).Concat(["--"]).Concat(candidates));
    }

    [Fact]
    public async Task 새_식당으로_식사_기록을_완료하면_그날_미리출발_알림도_종료한다()
    {
        var date = new DateOnly(2026, 9, 18);
        var notices = new EarlyDepartureNoticeTracker();
        var (fixture, _, handler) = await SetupAsync(notices: notices);
        await using var disposal = fixture;
        Assert.True(notices.TryClaim(date, 123, "U1"));

        await handler.Handle(MealSubmission(date, NameAndCategory("식사완료식당", 1)));

        Assert.False(notices.TryClaim(date, 456, "U2"));
        Assert.True(notices.TryClaim(date.AddDays(1), 123, "U1"));
    }

    [Fact]
    public async Task 잘못된_식당_입력은_미리출발_알림을_종료하지_않는다()
    {
        var date = new DateOnly(2026, 9, 18);
        var notices = new EarlyDepartureNoticeTracker();
        var (fixture, _, handler) = await SetupAsync(notices: notices);
        await using var disposal = fixture;
        Assert.True(notices.TryClaim(date, 123, "U1"));

        await handler.Handle(MealSubmission(date, NameAndCategory("", 1)));

        Assert.False(notices.TryClaim(date, 123, "U1"));
        Assert.True(notices.TryClaim(date, 456, "U2"));
    }

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
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 식사_기록용_등록은_새_식당을_그날_식사로_기록하고_이름은_블록으로만_안내한다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(2026, 9, 18);

        var response = await handler.Handle(MealSubmission(date, NameAndCategory("스시로", 3)),
            () => Assert.Equal(0, slack.ChatFake.PostCallCount));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Null(response.ResponseAction);

        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "스시로", ct);
        var record = await fixture.Db.MealRecords.SingleAsync(m => m.ChannelId == Channel && m.Date == date, ct);
        Assert.Equal(restaurant.Id, record.RestaurantId);
        Assert.Equal(MealSource.NewRegistration, record.Source);

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        var posted = slack.ChatFake.PostedMessage!;
        Assert.Contains("스시로", Shown(posted));
        Assert.DoesNotContain("스시로", posted.Text);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(null, false)]
    public async Task 투표가_열려_있으면_새_식당은_후보에_들어가고_메시지가_갱신된다_Pending은_제외(
        int? categoryId, bool expectedCandidate)
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, Channel);
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);
        await service.SaveRestaurantAsync(new(null, "기존식당", 1, null, null, null), "U0", ct);
        var poll = await service.OpenPollAsync(
            new DateOnly(2026, 9, 18),
            new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 18, 3, 0, 0, TimeSpan.Zero), ct);
        await announcer.PostPollAsync(poll.Id, poll.ClosesAt, ct);

        await handler.Handle(MealSubmission(new DateOnly(2026, 9, 18), NameAndCategory("뒤늦게등록", categoryId)));

        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "뒤늦게등록", ct);
        var isCandidate = await fixture.Db.PollCandidates
            .AnyAsync(c => c.PollId == poll.Id && c.RestaurantId == restaurant.Id, ct);
        Assert.Equal(expectedCandidate, isCandidate);
        Assert.Equal(expectedCandidate ? 1 : 0, slack.ChatFake.UpdateCallCount);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(1)]
    public async Task 다른_날짜의_기록_모달은_기존_기록과_식당_카테고리를_바꾸지_않는다(int dayOffset)
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero));
        var notices = new EarlyDepartureNoticeTracker();
        var (fixture, slack, handler) = await SetupAsync(clock, notices);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var original = await SeedAsync(fixture, "기존순대국", 1, ct);
        await SeedMealAsync(fixture, new DateOnly(2026, 10, 2), original.Id, ct);
        var before = await StateOfAsync(fixture, ct);
        var requestedDate = new DateOnly(2026, 10, 6).AddDays(dayOffset);
        var state = WithNewCategory(NameAndCategory("저장되면안되는새식당", null), "저장되면안되는새분류");

        var response = await handler.Handle(MealSubmission(requestedDate, state));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(fixture, ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
        Assert.Equal(0, slack.ChatFake.UpdateCallCount);
        Assert.False(notices.HasAttempted(requestedDate, 1, "U1"));
    }

    [Fact]
    public async Task 자정_전에_열린_모달은_자정_뒤_제출하면_저장하지_않는다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 6, 14, 59, 50, TimeSpan.Zero));
        var (fixture, slack, handler) = await SetupAsync(clock);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var modal = RestaurantModal.BuildForMeal(
            [], new DateOnly(2026, 10, 6), Channel, "U1", new MealSelectionState(true, true, 1, []));

        clock.UtcNow = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);
        var response = await handler.Handle(SubmissionOf(modal, NameAndCategory("자정넘긴식당", 1)));

        Assert.IsType<ViewErrorsResponse>(response);
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);

        await handler.Handle(Submission(ModalContext.None.Serialize(), NameAndCategory("일반등록식당", 1)));
        Assert.True(await fixture.Db.Restaurants.AnyAsync(r => r.Name == "일반등록식당", ct));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
    }

    [Fact]
    public async Task 한국_날짜의_오늘_기록_모달은_UTC가_전날이어도_저장된다()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero));
        var (fixture, _, handler) = await SetupAsync(clock);
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(2026, 10, 6);

        var response = await handler.Handle(MealSubmission(date, NameAndCategory("한국날짜식당", 1)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var record = await fixture.Db.MealRecords.SingleAsync(ct);
        Assert.Equal(date, record.Date);
        Assert.Equal("한국날짜식당", (await fixture.Db.Restaurants.SingleAsync(r => r.Id == record.RestaurantId, ct)).Name);
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

        var response = await handler.Handle(MealSubmission(RecordDate, NameAndCategory("무명식당", null)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var restaurant = await fixture.Db.Restaurants.SingleAsync(r => r.Name == "무명식당", ct);
        Assert.Equal(RestaurantStatus.Pending, restaurant.Status);
        Assert.Null(restaurant.CategoryId);
        var record = await fixture.Db.MealRecords.SingleAsync(m => m.Date == RecordDate, ct);
        Assert.Equal(restaurant.Id, record.RestaurantId);
        Assert.Equal(MealSource.NewRegistration, record.Source);
        var posted = slack.ChatFake.PostedMessage!;
        Assert.Contains("무명식당", Shown(posted));
        Assert.Contains("추천에서는 제외", Shown(posted));
        Assert.DoesNotContain("무명식당", posted.Text);
    }

    [Fact]
    public async Task 기록_흐름에서_새_카테고리만_입력해도_Active로_등록되고_식사가_기록된다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(MealSubmission(
            RecordDate, WithNewCategory(NameAndCategory("옛날순대", null), "순대국")));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal(RestaurantStatus.Active, (await fixture.Db.Restaurants.SingleAsync(r => r.Name == "옛날순대", ct)).Status);
        Assert.True(await fixture.Db.MealRecords.AnyAsync(m => m.Date == RecordDate, ct));
        Assert.DoesNotContain("추천에서는 제외", Shown(slack.ChatFake.PostedMessage!));
    }

    [Fact]
    public async Task 기록_흐름에서도_둘_다_채우면_오류이고_식사도_기록되지_않는다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(MealSubmission(
            RecordDate, WithNewCategory(NameAndCategory("국밥집", 1), "순대국")));

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
        await handler.Handle(MealSubmission(RecordDate, NameAndCategory("무명식당", null)));
        var restaurant = await fixture.Db.Restaurants.SingleAsync(ct);

        // /lunchpending 이 쓰는 조회와 같은 조건
        Assert.Equal(1, await fixture.Db.Restaurants.CountAsync(r => r.Status == RestaurantStatus.Pending, ct));

        // 금요일 알림 스케줄러의 입력
        var state = await fixture.Db.GetTodayStateAsync(Channel, RecordDate, ct);
        Assert.True(state.HasPendingRestaurants);

        // 알림 메시지: 식당 버튼이 실린다
        var announcer = new LunchAnnouncer(slack, fixture.Db, new LunchService(fixture.Db, Channel), Channel, TestPollLeaveRoster.Disabled);
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

    // --- 출발 시각: 등록 / 동명 재등록 / 명시적 수정 / 해제 / 형식 오류 ---

    const string DepartureName = "제주고사리칼국수 시콜";

    static ViewState WithDeparture(ViewState state, string? value)
    {
        state.Values[RestaurantModal.BlockIds.DepartureTime] = new Dictionary<string, ElementValue>
        {
            [RestaurantModal.BlockIds.DepartureTime + InputSuffix] = new PlainTextInputValue { Value = value },
        };
        return state;
    }

    static Task<Restaurant> SeedDepartureAsync(TestDb fixture, TimeOnly departure, CancellationToken ct) =>
        new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, DepartureName, 1, null, null, null, DepartureTime: departure), "U0", ct);

    [Fact]
    public async Task 출발시각을_넣어_등록하면_공백을_뺀_시각으로_저장된다()
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(
            Submission(null, WithDeparture(NameAndCategory(DepartureName, 1), " 11:15 ")));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var stored = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal(new TimeOnly(11, 15), stored.DepartureTime);
    }

    [Fact]
    public async Task 같은_이름으로_다시_등록하면_기존_식당의_출발시각이_바뀐다()
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedDepartureAsync(fixture, new TimeOnly(11, 15), ct);

        var response = await handler.Handle(
            Submission(null, WithDeparture(NameAndCategory(DepartureName, 1), "11:30")));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var stored = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal(seeded.Id, stored.Id);
        Assert.Equal(new TimeOnly(11, 30), stored.DepartureTime);
    }

    [Fact]
    public async Task 수정_흐름에서_시각을_바꾸고_값을_비우면_지정이_해제된다()
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedDepartureAsync(fixture, new TimeOnly(11, 15), ct);
        var edit = ModalContext.ForEdit(seeded.Id).Serialize();

        var changed = await handler.Handle(
            Submission(edit, WithDeparture(NameAndCategory(DepartureName, 1), "11:45")));
        Assert.IsNotType<ViewErrorsResponse>(changed);
        Assert.Equal(new TimeOnly(11, 45), (await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct)).DepartureTime);

        var cleared = await handler.Handle(
            Submission(edit, WithDeparture(NameAndCategory(DepartureName, 1), null)));
        Assert.IsNotType<ViewErrorsResponse>(cleared);
        Assert.Null((await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct)).DepartureTime);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("11:60")]
    [InlineData("abc")]
    public async Task 형식이_틀린_출발시각이면_출발시각_블록에_오류를_돌려주고_수정을_저장하지_않는다(string input)
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedDepartureAsync(fixture, new TimeOnly(11, 15), ct);

        var response = await handler.Handle(Submission(
            ModalContext.ForEdit(seeded.Id).Serialize(),
            WithDeparture(NameAndCategory("이름이바뀌면안됨", 1), input)));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.DepartureTime));
        var stored = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal(DepartureName, stored.Name);
        Assert.Equal(new TimeOnly(11, 15), stored.DepartureTime);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 기록_흐름에서도_출발시각이_틀리면_식당도_식사_기록도_만들지_않는다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        var response = await handler.Handle(MealSubmission(
            RecordDate, WithDeparture(NameAndCategory(DepartureName, 1), "24:00")));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.DepartureTime));
        Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
        Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 보완_모달은_식당에_저장된_출발시각을_채워_연다()
    {
        var (fixture, slack, _) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedDepartureAsync(fixture, new TimeOnly(11, 15), ct);

        await new PendingActionHandler(fixture.Db, slack).Handle(new BlockActionRequest
        {
            TriggerId = "T1",
            User = new User { Id = "U1" },
            Actions = [new ButtonAction { ActionId = ActionIds.RestaurantFill(seeded.Id) }],
        });

        var view = (ModalViewDefinition)slack.ViewsFake.LastView!;
        Assert.Contains(view.Blocks.OfType<InputBlock>(), block =>
            block.BlockId == RestaurantModal.BlockIds.DepartureTime
            && block.Element is PlainTextInput { InitialValue: "11:15" });
    }

    [Fact]
    public async Task Handle_이름으로수정창을열어출발시각만바꾸면_다른정보를보존한다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var seeded = await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, DepartureName, 1, 9, 2, "점심 특선", "수원시",
                WaitLevel.Moderate, DepartureTime: new TimeOnly(11, 15)), "U0", ct);
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
        await new LunchSlashCommandHandler(fixture.Db, slack, monitor, TestPollLeaveRoster.Disabled,
            Microsoft.Extensions.Options.Options.Create(
                new SelectLunch.Slack.Options.SlackOptions { ChannelId = Channel }),
            AdvanceVoteTestSupport.Handler(fixture.Db, slack, monitor, Channel)).Handle(new SlashCommand
        {
            Command = "/lunchedit",
            Text = DepartureName.Replace(" ", ""),
            TriggerId = "T1",
        });
        var view = (ModalViewDefinition)slack.ViewsFake.LastView!;
        var state = new ViewState { Values = [] };
        foreach (var block in view.Blocks.OfType<InputBlock>())
        {
            ElementValue value = block.Element switch
            {
                PlainTextInput input => new PlainTextInputValue { Value = input.InitialValue },
                StaticSelectMenu menu => new StaticSelectValue { SelectedOption = menu.InitialOption },
                _ => throw new InvalidOperationException("지원하지 않는 식당 입력 형식"),
            };
            state.Values[block.BlockId] = new Dictionary<string, ElementValue>
            {
                [block.BlockId + InputSuffix] = value,
            };
        }
        WithDeparture(state, "11:20");

        var response = await handler.Handle(Submission(view.PrivateMetadata, state));

        Assert.IsNotType<ViewErrorsResponse>(response);
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Restaurants.SingleAsync(ct);
        Assert.Equal(seeded.Id, stored.Id);
        Assert.Equal(new TimeOnly(11, 20), stored.DepartureTime);
        Assert.Equal(9, stored.WalkMinutes);
        Assert.Equal(2, stored.PriceLevel);
        Assert.Equal("점심 특선", stored.Note);
        Assert.Equal("수원시", stored.Address);
        Assert.Equal(WaitLevel.Moderate, stored.WaitLevel);
    }
    static ViewState WithEarlyDeparture(ViewState state, string? value)
    {
        state.Values[RestaurantModal.BlockIds.EarlyDeparture] = new Dictionary<string, ElementValue>
        {
            [RestaurantModal.BlockIds.EarlyDeparture + InputSuffix] = new PlainTextInputValue { Value = value },
        };
        return state;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public async Task 미리출발을_비우거나_영으로_수정하면_해제된다(string? input)
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var saved = await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, "식당", 1, null, null, null, EarlyDepartureSeconds: 600), "U1", ct);
        var response = await handler.Handle(Submission(ModalContext.ForEdit(saved.Id).Serialize(),
            WithEarlyDeparture(NameAndCategory("식당", 1), input)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal(0, (await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct)).EarlyDepartureSeconds);
    }

    [Fact]
    public async Task 미리출발_10분은_600초로_저장하고_수정창에서_10분으로_표시한다()
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var response = await handler.Handle(Submission(null,
            WithEarlyDeparture(NameAndCategory("식당", 1), "10")));
        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal(600, (await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct)).EarlyDepartureSeconds);

        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
        await new LunchSlashCommandHandler(fixture.Db, slack, monitor, TestPollLeaveRoster.Disabled,
            Microsoft.Extensions.Options.Options.Create(new SelectLunch.Slack.Options.SlackOptions { ChannelId = Channel }),
            AdvanceVoteTestSupport.Handler(fixture.Db, slack, monitor, Channel))
            .Handle(new SlashCommand { Command = "/lunchedit", Text = "식당", TriggerId = "T1" });
        var view = (ModalViewDefinition)slack.ViewsFake.LastView!;
        var input = (PlainTextInput)view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.EarlyDeparture).Element;
        Assert.Equal("10", input.InitialValue);
    }

    [Fact]
    public async Task 소수_분은_초로_변환하여_저장한다()
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var response = await handler.Handle(Submission(null,
            WithEarlyDeparture(NameAndCategory("식당", 1), "1.5")));
        Assert.IsNotType<ViewErrorsResponse>(response);
        Assert.Equal(90, (await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct)).EarlyDepartureSeconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("35791395")]
    [InlineData("999999999999999999999")]
    [InlineData("0.001")]
    public async Task 잘못된_미리출발은_필드오류를_반환하고_식당을_수정하지_않는다(string input)
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var saved = await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, "식당", 1, null, null, null, EarlyDepartureSeconds: 600), "U1", ct);
        var response = await handler.Handle(Submission(ModalContext.ForEdit(saved.Id).Serialize(),
            WithEarlyDeparture(NameAndCategory("바뀌면 안됨", 1), input)));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.Contains(RestaurantModal.BlockIds.EarlyDeparture, errors.Errors.Keys);
        var stored = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal("식당", stored.Name);
        Assert.Equal(600, stored.EarlyDepartureSeconds);
        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Theory]
    [InlineData(false, 600)]
    [InlineData(true, 600)]
    [InlineData(false, 61)]
    [InlineData(true, 61)]
    public async Task 수정과_보완_모달에서_다른_항목만_바꾸면_미리출발을_보존한다(bool pending, int seconds)
    {
        var (fixture, slack, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var seeded = await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, "식당", 1, null, null, null, EarlyDepartureSeconds: seconds), "U1", ct);
        if (pending)
        {
            await new PendingActionHandler(fixture.Db, slack).Handle(new BlockActionRequest
            {
                TriggerId = "T1", User = new User { Id = "U1" },
                Actions = [new ButtonAction { ActionId = ActionIds.RestaurantFill(seeded.Id) }],
            });
        }
        else
        {
            var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
                ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
            await new LunchSlashCommandHandler(fixture.Db, slack, monitor, TestPollLeaveRoster.Disabled,
                Microsoft.Extensions.Options.Options.Create(new SelectLunch.Slack.Options.SlackOptions { ChannelId = Channel }),
                AdvanceVoteTestSupport.Handler(fixture.Db, slack, monitor, Channel))
                .Handle(new SlashCommand { Command = "/lunchedit", Text = "식당", TriggerId = "T1" });
        }

        var view = (ModalViewDefinition)slack.ViewsFake.LastView!;
        var state = new ViewState { Values = [] };
        foreach (var block in view.Blocks.OfType<InputBlock>())
        {
            ElementValue value = block.Element switch
            {
                PlainTextInput input => new PlainTextInputValue { Value = input.InitialValue },
                StaticSelectMenu menu => new StaticSelectValue { SelectedOption = menu.InitialOption },
                _ => throw new InvalidOperationException(),
            };
            state.Values[block.BlockId] = new Dictionary<string, ElementValue> { [block.BlockId + InputSuffix] = value };
        }
        WithDeparture(state, "11:20");
        var response = await handler.Handle(Submission(view.PrivateMetadata, state));
        Assert.IsNotType<ViewErrorsResponse>(response);
        var stored = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal(seconds, stored.EarlyDepartureSeconds);
        Assert.Equal(new TimeOnly(11, 20), stored.DepartureTime);
    }
    [Fact]
    public async Task 동명_재등록에서_미리출발을_갱신한다()
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var original = await new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new RestaurantDraft(null, "식당", 1, null, null, null, EarlyDepartureSeconds: 600), "U1", ct);
        var response = await handler.Handle(Submission(null,
            WithEarlyDeparture(NameAndCategory("식 당", 1), "1.5")));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var stored = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(90, stored.EarlyDepartureSeconds);
    }

    [Theory]
    [InlineData("10", true)]
    [InlineData("-1", false)]
    public async Task 식사기록_등록에서도_미리출발을_검증하고_저장한다(string input, bool valid)
    {
        var (fixture, _, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var response = await handler.Handle(MealSubmission(
            RecordDate, WithEarlyDeparture(NameAndCategory("기록식당", 1), input)));

        if (valid)
        {
            Assert.IsNotType<ViewErrorsResponse>(response);
            var restaurant = await fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
            Assert.Equal(600, restaurant.EarlyDepartureSeconds);
            Assert.Equal(restaurant.Id, (await fixture.Db.MealRecords.SingleAsync(ct)).RestaurantId);
        }
        else
        {
            var errors = Assert.IsType<ViewErrorsResponse>(response);
            Assert.Contains(RestaurantModal.BlockIds.EarlyDeparture, errors.Errors.Keys);
            Assert.False(await fixture.Db.Restaurants.AnyAsync(ct));
            Assert.False(await fixture.Db.MealRecords.AnyAsync(ct));
        }
    }

    // --- 식사 기록용 등록(MealCallbackId): 새 식당 1곳 + 선택한 기존 식당들을 한 트랜잭션으로 확정 ---

    static readonly DateOnly CarryoverDate = new(2026, 9, 17);

    static async Task<Restaurant[]> SeedManyAsync(TestDb fixture, int count, CancellationToken ct)
    {
        var seeded = new Restaurant[count];
        for (var i = 0; i < count; i++)
            seeded[i] = await SeedAsync(fixture, $"부가식당{i + 1}", 1, ct);
        return seeded;
    }

    static Task<Harness> CreateCarryoverAsync(EarlyDepartureNoticeTracker? notices = null) =>
        CreateAsync(notices: notices, options: new LunchOptions { CarryoverMealPromptDate = CarryoverDate });

    static Task MarkPromptPostedAsync(Harness harness, DateOnly date, CancellationToken ct) =>
        new LunchService(harness.Fixture.Db, Channel).MarkMealPromptPostedAsync(
            date, new DateTimeOffset(2026, 9, 17, 4, 30, 0, TimeSpan.Zero), ct);

    [Fact]
    public async Task 한도가_2이면_새_식당과_기존_식당을_함께_기록하고_둘_다_안내한다()
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var existing = await SeedAsync(h.Fixture, "기존식당", 1, ct);
        await SeedClosedVotesAsync(h.Fixture, RecordDate, existing.Id, 6, ct);

        var response = await h.Handler.Handle(
            MealSubmission(RecordDate, WithPlaces(NameAndCategory("새식당", 3), existing.Id)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var registered = await h.Fixture.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Name == "새식당", ct);
        var records = await h.Fixture.Db.MealRecords.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        Assert.Equal(new[] { registered.Id, existing.Id }, records.Select(m => m.RestaurantId));
        Assert.Equal(new[] { MealSource.NewRegistration, MealSource.Prompt }, records.Select(m => m.Source));
        Assert.All(records, m => Assert.Equal("U1", m.RecordedBySlackUserId));
        Assert.Equal(1, h.Slack.ChatFake.PostCallCount);
        var posted = h.Slack.ChatFake.PostedMessage!;
        Assert.Contains("새식당", Shown(posted));
        Assert.Contains("기존식당", Shown(posted));
        Assert.DoesNotContain("새식당", posted.Text);
        Assert.DoesNotContain("기존식당", posted.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 한도가_2여도_기존_식당을_고르지_않으면_새_식당만_기록한다(bool emptySelectionSent)
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var existing = await SeedAsync(h.Fixture, "기존식당", 1, ct);
        await SeedClosedVotesAsync(h.Fixture, RecordDate, existing.Id, 6, ct);
        var state = NameAndCategory("새식당", 3);
        if (emptySelectionSent)
            WithPlaces(state);

        var response = await h.Handler.Handle(MealSubmission(RecordDate, state));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var registered = await h.Fixture.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Name == "새식당", ct);
        var record = Assert.Single(await h.Fixture.Db.MealRecords.AsNoTracking().ToListAsync(ct));
        Assert.Equal(registered.Id, record.RestaurantId);
    }

    [Theory]
    [InlineData(6, 1, true)]
    [InlineData(6, 2, false)]
    [InlineData(11, 2, true)]
    [InlineData(11, 3, false)]
    public async Task 부가_식당은_한도에서_새_식당_몫을_뺀_수까지만_받는다(int voters, int extraCount, bool accepted)
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var extras = await SeedManyAsync(h.Fixture, extraCount, ct);
        await SeedClosedVotesAsync(h.Fixture, RecordDate, extras[0].Id, voters, ct);
        var before = await StateOfAsync(h.Fixture, ct);

        var response = await h.Handler.Handle(MealSubmission(
            RecordDate, WithPlaces(NameAndCategory("새식당", 3), [.. extras.Select(extra => extra.Id)])));

        if (accepted)
        {
            Assert.IsNotType<ViewErrorsResponse>(response);
            Assert.Equal(extraCount + 1, await h.Fixture.Db.MealRecords.CountAsync(ct));
        }
        else
        {
            var errors = Assert.IsType<ViewErrorsResponse>(response);
            Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
            Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
            Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        }
    }

    [Theory]
    [InlineData("한도_1에서_부가_식당")]
    [InlineData("같은_식당을_두_번")]
    [InlineData("없는_식당")]
    [InlineData("새_식당과_같은_이름의_식당")]
    [InlineData("기록할_수_없는_Pending_식당")]
    public async Task 거절된_부가_식당_선택은_식당_카테고리_기록_후보_알림을_어느_것도_남기지_않는다(string kind)
    {
        var notices = new EarlyDepartureNoticeTracker();
        var h = await CreateAsync(notices: notices);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var existing = await SeedAsync(h.Fixture, "기존식당", 1, ct);
        var pending = await SeedAsync(h.Fixture, "이름만아는집", null, ct);
        if (kind != "한도_1에서_부가_식당")
            await SeedClosedVotesAsync(h.Fixture, RecordDate, existing.Id, 6, ct);
        // 열린 투표: 후보를 얹거나 메시지를 갱신했다면 후보 목록·갱신 횟수에 드러난다.
        await SeedOpenPollAsync(h, new DateOnly(2026, 9, 19), ct);
        var posts = h.Slack.ChatFake.PostCallCount;
        var before = await StateOfAsync(h.Fixture, ct);
        var (name, extraIds) = kind switch
        {
            "한도_1에서_부가_식당" => ("새식당", new[] { existing.Id }),
            "같은_식당을_두_번" => ("새식당", new[] { existing.Id, existing.Id }),
            "없는_식당" => ("새식당", new[] { 99_999L }),
            "새_식당과_같은_이름의_식당" => ("기존 식당", new[] { existing.Id }),
            _ => ("새식당", new[] { pending.Id }),
        };
        var state = WithNewCategory(WithPlaces(NameAndCategory(name, null), extraIds), "새분류");

        var response = await h.Handler.Handle(MealSubmission(RecordDate, state));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(posts, h.Slack.ChatFake.PostCallCount);
        Assert.Equal(0, h.Slack.ChatFake.UpdateCallCount);
        Assert.False(notices.HasAttempted(RecordDate, 1, "U1"));
    }

    [Fact]
    public async Task 선택한_식당_값을_읽을_수_없으면_식당_선택_블록에_오류를_돌려주고_저장하지_않는다()
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(h.Fixture, "기존식당", 1, ct);
        var before = await StateOfAsync(h.Fixture, ct);

        var response = await h.Handler.Handle(MealSubmission(
            RecordDate, WithPlaceOptions(WithNewCategory(NameAndCategory("새식당", null), "새분류"), "abc")));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(MealConfirmationModal.PlacesBlockId));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("date:20260918")]
    [InlineData("restaurant:7")]
    [InlineData("meal:20260918:C2:U1")]
    [InlineData("meal:20260918:C1:U2")]
    [InlineData("meal:2026-09-18:C1:U1")]
    public async Task 식사_기록용_창의_메타데이터가_틀리면_일반_등록으로_넘기지_않고_거절한다(string metadata)
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(h.Fixture, "기존식당", 1, ct);
        var before = await StateOfAsync(h.Fixture, ct);
        var submission = Submission(metadata, NameAndCategory("위조식당", 1));
        submission.View.CallbackId = RestaurantModal.MealCallbackId;

        var response = await h.Handler.Handle(submission);

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("date:nope")]
    [InlineData("restaurant:abc")]
    [InlineData("date:20260918")]
    [InlineData("meal:20260918:C1:U1")]
    public async Task 일반_callback의_비어_있지_않은_알_수_없는_메타데이터는_일반_등록으로_넘기지_않고_거절한다(string metadata)
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(h.Fixture, "기존식당", 1, ct);
        var before = await StateOfAsync(h.Fixture, ct);
        var submission = Submission(metadata, NameAndCategory("일반식당", 1));
        submission.View.CallbackId = RestaurantModal.CallbackId;

        var response = await h.Handler.Handle(submission);

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 이름만_있는_Pending_식당과_같은_이름으로_기록하면_그_식당이_Active로_승격되어_기록된다()
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var pending = await SeedAsync(h.Fixture, "이름만아는집", null, ct);

        var response = await h.Handler.Handle(MealSubmission(RecordDate, NameAndCategory("이름만아는집", 1)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var stored = await h.Fixture.Db.Restaurants.AsNoTracking().SingleAsync(ct);
        Assert.Equal(pending.Id, stored.Id);
        Assert.Equal(RestaurantStatus.Active, stored.Status);
        Assert.Equal(pending.Id, (await h.Fixture.Db.MealRecords.AsNoTracking().SingleAsync(ct)).RestaurantId);
    }

    [Fact]
    public async Task 기록이_있는_날_등록_창에서_하나를_빼고_제출하면_새_식당과_남긴_식당으로_정정된다()
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAsync(h.Fixture, "식당A", 1, ct);
        var second = await SeedAsync(h.Fixture, "식당B", 1, ct);
        await SeedClosedVotesAsync(h.Fixture, RecordDate, first.Id, 6, ct);
        await SeedMealAsync(h.Fixture, RecordDate, first.Id, ct);
        await SeedMealAsync(h.Fixture, RecordDate, second.Id, ct);
        var state = await h.Meals.GetStateAsync(RecordDate, ct);
        var categories = await h.Fixture.Db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);

        var view = RestaurantModal.BuildForMeal(categories, RecordDate, Channel, "U1", state);

        // 기록된 두 곳을 말없이 자르지 않고 모두 미리 담는다. 한도 2에서는 새 식당 몫을 위해 사람이 하나를 뺀다.
        var places = (ExternalMultiSelectMenu)view.Blocks.OfType<InputBlock>()
            .Single(block => block.BlockId == MealConfirmationModal.PlacesBlockId).Element;
        var initial = places.InitialOptions.Select(option => long.Parse(option.Value)).ToArray();
        Assert.Equal(new[] { first.Id, second.Id }, initial);

        var response = await h.Handler.Handle(
            SubmissionOf(view, WithPlaces(NameAndCategory("식당C", 1), first.Id)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var registered = await h.Fixture.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Name == "식당C", ct);
        var records = await h.Fixture.Db.MealRecords.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        // 유지한 식당의 행은 기록자를 보존하고, 뺀 식당은 사라지며, 새 식당이 더해진다.
        Assert.Equal(new[] { first.Id, registered.Id }, records.Select(m => m.RestaurantId));
        Assert.Equal("ORIGINAL", records[0].RecordedBySlackUserId);
        Assert.Equal("U1", records[1].RecordedBySlackUserId);
        var shown = Shown(h.Slack.ChatFake.PostedMessage!);
        Assert.Contains("식당A", shown);
        Assert.Contains("식당C", shown);
        Assert.DoesNotContain("식당B", shown);
    }

    [Fact]
    public async Task 기록이_있는_날_창이_미리_담은_선택을_그대로_제출하면_한도를_넘어_거절하고_기록을_보존한다()
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAsync(h.Fixture, "식당A", 1, ct);
        var second = await SeedAsync(h.Fixture, "식당B", 1, ct);
        await SeedClosedVotesAsync(h.Fixture, RecordDate, first.Id, 6, ct);
        await SeedMealAsync(h.Fixture, RecordDate, first.Id, ct);
        await SeedMealAsync(h.Fixture, RecordDate, second.Id, ct);
        var before = await StateOfAsync(h.Fixture, ct);
        var state = await h.Meals.GetStateAsync(RecordDate, ct);
        var categories = await h.Fixture.Db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
        var view = RestaurantModal.BuildForMeal(categories, RecordDate, Channel, "U1", state);
        var places = (ExternalMultiSelectMenu)view.Blocks.OfType<InputBlock>()
            .Single(block => block.BlockId == MealConfirmationModal.PlacesBlockId).Element;
        var initial = places.InitialOptions.Select(option => long.Parse(option.Value)).ToArray();

        var response = await h.Handler.Handle(
            SubmissionOf(view, WithNewCategory(WithPlaces(NameAndCategory("식당C", null), initial), "새분류")));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task 한도가_1인_날_등록하면_기존_기록이_새_식당으로_교체된다()
    {
        var h = await CreateAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var original = await SeedAsync(h.Fixture, "기존식당", 1, ct);
        await SeedMealAsync(h.Fixture, RecordDate, original.Id, ct);
        var state = await h.Meals.GetStateAsync(RecordDate, ct);
        var categories = await h.Fixture.Db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);

        var view = RestaurantModal.BuildForMeal(categories, RecordDate, Channel, "U1", state);

        Assert.DoesNotContain(view.Blocks.OfType<InputBlock>(), block => block.BlockId == MealConfirmationModal.PlacesBlockId);
        var response = await h.Handler.Handle(SubmissionOf(view, NameAndCategory("교체식당", 1)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var registered = await h.Fixture.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Name == "교체식당", ct);
        var record = await h.Fixture.Db.MealRecords.AsNoTracking().SingleAsync(ct);
        Assert.Equal(registered.Id, record.RestaurantId);
        Assert.Equal(MealSource.NewRegistration, record.Source);
    }

    [Fact]
    public async Task 기록_저장_도중_실패하면_함께_등록한_식당과_카테고리도_남기지_않고_안내하지_않는다()
    {
        var notices = new EarlyDepartureNoticeTracker();
        var h = await CreateAsync(notices: notices);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var original = await SeedAsync(h.Fixture, "기존식당", 1, ct);
        await SeedMealAsync(h.Fixture, RecordDate, original.Id, ct);
        await SeedOpenPollAsync(h, new DateOnly(2026, 9, 19), ct);
        var posts = h.Slack.ChatFake.PostCallCount;
        var before = await StateOfAsync(h.Fixture, ct);
        // 식당·카테고리를 쓴 뒤 기록 행을 넣는 단계에서 실패시킨다.
        await h.Fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_meal_insert BEFORE INSERT ON \"MealRecords\" " +
            "BEGIN SELECT RAISE(ABORT, 'meal insert blocked'); END;", ct);

        await Assert.ThrowsAnyAsync<Exception>(() => h.Handler.Handle(MealSubmission(
            RecordDate, WithNewCategory(NameAndCategory("되돌려질식당", null), "되돌려질분류"))));

        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(posts, h.Slack.ChatFake.PostCallCount);
        Assert.Equal(0, h.Slack.ChatFake.UpdateCallCount);
        Assert.True(notices.TryClaim(RecordDate, 123, "U1"));
    }

    [Fact]
    public async Task 지정한_지난_요청의_첫_기록은_저장하지만_오늘의_투표와_출발_안내는_바꾸지_않는다()
    {
        var notices = new EarlyDepartureNoticeTracker();
        var h = await CreateCarryoverAsync(notices);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(h.Fixture, "기존식당", 1, ct);
        await MarkPromptPostedAsync(h, CarryoverDate, ct);
        await SeedOpenPollAsync(h, RecordDate, ct);
        var candidatesBefore = await h.Fixture.Db.PollCandidates.AsNoTracking().CountAsync(ct);

        var response = await h.Handler.Handle(MealSubmission(CarryoverDate, NameAndCategory("지난날새식당", 3)));

        Assert.IsNotType<ViewErrorsResponse>(response);
        var registered = await h.Fixture.Db.Restaurants.AsNoTracking().SingleAsync(r => r.Name == "지난날새식당", ct);
        var record = await h.Fixture.Db.MealRecords.AsNoTracking().SingleAsync(ct);
        Assert.Equal(CarryoverDate, record.Date);
        Assert.Equal(registered.Id, record.RestaurantId);
        Assert.Equal(MealSource.NewRegistration, record.Source);
        // 오늘의 열린 투표에 식당을 얹거나 투표 메시지를 갱신하지 않는다.
        Assert.Equal(candidatesBefore, await h.Fixture.Db.PollCandidates.AsNoTracking().CountAsync(ct));
        Assert.Equal(0, h.Slack.ChatFake.UpdateCallCount);
        // 지난 날짜의 기록이 오늘의 미리 출발 안내를 끝내지 않는다.
        Assert.False(notices.HasAttempted(CarryoverDate, 1, "U1"));
        Assert.True(notices.TryClaim(RecordDate, 123, "U1"));
        Assert.Contains("지난날새식당", Shown(h.Slack.ChatFake.PostedMessage!));
    }

    [Fact]
    public async Task 지난_요청은_첫_기록_뒤_다시_제출하면_거절하고_첫_기록을_보존한다()
    {
        var h = await CreateCarryoverAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await MarkPromptPostedAsync(h, CarryoverDate, ct);
        await h.Handler.Handle(MealSubmission(CarryoverDate, NameAndCategory("먼저저장한식당", 3)));
        var before = await StateOfAsync(h.Fixture, ct);
        var posts = h.Slack.ChatFake.PostCallCount;

        var response = await h.Handler.Handle(MealSubmission(
            CarryoverDate, WithNewCategory(NameAndCategory("나중식당", null), "나중분류")));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(posts, h.Slack.ChatFake.PostCallCount);
    }

    // --- 배포 전에 열린 구형 창(일반 callback + 날짜만 실린 메타데이터) ---

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    public async Task 구형_날짜_전용_창은_날짜와_관계없이_재진입을_안내하고_저장하지_않는다(int dayOffset, bool carryover)
    {
        var notices = new EarlyDepartureNoticeTracker();
        var h = carryover ? await CreateCarryoverAsync(notices) : await CreateAsync(notices: notices);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await MarkPromptPostedAsync(h, CarryoverDate, ct);
        var before = await StateOfAsync(h.Fixture, ct);
        var requestedDate = RecordDate.AddDays(dayOffset);

        var response = await h.Handler.Handle(LegacyMealSubmission(
            requestedDate, WithNewCategory(NameAndCategory("구형창식당", null), "구형분류")));

        var errors = Assert.IsType<ViewErrorsResponse>(response);
        Assert.True(errors.Errors.ContainsKey(RestaurantModal.BlockIds.Name));
        Assert.Equal(before, await StateOfAsync(h.Fixture, ct));
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.False(notices.HasAttempted(requestedDate, 1, "U1"));
    }
}
