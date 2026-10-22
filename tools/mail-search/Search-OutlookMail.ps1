#Requires -Version 7.0
<#
.SYNOPSIS
Outlook 웹 화면(Chromium + Playwright)에서 메일 후보를 읽기 전용으로 검색하는 수동 실행 도구.

.DESCRIPTION
같은 폴더의 Node 작업 프로세스(mail-search.mjs)에 요청을 표준 입력(JSON)으로 넘겨 이 도구 전용 브라우저
프로필로 Outlook 웹을 연다. 로그아웃 상태면 검색하지 않고 종료 코드 3으로 끝나며, 로그인은 -Login 으로
사용자가 직접 한다. 결과는 stdout 으로만 나가고 파일로 저장하지 않는다.

사용법은 -Help(오프라인), 설정과 한계는 같은 폴더의 README.md 를 본다.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Account = 'vincent@snuailab.ai',
    [string[]]$Query = @('휴가', '연차', '반차', '반반차', '시간차', '대체휴가', '경조휴가'),
    [string[]]$Name,
    [string]$ReceivedAfter,
    [string]$ReceivedBefore,
    [int]$MaxResults = 200,
    [switch]$IncludeBody,
    [switch]$AsJson,
    [switch]$Login,
    [string]$ProfileDirectory,
    [int]$TimeoutSeconds = 60,
    [int]$LoginTimeoutSeconds = 600,
    [switch]$Help,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Usage {
    @'
Search-OutlookMail.ps1 — Outlook 웹(Chromium 화면 제어) 메일 후보 검색. 읽기 전용 수동 도구 (PowerShell 7+, Node.js 22+)

사용법
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Login [-Account <메일주소>] [-ProfileDirectory <경로>]
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 [검색 옵션] [-AsJson]
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Help

옵션
  -Account <메일주소>        검색할 계정. 기본 vincent@snuailab.ai. 다른 회사는 이 값만 바꾼다.
  -Login                     전용 브라우저 창을 열어 사용자가 직접 로그인(MFA·동의 포함)한다. 로그인만 하고 검색은 하지 않는다.
                             사람이 입력해야 하므로 이 모드만 창을 띄운다. 검색 옵션과 함께 쓸 수 없다.
  -Query <단어,...>          검색어. 하나라도 일치하면 후보. 쉼표로 여러 개.
                             기본 휴가,연차,반차,반반차,시간차,대체휴가,경조휴가
  -Name <이름,...>           사람 이름. 검색어와 AND, 이름끼리는 OR. 쉼표는 항목 구분자다.
  -ReceivedAfter <yyyy-MM-dd>   이 날짜 이상(포함). 날짜만 받는다. 시각은 지정할 수 없다.
  -ReceivedBefore <yyyy-MM-dd>  이 날짜 미만(제외). 날짜만 받는다.
  -MaxResults <1-1000>       읽어 올 최대 건수. 기본 200. 넘기면 결과에 "불완전"이 표시된다.
  -IncludeBody               각 메일을 열어 본문까지 읽는다(Outlook 에서 읽음 표시가 바뀔 수 있다).
  -AsJson                    작업 프로세스의 JSON 객체를 그대로 출력한다. 기본은 사람이 읽는 텍스트.
  -ProfileDirectory <경로>   전용 브라우저 프로필 폴더. 기본은 계정별로 저장소 밖에 자동 생성. 저장소 안은 거부한다.
  -TimeoutSeconds <초>       화면 조작 대기 시간. 기본 60 (1-3600).
  -LoginTimeoutSeconds <초>  -Login 에서 사용자를 기다리는 시간. 기본 600 (1-3600).
  -Help                      이 도움말. 브라우저·Node·네트워크를 건드리지 않는다.

예시
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Login
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 -ReceivedAfter 2026-10-01 -ReceivedBefore 2026-11-01
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Name 홍길동,김철수 -IncludeBody -AsJson
  pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Account someone@other.example -Login

종료 코드
  0  정상(결과 0건·불완전 포함. 잘린 결과는 출력에 "불완전"으로 표시된다)
  1  실행·화면 조작 실패(PowerShell 의 매개변수 형식 오류도 1)
  2  인자·의존성 오류(Node.js·Playwright·Chromium 없음 포함). 브라우저는 시작되지 않았다.
  3  로그아웃 상태이거나 로그인 계정이 -Account 와 다름. -Login 으로 로그인한다.
  4  -Login 에서 제한 시간 안에 로그인이 끝나지 않음

주의
  결과(메일 제목·보낸이·미리보기·본문)는 stdout 으로만 나간다. 이 도구는 결과를 파일에 쓰지 않는다.
  리다이렉트·공유 로그·CI 출력으로 흘리지 말 것. 결과는 후보일 뿐 휴가 사용·승인 여부를 판단하지 않는다.
  Node.js·Playwright·Chromium 은 자동으로 설치하지 않는다. 설치는 README.md 를 본다.
'@
}

