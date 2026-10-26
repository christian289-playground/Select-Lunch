using SelectLunch.Shared.Data;
using SelectLunch.Slack.Options;
using SlackNet;

namespace SelectLunch.Slack.Leave;

public static class LeaveDisplayServiceCollectionExtensions
{
    /// <summary>
    /// 휴가 표시 구성요소를 한 번에 등록한다. 이미 등록돼 있어야 하는 것: <see cref="ISlackApiClient"/>(SlackNet),
    /// <see cref="LunchDbContext"/>(scoped), <see cref="TimeProvider"/>, 로깅·호스팅.
    /// 메일 도구 실행기·회사 어댑터는 처음 필요할 때 만든다 — 꺼져 있으면 만들지도 않는다.
    /// </summary>
    /// <param name="options">시작 때 <see cref="LeaveDisplayOptions.Validate"/>를 통과한 설정.</param>
    /// <param name="channelId">명단을 모을 설정 채널 ID.</param>
    public static IServiceCollection AddLeaveDisplay(
        this IServiceCollection services,
        LeaveDisplayOptions options,
        string channelId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        services.AddSingleton(options);
        services.AddSingleton<ILeaveMailReader>(_ => new OutlookLeaveMailReader(options));
        services.AddSingleton(_ => LeaveMailProviders.Create(options.Provider));
        services.AddScoped<ILeaveRosterLoader>(sp => new LeaveRosterLoader(
            sp.GetRequiredService<ISlackApiClient>(),
            sp.GetRequiredService<LunchDbContext>(),
            channelId,
            sp.GetRequiredService<LeaveMailProvider>(),
            sp.GetRequiredService<ILeaveMailReader>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IPollLeaveRoster, PollLeaveRoster>();

        return services;
    }
}
