using Microsoft.Extensions.Logging;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Tests;

public class AdvanceVotePosterTests
{
    static readonly AdvanceVoteChange Change = new(AdvanceVoteChangeKind.Selected, "국밥집", 
        new DateTimeOffset(2026, 11, 16, 9, 35, 0, TimeSpan.FromHours(9)),
        new AdvanceVoteSnapshot(new DateOnly(2026, 11, 16), [new(42, "국밥집", 2)], 1));

    [Fact]
    public async Task PostAsync_성공하면_개인정보_없는_채널_메시지를_한번_게시한다()
    {
        var slack = new FakeSlackApiClient();
        var logger = new CaptureLogger();
        var poster = new AdvanceVotePoster(slack, "C1", logger);

        Assert.True(await poster.PostAsync(Change, TestContext.Current.CancellationToken));
        var message = Assert.Single(slack.ChatFake.PostAttempts);
        Assert.Equal("C1", message.Channel);
        Assert.Null(message.ThreadTs);
        Assert.Equal(ParseMode.None, message.Parse);
        Assert.False(message.LinkNames);
        Assert.False(message.UnfurlLinks);
        Assert.False(message.UnfurlMedia);
        Assert.DoesNotContain("국밥집", message.Text);
        Assert.Null(message.MetadataObject);
        Assert.Null(message.MetadataJson);
        Assert.All(message.Blocks, b =>
        {
            var section = Assert.IsType<SectionBlock>(b);
            Assert.IsType<PlainText>(section.Text);
            Assert.Null(section.BlockId);
            Assert.Null(section.Accessory);
            Assert.True(section.Fields is null || section.Fields.Count == 0);
        });
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task PostAsync_전송_실패는_재시도없이_false와_예외형식만_기록한다()
    {
        var slack = new FakeSlackApiClient();
        slack.ChatFake.OnPostMessage = (_, _) => throw new InvalidOperationException("U_PRIVATE 민감한 본문");
        var logger = new CaptureLogger();
        var poster = new AdvanceVotePoster(slack, "C1", logger);

        Assert.False(await poster.PostAsync(Change, TestContext.Current.CancellationToken));
        Assert.Single(slack.ChatFake.PostAttempts);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Text);
        Assert.DoesNotContain("U_PRIVATE", entry.Text);
        Assert.DoesNotContain("민감한 본문", entry.Text);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task PostAsync_요청_취소는_오류로_삼키지_않고_전파한다()
    {
        var slack = new FakeSlackApiClient();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        slack.ChatFake.OnPostMessage = (_, ct) => { source.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var logger = new CaptureLogger();
        var poster = new AdvanceVotePoster(slack, "C1", logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poster.PostAsync(Change, source.Token));
        Assert.Single(slack.ChatFake.PostAttempts);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task PostAsync_요청토큰과_무관한_취소_예외는_전송실패로_처리한다()
    {
        var slack = new FakeSlackApiClient();
        slack.ChatFake.OnPostMessage = (_, _) => throw new OperationCanceledException("민감");
        var logger = new CaptureLogger();
        var poster = new AdvanceVotePoster(slack, "C1", logger);

        Assert.False(await poster.PostAsync(Change, TestContext.Current.CancellationToken));
        Assert.Single(slack.ChatFake.PostAttempts);
        Assert.Single(logger.Entries);
    }

    sealed record Entry(LogLevel Level, string Text, Exception? Exception);

    sealed class CaptureLogger : ILogger<AdvanceVotePoster>
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new(level, formatter(state, exception), exception));
    }
}
