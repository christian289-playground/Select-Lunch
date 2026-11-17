using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// <c>/lunchpre</c> 본인 전용 화면을 받는 쪽(슬랙 메시지, 버튼·선택 payload를 읽는 핸들러)에서 본 계약:
/// 컨트롤이 본인·날짜에 묶여 실제 action 파서로 복원되고, 기존 일반·랜덤 투표 경로와 섞이지 않으며,
/// 선택·기권·취소 가능 상태와 긴 이름·비활성 식당이 슬랙에 거절되지 않는 형태로 나오는지.
/// </summary>
public class AdvanceVoteBlocksTests
{
    const string Me = "U1";

    static readonly DateOnly Today = new(2026, 11, 16);
    static readonly TimeOnly Opens = new(11, 30);
    static readonly DateTimeOffset VotedAt = new(2026, 11, 16, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 많은_득표와_긴_이름도_누락하거나_서식을_해석하지_않고_슬랙_제한_안에_표시한다()
    {
        var tallies = Enumerable.Range(1, 1200)
            .Select(i => new AdvanceVoteTally(i, $"식당{i:D4}_" + new string('가', 70), i)).ToList();
        var longName = string.Concat(Enumerable.Repeat("\U00020000", 1600));
        tallies.Add(new(1201, longName, 5));
        tallies.Add(new(1202, "<@U999> <!channel> *서식 이름*", 7));
        var snapshot = new AdvanceVoteSnapshot(Today, tallies, 4);

        var blocks = AdvanceVoteBlocks.Build(snapshot, Opens, Me, null, false);

        AssertWithinSlackLimits(blocks);
        var sections = blocks.OfType<SectionBlock>().Select(s => s.Text).OfType<PlainText>().ToArray();
        var text = string.Join("", sections.Select(s => s.Text));
        Assert.All(tallies, tally => Assert.Contains($"{tally.Name} — {tally.Count}표", text));
        Assert.Contains("기권 — 4표", text);
        Assert.All(sections, s =>
        {
            Assert.False(char.IsHighSurrogate(s.Text[^1]));
            Assert.False(char.IsLowSurrogate(s.Text[0]));
        });
        Assert.All(TextObjects(blocks).OfType<Markdown>(), m => Assert.DoesNotContain("<@U999>", m.Text));
        Assert.NotEmpty(blocks.OfType<ActionsBlock>());
    }

    sealed record Controls(ExternalSelectMenu Select, Button Abstain, Button? Cancel);

    static Restaurant Place(string name, RestaurantStatus status = RestaurantStatus.Active, long? categoryId = 1) => new()
    {
        Id = 42,
        Name = name,
        NormalizedName = Restaurant.Normalize(name),
        CategoryId = categoryId,
        Status = status,
        CreatedBySlackUserId = "U0",
    };

    static AdvanceVote Chose(Restaurant restaurant) => new()
    {
        ChannelId = "C1",
        Date = Today,
        SlackUserId = Me,
        RestaurantId = restaurant.Id,
        Restaurant = restaurant,
        VotedAt = VotedAt,
    };

    static AdvanceVote Abstained() => new()
    {
        ChannelId = "C1",
        Date = Today,
        SlackUserId = Me,
        RestaurantId = null,
        VotedAt = VotedAt,
    };

    /// <summary>화면을 만들고, 슬랙이 메시지째 거절하는 제한(<c>invalid_blocks</c>)을 넘지 않는지 함께 본다.</summary>
    static IList<Block> Screen(AdvanceVote? vote, DateOnly? date = null, TimeOnly? opensAt = null, string user = Me, bool votingStarted = false)
    {
        var blocks = AdvanceVoteBlocks.Build(new(date ?? Today, [], 0), opensAt ?? Opens, user, vote, votingStarted);
        AssertWithinSlackLimits(blocks);
        return blocks;
    }

    /// <summary>
    /// 사용자가 누를 수 있는 컨트롤 전부. 검색·기권·취소 말고 다른 컨트롤(랜덤 투표류)이
    /// 하나라도 끼어 있으면 개수가 어긋나 여기서 실패한다.
    /// </summary>
    static Controls ControlsOf(IList<Block> blocks)
    {
        var elements = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements).ToList();
        var select = Assert.Single(elements.OfType<ExternalSelectMenu>());
        var buttons = elements.OfType<Button>().ToList();
        var abstain = Assert.Single(buttons, b => ActionIds.TryParseAdvanceAbstain(b.ActionId, out _, out _, out _));
        var cancel = buttons.SingleOrDefault(b => ActionIds.TryParseAdvanceCancel(b.ActionId, out _, out _, out _));

        Assert.Equal(1 + 1 + (cancel is null ? 0 : 1), elements.Count);
        return new Controls(select, abstain, cancel);
    }