function Exit-WithError {
    param([string]$Message, [int]$Code = 2)
    [Console]::Error.WriteLine($Message)
    exit $Code
}

# pwsh -File 로 실행하면 -Query 연차,휴가 가 쉼표가 든 문자열 하나로 들어온다. 쉼표는 항목 구분자다.
# 빈 항목은 조용히 버리지 않는다 — 버리면 검색 범위가 말없이 달라진다.
function ConvertTo-TermList {
    param([string[]]$Values, [string]$Label)
    $terms = @(foreach ($value in $Values) {
        foreach ($part in ([string]$value -split ',')) {
            $term = $part.Trim().Normalize([System.Text.NormalizationForm]::FormC)
            if ($term.Length -eq 0) { Exit-WithError "$Label 에 빈 항목이 있습니다. 쉼표(,)로 항목을 나누며 항목은 비어 있을 수 없습니다." }
            $term
        }
    })
    , $terms
}

function ConvertTo-DateOnly {
    param([string]$Value, [string]$Label)
    $parsed = [datetime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::None
    if (-not [datetime]::TryParseExact($Value, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture, $styles, [ref]$parsed)) {
        Exit-WithError "$Label 는 yyyy-MM-dd 형식의 날짜만 받습니다(예: 2026-10-01). 시각은 지정할 수 없습니다."
    }
    $parsed
}

function Get-Property {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

# 메일 내용은 신뢰할 수 없는 입력이다. 터미널을 조작하는 제어 문자와 글자 방향을 뒤집는 문자를 지운다.
function Remove-ControlCharacter {
    param([AllowNull()][string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    [regex]::Replace($Text, '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]', '')
}

function ConvertTo-SingleLine {
    param([AllowNull()]$Text)
    (Remove-ControlCharacter ([regex]::Replace([string]$Text, '\s+', ' '))).Trim()
}

# ── 도움말: 가장 먼저, 아무것도 건드리지 않고 ─────────────────────────────
$remaining = @($RemainingArguments | Where-Object { $null -ne $_ })
if ($Help -or ($remaining -contains '--help') -or ($remaining -contains '/?')) {
    Write-Usage
    exit 0
}

# ── 인수 검증: 브라우저·Node 를 건드리기 전에 끝낸다 ──────────────────────
if ($remaining.Count -gt 0) {
    Exit-WithError "알 수 없는 인수가 있습니다: $($remaining -join ' '). -Help 로 사용법을 확인하세요."
}

$accountName = $Account.Trim()
if ($accountName -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
    Exit-WithError '-Account 는 name@company.com 형식의 메일 주소여야 합니다.'
}
if ($MaxResults -lt 1 -or $MaxResults -gt 1000) { Exit-WithError '-MaxResults 는 1–1000 사이여야 합니다.' }
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 3600) { Exit-WithError '-TimeoutSeconds 는 1–3600 사이여야 합니다.' }
if ($LoginTimeoutSeconds -lt 1 -or $LoginTimeoutSeconds -gt 3600) { Exit-WithError '-LoginTimeoutSeconds 는 1–3600 사이여야 합니다.' }

# 검색은 항상 창 없이(headless) 실행한다. 창은 사용자가 직접 로그인하는 -Login 에서만 띄운다.
if ($Login) {
    foreach ($option in 'Query', 'Name', 'ReceivedAfter', 'ReceivedBefore', 'MaxResults', 'IncludeBody') {
        if ($PSBoundParameters.ContainsKey($option)) {
            Exit-WithError "-Login 은 로그인만 하고 검색하지 않습니다. -$option 은 함께 쓸 수 없습니다."
        }
    }
}

$queryTerms = ConvertTo-TermList -Values $Query -Label '-Query'
if ($queryTerms.Count -eq 0) { Exit-WithError '-Query 가 비어 있습니다.' }
$nameTerms = @()
if ($PSBoundParameters.ContainsKey('Name')) {
    $nameTerms = ConvertTo-TermList -Values $Name -Label '-Name'
    if ($nameTerms.Count -eq 0) { Exit-WithError '-Name 이 비어 있습니다. 빈 이름 목록을 전체 검색으로 바꾸지 않습니다.' }
}

$after = $null
$before = $null
if ($PSBoundParameters.ContainsKey('ReceivedAfter')) { $after = ConvertTo-DateOnly -Value $ReceivedAfter -Label '-ReceivedAfter' }
if ($PSBoundParameters.ContainsKey('ReceivedBefore')) { $before = ConvertTo-DateOnly -Value $ReceivedBefore -Label '-ReceivedBefore' }
if ($null -ne $after -and $null -ne $before -and $after -ge $before) {
    Exit-WithError '-ReceivedAfter 는 -ReceivedBefore 보다 앞서야 합니다(-ReceivedBefore 날짜는 포함하지 않습니다).'
}

$profilePath = $null
if ($PSBoundParameters.ContainsKey('ProfileDirectory')) {
    if ([string]::IsNullOrWhiteSpace($ProfileDirectory)) { Exit-WithError '-ProfileDirectory 값이 비어 있습니다.' }
    try { $profilePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProfileDirectory) }
    catch { Exit-WithError '-ProfileDirectory 경로를 해석할 수 없습니다.' }
    # 프로필에는 로그인 쿠키가 들어간다. 커밋될 수 있는 저장소 안에는 두지 않는다.
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..'))
    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $profilePath)
    $outside = [System.IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('..' + [System.IO.Path]::DirectorySeparatorChar)
    if (-not $outside) { Exit-WithError '-ProfileDirectory 는 저장소 밖이어야 합니다. 프로필에는 로그인 정보가 저장됩니다.' }
}

if ($Login -and $IsLinux -and -not $env:DISPLAY -and -not $env:WAYLAND_DISPLAY) {
    Exit-WithError '로그인 창을 열 그래픽 세션(DISPLAY)이 없습니다. 원격 데스크톱 등 사람이 화면을 볼 수 있는 세션에서 -Login 을 실행하세요. 검색은 창 없이 실행됩니다.'
}

# ── 의존성: 없으면 안내만 하고 자동으로 설치하지 않는다 ───────────────────
$worker = Join-Path $PSScriptRoot 'mail-search.mjs'
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) {
    Exit-WithError "작업 프로세스 파일을 찾을 수 없습니다: $worker"
}
$node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $node) {
    Exit-WithError 'Node.js 22 이상을 찾을 수 없습니다. 설치한 뒤 다시 실행하세요(이 도구는 Node.js 를 자동으로 설치하지 않습니다).'
}
$nodeVersion = & $node.Source --version 2>$null | Select-Object -First 1
if (-not ($nodeVersion -match '^v(\d+)\.') -or [int]$Matches[1] -lt 22) {
    Exit-WithError "Node.js 22 이상이 필요합니다(현재: $nodeVersion). 업그레이드한 뒤 다시 실행하세요."
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'node_modules' 'playwright' 'package.json') -PathType Leaf)) {
    Exit-WithError @"
Playwright 가 설치되어 있지 않습니다. 아래를 실행한 뒤 다시 시도하세요(이 도구는 자동으로 설치하지 않습니다).

    cd '$PSScriptRoot'
    npm ci
    npm run install-browser

Linux 에서는 Chromium 이 쓰는 시스템 라이브러리도 필요할 수 있습니다. README.md 를 보세요.
"@
}

