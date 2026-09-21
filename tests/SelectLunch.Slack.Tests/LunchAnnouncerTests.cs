using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class LunchAnnouncerTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    static async Task<(TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, LunchAnnouncer Announcer)>
        SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var service = new LunchService(fixture.Db, Channel);
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel);
        return (fixture, service, slack, announcer);
    }

    static RestaurantDraft Draft(string name, long categoryId) =>
        new(null, name, categoryId, null, null, null);

    static string TextOf(IList<Block> blocks) =>
        string.Join("\n", blocks.OfType<SectionBlock>().Select(s => (s.Text as Markdown)?.Text ?? ""));

    // --- 슬랙을 전혀 호출하지 않는 경로 ---

    [Fact]
    public async Task Pending_식당이_없으면_알림을_보내지_않는다()
    {
        var (fixture, _, slack, announcer) = await SetupAsync();
        await using var _ = fixture;

        await announcer.PostPendingReminderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, slack.ChatFake.PostCallCount);
    }

    [Fact]
    public async Task MessageTs가_없으면_RefreshPollAsync는_슬랙을_호출하지_않는다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        // OpenPollAsync는 MessageTs를 채우지 않는다 — 아직 발송 전 상태를 그대로 재현한다.
        Assert.Null((await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct)).MessageTs);

        await announcer.RefreshPollAsync(poll.Id, ct);

        Assert.Equal(0, slack.ChatFake.UpdateCallCount);
    }

    // --- 슬랙 호출을 페이크로 검증하는 경로 ---

    [Fact]
    public async Task PostPollAsync는_메시지를_보내고_MessageTs를_저장한다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        slack.ChatFake.NextTs = "1700000000.000100";

        var ts = await announcer.PostPollAsync(poll.Id, ClosesAt, ct);

        Assert.Equal("1700000000.000100", ts);
        Assert.Equal(1, slack.ChatFake.PostCallCount);
        Assert.Equal(Channel, slack.ChatFake.PostedMessage!.Channel);
        Assert.Equal("오늘 점심 뭐 먹지?", slack.ChatFake.PostedMessage.Text);
        Assert.NotEmpty(slack.ChatFake.PostedMessage.Blocks);

        var saved = await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct);
        Assert.Equal("1700000000.000100", saved.MessageTs);
    }

    [Fact]
    public async Task RefreshPollAsync는_MessageTs가_있으면_집계를_담아_Update를_호출한다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 한식집 = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 한식집.Id, ct);

        await announcer.RefreshPollAsync(poll.Id, ct);

        Assert.Equal(1, slack.ChatFake.UpdateCallCount);
        var update = slack.ChatFake.UpdatedMessage!;
        Assert.Equal(Channel, update.ChannelId);
        Assert.Equal(slack.ChatFake.NextTs, update.Ts);
        Assert.Equal("오늘 점심 뭐 먹지?", update.Text);
        Assert.Contains("1표", TextOf(update.Blocks));
    }

    [Fact]
    public async Task RefreshPollAsync는_투표_진행_중_보관된_식당도_후보와_득표를_유지한다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 스시로 = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 스시로.Id, ct);

        // 투표가 열려 있는 동안 식당이 보관(Archived)된다 — 이 풀의 후보 스냅샷
        // (PollCandidate)은 그대로 유지되어야 하고, 이미 들어온 표도 화면에서
        // 사라지면 안 된다.
        (await fixture.Db.Restaurants.SingleAsync(r => r.Id == 스시로.Id, ct)).Status = RestaurantStatus.Archived;
        await fixture.Db.SaveChangesAsync(ct);

        await announcer.RefreshPollAsync(poll.Id, ct);

        var updated = slack.ChatFake.UpdatedMessage!;
        Assert.Contains("1표", TextOf(updated.Blocks));
        // 후보 버튼 블록 + 기권 버튼 블록, 두 개가 별도로 있어야 한다.
        var actionsBlocks = updated.Blocks.OfType<ActionsBlock>().ToList();
        Assert.Equal(2, actionsBlocks.Count);
        var button = actionsBlocks
            .SelectMany(a => a.Elements.OfType<Button>())
            .Single(b => ActionIds.TryParseVote(b.ActionId, out var _p, out var _r));
        Assert.True(ActionIds.TryParseVote(button.ActionId, out var parsedPollId, out var votedRestaurantId));
        Assert.Equal(poll.Id, parsedPollId);
        Assert.Equal(스시로.Id, votedRestaurantId);
    }

    [Fact]
    public async Task PostResultAsync는_결과_블록을_보낸다()
    {
        var (fixture, _, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var outcome = new PollOutcome(new VoteTally(1, "스시로", 2), [new VoteTally(1, "스시로", 2)], null, []);

        await announcer.PostResultAsync(outcome, new RecommendationOptions(), ct);

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        Assert.Equal(Channel, slack.ChatFake.PostedMessage!.Channel);
        Assert.Equal("오늘 점심 투표 결과", slack.ChatFake.PostedMessage.Text);
        Assert.Contains("스시로", TextOf(slack.ChatFake.PostedMessage.Blocks));
    }

    [Fact]
    public async Task PostMealPromptAsync는_기록된_식당_이름을_반영한다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        await service.RecordMealAsync(Today, 식당.Id, "U1", MealSource.Prompt, ct);

        await announcer.PostMealPromptAsync(Today, ct);

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        Assert.Equal("오늘 뭐 드셨어요?", slack.ChatFake.PostedMessage!.Text);
        Assert.Contains("스시로", TextOf(slack.ChatFake.PostedMessage.Blocks));
    }

    [Fact]
    public async Task PostPendingReminderAsync는_Pending_식당이_있으면_버튼과_함께_보낸다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 식당 = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "이름만아는집", null, null, null, null), "U1", ct);

        await announcer.PostPendingReminderAsync(ct);

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        var message = slack.ChatFake.PostedMessage!;
        Assert.Equal("정보가 덜 찬 식당이 있습니다", message.Text);
        var button = message.Blocks.OfType<ActionsBlock>().Single().Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseRestaurantFill(button.ActionId, out var restaurantId));
        Assert.Equal(식당.Id, restaurantId);
    }

    [Fact]
    public async Task PostPendingReminderAsync는_가장_오래된_5개_식당의_버튼만_보낸다()
    {
        var (fixture, service, slack, announcer) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var names = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var name = $"이름만아는집{i}";
            await service.SaveRestaurantAsync(
                new RestaurantDraft(null, name, null, null, null, null), "U1", ct);
            names.Add(name);
        }

        // CreatedAt을 명시적으로 벌려 놓는다 — 등록 순서와 우연히 같은 값으로는
        // Take(5)가 앞의 5개를 그대로 취해도 통과해 버려 OrderBy(CreatedAt)가
        // 실제로 "오래된 순"을 고르는지 검증하지 못한다. 여기서는 등록 역순으로
        // CreatedAt을 매겨, 나중에 등록한 식당("이름만아는집6"..."이름만아는집2")이
        // 더 오래된 값을 갖게 만든다.
        var restaurants = await fixture.Db.Restaurants.Where(r => names.Contains(r.Name)).ToListAsync(ct);
        var baseline = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < names.Count; i++)
            restaurants.Single(r => r.Name == names[i]).CreatedAt = baseline.AddDays(-i);
        await fixture.Db.SaveChangesAsync(ct);
        // 가장 오래된 5곳은 등록 순서 2~6번째("이름만아는집2".."이름만아는집6")다 —
        // 0, 1번은 CreatedAt이 더 최근이라 빠져야 한다.
        var expectedOldestFive = names.Skip(2).Order(StringComparer.Ordinal).ToArray();

        await announcer.PostPendingReminderAsync(ct);

        var buttons = slack.ChatFake.PostedMessage!.Blocks.OfType<ActionsBlock>()
            .Single().Elements.OfType<Button>().ToList();
        Assert.Equal(5, buttons.Count);
        Assert.Contains("7곳", TextOf(slack.ChatFake.PostedMessage.Blocks));

        var shownNames = buttons
            .Select(b => ((PlainText)b.Text!).Text)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedOldestFive, shownNames);
        Assert.DoesNotContain("이름만아는집0", shownNames);
        Assert.DoesNotContain("이름만아는집1", shownNames);
    }

    [Fact]
    public async Task PostTextAsync는_마크다운을_그대로_보낸다()
    {
        var (fixture, _, slack, announcer) = await SetupAsync();
        await using var _ = fixture;

        await announcer.PostTextAsync("*공지* 오늘은 휴무입니다", TestContext.Current.CancellationToken);

        Assert.Equal(1, slack.ChatFake.PostCallCount);
        Assert.Equal("*공지* 오늘은 휴무입니다", slack.ChatFake.PostedMessage!.Text);
        Assert.Equal("*공지* 오늘은 휴무입니다", TextOf(slack.ChatFake.PostedMessage.Blocks));
    }
}
