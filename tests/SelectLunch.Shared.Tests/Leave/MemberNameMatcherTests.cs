using SelectLunch.Shared.Leave;
using SelectLunch.Shared.Members;

namespace SelectLunch.Shared.Tests;

public class MemberNameMatcherTests
{
    private static RosterPerson P(string id, string realName, string displayName = "") => new(id, realName, displayName);

    [Fact]
    public void 실명과_정확히_같으면_유일_매칭이다()
    {
        var a = P("U1", "가나다", "alpha");
        var matcher = new MemberNameMatcher([a, P("U2", "라마바", "beta")]);

        var match = matcher.Match("가나다");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(a, match.Member);
    }

    [Fact]
    public void 별칭과_정확히_같으면_유일_매칭이다()
    {
        var a = P("U1", "가나다", "alpha");
        var matcher = new MemberNameMatcher([a, P("U2", "라마바", "beta")]);

        var match = matcher.Match("Alpha");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(a, match.Member);
    }

    [Fact]
    public void 공백과_대소문자를_무시하고_정확_일치한다()
    {
        var a = P("U1", "Jane  Doe", "");
        var matcher = new MemberNameMatcher([a]);

        Assert.Same(a, matcher.Match("  jane\tDOE ").Member);
        Assert.Same(a, matcher.Match("JANEDOE").Member);
    }

    [Fact]
    public void 분해형_한글과_조합형_한글은_같은_이름이다()
    {
        var composed = "가나다".Normalize(System.Text.NormalizationForm.FormC);
        var decomposed = "가나다".Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);
        var a = P("U1", composed);
        var b = P("U2", decomposed);

