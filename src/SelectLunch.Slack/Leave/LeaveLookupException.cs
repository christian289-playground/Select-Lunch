namespace SelectLunch.Slack.Leave;

/// <summary>휴가 정보를 확신할 수 없게 만든 원인 분류. 로그에는 이 이름만 남긴다.</summary>
public enum LeaveLookupFailure
{
    /// <summary>Slack 채널 명단을 끝까지 모으지 못했다(부분 명단은 쓰지 않는다).</summary>
    SlackDirectory,

    /// <summary>메일 도구를 실행하지 못했거나 비정상 종료했다.</summary>
    MailTransport,

    /// <summary>메일 도구가 제한 시간 안에 끝나지 않았다.</summary>
    MailTimeout,

    /// <summary>메일 도구 출력이 약속한 모양이 아니거나 너무 크다.</summary>
    MailOutput,

    /// <summary>메일 도구가 결과가 잘렸거나 불완전하다고 알렸다.</summary>
    MailIncomplete,

    /// <summary>회사 휴가 양식으로 확인되는 메일인데 사람이나 기간을 해석하지 못했다.</summary>
    MailUnparsed,

    /// <summary>메뉴일 휴가 기간의 이름이 현재 채널 사람 둘 이상과 맞는다.</summary>
    NameAmbiguous,

    /// <summary>현재 채널 사람으로 보이는 이름의 휴가 기간을 확정하지 못했다.</summary>
    DatesUnresolved,

    /// <summary>휴가자로 확인된 사람의 표시 이름이 비어 있다.</summary>
    AliasUnusable,
}

/// <summary>
/// "알 수 없음"을 알리는 예외. 메시지는 원인 분류만 담고 사람·날짜·메일 내용·외부 도구 출력은 절대 담지 않으며
/// 원인 예외도 붙이지 않는다(원인 예외 문자열에 개인정보가 섞일 수 있다).
/// </summary>
public sealed class LeaveLookupException(LeaveLookupFailure reason)
    : Exception($"휴가 정보를 조회하지 못했습니다 ({reason}).")
{
    public LeaveLookupFailure Reason { get; } = reason;
}
