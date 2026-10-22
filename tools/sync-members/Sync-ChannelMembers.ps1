#Requires -Version 7.0
<#
.SYNOPSIS
슬랙 채널의 사람 명단을 로컬 DB에 동기화하거나, 저장된 명단을 조회한다.

.DESCRIPTION
기본 동작(동기화): 앱 설정 파일(Slack:BotToken, Slack:ChannelId)로 슬랙에서 채널 구성원 전체를 수집한 뒤
DB(ChannelMembers)에 반영한다. 수집이 하나라도 실패하면 DB는 열리지도 않는다.
-List: DB에 저장된 활성 구성원만 출력한다. 슬랙 요청도 DB 변경도 하지 않으며 토큰도 필요 없다.

DB 쓰기는 손으로 SQL을 쓰지 않고 실제 EF 엔티티를 거쳐야 해서(저장소 규약), 이 스크립트는
같은 폴더의 .NET 도우미(SyncMembers.csproj)를 빌드해 호출하는 얇은 진입점이다.

.PARAMETER Config
앱 설정 파일. 기본값은 <저장소>/src/SelectLunch.Slack/appsettings.Local.json. 상대 경로는 현재 위치 기준.

.PARAMETER Database
DB 파일. 생략하면 설정 파일의 Database:Path(없으면 data/lunch.db)를 쓰며, 그 상대 경로는
"설정 파일이 있는 폴더" 기준이다(현재 위치와 무관). 직접 지정한 상대 경로는 현재 위치 기준.
-List와 함께 지정하면 설정 파일을 읽지 않는다.

.PARAMETER CreateDatabase
동기화에서 DB 파일이 없을 때 새로 만든다. 없으면 파일이 없는 경우 실패한다(오타로 빈 DB가 생기는 것 방지).

.PARAMETER List
저장된 활성 구성원을 출력한다(조회 전용).

.PARAMETER AsJson
-List 출력을 JSON 배열로 낸다. 필드: WorkspaceId, ChannelId, SlackUserId, RealName, DisplayName, LastSyncedAt.
비ASCII는 \uXXXX로 이스케이프되고, 결과가 없으면 [] 이다. 오류 메시지는 stderr로만 나간다.

.PARAMETER WorkspaceId
-List 필터(선택). 워크스페이스 ID(T...).

.PARAMETER ChannelId
-List 필터(선택). 채널 ID(C...).

.PARAMETER Help
사용법을 출력한다. 설정·DB·네트워크·dotnet을 건드리지 않는다.

.EXAMPLE
./tools/sync-members/Sync-ChannelMembers.ps1 -Database data/lunch.db -CreateDatabase

