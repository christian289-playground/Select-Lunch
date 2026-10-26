using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SelectLunch.Shared.Leave;

namespace SelectLunch.Slack.Leave.Snuailab;

/// <summary>
/// 회사 휴가 메일(Outlook 일정 메일) 해석기. 상태가 없고 입력 메일을 저장·기록하지 않는다.
/// <para>
/// 회사 휴가 메일은 "신청자/기간" 라벨 양식이 아니라 일정 메일이다. 사람은 <b>제목</b>에서, 기간은 본문 머리글의
/// <b>일정 줄</b>에서만 읽는다. 보낸이·수신자·보낸 시각·목록 행의 받은 날짜는 휴가자나 휴가일이 아니다.
/// </para>
/// <list type="bullet">
/// <item>제목 형식(줄 전체가 정확히 일치해야 함): <c>연차-이름</c>, <c>이름-연차</c>, <c>이름 연차</c>, <c>이름 휴가</c>, <c>이름 오전 반차</c>.
/// 휴가·연차·오전 반차는 승인된 것으로 본다. 승인·취소·변경 상태는 따지지 않는다.</item>
/// <item>일정 줄(연도 항상 포함): 하루 종일 <c>(요일 YYYY-MM-DD(하루 종일))</c>, 여러 날 <c>(요일 YYYY-MM-DD - 요일 YYYY-MM-DD)</c>,
/// 시간 지정 <c>(요일 YYYY-MM-DD 오전 H:MM - 오후 H:MM)</c>. 앞 안내 문구는 관측한 "이 이벤트가 발생함 N… 전"이지만
/// 앞으로 올 일정의 문구는 관측하지 못했으므로, 어떤 문구든 괄호 안이 이 모양이면(제목 줄 제외) 읽는다.
/// 응답 전 초대 메일의 괄호 없는 단독 범위 줄도 읽는다. 양 끝 날짜를 포함한다.</item>
/// <item>"받는 사람:" 이후(수신자·보낸 시각·인용)는 읽지 않는다. 연도 없는 날짜는 쓰지 않고 연도를 추정하지 않는다.</item>
/// </list>
/// <para>
/// 해석 못 한 것을 "휴가 없음"으로 속이지 않는다. 사람은 있고 기간이 불명이면 <see cref="LeaveMailParseResult.UnresolvedNames"/>,
/// 휴가 일정인데 사람을 식별하지 못하면 <see cref="LeaveMailParseResult.HasUnresolvedContent"/>다.
/// 일정 줄도 사람 형식 제목도 없는 메일(공지·잡담)과 일정 제목에 휴가 키워드가 없는 일반 일정은 무시한다.
/// 회신·전달 접두 제목은 일정 줄이 있을 때만(전달된 일정) 같은 일정으로 읽고, 일정 줄이 없으면 휴가 알림이 아니라 무시한다.
/// </para>
/// </summary>
public sealed partial class SnuailabLeaveMailParser : ILeaveMailParser
{
    /// <summary>Outlook 검색어. 쉼표 없는 비어 있지 않은 항목이며 공백이 든 구절도 허용한다.</summary>
    public static IReadOnlyList<string> Keywords { get; } = ["휴가", "연차", "오전 반차"];

    private const int MaxHeaderLines = 400;
    private const int MaxSubjectLength = 60;
    private const int MaxScheduleLength = 80;
    private const int RegexTimeoutMilliseconds = 1000;

    private const string Word = @"[\p{L}\p{M}][\p{L}\p{M}'’.·]*";
    private const string NameGroup = "(?<name>" + Word + "(?: " + Word + ")*)";
    private const string KeywordPattern = "(?:휴가|연차|오전 ?반차)";
    private const string Dash = "[-–—]";

    private const string WeekdayGroup = "(?<w>[월화수목금토일])(?:요일)?";
    private const string IsoDate = @"(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})";
    private const string Time = @"(?:(?:오전|오후) ?)?\d{1,2}:\d{2}";

    private static readonly LeaveMailParseResult Nothing = new([], []);
    private static readonly LeaveMailParseResult Unknown = new([], [], true);

    /// <summary>제목 한 줄 전체와 맞아야 하는 형식들. 위에서부터 먼저 맞는 형식을 쓴다.</summary>
    private static readonly Regex[] NamePatterns = [KeywordFirst(), NameFirstDashed(), NameFirstSpaced()];

