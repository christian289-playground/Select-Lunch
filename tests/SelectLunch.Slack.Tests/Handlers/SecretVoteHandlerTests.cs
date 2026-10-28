using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Button = SlackNet.Blocks.Button;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 비밀 투표 버튼의 경계: 접수증에는 식당 이름이 없고, 확인 버튼은 본인의 같은 표일 때만 읽으며,
/// 테스트 모드는 표를 쓰지 않은 채 접수 때 뽑은 미리보기만 보여준다.
/// (접수 → 확인 → 재확인의 기본 흐름은 <see cref="SecretRevealFlowTests"/>가 본다.)
/// </summary>
public class SecretVoteHandlerTests
{
    const string Channel = "C1";
    const long 한식 = 1;
    const long 일식 = 3;
    static readonly string[] CandidateNames = ["국밥집", "백반집", "스시로"];
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    sealed record Harness(
        TestDb Fixture, LunchService Service, FakeSlackApiClient Slack, VoteActionHandler Handler,
        long PollId, long 일식Id);

    sealed record VoteSnapshot(long? RestaurantId, bool IsSecret, DateTimeOffset VotedAt);

    static async Task<Harness> SetupAsync(bool withCandidates = true)
    {
        var fixture = await TestDb.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var service = new LunchService(fixture.Db, Channel, new Random(7));
        var slack = new FakeSlackApiClient();
        var announcer = new LunchAnnouncer(slack, fixture.Db, service, Channel, TestPollLeaveRoster.Disabled);

        long 일식Id = 0;
        if (withCandidates)
        {
            await service.SaveRestaurantAsync(new(null, "국밥집", 한식, null, null, null), "U0", ct);
            await service.SaveRestaurantAsync(new(null, "백반집", 한식, null, null, null), "U0", ct);
            일식Id = (await service.SaveRestaurantAsync(new(null, "스시로", 일식, null, null, null), "U0", ct)).Id;
        }

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await announcer.PostPollAsync(poll.Id, ClosesAt, ct);

        return new Harness(fixture, service, slack, new VoteActionHandler(service, announcer,
            EarlyDeparturePosterTests.CreatePoster(slack, fixture, Channel)), poll.Id, 일식Id);
    }

    static BlockActionRequest Request(string userId, BlockAction action) => new()
    {
        User = new User { Id = userId },
        Container = new Container { ChannelId = Channel, Type = "message" },
        Actions = [action],
    };

    static BlockActionRequest Press(string userId, string actionId) =>
        Request(userId, new ButtonAction { ActionId = actionId });

    static string TextOf(TextObject? text) => text switch
    {
        Markdown m => m.Text,
        PlainText p => p.Text,
        _ => "",
    };

    static List<Button> ButtonsOf(EphemeralAttempt attempt) =>
        [.. (attempt.Message.Blocks ?? new List<Block>())
            .OfType<ActionsBlock>().SelectMany(a => a.Elements).OfType<Button>()];

    static string RevealButtonId(EphemeralAttempt attempt) => Assert.Single(ButtonsOf(attempt)).ActionId;

    /// <summary>사용자 눈에 들어오는 모든 글 — 대체 텍스트, 섹션 본문, 버튼 라벨.</summary>
    static string VisibleText(EphemeralAttempt attempt)
    {
        var blocks = attempt.Message.Blocks ?? new List<Block>();
        List<string> parts = [attempt.Message.Text ?? ""];
        parts.AddRange(blocks.OfType<SectionBlock>().Select(b => TextOf(b.Text)));
        parts.AddRange(ButtonsOf(attempt).Select(b => TextOf(b.Text)));
        return string.Join("\n", parts);
    }

    static void AssertNoCandidateNamed(string? text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.All(CandidateNames, name => Assert.DoesNotContain(name, text));
    }

    static async Task<VoteSnapshot> SnapshotAsync(Harness h, string userId, CancellationToken ct)
    {
        var vote = await h.Fixture.Db.PollVotes.AsNoTracking()
            .SingleAsync(v => v.PollId == h.PollId && v.SlackUserId == userId, ct);
        return new VoteSnapshot(vote.RestaurantId, vote.IsSecret, vote.VotedAt);
    }

