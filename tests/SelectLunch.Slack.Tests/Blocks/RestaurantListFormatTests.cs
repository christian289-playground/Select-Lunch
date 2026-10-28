using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Handlers;

namespace SelectLunch.Slack.Tests;

public class RestaurantListFormatTests
{
    static Restaurant R(string name, string category, string? address = null, WaitLevel? wait = null) => new()
    {
        Name = name, NormalizedName = Restaurant.Normalize(name), CreatedBySlackUserId = "U1",
        Category = new Category { Name = category, NormalizedName = Category.Normalize(category) },
        Address = address, WaitLevel = wait,
    };

    [Fact]
    public void 주소와_대기수준은_있을_때만_표시한다()
    {
        var text = LunchSlashCommandHandler.FormatList(
        [
            R("가게A", "버거", "수원시 1", WaitLevel.Severe),
            R("가게B", "버거", null, null),
            R("가게C", "버거", "수원시 3", null),
        ]);

        Assert.Contains("• 가게A — 수원시 1 · 당장 출발하세요 (대기 엄청김)", text);
        Assert.Contains("• 가게B\n", text + "\n");
        Assert.DoesNotContain("가게B —", text);
        Assert.Contains("• 가게C — 수원시 3", text);
        Assert.DoesNotContain("모름", text);
    }

    [Fact]
    public void 미리출발을_분으로_표시하고_남은_초를_버리지_않는다()
    {
        var whole = R("정수", "한식");
        whole.EarlyDepartureSeconds = 600;
        var partial = R("나머지", "한식");
        partial.EarlyDepartureSeconds = 61;
        var none = R("없음", "한식");

        var text = LunchSlashCommandHandler.FormatList([whole, partial, none]);

        Assert.Contains("10분", text);
        Assert.Contains("1.0167분", text);
        Assert.DoesNotContain("없음 —", text);
    }

    [Fact]
    public void 식당이_없으면_안내한다()
    {
        Assert.Contains("등록된 식당이 없습니다", LunchSlashCommandHandler.FormatList([]));
    }
}
