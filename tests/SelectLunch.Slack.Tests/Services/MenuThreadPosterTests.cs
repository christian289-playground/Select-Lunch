using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Services;
using SelectLunch.Slack.Workers;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 오늘의 메뉴를 투표 메시지 스레드에 파일로 올리는 경로. 투표 메시지 본문은
/// 절대 건드리지 않으므로, 여기서 검증할 것은 "언제 올리는가"와 "두 번 올리지 않는가"다.
/// </summary>
public class MenuThreadPosterTests
{
    const string Channel = "C1";
    const string ImageUrl = "https://k.kakaocdn.net/menu.jpg";
    static readonly DateOnly Today = new(2026, 9, 29);          // 화요일
    static readonly DateTimeOffset OpensAt = new(2026, 9, 29, 11, 0, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset Now = new(2026, 9, 29, 11, 0, 30, TimeSpan.FromHours(9));
    static readonly LunchOptions Options = new() { VoteOpenAt = new TimeOnly(11, 0) };

    /// <summary>운영이 쓰는 스로틀 간격을 그대로 쓴다 — 값이 바뀌면 테스트도 같이 움직인다.</summary>
    static readonly TimeSpan Throttle = SchedulerWorker.MenuAttemptInterval;

    /// <summary>다운로드한 바이트. 페이크가 올린 내용과 같은지 비교하는 데 쓴다.</summary>
    static readonly byte[] Bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];

    sealed class FakeDownloader(Func<string, byte[]> respond) : IMenuImageDownloader
    {
        public List<string> RequestedUrls { get; } = [];

        public Task<byte[]> DownloadAsync(string imageUrl, CancellationToken ct)
        {
            RequestedUrls.Add(imageUrl);
            return Task.FromResult(respond(imageUrl));
        }
    }

