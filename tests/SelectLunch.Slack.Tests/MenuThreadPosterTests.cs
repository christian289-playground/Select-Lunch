using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Services;

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

    sealed class Harness(TestDb fixture, FakeSlackApiClient slack, FakeDownloader downloader, MenuThreadPoster poster)
        : IAsyncDisposable
    {
        public TestDb Fixture { get; } = fixture;
        public FakeSlackApiClient Slack { get; } = slack;
        public FakeDownloader Downloader { get; } = downloader;
        public MenuThreadPoster Poster { get; } = poster;

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    static async Task<Harness> SetupAsync(Func<string, byte[]>? download = null)
    {
        var fixture = await TestDb.CreateAsync();
        var slack = new FakeSlackApiClient();
        var downloader = new FakeDownloader(download ?? (_ => Bytes));
        var poster = new MenuThreadPoster(
            slack, fixture.Db, downloader, Channel, NullLogger<MenuThreadPoster>.Instance);
        return new Harness(fixture, slack, downloader, poster);
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

    static async Task<LunchPoll> AddPollAsync(TestDb fixture, string? messageTs)
    {
        var poll = new LunchPoll
        {
            ChannelId = Channel,
            Date = Today,
            OpensAt = OpensAt,
            ClosesAt = OpensAt.AddMinutes(30),
            MessageTs = messageTs,
        };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return poll;
    }

    // --- 게시 조건 네 가지 ---

    [Fact]
    public async Task 조건이_모두_갖춰지면_투표_메시지_스레드에_파일로_올린다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(1, await h.Poster.PostAsync(Today, Now, Options, ct));

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
        var ct = TestContext.Current.CancellationToken;
        await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
        Assert.Empty(h.Downloader.RequestedUrls);   // 올릴 곳이 없으면 내려받지도 않는다
    }

    [Fact]
    public async Task 투표_메시지가_아직_안_나갔으면_올리지_않는다()
    {
        // 풀 행은 있지만 MessageTs가 null인 재시작 복구 상황. 스레드를 걸 ts가 없다.
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, null);
        await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 어제_수집한_메뉴는_올리지_않는다()
    {
        // 지난 메뉴를 오늘 것처럼 올리는 것이 아예 안 올리는 것보다 나쁘다.
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        await AddRestaurantAsync(h.Fixture, Today.AddDays(-1));

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 수집된_이미지가_없으면_올리지_않는다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        await AddRestaurantAsync(h.Fixture, Today, imageUrl: null);

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 이미_올린_메뉴는_다시_올리지_않는다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        await AddRestaurantAsync(h.Fixture, Today, postedAt: Now.AddMinutes(-1));

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
    }

    [Theory]
    [InlineData(13, 29, 1)]
    [InlineData(13, 30, 0)]   // 식사 기록 시각부터는 올리지 않는다
    [InlineData(15, 0, 0)]
    public async Task 식사_기록_시각부터는_올리지_않는다(int hour, int minute, int expected)
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        await AddRestaurantAsync(h.Fixture, Today);
        var now = new DateTimeOffset(2026, 9, 29, hour, minute, 0, TimeSpan.FromHours(9));

        Assert.Equal(expected, await h.Poster.PostAsync(Today, now, Options, ct));
    }

    [Fact]
    public async Task 식사_기록_시각_설정을_늦추면_그만큼_더_올릴_수_있다()
    {
        // 13:30을 상수로 박아 두면 여기서 잡힌다.
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        await AddRestaurantAsync(h.Fixture, Today);
        var options = new LunchOptions { VoteOpenAt = new TimeOnly(11, 0), MealRecordAt = new TimeOnly(14, 30) };
        var now = new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.FromHours(9));

        Assert.Equal(1, await h.Poster.PostAsync(Today, now, options, ct));
    }

    // --- 중복 방지와 재시도 ---

    [Fact]
    public async Task 업로드에_성공하면_시각을_찍고_다음_주기에는_다시_올리지_않는다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        var restaurant = await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(1, await h.Poster.PostAsync(Today, Now, Options, ct));

        h.Fixture.Db.ChangeTracker.Clear();
        var saved = await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct);
        Assert.Equal(Now, saved.TodayMenuPostedAt);

        // 두 번째 tick — 같은 파일이 또 올라가면 안 된다.
        Assert.Equal(0, await h.Poster.PostAsync(Today, Now.AddSeconds(60), Options, ct));
        Assert.Single(h.Slack.FilesFake.Uploads);
    }

    [Fact]
    public async Task 업로드에_실패하면_시각을_찍지_않아_다음_주기에_다시_시도한다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        var restaurant = await AddRestaurantAsync(h.Fixture, Today);
        h.Slack.FilesFake.FailWith = () => new InvalidOperationException("upload_failed");

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Null((await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct)).TodayMenuPostedAt);

        // 다음 주기에 슬랙이 회복되면 그대로 올라간다.
        h.Slack.FilesFake.FailWith = null;
        Assert.Equal(1, await h.Poster.PostAsync(Today, Now.AddSeconds(60), Options, ct));
        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Equal(
            Now.AddSeconds(60),
            (await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct)).TodayMenuPostedAt);
    }

    [Fact]
    public async Task 다운로드에_실패해도_예외를_던지지_않고_다음_주기에_다시_시도한다()
    {
        await using var h = await SetupAsync(_ => throw new HttpRequestException("404"));
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        var restaurant = await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Null((await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == restaurant.Id, ct)).TodayMenuPostedAt);
    }

    [Fact]
    public async Task 한_곳이_실패해도_다른_식당의_메뉴는_올라간다()
    {
        await using var h = await SetupAsync(url =>
            url.Contains("broken") ? throw new HttpRequestException("404") : Bytes);
        var ct = TestContext.Current.CancellationToken;
        await AddPollAsync(h.Fixture, "1700000000.000100");
        var broken = await AddRestaurantAsync(
            h.Fixture, Today, "https://k.kakaocdn.net/broken.jpg", name: "깨진곳");
        var good = await AddRestaurantAsync(h.Fixture, Today, name: "정상곳");

        Assert.Equal(1, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Single(h.Slack.FilesFake.Uploads);
        h.Fixture.Db.ChangeTracker.Clear();
        Assert.Null((await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == broken.Id, ct)).TodayMenuPostedAt);
        Assert.Equal(Now, (await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == good.Id, ct)).TodayMenuPostedAt);
    }

    [Fact]
    public async Task 다른_채널의_투표는_스레드_대상이_아니다()
    {
        await using var h = await SetupAsync();
        var ct = TestContext.Current.CancellationToken;
        h.Fixture.Db.Polls.Add(new LunchPoll
        {
            ChannelId = "C-OTHER", Date = Today, OpensAt = OpensAt, ClosesAt = OpensAt.AddMinutes(30),
            MessageTs = "1700000000.000999",
        });
        await h.Fixture.Db.SaveChangesAsync(ct);
        await AddRestaurantAsync(h.Fixture, Today);

        Assert.Equal(0, await h.Poster.PostAsync(Today, Now, Options, ct));

        Assert.Empty(h.Slack.FilesFake.Uploads);
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