# ── 요청: 표준 입력 JSON 한 개. 인자·임시 파일을 쓰지 않아 셸 인용 문제와 파일 흔적이 없다 ──
$request = [ordered]@{
    account             = $accountName
    query               = $queryTerms
    names               = $nameTerms
    receivedAfter       = if ($null -ne $after) { $after.ToString('yyyy-MM-dd', [cultureinfo]::InvariantCulture) } else { $null }
    receivedBefore      = if ($null -ne $before) { $before.ToString('yyyy-MM-dd', [cultureinfo]::InvariantCulture) } else { $null }
    maxResults          = $MaxResults
    includeBody         = [bool]$IncludeBody
    login               = [bool]$Login
    headless            = -not [bool]$Login
    profileDirectory    = $profilePath
    timeoutSeconds      = $TimeoutSeconds
    loginTimeoutSeconds = $LoginTimeoutSeconds
} | ConvertTo-Json -Compress -Depth 3

function Invoke-Worker {
    param([string]$NodePath, [string]$WorkerPath, [string]$Request)
    $utf8 = [System.Text.UTF8Encoding]::new($false)   # BOM 없는 UTF-8: BOM 이 있으면 JSON.parse 가 실패한다
    $info = [System.Diagnostics.ProcessStartInfo]::new($NodePath)
    $info.ArgumentList.Add($WorkerPath)
    $info.WorkingDirectory = $PSScriptRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.StandardInputEncoding = $utf8
    $info.StandardOutputEncoding = $utf8
    # stderr 는 가로채지 않는다. 작업 프로세스가 호출자의 stderr 에 직접 쓰므로 로그인 안내가 지연 없이 보인다.
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        try {
            $process.StandardInput.Write($Request)
            $process.StandardInput.Close()
        }
        catch [System.IO.IOException] { }   # 입력을 읽기 전에 끝난 경우: 종료 코드로 판단한다
        $process.WaitForExit()
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.GetAwaiter().GetResult() }
    }
    finally {
        # Ctrl+C 등으로 중단된 경우 작업 프로세스가 브라우저를 정리할 시간을 잠깐 주고, 남아 있으면 끝낸다.
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
        $process.Dispose()
    }
}

