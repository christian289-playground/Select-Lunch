using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using SlackNet.WebApi;
using Button = SlackNet.Blocks.Button;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// "점수 자세히 보기" 버튼: 누른 사람에게만, 구성된 채널의 마감된 투표에 저장된 점수 내역만 보여주는지.
/// 슬랙 클라이언트는 <c>Views</c>만 열어 둔다 — 채널 메시지 갱신·게시를 건드리면 NotSupportedException으로 실패한다.
/// </summary>
public class ResultDetailsActionHandlerTests
{
    const string Configured = "C1";
    const string Other = "C2";

    /// <summary>비공개여야 하는 내역에만 들어 있는 문구 — 화면 어디에도 나오면 안 된다.</summary>
    const string Secret = "비공개";

    static readonly DateOnly Day = new(2026, 10, 20);
    static readonly Newtonsoft.Json.JsonSerializerSettings WireJson =
        Default.JsonSettings(Default.SlackTypeResolver(), Default.Logger).SerializerSettings;

    sealed class RecordingViewsApi : IViewsApi
    {
        public List<(string TriggerId, ViewDefinition View)> Opened { get; } = [];

        public List<(string ViewId, string Hash, ViewDefinition View)> Updated { get; } = [];
        public Exception? UpdateFailure { get; set; }

        public Task<ViewResponse> Open(string triggerId, ViewDefinition view, CancellationToken cancellationToken)
        {
            Opened.Add((triggerId, view));
            return Task.FromResult(new ViewResponse());
        }

        public Task<ViewResponse> UpdateByViewId(ViewDefinition view, string viewId, string hash, CancellationToken cancellationToken)
        {
            if (UpdateFailure is { } failure) throw failure;
            Updated.Add((viewId, hash, view));
            return Task.FromResult(new ViewResponse());
        }

        public Task<ViewResponse> Publish(string userId, HomeViewDefinition viewDefinition, string hash, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ViewResponse> Push(string triggerId, ViewDefinition view, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ViewResponse> UpdateByExternalId(ViewDefinition view, string externalId, string hash, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    static async Task<(TestDb Fixture, RecordingViewsApi Views, ResultDetailsActionHandler Handler)> SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var views = new RecordingViewsApi();
        var slack = StubApi.Of<ISlackApiClient>(("get_Views", (Func<IViewsApi>)(() => views)));
        var handler = new ResultDetailsActionHandler(
            fixture.Db, slack,
            Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = Configured }));
        return (fixture, views, handler);
    }

    static async Task<long> SeedAsync(
        TestDb fixture, PollStatus status, string? rationaleJson, CancellationToken ct,
        string channel = Configured)
    {
        var poll = new LunchPoll
        {
            ChannelId = channel,
            Date = Day,
            Status = status,
            MessageTs = "1700000000.000100",
            OpensAt = new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero),
            ClosesAt = new DateTimeOffset(2026, 10, 20, 2, 30, 0, TimeSpan.Zero),
            RationaleJson = rationaleJson,
        };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync(ct);
        fixture.Db.ChangeTracker.Clear();
        return poll.Id;
    }

    static Recommendation LongRecommendation(int others) => new(
        new RestaurantPick(5, $"{Secret}식당 🍜", new DateOnly(2026, 9, 1)),
        new CategoryScore(1, $"{Secret}카테고리", new DateOnly(2026, 10, 1), 20, 1, 2, 17),
        [.. Enumerable.Range(0, others).Select(i =>
            new CategoryScore(100 + i, $"경쟁 {i:D4} 😀", new DateOnly(2026, 10, 2), 10, 0, 1, 9 - i % 3))]);

    static string Snapshot(Recommendation recommendation) =>
        ResultScoreSnapshot.Create(recommendation, new RecommendationOptions());

    static string Expected(Recommendation recommendation) =>
        ResultBlocks.Rationale(recommendation, new RecommendationOptions());

    static BlockActionRequest ChannelClick(BlockAction action, string channelId = Configured, string triggerId = "T-click") => new()
    {
        TriggerId = triggerId,
        User = new User { Id = "U1" },
        Container = new Container { ChannelId = channelId, Type = "message" },
        Actions = [action],
    };

    static BlockActionRequest ChannelClick(string actionId, string channelId = Configured, string triggerId = "T-click") =>
        ChannelClick(new ButtonAction { ActionId = actionId }, channelId, triggerId);

