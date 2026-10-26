using Microsoft.Extensions.Options;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Leave;
using SelectLunch.Slack.Options;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Tests;

public class LunchLeavePreviewTests
{
    const string Alias = "예제별칭";

    [Theory]
    [InlineData("C1", true)]
    [InlineData("C2", false)]
    public async Task 미리보기는_브라우저를_기다리지_않고_구성된채널에서만_캐시를_보여준다(
        string requestedChannel, bool shouldShow)
    {
        await using var fixture = await TestDb.CreateAsync();
        var monitor = StubApi.Of<IOptionsMonitor<LunchOptions>>(
            ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
        var handler = new LunchSlashCommandHandler(fixture.Db, new FakeSlackApiClient(), monitor,
            new CachedOnly(), Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = "C1" }));

        var response = await handler.Handle(new SlashCommand { Text = "test", ChannelId = requestedChannel });
        Assert.NotNull(response.Message);
        var displayed = response.Message.Blocks.OfType<SectionBlock>()
            .Select(b => b.Text).OfType<PlainText>()
            .Any(p => p.Text.Contains(Alias, StringComparison.Ordinal));

        Assert.Equal(shouldShow, displayed);
    }

    sealed class CachedOnly : IPollLeaveRoster
    {
        public LeaveRosterSnapshot? GetCached(DateOnly date) =>
            new(LeaveRosterStatus.Available, [Alias]);

        public Task<LeaveRosterSnapshot> GetAsync(long pollId, DateOnly date, CancellationToken ct) =>
            throw new InvalidOperationException("Slash 응답에서 브라우저 조회를 시작하면 안 됩니다.");
    }
}
