using SelectLunch.Slack.Handlers;

namespace SelectLunch.Slack.Tests;

public class SlashCommandParsingTests
{
    [Theory]
    [InlineData("", "help", "")]
    [InlineData("   ", "help", "")]
    [InlineData("help", "help", "")]
    [InlineData("list", "list", "")]
    [InlineData("add", "add", "")]
    [InlineData("add 스시로", "add", "스시로")]
    [InlineData("add  스시 로  ", "add", "스시 로")]
    [InlineData("stats week", "stats", "week")]   // 인자는 무시되지만 파싱은 되어야 한다
    public void 서브커맨드와_인자를_분해한다(string text, string expectedSub, string expectedArg)
    {
        var command = LunchCommand.Parse(text);

        Assert.Equal(expectedSub, command.SubCommand);
        Assert.Equal(expectedArg, command.Argument);
    }

    [Fact]
    public void 대소문자를_가리지_않는다()
    {
        Assert.Equal("list", LunchCommand.Parse("LIST").SubCommand);
    }
}
