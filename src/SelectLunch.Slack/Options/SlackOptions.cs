namespace SelectLunch.Slack.Options;

/// <summary>
/// 토큰과 채널 ID. appsettings.Local.json 또는 환경변수로만 주입하며 커밋하지 않는다.
/// 이 값들은 핫리로드 대상이 아니다 — 바뀌면 웹소켓을 다시 맺어야 한다.
/// </summary>
public sealed class SlackOptions
{
    public const string SectionName = "Slack";

    public string BotToken { get; set; } = "";

    public string AppToken { get; set; } = "";

    /// <summary>봇이 동작할 잠금 채널의 ID (`C`로 시작).</summary>
    public string ChannelId { get; set; } = "";

    public void Validate()
    {
        Require(BotToken, nameof(BotToken), "xoxb-");
        Require(AppToken, nameof(AppToken), "xapp-");
        Require(ChannelId, nameof(ChannelId), null);
    }

    static void Require(string value, string name, string? expectedPrefix)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Slack:{name} 설정이 비어 있습니다. appsettings.Local.json 또는 환경변수로 지정하세요.");

        if (expectedPrefix is not null && !value.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Slack:{name} 값이 '{expectedPrefix}'로 시작하지 않습니다.");
    }
}
