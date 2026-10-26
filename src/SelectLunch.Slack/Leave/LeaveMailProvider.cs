using SelectLunch.Slack.Leave.Snuailab;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// 회사별 메일 어댑터 한 벌: 메일 검색에 쓸 키워드와 그 회사 메일 형식을 읽는 파서.
/// 두 값은 회사 형식에 묶여 있어 함께 교체한다. 상태가 없다.
/// </summary>
public sealed record LeaveMailProvider(ILeaveMailParser Parser, IReadOnlyList<string> Keywords);

/// <summary>회사 이름으로 <see cref="LeaveMailProvider"/>를 만든다. 지원하지 않는 이름은 만들지 않고 실패한다.</summary>
public static class LeaveMailProviders
{
    public const string SnuailabProvider = "snuailab";

    public static bool IsSupported(string? provider) =>
        string.Equals(provider?.Trim(), SnuailabProvider, StringComparison.OrdinalIgnoreCase);

    /// <exception cref="InvalidOperationException">지원하지 않는 이름.</exception>
    public static LeaveMailProvider Create(string? provider)
    {
        if (!IsSupported(provider))
            throw new InvalidOperationException(
                $"휴가 메일 형식 제공자를 지원하지 않습니다. 지원하는 값: {SnuailabProvider}.");

        return new LeaveMailProvider(new SnuailabLeaveMailParser(), SnuailabLeaveMailParser.Keywords);
    }
}
