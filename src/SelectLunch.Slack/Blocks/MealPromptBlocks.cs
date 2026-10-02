using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>오후 기록 요청. 목록 선택과 신규 등록 두 갈래를 함께 제공한다.</summary>
public static class MealPromptBlocks
{
    public const int ButtonThreshold = 10;

    public static IList<Block> Build(
        DateOnly date,
        IReadOnlyList<RestaurantInfo> restaurants,
        string? recordedName)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍜 오늘 뭐 드셨어요?") },
            new SectionBlock
            {
                Text = new Markdown(recordedName is null
                    ? "오늘 먹은 곳을 알려주시면 내일 추천이 정확해집니다."
                    : $"오늘은 *{recordedName}* 으로 기록되어 있습니다. 다르면 아래에서 고쳐 주세요."),
            },
        };

        // 예전에는 100곳을 넘으면 드롭다운을 포기하고 안내문만 남겼다. 외부 선택은
        // 옵션을 메시지에 싣지 않아 그 상한이 사라졌다 — PollBlocks와 같은 이유다.
        if (restaurants.Count > 0)
        {
            blocks.Add(restaurants.Count <= ButtonThreshold
                ? ButtonActions(date, restaurants)
                : SelectActions(date));
        }

        blocks.Add(new ActionsBlock
        {
            Elements =
            {
                new Button
                {
                    ActionId = ActionIds.MealNew(date),
                    Text = new PlainText("➕ 새 식당 등록"),
                    Style = ButtonStyle.Primary,
                },
            },
        });

        blocks.Add(new ContextBlock
        {
            Elements = { new Markdown("등록 시 카테고리를 함께 지정해야 추천에 반영됩니다.") },
        });

        return blocks;
    }

    static ActionsBlock ButtonActions(DateOnly date, IReadOnlyList<RestaurantInfo> restaurants)
    {
        var actions = new ActionsBlock();

        foreach (var restaurant in restaurants)
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.Meal(date, restaurant.RestaurantId),
                Text = new PlainText(restaurant.Name),
            });
        }

        return actions;
    }

    /// <summary>
    /// 기록 드롭다운. 외부 선택이라 옵션을 싣지 않고, 사용자가 친 글자를
    /// <c>block_suggestion</c>으로 받아 <see cref="Handlers.RestaurantOptionProvider"/>가
    /// 부분 일치로 거른다. 이쪽 모집단은 투표 후보 스냅샷이 아니라 **Active 전체**다 —
    /// 오늘 점심은 투표 후보가 아니었던 곳에서 먹었을 수도 있다.
    ///
    /// action_id 하나로 받고 식당은 선택 값에서 읽는다(action_id의 0은 자리표시자).
    /// </summary>
    static ActionsBlock SelectActions(DateOnly date)
    {
        var menu = new ExternalSelectMenu
        {
            ActionId = ActionIds.Meal(date, restaurantId: 0),
            Placeholder = new PlainText("식당 이름·음식 종류로 검색"),
            // 기본 3글자로는 "순대"·"국밥" 같은 두 글자 검색이 막힌다. 0이면
            // 열자마자 전체 목록이 떠서 기존 동작과 그대로 이어진다.
            MinQueryLength = 0,
        };

        return new ActionsBlock { Elements = { menu } };
    }
}
