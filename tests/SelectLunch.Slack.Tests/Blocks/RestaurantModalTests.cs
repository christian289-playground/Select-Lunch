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
        new() { Id = 1, Name = "한식", NormalizedName = "한식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
        new() { Id = 3, Name = "일식", NormalizedName = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
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
    public void 이름만_필수이고_카테고리는_선택과_입력_중_하나라_둘_다_선택항목이다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var inputs = view.Blocks.OfType<InputBlock>().ToDictionary(b => b.BlockId);
        Assert.False(inputs[RestaurantModal.BlockIds.Name].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.Category].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.CategoryNew].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.Address].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.WaitLevel].Optional);
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

    [Fact]
    public void 주소_대기수준_새카테고리도_초안으로_읽는다()
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "순대집" }),
            (RestaurantModal.BlockIds.CategoryNew, new PlainTextInputValue { Value = "  순대국 " }),
            (RestaurantModal.BlockIds.Address, new PlainTextInputValue { Value = "수원시" }),
            (RestaurantModal.BlockIds.WaitLevel, new StaticSelectValue { SelectedOption = new Option { Value = "2" } }));

        var draft = RestaurantModal.Parse(Submission(null, state));

        Assert.Null(draft.CategoryId);
        Assert.Equal("순대국", draft.NewCategoryName);
        Assert.Equal("수원시", draft.Address);
        Assert.Equal(WaitLevel.Moderate, draft.WaitLevel);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("99")]
    [InlineData(null)]
    public void 대기수준이_모름이거나_알수없으면_null이다(string? selected)
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "가게" }),
            (RestaurantModal.BlockIds.WaitLevel, new StaticSelectValue
            {
                SelectedOption = selected is null ? null : new Option { Value = selected },
            }));

        Assert.Null(RestaurantModal.Parse(Submission(null, state)).WaitLevel);
    }

    [Fact]
    public void 기존_대기수준이_있으면_초기_선택으로_채워진다()
    {
        var draft = new RestaurantDraft(7, "스시로", 3, null, null, null, "주소", WaitLevel.Severe);

        var view = RestaurantModal.Build(Categories(), draft, ModalContext.None);

        var menu = (StaticSelectMenu)view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.WaitLevel).Element;
        Assert.Equal("3", menu.InitialOption!.Value);
    }

    [Fact]
    public void Parse_출발시각을입력하면_수정창에도유지된다()
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "제주고사리칼국수 시콜" }),
            ("departure_time", new PlainTextInputValue { Value = "11:15" }));

        var draft = RestaurantModal.Parse(Submission("restaurant:7", state));
        var view = RestaurantModal.Build(Categories(), draft, ModalContext.ForEdit(7));

        Assert.Contains(view.Blocks.OfType<InputBlock>(), block =>
            block.BlockId == "departure_time"
            && block.Optional
            && block.Element is PlainTextInput { InitialValue: "11:15" });
    }

    public static TheoryData<string?, TimeOnly?> DepartureInputs() => new()
    {
        { "11:15", new TimeOnly(11, 15) },
        { " 11:15 ", new TimeOnly(11, 15) },
        { "09:05", new TimeOnly(9, 5) },
        { "00:00", new TimeOnly(0, 0) },
        { "23:59", new TimeOnly(23, 59) },
        { "", null },
        { "   ", null },
        { null, null },
    };

    [Theory]
    [MemberData(nameof(DepartureInputs))]
    public void 출발시각은_HHmm이면_시각으로_비우면_지정없음으로_읽고_검증을_통과한다(string? input, TimeOnly? expected)
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }),
            (RestaurantModal.BlockIds.DepartureTime, new PlainTextInputValue { Value = input }));
        var submission = Submission(null, state);

        Assert.Equal(expected, RestaurantModal.Parse(submission).DepartureTime);
        Assert.Null(RestaurantModal.ValidateDepartureTime(submission));
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("11:60")]
    [InlineData("abc")]
    [InlineData("1115")]
    [InlineData("11:15:30")]
    public void 형식이_틀린_출발시각은_출발시각_블록의_오류가_된다(string input)
    {
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }),
            (RestaurantModal.BlockIds.DepartureTime, new PlainTextInputValue { Value = input }));

        var error = RestaurantModal.ValidateDepartureTime(Submission(null, state));

        Assert.Equal(RestaurantModal.BlockIds.DepartureTime, error!.Value.BlockId);
    }

    [Fact]
    public void 수정창에_채워진_출발시각을_그대로_다시_제출해도_같은_시각이다()
    {
        var draft = new RestaurantDraft(7, "스시로", 3, null, null, null, DepartureTime: new TimeOnly(9, 5));
        var view = RestaurantModal.Build(Categories(), draft, ModalContext.ForEdit(7));
        var input = (PlainTextInput)view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.DepartureTime).Element;
        var state = StateWith(
            (RestaurantModal.BlockIds.Name, new PlainTextInputValue { Value = "스시로" }),
            (RestaurantModal.BlockIds.DepartureTime, new PlainTextInputValue { Value = input.InitialValue }));
        var submission = Submission(view.PrivateMetadata, state);

        Assert.Null(RestaurantModal.ValidateDepartureTime(submission));
        Assert.Equal(new TimeOnly(9, 5), RestaurantModal.Parse(submission).DepartureTime);
    }

    [Fact]
    public void 카테고리_검증은_정확히_하나만_통과시킨다()
    {
        static RestaurantDraft Draft(long? id, string? name) =>
            new(null, "가게", id, null, null, null, NewCategoryName: name);

        Assert.Null(RestaurantModal.ValidateCategory(Draft(3, null)));
        Assert.Null(RestaurantModal.ValidateCategory(Draft(null, "순대국")));
        Assert.Equal(RestaurantModal.BlockIds.Category, RestaurantModal.ValidateCategory(Draft(null, null))!.Value.BlockId);
        Assert.Equal(RestaurantModal.BlockIds.Category, RestaurantModal.ValidateCategory(Draft(null, "  "))!.Value.BlockId);
        Assert.Equal(RestaurantModal.BlockIds.CategoryNew, RestaurantModal.ValidateCategory(Draft(3, "순대국"))!.Value.BlockId);
        Assert.Equal(RestaurantModal.BlockIds.CategoryNew,
            RestaurantModal.ValidateCategory(Draft(null, new string('가', 51)))!.Value.BlockId);
    }
}
