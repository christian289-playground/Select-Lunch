using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class ResultBlocksTests
{
    static readonly RecommendationOptions Options = new();

    static Recommendation Sample() => new(
        new RestaurantPick(20, "스시로", new DateOnly(2026, 8, 21)),
        new CategoryScore(3, "일식", new DateOnly(2026, 9, 4), 14, 0, 1, 13),
        [
            new CategoryScore(5, "분식", new DateOnly(2026, 9, 7), 11, 0, 0, 11),
            new CategoryScore(1, "한식", new DateOnly(2026, 9, 15), 3, 2, 5, -8),
        ]);

    static string TextOf(IList<Block> blocks) =>
        string.Join("\n", blocks.OfType<SectionBlock>().Select(s => (s.Text as Markdown)?.Text ?? "")
            .Concat(blocks.OfType<ContextBlock>().SelectMany(c => c.Elements.OfType<Markdown>().Select(m => m.Text))));

    [Fact]
    public void 추천_근거에_점수_계산_과정이_모두_드러난다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("13점", text);          // 최종 점수
        Assert.Contains("14일", text);          // 경과일 D
        Assert.Contains("최근 7일", text);       // N7
        Assert.Contains("최근 30일", text);      // N30
        Assert.Contains("14 −", text);          // 실제 뺄셈 식
    }

    [Fact]
    public void 경쟁_카테고리의_점수도_함께_보여준다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("분식", text);
        Assert.Contains("11점", text);
        Assert.Contains("한식", text);
    }

    [Fact]
    public void 투표_결과와_추천을_두_갈래로_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(10, "김밥천국", 4),
            [new VoteTally(10, "김밥천국", 4), new VoteTally(20, "스시로", 1)],
            Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("김밥천국", text);
        Assert.Contains("스시로", text);
        Assert.Contains("투표 1위", text);
        Assert.Contains("앱 추천", text);
    }

    [Fact]
    public void 투표_1위와_추천이_같으면_하나로_합쳐_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(20, "스시로", 3),
            [new VoteTally(20, "스시로", 3)],
            Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("투표와 추천이 일치", text);
        Assert.DoesNotContain("투표 1위", text);
    }

    [Fact]
    public void 아무도_투표하지_않으면_추천만_보여준다()
    {
        var outcome = new PollOutcome(null, [], Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("투표가 없었습니다", text);
        Assert.Contains("스시로", text);
    }

    [Fact]
    public void 추천할_식당이_없어도_투표_결과는_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(10, "김밥천국", 2),
            [new VoteTally(10, "김밥천국", 2)],
            Recommendation: null);

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("김밥천국", text);
    }

    [Fact]
    public void 전체_산식이_검증_가능하다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        // 정확한 산식 줄 전체를 검증 — 파편이 아니라 완전한 줄
        Assert.Contains("`14 − 0 − 1 = 13점`", text);
    }

    [Fact]
    public void 제로_패널티는_음수_제로가_아니다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        // "−0"이 아닌 "0" 렌더링 확인
        Assert.Contains("= 0`", text);
        Assert.DoesNotContain("= −0", text);
    }

    [Fact]
    public void 옵션이_맞지_않아도_산식은_정확하다()
    {
        // Sample의 점수는 14 - 0 - 1 = 13이었는데, 다른 가중치로 계산하면?
        var mismatchedOptions = new RecommendationOptions { Weight7d = 5, Weight30d = 2 };
        var text = ResultBlocks.Rationale(Sample(), mismatchedOptions);

        // 산식은 여전히 정확해야 함
        Assert.Contains("`14 − 1 = 13점`", text);

        // 세부 항목 분석은 나타나지 않아야 함 (가중치가 안 맞으니까)
        Assert.DoesNotContain("−5 ×", text);
        Assert.DoesNotContain("−2 ×", text);

        // 대신 집계 차감만 보여줄 것
        Assert.Contains("최근 식사 차감", text);
    }

    [Fact]
    public void 방문_기록_없음은_으로를_사용한다()
    {
        var rec = new Recommendation(
            new RestaurantPick(10, "식당", null),
            new CategoryScore(1, "기타", null, 30, 0, 0, 30),
            []);

        var text = ResultBlocks.Rationale(rec, Options);

        Assert.Contains("기록 없음으로", text);
    }

    [Fact]
    public void 음수_점수도_렌더링된다()
    {
        // 경쟁 카테고리 목록에서 한식 -8점이 나타나는지 확인
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("한식 -8점", text);
    }

    [Fact]
    public void 날짜_끝자리에_따라_조사가_바뀐다()
    {
        // 21 → 1(일) → 로
        var text = ResultBlocks.Rationale(Sample(), Options);
        Assert.Contains("2026-08-21로", text);
        Assert.DoesNotContain("2026-08-21으로", text);
    }

    [Fact]
    public void 카테고리명_조사는_받침_유무로_결정된다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);
        // 일식: 받침 있음 → "이"
        Assert.Contains("*일식이 선정된", text);
        Assert.DoesNotContain("*일식가 선정된", text);
    }

    [Fact]
    public void 페널티_제로는_옵션_불일치_시에도_마이너스_부호_없다()
    {
        // 음식을 최근에 먹지 않은 경우: DaysSince=30, Score=30 → penalty=0
        var rec = new Recommendation(
            new RestaurantPick(1, "식당A", null),
            new CategoryScore(1, "카테고리", new DateOnly(2026, 8, 19), 30, 0, 0, 30),
            []);

        // 가중치가 맞지 않는 옵션
        var mismatchedOptions = new RecommendationOptions { Weight7d = 5, Weight30d = 3 };
        var text = ResultBlocks.Rationale(rec, mismatchedOptions);

        // −0이 전체 문자열 어디에도 나타나면 안 됨
        Assert.DoesNotContain("−0", text);
        // 최종 점수가 30이고 페널티 0이 안전하게 처리됨
        Assert.Contains("30점", text);
        Assert.Contains("30 −", text);
    }

    [Fact]
    public void 우승자_점수가_음수여도_산식이_정확하다()
    {
        // 우승자의 점수가 음수인 경우: DaysSince=8, Count7d=5, Count30d=0, Score=-7
        // 8 - (3*5 + 1*0) = 8 - 15 = -7
        var rec = new Recommendation(
            new RestaurantPick(2, "식당B", null),
            new CategoryScore(2, "기타", new DateOnly(2026, 9, 10), 8, 5, 0, -7),
            []);

        var text = ResultBlocks.Rationale(rec, Options);

        // 음수 점수가 정확하게 표시됨
        Assert.Contains("-7점", text);
        // 음수 결과가 있는 산식도 있음
        Assert.Contains("− 15", text);
    }
}
