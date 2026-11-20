using System.Globalization;
using SelectLunch.Shared.Search;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// 식사 확정 창. 기존 선택 메시지에서 고른 식당(또는 당일 정정이면 기록된 목록)을 미리 담고,
/// 검색으로 식당을 더 고른 뒤 저장한다. 창을 열고 닫는 것만으로는 아무것도 기록되지 않는다.
///
/// 식당 선택 입력(<see cref="PlacesInput"/>)과 날짜 메타데이터(<see cref="Metadata"/>·<see cref="TryDate"/>)는
/// 신규 식당 등록 창(<see cref="RestaurantModal"/>)과 같은 모양을 쓰도록 공용으로 둔다.
/// </summary>
public static class MealConfirmationModal
{
    public const string CallbackId = "meal_confirm";

    public const string PlacesBlockId = "meal_places";

    /// <summary>
    /// 식당 검색 제안의 action_id. <c>meal:</c> 접두사가 없으므로 기존 기록 버튼·선택(<see cref="ActionIds.TryParseMeal"/>)과
    /// 섞이지 않고, <see cref="Handlers.RestaurantOptionProvider"/>가 이 값으로 알아본다.
    /// </summary>
    public const string PlacesActionId = "meal_places_input";

    const string MetadataPrefix = "meal";
    const string MetadataDateFormat = "yyyyMMdd";

    /// <summary>
    /// 선택 항목의 라벨. 검색 제안과 <see cref="PlacesInput"/>의 초기 선택이 **같은 함수**를 써야 한다 —
    /// 슬랙은 초기 선택이 제안 옵션과 정확히 일치하길 요구한다.
    /// </summary>
    public static string PlaceLabel(string name) => RestaurantSearch.Truncate(name, RestaurantSearch.MaxLabelLength);

    public static ModalViewDefinition Build(
        DateOnly date, string channel, string user, MealSelectionState state, IReadOnlyList<MealPlace> initial)
    {
        var mealDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var view = new ModalViewDefinition
        {
            CallbackId = CallbackId,
            Title = new PlainText($"{mealDate} 식사 기록"),
            Submit = new PlainText("저장"),
            Close = new PlainText("취소"),
            PrivateMetadata = Metadata(date, channel, user),
        };

        view.Blocks.Add(new SectionBlock
        {
            Text = new PlainText(state.Recorded.Count > 0
                ? $"{mealDate}에 이미 기록된 식당에서 시작합니다. 저장하면 이 목록 전체로 정정됩니다."
                : $"{mealDate}에 실제로 다녀온 식당을 확인해 주세요. 저장하기 전에는 아무것도 기록되지 않습니다."),
        });

        if (!state.IsToday)
        {
            view.Blocks.Add(new SectionBlock
            {
                Text = new PlainText("지난 날짜의 최초 기록입니다. 저장한 뒤에는 이 날짜의 기록을 다시 바꿀 수 없습니다."),
            });
        }

        // 설정을 낮춘 뒤 열면 기존 기록이 한도보다 많을 수 있다. 기존 기록을 자르지 않고 그대로 보여 주고,
        // 저장은 서버가 한도로 막으므로 사용자가 직접 줄이게 안내한다.
        if (initial.Count > state.Limit)
        {
            view.Blocks.Add(new SectionBlock
            {
                Text = new PlainText(
                    $"현재 기준으로는 식당을 최대 {state.Limit}곳까지만 확정할 수 있습니다. " +
                    $"기록된 {initial.Count}곳 중 {initial.Count - state.Limit}곳 이상을 빼야 저장할 수 있습니다."),
            });
        }

        view.Blocks.Add(PlacesInput(state.Limit, initial, optional: false));
        return view;
    }

