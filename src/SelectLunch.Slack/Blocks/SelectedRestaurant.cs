using System.Globalization;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// 드롭다운에서 고른 식당 id를 읽는다. 외부 선택(<see cref="ExternalSelectAction"/>)과
/// 예전 <see cref="StaticSelectAction"/>을 **둘 다** 받는다 —
/// 배포 전에 올라간 투표 메시지에는 아직 static_select가 붙어 있고, 그 메시지의
/// 드롭다운을 누르면 static 쪽 payload가 온다. 한쪽만 받으면 그 표가 조용히 사라진다.
/// </summary>
public static class SelectedRestaurant
{
    public static bool TryRead(BlockAction action, out long restaurantId)
    {
        restaurantId = 0;

        var value = action switch
        {
            ExternalSelectAction external => external.SelectedOption?.Value,
            StaticSelectAction stat => stat.SelectedOption?.Value,
            _ => null,
        };

        return value is not null
            && long.TryParse(value, CultureInfo.InvariantCulture, out restaurantId);
    }
}
