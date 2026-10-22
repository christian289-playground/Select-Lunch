#Requires -Version 7.0
# 브라우저나 대체 작업 프로세스를 띄우지 않고 실제 CLI의 위험 입력 거부를 확인한다.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Search-OutlookMail.ps1'
$pwsh = Join-Path $PSHOME ($IsWindows ? 'pwsh.exe' : 'pwsh')
$cases = @(
    @('-Account', 'not-an-email'),
    @('-MaxResults', '0'),
    @('-MaxResults', '1001'),
    @('-TimeoutSeconds', '0'),
    @('-LoginTimeoutSeconds', '3601'),
    @('-ReceivedAfter', '2026/10/01'),
    @('-ReceivedAfter', '2026-02-30'),
    @('-ReceivedBefore', '2026-10-01T09:00:00'),
    @('-ReceivedAfter', '2026-10-05', '-ReceivedBefore', '2026-10-05'),
    @('-ReceivedAfter', '2026-11-01', '-ReceivedBefore', '2026-10-01'),
    @('-Query', ','),
    @('-Name', ','),
    @('-Login', '-Headless'),
    @('-Login', '-Query', '연차'),
    @('-Login', '-IncludeBody'),
    @('-UnknownOption'),
    @('-ProfileDirectory', (Join-Path $PSScriptRoot 'unsafe-profile'))
)
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($arguments in $cases) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $scriptPath) + $arguments) {
        $info.ArgumentList.Add($argument)
    }
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            $failures.Add("입력 거부가 브라우저 실행 전에 끝나지 않았습니다: $($arguments -join ' ')")
            continue
        }
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult()
        [void]$stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 2 -or $text.Length -ne 0) {
            $failures.Add("입력 거부 실패: $($arguments -join ' ') (종료 코드 $($process.ExitCode), stdout 문자 수 $($text.Length))")
        }
    }
    finally { $process.Dispose() }
}
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { [Console]::Error.WriteLine($failure) }
    exit 1
}
Write-Output "CLI 경계 검증 $($cases.Count)건 통과"