# 작업 프로세스 응답이 약속한 모양인지 본다. 문제가 있으면 고정 문구를 돌려주고, 응답 원문은 옮기지 않는다.
function Get-ResponseProblem {
    param($Payload, [bool]$LoginMode, [bool]$BodyRequired)
    if ($Payload -isnot [pscustomobject]) { return '응답이 JSON 객체가 아닙니다.' }
    if ($LoginMode) {
        if ((Get-Property $Payload 'mode') -ne 'login' -or (Get-Property $Payload 'status') -ne 'ready') { return '로그인 완료 응답이 아닙니다.' }
        return $null
    }
    if ((Get-Property $Payload 'mode') -ne 'search') { return '검색 응답이 아닙니다.' }
    if ((Get-Property $Payload 'result') -isnot [pscustomobject] -or $null -eq $Payload.PSObject.Properties['messages'] -or $Payload.messages -isnot [array]) {
        return 'result 또는 messages 가 없습니다.'
    }
    $bodyless = 0
    foreach ($message in $Payload.messages) {
        if ($message -isnot [pscustomobject] -or $null -eq (Get-Property $message 'text')) { return '메시지 항목 형식이 올바르지 않습니다.' }
        if ($BodyRequired -and $null -eq (Get-Property $message 'body')) { $bodyless++ }
    }
    if ($bodyless -gt 0) { return "-IncludeBody 인데 본문을 읽지 못한 메일이 ${bodyless}건 있어 결과를 표시하지 않습니다." }
    return $null
}

function Format-SearchText {
    param([Parameter(Mandatory)]$Payload)
    $result = $Payload.result
    $messages = @($Payload.messages)
    $reason = ConvertTo-SingleLine (Get-Property $result 'incompleteReason')
    $incomplete = ((Get-Property $result 'truncated') -eq $true) -or $reason.Length -gt 0

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Outlook 메일 후보 $($messages.Count)건 (계정: $(ConvertTo-SingleLine (Get-Property $Payload 'account')))")
    $expression = ConvertTo-SingleLine (Get-Property $Payload 'searchExpression')
    if ($expression.Length -gt 0) { $lines.Add("검색식: $expression") }
    if ($incomplete) {
        $detail = if ($reason.Length -gt 0) { " (사유: $reason)" } else { '' }
        $lines.Add("※ 불완전한 결과입니다${detail}. 화면에서 끝까지 확인하지 못했을 수 있으니 기간·이름을 좁혀 다시 검색하세요.")
    }
    $lines.Add("※ 화면에 보인 메일 목록에서 읽은 후보일 뿐 휴가 사용·승인 여부를 판단하지 않습니다. 0건이 '없음'을 뜻하지는 않습니다.")

    $index = 0
    foreach ($message in $messages) {
        $index++
        $lines.Add('')
        $lines.Add("[${index}]")
        $textLines = @((Remove-ControlCharacter ([string]$message.text)) -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 })
        if ($textLines.Count -eq 0) { $textLines = @('(내용 없음)') }
        foreach ($textLine in $textLines) { $lines.Add('    ' + $textLine) }
        $body = Get-Property $message 'body'
        if ($null -ne $body) {
            $lines.Add('    본문:')
            foreach ($bodyLine in ((Remove-ControlCharacter ([string]$body)) -split '\r?\n')) { $lines.Add('      ' + $bodyLine) }
        }
    }
    $lines -join [Environment]::NewLine
}

