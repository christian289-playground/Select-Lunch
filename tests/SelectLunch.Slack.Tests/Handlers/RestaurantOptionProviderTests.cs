using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Search;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// <c>block_suggestion</c> 응답. 두 드롭다운이 서로 다른 모집단을 쓰므로
/// action_id로 갈라야 한다 — 잘못 가르면 투표 제안에 후보 아닌 식당이 섞인다.
/// </summary>
public class RestaurantOptionProviderTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);

    static async Task<TestDb> SeedAsync()
    {
        var fixture = await TestDb.CreateAsync();

        // 카테고리 1 한식 · 3 일식 · 5 분식은 마이그레이션 시드로 이미 들어 있다.
        fixture.Db.Restaurants.AddRange(
            Restaurant(10, "옛날경성순대국 광교중앙역점", 1, "수원시 영통구 광교중앙로 123"),
            Restaurant(20, "스시로", 3, "성남시 분당구 판교역로 7"),
            Restaurant(30, "김밥천국", 5, null));

        fixture.Db.Polls.Add(new LunchPoll
        {
            Id = 1, ChannelId = Channel, Date = Today,
            OpensAt = DateTimeOffset.UnixEpoch, ClosesAt = DateTimeOffset.UnixEpoch.AddHours(1),
            Status = PollStatus.Open,
        });
        // 후보 스냅샷에는 10·20만 들어 있다 — 30은 투표가 열린 뒤에 등록된 셈이다.
        fixture.Db.PollCandidates.AddRange(
            new PollCandidate { PollId = 1, RestaurantId = 10, DisplayOrder = 0 },
            new PollCandidate { PollId = 1, RestaurantId = 20, DisplayOrder = 1 });

        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    static Restaurant Restaurant(long id, string name, long categoryId, string? address) => new()
    {
        Id = id, Name = name, NormalizedName = Shared.Entities.Restaurant.Normalize(name),
        CategoryId = categoryId, Status = RestaurantStatus.Active, Address = address,
        CreatedBySlackUserId = "U1",
        CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static BlockOptionsRequest Request(string actionId, string? value) =>
        new() { ActionId = actionId, Value = value };

    static string[] Values(BlockOptionsResponse response) =>
        [.. (response.OptionGroups ?? []).SelectMany(g => g.Options).Select(o => o.Value)];

    [Fact]
    public async Task 수정_목록은_분류_미완성도_포함하고_보관된_식당은_제외한다()
    {
        await using var fixture = await SeedAsync();
        var pending = Restaurant(40, "분류전가게", 1, "광교");
        pending.CategoryId = null;
        pending.Status = RestaurantStatus.Pending;
        var archived = Restaurant(50, "보관가게", 1, null);
        archived.Status = RestaurantStatus.Archived;
        fixture.Db.Restaurants.AddRange(pending, archived);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.RestaurantEditSelect, ""));

        Assert.Equal(new[] { "10", "20", "30", "40" }, Values(response).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "40" }, Values(await provider.GetOptions(Request(ActionIds.RestaurantEditSelect, "미분류"))));
        Assert.Equal(new[] { "10" }, Values(await provider.GetOptions(Request(ActionIds.RestaurantEditSelect, "순대국"))));
        Assert.Equal(new[] { "20" }, Values(await provider.GetOptions(Request(ActionIds.RestaurantEditSelect, "분당"))));
    }

    [Fact]
    public async Task 수정_검색으로_첫_백개_목록_밖의_식당도_선택할_수_있다()
    {
        await using var fixture = await SeedAsync();
        for (var i = 0; i < 110; i++)
            fixture.Db.Restaurants.Add(Restaurant(1000 + i, $"가게{i:D3}", 1, null));
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provider = new RestaurantOptionProvider(fixture.Db);

        var initial = await provider.GetOptions(Request(ActionIds.RestaurantEditSelect, ""));
        Assert.DoesNotContain("1109", Values(initial));
        var searched = await provider.GetOptions(Request(ActionIds.RestaurantEditSelect, "가게109"));
        Assert.Equal(new[] { "1109" }, Values(searched));
    }

    [Fact]
    public async Task 투표_드롭다운은_음식_이름으로_후보를_찾아준다()
    {
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.VoteSelect(1), "순대국"));

        Assert.Equal(["10"], Values(response));
    }

    [Fact]
    public async Task 투표_드롭다운은_후보_스냅샷_밖의_식당을_제안하지_않는다()
    {
        // Active 전체에서 뽑으면 "김밥천국"이 끼어들고, 그걸 고른 표는
        // 후보에 없는 식당으로 들어가 집계에서 증발한다.
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.VoteSelect(1), "김밥"));

        Assert.Empty(response.Options);
    }

    [Fact]
    public async Task 기록_드롭다운은_Active_전체에서_찾는다()
    {
        // 오늘 점심은 투표 후보가 아니었던 곳에서 먹었을 수도 있다.
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.Meal(Today, 0), "김밥"));

        Assert.Equal(["30"], Values(response));
    }

    [Fact]
    public async Task 카테고리_이름으로도_찾힌다()
    {
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.Meal(Today, 0), "일식"));

        Assert.Equal(["20"], Values(response));
    }

    [Fact]
    public async Task 주소로도_찾힌다()
    {
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.Meal(Today, 0), "분당"));

        Assert.Equal(["20"], Values(response));
    }

    [Fact]
    public async Task 검색어가_비면_모집단_전체를_돌려준다()
    {
        // MinQueryLength = 0이라 드롭다운을 열자마자 빈 검색어로 한 번 불린다.
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        Assert.Equal(3, Values(await provider.GetOptions(Request(ActionIds.Meal(Today, 0), ""))).Length);
        Assert.Equal(2, Values(await provider.GetOptions(Request(ActionIds.VoteSelect(1), null))).Length);
    }

    [Fact]
    public async Task 응답은_카테고리_제목_아래로_묶이고_라벨에는_카테고리를_반복하지_않는다()
    {
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.Meal(Today, 0), ""));

        Assert.Null(response.Options);
        Assert.Equal(["분식", "일식", "한식"], response.OptionGroups.Select(g => g.Label.Text));
        var sushi = response.OptionGroups.Single(g => g.Label.Text == "일식").Options.Single();
        Assert.Equal("스시로", sushi.Text.Text);
        Assert.Equal("20", sushi.Value);
        Assert.All(response.OptionGroups.SelectMany(g => g.Options), o =>
            Assert.True(o.Text.Text.Length <= RestaurantSearch.MaxLabelLength));
    }

    [Fact]
    public async Task 모르는_action_id에는_빈_목록을_돌려준다()
    {
        // 예외를 던지면 사용자 화면에 슬랙 오류가 뜬다. 조용히 비우는 쪽이 맞다.
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        Assert.Empty((await provider.GetOptions(Request("category_select", "한"))).Options);
        Assert.Empty((await provider.GetOptions(Request("", "한"))).Options);
        Assert.Empty((await provider.GetOptions(Request("vote:1:2", "한"))).Options);
    }

    [Fact]
    public async Task 없는_풀의_제안은_빈_목록이다()
    {
        // 며칠 지난 메시지의 드롭다운을 열어도 터지지 않아야 한다.
        await using var fixture = await SeedAsync();
        var provider = new RestaurantOptionProvider(fixture.Db);

        var response = await provider.GetOptions(Request(ActionIds.VoteSelect(999), ""));

        Assert.Empty(response.Options);
    }

    [Fact]
    public async Task 후보가_백개를_넘어도_응답은_백개를_넘지_않는다()
    {
        // 외부 선택이라 식당 수 자체에는 상한이 없어졌지만, **한 응답**의 100개 상한은
        // 그대로다. 넘겨 보내면 제안 응답이 거부돼 드롭다운이 통째로 비어 보인다.
        await using var fixture = await SeedAsync();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 150; i++)
            fixture.Db.Restaurants.Add(Restaurant(1000 + i, $"국밥{i:D3}", 1, null));
        await fixture.Db.SaveChangesAsync(ct);

        var provider = new RestaurantOptionProvider(fixture.Db);
        var response = await provider.GetOptions(Request(ActionIds.Meal(Today, 0), ""));

        Assert.Equal(RestaurantSearch.MaxOptions, Values(response).Length);
    }
}
