using System.Globalization;
using SelectLunch.Shared;
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Blocks;

public sealed record RestaurantDraft(
    long? RestaurantId,
    string Name,
    long? CategoryId,
    int? WalkMinutes,
    int? PriceLevel,
    string? Note,
    string? Address = null,
    WaitLevel? WaitLevel = null,
    string? NewCategoryName = null,
    TimeOnly? DepartureTime = null,
    int EarlyDepartureSeconds = 0);

/// <summary>
/// 일반 식당 등록·수정 모달이 어떤 흐름에서 열렸는지. private_metadata로 왕복한다.
/// 식사 기록용 등록 창은 이 값이 아니라 <see cref="RestaurantModal.MealCallbackId"/>와
/// <see cref="MealConfirmationModal.Metadata"/>를 쓴다.
/// </summary>
public closed record ModalContext
{
    private sealed record Registration : ModalContext;
    public sealed record Edit(long RestaurantId) : ModalContext;

    public static readonly ModalContext None = new Registration();

    public static ModalContext ForEdit(long restaurantId) => new Edit(restaurantId);

    public string Serialize() => this switch
    {
        Registration => "",
        Edit(var id) => $"restaurant:{id}",
    };

    /// <summary>
    /// 비어 있으면 일반 등록이다. 비어 있지 않은데 알아볼 수 없는 값(구형 날짜 전용 창·식사 기록용 메타데이터 포함)도
    /// <see cref="None"/>이 되므로, 제출을 받는 쪽이 비어 있지 않은 <see cref="None"/>을 따로 거절해야 한다.
    /// </summary>
    public static ModalContext Parse(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return None;

        var parts = metadata.Split(':', 2);
        if (parts.Length != 2)
            return None;

        return parts[0] switch
        {
            "restaurant" when long.TryParse(
                parts[1], CultureInfo.InvariantCulture, out var id) => ForEdit(id),
            _ => None,
        };
    }
}

/// <summary>
/// 식당 등록·수정 모달. 이름은 필수이고, 카테고리는 "기존 선택"과 "새로 입력" 중
/// 정확히 하나만 채워야 한다(<see cref="ValidateCategory"/>).
/// </summary>
public static class RestaurantModal
{
    public const string CallbackId = "restaurant_form";

    /// <summary>
    /// 식사 기록용 신규 식당 등록 창의 callback. 일반 등록·수정(<see cref="CallbackId"/>)과 제출 처리가 다르므로
    /// 같은 callback을 쓰지 않는다 — 날짜·채널·사용자 메타데이터가 틀린 제출이 일반 등록으로 떨어지지 않게 한다.
    /// </summary>
    public const string MealCallbackId = "restaurant_meal_form";

    public static class BlockIds
    {
        public const string Name = "name";
        public const string Category = "category";
        public const string CategoryNew = "category_new";
        public const string Address = "address";
        public const string WaitLevel = "wait_level";
        public const string WalkMinutes = "walk_minutes";
        public const string DepartureTime = "departure_time";
        public const string EarlyDeparture = "early_departure";
        public const string PriceLevel = "price_level";
        public const string Note = "note";
    }

    const string ActionSuffix = "_input";

    /// <summary>대기 수준 "모름". Slack 선택 메뉴는 선택 해제가 안 되므로 명시적 항목이 필요하다.</summary>
    const string WaitUnknownValue = "unknown";

    /// <summary>새 카테고리 이름 최대 길이. Category.Name 컬럼 길이와 같다.</summary>
    public const int MaxCategoryNameLength = 50;

    /// <summary>출발 시각 입력 형식. 24시간제 HH:mm, Lunch TimeZone 기준 현지 시각이다.</summary>
    const string DepartureTimeFormat = "HH:mm";

    public static ModalViewDefinition Build(
        IReadOnlyList<Category> categories,
        RestaurantDraft? existing,
        ModalContext context)
    {
        var view = new ModalViewDefinition
        {
            CallbackId = CallbackId,
            Title = new PlainText(existing?.RestaurantId is null ? "식당 등록" : "식당 수정"),
            Submit = new PlainText("저장"),
            Close = new PlainText("취소"),
            PrivateMetadata = context.Serialize(),
        };
        AddFormBlocks(view.Blocks, categories, existing);

        return view;
    }