    public LeaveMailParseResult Parse(LeaveMailCandidate mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var header = ReadLines(mail.Body, stopAtRecipients: true);
        if (header.Count == 0)
        {
            // 본문을 읽지 못했다. 행 글에 휴가 키워드가 있으면 확인하지 못한 것이지 "휴가 없음"이 아니다.
            return LeaveKeyword().IsMatch(string.Join(' ', ReadLines(mail.Text, stopAtRecipients: false))) ? Unknown : Nothing;
        }

        var eventAt = -1;
        var invalidEvent = false;
        var ranges = new List<(DateOnly Start, DateOnly End)>();
        for (var i = 0; i < header.Count; i++)
        {
            // 제목 줄(맨 위)은 일정 줄로 읽지 않는다. 제목에 날짜가 들어 있어도 일정 안내가 아니다.
            var kind = ParseEventLine(header[i], allowUnprefixedEnclosed: i > 0, out var start, out var end);
            if (kind == EventLineKind.None) continue;

            if (eventAt < 0) eventAt = i;
            if (kind == EventLineKind.Invalid) invalidEvent = true;
            else if (!ranges.Contains((start, end))) ranges.Add((start, end));
        }

        return eventAt >= 0
            ? ParseScheduledEvent(header[0], invalidEvent, ranges)
            : ParseWithoutSchedule(header[0]);
    }

    /// <summary>일정 줄이 있는 메일: 사람은 첫 제목 줄에서만 읽는다.</summary>
    private static LeaveMailParseResult ParseScheduledEvent(
        string heading, bool invalidEvent, List<(DateOnly Start, DateOnly End)> ranges)
    {
        var subject = StripReplyPrefix(heading, out _);
        if (!LeaveKeyword().IsMatch(subject)) return Nothing;
        if (!TryExtractName(subject, out var person)) return Unknown;

        if (invalidEvent || ranges.Count != 1)
        {
            // 날짜가 잘못됐거나 서로 다른 기간이 둘 이상이면 고르지 않는다.
            return new LeaveMailParseResult([], [person]);
        }

        return new LeaveMailParseResult([new LeavePeriod(person, ranges[0].Start, ranges[0].End)], []);
    }

    /// <summary>일정 줄이 없는 메일: 제목이 정확한 휴가 형식이면 사람만 알 뿐 기간은 모른다.</summary>
    private static LeaveMailParseResult ParseWithoutSchedule(string heading)
    {
        var subject = StripReplyPrefix(heading, out var isReplyOrForward);
        if (isReplyOrForward) return Nothing;

        return TryExtractName(subject, out var name)
            ? new LeaveMailParseResult([], [name])
            : Nothing;
    }

    // --- 제목 → 이름 ---------------------------------------------------------------

    private static bool TryExtractName(string subject, out string name)
    {
        name = string.Empty;
        if (subject.Length == 0 || subject.Length > MaxSubjectLength) return false;

        foreach (var pattern in NamePatterns)
        {
            var match = pattern.Match(subject);
            if (!match.Success) continue;

            var candidate = match.Groups["name"].Value;
            // 이름 자리에 키워드가 오면 사람이 아니다(예: "연차-휴가").
            if (NameContainsKeyword().IsMatch(candidate)) return false;

            name = candidate;
            return true;
        }

        return false;
    }

    private static string StripReplyPrefix(string subject, out bool hadPrefix)
    {
        var prefix = ReplyPrefix().Match(subject);
        hadPrefix = prefix.Success;
        return prefix.Success ? subject[prefix.Length..] : subject;
    }

    // --- 일정 줄 -------------------------------------------------------------------

    private enum EventLineKind
    {
        /// <summary>일정 줄이 아니다.</summary>
        None,

        /// <summary>일정 줄이 맞지만 날짜를 믿을 수 없다(없는 날짜·요일 불일치·끝이 시작보다 앞·읽을 수 없는 모양).</summary>
        Invalid,

        Valid,
    }

    private static EventLineKind ParseEventLine(string line, bool allowUnprefixedEnclosed, out DateOnly start, out DateOnly end)
    {
        start = default;
        end = default;

        // 일정 줄은 세 가지다. (1) "이 이벤트…" 안내 + 괄호 안 기간, (2) 안내 문구가 달라도(예: 앞으로 올 일정) 괄호 안이
        // 연도 있는 일정 모양인 줄, (3) 응답 전 초대 메일의 괄호·안내 없는 단독 범위 줄.
        var prefixed = line.StartsWith("이 이벤트", StringComparison.Ordinal);
        var tail = TrailingParenthesis().Match(line);
        string segment;
        bool enclosed;
        if (tail.Success && (prefixed || (allowUnprefixedEnclosed && StartsLikeSchedule(tail.Groups["seg"].Value))))
        {
            segment = tail.Groups["seg"].Value;
            enclosed = true;
        }
        else if (prefixed)
        {
            return EventLineKind.Invalid;
        }
        else
        {
            if (line.Length > MaxScheduleLength) return EventLineKind.None;
            segment = line;
            enclosed = false;
        }

        // 안내 접두가 있으면 일정 줄이 확실하니 모양이 이상할 때 Invalid, 그 밖에는 일정 줄이 아닐 수 있어 None.
        var badShape = prefixed ? EventLineKind.Invalid : EventLineKind.None;

        var parts = RangeSeparator().Split(segment);
        if (parts.Length > 2) return badShape;

        var first = StartPart().Match(parts[0]);
        if (!first.Success) return badShape;

        // 괄호 밖의 한 날짜+시각 줄은 "보낸 시각" 줄과 같은 모양이라 일정으로 보지 않는다.
        if (parts.Length == 1 && !enclosed && !first.Groups["allday"].Success) return EventLineKind.None;

        if (!TryReadDate(first, out start)) return EventLineKind.Invalid;

        end = start;
        if (parts.Length == 2)
        {
            var endDate = EndDatePart().Match(parts[1]);
            if (endDate.Success)
            {
                if (!TryReadDate(endDate, out end)) return EventLineKind.Invalid;
            }
            else if (!EndTimePart().IsMatch(parts[1]))
            {
                return badShape;
            }
        }

        return end < start ? EventLineKind.Invalid : EventLineKind.Valid;
    }

