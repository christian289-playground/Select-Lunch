using SlackNet;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>수정할 식당을 먼저 고르는 창. 선택하기 전에는 저장할 입력폼이 없다.</summary>
public static class RestaurantEditPicker
{
    public const string CallbackId = "restaurant_edit_picker";

    public static ModalViewDefinition Build(bool hasRestaurants, string userId, bool selectionUnavailable = false)
    {
        var view = new ModalViewDefinition
        {
            CallbackId = CallbackId,
            Title = new PlainText("수정할 식당 선택"),
            Close = new PlainText("취소"),
            PrivateMetadata = userId,
            Blocks =
            {
                new SectionBlock
                {
                    Text = new PlainText(!hasRestaurants
                        ? "수정할 식당이 없습니다. /lunch add로 먼저 등록해 주세요."
                        : selectionUnavailable
                            ? "선택한 식당을 더 이상 수정할 수 없습니다. 목록에서 다른 식당을 선택해 주세요."
                            : "등록된 식당을 선택하면 기존 정보가 채워진 수정 창이 열립니다. 상호·음식 종류·주소로 검색할 수 있습니다."),
                },
            },
        };
        if (hasRestaurants)
        {
            view.Blocks.Add(new ActionsBlock
            {
                Elements =
                {
                    new ExternalSelectMenu
                    {
                        ActionId = ActionIds.RestaurantEditSelect,
                        Placeholder = new PlainText("수정할 식당을 선택하거나 검색"),
                        MinQueryLength = 0,
                    },
                },
            });
        }
        return view;
    }
}