    /// <summary>
    /// 식사 기록 중 새 식당을 등록하는 창. 새 식당 한 곳이 확정 한도(<see cref="MealSelectionState.Limit"/>)에서 먼저
    /// 한 자리를 차지하므로, 한도가 2 이상일 때만 남은 자리 수만큼 기존 식당을 선택 사항으로 더 고르는 입력을 붙인다.
    /// 이미 기록된 식당은 전부 그 입력에 미리 담는다 — 저장은 이 날짜의 기록 전체를 교체하므로 말없이 자르지 않고,
    /// 자리가 모자라면 사람이 직접 빼도록 안내한다. 남는 자리가 없으면 입력 없이 어느 식당이 교체되는지 보여 준다.
    /// 창을 연 날짜·채널·사용자는 private_metadata에 실려 제출 때 <see cref="MealConfirmationModal.TryDate"/>로 대조한다.
    /// 창을 여는 것만으로는 아무것도 쓰지 않는다.
    /// </summary>
    public static ModalViewDefinition BuildForMeal(
        IReadOnlyList<Category> categories,
        DateOnly date,
        string channelId,
        string userId,
        MealSelectionState state)
    {
        var mealDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var view = new ModalViewDefinition
        {
            CallbackId = MealCallbackId,
            Title = new PlainText($"{mealDate} 식사 기록"),
            Submit = new PlainText("저장"),
            Close = new PlainText("취소"),
            PrivateMetadata = MealConfirmationModal.Metadata(date, channelId, userId),
        };

        // 새 식당이 한 자리를 쓴다.
        var extras = state.Limit - 1;
        var recorded = state.Recorded;
        if (recorded.Count > 0 && extras >= 1)
        {
            view.Blocks.Add(new SectionBlock
            {
                Text = new PlainText(
                    $"{mealDate}에 이미 기록된 식당이 아래 선택에 담겨 있습니다. 새 식당을 저장하면 이 날짜의 기록 전체가 " +
                    "새 식당과 아래 선택으로 정정됩니다."),
            });

            if (recorded.Count > extras)
            {
                view.Blocks.Add(new SectionBlock
                {
                    Text = new PlainText(
                        $"현재 기준으로는 새 식당을 포함해 최대 {state.Limit}곳까지만 기록할 수 있습니다. " +
                        $"기록된 {recorded.Count}곳 중 {recorded.Count - extras}곳 이상을 아래 선택에서 빼야 저장할 수 있습니다."),
                });
            }
        }
        else if (recorded.Count > 0)
        {
            var sections = new PlainTextSections(view.Blocks, 100);
            sections.AddLine(
                $"{mealDate}에는 새 식당 한 곳만 기록할 수 있습니다. 이미 기록된 아래 식당은 새 식당을 저장하면 새 식당으로 교체됩니다.");
            foreach (var place in recorded)
                sections.AddLine(place.Name);
            sections.Flush();
        }

        if (!state.IsToday)
        {
            view.Blocks.Add(new SectionBlock
            {
                Text = new PlainText("지난 날짜의 최초 기록입니다. 저장한 뒤에는 이 날짜의 기록을 다시 바꿀 수 없습니다."),
            });
        }

        AddFormBlocks(view.Blocks, categories, existing: null);

        if (extras >= 1)
            view.Blocks.Add(MealConfirmationModal.PlacesInput(extras, recorded, optional: true));

        return view;
    }

