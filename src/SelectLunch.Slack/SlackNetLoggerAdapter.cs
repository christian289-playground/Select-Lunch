using Microsoft.Extensions.Logging;
using SlackNet;

namespace SelectLunch.Slack;

/// <summary>
/// SlackNet 자체 로깅(<see cref="SlackNet.ILogger"/>)을 이 앱의
/// Microsoft.Extensions.Logging 파이프라인으로 잇는다. <c>AddSlackNet(...)</c>에
/// 연결하지 않으면 SlackNet은 내부적으로 NullLogger를 쓴다 — 토큰이 폐기되거나
/// 소켓이 영구히 재연결에 실패해도 이 앱의 로그에는 아무것도 남지 않는다.
/// <see cref="Workers.SocketModeWorker"/>의 <c>Task.Delay(Infinite)</c>는
/// 그대로 끝나지 않아 프로세스는 멀쩡히 살아있는 것처럼 보이지만, 실제로는
/// Slack과 완전히 끊긴 채 아무 일도 하지 않는 상태가 된다.
/// </summary>
public sealed class SlackNetLoggerAdapter(ILoggerFactory loggerFactory) : SlackNet.ILogger
{
    public void Log(ILogEvent logEvent)
    {
        var logger = loggerFactory.CreateLogger($"SlackNet.{logEvent.Category}");

        logger.Log(
            ToLogLevel(logEvent.Category),
            logEvent.Exception,
            logEvent.FullMessageTemplate(),
            logEvent.FullMessagePropertyValues());
    }

    static LogLevel ToLogLevel(LogCategory category) => category switch
    {
        LogCategory.Error => LogLevel.Error,
        LogCategory.Internal => LogLevel.Debug,
        LogCategory.Serialization => LogLevel.Debug,
        LogCategory.Request => LogLevel.Debug,
        LogCategory.Data => LogLevel.Trace,
        _ => LogLevel.Information,
    };
}
