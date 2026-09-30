using Microsoft.Extensions.Configuration;
using SelectLunch.Shared.Options;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 배포되는 <c>appsettings.json</c>과 <see cref="LunchOptions"/>의 코드 기본값이
/// 어긋나지 않는지 본다. 어긋나면 설정이 빠지거나 바인딩에 실패했을 때 봇이
/// 조용히 다른 시각으로 돈다 — 실제로 투표가 10:30에 열려 메뉴가 없는 시각에
/// 투표가 나가는 사고가 이 어긋남에서 나온다.
/// </summary>
public class ShippedSettingsTests
{
    static LunchOptions LoadShipped()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"배포 설정 파일을 찾지 못했다: {path}");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        var options = new LunchOptions();
        configuration.GetSection(LunchOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void 배포_설정의_시각과_주기가_코드_기본값과_같다()
    {
        var shipped = LoadShipped();
        var defaults = new LunchOptions();

        Assert.Equal(defaults.TimeZone, shipped.TimeZone);
        Assert.Equal(defaults.VoteOpenAt, shipped.VoteOpenAt);
        Assert.Equal(defaults.VoteDurationMinutes, shipped.VoteDurationMinutes);
        Assert.Equal(defaults.MealRecordAt, shipped.MealRecordAt);
        Assert.Equal(defaults.MenuCollectionLeadMinutes, shipped.MenuCollectionLeadMinutes);
        Assert.Equal(defaults.PollIntervalSeconds, shipped.PollIntervalSeconds);
        Assert.Equal(defaults.CatchUpGraceMinutes, shipped.CatchUpGraceMinutes);
        Assert.Equal(defaults.WeekdaysOnly, shipped.WeekdaysOnly);
    }

    [Fact]
    public void 배포_설정은_투표_11시_마감_11시30분_수집_10시55분이다()
    {
        var shipped = LoadShipped();

        Assert.Equal(new TimeOnly(11, 0), shipped.VoteOpenAt);
        Assert.Equal(new TimeOnly(11, 30), shipped.VoteCloseAt);
        Assert.Equal(new TimeOnly(10, 55), shipped.MenuCollectionStart);
    }

    [Fact]
    public void 운영_설정은_EF_Core_SQL_로그를_켜지_않는다()
    {
        // 스케줄러가 주기마다 상태를 조회해서, Information으로 두면 같은 SELECT가 끝없이 쌓인다.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.Equal(
            "Warning",
            configuration["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"]);
    }
}
