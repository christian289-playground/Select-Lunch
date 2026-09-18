using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class RestaurantModalTests
{
    static IReadOnlyList<Category> Categories() =>
    [
        new() { Id = 1, Name = "한식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
        new() { Id = 3, Name = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
    ];

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
}