    /// <summary>괄호 안 글이 "(요일) YYYY-MM-DD…"로 시작하는 일정 모양인가(연도가 없는 표기는 일정 모양이 아니다).</summary>
    private static bool StartsLikeSchedule(string segment) => StartPart().IsMatch(RangeSeparator().Split(segment)[0]);

    private static bool TryReadDate(Match match, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(match.Groups["y"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(match.Groups["m"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(match.Groups["d"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return false;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;

        date = new DateOnly(year, month, day);

        // Outlook은 요일을 날짜에서 계산해 적는다. 어긋나면 다른 형식의 숫자를 날짜로 오해한 것이다.
        var weekday = match.Groups["w"];
        return !weekday.Success || WeekdayOf(weekday.ValueSpan[0]) == date.DayOfWeek;
    }

    private static DayOfWeek WeekdayOf(char symbol) => symbol switch
    {
        '월' => DayOfWeek.Monday,
        '화' => DayOfWeek.Tuesday,
        '수' => DayOfWeek.Wednesday,
        '목' => DayOfWeek.Thursday,
        '금' => DayOfWeek.Friday,
        '토' => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    // --- 본문 정리 -----------------------------------------------------------------

    /// <summary>
    /// NFC로 정규화하고, 아이콘 글자(사설 영역)·폭 없는 서식 문자를 지우고, 공백을 하나로 접은 비어 있지 않은 줄 목록.
    /// <paramref name="stopAtRecipients"/>면 "받는 사람:" 줄 앞까지만 읽는다(그 뒤는 수신자·보낸 시각·인용).
    /// </summary>
    private static List<string> ReadLines(string? raw, bool stopAtRecipients)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return lines;

        var text = ToNfc(raw);
        var line = new StringBuilder();
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (c is '\n' or '\r')
            {
                if (EndLine(line, lines, stopAtRecipients)) return lines;
                pendingSpace = false;
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.PrivateUse or UnicodeCategory.Format) continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = line.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                line.Append(' ');
                pendingSpace = false;
            }

            line.Append(c);
        }

        EndLine(line, lines, stopAtRecipients);
        return lines;
    }

    /// <summary>한 줄을 끝낸다. 더 읽지 말아야 하면 true.</summary>
    private static bool EndLine(StringBuilder line, List<string> lines, bool stopAtRecipients)
    {
        if (line.Length == 0) return false;

        var value = line.ToString();
        line.Clear();

        if (stopAtRecipients && RecipientLabel().IsMatch(value)) return true;

        lines.Add(value);
        return lines.Count >= MaxHeaderLines;
    }

    /// <summary>짝 없는 서로게이트가 섞인 글은 정규화하지 못하므로 그대로 둔다.</summary>
    private static string ToNfc(string value)
    {
        try
        {
            return value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    // --- 정규식 --------------------------------------------------------------------

    [GeneratedRegex("휴가|연차|오전 ?반차", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex LeaveKeyword();

    [GeneratedRegex("휴가|연차|반차", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex NameContainsKeyword();

    [GeneratedRegex("^" + KeywordPattern + " ?" + Dash + " ?" + NameGroup + "$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex KeywordFirst();

    [GeneratedRegex("^" + NameGroup + " ?" + Dash + " ?" + KeywordPattern + "$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex NameFirstDashed();

    [GeneratedRegex("^" + NameGroup + " " + KeywordPattern + "$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex NameFirstSpaced();

    [GeneratedRegex("^(?:(?:re|fw|fwd|회신|전달|답장) ?: ?)+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex("^(?:받는 ?사람|to) ?:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex RecipientLabel();

    /// <summary>줄 끝의 괄호 구간. 안쪽에 "(하루 종일)" 같은 한 겹 괄호가 올 수 있다.</summary>
    [GeneratedRegex(@"\((?<seg>[^()]*(?:\([^()]*\)[^()]*)?)\)$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex TrailingParenthesis();

    [GeneratedRegex(" [-–—~] ", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex RangeSeparator();

    [GeneratedRegex("^(?:" + WeekdayGroup + " ?)?" + IsoDate + @"(?<allday> ?\( ?하루 ?종일 ?\)| 하루 ?종일)?(?: " + Time + ")?$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex StartPart();

    [GeneratedRegex("^(?:" + WeekdayGroup + " ?)?" + IsoDate + "(?: " + Time + ")?$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex EndDatePart();

    [GeneratedRegex("^" + Time + "$", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex EndTimePart();
}
