using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Slack.Options;
using SlackNet.Extensions.DependencyInjection;

namespace SelectLunch.Slack.Workers;

/// <summary>
/// Socket Mode 연결을 유지한다. 아웃바운드 웹소켓이라 공인 IP·인증서가 필요 없다.
/// 재연결은 SlackNet(<c>ReconnectingWebSocket</c>)이 내부적으로 백오프를 두고 처리하므로
/// 여기서는 연결 수립과 <c>Task.Delay(Infinite)</c>로의 수명 유지만 담당한다.
///
/// 다만 그 재연결·실패 로그는 SlackNet 자체의 <see cref="SlackNet.ILogger"/>로만 나가고,
/// <c>Program.cs</c>에서 <see cref="SelectLunch.Slack.SlackNetLoggerAdapter"/>로 연결해 주지 않으면 기본값인
/// NullLogger로 사라진다 — 토큰이 폐기되는 등 영구히 재연결에 실패해도 이 클래스의
/// <c>Task.Delay(Infinite)</c>는 그대로 끝나지 않고, 이 앱의 로그에는 아무 것도 남지
/// 않은 채 프로세스만 멀쩡히 살아 있는 것처럼 보인다.
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