.EXAMPLE
./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db -AsJson | ConvertFrom-Json | ForEach-Object RealName
#>
[CmdletBinding()]
param(
    [string] $Config,
    [string] $Database,
    [switch] $List,
    [switch] $AsJson,
    [string] $WorkspaceId,
    [string] $ChannelId,
    [switch] $CreateDatabase,
    [switch] $Help,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Usage {
    @'
Sync-ChannelMembers.ps1 — 슬랙 채널 사람 명단을 로컬 DB에 저장하고 조회한다 (PowerShell 7+)

사용법:
  ./tools/sync-members/Sync-ChannelMembers.ps1 [-Config <경로>] [-Database <경로>] [-CreateDatabase]
  ./tools/sync-members/Sync-ChannelMembers.ps1 -List [-Database <경로> | -Config <경로>]
                                               [-WorkspaceId <ID>] [-ChannelId <ID>] [-AsJson]
  ./tools/sync-members/Sync-ChannelMembers.ps1 -Help

동기화 (기본)
  설정 파일(Slack:BotToken, Slack:ChannelId)로 채널 구성원 전체를 수집해 DB에 반영한다.
  - 수집이 하나라도 실패하면 DB는 그대로다(열지도, 만들지도 않는다). 실패를 퇴장으로 보지 않는다.
  - 이번 명단에 없는 같은 채널의 기존 구성원만 비활성으로 바꾼다. 행은 지우지 않는다.
  - 봇·앱·Slackbot·삭제 계정은 제외한다. 출력에는 인원 수와 채널만 나오고 이름은 나오지 않는다.

조회 (-List)
  DB의 활성 구성원만 출력한다. 슬랙 요청·DB 변경·토큰이 필요 없다.
  -AsJson 이면 필드 WorkspaceId, ChannelId, SlackUserId, RealName, DisplayName, LastSyncedAt 의
  JSON 배열(결과가 없으면 [])만 표준 출력에 나온다. 오류는 표준 오류로만 나간다.

옵션
  -Config <경로>      앱 설정 파일. 기본: <저장소>/src/SelectLunch.Slack/appsettings.Local.json
  -Database <경로>    DB 파일. 생략하면 설정의 Database:Path (기본 data/lunch.db).
                      설정에서 온 상대 경로는 설정 파일이 있는 폴더 기준, 여기서 직접 준 상대 경로는 현재 위치 기준.
  -CreateDatabase     동기화에서 DB 파일이 없을 때 새로 만든다. 없으면 파일이 없는 경우 실패한다.
  -List               저장된 활성 구성원 조회.
  -AsJson             -List 출력을 JSON으로.
  -WorkspaceId <ID>   -List 필터(선택).
  -ChannelId <ID>     -List 필터(선택).
  -Help               이 도움말. 설정·DB·네트워크·dotnet 을 건드리지 않는다.

종료 코드: 0 성공, 1 실패, 2 인수 오류, 130 취소.
'@
}

function Exit-WithError {
    param(
        [string] $Message,
        [int] $Code = 2
    )

    [Console]::Error.WriteLine($Message)
    exit $Code
}

function Resolve-UserPath {
    # 셸의 현재 위치 기준으로 절대 경로를 만든다. .NET 프로세스의 작업 디렉터리와 다를 수 있어 직접 푼다.
    param([string] $Path)

    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

# ── 도움말: 가장 먼저, 아무것도 건드리지 않고 ─────────────────────────────
$remaining = @($RemainingArguments | Where-Object { $null -ne $_ })
if ($Help -or ($remaining -contains '--help') -or ($remaining -contains '/?')) {
    Write-Usage
    exit 0
}

# ── 인수 검증 ────────────────────────────────────────────────────────────
if ($remaining.Count -gt 0) {
    Exit-WithError '알 수 없는 인수가 있습니다. -Help로 사용법을 확인하세요.'
}

foreach ($name in 'Config', 'Database', 'WorkspaceId', 'ChannelId') {
    if ($PSBoundParameters.ContainsKey($name) -and [string]::IsNullOrWhiteSpace($PSBoundParameters[$name])) {
        Exit-WithError "-$name 값이 비어 있습니다."
    }
}

if ($List) {
    if ($CreateDatabase) {
        Exit-WithError '-CreateDatabase는 동기화에서만 쓸 수 있습니다.'
    }
}
elseif ($AsJson -or $PSBoundParameters.ContainsKey('WorkspaceId') -or $PSBoundParameters.ContainsKey('ChannelId')) {
    Exit-WithError '-AsJson, -WorkspaceId, -ChannelId 는 -List 와 함께만 쓸 수 있습니다.'
}

# ── 도우미 인수 만들기 (토큰은 인수로 넘기지 않는다 — 도우미가 설정 파일에서 직접 읽는다) ──
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..'))
$configPath = if ($PSBoundParameters.ContainsKey('Config')) {
    Resolve-UserPath $Config
}
else {
    Join-Path $repoRoot 'src' 'SelectLunch.Slack' 'appsettings.Local.json'
}

$helperArgs = [System.Collections.Generic.List[string]]::new()
if ($List) {
    $helperArgs.Add('list')
    if ($PSBoundParameters.ContainsKey('Database')) {
        $helperArgs.Add('--db')
        $helperArgs.Add((Resolve-UserPath $Database))
    }
    else {
        $helperArgs.Add('--config')
        $helperArgs.Add($configPath)
    }
    if ($AsJson) { $helperArgs.Add('--json') }
    if ($PSBoundParameters.ContainsKey('WorkspaceId')) {
        $helperArgs.Add('--workspace')
        $helperArgs.Add($WorkspaceId)
    }
    if ($PSBoundParameters.ContainsKey('ChannelId')) {
        $helperArgs.Add('--channel')
        $helperArgs.Add($ChannelId)
    }
}
else {
    $helperArgs.Add('sync')
    $helperArgs.Add('--config')
    $helperArgs.Add($configPath)
    if ($PSBoundParameters.ContainsKey('Database')) {
        $helperArgs.Add('--db')
        $helperArgs.Add((Resolve-UserPath $Database))
    }
    if ($CreateDatabase) { $helperArgs.Add('--create-db') }
}

# ── 도우미 빌드·실행 ─────────────────────────────────────────────────────
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    Exit-WithError 'dotnet SDK(.NET 10)를 찾을 수 없습니다. 설치 후 다시 실행하세요.' 1
}

$project = Join-Path $PSScriptRoot 'SyncMembers.csproj'
$exitCode = 1
$previousNoLogo = $env:DOTNET_NOLOGO
$previousEncoding = [Console]::OutputEncoding

try {
    $env:DOTNET_NOLOGO = '1'
    try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }   # 콘솔이 없으면 무시한다

    # 네이티브 stderr 줄을 오류로 승격하지 않도록 이 구간만 완화한다.
    $ErrorActionPreference = 'Continue'

    # 빌드 출력은 전부 변수로 받는다. 표준 출력에는 도우미의 출력만 남아야 -AsJson 이 깨지지 않는다.
    $buildOutput = & dotnet build $project -c Release --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine('도우미 프로젝트(SyncMembers) 빌드에 실패했습니다.')
        foreach ($line in $buildOutput) { [Console]::Error.WriteLine([string] $line) }
    }
    else {
        $runArgs = @('run', '--project', $project, '-c', 'Release', '--no-build', '--no-launch-profile', '--') + $helperArgs.ToArray()
        & dotnet @runArgs
        $exitCode = $LASTEXITCODE
    }
}
finally {
    $env:DOTNET_NOLOGO = $previousNoLogo
    try { [Console]::OutputEncoding = $previousEncoding } catch { }
}

exit $exitCode
