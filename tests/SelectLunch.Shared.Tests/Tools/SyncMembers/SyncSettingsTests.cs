using System.Text;
using SyncMembers;

namespace SelectLunch.Shared.Tests;

public class SyncSettingsTests
{
    const string SlackOnly = """{ "Slack": { "BotToken": "xoxb-a", "AppToken": "xapp-b", "ChannelId": "C01" } }""";

    [Fact]
    public void 상대_DB_경로는_작업_디렉터리가_아니라_설정_파일_폴더_기준으로_해석한다()
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json",
            """{ "Slack": { "BotToken": "xoxb-a", "ChannelId": "C01" }, "Database": { "Path": "data/x.db" } }""");

        var resolved = SyncSettings.ResolveDatabasePath(config);

        Assert.Equal(Path.GetFullPath(Path.Combine(dir.Root, "data", "x.db")), resolved);
    }

    [Fact]
    public void Local_설정에_DB_경로가_없으면_같은_폴더_appsettings_json의_값을_쓴다()
    {
        using var dir = new TempDir();
        dir.Write("appsettings.json", """{ "Database": { "Path": "base/lunch.db" } }""");
        var config = dir.Write("appsettings.Local.json", SlackOnly);

        Assert.Equal(Path.GetFullPath(Path.Combine(dir.Root, "base", "lunch.db")), SyncSettings.ResolveDatabasePath(config));
    }

    [Fact]
    public void Local_설정의_DB_경로가_appsettings_json보다_우선한다()
    {
        using var dir = new TempDir();
        dir.Write("appsettings.json", """{ "Database": { "Path": "base/lunch.db" } }""");
        var config = dir.Write("appsettings.Local.json", """{ "Database": { "Path": "local/lunch.db" } }""");

        Assert.Equal(Path.GetFullPath(Path.Combine(dir.Root, "local", "lunch.db")), SyncSettings.ResolveDatabasePath(config));
    }

    [Fact]
    public void 어디에도_DB_경로가_없으면_앱과_같은_기본값_data_lunch_db를_쓴다()
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json", SlackOnly);

        Assert.Equal(Path.GetFullPath(Path.Combine(dir.Root, "data", "lunch.db")), SyncSettings.ResolveDatabasePath(config));
    }

    [Fact]
    public void 절대_DB_경로는_그대로_쓴다()
    {
        using var dir = new TempDir();
        var absolute = Path.GetFullPath(Path.Combine(dir.Root, "elsewhere", "my.db"));
        var json = $$"""{ "Database": { "Path": {{System.Text.Json.JsonSerializer.Serialize(absolute)}} } }""";
        var config = dir.Write("appsettings.Local.json", json);

        Assert.Equal(absolute, SyncSettings.ResolveDatabasePath(config));
    }

    [Fact]
    public void 키_대소문자와_JSON_주석_후행_쉼표_UTF8_BOM을_허용한다()
    {
        using var dir = new TempDir();
        var path = dir.Combine("appsettings.Local.json");
        var json = """
            // 로컬 설정
            { "slack": { "botToken": "xoxb-a", "channelid": "C77", }, }
            """;
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)];
        File.WriteAllBytes(path, bytes);

        var slack = SyncSettings.LoadSlack(path);

        Assert.Equal("xoxb-a", slack.BotToken);
        Assert.Equal("C77", slack.ChannelId);
    }

    [Fact]
    public void 슬랙_설정은_봇_토큰과_채널_ID만_읽고_앱_토큰은_요구하지_않는다()
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json",
            """{ "Slack": { "BotToken": " xoxb-a ", "ChannelId": " C01 " } }""");

        var slack = SyncSettings.LoadSlack(config);

        Assert.Equal("xoxb-a", slack.BotToken);
        Assert.Equal("C01", slack.ChannelId);
    }

    [Fact]
    public void 설정_파일이_없으면_경로를_알리고_실패한다()
    {
        using var dir = new TempDir();
        var missing = dir.Combine("nope.json");

        var ex = Assert.Throws<SyncException>(() => SyncSettings.ResolveDatabasePath(missing));

        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void 설정_파일이_깨져_있으면_내용을_노출하지_않고_파일_이름만_알린다()
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json", """{ "Slack": { "BotToken": "xoxb-leak" """);

        var ex = Assert.Throws<SyncException>(() => SyncSettings.LoadSlack(config));

        Assert.Contains("appsettings.Local.json", ex.Message);
        Assert.DoesNotContain("xoxb-leak", ex.Message);
    }

    [Fact]
    public void 봇_토큰_형식이_틀리면_값을_노출하지_않고_거부한다()
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json",
            """{ "Slack": { "BotToken": "xapp-looks-wrong-secret", "ChannelId": "C01" } }""");

        var ex = Assert.Throws<SyncException>(() => SyncSettings.LoadSlack(config));

        Assert.Contains("BotToken", ex.Message);
        Assert.DoesNotContain("xapp-looks-wrong-secret", ex.Message);
    }

    [Theory]
    [InlineData("""{ "Slack": { "ChannelId": "C01" } }""", "BotToken")]
    [InlineData("""{ "Slack": { "BotToken": "xoxb-a" } }""", "ChannelId")]
    [InlineData("""{ "Slack": { "BotToken": "xoxb-a", "ChannelId": "  " } }""", "ChannelId")]
    [InlineData("""{ }""", "BotToken")]
    public void 필수_슬랙_설정이_비어_있으면_어느_값인지_알리고_거부한다(string json, string missingKey)
    {
        using var dir = new TempDir();
        var config = dir.Write("appsettings.Local.json", json);

        var ex = Assert.Throws<SyncException>(() => SyncSettings.LoadSlack(config));

        Assert.Contains(missingKey, ex.Message);
    }

    [Fact]
    public void 슬랙_설정을_문자열로_찍어도_토큰이_나오지_않는다()
    {
        var slack = new SlackSettings("xoxb-secret-value", "C01");

        Assert.DoesNotContain("xoxb-secret-value", slack.ToString());
    }
}