    static void AddFormBlocks(IList<Block> blocks, IReadOnlyList<Category> categories, RestaurantDraft? existing)
    {
        var categoryMenu = new StaticSelectMenu
        {
            ActionId = BlockIds.Category + ActionSuffix,
            Placeholder = new PlainText("카테고리를 고르세요"),
        };

        foreach (var category in categories)
        {
            var option = new Option
            {
                Text = new PlainText(category.Name),
                Value = category.Id.ToString(CultureInfo.InvariantCulture),
            };
            categoryMenu.Options.Add(option);

            if (existing?.CategoryId == category.Id)
                categoryMenu.InitialOption = option;
        }

        var waitMenu = new StaticSelectMenu
        {
            ActionId = BlockIds.WaitLevel + ActionSuffix,
            Placeholder = new PlainText("대기 수준"),
        };
        waitMenu.Options.Add(new Option { Text = new PlainText("모름"), Value = WaitUnknownValue });
        foreach (var level in Enum.GetValues<WaitLevel>())
        {
            var option = new Option
            {
                Text = new PlainText(level.ToDisplay()),
                Value = ((int)level).ToString(CultureInfo.InvariantCulture),
            };
            waitMenu.Options.Add(option);

            if (existing?.WaitLevel == level)
                waitMenu.InitialOption = option;
        }

        blocks.Add(Text(BlockIds.Name, "이름", existing?.Name, optional: false, "예) 스시로"));
        blocks.Add(new InputBlock
        {
            BlockId = BlockIds.Category,
            Label = new PlainText("카테고리"),
            Optional = true,
            Element = categoryMenu,
        });
        blocks.Add(Text(BlockIds.CategoryNew, "새 카테고리 (직접 입력)", null, optional: true, "예) 순대국 — 위 선택과 둘 중 하나만"));
        blocks.Add(Text(BlockIds.Address, "주소", existing?.Address, optional: true, "예) 경기 수원시 영통구 …"));
        blocks.Add(new InputBlock
        {
            BlockId = BlockIds.WaitLevel,
            Label = new PlainText("대기 시간"),
            Optional = true,
            Element = waitMenu,
        });
        blocks.Add(Text(BlockIds.WalkMinutes, "도보 시간(분)", existing?.WalkMinutes?.ToString(), optional: true, "예) 5"));
        blocks.Add(Text(
            BlockIds.DepartureTime, "출발 시각 (현지 시각, 24시간)",
            existing?.DepartureTime?.ToString(DepartureTimeFormat, CultureInfo.InvariantCulture),
            optional: true, "예) 11:15 — 비우면 지정 없음"));
        blocks.Add(Text(BlockIds.EarlyDeparture, "미리 출발(분)",
            existing is { EarlyDepartureSeconds: > 0 }
                ? EarlyDepartureDuration.FormatMinutes(existing.EarlyDepartureSeconds)
                : null,
            optional: true, "예) 10 — 비우거나 0이면 안내 없음"));
        blocks.Add(Text(BlockIds.PriceLevel, "가격대(1~4)", existing?.PriceLevel?.ToString(), optional: true, "예) 2"));
        blocks.Add(Text(BlockIds.Note, "메모", existing?.Note, optional: true, "예) 점심 특선 있음"));
    }

    /// <summary>
    /// 제출된 모달에서 초안을 읽는다. 숫자 필드는 형식이 틀리면 null로 둔다.
    /// 출발 시각도 형식이 틀리면 null이 되므로, 저장 전에 반드시 <see cref="ValidateDepartureTime"/>을 먼저 통과시켜야 한다.
    /// </summary>
    public static RestaurantDraft Parse(ViewSubmission submission)
    {
        var state = submission.View.State;
        var context = ModalContext.Parse(submission.View.PrivateMetadata);

        return new RestaurantDraft(
            context is ModalContext.Edit(var id) ? id : null,
            Value(state, BlockIds.Name) ?? "",
            long.TryParse(Selected(state, BlockIds.Category), CultureInfo.InvariantCulture, out var categoryId)
                ? categoryId
                : null,
            int.TryParse(Value(state, BlockIds.WalkMinutes), CultureInfo.InvariantCulture, out var walk)
                ? walk
                : null,
            int.TryParse(Value(state, BlockIds.PriceLevel), CultureInfo.InvariantCulture, out var price)
                ? Math.Clamp(price, 1, 4)
                : null,
            Value(state, BlockIds.Note),
            Value(state, BlockIds.Address),
            ParseWaitLevel(Selected(state, BlockIds.WaitLevel)),
            Value(state, BlockIds.CategoryNew)?.Trim(),
            TryReadDepartureTime(state, out var departureTime) ? departureTime : null,
            EarlyDepartureDuration.TryParseMinutes(Value(state, BlockIds.EarlyDeparture), out var earlyDepartureSeconds)
                ? earlyDepartureSeconds
                : 0);
    }

