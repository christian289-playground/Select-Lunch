using SelectLunch.Slack.Blocks;
using SlackNet;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 점수 상세 모달이 마감 때 저장한 문장을 슬랙 한도(section 3000자, 블록 100개) 안에서
/// 한 글자도 잃지 않고 보여주는지 — 소비자가 보는 블록만으로 검증한다.
/// </summary>
public class ResultDetailsModalTests
{
    const long PollId = 42;
    static readonly DateOnly Day = new(2026, 10, 20);

    static List<string> SectionTexts(ViewDefinition view) =>
        [.. view.Blocks.OfType<SectionBlock>().Select(s => Assert.IsType<Markdown>(s.Text).Text)];

    static string HeaderText(ViewDefinition view) =>
        string.Concat(view.Blocks.OfType<ContextBlock>().SelectMany(c => c.Elements.OfType<Markdown>()).Select(m => m.Text));

    static List<string> Buttons(ViewDefinition view) =>
        [.. view.Blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<SlackNet.Blocks.Button>()).Select(b => b.ActionId)];

    static IEnumerable<ModalViewDefinition> Pages(string text)
    {
        for (var page = 0; ResultDetailsModal.TryBuild(PollId, Day, text, page, out var view); page++)
            yield return view;
    }

    /// <summary>모든 쪽의 section을 차례로 이어 붙인다 — 저장된 문장과 같아야 한다.</summary>
    static string AllPagesText(string text) => string.Concat(Pages(text).SelectMany(SectionTexts));

    static string Lines(int count, int lineLength, string newline) =>
        string.Join(newline, Enumerable.Range(0, count).Select(i => $"• {i:D5} {new string('가', lineLength)}"));

    [Fact]
    public void 짧은_내역은_한_쪽에_원문_그대로_담기고_버튼이_없다()
    {
        const string text = "*한식이 선정된 이유 — 점수 17점 (1위)*\n• 마지막 방문 2026-10-01 → 20일 경과";

        var view = ResultDetailsModal.Build(PollId, Day, text);

        Assert.Null(view.Submit);
        Assert.Contains("2026-10-20", HeaderText(view));
        Assert.Equal(text, Assert.Single(SectionTexts(view)));
        Assert.Empty(view.Blocks.OfType<ActionsBlock>());
        Assert.Single(Pages(text));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void 긴_내역은_줄_끝에서_나뉘어_한_줄이_두_블록에_걸치지_않고_원문과_같다(string newline)
    {
        var text = Lines(count: 400, lineLength: 100, newline);

        var sections = Pages(text).SelectMany(SectionTexts).ToList();

        Assert.Equal(text, string.Concat(sections));
        Assert.All(sections, s =>
        {
            Assert.InRange(s.Length, 1, ResultDetailsModal.MaxSectionTextLength);
            Assert.False(s.StartsWith('\n'), "CRLF가 \\r과 \\n 사이에서 갈라지면 안 된다.");
        });
        Assert.All(sections.SkipLast(1), s => Assert.Equal('\n', s[^1]));
    }

    [Theory]
    [InlineData(3000, 1)]
    [InlineData(3001, 2)]
    [InlineData(6000, 2)]
    [InlineData(7000, 3)]
    public void 줄이_한도를_넘으면_글자_수로_자르되_한_글자도_잃지_않는다(int length, int expectedSections)
    {
        var text = new string('가', length);

        var sections = SectionTexts(ResultDetailsModal.Build(PollId, Day, text));

        Assert.Equal(expectedSections, sections.Count);
        Assert.All(sections, s => Assert.InRange(s.Length, 1, ResultDetailsModal.MaxSectionTextLength));
        Assert.Equal(text, string.Concat(sections));
    }

    [Fact]
    public void 경계에_걸친_서로게이트_쌍은_쪼개지_않고_다음_조각으로_넘긴다()
    {
        // 😀는 UTF-16 두 단위 — 2999·3000번째에 걸쳐 있다.
        var text = new string('가', ResultDetailsModal.MaxSectionTextLength - 1) + "😀" + new string('나', 10);

        var sections = SectionTexts(ResultDetailsModal.Build(PollId, Day, text));

        Assert.Equal(2, sections.Count);
        Assert.Equal(ResultDetailsModal.MaxSectionTextLength - 1, sections[0].Length);
        Assert.StartsWith("😀", sections[1], StringComparison.Ordinal);
        Assert.Equal(text, string.Concat(sections));
    }

    [Fact]
    public void 서로게이트가_이어지는_긴_내역은_어느_경계에서도_쌍이_깨지지_않는다()
    {
        // 앞의 한 글자 때문에 쌍이 짝수 번째 경계마다 어긋난다.
        var text = "a" + string.Concat(Enumerable.Repeat("😀", 4000));

        var pages = Pages(text).SelectMany(SectionTexts).ToList();

        Assert.Equal(text, string.Concat(pages));
        Assert.All(pages, s =>
        {
            Assert.False(char.IsLowSurrogate(s[0]));
            Assert.False(char.IsHighSurrogate(s[^1]));
            Assert.InRange(s.Length, 1, ResultDetailsModal.MaxSectionTextLength);
        });
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(20, 2)]
    [InlineData(21, 3)]
    public void 한_쪽에_싣는_블록_수를_넘으면_다음_쪽으로_나뉘고_모든_쪽을_이으면_원문이다(int chunks, int expectedPages)
    {
        var text = new string('가', ResultDetailsModal.MaxSectionTextLength * chunks);

        Assert.Equal(expectedPages, Pages(text).Count());
        Assert.Equal(text, AllPagesText(text));

        for (var page = 0; page < expectedPages; page++)
        {
            var view = ResultDetailsModal.Build(PollId, Day, text, page);
            Assert.InRange(view.Blocks.Count, 1, 100);
            Assert.Equal(
                Math.Min(ResultDetailsModal.MaxSectionsPerPage, chunks - page * ResultDetailsModal.MaxSectionsPerPage),
                SectionTexts(view).Count);
        }
    }

    [Fact]
    public void 쪽_이동_버튼은_이웃_쪽만_가리키고_머리글에_현재_쪽이_표시된다()
    {
        var text = new string('가', ResultDetailsModal.MaxSectionTextLength * 25);

        var first = Buttons(ResultDetailsModal.Build(PollId, Day, text, 0));
        var middle = Buttons(ResultDetailsModal.Build(PollId, Day, text, 1));
        var last = Buttons(ResultDetailsModal.Build(PollId, Day, text, 2));

        Assert.Equal([ActionIds.ResultDetails(PollId, 1)], first);
        Assert.Equal([ActionIds.ResultDetails(PollId, 0), ActionIds.ResultDetails(PollId, 2)], middle);
        Assert.Equal([ActionIds.ResultDetails(PollId, 1)], last);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void 범위_밖_쪽은_다른_쪽으로_바꾸지_않고_거절한다(int page)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResultDetailsModal.Build(PollId, Day, "내역", page));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public void 내용이_없는_내역으로는_모달을_만들지_않는다(string text)
    {
        Assert.Throws<ArgumentException>(() => ResultDetailsModal.Build(PollId, Day, text));
    }

}