    static IEnumerable<TextObject> TextObjects(IList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeaderBlock header:
                    yield return header.Text;
                    break;
                case SectionBlock section:
                    yield return section.Text;
                    break;
                case ContextBlock context:
                    foreach (var text in context.Elements.OfType<TextObject>())
                        yield return text;
                    break;
                case ActionsBlock actions:
                    foreach (var button in actions.Elements.OfType<Button>())
                        yield return button.Text;
                    foreach (var menu in actions.Elements.OfType<ExternalSelectMenu>())
                        yield return menu.Placeholder;
                    break;
            }
        }
    }

    static string AllText(IList<Block> blocks) => string.Join("\n", TextObjects(blocks).Select(t => t.Text));

    /// <summary>집계보다 먼저 표시되는 본인 선택 section.</summary>
    static string SelectionText(IList<Block> blocks) =>
        blocks.OfType<SectionBlock>().Select(s => s.Text).OfType<PlainText>().First().Text;

    static IEnumerable<string> MarkdownLines(IList<Block> blocks) =>
        TextObjects(blocks).OfType<Markdown>().SelectMany(m => m.Text.Split('\n'));

    /// <summary>"*라벨:* 값" 줄의 값. 날짜·시작 시각처럼 계산해서 만드는 표시를 줄 단위로 비교한다.</summary>
    static string FieldOf(IList<Block> blocks, string label)
    {
        var prefix = $"*{label}:* ";
        var line = Assert.Single(MarkdownLines(blocks), l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..];
    }

    static void AssertWithinSlackLimits(IList<Block> blocks)
    {
        Assert.InRange(blocks.Count, 1, 50);

        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeaderBlock header:
                    Assert.InRange(header.Text.Text.Length, 1, 150);
                    break;
                case SectionBlock section:
                    Assert.InRange(section.Text.Text.Length, 1, 3000);
                    break;
                case ContextBlock context:
                    Assert.InRange(context.Elements.Count, 1, 10);
                    Assert.All(context.Elements.OfType<TextObject>(), t => Assert.InRange(t.Text.Length, 1, 2000));
                    break;
                case ActionsBlock actions:
                    Assert.InRange(actions.Elements.Count, 1, 25);
                    Assert.All(actions.Elements.OfType<Button>(), b =>
                    {
                        Assert.InRange(b.Text.Text.Length, 1, 75);
                        Assert.InRange(b.ActionId.Length, 1, 255);
                    });
                    Assert.All(actions.Elements.OfType<ExternalSelectMenu>(), m =>
                    {
                        Assert.InRange(m.Placeholder.Text.Length, 1, 150);
                        Assert.InRange(m.ActionId.Length, 1, 255);
                    });
                    break;
                default:
                    Assert.Fail($"슬랙 제한을 검증하지 못하는 블록: {block.GetType().Name}");
                    break;
            }
        }
    }

    /// <summary>기존 일반·랜덤·기록·결과 경로의 파서 중 이 action_id를 자기 것으로 가져가는 것들.</summary>
    static List<string> ExistingPathsClaiming(string id)
    {
        var claimed = new List<string>();
        if (ActionIds.TryParseVote(id, out _, out _)) claimed.Add("vote");
        if (ActionIds.TryParseVoteSelect(id, out _)) claimed.Add("vote_select");
        if (ActionIds.TryParseAbstain(id, out _)) claimed.Add("abstain");
        if (ActionIds.TryParseVoteCategory(id, out _)) claimed.Add("vote_category");
        if (ActionIds.TryParseVoteRandom(id, out _)) claimed.Add("vote_random");
        if (ActionIds.TryParseVoteSecret(id, out _)) claimed.Add("vote_secret");
        if (ActionIds.TryParseSecretReveal(id, out _, out _)) claimed.Add("secret_reveal");
        if (ActionIds.TryParseSecretPreview(id, out _)) claimed.Add("secret_preview");
        if (ActionIds.TryParseRestaurantFill(id, out _)) claimed.Add("restaurant_fill");
        if (ActionIds.TryParseMeal(id, out _, out _)) claimed.Add("meal");
        if (ActionIds.TryParseMealNew(id, out _)) claimed.Add("meal_new");
        if (ActionIds.TryParseResultDetails(id, out _, out _)) claimed.Add("result_details");
        if (ActionIds.TryUnwrapTest(id, out _)) claimed.Add("test");
        return claimed;
    }

    static List<string> AdvancePathsClaiming(string id)
    {
        var claimed = new List<string>();
        if (ActionIds.TryParseAdvanceSelect(id, out _, out _, out _)) claimed.Add("select");
        if (ActionIds.TryParseAdvanceAbstain(id, out _, out _, out _)) claimed.Add("abstain");
        if (ActionIds.TryParseAdvanceCancel(id, out _, out _, out _)) claimed.Add("cancel");
        return claimed;
    }

    // --- 본인·날짜 바인딩: 실제 파서로 복원 ---

    [Theory]
    [InlineData(2026, 11, 16, "U1")]
    [InlineData(2026, 12, 31, "U0ABC9XYZ")]
    [InlineData(2028, 2, 29, "W1234567")]
    public void 컨트롤_action_id는_본인과_날짜를_실제_파서로_복원한다(int year, int month, int day, string user)
    {
        var date = new DateOnly(year, month, day);

        var vote = Chose(Place("국밥집"));
        vote.ChangeCount = 2;
        var controls = ControlsOf(Screen(vote, date, user: user));

        Assert.True(ActionIds.TryParseAdvanceSelect(controls.Select.ActionId, out var selectDate, out var selectUser, out var selectCount));
        Assert.Equal((date, user, 2), (selectDate, selectUser, selectCount));
        Assert.True(ActionIds.TryParseAdvanceAbstain(controls.Abstain.ActionId, out var abstainDate, out var abstainUser, out var abstainCount));
        Assert.Equal((date, user, 2), (abstainDate, abstainUser, abstainCount));
        Assert.NotNull(controls.Cancel);
        Assert.True(ActionIds.TryParseAdvanceCancel(controls.Cancel.ActionId, out var cancelDate, out var cancelUser, out var cancelCount));
        Assert.Equal((date, user, 2), (cancelDate, cancelUser, cancelCount));
    }

    [Fact]
    public void 검색_선택_payload는_고른_식당과_본인_날짜를_함께_읽힌다()
    {
        var menu = ControlsOf(Screen(null)).Select;

        // 슬랙이 드롭다운에서 식당을 고른 직후 돌려보내는 payload와 같은 모양.
        BlockAction payload = new ExternalSelectAction
        {
            ActionId = menu.ActionId,
            SelectedOption = new Option { Value = "42" },
        };

        Assert.True(SelectedRestaurant.TryRead(payload, out var restaurantId));
        Assert.Equal(42, restaurantId);
        Assert.True(ActionIds.TryParseAdvanceSelect(payload.ActionId, out var date, out var user, out var count));
        Assert.Equal((Today, Me, 0), (date, user, count));
    }

    // --- 랜덤·일반 투표 경로와의 분리 ---

    [Fact]
    public void 사전투표_컨트롤은_기존_투표_랜덤_기록_경로로_해석되지_않는다()
    {
        var controls = ControlsOf(Screen(Chose(Place("국밥집"))));

        Assert.NotNull(controls.Cancel);
        string[] ids = [controls.Select.ActionId, controls.Abstain.ActionId, controls.Cancel.ActionId];
        Assert.All(ids, id => Assert.Empty(ExistingPathsClaiming(id)));
    }

    [Fact]
    public void 기존_투표_랜덤_기록_action_id는_사전투표로_해석되지_않는다()
    {
        string[] existing =
        [
            ActionIds.Vote(1, 2),
            ActionIds.VoteSelect(1),
            ActionIds.Abstain(1),
            ActionIds.VoteCategory(1),
            ActionIds.VoteRandom(1),
            ActionIds.VoteSecret(1),
            ActionIds.SecretReveal(1, 2),
            ActionIds.SecretPreview(2),
            ActionIds.Meal(Today, 7),
            ActionIds.MealNew(Today),
            ActionIds.RestaurantFill(7),
            ActionIds.ResultDetails(1),
            ActionIds.Test(ActionIds.VoteRandom(1)),
            ActionIds.Test(ActionIds.Abstain(1)),
        ];

        Assert.All(existing, id => Assert.Empty(AdvancePathsClaiming(id)));
    }

    [Fact]
    public void 사전투표_컨트롤은_서로의_파서에도_섞이지_않고_테스트_접두사_뒤에는_거절된다()
    {
        var controls = ControlsOf(Screen(Chose(Place("국밥집"))));

        Assert.Equal("select", Assert.Single(AdvancePathsClaiming(controls.Select.ActionId)));
        Assert.Equal("abstain", Assert.Single(AdvancePathsClaiming(controls.Abstain.ActionId)));
        Assert.NotNull(controls.Cancel);
        Assert.Equal("cancel", Assert.Single(AdvancePathsClaiming(controls.Cancel.ActionId)));
        Assert.Empty(AdvancePathsClaiming(ActionIds.Test(controls.Select.ActionId)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(":20261116")]
    [InlineData(":20261116:")]
    [InlineData("::U1")]
    [InlineData(":2026-11-16:U1")]
    [InlineData(":2026116:U1")]
    [InlineData(":202611160:U1")]
    [InlineData(":20261301:U1")]
    [InlineData(":20260230:U1")]
    [InlineData(":20261116:U1:extra")]
    [InlineData(":20261116: U1")]
    [InlineData(":20261116:U 1")]
    [InlineData(":20261116:\tU1")]
    [InlineData("x:20261116:U1")]
    public void 형식이_틀린_사전투표_action_id는_모든_파서가_거절한다(string tail)
    {
        Assert.False(ActionIds.TryParseAdvanceSelect("pre_select" + tail, out _, out _, out _));
        Assert.False(ActionIds.TryParseAdvanceAbstain("pre_abstain" + tail, out _, out _, out _));
        Assert.False(ActionIds.TryParseAdvanceCancel("pre_cancel" + tail, out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("U 1")]
    [InlineData("U:1")]
    public void 사용자_ID가_action_id로_옮길_수_없으면_화면을_만들지_않는다(string user)
    {
        // 해석할 수 없는 ID로 버튼을 만들면 눌러도 아무 일도 없는 죽은 버튼이 된다.
        Assert.Throws<ArgumentException>(() => AdvanceVoteBlocks.Build(new(Today, [], 0), Opens, user, null, false));
    }

    // --- 선택·기권·취소 가능 상태 ---

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public void 입력이_잠긴_상태는_횟수를_보여주고_컨트롤을_표시하지_않는다(bool started, int count)
    {
        var vote = Chose(Place("국밥집"));
        vote.ChangeCount = count;
        var blocks = Screen(vote, votingStarted: started);

        Assert.Empty(blocks.OfType<ActionsBlock>());
        Assert.Contains($"{count}/3", AllText(blocks));
        Assert.Contains($"남은 횟수: {3 - count}", AllText(blocks));
        Assert.Contains(started ? "조회" : "소진", AllText(blocks));
    }

    [Fact]
    public void 취소된_행은_사용횟수를_유지하고_선택과_취소버튼은_없다()
    {
        var vote = Abstained();
        vote.IsCancelled = true;
        vote.ChangeCount = 2;

        var blocks = Screen(vote);
        var controls = ControlsOf(blocks);
        Assert.Null(controls.Cancel);
        Assert.Contains("아직 선택하지 않았", SelectionText(blocks));
        Assert.Contains("취소", SelectionText(blocks));
        Assert.Contains("2/3", AllText(blocks));
        Assert.Contains("남은 횟수: 1", AllText(blocks));
        Assert.True(ActionIds.TryParseAdvanceSelect(controls.Select.ActionId, out _, out _, out var count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void 아직_선택하지_않았으면_검색과_기권만_있고_취소할_선택은_없다()
    {
        var blocks = Screen(null);

        var controls = ControlsOf(blocks);

        Assert.Null(controls.Cancel);
        Assert.Contains("아직 선택하지 않았", SelectionText(blocks));
    }

    [Fact]
    public void 기권을_저장했으면_따로_먹어요로_보이고_변경과_취소가_가능하다()
    {
        var blocks = Screen(Abstained());

        var controls = ControlsOf(blocks);

        Assert.NotNull(controls.Cancel);
        Assert.Contains("따로 먹어요", SelectionText(blocks));
        Assert.DoesNotContain("반영되지 않", AllText(blocks));
    }

    [Fact]
    public void 식당을_골랐으면_이름을_보이고_변경과_취소가_가능하다()
    {
        var blocks = Screen(Chose(Place("옛날경성순대국")));

        var controls = ControlsOf(blocks);

        Assert.NotNull(controls.Cancel);
        Assert.Contains("옛날경성순대국", SelectionText(blocks));
        Assert.DoesNotContain("반영되지 않", AllText(blocks));
    }

    [Theory]
    [InlineData(RestaurantStatus.Pending, null)]
    [InlineData(RestaurantStatus.Archived, null)]
    [InlineData(RestaurantStatus.Archived, 1L)]
    [InlineData(RestaurantStatus.Active, null)]
    public void 비활성_식당을_골라_뒀으면_반영되지_않는다고_안내하고_바꾸거나_취소할_수_있다(
        RestaurantStatus status, long? categoryId)
    {
        var blocks = Screen(Chose(Place("문닫은식당", status, categoryId)));

        var controls = ControlsOf(blocks);

        Assert.Contains("집계", AllText(blocks));
        Assert.Contains("문닫은식당", SelectionText(blocks));
        Assert.NotNull(controls.Cancel);
    }

    [Fact]
    public void 저장된_식당을_읽지_못해도_반영되지_않는다고_안내하고_바꿀_수_있다()
    {
        var vote = Chose(Place("사라진식당"));
        vote.Restaurant = null;

        var blocks = Screen(vote);

        var controls = ControlsOf(blocks);
        Assert.Contains("집계", AllText(blocks));
        Assert.DoesNotContain("사라진식당", AllText(blocks));
        Assert.NotNull(controls.Cancel);
    }

    // --- 식당 이름 표시 ---

    [Fact]
    public void 식당_이름의_서식과_멘션은_해석되지_않게_PlainText로만_보여준다()
    {
        const string name = "*굵게* <@U999> <!channel> &amp; :smile: <https://x.test|링크>";

        var blocks = Screen(Chose(Place(name)));

        Assert.EndsWith(name, SelectionText(blocks));
        Assert.All(TextObjects(blocks).OfType<Markdown>(), m =>
        {
            Assert.DoesNotContain("U999", m.Text);
            Assert.DoesNotContain("<!channel>", m.Text);
            Assert.DoesNotContain("굵게", m.Text);
        });
    }

    [Fact]
    public void 슬랙_제한에_맞는_이름은_자르지_않는다()
    {
        // 등록 단계의 이름 상한(100자)까지는 정상 이름이다.
        var name = new string('나', 100);

        var blocks = Screen(Chose(Place(name)));

        Assert.EndsWith(name, SelectionText(blocks));
    }

    [Fact]
    public void 비정상적으로_긴_이름은_슬랙_제한_안으로_줄여_보여준다()
    {
        // Screen이 section 3,000자 같은 슬랙 제한을 검사한다 — 줄이지 않으면 메시지째 거절된다.
        var name = string.Concat(Enumerable.Repeat("옛날경성순대국", 1000));

        var blocks = Screen(Chose(Place(name)));

        var shown = SelectionText(blocks);
        Assert.Contains("옛날경성순대국", shown);
        Assert.EndsWith("…", shown);
        Assert.DoesNotContain(name, shown);
    }

    [Fact]
    public void 식당을_등록한_다른_사람의_정보는_화면에_싣지_않는다()
    {
        var place = Place("국밥집");
        place.CreatedBySlackUserId = "UCREATOR9";
        place.CreatedByDisplayName = "등록자홍길동";

        var text = AllText(Screen(Chose(place)));

        Assert.DoesNotContain("UCREATOR9", text);
        Assert.DoesNotContain("등록자홍길동", text);
    }

    // --- 날짜·정규 투표 시작 시각의 한국어 표기 ---

    [Theory]
    [InlineData(2026, 11, 16, "2026년 11월 16일 (월)")]
    [InlineData(2026, 11, 17, "2026년 11월 17일 (화)")]
    [InlineData(2026, 11, 18, "2026년 11월 18일 (수)")]
    [InlineData(2026, 11, 19, "2026년 11월 19일 (목)")]
    [InlineData(2026, 11, 20, "2026년 11월 20일 (금)")]
    [InlineData(2026, 11, 21, "2026년 11월 21일 (토)")]
    [InlineData(2026, 11, 22, "2026년 11월 22일 (일)")]
    [InlineData(2027, 1, 1, "2027년 1월 1일 (금)")]
    [InlineData(2028, 2, 29, "2028년 2월 29일 (화)")]
    public void 날짜는_요일과_함께_한국어로_표시한다(int year, int month, int day, string expected)
    {
        var blocks = Screen(null, date: new DateOnly(year, month, day));

        Assert.Equal(expected, FieldOf(blocks, "날짜"));
    }

    [Theory]
    [InlineData(0, 0, "오전 12시")]
    [InlineData(9, 5, "오전 9시 5분")]
    [InlineData(11, 0, "오전 11시")]
    [InlineData(11, 30, "오전 11시 30분")]
    [InlineData(12, 0, "오후 12시")]
    [InlineData(12, 30, "오후 12시 30분")]
    [InlineData(13, 0, "오후 1시")]
    [InlineData(23, 59, "오후 11시 59분")]
    public void 정규_투표_시작_시각은_12시간제_한국어로_표시한다(int hour, int minute, string expected)
    {
        var blocks = Screen(null, opensAt: new TimeOnly(hour, minute));

        Assert.Equal(expected, FieldOf(blocks, "정규 투표 시작"));
    }
}