    static WaitLevel? ParseWaitLevel(string? value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var number) && Enum.IsDefined((WaitLevel)number)
            ? (WaitLevel)number
            : null;

    /// <summary>
    /// 출발 시각 입력을 읽는다. 비어 있으면(공백만 포함) "지정 없음"이라 true와 null을,
    /// HH:mm 24시간 형식이면 true와 그 시각을, 그 밖의 원문은 false를 돌려준다.
    /// </summary>
    static bool TryReadDepartureTime(ViewState state, out TimeOnly? time)
    {
        var text = Value(state, BlockIds.DepartureTime)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            time = null;
            return true;
        }

        // TimeOnly는 .NET 11부터 24:00을 자정으로 받아들이지만, 이 입력란은 00~23시만 허용한다.
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var parsed))
        {
            time = TimeOnly.FromTimeSpan(parsed);
            return true;
        }

        time = null;
        return false;
    }

    /// <summary>
    /// 출발 시각 원문이 비었거나 HH:mm 24시간 형식인지 확인한다.
    /// 어긋나면 (오류를 띄울 블록 ID, 메시지)를, 통과하면 null을 돌려준다.
    /// 값을 말없이 null로 바꾸면 "지정 해제"로 저장되므로 형식 오류는 저장 전에 막아야 한다.
    /// </summary>
    public static (string BlockId, string Message)? ValidateDepartureTime(ViewSubmission submission) =>
        TryReadDepartureTime(submission.View.State, out _)
            ? null
            : (BlockIds.DepartureTime, "출발 시각은 24시간 HH:mm 형식으로 입력해 주세요. 예) 11:15 (비우면 지정 없음)");

    public static (string BlockId, string Message)? ValidateEarlyDeparture(ViewSubmission submission) =>
        EarlyDepartureDuration.TryParseMinutes(Value(submission.View.State, BlockIds.EarlyDeparture), out _)
            ? null
            : (BlockIds.EarlyDeparture, "미리 출발 시간은 0 이상의 분으로 입력해 주세요.");

    /// <summary>
    /// 카테고리는 "기존 선택"과 "새로 입력" 중 정확히 하나여야 한다.
    /// 어긋나면 (오류를 띄울 블록 ID, 메시지)를, 통과하면 null을 돌려준다.
    /// </summary>
    /// <param name="requireCategory">
    /// false면 둘 다 비어도 통과한다 — 식사 기록용 등록(<see cref="RestaurantModal.BuildForMeal"/>)에서는
    /// 분류를 강요하면 "기타"로 대충 넘겨 추천 이력을 오염시키므로 Pending으로 미룬다.
    /// "둘 다 채움"은 어느 흐름에서나 오류다.
    /// </param>
    public static (string BlockId, string Message)? ValidateCategory(RestaurantDraft draft, bool requireCategory = true)
    {
        var hasNew = !string.IsNullOrWhiteSpace(draft.NewCategoryName);

        if (draft.CategoryId is null && !hasNew)
            return !requireCategory ? null : (BlockIds.Category, "카테고리를 선택하거나 새로 입력해 주세요.");

        if (draft.CategoryId is not null && hasNew)
            return (BlockIds.CategoryNew, "카테고리는 선택과 직접 입력 중 하나만 채워 주세요.");

        if (hasNew && draft.NewCategoryName!.Trim().Length > MaxCategoryNameLength)
            return (BlockIds.CategoryNew, $"카테고리 이름은 {MaxCategoryNameLength}자 이하로 입력해 주세요.");

        return null;
    }

    static InputBlock Text(string blockId, string label, string? initial, bool optional, string placeholder) =>
        new()
        {
            BlockId = blockId,
            Label = new PlainText(label),
            Optional = optional,
            Element = new PlainTextInput
            {
                ActionId = blockId + ActionSuffix,
                InitialValue = initial ?? "",
                Placeholder = new PlainText(placeholder),
            },
        };

    static string? Value(ViewState state, string blockId) =>
        state.GetValue<PlainTextInputValue>(blockId, blockId + ActionSuffix)?.Value is { Length: > 0 } text
            ? text
            : null;

    static string? Selected(ViewState state, string blockId) =>
        state.GetValue<StaticSelectValue>(blockId, blockId + ActionSuffix)?.SelectedOption?.Value;
}
