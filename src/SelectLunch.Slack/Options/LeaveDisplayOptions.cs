using System.Text.RegularExpressions;
using SelectLunch.Slack.Leave;

namespace SelectLunch.Slack.Options;

/// <summary>
/// 메뉴 메시지에 "오늘 휴가자"를 보여 주기 위한 메일 조회 설정. 기본은 꺼짐이며, 꺼져 있으면 조회·검증 모두 하지 않고
/// 의도적으로 휴가 정보를 숨긴다(<see cref="LeaveRosterStatus.Disabled"/>).
/// 켜면 <see cref="Executable"/>(+ <see cref="Arguments"/>)이 실제로 실행할 수 있는 메일 검색 도구여야 한다.
/// 메일에서 읽은 내용은 이 설정이나 어디에도 저장하지 않는다. 값은 핫리로드 대상이 아니다.
/// </summary>
public sealed class LeaveDisplayOptions
{
    public const string SectionName = "LeaveDisplay";

    public const int MaxTimeoutSeconds = 3600;

    /// <summary>메일 도구(<c>-MaxResults</c>)가 받는 상한. 이 값에서 멈추면 도구가 "불완전"을 알린다.</summary>
    public const int MaxResultsLimit = 1000;

    static readonly Regex MailAddress = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z", RegexOptions.CultureInvariant);

    public bool Enabled { get; set; }

    /// <summary>회사별 메일 형식 어댑터 이름. 지원하지 않는 값이면 켜진 상태에서 시작 시 실패한다.</summary>
    public string Provider { get; set; } = LeaveMailProviders.SnuailabProvider;

    /// <summary>실행 파일 경로 또는 PATH의 명령 이름. 셸을 거치지 않고 그대로 실행한다.</summary>
    public string Executable { get; set; } = "";

    /// <summary><see cref="Executable"/> 뒤, 조회 인자(-AsJson 등) 앞에 붙는 고정 인자.</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>검색할 메일 계정(<c>-Account</c>).</summary>
    public string Account { get; set; } = "";

    /// <summary>도구 한 번 실행의 전체 제한 시간(초).</summary>
    public int TimeoutSeconds { get; set; } = 90;

    public int MaxResults { get; set; } = MaxResultsLimit;

    /// <summary>
    /// 켜져 있을 때만 검사한다. 오류 메시지에는 설정 이름만 넣고 값(계정·경로)은 넣지 않는다.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        if (!LeaveMailProviders.IsSupported(Provider))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Provider)} 값을 지원하지 않습니다. 지원하는 값: {LeaveMailProviders.SnuailabProvider}.");

        if (string.IsNullOrWhiteSpace(Executable))
            throw Missing(nameof(Executable));

        if (!CanResolveExecutable(Executable.Trim()))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Executable)}에 지정한 실행 파일을 찾을 수 없습니다.");

        if (Arguments is null || Array.Exists(Arguments, static a => a is null))
            throw new InvalidOperationException($"{SectionName}:{nameof(Arguments)}에 빈 항목이 있습니다.");

        if (string.IsNullOrWhiteSpace(Account))
            throw Missing(nameof(Account));

        if (!MailAddress.IsMatch(Account.Trim()))
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Account)}는 name@company.com 형식의 메일 주소여야 합니다.");

        if (TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(TimeoutSeconds)}는 1–{MaxTimeoutSeconds} 사이여야 합니다.");

        if (MaxResults is < 1 or > MaxResultsLimit)
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxResults)}는 1–{MaxResultsLimit} 사이여야 합니다.");
    }

    /// <summary>
    /// 프로세스 시작이 찾을 수 있는 실행 파일인가. 경로면 파일 존재를, 이름만이면 PATH 탐색을 본다
    /// (Windows에서 확장자가 없으면 CreateProcess처럼 <c>.exe</c>도 시도한다).
    /// </summary>
    public static bool CanResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        if (Path.IsPathRooted(executable) || executable.Contains('/') || executable.Contains('\\'))
            return FileExists(Path.GetFullPath(executable));

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (FileExists(Path.Combine(directory.Trim('"'), executable)))
                    return true;
            }
            catch (ArgumentException)
            {
                // PATH에 경로로 쓸 수 없는 항목이 섞여 있어도 나머지 항목은 계속 본다.
            }
        }

        return false;
    }

    static bool FileExists(string path) =>
        File.Exists(path) || (OperatingSystem.IsWindows() && !Path.HasExtension(path) && File.Exists(path + ".exe"));

    static InvalidOperationException Missing(string name) =>
        new($"{SectionName}:{name} 설정이 비어 있습니다. appsettings.Local.json 또는 환경변수로 지정하세요.");
}
