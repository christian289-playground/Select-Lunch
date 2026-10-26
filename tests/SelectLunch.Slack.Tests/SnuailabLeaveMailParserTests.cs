using System.Text;
using SelectLunch.Shared.Leave;
using SelectLunch.Slack.Leave;
using SelectLunch.Slack.Leave.Snuailab;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 합성 메일로만 검증한다(실제 이름·날짜·본문 없음). 본문 배치는 회사 Outlook 일정 메일의 읽기 창 글 구조를 따른다:
/// 제목(heading) → 아이콘 줄 → UI 줄 → 일정 줄 → 보낸이 → "받는 사람:" → 보낸 시각 → 상투문.
/// </summary>
public class SnuailabLeaveMailParserTests
{
    private static readonly ILeaveMailParser Parser = new SnuailabLeaveMailParser();

    private static readonly DateOnly Mar4 = new(2031, 3, 4);
    private static readonly DateOnly Mar6 = new(2031, 3, 6);

    private const string Icon = "\uE001";

    private static string Weekday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        _ => "일",
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string AllDayOne(DateOnly date) => $"이 이벤트가 발생함 3주일 전 ({Weekday(date)} {Iso(date)}(하루 종일))";

    private static string AllDayRange(DateOnly start, DateOnly end) =>
        $"이 이벤트가 발생함 2개월 전 ({Weekday(start)} {Iso(start)} - {Weekday(end)} {Iso(end)})";

    private static string Timed(DateOnly date) =>
        $"이 이벤트가 발생함 1개월 전 ({Weekday(date)} {Iso(date)} 오전 9:00 - 오후 1:00)";

    /// <summary>일정 메일 읽기 창 본문. <paramref name="scheduleLines"/>가 제목·아이콘·UI 줄 다음에 온다.</summary>
    private static string EventBody(string subject, string scheduleLines, string sender = "라마바", string[]? recipients = null, string sentLine = "월 2031-01-06 오전 9:12")
    {
        var body = new StringBuilder();
        body.Append(subject).Append('\n');
        body.Append(Icon).Append('\n');
        body.Append(Icon).Append(' ').Append(Icon).Append('\n');
        body.Append("가 나다 메일 요약\n");
        body.Append(scheduleLines).Append('\n');
        body.Append(sender).Append('\n');
        body.Append("회신 단추 안내\n");
        body.Append("받는 사람:\n");
        foreach (var recipient in recipients ?? ["바사아"])
        {
            body.Append(recipient).Append(";\n");
        }

        body.Append(sentLine).Append('\n');
        body.Append("________\n");
        body.Append("모임 ID: 111 111 111\n");
        body.Append("회신\n전체 회신\n전달\n");
        return body.ToString();
    }

    private static string RowText(string subject) =>
        $"{Icon} 라마바 {Icon} {Icon} {subject} 월 10-05 ... 모임 옵션 ... 요청입니다 받은 편지함";

    private static LeaveMailParseResult Parse(string subject, string scheduleLines) =>
        Parser.Parse(new LeaveMailCandidate(RowText(subject), EventBody(subject, scheduleLines)));

    // --- 제목에서 이름 식별 + 일정 줄에서 기간 ---------------------------------------

