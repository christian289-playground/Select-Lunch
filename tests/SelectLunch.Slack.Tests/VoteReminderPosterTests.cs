using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 마감 5분 전 미참여자 비공개 알림. 실제 SQLite 위에서 서비스 로직을 그대로 돌리고, 슬랙과 시계만
/// 페이크로 바꾼다. 여기서 지켜야 할 것은 하나다 — <b>같은 풀에서 같은 사람에게 두 번 가지 않고,
/// 마감 뒤에는 아무것도 가지 않는다.</b> 그래서 "누구에게 보냈는가"보다 "다시 보내지 않는가"와
/// "언제 멈추는가"를 중점으로 본다.
/// </summary>
public class VoteReminderPosterTests
{
    const string Channel = "C1";
    const string MessageTs = "1700000000.000100";
    static readonly DateOnly Today = new(2026, 9, 29);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 29, 11, 0, 0, TimeSpan.FromHours(9));

    /// <summary>저장된 마감 시각. 시계는 UTC, 마감은 +09:00라 오프셋이 다른 값끼리 비교되는지도 함께 본다.</summary>
    static readonly DateTimeOffset ClosesAt = new(2026, 9, 29, 11, 30, 0, TimeSpan.FromHours(9));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 테스트가 손으로 돌리는 시계. <see cref="CancellationTokenSource"/>의 마감 타이머도 이 시계를
    /// 따라가므로, <see cref="AdvanceTo"/> 한 번으로 "마감 시각이 지났다"를 재현한다.
    /// 타이머는 한 번만 울리는 것(CancellationTokenSource가 쓰는 형태)만 지원한다.
    /// </summary>
    sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        readonly object _gate = new();
        readonly List<ManualTimer> _timers = [];
        DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public void AdvanceTo(DateTimeOffset now)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now = now;
                due = [.. _timers.Where(t => t.DueAt <= now)];
            }

            foreach (var timer in due)
                timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        void Remove(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// 채널 멤버 목록(여러 페이지)과 사용자 프로필을 돌려주는 페이크. 슬랙의 페이지네이션 규칙대로
    /// 마지막 페이지의 NextCursor는 빈 문자열이다.
    /// </summary>
    sealed class FakeDirectory
    {
        public List<string[]> Pages { get; } = [];

        /// <summary>여기 없는 사용자는 평범한 활성 사람 계정이다.</summary>
        public Dictionary<string, User> Profiles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Exception> InfoFailures { get; } = new(StringComparer.Ordinal);

        public Exception? MembersFailure { get; set; }

        /// <summary>멤버 조회 중에 도는 훅. 오래 걸리는 조회·그 사이 벌어지는 일을 재현한다.</summary>
        public Func<CancellationToken, Task>? OnMembers { get; set; }

        /// <summary>프로필 조회 중에 도는 훅.</summary>
        public Func<string, CancellationToken, Task>? OnInfo { get; set; }

        public List<(string ChannelId, int Limit, string? Cursor)> MemberRequests { get; } = [];

        public void Attach(FakeSlackApiClient slack)
        {
            slack.ConversationsApi = StubApi.Of<IConversationsApi>(
                ("Members", (Func<string, int, string?, CancellationToken, Task<ConversationMembersResponse>>)MembersAsync));
            slack.UsersApi = StubApi.Of<IUsersApi>(
                ("Info", (Func<string, bool, CancellationToken, Task<User>>)InfoAsync));
        }

        async Task<ConversationMembersResponse> MembersAsync(
            string channelId, int limit, string? cursor, CancellationToken ct)
        {
            MemberRequests.Add((channelId, limit, cursor));

            if (OnMembers is { } hook)
                await hook(ct);

            if (MembersFailure is { } failure)
                throw failure;

            var index = cursor is null ? 0 : int.Parse(cursor["page".Length..]);
            return new ConversationMembersResponse
            {
                Members = Pages[index],
                ResponseMetadata = new ResponseMetadata
                {
                    NextCursor = index + 1 < Pages.Count ? $"page{index + 1}" : "",
                },
            };
        }

        async Task<User> InfoAsync(string userId, bool includeLocale, CancellationToken ct)
        {
            if (OnInfo is { } hook)
                await hook(userId, ct);

            if (InfoFailures.TryGetValue(userId, out var failure))
                throw failure;

            return Profiles.TryGetValue(userId, out var profile) ? profile : new User { Id = userId };
        }
    }

    sealed class Harness(
        TestDb fixture, LunchService service, FakeSlackApiClient slack, FakeDirectory directory,
        ManualClock clock, long pollId, long restaurantId)
        : IAsyncDisposable
    {
        public TestDb Fixture { get; } = fixture;
        public LunchService Service { get; } = service;
        public FakeSlackApiClient Slack { get; } = slack;
        public FakeDirectory Directory { get; } = directory;
        public ManualClock Clock { get; } = clock;
        public long PollId { get; } = pollId;
        public long RestaurantId { get; } = restaurantId;

        /// <summary>같은 DB·슬랙을 쓰는 새 인스턴스. 프로세스 재기동 뒤의 서비스를 흉내 낸다.</summary>
        public VoteReminderPoster NewPoster(string channelId = Channel) =>
            new(Slack, Fixture.Db, channelId, Clock, NullLogger<VoteReminderPoster>.Instance);

        VoteReminderPoster? _poster;

        public VoteReminderPoster Poster => _poster ??= NewPoster();

        public Task ClosePollAsync(CancellationToken ct) =>
            Fixture.Db.Polls
                .Where(p => p.Id == PollId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PollStatus.Closed), ct);

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    /// <param name="untilClose">시계가 마감 몇 분·초 전을 가리키는가. 기본은 구간 한가운데(마감 4분 전).</param>
    static async Task<Harness> SetupAsync(TimeSpan? untilClose = null, string? messageTs = MessageTs)
    {
        var ct = Ct;
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);

        // 투표 행은 식당을 가리켜야 하므로(FK) 식당 하나를 먼저 만든다.
        var restaurant = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "스시로", 3, null, null, null), "U0", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        poll.MessageTs = messageTs;
        await fixture.Db.SaveChangesAsync(ct);

        var slack = new FakeSlackApiClient();
        var directory = new FakeDirectory();
        directory.Attach(slack);

        var clock = new ManualClock(ClosesAt - (untilClose ?? TimeSpan.FromMinutes(4)));
        return new Harness(fixture, service, slack, directory, clock, poll.Id, restaurant.Id);
    }

    static string[] Targets(Harness h) => [.. h.Slack.ChatFake.EphemeralAttempts.Select(a => a.UserId)];

    static Task<DateTimeOffset?> MarkerAsync(Harness h, CancellationToken ct) =>
        h.Fixture.Db.Polls
            .AsNoTracking()
            .Where(p => p.Id == h.PollId)
            .Select(p => p.VoteReminderAttemptedAt)
            .SingleAsync(ct);

    /// <summary>슬랙을 한 번도 건드리지 않았다. 조회조차 하지 않아야 하는 "보낼 상태가 아님"을 확인한다.</summary>
    static void AssertNoSlackCalls(Harness h)
    {
        Assert.Empty(h.Directory.MemberRequests);
        Assert.Empty(h.Slack.ChatFake.PermalinkRequests);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    // --- 누구에게 보내는가 ---

    [Fact]
    public async Task 투표도_기권도_하지_않은_멤버에게만_본인에게만_보이는_알림을_보낸다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U4", "U1", "U3", "U2", "U5"]);   // 일부러 정렬되지 않은 순서
        await h.Service.CastVoteAsync(h.PollId, "U1", h.RestaurantId, Ct);
        await h.Service.CastAbstentionAsync(h.PollId, "U2", Ct);

        Assert.Equal(3, await h.Poster.PostAsync(h.PollId, Ct));

        // 순서는 ID 서수 정렬이라 같은 입력이면 항상 같다.
        Assert.Equal(["U3", "U4", "U5"], Targets(h));
        Assert.All(h.Slack.ChatFake.EphemeralAttempts, attempt =>
        {
            Assert.Equal(Channel, attempt.Message.Channel);
            // 비공개 메시지는 스레드 안에서 안 보이는 경우가 있어 스레드에 걸지 않는다.
            Assert.Null(attempt.Message.ThreadTs);
            Assert.Contains(h.Slack.ChatFake.PermalinkUrl, attempt.Message.Text);
        });
        // 채널 공개 메시지는 한 건도 나가지 않는다.
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.Equal(0, h.Slack.ChatFake.UpdateCallCount);

        // 퍼머링크는 대상 목록 전체에 한 번만 구한다. 대상은 투표 메시지 자신이다.
        var permalink = Assert.Single(h.Slack.ChatFake.PermalinkRequests);
        Assert.Equal(Channel, permalink.ChannelId);
        Assert.Equal(MessageTs, permalink.MessageTs);
    }

    [Fact]
    public async Task 슬랙봇_봇_앱_비활성_계정은_알림_대상이_아니다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["UBOT", "UAPP", "UGONE", "USLACKBOT", "UHUMAN"]);
        h.Directory.Profiles["UBOT"] = new User { Id = "UBOT", IsBot = true };
        h.Directory.Profiles["UAPP"] = new User { Id = "UAPP", IsAppUser = true };
        h.Directory.Profiles["UGONE"] = new User { Id = "UGONE", Deleted = true };
        // USLACKBOT의 프로필은 일반 사람과 똑같이 나온다(is_bot=false) — ID로 걸러야 한다.

        Assert.Equal(1, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["UHUMAN"], Targets(h));
    }

    [Fact]
    public async Task 멤버_목록은_모든_페이지를_끝까지_읽고_겹치는_ID는_한_번만_보낸다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U1", "U2"]);
        h.Directory.Pages.Add(["U2", "U3"]);   // 페이지 경계에서 U2가 또 나온다
        h.Directory.Pages.Add(["U4"]);
        await h.Service.CastVoteAsync(h.PollId, "U1", h.RestaurantId, Ct);

        Assert.Equal(3, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U2", "U3", "U4"], Targets(h));
        Assert.Equal([null, "page1", "page2"], h.Directory.MemberRequests.Select(r => r.Cursor).ToArray());
        Assert.All(h.Directory.MemberRequests, request =>
        {
            Assert.Equal(Channel, request.ChannelId);
            Assert.Equal(200, request.Limit);
        });
    }

    [Fact]
    public async Task 사용자_정보를_확인할_수_없는_계정만_빼고_나머지에게_보낸다()
    {
        // 슬랙 커넥트 외부 멤버처럼 users.info가 user_not_found로 답하는 계정 하나 때문에
        // 전원의 알림이 막히면 안 된다. 확인할 수 없는 계정에는 보내지도 않는다.
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["UEXT", "U1"]);
        h.Directory.InfoFailures["UEXT"] = new SlackException(new ErrorResponse { Error = "user_not_found" });

        Assert.Equal(1, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U1"], Targets(h));
    }

    [Fact]
    public async Task 사용자_조회가_권한_오류면_전원이_제외된_채_표시되지_않고_다음_호출이_다시_한다()
    {
        // users:read 스코프가 빠지면 users.info는 **모든** 사용자에게 missing_scope로 답한다.
        // 이걸 "그 사람만 제외"로 삼키면 대상이 0명이 된 채 표시만 남아 알림이 조용히 사라진다.
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        var missingScope = new SlackException(new ErrorResponse { Error = "missing_scope" });
        h.Directory.InfoFailures["U3"] = missingScope;
        h.Directory.InfoFailures["U4"] = missingScope;

        await Assert.ThrowsAsync<SlackException>(() => h.Poster.PostAsync(h.PollId, Ct));

        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Null(await MarkerAsync(h, Ct));

        // 스코프를 추가한 뒤에는 같은 풀에서 정상적으로 나간다.
        h.Directory.InfoFailures.Clear();
        Assert.Equal(2, await h.Poster.PostAsync(h.PollId, Ct));
        Assert.Equal(["U3", "U4"], Targets(h));
    }

    // --- 한 번만 보낸다 ---

    [Fact]
    public async Task 첫_전송_전에_시도_표시가_이미_저장되어_있다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        var markersAtSend = new List<DateTimeOffset?>();
        h.Slack.ChatFake.OnPostEphemeral = async (_, _, ct) => markersAtSend.Add(await MarkerAsync(h, ct));

        await h.Poster.PostAsync(h.PollId, Ct);

        // 전송이 진행되는 시점에 DB에 이미 표시가 있어야, 그 뒤 어떤 일이 터져도 다시 보내지 않는다.
        Assert.Equal(2, markersAtSend.Count);
        Assert.All(markersAtSend, marker => Assert.Equal(h.Clock.GetUtcNow(), marker));
    }

    [Fact]
    public async Task 서비스를_새로_만들어도_다시_보내지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        Assert.Equal(2, await h.Poster.PostAsync(h.PollId, Ct));

        // 재기동 뒤의 새 인스턴스. 그 사이 새 멤버가 들어왔어도 이미 처리된 풀이다.
        h.Directory.Pages[0] = ["U3", "U4", "U9"];
        var restarted = h.NewPoster();
        Assert.Equal(0, await restarted.PostAsync(h.PollId, Ct));
        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U3", "U4"], Targets(h));
        Assert.Single(h.Directory.MemberRequests);
    }

    [Fact]
    public async Task 전송이_실패해도_다시_시도하지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);
        h.Slack.ChatFake.OnPostEphemeral = (_, _, _) => throw new HttpRequestException("슬랙 응답 없음");

        // 실패는 던지지 않고 로그로만 남는다. 성공한 것이 없으니 0.
        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        // 슬랙이 회복돼도, 새 인스턴스로 다시 불러도 같은 사람에게 다시 가지 않는다.
        h.Slack.ChatFake.OnPostEphemeral = null;
        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));
        Assert.Equal(0, await h.NewPoster().PostAsync(h.PollId, Ct));

        Assert.Equal(["U3"], Targets(h));
        Assert.NotNull(await MarkerAsync(h, Ct));
    }

    [Fact]
    public async Task 한_명에게_실패해도_나머지에게는_보낸다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U1", "U2", "U3"]);
        h.Slack.ChatFake.OnPostEphemeral = (user, _, _) =>
            user == "U2" ? throw new HttpRequestException("U2만 실패") : Task.CompletedTask;

        Assert.Equal(2, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U1", "U2", "U3"], Targets(h));
    }

    [Fact]
    public async Task 시도_표시를_저장하지_못하면_아무에게도_보내지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        // 장애 주입이다(데이터 투입이 아님): 풀 행 UPDATE를 막아 선점이 실패하는 상황을 만든다.
        await h.Fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER block_poll_update BEFORE UPDATE ON Polls BEGIN SELECT RAISE(ABORT, 'blocked'); END;", Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => h.Poster.PostAsync(h.PollId, Ct));

        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Null(await MarkerAsync(h, Ct));
    }

    [Fact]
    public async Task 조회하는_사이_다른_경로가_먼저_표시하면_보내지_않는다()
    {
        // 같은 풀에 호출이 겹쳐도 배치는 하나만 나간다. 먼저 선점한 쪽만 보낸다.
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        var other = h.NewPoster();
        h.Directory.OnMembers = async _ =>
        {
            h.Directory.OnMembers = null;
            Assert.Equal(2, await other.PostAsync(h.PollId, Ct));
        };

        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U3", "U4"], Targets(h));
    }

    // --- 선점 전 실패는 아무것도 보내지 않았으니 다시 할 수 있다 ---

    [Fact]
    public async Task 멤버_조회가_실패하면_표시도_전송도_없고_다음_호출이_처음부터_다시_한다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);
        h.Directory.MembersFailure = new SlackRateLimitException(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<SlackRateLimitException>(() => h.Poster.PostAsync(h.PollId, Ct));

        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Null(await MarkerAsync(h, Ct));

        h.Directory.MembersFailure = null;
        Assert.Equal(1, await h.Poster.PostAsync(h.PollId, Ct));
        Assert.Equal(["U3"], Targets(h));
    }

    [Fact]
    public async Task 퍼머링크를_얻지_못하면_표시도_전송도_없고_다음_호출이_다시_한다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);
        h.Slack.ChatFake.PermalinkFailWith = () => new HttpRequestException("퍼머링크 실패");

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Poster.PostAsync(h.PollId, Ct));

        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Null(await MarkerAsync(h, Ct));

        h.Slack.ChatFake.PermalinkFailWith = null;
        Assert.Equal(1, await h.Poster.PostAsync(h.PollId, Ct));
        Assert.Equal(["U3"], Targets(h));
    }

    [Fact]
    public async Task 보낼_대상이_없으면_슬랙_전송과_퍼머링크_조회_없이_표시만_남긴다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U1", "U2"]);
        await h.Service.CastVoteAsync(h.PollId, "U1", h.RestaurantId, Ct);
        await h.Service.CastAbstentionAsync(h.PollId, "U2", Ct);

        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Empty(h.Slack.ChatFake.PermalinkRequests);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.NotNull(await MarkerAsync(h, Ct));

        // 표시가 남았으니 다음 tick은 멤버 목록을 다시 훑지 않는다.
        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));
        Assert.Single(h.Directory.MemberRequests);
    }

    // --- 보내기 직전의 재확인 ---

    [Theory]
    [InlineData(false)]   // 투표
    [InlineData(true)]    // 기권
    public async Task 조회한_뒤_보내기_전에_투표나_기권한_사람은_건너뛴다(bool abstain)
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        h.Slack.ChatFake.OnPostEphemeral = async (user, _, ct) =>
        {
            // U3에게 보내는 사이 U4가 반응한다. 대상 목록은 이미 만들어진 뒤다.
            if (user != "U3")
                return;

            if (abstain)
                await h.Service.CastAbstentionAsync(h.PollId, "U4", ct);
            else
                await h.Service.CastVoteAsync(h.PollId, "U4", h.RestaurantId, ct);
        };

        Assert.Equal(1, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U3"], Targets(h));
    }

    [Fact]
    public async Task 보내는_도중_투표가_닫히면_남은_사람에게는_보내지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4", "U5"]);
        h.Slack.ChatFake.OnPostEphemeral = async (user, _, ct) =>
        {
            if (user == "U3")
                await h.ClosePollAsync(ct);
        };

        Assert.Equal(1, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Equal(["U3"], Targets(h));
    }

    // --- 보낼 수 있는 상태인가 ---

    [Fact]
    public async Task 다른_채널의_풀이면_아무것도_하지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);

        Assert.Equal(0, await h.NewPoster("C2").PostAsync(h.PollId, Ct));

        AssertNoSlackCalls(h);
        Assert.Null(await MarkerAsync(h, Ct));
    }

    [Fact]
    public async Task 없는_풀이면_아무것도_하지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);

        Assert.Equal(0, await h.Poster.PostAsync(h.PollId + 1000, Ct));

        AssertNoSlackCalls(h);
    }

    [Fact]
    public async Task 닫힌_풀이면_아무것도_하지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);
        await h.ClosePollAsync(Ct);

        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        AssertNoSlackCalls(h);
        Assert.Null(await MarkerAsync(h, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task 투표_메시지가_아직_안_나갔으면_아무것도_하지_않는다(string? messageTs)
    {
        await using var h = await SetupAsync(messageTs: messageTs);
        h.Directory.Pages.Add(["U3"]);

        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        AssertNoSlackCalls(h);
        Assert.Null(await MarkerAsync(h, Ct));
    }

    [Theory]
    [InlineData(-301, false)]   // 구간 시작 1초 전
    [InlineData(-300, true)]    // 마감 5분 전 정각은 포함
    [InlineData(-1, true)]      // 마감 1초 전
    [InlineData(0, false)]      // 마감 정각은 제외
    [InlineData(30, false)]     // 마감 뒤
    public async Task 알림_구간은_저장된_마감_5분_전부터_마감_직전까지다(int secondsFromClose, bool expectSend)
    {
        await using var h = await SetupAsync(untilClose: TimeSpan.FromSeconds(-secondsFromClose));
        h.Directory.Pages.Add(["U3"]);

        var sent = await h.Poster.PostAsync(h.PollId, Ct);

        if (expectSend)
        {
            Assert.Equal(1, sent);
            Assert.Equal(["U3"], Targets(h));
            Assert.NotNull(await MarkerAsync(h, Ct));
        }
        else
        {
            Assert.Equal(0, sent);
            AssertNoSlackCalls(h);
            Assert.Null(await MarkerAsync(h, Ct));
        }
    }

    // --- 마감 시각에 멈춘다 ---

    [Fact]
    public async Task 조회가_늦어져_마감_시각을_넘기면_표시도_전송도_하지_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        // 프로필 조회가 느려서 끝나 보니 마감 시각이 지나 있다(가짜는 취소 토큰을 무시한다).
        h.Directory.OnInfo = (_, _) =>
        {
            h.Clock.AdvanceTo(ClosesAt.AddSeconds(1));
            return Task.CompletedTask;
        };

        Assert.Equal(0, await h.Poster.PostAsync(h.PollId, Ct));

        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 멈춘_조회는_마감_시각에_취소되고_던지지_않고_끝난다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Directory.OnMembers = async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);   // 응답이 오지 않는 호출. 토큰이 취소돼야만 풀린다.
        };

        var run = h.Poster.PostAsync(h.PollId, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(run.IsCompleted);

        h.Clock.AdvanceTo(ClosesAt);

        // 마감에 의한 중단은 오류가 아니다 — 스케줄러에 예외로 올라가지 않는다.
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Null(await MarkerAsync(h, Ct));
    }

    [Theory]
    [InlineData(false)]   // 이미 나간 요청이라 슬랙이 받아 준 뒤에 마감을 넘김
    [InlineData(true)]    // 응답을 기다리던 중 취소되어 호출이 중단됨
    public async Task 전송_도중_마감되면_남은_사람에게는_보내지_않는다(bool abortsInFlight)
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4", "U5"]);
        h.Slack.ChatFake.OnPostEphemeral = (user, _, ct) =>
        {
            if (user == "U3")
            {
                h.Clock.AdvanceTo(ClosesAt);
                if (abortsInFlight)
                    ct.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        var sent = await h.Poster.PostAsync(h.PollId, Ct);

        // 마감에 걸린 U3 이후로는 한 건도 시도하지 않는다. 중단된 U3는 성공으로 세지 않는다.
        Assert.Equal(abortsInFlight ? 0 : 1, sent);
        Assert.Equal(["U3"], Targets(h));
    }

    // --- 호스트 종료는 삼키지 않는다 ---

    [Fact]
    public async Task 호스트_종료로_취소되면_예외로_전파하고_보내지도_표시하지도_않는다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3"]);
        using var host = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Directory.OnMembers = async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var run = h.Poster.PostAsync(h.PollId, host.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        host.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Null(await MarkerAsync(h, Ct));
    }

    [Fact]
    public async Task 전송_도중_호스트가_종료되면_남은_사람에게_보내지_않고_예외로_전파한다()
    {
        await using var h = await SetupAsync();
        h.Directory.Pages.Add(["U3", "U4"]);
        using var host = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        h.Slack.ChatFake.OnPostEphemeral = (_, _, ct) =>
        {
            host.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Poster.PostAsync(h.PollId, host.Token));

        // 종료 요청은 "전송 실패"로 삼켜 다음 사람에게 넘어가지 않는다. 표시는 이미 남았으므로 재기동해도 다시 가지 않는다.
        Assert.Equal(["U3"], Targets(h));
        Assert.Equal(0, await h.NewPoster().PostAsync(h.PollId, Ct));
        Assert.Equal(["U3"], Targets(h));
    }
}
