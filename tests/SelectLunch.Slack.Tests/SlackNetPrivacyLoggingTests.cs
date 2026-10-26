using Microsoft.Extensions.Logging;
using SlackNet;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;

namespace SelectLunch.Slack.Tests;

public class SlackNetPrivacyLoggingTests
{
    [Fact]
    public void SDK_오류의_본문_속성_메시지인자_예외원문은_저장하지_않고_오류형식은_남긴다()
    {
        const string privateValue = "합성_휴가자정보_비공개";
        var sink = new CaptureLogger();
        var factory = StubApi.Of<ILoggerFactory>(("CreateLogger", (Func<string, MicrosoftLogger>)(_ => sink)));
        SlackNet.ILogger sdk = new SlackNetLoggerAdapter(factory);

        sdk.WithContext("RequestBody", privateValue)
            .WithContext("ResponseBody", privateValue)
            .WithContext("Message", privateValue)
            .WithContext("Payload", privateValue)
            .Error(new FormatException(privateValue), "Error parsing {Message}", privateValue);

        var captured = sink.Last;
        Assert.NotNull(captured);
        Assert.Equal(LogLevel.Error, captured.Level);
        Assert.DoesNotContain(privateValue, captured.Text);
        Assert.DoesNotContain(privateValue, captured.Values);
        Assert.Null(captured.Error);
        Assert.Contains(nameof(FormatException), captured.Values);
    }

    sealed record Captured(LogLevel Level, string Text, string Values, Exception? Error);

    sealed class CaptureLogger : MicrosoftLogger
    {
        public Captured? Last { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("|", pairs.Select(pair => pair.Value?.ToString()))
                : state?.ToString() ?? "";
            Last = new(logLevel, formatter(state, exception), values, exception);
        }
    }
}