    static BlockActionRequest ChannelObjectClick(string actionId, string? containerChannel, string? channelObject)
    {
        // 폐기된 SDK Channel 타입을 직접 만들지 않고 실제 수신 JSON과 같은 경로로 읽는다.
        var json = JsonSerializer.Serialize(new
        {
            trigger_id = "T-click",
            user = new { id = "U1" },
            container = containerChannel is null ? null : new { channel_id = containerChannel, type = "message" },
            channel = channelObject is null ? null : new { id = channelObject },
            actions = new[] { new { type = "button", action_id = actionId } },
        });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<BlockActionRequest>(json, WireJson)
            ?? throw new InvalidOperationException("합성 요청 JSON을 읽지 못했습니다.");
    }

    static BlockActionRequest ViewClick(
        string actionId, string callbackId, string? privateMetadata,
        string viewId = "V-open", string hash = "H-open", string? channelId = null) => new()
    {
        TriggerId = "T-page",
        User = new User { Id = "U1" },
        Container = new Container { ChannelId = channelId, Type = "view" },
        View = new ModalViewInfo
        {
            Id = viewId,
            Hash = hash,
            CallbackId = callbackId,
            PrivateMetadata = privateMetadata,
        },
        Actions = [new ButtonAction { ActionId = actionId }],
    };

    static BlockActionRequest PageClick(string actionId, ViewDefinition shown) =>
        ViewClick(actionId, shown.CallbackId, shown.PrivateMetadata);

    static List<string> Sections(ViewDefinition view) =>
        [.. view.Blocks.OfType<SectionBlock>().Select(s => Assert.IsType<Markdown>(s.Text).Text)];

    static string Joined(ViewDefinition view) => string.Concat(Sections(view));

    static string VisibleText(ViewDefinition view) => string.Join("\n",
        view.Blocks.OfType<SectionBlock>().Select(s => ((Markdown)s.Text).Text)
            .Concat(view.Blocks.OfType<ContextBlock>().SelectMany(c => c.Elements.OfType<Markdown>().Select(m => m.Text))));

