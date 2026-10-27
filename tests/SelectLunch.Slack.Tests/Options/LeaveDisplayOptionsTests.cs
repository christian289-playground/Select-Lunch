using Microsoft.Extensions.Configuration;
using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 휴가 표시 설정. 꺼져 있으면 아무것도 검사하지 않고(의도적으로 Disabled), 켜져 있으면 실제로 실행할 수 있는
/// 설정인지 시작할 때 검증한다 — 잘못된 설정이 스케줄러 안의 catch-all에 삼켜지지 않게 하려는 경계다.
/// </summary>
public class LeaveDisplayOptionsTests
{
    /// <summary>테스트 호스트 자신은 확실히 존재하는 실행 파일이다.</summary>
    static LeaveDisplayOptions Valid() => new()
    {
        Enabled = true,
        Executable = Environment.ProcessPath!,
        Arguments = ["-NoProfile"],
        Account = "tester@example.test",
    };

    [Fact]
    public void Disabled_options_are_never_validated()
    {
        var options = new LeaveDisplayOptions
        {
            Enabled = false,
            Provider = "no-such-provider",
            Executable = "",
            Account = "",
            TimeoutSeconds = -1,
            MaxResults = 0,
        };

        options.Validate();
    }

    [Fact]
    public void Enabled_options_with_a_real_command_pass()
    {
        Valid().Validate();
    }

    [Fact]
    public void Provider_name_ignores_case_and_surrounding_spaces()
    {
        var options = Valid();
        options.Provider = " Snuailab ";

        options.Validate();
    }

    [Theory]
    [InlineData("provider-unsupported")]
    [InlineData("provider-empty")]
    [InlineData("executable-empty")]
    [InlineData("executable-missing-absolute")]
    [InlineData("executable-missing-on-path")]
    [InlineData("arguments-null-entry")]
    [InlineData("account-empty")]
    [InlineData("account-not-an-address")]
    [InlineData("account-has-space")]
    [InlineData("timeout-zero")]
    [InlineData("timeout-too-large")]
    [InlineData("max-results-zero")]
    [InlineData("max-results-too-large")]
    public void Enabled_options_reject_an_unusable_setting(string flaw)
    {
        var options = Valid();

        switch (flaw)
        {
            case "provider-unsupported": options.Provider = "acme"; break;
            case "provider-empty": options.Provider = " "; break;
            case "executable-empty": options.Executable = " "; break;
            case "executable-missing-absolute":
                options.Executable = Path.Combine(Path.GetTempPath(), "select-lunch-" + Guid.NewGuid().ToString("N"));
                break;
            case "executable-missing-on-path":
                options.Executable = "select-lunch-no-such-command-" + Guid.NewGuid().ToString("N");
                break;
            case "arguments-null-entry": options.Arguments = ["-NoProfile", null!]; break;
            case "account-empty": options.Account = " "; break;
            case "account-not-an-address": options.Account = "not-an-address"; break;
            case "account-has-space": options.Account = "a b@example.test"; break;
            case "timeout-zero": options.TimeoutSeconds = 0; break;
            case "timeout-too-large": options.TimeoutSeconds = LeaveDisplayOptions.MaxTimeoutSeconds + 1; break;
            case "max-results-zero": options.MaxResults = 0; break;
            case "max-results-too-large": options.MaxResults = LeaveDisplayOptions.MaxResultsLimit + 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(flaw), flaw, null);
        }

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validation_message_names_the_setting_but_not_its_value()
    {
        var options = Valid();
        options.Account = "secret-person@example.test ";
        options.Executable = Environment.ProcessPath!;
        options.TimeoutSeconds = 0;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(nameof(LeaveDisplayOptions.TimeoutSeconds), ex.Message);
        Assert.DoesNotContain("secret-person", ex.Message);
    }

    [Fact]
    public void Binds_from_configuration_and_keeps_defaults_for_missing_keys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LeaveDisplay:Enabled"] = "true",
                ["LeaveDisplay:Executable"] = "/opt/mail/search",
                ["LeaveDisplay:Arguments:0"] = "-NoProfile",
                ["LeaveDisplay:Arguments:1"] = "-File",
                ["LeaveDisplay:Account"] = "tester@example.test",
            })
            .Build();

        var options = configuration.GetSection(LeaveDisplayOptions.SectionName).Get<LeaveDisplayOptions>();

        Assert.NotNull(options);
        Assert.True(options.Enabled);
        Assert.Equal(["-NoProfile", "-File"], options.Arguments);
        Assert.Equal("snuailab", options.Provider);
        Assert.Equal(90, options.TimeoutSeconds);
        Assert.Equal(1000, options.MaxResults);
    }

    [Fact]
    public void Without_any_configuration_the_feature_is_off()
    {
        var options = new ConfigurationBuilder().Build()
            .GetSection(LeaveDisplayOptions.SectionName).Get<LeaveDisplayOptions>() ?? new LeaveDisplayOptions();

        Assert.False(options.Enabled);
        Assert.Empty(options.Arguments);
    }
}