        Assert.Same(a, new MemberNameMatcher([a]).Match(decomposed).Member);
        Assert.Same(b, new MemberNameMatcher([b]).Match(composed).Member);
    }

    [Fact]
    public void 폭이_없는_서식문자는_이름에서_무시한다()
    {
        var a = P("U1", "가나다");

        var match = new MemberNameMatcher([a]).Match("가\u200B나\u200E다");

        Assert.Same(a, match.Member);
    }

    [Fact]
    public void 추출한_이름이_DB_이름의_접미사이면_매칭한다()
    {
        var a = P("U1", "홍길동");
        var matcher = new MemberNameMatcher([a, P("U2", "김철수")]);

        var match = matcher.Match("길동");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(a, match.Member);
    }

    [Fact]
    public void DB_이름이_추출한_이름의_접미사이면_매칭한다()
    {
        var a = P("U1", "홍길동", "길동");
        var matcher = new MemberNameMatcher([a, P("U2", "김철수", "철수")]);

        var match = matcher.Match("Dev 홍길동");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(a, match.Member);
    }

    [Fact]
    public void 별칭이_접미사_관계여도_매칭한다()
    {
        var a = P("U1", "Someone Else", "kim");
        var matcher = new MemberNameMatcher([a]);

        var match = matcher.Match("Chris Kim");

        Assert.Same(a, match.Member);
    }

    [Theory]
    [InlineData("홍길")]   // 접두
    [InlineData("길")]     // 가운데 한 글자
    [InlineData("홍동")]   // 중간 글자를 건너뛴 부분열
    public void 접두나_부분_문자열만_겹치면_매칭하지_않는다(string extracted)
    {
        var matcher = new MemberNameMatcher([P("U1", "홍길동"), P("U2", "김철수")]);

        var match = matcher.Match(extracted);

        Assert.Equal(MemberNameMatchStatus.None, match.Status);
        Assert.Null(match.Member);
    }

    [Fact]
    public void 가운데_부분만_겹치면_매칭하지_않는다()
    {
        var matcher = new MemberNameMatcher([P("U1", "AB홍길동CD")]);

        Assert.Equal(MemberNameMatchStatus.None, matcher.Match("홍길동").Status);
    }

    [Fact]
    public void 정확_일치가_있으면_접미사_후보는_보지_않는다()
    {
        var exact = P("U1", "길동");
        var suffix = P("U2", "홍길동");
        var matcher = new MemberNameMatcher([suffix, exact]);

        var match = matcher.Match("길동");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(exact, match.Member);
    }

    [Fact]
    public void 서로_다른_사람이_같은_별칭이면_모호하다()
    {
        var matcher = new MemberNameMatcher([P("U1", "가나다", "dev"), P("U2", "라마바", "Dev")]);

        var match = matcher.Match("dev");

        Assert.Equal(MemberNameMatchStatus.Ambiguous, match.Status);
        Assert.Null(match.Member);
    }

    [Fact]
    public void 한_사람의_실명과_별칭이_같은_말이면_모호하지_않다()
    {
        var a = P("U1", "가나다", "가나다");

        var match = new MemberNameMatcher([a]).Match("가나다");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(a, match.Member);
    }

    [Fact]
    public void 같은_SlackUserId_행이_중복되어도_모호하지_않다()
    {
        var first = P("U1", "가나다", "alpha");
        var matcher = new MemberNameMatcher([first, P("U1", "가나다", "alpha2")]);

        var match = matcher.Match("가나다");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(first, match.Member);
    }

    [Fact]
    public void 한_사람의_실명과_다른_사람의_별칭이_같으면_모호하다()
    {
        var matcher = new MemberNameMatcher([P("U1", "가나다", "x"), P("U2", "라마바", "가나다")]);

        Assert.Equal(MemberNameMatchStatus.Ambiguous, matcher.Match("가나다").Status);
    }

    [Fact]
    public void 정확_일치가_없고_접미사_후보가_둘_이상이면_모호하다()
    {
        var matcher = new MemberNameMatcher([P("U1", "홍길동"), P("U2", "박길동"), P("U3", "김철수")]);

        var match = matcher.Match("길동");

        Assert.Equal(MemberNameMatchStatus.Ambiguous, match.Status);
        Assert.Null(match.Member);
    }

    [Fact]
    public void 양방향_접미사_후보가_서로_다른_사람이면_모호하다()
    {
        // 입력 "홍길동"은 U1의 "길동"을 접미사로 가지고(입력 쪽이 더 김), U2의 "Dev 홍길동"은 입력을 접미사로 가진다(DB 쪽이 더 김).
        var matcher = new MemberNameMatcher([P("U1", "길동"), P("U2", "Dev 홍길동")]);

        Assert.Equal(MemberNameMatchStatus.Ambiguous, matcher.Match("홍길동").Status);
    }

    [Fact]
    public void 같은_사람의_여러_이름이_접미사로_맞아도_유일_매칭이다()
    {
        var a = P("U1", "홍길동", "길동");

        var match = new MemberNameMatcher([a]).Match("동");

        Assert.Equal(MemberNameMatchStatus.Unique, match.Status);
        Assert.Same(a, match.Member);
    }

    [Fact]
    public void 명단에_없는_사람이면_없음이다()
    {
        var matcher = new MemberNameMatcher([P("U1", "홍길동")]);

        var match = matcher.Match("이몽룡");

        Assert.Equal(MemberNameMatchStatus.None, match.Status);
        Assert.Null(match.Member);
    }

    [Fact]
    public void 호출자가_넘긴_명단만_후보다()
    {
        // 비활성 구성원 걸러내기는 호출자 책임이다. 명단에 넣지 않은 사람은 후보가 아니다.
        var active = P("U1", "홍길동");
        var matcher = new MemberNameMatcher([active]);

        Assert.Equal(MemberNameMatchStatus.None, matcher.Match("김철수").Status);
        Assert.Same(active, matcher.Match("홍길동").Member);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B")]
    public void 빈_입력은_없음이다(string extracted)
    {
        var matcher = new MemberNameMatcher([P("U1", "홍길동", "길동")]);

        Assert.Equal(MemberNameMatchStatus.None, matcher.Match(extracted).Status);
    }

    [Fact]
    public void 이름이_비어_있는_구성원은_어떤_입력과도_맞지_않는다()
    {
        var matcher = new MemberNameMatcher([P("U1", "", ""), P("U2", "  ", "홍길동")]);

        // 빈 이름이 모든 문자열의 접미사로 취급되면 안 된다.
        Assert.Equal(MemberNameMatchStatus.Unique, matcher.Match("홍길동").Status);
        Assert.Equal(MemberNameMatchStatus.None, matcher.Match("김철수").Status);
    }

    [Fact]
    public void 빈_명단은_항상_없음이다()
    {
        var matcher = new MemberNameMatcher([]);

        Assert.Equal(MemberNameMatchStatus.None, matcher.Match("홍길동").Status);
    }

    [Fact]
    public void 매칭은_명단_원본_변경에_영향받지_않는다()
    {
        var list = new List<RosterPerson> { P("U1", "홍길동") };
        var matcher = new MemberNameMatcher(list);

        list.Clear();

        Assert.Equal(MemberNameMatchStatus.Unique, matcher.Match("홍길동").Status);
    }
}
