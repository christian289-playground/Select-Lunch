using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Tests;

public class SlackOptionsTests
{
    static SlackOptions Valid() => new()
    {
        BotToken = "xoxb-test", AppToken = "xapp-test", ChannelId = "C0TEST",
    };

    [Fact]
    public void 모든_값이_채워지면_검증을_통과한다()
    {
        Valid().Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 봇_토큰이_비면_예외를_던진다(string token)
    {
        var options = Valid();
        options.BotToken = token;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("BotToken", ex.Message);
    }

    [Fact]
    public void 채널_아이디가_비면_예외를_던진다()
    {
        var options = Valid();
        options.ChannelId = "";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
