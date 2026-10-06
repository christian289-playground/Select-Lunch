namespace SelectLunch.Slack.Tests;

public sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