    [Theory]
    [InlineData("연차-가나다", "가나다")]
    [InlineData("가나다-연차", "가나다")]
    [InlineData("가나다 연차", "가나다")]
    [InlineData("가나다 휴가", "가나다")]
    [InlineData("가나다 오전 반차", "가나다")]
    [InlineData("가나다 - 휴가", "가나다")]
    [InlineData("Alex Test 연차", "Alex Test")]
    public void 제목_형식별로_이름을_뽑고_하루_종일_일정이면_하루_기간이다(string subject, string name)
    {
        var result = Parse(subject, AllDayOne(Mar4));

        Assert.Equal([new LeavePeriod(name, Mar4, Mar4)], result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 여러_날_일정은_시작과_끝_날짜를_모두_포함하는_기간이다()
    {
        var result = Parse("가나다 연차", AllDayRange(Mar4, Mar6));

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar6)], result.Periods);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 시간이_지정된_오전_반차는_그날_하루_기간이다()
    {
        var result = Parse("가나다 오전 반차", Timed(Mar4));

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar4)], result.Periods);
        Assert.Empty(result.UnresolvedNames);
    }

    [Fact]
    public void 미응답_초대_메일의_접두_없는_일정_줄도_읽고_시간표_줄은_날짜로_보지_않는다()
    {
        var schedule = string.Join('\n',
            $"{Weekday(Mar4)} {Iso(Mar4)} - {Weekday(Mar6)} {Iso(Mar6)}",
            "수락",
            "거절",
            "가나다 연차",
            "오전 9",
            "오전 10",
            "오후 1",
            "03월 04",
            "03월 05");

        var result = Parse("가나다 연차", schedule);

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar6)], result.Periods);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 아이콘_글자와_폭없는_문자_NFC_차이를_무시한다()
    {
        var decomposed = "가나다".Normalize(NormalizationForm.FormD);
        var subject = $"{Icon}{decomposed}\u200B 연차";

        var result = Parse(subject, AllDayOne(Mar4));

        var period = Assert.Single(result.Periods);
        Assert.Equal("가나다", period.PersonName);
        Assert.Equal(Mar4, period.StartDate);
    }

    [Fact]
    public void 전달_접두_제목도_일정_줄이_있으면_같은_일정으로_읽는다()
    {
        var result = Parse("FW: 가나다 연차", AllDayOne(Mar4));

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar4)], result.Periods);
    }

    // --- 이름·날짜 연관의 경계 -------------------------------------------------------

    [Fact]
    public void 보낸이와_수신자_이름은_휴가자로_쓰지_않는다()
    {
        var body = EventBody("가나다 연차", AllDayOne(Mar4), sender: "라마바", recipients: ["바사아", "자차카"]);

        var result = Parser.Parse(new LeaveMailCandidate(RowText("가나다 연차"), body));

        var period = Assert.Single(result.Periods);
        Assert.Equal("가나다", period.PersonName);
        Assert.Empty(result.UnresolvedNames);
    }

    [Theory]
    [InlineData("가나다 연차", false)]
    [InlineData("휴가 안내", true)]
    public void 제목_외의_헤더_텍스트를_휴가자_이름으로_쓰지_않는다(string subject, bool unresolved)
    {
        var body = EventBody(subject, "라마바 휴가\n" + AllDayOne(Mar4));
        var result = Parser.Parse(new LeaveMailCandidate(RowText(subject), body));

        Assert.Equal(unresolved, result.HasUnresolvedContent);
        if (unresolved) Assert.Empty(result.Periods);
        else Assert.Equal([new LeavePeriod("가나다", Mar4, Mar4)], result.Periods);
    }

    [Fact]
    public void 휴가_일정_줄이_깨졌고_이름도_불명하면_휴가없음으로_처리하지_않는다()
    {
        var result = Parse("휴가 안내", "이 이벤트가 발생함 (읽을 수 없는 기간");

        Assert.Empty(result.Periods);
        Assert.True(result.HasUnresolvedContent);
    }

    [Fact]
    public void 받은_날짜_칸과_보낸_시각은_휴가일이_아니다()
    {
        var result = Parse("가나다 연차", AllDayOne(Mar4));

        var period = Assert.Single(result.Periods);
        Assert.Equal(Mar4, period.StartDate);
        Assert.Equal(Mar4, period.EndDate);
    }

    [Fact]
    public void 받는_사람_뒤에_있는_인용_일정_줄은_읽지_않는다()
    {
        var body = EventBody("가나다 연차", AllDayOne(Mar4), sentLine: AllDayRange(Mar6, Mar6.AddDays(20)));

        var result = Parser.Parse(new LeaveMailCandidate(RowText("가나다 연차"), body));

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar4)], result.Periods);
    }

    [Fact]
    public void 헤더에_서로_다른_기간이_둘이면_고르지_않고_이름을_미확정으로_돌려준다()
    {
        var schedule = AllDayOne(Mar4) + "\n" + AllDayRange(Mar6, Mar6.AddDays(1));

        var result = Parse("가나다 연차", schedule);

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 같은_기간이_두_줄로_반복되면_하나로_본다()
    {
        var schedule = AllDayRange(Mar4, Mar6) + "\n" + $"{Weekday(Mar4)} {Iso(Mar4)} - {Weekday(Mar6)} {Iso(Mar6)}";

        var result = Parse("가나다 연차", schedule);

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar6)], result.Periods);
    }

    [Theory]
    [InlineData("이 이벤트가 발생함 3주일 전")]
    [InlineData("이 이벤트가 발생함 5일 전")]
    [InlineData("이 이벤트가 발생함 2개월 전")]
    public void 경과_시간_표현의_숫자와_단위는_휴가일이_아니다(string prefix)
    {
        var result = Parse("가나다 연차", $"{prefix} ({Weekday(Mar4)} {Iso(Mar4)}(하루 종일))");

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar4)], result.Periods);
    }

    [Theory]
    [InlineData("이 이벤트는 3일 후에 발생합니다")]
    [InlineData("곧 시작")]
    public void 앞으로_올_일정의_안내_문구가_달라도_괄호_안_일정_모양이면_읽는다(string prefix)
    {
        var result = Parse("가나다 연차", $"{prefix} ({Weekday(Mar4)} {Iso(Mar4)} - {Weekday(Mar6)} {Iso(Mar6)})");

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar6)], result.Periods);
    }

    [Fact]
    public void 괄호_안이_연도_없는_날짜이면_일정_줄이_아니다()
    {
        var result = Parse("가나다 연차", "곧 시작 (화 03-04)");

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
    }

    [Fact]
    public void 제목_줄에_적힌_날짜는_일정_줄이_아니다()
    {
        var subject = $"가나다 연차 ({Weekday(Mar4)} {Iso(Mar4)})";
        var body = subject + "\n라마바\n받는 사람:\n바사아;\n";

        var result = Parser.Parse(new LeaveMailCandidate(RowText(subject), body));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 대화가_두_건이어서_같은_일정_줄과_보낸_시각_줄이_반복되어도_한_기간이다()
    {
        var body = string.Join('\n',
            "가나다 연차",
            Icon,
            "가 나다 메일 요약",
            AllDayRange(Mar4, Mar6),
            "월 2031-01-06 오전 9:12",
            AllDayRange(Mar4, Mar6),
            "화 2031-01-07 오후 3:30",
            "라마바",
            "받는 사람:",
            "바사아;",
            "수 2031-01-08 오후 4:00");

        var result = Parser.Parse(new LeaveMailCandidate(RowText("가나다 연차"), body));

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar6)], result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 같은_사람의_다른_메일은_각자_기간을_내고_합치는_일은_호출자가_한다()
    {
        var first = Parse("가나다 연차", AllDayOne(Mar4));
        var second = Parse("가나다 연차", AllDayOne(Mar6));

        Assert.Equal([new LeavePeriod("가나다", Mar4, Mar4)], first.Periods);
        Assert.Equal([new LeavePeriod("가나다", Mar6, Mar6)], second.Periods);
    }

    // --- 읽을 수 없는 날짜는 미확정 ---------------------------------------------------

    [Fact]
    public void 끝날짜가_시작보다_앞이면_이름을_미확정으로_돌려준다()
    {
        var result = Parse("가나다 연차", AllDayRange(Mar6, Mar4));

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
    }

    [Fact]
    public void 존재하지_않는_날짜는_미확정이다()
    {
        var result = Parse("가나다 연차", "이 이벤트가 발생함 1일 전 (월 2031-02-30(하루 종일))");

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
    }

    [Fact]
    public void 요일이_날짜와_맞지_않으면_미확정이다()
    {
        var wrongWeekday = Weekday(Mar4.AddDays(1));

        var result = Parse("가나다 연차", $"이 이벤트가 발생함 1일 전 ({wrongWeekday} {Iso(Mar4)}(하루 종일))");

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
    }

    [Fact]
    public void 일정_줄이_없으면_제목의_이름만_미확정으로_돌려주고_수신일로_채우지_않는다()
    {
        var body = "가나다 연차\n" + Icon + "\n가 나다 메일 요약\n라마바\n받는 사람:\n바사아;\n월 2031-01-06 오전 9:12\n";

        var result = Parser.Parse(new LeaveMailCandidate(RowText("가나다 연차"), body));

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 연도_없는_날짜만_있으면_연도를_추정하지_않는다()
    {
        var body = "가나다 연차\n03월 04\n받는 사람:\n";

        var result = Parser.Parse(new LeaveMailCandidate(RowText("가나다 연차"), body));

        Assert.Empty(result.Periods);
        Assert.Equal(["가나다"], result.UnresolvedNames);
    }

    // --- 식별 불가/휴가 일정 아님 ------------------------------------------------------

    [Fact]
    public void 휴가_일정인데_제목에서_사람을_식별하지_못하면_내용_미확정_신호를_켠다()
    {
        var result = Parse("연차 사용 현황 공유", AllDayOne(Mar4));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.True(result.HasUnresolvedContent);
    }

    [Fact]
    public void 식별_불가_휴가_일정에서도_보낸이와_수신자를_이름으로_삼지_않는다()
    {
        var body = EventBody("연차", AllDayOne(Mar4), sender: "라마바", recipients: ["바사아"]);

        var result = Parser.Parse(new LeaveMailCandidate(RowText("연차"), body));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.True(result.HasUnresolvedContent);
    }

    [Fact]
    public void 제목에_휴가_키워드가_없는_일반_일정은_무시한다()
    {
        var result = Parse("팀 점심 회식", AllDayOne(Mar4));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 오후_반차_일정은_휴가로_보지_않는다()
    {
        var result = Parse("가나다 오후 반차", Timed(Mar4));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 일정_줄도_사람_형식_제목도_없는_공지는_무시한다()
    {
        var body = "연차 사용 안내\n전 직원에게 연차 사용을 권장합니다.\n라마바\n받는 사람:\n바사아;\n";

        var result = Parser.Parse(new LeaveMailCandidate(RowText("연차 사용 안내"), body));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    [Fact]
    public void 회신_접두_제목은_일정_줄이_없으면_휴가_알림이_아니다()
    {
        var body = "RE: 가나다 연차\n감사합니다.\n라마바\n받는 사람:\n바사아;\n";

        var result = Parser.Parse(new LeaveMailCandidate(RowText("RE: 가나다 연차"), body));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    // --- 본문이 없을 때 ------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void 본문이_없고_행에_휴가_키워드가_있으면_해석_불가_신호다(string? body)
    {
        var result = Parser.Parse(new LeaveMailCandidate(RowText("가나다 연차"), body));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.True(result.HasUnresolvedContent);
    }

    [Fact]
    public void 본문이_없고_행에_휴가_키워드도_없으면_무시한다()
    {
        var result = Parser.Parse(new LeaveMailCandidate(RowText("팀 점심"), null));

        Assert.Empty(result.Periods);
        Assert.Empty(result.UnresolvedNames);
        Assert.False(result.HasUnresolvedContent);
    }

    // --- 결과 형태 -------------------------------------------------------------------

    [Fact]
    public void 같은_메일은_매번_같은_결과를_낸다()
    {
        var mail = new LeaveMailCandidate(RowText("가나다 연차"), EventBody("가나다 연차", AllDayRange(Mar4, Mar6)));

        var first = Parser.Parse(mail);
        var second = Parser.Parse(mail);

        Assert.Equal(first.Periods, second.Periods);
        Assert.Equal(first.UnresolvedNames, second.UnresolvedNames);
        Assert.Equal(first.HasUnresolvedContent, second.HasUnresolvedContent);
    }

    [Fact]
    public void 이름은_NFC_공백_정리된_글로_돌려준다()
    {
        var result = Parse("Alex   Test\u00A0연차", AllDayOne(Mar4));

        var period = Assert.Single(result.Periods);
        Assert.Equal("Alex Test", period.PersonName);
    }
}
