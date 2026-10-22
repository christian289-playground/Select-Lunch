# mail-search

Outlook(Microsoft 365) **웹 화면을 Chromium + Playwright로 제어해** 메일을 **읽기 전용으로 검색**하는 수동 실행 도구.
점심 봇 쪽에서 "연차·휴가·반차 메일이 온 사람"을 사람이 눈으로 확인할 때 쓴다.
공개 진입점은 PowerShell 7 스크립트 **`Search-OutlookMail.ps1`** 이고, 같은 폴더의 Node 작업 프로세스(`mail-search.mjs`)가
브라우저를 조종한다.

- 사용자가 화면에서 보는 것(DOM)만 읽는다. Graph·Exchange 등 **API 호출, 토큰·쿠키 추출, 내부 API 호출, 탐지 회피(stealth)를 하지 않는다.**
- 메일 삭제·이동·회신·전송 같은 **쓰기 동작을 하지 않는다.** (단, `-IncludeBody`는 메일을 열어서 [읽음 표시가 바뀔 수 있다](#본문-읽기와-읽음-표시).)
- **결과·본문을 파일, DB, 로그, 스크린샷, HAR, trace, 다운로드로 저장하지 않는다.** 결과는 메모리와 stdout에만 있다.
  브라우저가 로그인 상태와 캐시를 쓰는 [전용 프로필](#전용-브라우저-프로필과-디스크에-남는-것)은 별개이며 아래에서 따로 설명한다.
- 결과는 검색어가 일치한 **후보 메일**이다. 휴가 사용·승인 여부를 판단하지 않는다.
- 봇의 투표·알림·스케줄·Slack 명단 도구(`sync-members`)·DB와 연결되어 있지 않다. 수동으로만 돌린다.
  Linux 서버의 앱 배포나 자동 실행에는 영향이 없고 자동으로 쓰이지도 않는다.

솔루션(`.slnx`)과 무관한 스크립트라 `dotnet build`/`dotnet test` 대상이 아니다.

## 요구 사항

- PowerShell 7 이상(`pwsh`), Node.js 22 이상, `npm`
- Playwright(`package.json`에 1.64.1로 고정)와 Chromium — 아래 설치 절차. **스크립트는 자동으로 설치하지 않고**, 없으면 명령을 안내하고 종료 코드 2로 끝난다.
- 검색할 계정으로 Outlook 웹(회사 Microsoft 365 메일함)에 로그인할 수 있어야 한다.

## 설치 (한 번)

### Windows

```powershell
cd tools/mail-search
npm ci
npm run install-browser     # Chromium 내려받기(Playwright가 사용자 캐시 폴더에 둔다)
```

### Linux

```bash
cd tools/mail-search
npm ci
npm run install-browser
sudo npx playwright install-deps chromium    # Chromium이 쓰는 시스템 라이브러리(배포판 패키지 설치, 관리자 권한 필요)
```

`pwsh`(PowerShell 7)와 Node.js 22+는 배포판 방식으로 먼저 설치한다. 최초 로그인에는 **화면을 볼 수 있는 그래픽 세션**이 필요하다
([Linux 서버에서 쓸 때](#linux-서버에서-쓸-때) 참고).

## 처음 한 번: 로그인

```powershell
pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Login
```

- 이 도구 **전용 Chromium 창**이 열린다. 사용자가 그 창에서 **직접** 로그인·MFA·동의를 마친다.
  `-Login`은 비밀번호·MFA를 입력하거나 보안 정책을 우회하지 않는다. 사용자 조작을 기다릴 뿐이다.
- 로그인된 메일함과 계정이 화면에서 확인되면 "로그인 확인 완료"를 출력하고 종료 코드 0으로 끝난다.
  Microsoft가 **"로그인 유지"** 를 물으면 **"예"** 를 선택해야 다음 실행에도 로그인이 유지된다. 진행 안내는 stderr에 실시간으로 나온다.
  `-LoginTimeoutSeconds`(기본 600초) 안에 끝나지 않으면 종료 코드 4다.
- `-Login`은 검색 옵션(`-Query`, `-Name`, `-ReceivedAfter` …)을 함께 받지 않는다. 로그인만 하고 검색하지 않는다.
  **창을 띄우는 것은 `-Login`뿐이다. 검색은 항상 창 없이(headless) 실행한다.**
- 다른 회사는 `-Account someone@other.example -Login`. **계정마다 프로필이 따로**라서 계정별로 한 번씩 로그인한다.
- **검색은 로그인을 시도하지 않는다.** 로그아웃 상태이거나 로그인된 계정이 `-Account`와 다르면 메일을 읽지 않고
  종료 코드 3으로 끝나며 `-Login` 명령을 안내한다. 세션이 만료되면 같은 방식으로 다시 `-Login`을 한다.

### 1Password 확장이 없는 환경

검색에는 1Password 확장이 필요하지 않다. 최초 로그인에서만 기존 Aside의 1Password 확장이나
1Password 앱을 사용해 사용자가 직접 암호를 복사·입력할 수 있다. 스크립트는 암호 보관함·클립보드를
읽거나 암호를 저장하지 않는다. 자동완성이 필요하면 해당 전용 프로필의 확장 설치 가능 여부를 따로 확인한다.
로그인 유지 기간은 회사 정책에 달려 있으며, 다른 PC·다른 OS는 각각 수동 로그인해야 한다.

## 검색

```powershell
# 도움말 (오프라인: Node·브라우저를 건드리지 않는다)
pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Help

# 기본: vincent@snuailab.ai, 휴가·연차·반차·반반차·시간차·대체휴가·경조휴가
pwsh -File tools/mail-search/Search-OutlookMail.ps1 -ReceivedAfter 2026-10-01 -ReceivedBefore 2026-11-01

# 특정 사람 이름이 들어간 메일만
pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Name 홍길동,김철수 -ReceivedAfter 2026-10-01

# 다른 회사: -Account 만 바꾼다
pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Account someone@other.example

# 본문까지, JSON으로
pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Query 연차,휴가 -IncludeBody -AsJson
```

| 옵션 | 기본 | 설명 |
| --- | --- | --- |
| `-Account` | `vincent@snuailab.ai` | 검색할 계정. 로그인된 계정이 다르면 메일을 읽기 전에 중단한다(종료 코드 3). |
| `-Login` | 끔 | 전용 브라우저 창에서 사용자가 직접 로그인한다. 창을 띄우는 유일한 모드이며 검색 옵션과 함께 쓸 수 없다. |
| `-Query` | `휴가`,`연차`,`반차`,`반반차`,`시간차`,`대체휴가`,`경조휴가` | 검색어. 쉼표로 여러 개를 줄 수 있다. 빈 항목은 오류다. |
| `-Name` | 없음 | 사람 이름. 쉼표로 여러 개. **쉼표는 항목 구분자**라 쉼표가 든 이름은 나뉜다. 빈 목록은 오류다(전체 검색으로 바꾸지 않는다). |
| `-ReceivedAfter` | 없음 | 받은 날짜가 이 날짜 **이상**. **`yyyy-MM-dd` 날짜만** 받는다. |
| `-ReceivedBefore` | 없음 | 받은 날짜가 이 날짜 **미만**(그 날짜는 포함하지 않음). 날짜만 받는다. |
| `-MaxResults` | `200` | 읽어 올 최대 건수(1–1000). 이 상한에서 멈추면 "불완전"으로 표시한다. |
| `-IncludeBody` | 끔 | 각 메일을 열어 본문까지 읽는다. 본문을 읽지 못하면 일부만 내지 않고 실패로 끝낸다. |
| `-AsJson` | 끔 | 작업 프로세스의 JSON 객체 전체를 출력한다. 끄면 사람이 읽는 텍스트. |
| `-ProfileDirectory` | 계정별 자동 | 전용 프로필 폴더. [아래](#전용-브라우저-프로필과-디스크에-남는-것) 참고. |
| `-TimeoutSeconds` | `60` | 화면 대기 **한 단계**의 상한(1–3600). 느린 회선·큰 메일함에서 시간 초과(종료 코드 1)가 나면 늘린다. |
| `-LoginTimeoutSeconds` | `600` | `-Login`에서 사용자를 기다리는 시간(1–3600). |
| `-Help` | | 사용법 출력. |


검색어·이름은 평문이다. 여러 검색어는 서로 **OR**, 검색어와 이름은 **AND**로 묶어 Outlook 검색창에 넣는다.
실제 입력한 검색식은 `searchExpression`으로 출력한다. 따옴표·괄호·콜론 등은 단어 구분자로 바꾸며,
검색 범위를 바꾸는 AND/OR/NOT/NEAR 연산자 단어는 인자 오류로 거부한다.

## 출력

기본은 사람이 읽는 텍스트다. 맨 위에 건수·계정·검색식·**불완전 여부**가 나오고, 후보마다 Outlook 화면에 보이던 목록 항목의 텍스트
(보낸이·제목·미리보기·시각 등 화면에 표시된 그대로)가 한 번씩 나온다. `-IncludeBody`면 본문이 이어진다.
메일 내용에서 터미널을 조작하는 제어 문자와 글자 방향 제어 문자는 텍스트 출력에서 지운다.

`-AsJson`의 구조(값은 예시). 비ASCII는 `\uXXXX`로 이스케이프되어 콘솔 코드 페이지와 무관하게 한글이 유지된다.

```json
{
  "mode": "search",
  "account": "user@example.com",
  "searchExpression": "(휴가 OR 연차 OR 반차 …)",
  "result": { "returned": 2, "truncated": false, "incompleteReason": null },
  "messages": [
    { "text": "홍길동\n연차 사용 신청합니다\n10/20", "body": "…(-IncludeBody일 때만)" }
  ]
}
```

`-Login`의 JSON은 `{ "mode": "login", "account": "…", "status": "ready" }`다.

- stdout에는 **결과만**, 진행 안내(브라우저 시작, 로그인 대기 등)는 **stderr**에 실시간으로 나온다. 그래서 `-AsJson` 출력은 항상 JSON 하나다.
- 종료 코드: `0` 정상(결과 0건·불완전 포함), `1` 실행·화면 조작 실패, `2` 인자·의존성 오류(브라우저는 시작되지 않음),
  `3` 로그아웃 상태 또는 계정 불일치(→ `-Login`), `4` `-Login` 제한 시간 초과.
  `-MaxResults abc` 같은 **PowerShell 자체의 매개변수 형식 오류는 종료 코드 1**이다(실행은 시작되지 않는다).
- **불완전한 결과도 종료 코드는 0**이다. 자동화에서 쓴다면 `result.truncated`와 `result.incompleteReason`을 확인한다.
  `truncated`가 true일 때만 `incompleteReason`에 값이 있고, 여러 개면 쉼표로 이어 붙는다:
  `maxResults`(`-MaxResults` 상한에서 멈춤), `serverLimit`(Outlook 웹 검색이 처음 1,000건만 돌려줌), `endNotConfirmed`(목록 끝에서 새 행이 없음을 확인하지 못함),
  `scopeUnverified`(검색 범위가 "모든 폴더"임을 화면에서 확인하지 못함), `dateBoundaryMismatch`(요청 기간 밖의 날짜가 보여 그 행을 뺌).
  결과 없음은 화면에 "없음" 문구가 확인될 때만 0건·완전으로 나온다.
- Ctrl+C 등으로 중단하면 종료 코드 1이다. 계정마다 프로필이 하나라 **같은 프로필을 쓰는 Chromium이 이미 떠 있으면** 종료 코드 1로 끝난다(먼저 그 창을 닫는다).
- 실패한 실행의 stdout은 쓰지 않는다. 오류 안내에는 메일 내용·응답 원문·예외 원문을 싣지 않는다.

## 명단(Slack 채널 구성원)과 연결

[`sync-members`](../sync-members/README.md)로 저장한 명단의 이름을 `-Name`으로 넘기는 예. 조회 실패나 빈 명단을 **전체 메일 검색으로 바꾸지 않는다.**
여러 회사·채널을 한 DB에 저장했다면 `-WorkspaceId`와 `-ChannelId`로 범위를 지정한다.

```powershell
$json = & ./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db -WorkspaceId T01ABC -ChannelId C01XYZ -AsJson
if ($LASTEXITCODE -ne 0) { throw '명단 조회 실패' }
$names = @($json | ConvertFrom-Json | ForEach-Object RealName |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
if ($names.Count -eq 0) { throw '검색할 구성원 이름이 없습니다.' }
if (@($names | Where-Object { $_.Contains(',') }).Count -gt 0) { throw '쉼표가 든 이름은 항목으로 나뉘므로 직접 확인하세요.' }

$output = & ./tools/mail-search/Search-OutlookMail.ps1 -Name $names -ReceivedAfter 2026-10-01 -ReceivedBefore 2026-11-01 -AsJson
if ($LASTEXITCODE -ne 0) { throw "메일 검색 실패(종료 코드 $LASTEXITCODE). 3이면 -Login 이 필요합니다." }
$report = $output | ConvertFrom-Json
if ($report.result.truncated) { Write-Warning "불완전한 결과: $($report.result.incompleteReason)" }
```

이름은 후보 검색용이며 동명이인을 확정하지 않는다. 결과는 변수(메모리)와 출력에만 존재하게 두고, 파일로 리다이렉트하지 않는다.

## 전용 브라우저 프로필과 디스크에 남는 것

작업 프로세스는 이 도구 **전용** Chromium 프로필을 쓴다. 개인 Chrome/Edge/Aside 프로필은 쓰지 않으며, 그런 프로필을 `-ProfileDirectory`로
가리키면 작업 프로세스가 거부한다.

- 기본 위치(저장소 밖): `<상태 폴더>/SelectLunch/mail-search/chromium-<계정 소문자 SHA-256의 앞 16자리>`.
  상태 폴더는 Windows `%LOCALAPPDATA%`, macOS `~/Library/Application Support`, Linux `$XDG_STATE_HOME`(없으면 `~/.local/state`).
- `-ProfileDirectory <경로>`로 바꿀 수 있다. 상대 경로는 **현재 위치 기준 절대 경로로 바꿔** 작업 프로세스에 넘기고, **저장소 안의 경로는 거부**한다.
  `-Login`과 검색은 **같은 프로필**을 써야 로그인 상태가 이어진다. 기본 위치를 쓰면 같은 계정이 같은 프로필이므로 아무것도 줄 필요가 없다.
- 이 폴더에는 Chromium이 **자기 방식으로** 로그인 쿠키·세션과 캐시(브라우저 캐시, 로컬 저장소 등)를 쓴다.
  이것은 로그인 상태를 유지하려는 브라우저의 자체 저장이지, 이 도구의 메일 결과 저장소가 아니다(도구는 결과를 쓰지 않는다).
  다만 Outlook 웹이 브라우저 캐시에 넣는 내용까지 이 도구가 통제하지는 못하므로 **"디스크에 아무것도 남지 않는다"고 약속하지 않는다.**
- 프로필 폴더는 **로그인 정보와 같은 수준의 비밀**이다. 복사·백업·공유·커밋하지 않는다. 폴더를 지우면 로그아웃되어 다시 `-Login`을 해야 한다.
- Playwright가 내려받은 Chromium 실행 파일은 사용자 캐시 폴더(예: `%LOCALAPPDATA%\ms-playwright`, `~/.cache/ms-playwright`)에 있다. 메일과 무관하다.

## 본문 읽기와 읽음 표시

기본 검색은 **목록**만 읽는다. `-IncludeBody`는 각 메일을 **열어서** 읽기 창의 내용을 가져오므로, Outlook의 설정에 따라 그 메일이 **읽음으로 바뀔 수 있다**
(메일함 상태가 바뀌는 유일한 경우다). 쓰기 API는 호출하지 않으며 삭제·이동·회신·전송 동작은 없다. 읽음 표시가 문제라면 `-IncludeBody` 없이 목록만 보고
필요한 메일만 Outlook에서 직접 연다.

## Linux 서버에서 쓸 때

이 도구를 Linux 서버에 두는 것은 **사람이 수동으로 돌리는 용도**다. 앱(`SelectLunch.Slack`) 배포 방식과 자동 실행·스케줄·투표·알림은 이 도구와 무관하며 바뀌지 않는다.

1. [설치](#linux)(Node.js 22+, `npm ci`, `npm run install-browser`, `sudo npx playwright install-deps chromium`)를 한다.
2. **최초 로그인은 사람이 그래픽 세션에서 한다.** 원격 데스크톱, 또는 사람이 화면을 볼 수 있는 VNC 등 `DISPLAY`가 있는 세션이 필요하다.
   `-Login`에서 `DISPLAY`/`WAYLAND_DISPLAY`가 없으면 스크립트가 브라우저를 시작하지 않고 종료 코드 2로 끝난다.
3. 그 세션에서 `pwsh -File tools/mail-search/Search-OutlookMail.ps1 -Account <계정> -Login`을 실행해 로그인·MFA·동의를 직접 마친다.
   `-Login`은 MFA를 자동화하거나 우회하지 않는다.
4. **로그인과 검색은 같은 OS 사용자, 같은 홈 폴더(같은 프로필 경로)** 로 해야 한다. 프로필이 그 사용자의 상태 폴더(`$XDG_STATE_HOME`/`~/.local/state`)에
   저장되기 때문이다. 다른 사용자나 다른 홈으로 실행하면 로그아웃 상태로 보여 종료 코드 3이 나온다. 프로필 폴더를 옮기려면 `-ProfileDirectory`를 양쪽에 똑같이 준다.
5. 검색은 **항상 화면 없이(headless)** 실행하므로 그래픽 세션이 필요 없다. 회사 정책이 자동화된 브라우저 접근을 허용하는지 확인한 뒤에 쓴다.
   로그인 상태가 풀리면 검색으로는 복구할 수 없고(3) 다시 그래픽 세션에서 `-Login`을 해야 한다.

회사 정책(조건부 액세스·장치 준수·자동화 탐지 등)이 막으면 이 도구는 우회하지 않고 실패한다. 그 경우 IT 정책 담당자에게 문의한다.

## 한계

- 화면 기반이다. **Outlook 웹의 화면 구성이 바뀌면 깨질 수 있다.** 그때는 추측해서 빈 결과를 내지 않고 종료 코드 1로 끝난다.
- 화면에 보이는 목록만 읽는다. 목록이 가상 스크롤이라 끝까지 확인하지 못했거나 `-MaxResults`에서 멈추면 `truncated`/`incompleteReason`으로 **불완전**을 알린다.
  기간(`-ReceivedAfter`/`-ReceivedBefore`)이나 이름을 좁혀 다시 검색한다.
- Outlook 검색 인덱스와 단어 분리에 의존한다. 한국어 복합어(`연차신청서`)처럼 검색어가 단어의 일부인 메일은 걸리지 않을 수 있고, 인덱싱 지연도 있다.
  일치해야 할 메일이 빠질 수 있으므로 **0건이 "없다"를 뜻하지 않는다.** 실제 판단 전에 Outlook에서 직접 확인한다.
- 날짜는 날짜 단위(`yyyy-MM-dd`)까지만 지정한다. `-ReceivedBefore` 날짜는 포함하지 않는다.

## 민감 정보 취급

- 출력에는 **메일 제목, 보낸이, 미리보기, 본문이 그대로** 들어간다. 화면 공유·녹화·터미널 로그 수집을 조심한다.
- `> result.json`, `Tee-Object`, CI 로그 같은 **리다이렉트로 사본을 만들지 않는다.** 필요한 사실(누가 언제 쉬는지)만 사람이 확인해 옮긴다.
- 휴가 정보는 이 저장소의 DB·파일·로그 어디에도 저장하지 않는다는 방침을 따른다.

## 문제 해결

| 증상 | 확인 |
| --- | --- |
| "Node.js 22 이상을 찾을 수 없습니다" | Node.js 22 이상을 설치한다. 이 도구는 설치하지 않는다. |
| "Playwright 가 설치되어 있지 않습니다" | `tools/mail-search`에서 `npm ci`, `npm run install-browser`. |
| Chromium 실행 파일이 없다는 오류(종료 코드 2) | `npm run install-browser`. 사용자를 바꿔 실행했다면 그 사용자 계정으로 설치한다. |
| Linux에서 공유 라이브러리 오류 | `sudo npx playwright install-deps chromium`. |
| 종료 코드 3 | `-Login`으로 로그인한다(안내된 명령 그대로, `-ProfileDirectory`를 썼다면 같은 값). 계정이 다르면 `-Account`를 확인한다. |
| 종료 코드 4 | 제한 시간 안에 로그인을 못 마쳤다. 다시 `-Login`(필요하면 `-LoginTimeoutSeconds` 증가). |
| 종료 코드 1이 반복 | 같은 프로필의 Chromium이 이미 떠 있지 않은지 확인하고 `-TimeoutSeconds`를 늘려 본다. 화면 구성 변경이면 작업 프로세스를 고쳐야 한다. |
| Linux에서 `-Login` 브라우저가 안 뜸(종료 코드 2) | `DISPLAY`가 있는 그래픽 세션에서 실행했는지 확인한다. |

## 구조와 테스트

| 파일 | 역할 |
| --- | --- |
| `Search-OutlookMail.ps1` | 공개 진입점. 인수 검증, 의존성 확인, Node 작업 프로세스 실행(요청은 stdin JSON 한 개), 종료 코드·출력 처리 |
| `mail-search.mjs` | 작업 프로세스. 전용 프로필로 Chromium을 열어 로그인 확인·검색. 입출력은 JSON 한 개(stdin 요청, 성공 시 stdout 결과) |
| `package.json`, `package-lock.json` | Node 의존성(Playwright 1.64.1 고정)과 `npm test`, `npm run install-browser` |
| `Search-OutlookMail.Tests.ps1` | 진입점 테스트 |

```powershell
# 진입점: 잘못된 날짜·계정·빈 이름·위험한 프로필 경로 등을 브라우저 시작 전에 거부하는지 검증
pwsh -NoProfile -File tools/mail-search/Search-OutlookMail.Tests.ps1

# 작업 프로세스(Node 테스트)
cd tools/mail-search; npm test
```

진입점 테스트는 실제 `pwsh -File`을 스텁 작업 프로세스에 붙여 종료 코드와 입출력만 확인한다. **실제 Outlook 화면 제어와 로그인 흐름은 자동 테스트하지 않으므로**
새 계정이나 새 회사에서는 `-Login` 후 첫 검색을 한 번 수동으로 확인한다.