    /// <summary>
    /// 식당 다중 선택 입력. 식당은 외부 검색으로 고르므로 식당 수에 상한이 없고, 선택은 최대
    /// <paramref name="limit"/>곳이다. <paramref name="initial"/>이 한도보다 많아도 슬랙이 창을 거부하지 않도록
    /// 선택 상한은 초기 선택 수 이상으로 둔다 — 한도 초과 저장은 서버가 막는다.
    /// </summary>
    /// <param name="optional">true면 아무것도 고르지 않아도 제출할 수 있다(새 식당 등록 창의 추가 식당).</param>
    public static InputBlock PlacesInput(int limit, IReadOnlyList<MealPlace> initial, bool optional)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentNullException.ThrowIfNull(initial);

        var menu = new ExternalMultiSelectMenu
        {
            ActionId = PlacesActionId,
            Placeholder = new PlainText("식당 이름·음식 종류로 검색"),
            // 0이면 열자마자 전체 목록이 떠서 두 글자 검색("순대"·"국밥")도 막지 않는다.
            MinQueryLength = 0,
            MaxSelectedItems = Math.Max(limit, initial.Count),
        };

        if (initial.Count > 0)
        {
            var preselected = new Option[initial.Count];
            for (var i = 0; i < initial.Count; i++)
            {
                var place = initial[i];
                preselected[i] = new Option
                {
                    Text = new PlainText(PlaceLabel(place.Name)),
                    Value = place.Id.ToString(CultureInfo.InvariantCulture),
                };
            }
            menu.InitialOptions = preselected;
        }

        return new InputBlock
        {
            BlockId = PlacesBlockId,
            Label = new PlainText("식사한 식당"),
            Hint = new PlainText($"최대 {limit}곳까지 고를 수 있습니다."),
            Optional = optional,
            Element = menu,
        };
    }

    /// <summary>
    /// 제출된 창에서 고른 식당 id를 순서대로 읽는다. 선택 입력이 없거나 비어 있으면 빈 배열과 true,
    /// 선택 값이 식당 id 형식이 아니거나 입력 형식이 다르면 false(이때 <paramref name="ids"/>는 비어 있다).
    /// 중복·실제 존재 여부는 저장 때 서버가 검사한다.
    /// </summary>
    public static bool TryReadPlaces(ViewSubmission submission, out long[] ids)
    {
        ids = [];

        var values = submission.View?.State?.Values;
        if (values is null || !values.TryGetValue(PlacesBlockId, out var block))
            return true; // 한 곳만 가능한 신규 등록 창에는 추가 선택 블록이 없다.
        if (block is null || !block.TryGetValue(PlacesActionId, out var element)
            || element is not ExternalMultiSelectValue selection)
            return false;
        if (selection.SelectedOptions is not { Count: > 0 } options)
            return true;

        var parsed = new long[options.Count];
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            if (option?.Value is not { } text
                || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
            {
                return false;
            }

            parsed[i] = id;
        }

        ids = parsed;
        return true;
    }

    /// <summary>
    /// 창의 private_metadata. 날짜와 함께 창을 연 채널·사용자를 실어 제출 때 대조한다.
    /// 사용자는 마지막 칸이라 구분자가 섞여도 다른 칸을 밀어내지 않는다.
    /// </summary>
    public static string Metadata(DateOnly date, string channel, string user) =>
        string.Create(CultureInfo.InvariantCulture, $"{MetadataPrefix}:{date:yyyyMMdd}:{channel}:{user}");

    /// <summary>
    /// <see cref="Metadata"/>가 만든 정식 표기이고 채널·사용자가 모두 기대와 같을 때만 날짜를 돌려준다.
    /// 다른 사용자·채널의 창, 비정형 값, 없는 값은 거절하고 날짜를 비운다.
    /// </summary>
    public static bool TryDate(string? metadata, string channel, string user, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrEmpty(metadata) || string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(user))
            return false;

        var text = metadata.AsSpan();
        Span<Range> parts = stackalloc Range[4];
        return text.Split(parts, ':') == 4
            && text[parts[0]].SequenceEqual(MetadataPrefix)
            && text[parts[2]].SequenceEqual(channel)
            && text[parts[3]].SequenceEqual(user)
            && DateOnly.TryParseExact(text[parts[1]], MetadataDateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
    }
}
