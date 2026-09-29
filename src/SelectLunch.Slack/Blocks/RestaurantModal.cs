using System.Globalization;
using SelectLunch.Shared.Entities;
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
    string? NewCategoryName = null);

/// <summary>
/// 모달이 어떤 흐름에서 열렸는지. private_metadata로 왕복한다.
/// 날짜(기록 흐름)와 식당 ID(수정 흐름)가 같은 자리를 쓰므로 접두사로 구분한다.
/// </summary>
public sealed record ModalContext(DateOnly? RecordFor, long? RestaurantId)
{
    public static readonly ModalContext None = new(null, null);

    public static ModalContext ForRecord(DateOnly date) => new(date, null);

    public static ModalContext ForEdit(long restaurantId) => new(null, restaurantId);

    public string Serialize() =>
        RecordFor is { } date ? $"date:{date:yyyyMMdd}"
        : RestaurantId is { } id ? $"restaurant:{id}"
        : "";

    public static ModalContext Parse(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return None;

        var parts = metadata.Split(':', 2);
        if (parts.Length != 2)
            return None;

        return parts[0] switch
        {
            "date" when DateOnly.TryParseExact(
                parts[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) => ForRecord(date),
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

    public static class BlockIds
    {
        public const string Name = "name";
        public const string Category = "category";
        public const string CategoryNew = "category_new";
        public const string Address = "address";
        public const string WaitLevel = "wait_level";
        public const string WalkMinutes = "walk_minutes";
        public const string PriceLevel = "price_level";
        public const string Note = "note";
    }

    const string ActionSuffix = "_input";

    /// <summary>대기 수준 "모름". Slack 선택 메뉴는 선택 해제가 안 되므로 명시적 항목이 필요하다.</summary>
    const string WaitUnknownValue = "unknown";

    /// <summary>새 카테고리 이름 최대 길이. Category.Name 컬럼 길이와 같다.</summary>
    public const int MaxCategoryNameLength = 50;

    public static ModalViewDefinition Build(
        IReadOnlyList<Category> categories,
        RestaurantDraft? existing,
        ModalContext context)
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

        return new ModalViewDefinition
        {
            CallbackId = CallbackId,
            Title = new PlainText(existing?.RestaurantId is null ? "식당 등록" : "식당 수정"),
            Submit = new PlainText("저장"),
            Close = new PlainText("취소"),
            PrivateMetadata = context.Serialize(),
            Blocks =
            {
                Text(BlockIds.Name, "이름", existing?.Name, optional: false, "예) 스시로"),
                new InputBlock
                {
                    BlockId = BlockIds.Category,
                    Label = new PlainText("카테고리"),
                    Optional = true,
                    Element = categoryMenu,
                },
                Text(BlockIds.CategoryNew, "새 카테고리 (직접 입력)", null, optional: true, "예) 순대국 — 위 선택과 둘 중 하나만"),
                Text(BlockIds.Address, "주소", existing?.Address, optional: true, "예) 경기 수원시 영통구 …"),
                new InputBlock
                {
                    BlockId = BlockIds.WaitLevel,
                    Label = new PlainText("대기 시간"),
                    Optional = true,
                    Element = waitMenu,
                },
                Text(BlockIds.WalkMinutes, "도보 시간(분)", existing?.WalkMinutes?.ToString(), optional: true, "예) 5"),
                Text(BlockIds.PriceLevel, "가격대(1~4)", existing?.PriceLevel?.ToString(), optional: true, "예) 2"),
                Text(BlockIds.Note, "메모", existing?.Note, optional: true, "예) 점심 특선 있음"),
            },
        };
    }

    /// <summary>제출된 모달에서 초안을 읽는다. 숫자 필드는 형식이 틀리면 null로 둔다.</summary>
    public static RestaurantDraft Parse(ViewSubmission submission)
    {
        var state = submission.View.State;
        var context = ModalContext.Parse(submission.View.PrivateMetadata);

        return new RestaurantDraft(
            context.RestaurantId,
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
            Value(state, BlockIds.CategoryNew)?.Trim());
    }

    static WaitLevel? ParseWaitLevel(string? value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var number) && Enum.IsDefined((WaitLevel)number)
            ? (WaitLevel)number
            : null;

    /// <summary>
    /// 카테고리는 "기존 선택"과 "새로 입력" 중 정확히 하나여야 한다.
    /// 어긋나면 (오류를 띄울 블록 ID, 메시지)를, 통과하면 null을 돌려준다.
    /// </summary>
    public static (string BlockId, string Message)? ValidateCategory(RestaurantDraft draft)
    {
        var hasNew = !string.IsNullOrWhiteSpace(draft.NewCategoryName);

        if (draft.CategoryId is null && !hasNew)
            return (BlockIds.Category, "카테고리를 선택하거나 새로 입력해 주세요.");

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
