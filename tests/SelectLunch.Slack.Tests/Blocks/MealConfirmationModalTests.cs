using SelectLunch.Shared.Search;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Option = SlackNet.Blocks.Option;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 식사 확정 창의 공용 계약 — 식당 선택 입력, 선택 읽기, 날짜·채널·사용자 메타데이터.
/// </summary>
public class MealConfirmationModalTests
{
    const string Channel = "C1";
    const string User = "U1";
    static readonly DateOnly Date = new(2026, 9, 18);

    static MealPlace[] Places(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new MealPlace(i * 10, $"식당{i}"))];

    static ExternalMultiSelectMenu Menu(InputBlock block) => Assert.IsType<ExternalMultiSelectMenu>(block.Element);

    static ViewSubmission SubmissionWith(ElementValue? value, bool blockPresent = true)
    {
        var state = new ViewState { Values = [] };
        if (blockPresent)
        {
            state.Values[MealConfirmationModal.PlacesBlockId] = new Dictionary<string, ElementValue>();
            if (value is not null)
                state.Values[MealConfirmationModal.PlacesBlockId][MealConfirmationModal.PlacesActionId] = value;
        }

        return new ViewSubmission { View = new ModalViewInfo { PrivateMetadata = "", State = state } };
    }

    static ExternalMultiSelectValue Selected(params string?[] values) => new()
    {
        SelectedOptions = [.. values.Select(v => new Option { Value = v! })],
    };

    [Fact]
    public void 식당_선택_입력은_외부_검색_다중_선택이고_상한은_한도다()
    {
        var block = MealConfirmationModal.PlacesInput(2, [], optional: false);

        Assert.Equal(MealConfirmationModal.PlacesBlockId, block.BlockId);
        Assert.False(block.Optional);
        var menu = Menu(block);
        Assert.Equal(MealConfirmationModal.PlacesActionId, menu.ActionId);
        Assert.Equal(2, menu.MaxSelectedItems);
        Assert.Equal(0, menu.MinQueryLength);
        Assert.Empty(menu.InitialOptions ?? []);
    }

    [Fact]
    public void 초기_선택은_순서와_id_이름_라벨을_그대로_싣는다()
    {
        var places = Places(2);

        var menu = Menu(MealConfirmationModal.PlacesInput(3, places, optional: true));

        Assert.Equal(["10", "20"], menu.InitialOptions.Select(o => o.Value));
        Assert.Equal(["식당1", "식당2"], menu.InitialOptions.Select(o => o.Text.Text));
        Assert.Equal(3, menu.MaxSelectedItems);
    }

    [Fact]
    public void 기존_기록이_한도보다_많아도_자르지_않고_슬랙이_거부하지_않는_상한을_쓴다()
    {
        var places = Places(3);

        var menu = Menu(MealConfirmationModal.PlacesInput(limit: 2, places, optional: false));

        Assert.Equal(3, menu.InitialOptions.Count);
        Assert.True(menu.MaxSelectedItems >= menu.InitialOptions.Count);
    }

    [Fact]
    public void 한도가_1_미만이면_거부한다() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MealConfirmationModal.PlacesInput(0, [], optional: false));

    [Fact]
    public void 긴_이름의_라벨은_슬랙_제한으로_자르고_검색_제안과_같은_함수를_쓴다()
    {
        var name = new string('가', RestaurantSearch.MaxLabelLength + 20);

        var menu = Menu(MealConfirmationModal.PlacesInput(1, [new MealPlace(7, name)], optional: false));

        var label = Assert.Single(menu.InitialOptions).Text.Text;
        Assert.InRange(label.Length, 1, RestaurantSearch.MaxLabelLength);
    }

    [Fact]
    public void 확정창은_미리_담은_식당과_본인_메타데이터를_가진다()
    {
        var state = new MealSelectionState(true, true, 2, []);

        var view = MealConfirmationModal.Build(Date, Channel, User, state, Places(1));

        Assert.Equal(MealConfirmationModal.CallbackId, view.CallbackId);
        Assert.Contains("2026-09-18", view.Title.Text);
        Assert.True(MealConfirmationModal.TryDate(view.PrivateMetadata, Channel, User, out var date));
        Assert.Equal(Date, date);
        var input = Assert.Single(view.Blocks.OfType<InputBlock>());
        Assert.False(input.Optional);
        Assert.Equal(["10"], Menu(input).InitialOptions.Select(o => o.Value));
        Assert.Equal(2, Menu(input).MaxSelectedItems);
    }

    [Fact]
    public void 한도보다_많은_기존_기록으로도_창을_만들고_줄여야_한다고_알린다()
    {
        var state = new MealSelectionState(true, true, 1, Places(2));

        var view = MealConfirmationModal.Build(Date, Channel, User, state, state.Recorded);

        var menu = Menu(Assert.Single(view.Blocks.OfType<InputBlock>()));
        Assert.Equal(2, menu.InitialOptions.Count);
        Assert.True(menu.MaxSelectedItems >= 2);
        Assert.Contains(view.Blocks.OfType<SectionBlock>(), b => b.Text.Text.Contains("최대 1곳"));
    }

    [Fact]
    public void 지난_날짜는_한_번만_저장된다고_알린다()
    {
        var state = new MealSelectionState(true, false, 2, []);

        var view = MealConfirmationModal.Build(Date, Channel, User, state, Places(1));

        Assert.Contains(view.Blocks.OfType<SectionBlock>(), b => b.Text.Text.Contains("지난 날짜"));
    }

    [Fact]
    public void 이름은_마크업_없는_평문으로만_담는다()
    {
        var state = new MealSelectionState(true, true, 1, []);

        var view = MealConfirmationModal.Build(Date, Channel, User, state, [new MealPlace(5, "<!channel> *굵게*")]);

        Assert.DoesNotContain(view.Blocks.OfType<SectionBlock>(), b => b.Text.Text.Contains("<!channel>"));
        Assert.Equal("<!channel> *굵게*", Assert.Single(Menu(Assert.Single(view.Blocks.OfType<InputBlock>())).InitialOptions).Text.Text);
    }

    [Fact]
    public void 선택을_순서대로_읽는다()
    {
        Assert.True(MealConfirmationModal.TryReadPlaces(SubmissionWith(Selected("30", "10", "20")), out var ids));
        Assert.Equal([30L, 10L, 20L], ids);
    }

    [Fact]
    public void 선택_입력이_없으면_빈_선택이다()
    {
        Assert.True(MealConfirmationModal.TryReadPlaces(SubmissionWith(null, blockPresent: false), out var absentBlock));
        Assert.Empty(absentBlock);
        Assert.False(MealConfirmationModal.TryReadPlaces(SubmissionWith(null), out _)); // 보낸 블록의 액션이 손상됨
        Assert.True(MealConfirmationModal.TryReadPlaces(SubmissionWith(Selected()), out var empty));
        Assert.Empty(empty);
        Assert.True(MealConfirmationModal.TryReadPlaces(
            SubmissionWith(new ExternalMultiSelectValue { SelectedOptions = null! }), out var nullOptions));
        Assert.Empty(nullOptions);
        Assert.True(MealConfirmationModal.TryReadPlaces(
            new ViewSubmission { View = new ModalViewInfo { State = new ViewState { Values = null! } } }, out var noValues));
        Assert.Empty(noValues);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("+3")]
    [InlineData(" 3")]
    [InlineData("3.0")]
    [InlineData("99999999999999999999")]
    public void 식당_id_형식이_아닌_선택은_빈_선택과_구분해_거절한다(string value)
    {
        Assert.False(MealConfirmationModal.TryReadPlaces(SubmissionWith(Selected("10", value)), out var ids));
        Assert.Empty(ids);
    }

    [Fact]
    public void 값이_없는_선택_항목과_다른_입력_형식도_거절한다()
    {
        Assert.False(MealConfirmationModal.TryReadPlaces(SubmissionWith(Selected((string?)null)), out _));
        Assert.False(MealConfirmationModal.TryReadPlaces(
            SubmissionWith(new PlainTextInputValue { Value = "10" }), out var ids));
        Assert.Empty(ids);
    }

    [Fact]
    public void 중복된_선택은_읽기에서_합치지_않고_서버_검증에_맡긴다()
    {
        Assert.True(MealConfirmationModal.TryReadPlaces(SubmissionWith(Selected("10", "10")), out var ids));
        Assert.Equal([10L, 10L], ids);
    }

    [Fact]
    public void 메타데이터는_같은_날짜_채널_사용자일_때만_날짜를_돌려준다()
    {
        var metadata = MealConfirmationModal.Metadata(Date, Channel, User);

        Assert.True(MealConfirmationModal.TryDate(metadata, Channel, User, out var date));
        Assert.Equal(Date, date);
        Assert.False(MealConfirmationModal.TryDate(metadata, "C2", User, out var otherChannel));
        Assert.Equal(default, otherChannel);
        Assert.False(MealConfirmationModal.TryDate(metadata, Channel, "U2", out var otherUser));
        Assert.Equal(default, otherUser);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("meal")]
    [InlineData("meal:20260918")]
    [InlineData("meal:20260918:C1")]
    [InlineData("meal:2026-09-18:C1:U1")]
    [InlineData("meal:20261340:C1:U1")]
    [InlineData("other:20260918:C1:U1")]
    [InlineData("meal:20260918:C1:U1x")]
    [InlineData("date:20260918")]
    public void 정식_표기가_아닌_메타데이터는_거절하고_날짜를_비운다(string? metadata)
    {
        Assert.False(MealConfirmationModal.TryDate(metadata, Channel, User, out var date));
        Assert.Equal(default, date);
    }

    [Fact]
    public void 채널이나_사용자가_비어_있으면_어떤_메타데이터도_거절한다()
    {
        Assert.False(MealConfirmationModal.TryDate(MealConfirmationModal.Metadata(Date, "", User), "", User, out _));
        Assert.False(MealConfirmationModal.TryDate(MealConfirmationModal.Metadata(Date, Channel, ""), Channel, "", out _));
    }
}