    sealed class Harness(
        TestDb fixture, LunchService service, FakeSlackApiClient slack,
        FakeDownloader downloader, MenuThreadPoster poster)
        : IAsyncDisposable
    {
        public TestDb Fixture { get; } = fixture;
        public LunchService Service { get; } = service;
        public FakeSlackApiClient Slack { get; } = slack;
        public FakeDownloader Downloader { get; } = downloader;
        public MenuThreadPoster Poster { get; } = poster;

        /// <summary>스케줄러의 인메모리 스로틀에 해당한다 — 테스트 하나가 하나를 소유한다.</summary>
        public AttemptThrottle Throttle { get; } = new(MenuThreadPosterTests.Throttle);

        public Task<int> PostAsync(DateTimeOffset now, LunchOptions? options = null) =>
            Poster.PostAsync(Today, now, options ?? Options, Throttle, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    static async Task<Harness> SetupAsync(Func<string, byte[]>? download = null)
    {
        var fixture = await TestDb.CreateAsync();
        var slack = new FakeSlackApiClient();
        var downloader = new FakeDownloader(download ?? (_ => Bytes));
        var poster = new MenuThreadPoster(
            slack, fixture.Db, downloader, Channel, NullLogger<MenuThreadPoster>.Instance);
        return new Harness(fixture, new LunchService(fixture.Db, Channel), slack, downloader, poster);
    }

    /// <summary>게시 조건이 전부 갖춰진 기본 상태를 만든다. 각 테스트가 하나씩만 어긋뜨린다.</summary>
    static async Task<Restaurant> AddRestaurantAsync(
        TestDb fixture, DateOnly? menuDate, string? imageUrl = ImageUrl, DateTimeOffset? postedAt = null,
        string name = "광교다인푸드")
    {
        var restaurant = new Restaurant
        {
            Name = name,
            NormalizedName = Restaurant.Normalize(name),
            CategoryId = 1,
            Status = RestaurantStatus.Active,
            CreatedBySlackUserId = "U1",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            MenuSourceUrl = "https://pf.kakao.com/x/posts",
            TodayMenuDate = menuDate,
            TodayMenuImageUrl = imageUrl,
            TodayMenuPostedAt = postedAt,
        };
        fixture.Db.Restaurants.Add(restaurant);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return restaurant;
    }

    /// <summary>
    /// 투표를 연다. 후보는 이 시점의 Active 식당으로 고정되므로, 후보가 되어야 할
    /// 식당은 반드시 이 호출 **전에** 만들어 두어야 한다.
    /// </summary>
    static async Task<LunchPoll> OpenPollAsync(Harness h, string? messageTs = "1700000000.000100")
    {
        var ct = TestContext.Current.CancellationToken;
        var poll = await h.Service.OpenPollAsync(Today, OpensAt, OpensAt.AddMinutes(30), ct);
        poll.MessageTs = messageTs;
        await h.Fixture.Db.SaveChangesAsync(ct);
        return poll;
    }

    // --- 게시 조건 ---

    [Fact]
    public async Task 조건이_모두_갖춰지면_투표_메시지_스레드에_파일로_올린다()
    {
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h);

        Assert.Equal(1, await h.PostAsync(Now));

        var upload = Assert.Single(h.Slack.FilesFake.Uploads);
        Assert.Equal(Channel, upload.ChannelId);
        // 스레드 대상은 투표 메시지 자신의 ts여야 한다.
        Assert.Equal("1700000000.000100", upload.ThreadTs);
        Assert.Equal("광교다인푸드 오늘의 메뉴", upload.InitialComment);
        Assert.Equal("광교다인푸드 오늘의 메뉴.jpg", upload.File.FileName);
        Assert.Equal("광교다인푸드 오늘의 메뉴", upload.File.Title);
        Assert.False(string.IsNullOrWhiteSpace(upload.File.AltText));
        // 내려받은 바이트를 그대로 올려야 한다 — URL 문자열이나 빈 배열을 올리면 여기서 잡힌다.
        Assert.Equal(Bytes, upload.Content);
        Assert.Equal([ImageUrl], h.Downloader.RequestedUrls);
        // 메시지 본문은 손대지 않는다.
        Assert.Equal(0, h.Slack.ChatFake.PostCallCount);
        Assert.Equal(0, h.Slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 오늘_투표가_없으면_올리지_않는다()
    {
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
        Assert.Empty(h.Downloader.RequestedUrls);   // 올릴 곳이 없으면 내려받지도 않는다
    }

    [Fact]
    public async Task 투표_메시지가_아직_안_나갔으면_올리지_않는다()
    {
        // 풀 행은 있지만 MessageTs가 null인 재시작 복구 상황. 스레드를 걸 ts가 없다.
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h, messageTs: null);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 어제_수집한_메뉴는_올리지_않는다()
    {
        // 지난 메뉴를 오늘 것처럼 올리는 것이 아예 안 올리는 것보다 나쁘다.
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today.AddDays(-1));
        await OpenPollAsync(h);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 수집된_이미지가_없으면_올리지_않는다()
    {
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today, imageUrl: null);
        await OpenPollAsync(h);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 이미_올린_메뉴는_다시_올리지_않는다()
    {
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today, postedAt: Now.AddMinutes(-1));
        await OpenPollAsync(h);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 투표_후보가_아닌_식당의_메뉴는_올리지_않는다()
    {
        // 후보 스냅샷은 개시 시점에 고정된다. 개시 뒤에 등록된 식당은 이번 투표에서
        // 아무도 고를 수 없으므로, 그 사진은 투표 스레드에서 소음일 뿐이다.
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today, name: "후보인집");
        await OpenPollAsync(h);
        await AddRestaurantAsync(h.Fixture, Today, name: "나중에등록한집");

        Assert.Equal(1, await h.PostAsync(Now));

        var upload = Assert.Single(h.Slack.FilesFake.Uploads);
        Assert.Equal("후보인집 오늘의 메뉴", upload.InitialComment);
    }

    [Theory]
    [InlineData(13, 29, 1)]
    [InlineData(13, 30, 0)]   // 식사 기록 시각부터는 올리지 않는다
    [InlineData(15, 0, 0)]
    public async Task 식사_기록_시각부터는_올리지_않는다(int hour, int minute, int expected)
    {
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h);

        Assert.Equal(expected, await h.PostAsync(new DateTimeOffset(2026, 9, 29, hour, minute, 0, TimeSpan.FromHours(9))));
    }

    [Fact]
    public async Task 식사_기록_시각_설정을_늦추면_그만큼_더_올릴_수_있다()
    {
        // 13:30을 상수로 박아 두면 여기서 잡힌다.
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h);
        var options = new LunchOptions { VoteOpenAt = new TimeOnly(11, 0), MealRecordAt = new TimeOnly(14, 30) };

        Assert.Equal(1, await h.PostAsync(new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.FromHours(9)), options));
    }

    [Fact]
    public async Task 다른_채널의_투표는_스레드_대상이_아니다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddRestaurantAsync(h.Fixture, Today);
        var other = new LunchService(h.Fixture.Db, "C-OTHER");
        var poll = await other.OpenPollAsync(Today, OpensAt, OpensAt.AddMinutes(30), ct);
        poll.MessageTs = "1700000000.000999";
        await h.Fixture.Db.SaveChangesAsync(ct);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    // --- 중복 방지와 재시도 스로틀 ---

    [Fact]
    public async Task 업로드에_성공하면_시각을_찍고_다음_주기에는_다시_올리지_않는다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h);

        Assert.Equal(1, await h.PostAsync(Now));

        h.Fixture.Db.ChangeTracker.Clear();
        var saved = await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct);
        Assert.Equal(Now, saved.TodayMenuPostedAt);

        // 스로틀이 한참 지난 뒤에 다시 돌려도 올라가면 안 된다 — 여기서 재게시를 막는
        // 것은 스로틀이 아니라 TodayMenuPostedAt이어야 한다.
        Assert.Equal(0, await h.PostAsync(Now + Throttle + TimeSpan.FromMinutes(1)));
        Assert.Single(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 실패_후_스로틀이_지나기_전에는_재시도하지_않고_지난_뒤에는_재시도한다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h);
        h.Slack.FilesFake.FailWith = () => new InvalidOperationException("upload_failed");

        Assert.Equal(0, await h.PostAsync(Now));
        Assert.Single(h.Downloader.RequestedUrls);   // 한 번은 실제로 시도했다

        // 슬랙이 곧바로 회복돼도 스로틀 안에서는 손대지 않는다.
        h.Slack.FilesFake.FailWith = null;
        Assert.Equal(0, await h.PostAsync(Now + Throttle - TimeSpan.FromSeconds(1)));
        Assert.Empty(h.Slack.FilesFake.Uploads);
        Assert.Single(h.Downloader.RequestedUrls);   // 이미지를 다시 내려받지도 않았다

        // 스로틀이 지나면 그대로 재시도한다.
        var retryAt = Now + Throttle;
        Assert.Equal(1, await h.PostAsync(retryAt));
        Assert.Single(h.Slack.FilesFake.Uploads);
        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            retryAt, (await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct)).TodayMenuPostedAt);
    }

    [Fact]
    public async Task 올릴_것이_없는_주기는_스로틀을_쓰지_않는다()
    {
        // 투표 개시 직전의 빈 tick이 스로틀을 소진하면, 정작 개시 직후 게시가 5분 밀린다.
        await using var h = await SetupAsync();
        await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(0, await h.PostAsync(Now));   // 아직 투표가 없다

        await OpenPollAsync(h);

        // 곧바로 다음 주기 — 스로틀에 걸리지 않고 올라가야 한다.
        Assert.Equal(1, await h.PostAsync(Now.AddSeconds(60)));
    }

    [Fact]
    public async Task 다운로드에_실패해도_예외를_던지지_않고_스로틀_뒤에_다시_시도한다()
    {
        var fail = true;
        await using var h = await SetupAsync(_ => fail ? throw new HttpRequestException("404") : Bytes);
        var ct = TestContext.Current.CancellationToken;
        var restaurant = await AddRestaurantAsync(h.Fixture, Today);
        await OpenPollAsync(h);

        Assert.Equal(0, await h.PostAsync(Now));

        Assert.Empty(h.Slack.FilesFake.Uploads);
        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Null((await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct)).TodayMenuPostedAt);

        fail = false;
        Assert.Equal(1, await h.PostAsync(Now + Throttle));
    }

    [Fact]
    public async Task 한_곳이_실패해도_다른_식당의_메뉴는_올라간다()
    {
        await using var h = await SetupAsync(url =>
            url.Contains("broken") ? throw new HttpRequestException("404") : Bytes);
        var ct = TestContext.Current.CancellationToken;
        var broken = await AddRestaurantAsync(
            h.Fixture, Today, "https://k.kakaocdn.net/broken.jpg", name: "깨진곳");
        var good = await AddRestaurantAsync(h.Fixture, Today, name: "정상곳");
        await OpenPollAsync(h);

        Assert.Equal(1, await h.PostAsync(Now));

        Assert.Single(h.Slack.FilesFake.Uploads);
        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Null((await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == broken.Id, ct)).TodayMenuPostedAt);
        Assert.Equal(Now, (await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == good.Id, ct)).TodayMenuPostedAt);
    }

    // --- 파일 이름 ---

    [Theory]
    [InlineData("https://k.kakaocdn.net/menu.jpg", "광교다인푸드 오늘의 메뉴.jpg")]
    [InlineData("https://k.kakaocdn.net/menu.PNG", "광교다인푸드 오늘의 메뉴.png")]
    [InlineData("https://k.kakaocdn.net/menu.webp", "광교다인푸드 오늘의 메뉴.webp")]
    [InlineData("https://k.kakaocdn.net/img/abcdef", "광교다인푸드 오늘의 메뉴.jpg")]
    [InlineData("https://k.kakaocdn.net/menu.jpg?w=800", "광교다인푸드 오늘의 메뉴.jpg")]
    [InlineData("https://k.kakaocdn.net/dn/abc/img.php", "광교다인푸드 오늘의 메뉴.jpg")]
    public void 파일_이름은_이미지_확장자를_살리고_모르는_형식은_jpg로_본다(string url, string expected)
    {
        Assert.Equal(expected, MenuThreadPoster.FileNameFor("광교다인푸드", url));
    }
}
