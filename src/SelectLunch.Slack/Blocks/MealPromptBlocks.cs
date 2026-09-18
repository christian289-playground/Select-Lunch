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

        if (restaurants.Count > 0)
        {
            blocks.Add(restaurants.Count <= ButtonThreshold
                ? ButtonActions(date, restaurants)
                : SelectActions(date, restaurants));
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

    static ActionsBlock SelectActions(DateOnly date, IReadOnlyList<RestaurantInfo> restaurants)
    {
        // 드롭다운은 action_id 하나로 받고 선택 값에서 식당을 읽는다.
        var menu = new StaticSelectMenu
        {
            ActionId = ActionIds.Meal(date, restaurantId: 0),
            Placeholder = new PlainText("먹은 곳을 고르세요"),
        };

        foreach (var group in restaurants.GroupBy(r => r.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var optionGroup = new OptionGroup
            {
                Label = new PlainText(group.Key),
                Options = [],   // SlackNet은 이 컬렉션을 자동 초기화하지 않는다
            };

            foreach (var restaurant in group.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                optionGroup.Options.Add(new Option
                {
                    Text = new PlainText(restaurant.Name),
                    Value = restaurant.RestaurantId.ToString(),
                });
            }

            menu.OptionGroups.Add(optionGroup);
        }

        return new ActionsBlock { Elements = { menu } };
    }
}
