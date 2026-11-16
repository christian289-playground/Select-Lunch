using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;

namespace SelectLunch.Slack.Tests;

internal static class AdvanceVoteTestSupport
{
    public static AdvanceVoteActionHandler Handler(
        LunchDbContext db, ISlackApiClient slack, IOptionsMonitor<LunchOptions>? options = null, string channelId = "C1")
    {
        options ??= Monitor();
        var settings = Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = channelId });
        var service = new AdvanceVoteService(db, channelId, options, TimeProvider.System);
        return new AdvanceVoteActionHandler(db, service, slack, options, settings, TimeProvider.System);
    }

    public static RestaurantOptionProvider Provider(LunchDbContext db)
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new SlackOptions { ChannelId = "C1" });
        return new RestaurantOptionProvider(db, new AdvanceVoteService(db, "C1", Monitor(), TimeProvider.System), settings);
    }

    static IOptionsMonitor<LunchOptions> Monitor() => StubApi.Of<IOptionsMonitor<LunchOptions>>(
        ("get_CurrentValue", (Func<LunchOptions>)(() => new LunchOptions())));
}
