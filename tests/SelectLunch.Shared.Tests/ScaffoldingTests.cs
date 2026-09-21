namespace SelectLunch.Shared.Tests;

public class ScaffoldingTests
{
    [Fact]
    public void 한국_타임존을_조회할_수_있다()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

        Assert.Equal(TimeSpan.FromHours(9), tz.BaseUtcOffset);
    }
}