    static async Task<string> PickedNameAsync(Harness h, string userId, CancellationToken ct)
    {
        var vote = await h.Fixture.Db.PollVotes.AsNoTracking()
            .SingleAsync(v => v.PollId == h.PollId && v.SlackUserId == userId, ct);
        return (await h.Fixture.Db.Restaurants.AsNoTracking()
            .SingleAsync(r => r.Id == vote.RestaurantId, ct)).Name;
    }

    [Fact]
    public async Task 비밀_버튼은_표를_숨겨_저장하고_접수증과_공개_메시지에_식당_이름이_없다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));

        var vote = await h.Fixture.Db.PollVotes.SingleAsync(v => v.PollId == h.PollId && v.SlackUserId == "U1", ct);
        Assert.True(vote.IsSecret);
        Assert.NotNull(vote.RestaurantId);
        Assert.Equal(1, await h.Service.GetSecretVoteCountAsync(h.PollId, ct));
        Assert.Empty(await h.Service.GetTalliesAsync(h.PollId, ct));
        Assert.Equal(updatesBefore + 1, h.Slack.ChatFake.UpdateCallCount);

        var receipt = Assert.Single(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", receipt.UserId);
        Assert.Equal(Channel, receipt.Message.Channel);
        AssertNoCandidateNamed(VisibleText(receipt));
        var publicText = string.Join("\n", h.Slack.ChatFake.UpdatedMessage!.Blocks
            .OfType<SectionBlock>().Select(b => TextOf(b.Text)));
        AssertNoCandidateNamed(publicText);

        var outcome = await h.Service.ClosePollAsync(h.PollId, Today, new(), ct);
        Assert.Equal(vote.RestaurantId, outcome.Winner!.RestaurantId);
        Assert.Equal(1, outcome.Winner.Count);
    }

    [Fact]
    public async Task 연달아_다시_뽑아도_접수증_revision은_항상_커지고_저장된_표와_일치한다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        var previous = (await h.Service.CastSecretRandomVoteAsync(h.PollId, "U1", ct))!.Value.Revision;
        for (var i = 0; i < 50; i++)
        {
            var next = (await h.Service.CastSecretRandomVoteAsync(h.PollId, "U1", ct))!.Value.Revision;
            Assert.True(next > previous);
            previous = next;
        }

        Assert.Equal(previous, (await SnapshotAsync(h, "U1", ct)).VotedAt.UtcTicks);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("random")]
    [InlineData("abstain")]
    [InlineData("secret")]
    public async Task 표가_바뀌면_옛_접수증은_새_표를_보여주지_않는다(string next)
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));
        var oldReveal = RevealButtonId(Assert.Single(h.Slack.ChatFake.EphemeralAttempts));

        string? newReveal = null;
        switch (next)
        {
            case "ordinary":
                await h.Service.CastVoteAsync(h.PollId, "U1", h.일식Id, ct);
                break;
            case "random":
                await h.Service.CastRandomVoteAsync(h.PollId, "U1", 일식, ct);
                break;
            case "abstain":
                await h.Service.CastAbstentionAsync(h.PollId, "U1", ct);
                break;
            default:
                await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));
                newReveal = RevealButtonId(h.Slack.ChatFake.EphemeralAttempts[^1]);
                break;
        }

        var before = await SnapshotAsync(h, "U1", ct);
        var attempts = h.Slack.ChatFake.EphemeralAttempts.Count;
        var updates = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Press("U1", oldReveal));

        Assert.Equal(attempts + 1, h.Slack.ChatFake.EphemeralAttempts.Count);
        var stale = h.Slack.ChatFake.EphemeralAttempts[^1];
        Assert.Equal("U1", stale.UserId);
        AssertNoCandidateNamed(VisibleText(stale));
        Assert.Empty(ButtonsOf(stale));
        Assert.Equal(before, await SnapshotAsync(h, "U1", ct));
        Assert.Equal(updates, h.Slack.ChatFake.UpdateCallCount);

        if (newReveal is not null)
        {
            await h.Handler.Handle(Press("U1", newReveal));
            Assert.Contains(await PickedNameAsync(h, "U1", ct), h.Slack.ChatFake.EphemeralAttempts[^1].Message.Text);
        }
    }

    [Fact]
    public async Task 남의_접수증은_다른_사람에게_식당을_알리지_않고_주인의_표도_건드리지_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));
        var reveal = RevealButtonId(Assert.Single(h.Slack.ChatFake.EphemeralAttempts));
        var ownerBefore = await SnapshotAsync(h, "U1", ct);
        var updates = h.Slack.ChatFake.UpdateCallCount;

        // 표가 아예 없는 사람, 그리고 일반 표를 던진 사람 모두 같은 접수증으로는 아무것도 못 본다.
        await h.Handler.Handle(Press("U2", reveal));
        await h.Service.CastVoteAsync(h.PollId, "U2", h.일식Id, ct);
        await h.Handler.Handle(Press("U2", reveal));

        Assert.Equal(3, h.Slack.ChatFake.EphemeralAttempts.Count);
        Assert.All(h.Slack.ChatFake.EphemeralAttempts.Skip(1), a =>
        {
            Assert.Equal("U2", a.UserId);
            AssertNoCandidateNamed(VisibleText(a));
            Assert.Empty(ButtonsOf(a));
        });
        Assert.Equal(ownerBefore, await SnapshotAsync(h, "U1", ct));
        Assert.False((await SnapshotAsync(h, "U2", ct)).IsSecret);
        Assert.Equal(updates, h.Slack.ChatFake.UpdateCallCount);

        // 주인은 그대로 자기 표를 확인할 수 있다.
        await h.Handler.Handle(Press("U1", reveal));
        Assert.Contains(await PickedNameAsync(h, "U1", ct), h.Slack.ChatFake.EphemeralAttempts[^1].Message.Text);
    }

    [Fact]
    public async Task 마감_뒤에도_본인은_접수증으로_뽑은_식당을_볼_수_있고_표는_그대로다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));
        var reveal = RevealButtonId(Assert.Single(h.Slack.ChatFake.EphemeralAttempts));
        var pickedName = await PickedNameAsync(h, "U1", ct);
        await h.Service.ClosePollAsync(h.PollId, Today, new(), ct);
        var before = await SnapshotAsync(h, "U1", ct);
        var updates = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Press("U1", reveal));

        var revealed = h.Slack.ChatFake.EphemeralAttempts[^1];
        Assert.Equal("U1", revealed.UserId);
        Assert.Contains(pickedName, revealed.Message.Text);
        Assert.Equal(before, await SnapshotAsync(h, "U1", ct));
        Assert.Equal(updates, h.Slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 마감된_투표의_비밀_버튼은_집계도_화면도_바꾸지_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var poll = await h.Fixture.Db.Polls.SingleAsync(p => p.Id == h.PollId, ct);
        poll.Status = PollStatus.Closed;
        await h.Fixture.Db.SaveChangesAsync(ct);
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 후보가_없는_투표의_비밀_버튼은_표도_성공_안내도_남기지_않는다()
    {
        var h = await SetupAsync(withCandidates: false);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        await h.Handler.Handle(Press("U1", ActionIds.VoteSecret(h.PollId)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 옛_비밀_드롭다운_선택은_표도_안내도_남기지_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;
        BlockAction OldDropdown(string actionId) => new StaticSelectAction
        {
            ActionId = actionId,
            SelectedOption = new SlackNet.Blocks.Option { Value = "reveal" },
        };

        await h.Handler.Handle(Request("U1", OldDropdown(ActionIds.VoteSecret(h.PollId))));
        await h.Handler.Handle(Request("U1", OldDropdown(ActionIds.Test(ActionIds.VoteSecret(0)))));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Empty(h.Slack.ChatFake.EphemeralAttempts);
    }

    [Fact]
    public async Task 테스트_모드_비밀_버튼은_표를_기록하지_않고_이름_없는_접수증만_보낸다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        var updatesBefore = h.Slack.ChatFake.UpdateCallCount;

        // 테스트 메시지의 자리표시자 id(0)와, 실제 투표 id를 단 경우 모두 집계를 건드리면 안 된다.
        await h.Handler.Handle(Press("U1", ActionIds.Test(ActionIds.VoteSecret(0))));
        await h.Handler.Handle(Press("U1", ActionIds.Test(ActionIds.VoteSecret(h.PollId))));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(0, await h.Service.GetSecretVoteCountAsync(h.PollId, ct));
        Assert.Equal(updatesBefore, h.Slack.ChatFake.UpdateCallCount);
        Assert.Equal(2, h.Slack.ChatFake.EphemeralAttempts.Count);
        Assert.All(h.Slack.ChatFake.EphemeralAttempts, a =>
        {
            Assert.Equal("U1", a.UserId);
            Assert.Equal(Channel, a.Message.Channel);
            AssertNoCandidateNamed(VisibleText(a));
            var button = Assert.Single(ButtonsOf(a));
            Assert.True(ActionIds.TryUnwrapTest(button.ActionId, out var inner));
            Assert.True(ActionIds.TryParseSecretPreview(inner, out var previewId));
        });
    }

    [Fact]
    public async Task 테스트_모드_확인은_접수_때_뽑은_식당을_그대로_보여주고_다시_뽑지도_기록하지도_않는다()
    {
        var h = await SetupAsync();
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;
        await h.Handler.Handle(Press("U1", ActionIds.Test(ActionIds.VoteSecret(0))));
        var reveal = RevealButtonId(Assert.Single(h.Slack.ChatFake.EphemeralAttempts));
        Assert.True(ActionIds.TryUnwrapTest(reveal, out var inner));
        Assert.True(ActionIds.TryParseSecretPreview(inner, out var previewId));
        var previewName = (await h.Fixture.Db.Restaurants.SingleAsync(r => r.Id == previewId, ct)).Name;
        var updates = h.Slack.ChatFake.UpdateCallCount;

        for (var i = 0; i < 5; i++)
            await h.Handler.Handle(Press("U1", reveal));

        Assert.Equal(6, h.Slack.ChatFake.EphemeralAttempts.Count);
        Assert.All(h.Slack.ChatFake.EphemeralAttempts.Skip(1), a =>
        {
            Assert.Equal("U1", a.UserId);
            Assert.Equal(Channel, a.Message.Channel);
            Assert.Equal(previewName, Assert.Single(CandidateNames, name => a.Message.Text.Contains(name)));
        });

        // 확인 버튼에 실린 id만 읽는다는 것을 시드와 무관하게 못 박는다.
        await h.Handler.Handle(Press("U1", ActionIds.Test(ActionIds.SecretPreview(h.일식Id))));
        Assert.Equal("스시로", Assert.Single(CandidateNames,
            name => h.Slack.ChatFake.EphemeralAttempts[^1].Message.Text.Contains(name)));

        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
        Assert.Equal(updates, h.Slack.ChatFake.UpdateCallCount);
    }

    [Fact]
    public async Task 테스트_모드_비밀_버튼은_고를_식당이_없으면_확인_버튼_없이_알린다()
    {
        var h = await SetupAsync(withCandidates: false);
        await using var _ = h.Fixture;
        var ct = TestContext.Current.CancellationToken;

        await h.Handler.Handle(Press("U1", ActionIds.Test(ActionIds.VoteSecret(0))));

        var notice = Assert.Single(h.Slack.ChatFake.EphemeralAttempts);
        Assert.Equal("U1", notice.UserId);
        Assert.Empty(ButtonsOf(notice));
        Assert.False(await h.Fixture.Db.PollVotes.AnyAsync(ct));
    }
}
