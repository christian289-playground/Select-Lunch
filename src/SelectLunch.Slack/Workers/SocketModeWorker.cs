using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Slack.Options;
using SlackNet.Extensions.DependencyInjection;

namespace SelectLunch.Slack.Workers;

/// <summary>
/// Socket Mode 연결을 유지한다. 아웃바운드 웹소켓이라 공인 IP·인증서가 필요 없다.
/// 재연결은 SlackNet이 처리하므로 여기서는 연결 수립과 수명만 관리한다.
/// </summary>
public sealed class SocketModeWorker(
    IServiceProvider services,
    IOptions<SlackOptions> slackOptions,
    ILogger<SocketModeWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = services.SlackServices().GetSocketModeClient();

        logger.LogInformation("Socket Mode 연결을 시작합니다.");
        await client.Connect(null, stoppingToken);
        logger.LogInformation("Socket Mode 연결됨. 채널 {ChannelId} 를 담당합니다.",
            slackOptions.Value.ChannelId);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
