using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Tests;

public class RestaurantModalTests
{
    const string InputSuffix = "_input";

    static IReadOnlyList<Category> Categories() =>
    [
        new() { Id = 1, Name = "한식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
        new() { Id = 3, Name = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
    ];

    static ViewState StateWith(params (string BlockId, ElementValue Value)[] values)
    {
        var state = new ViewState { Values = [] };

        foreach (var (blockId, value) in values)
            state.Values[blockId] = new Dictionary<string, ElementValue> { [blockId + InputSuffix] = value };

        return state;
    }

    static ViewSubmission Submission(string? privateMetadata, ViewState state) =>
        new()
        {
            View = new ModalViewInfo
            {
                PrivateMetadata = privateMetadata ?? "",
                State = state,
            },
        };

    [Fact]
    public void 이름과_카테고리_입력이_있다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var blockIds = view.Blocks.OfType<InputBlock>().Select(b => b.BlockId).ToList();
        Assert.Contains(RestaurantModal.BlockIds.Name, blockIds);
        Assert.Contains(RestaurantModal.BlockIds.Category, blockIds);
    }

    [Fact]
    public void 이름과_카테고리는_필수이고_나머지는_선택이다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var inputs = view.Blocks.OfType<InputBlock>().ToDictionary(b => b.BlockId);
        Assert.False(inputs[RestaurantModal.BlockIds.Name].Optional);
        Assert.False(inputs[RestaurantModal.BlockIds.Category].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.WalkMinutes].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.Note].Optional);
    }

    [Fact]
    public void 카테고리_선택지는_전달된_목록에서_나온다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var menu = view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.Category)
            .Element as StaticSelectMenu;
        Assert.Equal(2, menu!.Options.Count);
    }

    [Fact]
    public void 기존_값이_있으면_이름이_채워진다()
    {
        var draft = new RestaurantDraft(7, "스시로", 3, 5, 2, "회전초밥");

        var view = RestaurantModal.Build(Categories(), draft, ModalContext.None);

        var input = view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.Name)
            .Element as PlainTextInput;
        Assert.Equal("스시로", input!.InitialValue);
    }

    [Fact]
    public void 콜백_아이디가_고정되어_있다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        Assert.Equal(RestaurantModal.CallbackId, view.CallbackId);
    }

    [Fact]
    public void 기록_흐름의_컨텍스트가_private_metadata에_실린다()
    {
        var view = RestaurantModal.Build(
            Categories(), existing: null, ModalContext.ForRecord(new DateOnly(2026, 9, 18)));

        Assert.Equal("date:20260918", view.PrivateMetadata);
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void 모달_컨텍스트를_왕복_변환한다(ModalContext context)
    {
        Assert.Equal(context, ModalContext.Parse(context.Serialize()));
    }

    public static TheoryData<ModalContext> Contexts() =>
    [
        ModalContext.None,
        ModalContext.ForRecord(new DateOnly(2026, 9, 18)),
        ModalContext.ForEdit(7),
    ];

    [Theory]
    [InlineData("garbage")]
    [InlineData("date:nope")]
    [InlineData("restaurant:abc")]
    public void 형식이_깨진_컨텍스트는_None이_된다(string metadata)
    {
        Assert.Equal(ModalContext.None, ModalContext.Parse(metadata));
    }

    [Fact]
    public void Parse가_모든_필드를_읽어_초안으로_돌려준다()
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }),
            (RestaurantModal.BlockIds.Category, new StaticSelectValue { SelectedOption = new Option { Value = "3" } }),
            (RestaurantModal.BlockIds.WalkMinutes, new PlainTextInputValue { Value = "5" }),
            (RestaurantModal.BlockIds.PriceLevel, new PlainTextInputValue { Value = "2" }),
            (RestaurantModal.BlockIds.Note, new PlainTextInputValue { Value = "회전초밥" }));

        var draft = RestaurantModal.Parse(Submission("date:20260918", state));

        Assert.Equal(new RestaurantDraft(null, "스시로", 3, 5, 2, "회전초밥"), draft);
    }

    [Fact]
    public void 숫자가_아닌_선택_입력은_예외_없이_null이_된다()
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }),
            (RestaurantModal.BlockIds.Category, new StaticSelectValue { SelectedOption = new Option { Value = "3" } }),
            (RestaurantModal.BlockIds.WalkMinutes, new PlainTextInputValue { Value = "빠름" }),
            (RestaurantModal.BlockIds.PriceLevel, new PlainTextInputValue { Value = "" }),
            (RestaurantModal.BlockIds.Note, new PlainTextInputValue { Value = "" }));

        var draft = RestaurantModal.Parse(Submission(null, state));

        Assert.Null(draft.WalkMinutes);
        Assert.Null(draft.PriceLevel);
        Assert.Null(draft.Note);
    }

    [Fact]
    public void 카테고리를_고르지_않아도_예외_없이_null이_된다()
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }),
            (RestaurantModal.BlockIds.Category, new StaticSelectValue { SelectedOption = null }));

        var draft = RestaurantModal.Parse(Submission(null, state));

        Assert.Null(draft.CategoryId);
    }

    [Theory]
    [InlineData("date:20260918", null)]
    [InlineData("restaurant:7", 7L)]
    public void 컨텍스트에_따라_RestaurantId가_결정된다(string metadata, long? expectedRestaurantId)
    {
        var state = StateWith((RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }));

        var draft = RestaurantModal.Parse(Submission(metadata, state));

        Assert.Equal(expectedRestaurantId, draft.RestaurantId);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("date:nope")]
    [InlineData("restaurant:abc")]
    public void 형식이_깨진_private_metadata는_예외_없이_None으로_처리된다(string metadata)
    {
        var state = StateWith((RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }));

        var draft = RestaurantModal.Parse(Submission(metadata, state));

        Assert.Null(draft.RestaurantId);
    }
}
