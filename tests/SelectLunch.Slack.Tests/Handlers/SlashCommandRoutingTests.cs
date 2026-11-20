using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 슬랙이 실제로 보내는 payload(Command=명령 이름, Text=명령 뒤 문자열) 기준의 라우팅 계약.
/// 기능은 Command로 정하고 식당 이름·기준 인원 등 각 명령의 인자만 Text에서 읽는다.
/// </summary>
public sealed class SlashCommandRoutingTests
{
    const string Channel = "C1";
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly string[] FeatureCommands =
    [
        "/lunchadd", "/lunchedit", "/lunchlist", "/lunchpending",
        "/lunchstats", "/lunchtoday", "/lunchpre", "/lunchtest", "/lunchsize",
    ];

    static IOptionsMonitor<LunchOptions> Monitor(LunchOptions settings) =>
        StubApi.Of<IOptionsMonitor<LunchOptions>>(("get_CurrentValue", (Func<LunchOptions>)(() => settings)));

    static LunchSlashCommandHandler Commands(TestDb fixture, FakeSlackApiClient slack) =>
        Commands(fixture.Db, slack, new LunchOptions());

    static LunchSlashCommandHandler Commands(LunchDbContext db, FakeSlackApiClient slack, LunchOptions settings,
        string channelId = Channel) => new(
        db, slack, Monitor(settings), TestPollLeaveRoster.Disabled,
        Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = channelId }),
        AdvanceVoteTestSupport.Handler(db, slack, channelId: channelId));

    static SlashCommand Invoke(string? command, string? text = "") => new()
    {
        Command = command!, Text = text!, TriggerId = "T1", UserId = "U1", ChannelId = Channel,
    };

    static Task<Restaurant> SeedAsync(TestDb fixture, string name, long? categoryId = 1) =>
        new LunchService(fixture.Db, Channel).SaveRestaurantAsync(
            new(null, name, categoryId, null, null, null), "U1", Ct);

    static string? NameOf(ModalViewDefinition view) =>
        Assert.IsType<PlainTextInput>(view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.Name).Element).InitialValue;

    static Task<string[]> RestaurantNamesAsync(TestDb fixture) =>
        fixture.Db.Restaurants.OrderBy(r => r.Id).Select(r => r.Name).ToArrayAsync(Ct);

    [Theory]
    [InlineData("edit 식당", "edit 식당")]
    [InlineData("add", "add")]
    [InlineData("  KFC  강남점  ", "KFC  강남점")]
    public async Task 등록_명령은_입력_전체를_식당_이름으로_등록창에_채운다(string text, string expectedName)
    {
        await using var fixture = await TestDb.CreateAsync();
        var slack = new FakeSlackApiClient();

        var response = await Commands(fixture, slack).Handle(Invoke("/lunchadd", text));

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.Equal(expectedName, NameOf(view));
        Assert.Equal(ModalContext.None, ModalContext.Parse(view.PrivateMetadata));
        Assert.NotNull(view.Submit);
        Assert.Equal(ResponseType.Ephemeral, response.ResponseType);
        Assert.Empty(await fixture.Db.Restaurants.ToArrayAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task 이름_없는_등록은_이름을_채우지_않은_빈_등록창을_연다(string? text)
    {
        await using var fixture = await TestDb.CreateAsync();
        var slack = new FakeSlackApiClient();

        await Commands(fixture, slack).Handle(Invoke("/lunchadd", text));

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.True(string.IsNullOrEmpty(NameOf(view)));
        Assert.NotNull(view.Submit);
    }

    [Theory]
    [InlineData("add 식당")]
    [InlineData("  ADD   식당 ")]
    public async Task 수정_명령은_구형_서브커맨드_단어로_시작하는_식당도_이름_전체로_찾는다(string text)
    {
        await using var fixture = await TestDb.CreateAsync();
        var target = await SeedAsync(fixture, "add 식당");
        await SeedAsync(fixture, "다른식당");
        var slack = new FakeSlackApiClient();

        await Commands(fixture, slack).Handle(Invoke("/lunchedit", text));

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.Equal(ModalContext.ForEdit(target.Id), ModalContext.Parse(view.PrivateMetadata));
        Assert.Equal("add 식당", NameOf(view));
        Assert.Equal(new[] { "add 식당", "다른식당" }, await RestaurantNamesAsync(fixture));
    }

    [Fact]
    public async Task 없는_식당_수정은_등록창을_열지_않고_목록_선택을_안내한다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "기존식당");
        var slack = new FakeSlackApiClient();

        var response = await Commands(fixture, slack).Handle(Invoke("/lunchedit", "없는식당"));

        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("/lunchedit", response.Message.Text);
        Assert.Equal(new[] { "기존식당" }, await RestaurantNamesAsync(fixture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("add 새식당")]
    [InlineData("edit 기존식당")]
    [InlineData("list")]
    [InlineData("pending")]
    [InlineData("stats")]
    [InlineData("today")]
    [InlineData("pre")]
    [InlineData("test")]
    [InlineData("size 4")]
    public async Task 루트_명령은_Text와_상관없이_도움말만_보여주고_아무것도_바꾸지_않는다(string text)
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "기존식당");
        var slack = new FakeSlackApiClient();
        var handler = Commands(fixture, slack);
        var help = await handler.Handle(Invoke("/lunch", ""));

        var response = await handler.Handle(Invoke("/lunch", text));

        Assert.Equal(ResponseType.Ephemeral, response.ResponseType);
        Assert.Equal(help.Message.Text, response.Message.Text);
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Null(slack.ChatFake.PostedMessage);
        Assert.Empty(await fixture.Db.Polls.ToArrayAsync(Ct));
        Assert.Equal(new[] { "기존식당" }, await RestaurantNamesAsync(fixture));
    }

    [Fact]
    public async Task 도움말은_각_기능을_붙여_쓴_독립_명령으로_안내한다()
    {
        await using var fixture = await TestDb.CreateAsync();

        var response = await Commands(fixture, new FakeSlackApiClient()).Handle(Invoke("/lunch"));

        foreach (var command in FeatureCommands)
            Assert.Contains($"`{command}", response.Message.Text);
        Assert.DoesNotContain("`/lunch ", response.Message.Text);
    }

    [Theory]
    [InlineData("/lunchdelete")]
    [InlineData("/lunchaddd")]
    [InlineData(null)]
    public async Task 알_수_없는_명령은_Text와_상관없이_도움말이며_아무것도_바꾸지_않는다(string? command)
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "기존식당");
        var slack = new FakeSlackApiClient();
        var handler = Commands(fixture, slack);
        var help = await handler.Handle(Invoke("/lunch", ""));

        var response = await handler.Handle(Invoke(command, "add 새식당"));

        Assert.Equal(ResponseType.Ephemeral, response.ResponseType);
        Assert.Equal(help.Message.Text, response.Message.Text);
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(new[] { "기존식당" }, await RestaurantNamesAsync(fixture));
    }

    [Theory]
    [InlineData("/lunchlist")]
    [InlineData("/lunchpending")]
    [InlineData("/lunchstats")]
    [InlineData("/lunchtoday")]
    [InlineData("/lunchpre")]
    [InlineData("/lunchtest")]
    public async Task 이름을_받지_않는_명령은_뒤에_글자를_붙여도_식당을_등록하거나_창을_열지_않는다(string command)
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "기존식당");
        var slack = new FakeSlackApiClient();
        var handler = Commands(fixture, slack);
        var help = await handler.Handle(Invoke("/lunch", ""));

        var response = await handler.Handle(Invoke(command, "add 새식당"));

        Assert.Equal(ResponseType.Ephemeral, response.ResponseType);
        Assert.NotEqual(help.Message.Text, response.Message.Text);
        Assert.Equal(0, slack.ViewsFake.OpenCallCount);
        Assert.Equal(new[] { "기존식당" }, await RestaurantNamesAsync(fixture));
    }

    [Fact]
    public async Task 목록_명령은_추천_대상_식당만_카테고리별로_보여준다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "첫식당", categoryId: 1);
        await SeedAsync(fixture, "일식당", categoryId: 3);
        await SeedAsync(fixture, "무명식당", categoryId: null);

        var response = await Commands(fixture, new FakeSlackApiClient()).Handle(Invoke("/lunchlist"));

        Assert.Contains("*한식*\n• 첫식당", response.Message.Text);
        Assert.Contains("*일식*\n• 일식당", response.Message.Text);
        Assert.DoesNotContain("무명식당", response.Message.Text);
    }

    [Fact]
    public async Task 보완_명령은_카테고리_없는_식당을_보여주고_안내한_수정_명령이_그_식당을_연다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var pending = await SeedAsync(fixture, "무명식당", categoryId: null);
        await SeedAsync(fixture, "한식당", categoryId: 1);
        var slack = new FakeSlackApiClient();
        var handler = Commands(fixture, slack);

        var response = await handler.Handle(Invoke("/lunchpending"));

        Assert.Contains("무명식당", response.Message.Text);
        Assert.DoesNotContain("한식당", response.Message.Text);
        Assert.Contains("/lunchedit", response.Message.Text);

        await handler.Handle(Invoke("/lunchedit", "무명식당"));

        var view = Assert.IsType<ModalViewDefinition>(slack.ViewsFake.LastView);
        Assert.Equal(ModalContext.ForEdit(pending.Id), ModalContext.Parse(view.PrivateMetadata));
        Assert.Equal("무명식당", NameOf(view));
    }

    [Fact]
    public async Task 보완할_식당이_없으면_없다고_알린다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "한식당", categoryId: 1);

        var response = await Commands(fixture, new FakeSlackApiClient()).Handle(Invoke("/lunchpending"));

        Assert.Equal("정보가 덜 찬 식당이 없습니다.", response.Message.Text);
    }

    [Fact]
    public async Task 통계_명령은_추천_대상_카테고리의_점수를_보여준다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "첫식당", categoryId: 1);

        var response = await Commands(fixture, new FakeSlackApiClient()).Handle(Invoke("/lunchstats"));

        Assert.StartsWith("*카테고리 점수 현황*", response.Message.Text);
        Assert.Contains("한식", response.Message.Text);
    }

    [Fact]
    public async Task 오늘_명령은_투표가_없으면_아직_열리지_않았다고_알린다()
    {
        await using var fixture = await TestDb.CreateAsync();

        var response = await Commands(fixture, new FakeSlackApiClient()).Handle(Invoke("/lunchtoday"));

        Assert.Equal("오늘 투표가 아직 열리지 않았습니다.", response.Message.Text);
    }

    [Fact]
    public async Task 오늘_명령은_오늘_열린_투표의_상태와_마감을_알린다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var today = LunchClock.TodayIn(new LunchOptions().TimeZone);
        var now = DateTimeOffset.UtcNow;
        await new LunchService(fixture.Db, Channel).OpenPollAsync(today, now.AddHours(-1), now.AddHours(1), Ct);

        var response = await Commands(fixture, new FakeSlackApiClient()).Handle(Invoke("/lunchtoday"));

        Assert.StartsWith("오늘 투표 상태: Open (마감 ", response.Message.Text);
    }

    [Fact]
    public async Task 인원_기준은_재시작_후에도_확정에_쓰이고_초기화_뒤에는_서버_설정을_따른다()
    {
        var dir = Directory.CreateTempSubdirectory("lunchsize-");
        var dbOptions = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dir.FullName, "lunch.db")};Pooling=False").Options;
        var today = new DateOnly(2026, 10, 20);
        var now = new DateTimeOffset(2026, 10, 20, 4, 0, 0, TimeSpan.Zero);
        var slack = new FakeSlackApiClient();
        try
        {
            long first, second;
            await using (var db = new LunchDbContext(dbOptions))
            {
                await db.Database.MigrateAsync(Ct);
                var lunch = new LunchService(db, Channel);
                first = (await lunch.SaveRestaurantAsync(new(null, "첫 식당", 1, null, null, null), "U1", Ct)).Id;
                second = (await lunch.SaveRestaurantAsync(new(null, "둘째 식당", 2, null, null, null), "U1", Ct)).Id;
                db.Polls.Add(new LunchPoll
                {
                    ChannelId = Channel, Date = today, Status = PollStatus.Closed, OpensAt = now.AddHours(-2), ClosesAt = now.AddHours(-1),
                    Votes = [.. Enumerable.Range(1, 5).Select(i => new PollVote { SlackUserId = $"U{i}", RestaurantId = first, VotedAt = now.AddHours(-2) })],
                });
                await db.SaveChangesAsync(Ct);
                var server = new LunchOptions();
                var commands = Commands(db, slack, server);
                Assert.Contains("5명", (await commands.Handle(Invoke("/lunchsize"))).Message.Text);
                server.MealParticipantsPerRestaurant = 7;
                Assert.Contains("7명", (await commands.Handle(Invoke("/lunchsize"))).Message.Text);
                await commands.Handle(Invoke("/lunchsize", "3"));
                var changed = await commands.Handle(Invoke("/lunchsize", " 4 "));
                Assert.Equal(ResponseType.Ephemeral, changed.ResponseType);
                Assert.Contains("4명", changed.Message.Text);
            }

            await using var reopened = new LunchDbContext(dbOptions);
            var settings = new LunchOptions { MealParticipantsPerRestaurant = 8 };
            var handler = Commands(reopened, slack, settings);
            var otherChannel = Commands(reopened, slack, settings, "C2");
            var otherQuery = Invoke("/lunchsize");
            otherQuery.ChannelId = "C2";
            Assert.Contains("8명", (await otherChannel.Handle(otherQuery)).Message.Text);
            Assert.Contains("4명", (await handler.Handle(Invoke("/lunchsize"))).Message.Text);
            var meals = new MealConfirmationService(reopened, new LunchService(reopened, Channel), Channel,
                Monitor(settings), new TestTimeProvider(now));
            Assert.Equal(2, (await meals.GetStateAsync(today, Ct)).Limit);
            Assert.Null((await meals.SaveAsync(today, [first, second], "U1", Ct)).Error);

            Assert.Contains("8명", (await handler.Handle(Invoke("/lunchsize", "기본값"))).Message.Text);
            settings.MealParticipantsPerRestaurant = 9;
            Assert.Contains("9명", (await handler.Handle(Invoke("/lunchsize"))).Message.Text);
            Assert.NotNull((await meals.SaveAsync(today, [first, second], "U2", Ct)).Error);
            var current = await meals.GetStateAsync(today, Ct);
            Assert.Equal(1, current.Limit);
            Assert.Equal(new[] { first, second }, current.Recorded.Select(p => p.Id).Order());
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("3")]
    [InlineData("기본값")]
    public async Task 다른_쓰기가_잠긴_동안에는_기준_변경을_거절하고_나중에도_적용하지_않는다(string argument)
    {
        var dir = Directory.CreateTempSubdirectory("lunchsize-busy-");
        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dir.FullName, "lunch.db")};Pooling=False;Default Timeout=4").Options;
        try
        {
            await using var db = new LunchDbContext(options);
            await db.Database.EnsureCreatedAsync(Ct);
            var handler = Commands(db, new FakeSlackApiClient(), new LunchOptions());
            await handler.Handle(Invoke("/lunchsize", "4"));
            await using var writer = new LunchDbContext(options);
            await using (await writer.Database.BeginTransactionAsync(Ct))
            {
                var denied = await handler.Handle(Invoke("/lunchsize", argument));
                Assert.Equal(ResponseType.Ephemeral, denied.ResponseType);
                Assert.Contains("잠시 후", denied.Message.Text);
            }
            Assert.Contains("4명", (await handler.Handle(Invoke("/lunchsize"))).Message.Text);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(Channel, "0")]
    [InlineData(Channel, "-2")]
    [InlineData(Channel, "4.5")]
    [InlineData(Channel, "4 5")]
    [InlineData(Channel, "2147483648")]
    [InlineData("C2", "3")]
    [InlineData("D1", "기본값")]
    [InlineData("C2", "")]
    public async Task 기준_명령은_잘못된_인자와_다른_채널에서의_접근을_거절한다(string channelId, string text)
    {
        await using var fixture = await TestDb.CreateAsync();
        var handler = Commands(fixture, new FakeSlackApiClient());
        await handler.Handle(Invoke("/lunchsize", "6"));
        var request = Invoke("/lunchsize", text);
        request.ChannelId = channelId;
        var rejected = await handler.Handle(request);
        Assert.Equal(ResponseType.Ephemeral, rejected.ResponseType);
        Assert.Contains(channelId == Channel ? "/lunchsize" : "채널", rejected.Message.Text);
        Assert.DoesNotContain("6명", rejected.Message.Text);
        Assert.Contains("6명", (await handler.Handle(Invoke("/lunchsize"))).Message.Text);
    }

    [Fact]
    public async Task 테스트_명령은_나에게만_보이는_투표_화면을_띄우고_아무것도_저장하지_않는다()
    {
        await using var fixture = await TestDb.CreateAsync();
        await SeedAsync(fixture, "첫식당");
        var slack = new FakeSlackApiClient();

        var response = await Commands(fixture, slack).Handle(Invoke("/lunchtest"));

        Assert.Equal(ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("[테스트]", response.Message.Text);
        Assert.NotEmpty(response.Message.Blocks);
        Assert.Null(slack.ChatFake.PostedMessage);
        Assert.Empty(await fixture.Db.Polls.ToArrayAsync(Ct));
        Assert.Empty(await fixture.Db.PollVotes.ToArrayAsync(Ct));
    }
}
