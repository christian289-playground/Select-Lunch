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
    string? Note);

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

/// <summary>식당 등록·수정 모달. 이름과 카테고리는 필수다.</summary>
public static class RestaurantModal
{
    public const string CallbackId = "restaurant_form";

    public static class BlockIds
    {
        public const string Name = "name";
        public const string Category = "category";
        public const string WalkMinutes = "walk_minutes";
        public const string PriceLevel = "price_level";
        public const string Note = "note";
    }

    const string ActionSuffix = "_input";

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
                    Optional = false,
                    Element = categoryMenu,
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
            Value(state, BlockIds.Note));
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