    static string? ButtonTo(ViewDefinition view, int page) =>
        view.Blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>()).Select(b => b.ActionId)
            .FirstOrDefault(id => ActionIds.TryParseResultDetails(id, out _, out var parsed) && parsed == page);

    /// <summary>읽기 전용이어야 한다 — 투표 행과 그 밖의 저장 상태가 요청 전후로 같다.</summary>
    static async Task<string> StateAsync(LunchDbContext db, CancellationToken ct) => JsonSerializer.Serialize(new
    {
        Polls = (await db.Polls.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct)).Select(p => new
        {
            p.Id, p.ChannelId, p.Date, p.Status, p.MessageTs, p.ResultAnnouncedAt,
            p.WinnerRestaurantId, p.RecommendedRestaurantId, p.RationaleJson,
        }),
        Votes = await db.PollVotes.AsNoTracking().CountAsync(ct),
        Candidates = await db.PollCandidates.AsNoTracking().CountAsync(ct),
        Meals = await db.MealRecords.AsNoTracking().CountAsync(ct),
        Restaurants = await db.Restaurants.AsNoTracking().CountAsync(ct),
    });

    static void AssertUnavailable(RecordingViewsApi views)
    {
        var (_, view) = Assert.Single(views.Opened);
        Assert.Equal(VisibleText(ResultDetailsModal.Unavailable()), VisibleText(view));
        Assert.Empty(view.Blocks.OfType<ActionsBlock>());
        Assert.DoesNotContain(Secret, VisibleText(view));
        Assert.Empty(views.Updated);
    }

    [Fact]
    public async Task 채널_버튼은_누른_사람의_트리거로_저장된_점수를_개인_모달로_열고_아무것도_바꾸지_않는다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var recommendation = LongRecommendation(others: 150);
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(recommendation), ct);
        var before = await StateAsync(fixture.Db, ct);

        await handler.Handle(ChannelClick(ActionIds.ResultDetails(pollId), triggerId: "T-clicker"));

        var (triggerId, view) = Assert.Single(views.Opened);
        Assert.Equal("T-clicker", triggerId);
        Assert.IsType<ModalViewDefinition>(view);
        Assert.Equal(Expected(recommendation), Joined(view));
        Assert.Contains("2026-10-20", VisibleText(view));
        Assert.Empty(views.Updated);
        Assert.Equal(before, await StateAsync(fixture.Db, ct));
    }

    [Fact]
    public async Task 옛_스냅샷은_저장된_추천_점수만으로_그린다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var recommendation = LongRecommendation(others: 3);
        var legacyJson = JsonSerializer.Serialize(new { recommendation.Pick, recommendation.Winner, recommendation.Others });
        var pollId = await SeedAsync(fixture, PollStatus.Closed, legacyJson, ct);

        await handler.Handle(ChannelClick(ActionIds.ResultDetails(pollId)));

        var (_, view) = Assert.Single(views.Opened);
        Assert.Equal(ResultBlocks.Rationale(recommendation, null), Joined(view));
    }

    [Fact]
    public async Task 긴_내역은_쪽을_앞뒤로_넘겨도_같은_창에서_원문_그대로_이어진다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var expected = Expected(LongRecommendation(others: 1500));
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 1500)), ct);
        var before = await StateAsync(fixture.Db, ct);

        await handler.Handle(ChannelClick(ActionIds.ResultDetails(pollId)));

        var current = Assert.Single(views.Opened).View;
        var pages = new List<string> { Joined(current) };
        Assert.NotNull(ButtonTo(current, 1));
        for (var page = 1; ButtonTo(current, page) is { } next; page++)
        {
            await handler.Handle(PageClick(next, current));
            current = views.Updated[^1].View;
            pages.Add(Joined(current));
        }

        var pageCount = pages.Count;
        Assert.Equal(expected, string.Concat(pages));

        // 거꾸로 돌아가도 같은 쪽이 같은 내용으로 나온다.
        for (var page = pageCount - 2; page >= 0; page--)
        {
            var back = ButtonTo(current, page) ?? throw new InvalidOperationException("이전 쪽 버튼이 없다.");
            await handler.Handle(PageClick(back, current));
            current = views.Updated[^1].View;
            Assert.Equal(pages[page], Joined(current));
        }

        Assert.Single(views.Opened);
        Assert.Equal(before, await StateAsync(fixture.Db, ct));
    }

    [Theory]
    [InlineData("container")]
    [InlineData("channelObject")]
    [InlineData("containerOkChannelObjectOther")]
    [InlineData("noChannelAtAll")]
    public async Task 구성된_채널의_클릭이_아니면_아무것도_열지_않는다(string kind)
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 3)), ct);
        var id = ActionIds.ResultDetails(pollId);

        var request = kind switch
        {
            "container" => ChannelClick(id, Other),
            "channelObject" => ChannelObjectClick(id, null, Other),
            "containerOkChannelObjectOther" => ChannelObjectClick(id, Configured, Other),
            _ => ChannelObjectClick(id, null, null),
        };
        await handler.Handle(request);

        Assert.Empty(views.Opened);
        Assert.Empty(views.Updated);
    }

    [Fact]
    public async Task 채널_정보가_channel_객체로만_와도_구성된_채널이면_연다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var recommendation = LongRecommendation(others: 3);
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(recommendation), ct);

        await handler.Handle(ChannelObjectClick(ActionIds.ResultDetails(pollId), null, Configured));

        Assert.Equal(Expected(recommendation), Joined(Assert.Single(views.Opened).View));
    }

    [Fact]
    public async Task 다른_채널의_투표_id로는_내역이_아니라_안내만_열린다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var foreignPoll = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 3)), ct, channel: Other);

        await handler.Handle(ChannelClick(ActionIds.ResultDetails(foreignPoll)));

        AssertUnavailable(views);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("missing")]
    public async Task 열려_있거나_없는_투표는_내역이_아니라_안내만_열린다(string kind)
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var openPoll = await SeedAsync(fixture, PollStatus.Open, Snapshot(LongRecommendation(others: 3)), ct);

        await handler.Handle(ChannelClick(ActionIds.ResultDetails(kind == "open" ? openPoll : openPoll + 1000)));

        AssertUnavailable(views);
    }

    [Fact]
    public async Task 저장된_내역이_없으면_안내만_열리고_점수를_지어내지_않는다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pollId = await SeedAsync(fixture, PollStatus.Closed, null, ct);
        var before = await StateAsync(fixture.Db, ct);

        await handler.Handle(ChannelClick(ActionIds.ResultDetails(pollId)));

        AssertUnavailable(views);
        Assert.Equal(before, await StateAsync(fixture.Db, ct));
    }

    [Theory]
    [InlineData("result_details:{poll}:1")]
    [InlineData("vote:{poll}:1")]
    public async Task 정식_점수_상세_id가_아니거나_채널_버튼이_0쪽이_아니면_무시한다(string template)
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 3)), ct);

        await handler.Handle(ChannelClick(template.Replace("{poll}", pollId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)));

        Assert.Empty(views.Opened);
        Assert.Empty(views.Updated);
    }

    [Fact]
    public async Task 버튼이_아닌_동작이나_동작_없는_요청과_트리거_없는_클릭은_무시한다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 3)), ct);
        var id = ActionIds.ResultDetails(pollId);

        await handler.Handle(ChannelClick(new ExternalSelectAction { ActionId = id }));
        await handler.Handle(new BlockActionRequest
        {
            TriggerId = "T-click",
            User = new User { Id = "U1" },
            Container = new Container { ChannelId = Configured, Type = "message" },
            Actions = [],
        });
        await handler.Handle(ChannelClick(id, triggerId: ""));

        Assert.Empty(views.Opened);
        Assert.Empty(views.Updated);
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("noViewId")]
    [InlineData("otherPollModal")]
    [InlineData("pageOutOfRange")]
    [InlineData("otherChannel")]
    public async Task 쪽_이동은_이_투표의_점수_모달_안의_존재하는_쪽일_때만_창을_갱신한다(string kind)
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var expected = Expected(LongRecommendation(others: 1500));
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 1500)), ct);
        var shown = ResultDetailsModal.Build(pollId, Day, expected);
        var next = ActionIds.ResultDetails(pollId, 1);
        var otherPollMetadata = (pollId + 1).ToString(CultureInfo.InvariantCulture);

        var request = kind switch
        {
            "callback" => ViewClick(next, RestaurantModal.CallbackId, shown.PrivateMetadata),
            "noViewId" => ViewClick(next, shown.CallbackId, shown.PrivateMetadata, viewId: ""),
            "otherPollModal" => ViewClick(next, shown.CallbackId, otherPollMetadata),
            "pageOutOfRange" => ViewClick(ActionIds.ResultDetails(pollId, int.MaxValue), shown.CallbackId, shown.PrivateMetadata),
            _ => ViewClick(next, shown.CallbackId, shown.PrivateMetadata, channelId: Other),
        };
        await handler.Handle(request);

        Assert.Empty(views.Opened);
        Assert.Empty(views.Updated);
    }

    [Fact]
    public async Task 쪽을_넘기는_중_저장된_내역을_읽을_수_없게_되면_같은_창을_안내로_바꾼다()
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 1500)), ct);
        await handler.Handle(ChannelClick(ActionIds.ResultDetails(pollId)));
        var shown = Assert.Single(views.Opened).View;
        await fixture.Db.Polls.Where(p => p.Id == pollId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RationaleJson, "{broken"), ct);

        await handler.Handle(PageClick(ActionIds.ResultDetails(pollId, 1), shown));

        var (viewId, _, updated) = Assert.Single(views.Updated);
        Assert.Equal("V-open", viewId);
        Assert.Equal(VisibleText(ResultDetailsModal.Unavailable()), VisibleText(updated));
        Assert.DoesNotContain(Secret, VisibleText(updated));
        Assert.Empty(updated.Blocks.OfType<ActionsBlock>());
    }

    [Theory]
    [InlineData("hash_conflict", true)]
    [InlineData("missing_scope", false)]
    public async Task 중복_쪽이동은_해시충돌만_허용하고_다른_실패는_보존한다(string errorCode, bool ignore)
    {
        var (fixture, views, handler) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pollId = await SeedAsync(fixture, PollStatus.Closed, Snapshot(LongRecommendation(others: 1500)), ct);
        var before = await StateAsync(fixture.Db, ct);
        await handler.Handle(ChannelClick(ActionIds.ResultDetails(pollId)));
        var shown = Assert.Single(views.Opened).View;
        var click = PageClick(ActionIds.ResultDetails(pollId, 1), shown);
        await handler.Handle(click);
        var accepted = Joined(Assert.Single(views.Updated).View);
        views.UpdateFailure = new SlackException(new ErrorResponse { Error = errorCode });

        var error = await Record.ExceptionAsync(() => handler.Handle(click));

        if (ignore) Assert.Null(error);
        else Assert.Same(views.UpdateFailure, error);
        Assert.Equal(accepted, Joined(Assert.Single(views.Updated).View));
        Assert.Equal(before, await StateAsync(fixture.Db, ct));
    }
}
