using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class KakaoMenuTests
{
    const string Tz = "Asia/Seoul";
    const string SourceUrl = "https://pf.kakao.com/rocket-web/web/profiles/_xoxcxcxen/posts";
    static readonly DateOnly Today = new(2026, 9, 29);

    static long Kst(int y, int mo, int d, int h, int mi) =>
        new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();

    /// <summary>실측한 응답 형태(items[].created_at / media[].url)를 줄인 것.</summary>
    static string Json(params (long CreatedAt, string[] Urls)[] items) =>
        "{\"items\":[" + string.Join(",", items.Select(i =>
            $"{{\"id\":1,\"title\":\"t\",\"created_at\":{i.CreatedAt},\"media\":[" +
            string.Join(",", i.Urls.Select(u => $"{{\"type\":\"image\",\"url\":\"{u}\",\"mimetype\":\"image/jpeg\"}}")) +
            "]}")) + "],\"has_next\":true}";

    // --- 파서: 날짜 가드 ---

    [Fact]
    public void 오늘_글이면_첫_이미지만_돌려준다()
    {
        var json = Json((Kst(2026, 9, 29, 10, 42), ["https://k.kakaocdn.net/a.jpg", "https://k.kakaocdn.net/b.jpg"]));

        Assert.Equal("https://k.kakaocdn.net/a.jpg", KakaoMenuParser.FindTodayImageUrl(json, Today, Tz));
    }

    [Fact]
    public void 가장_최근_글이_어제면_오늘_메뉴가_없는_것으로_본다()
    {
        // 주말·휴무일 시나리오: items[0]이 어제 글이다. 절대 오늘 메뉴로 쓰면 안 된다.
        var json = Json(
            (Kst(2026, 9, 28, 10, 45), ["https://k.kakaocdn.net/yesterday.jpg"]),
            (Kst(2026, 9, 27, 10, 45), ["https://k.kakaocdn.net/older.jpg"]));

        Assert.Null(KakaoMenuParser.FindTodayImageUrl(json, Today, Tz));
    }

    [Fact]
    public void 이미지_없는_오늘_글은_건너뛰고_이미지_있는_오늘_글을_쓴다()
    {
        var json = Json(
            (Kst(2026, 9, 29, 15, 0), []),
            (Kst(2026, 9, 29, 10, 42), ["https://k.kakaocdn.net/menu.jpg"]));

        Assert.Equal("https://k.kakaocdn.net/menu.jpg", KakaoMenuParser.FindTodayImageUrl(json, Today, Tz));
    }

    [Fact]
    public void 날짜는_UTC가_아니라_KST_기준으로_비교한다()
    {
        // KST 00:30 = 전날 15:30 UTC. UTC 날짜로 보면 9/28이라 오늘(9/29)이 아니게 된다.
        var justAfterMidnight = Json((Kst(2026, 9, 29, 0, 30), ["https://k.kakaocdn.net/a.jpg"]));
        // KST 다음날 00:00 = 9/29 15:00 UTC. UTC 날짜로는 9/29지만 KST로는 내일이다.
        var midnightTomorrow = Json((Kst(2026, 9, 30, 0, 0), ["https://k.kakaocdn.net/b.jpg"]));

        Assert.NotNull(KakaoMenuParser.FindTodayImageUrl(justAfterMidnight, Today, Tz));
        Assert.Null(KakaoMenuParser.FindTodayImageUrl(midnightTomorrow, Today, Tz));
    }

    [Fact]
    public void http_이미지_주소는_쓰지_않는다()
    {
        var json = Json((Kst(2026, 9, 29, 10, 42), ["http://k.kakaocdn.net/a.jpg"]));

        Assert.Null(KakaoMenuParser.FindTodayImageUrl(json, Today, Tz));
    }

    // --- 파서: 형식 변경은 예외 ---

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("[]")]
    [InlineData("{\"items\":[{\"id\":1},{\"id\":2}]}")]   // created_at 필드가 사라진 경우
    public void 응답_형태가_바뀌면_FormatException(string json)
    {
        Assert.Throws<FormatException>(() => KakaoMenuParser.FindTodayImageUrl(json, Today, Tz));
    }

    [Fact]
    public void 글이_하나도_없으면_예외가_아니라_null이다()
    {
        Assert.Null(KakaoMenuParser.FindTodayImageUrl("{\"items\":[]}", Today, Tz));
    }

    // --- HTTP 클라이언트 ---

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task 클라이언트는_브라우저_UA와_채널_Referer를_보내고_오늘_이미지를_돌려준다()
    {
        var body = Json((Kst(2026, 9, 29, 10, 42), ["https://k.kakaocdn.net/a.jpg"]));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var client = new KakaoMenuClient(new HttpClient(handler));

        var url = await client.GetTodayImageUrlAsync(SourceUrl, Today, Tz, TestContext.Current.CancellationToken);

        Assert.Equal("https://k.kakaocdn.net/a.jpg", url);
        Assert.Contains("Mozilla", handler.Last!.Headers.UserAgent.ToString());
        Assert.Equal("https://pf.kakao.com/_xoxcxcxen/posts", handler.Last.Headers.Referrer!.ToString());
    }

    [Fact]
    public async Task 클라이언트는_HTTP_오류를_예외로_던진다()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client = new KakaoMenuClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetTodayImageUrlAsync(SourceUrl, Today, Tz, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 클라이언트는_https가_아닌_출처를_거절한다()
    {
        var client = new KakaoMenuClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage())));

        await Assert.ThrowsAsync<FormatException>(() =>
            client.GetTodayImageUrlAsync("http://pf.kakao.com/x", Today, Tz, TestContext.Current.CancellationToken));
    }

    // --- 수집기: 실패 격리 ---

    sealed class FakeClient(Func<string, string?> respond) : IMenuImageClient
    {
        public int Calls { get; private set; }

        public Task<string?> GetTodayImageUrlAsync(string sourceUrl, DateOnly today, string tz, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(sourceUrl));
        }
    }

    sealed class CapturingLogger : ILogger<MenuCollector>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    static async Task<Restaurant> AddAsync(
        TestDb fixture, string name, string? source, DateOnly? menuDate = null, string? menuUrl = null)
    {
        var restaurant = new Restaurant
        {
            Name = name, NormalizedName = Restaurant.Normalize(name), CategoryId = 1,
            Status = RestaurantStatus.Active, CreatedBySlackUserId = "U1",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
            MenuSourceUrl = source, TodayMenuDate = menuDate, TodayMenuImageUrl = menuUrl,
        };
        fixture.Db.Restaurants.Add(restaurant);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return restaurant;
    }

    [Fact]
    public async Task 수집에_성공하면_이미지와_날짜를_저장한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await AddAsync(fixture, "광교다인푸드", SourceUrl);
        await AddAsync(fixture, "출처없는집", null);
        var client = new FakeClient(_ => "https://k.kakaocdn.net/a.jpg");
        var collector = new MenuCollector(fixture.Db, client, NullLogger<MenuCollector>.Instance);

        var collected = await collector.CollectAsync(Today, Tz, ct);

        Assert.Equal(1, collected);
        Assert.Equal(1, client.Calls);   // 출처 없는 식당은 호출하지 않는다
        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct);
        Assert.Equal("https://k.kakaocdn.net/a.jpg", saved.TodayMenuImageUrl);
        Assert.Equal(Today, saved.TodayMenuDate);
    }

    [Fact]
    public async Task 오늘자를_이미_확보했으면_다시_호출하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await AddAsync(fixture, "광교다인푸드", SourceUrl, Today, "https://k.kakaocdn.net/a.jpg");
        var client = new FakeClient(_ => "https://k.kakaocdn.net/new.jpg");
        var collector = new MenuCollector(fixture.Db, client, NullLogger<MenuCollector>.Instance);

        var collected = await collector.CollectAsync(Today, Tz, TestContext.Current.CancellationToken);

        Assert.Equal(0, collected);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task 어제_수집분은_오늘_다시_수집_대상이다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await AddAsync(fixture, "광교다인푸드", SourceUrl, Today.AddDays(-1), "https://k.kakaocdn.net/old.jpg");
        var client = new FakeClient(_ => "https://k.kakaocdn.net/new.jpg");
        var collector = new MenuCollector(fixture.Db, client, NullLogger<MenuCollector>.Instance);

        Assert.Equal(1, await collector.CollectAsync(Today, Tz, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 오늘_글이_없으면_저장하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await AddAsync(fixture, "광교다인푸드", SourceUrl);
        var collector = new MenuCollector(fixture.Db, new FakeClient(_ => null), NullLogger<MenuCollector>.Instance);

        Assert.Equal(0, await collector.CollectAsync(Today, Tz, ct));

        fixture.Db.ChangeTracker.Clear();
        var saved = await fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct);
        Assert.Null(saved.TodayMenuImageUrl);
        Assert.Null(saved.TodayMenuDate);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]   // HttpClient 타임아웃은 이 예외로 온다
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task 수집이_어떤_방식으로_실패해도_예외를_던지지_않고_경고만_남긴다(Type exceptionType)
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await AddAsync(fixture, "광교다인푸드", SourceUrl);
        var logger = new CapturingLogger();
        var collector = new MenuCollector(
            fixture.Db, new FakeClient(_ => throw (Exception)Activator.CreateInstance(exceptionType)!), logger);

        var collected = await collector.CollectAsync(Today, Tz, ct);

        Assert.Equal(0, collected);
        Assert.Contains(LogLevel.Warning, logger.Levels);
        fixture.Db.ChangeTracker.Clear();
        Assert.Null((await fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct)).TodayMenuDate);
    }

    [Fact]
    public async Task 한_곳이_실패해도_다음_식당은_계속_수집한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddAsync(fixture, "깨진곳", "https://broken.example/x");
        var good = await AddAsync(fixture, "정상곳", "https://ok.example/x");
        var client = new FakeClient(u =>
            u.Contains("broken") ? throw new HttpRequestException() : "https://k.kakaocdn.net/a.jpg");
        var collector = new MenuCollector(fixture.Db, client, NullLogger<MenuCollector>.Instance);

        Assert.Equal(1, await collector.CollectAsync(Today, Tz, ct));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(Today, (await fixture.Db.Restaurants.SingleAsync(r => r.Id == good.Id, ct)).TodayMenuDate);
    }

    // --- 표시 ---

    [Fact]
    public void 메뉴_이미지가_있으면_alt_text가_있는_ImageBlock을_붙이고_없으면_붙이지_않는다()
    {
        IReadOnlyList<RestaurantInfo> candidates =
            [new(1, "광교다인푸드", 1, "한식", null, DateTimeOffset.UnixEpoch)];
        var closesAt = new DateTimeOffset(2026, 9, 29, 11, 0, 0, TimeSpan.FromHours(9));

        var with = PollBlocks.Build(1, candidates, [], [], closesAt,
            menuImages: [new MenuImage("광교다인푸드", "https://k.kakaocdn.net/a.jpg")]);
        var without = PollBlocks.Build(1, candidates, [], [], closesAt);

        var image = Assert.Single(with.OfType<ImageBlock>());
        Assert.Equal("https://k.kakaocdn.net/a.jpg", image.ImageUrl);
        Assert.False(string.IsNullOrWhiteSpace(image.AltText));
        Assert.Empty(without.OfType<ImageBlock>());
    }

    [Fact]
    public async Task 투표_메시지는_투표_날짜의_메뉴만_붙이고_지난_메뉴는_붙이지_않는다()
    {
        var pollDate = new DateOnly(2026, 9, 29);
        var opensAt = new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.FromHours(9));
        await using var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, "C1");
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, "C1");

        await AddAsync(fixture, "오늘메뉴집", SourceUrl, pollDate, "https://k.kakaocdn.net/today.jpg");
        await AddAsync(fixture, "어제메뉴집", SourceUrl, pollDate.AddDays(-1), "https://k.kakaocdn.net/yesterday.jpg");
        var poll = await service.OpenPollAsync(pollDate, opensAt, opensAt.AddMinutes(30), ct);

        await announcer.PostPollAsync(poll.Id, poll.ClosesAt, ct);

        var image = Assert.Single(slack.ChatFake.PostedMessage!.Blocks.OfType<ImageBlock>());
        Assert.Equal("https://k.kakaocdn.net/today.jpg", image.ImageUrl);
        Assert.Contains("오늘메뉴집", image.AltText);
    }
}