# JSON 의 비ASCII 는 문자열 안에만 있으므로 \uXXXX 로 바꿔도 같은 값이다. 콘솔 코드 페이지와 무관하게 한글이 유지된다.
function ConvertTo-AsciiJson {
    param([string]$Json)
    [regex]::Replace($Json.Trim(), '[^\x00-\x7F]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
}

function Get-FailureHint {
    param([int]$ExitCode)
    switch ($ExitCode) {
        1 { '화면 조작 중 오류가 있었습니다(시간 초과·화면 구성 변경 등). 위 안내를 확인하고, 반복되면 -TimeoutSeconds 를 늘리거나 README.md 를 보세요.' }
        2 { '인자 또는 의존성 오류입니다. Playwright/Chromium 이 없다면 tools/mail-search 에서 npm ci, npm run install-browser 를 실행하세요(README.md 참고).' }
        3 {
            if ($Login) { '로그인된 계정이 -Account 와 다릅니다. 이 프로필에서 -Account 계정으로 로그인하세요.' }
            else {
                $command = "pwsh -File '$PSCommandPath' -Account $accountName"
                if ($null -ne $profilePath) { $command += " -ProfileDirectory '$($profilePath.Replace("'", "''"))'" }
                "로그인이 필요하거나 계정이 다릅니다. 메일은 검색하지 않았습니다. 먼저 로그인하세요: $command -Login"
            }
        }
        4 { "수동 로그인 제한 시간(${LoginTimeoutSeconds}초)이 지났습니다. -Login 을 다시 실행하세요(-LoginTimeoutSeconds 로 늘릴 수 있습니다)." }
        default { "작업 프로세스가 예상하지 못한 종료 코드($ExitCode)로 끝났습니다." }
    }
}

# ── 실행 ─────────────────────────────────────────────────────────────────
$exitCode = 1
$previousEncoding = [Console]::OutputEncoding
try {
    try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }   # 콘솔이 없으면 무시한다

    $run = Invoke-Worker -NodePath $node.Source -WorkerPath $worker -Request $request
    if ($run.ExitCode -ne 0) {
        # 실패한 실행의 stdout 은 쓰지 않는다. 진단은 작업 프로세스가 이미 stderr 로 냈다.
        [Console]::Error.WriteLine((Get-FailureHint -ExitCode $run.ExitCode))
        $exitCode = if ($run.ExitCode -in 1, 2, 3, 4) { $run.ExitCode } else { 1 }
    }
    else {
        try {
            $payload = try { ConvertFrom-Json -InputObject $run.Output -ErrorAction Stop } catch { $null }
            $problem = Get-ResponseProblem -Payload $payload -LoginMode ([bool]$Login) -BodyRequired ([bool]$IncludeBody)
            if ($null -ne $problem) {
                [Console]::Error.WriteLine("작업 프로세스의 응답을 사용할 수 없습니다: $problem 메일 내용 보호를 위해 응답 원문은 출력하지 않습니다.")
            }
            else {
                if ($AsJson) { ConvertTo-AsciiJson $run.Output }
                elseif ($Login) {
                    "로그인 확인 완료: $(ConvertTo-SingleLine (Get-Property $payload 'account')). 전용 브라우저 프로필에 로그인 상태가 준비되었습니다. 이제 -Login 없이 검색할 수 있습니다."
                }
                else { Format-SearchText -Payload $payload }
                $exitCode = 0
            }
        }
        catch {
            [Console]::Error.WriteLine("작업 프로세스의 응답을 처리하지 못했습니다($($_.Exception.GetType().Name)). 메일 내용 보호를 위해 오류 원문과 응답은 출력하지 않습니다.")
        }
    }
}
finally {
    try { [Console]::OutputEncoding = $previousEncoding } catch { }
}

exit $exitCode
