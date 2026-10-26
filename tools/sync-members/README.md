# sync-members

슬랙 **채널의 사람 명단**을 로컬 DB(`ChannelMembers` 테이블)에 저장하고 조회하는 수동 실행 도구.
공개 진입점은 PowerShell 7 스크립트 **`Sync-ChannelMembers.ps1`** 이다.

저장하는 것은 식별(워크스페이스·채널·사용자 ID), **이름 키워드**(`RealName`, `DisplayName`),
채널 소속 여부와 동기화 시각이다. 휴가·부재·메일 정보는 저장하지 않는다.
이 스크립트는 수동 실행한다. 봇의 `LeaveDisplay` 연동도 같은 공통 DB 저장 계층으로 명단을 갱신한다.

```powershell
# 동기화 (설정 파일의 Slack:BotToken / Slack:ChannelId 사용)
./tools/sync-members/Sync-ChannelMembers.ps1
./tools/sync-members/Sync-ChannelMembers.ps1 -Database data/lunch.db -CreateDatabase   # DB 파일이 아직 없을 때

# 조회 전용: 슬랙 요청도 DB 변경도 토큰도 필요 없다
./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db
./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db -AsJson
./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db -WorkspaceId T01ABC -ChannelId C01XYZ -AsJson

# 도움말: 설정·DB·네트워크·dotnet 어느 것도 건드리지 않는다
./tools/sync-members/Sync-ChannelMembers.ps1 -Help
```

## 왜 `.ps1` 뒤에 .NET 도우미가 있나

저장소 규약상 **DB에 직접 SQL을 쓰지 않고** 실제 EF 엔티티를 거쳐야 한다
(`SelectLunch.Shared`의 `ChannelMember`, `LunchDbContext`, 마이그레이션). PowerShell에서 EF를 직접 부르는
방법은 어셈블리 로딩이 취약하므로, 같은 폴더의 작은 콘솔 프로젝트 `SyncMembers.csproj`가 그 일을 맡고
스크립트가 빌드해서 호출한다. 사용자가 `dotnet` 명령을 직접 칠 일은 없다.

- 스크립트는 `dotnet build -c Release`(출력은 변수로 받는다) → `dotnet run --no-build` 순으로 부른다.
  그래서 `-AsJson` 의 표준 출력에는 도우미의 JSON만 나오고, 빌드 메시지가 섞이지 않는다.
- 토큰은 **명령줄 인수로 넘기지 않는다.** 도우미가 설정 파일에서 직접 읽는다.
- `-AsJson` 은 비ASCII를 `\uXXXX`로 이스케이프한다. 콘솔 코드 페이지와 무관하게 한글이 깨지지 않는다.

## 설정과 경로 규칙

설정은 앱과 같은 파일에서 읽는다. 읽는 키는 세 개뿐이다 (환경 변수는 읽지 않는다).

| 키 | 용도 | 필요한 때 |
|---|---|---|
| `Slack:BotToken` | `xoxb-…` | 동기화 |
| `Slack:ChannelId` | 동기화할 채널 | 동기화 |
| `Database:Path` | DB 파일 | `-Database`를 안 줬을 때 |

- **`-Config`** 기본값: `<저장소>/src/SelectLunch.Slack/appsettings.Local.json`. 직접 준 상대 경로는 **현재 위치** 기준.
  같은 폴더에 `appsettings.json`이 있으면 바탕으로 깔고 지정한 파일이 덮는다(앱이 두 파일을 합치는 순서와 같다).
- **`-Database`** 를 직접 주면 그 경로를 쓴다. 상대 경로는 **현재 위치** 기준이다.
- 안 주면 `Database:Path`(없으면 `data/lunch.db`)를 쓰고, 그 상대 경로는 **설정 파일이 있는 폴더** 기준이다.
  앱의 작업 디렉터리(서버는 `/opt/select-lunch`, 개발은 프로젝트 폴더)가 곧 설정 파일 폴더라서 앱이 여는 DB와
  같은 파일이 된다. 기본 설정이면 `src/SelectLunch.Slack/data/lunch.db`.
- 실제로 쓴 DB 경로는 동기화 출력의 `DB:` 줄에 항상 찍힌다.
- `-List`는 `-Database`가 있으면 **설정 파일을 열지 않는다.** 없을 때만 설정에서 `Database:Path`만 읽고,
  토큰과 채널은 보지 않는다.
- DB 파일이 없으면 동기화는 **실패한다.** 새로 만들려면 `-CreateDatabase`를 명시해야 한다(경로 오타로
  빈 DB가 조용히 생기는 것을 막는다). `-List`는 파일을 절대 만들지 않는다.
- 동기화는 DB를 연 뒤 `MigrateAsync()`를 먼저 부른다(seed-restaurants와 같다). 앱이 DB를 열고 있어도
  SQLite가 쓰기를 직렬화하지만, 가능하면 앱이 한가한 때 돌린다.

## 슬랙 호출과 필요한 스코프

SDK 없이 `HttpClient`로 문서화된 Web API만 부른다. 토큰은 `Authorization: Bearer` 헤더로만 나간다.

| 순서 | 메서드 | 목적 |
|---|---|---|
| 1 | `auth.test` (POST) | 워크스페이스 ID(`team_id`) 확인 — 명단의 `WorkspaceId`가 된다 |
| 2 | `conversations.info` | 설정한 채널이 실제로 그 채널인지 확인하고 이름을 출력 |
| 3 | `conversations.members` | 구성원 ID. `limit=200`, `next_cursor` 따라 전 페이지 |
| 4 | `users.info` | 구성원마다 프로필(실명·표시 이름)과 봇/앱/삭제 여부 |

스코프는 앱이 이미 쓰는 `channels:read` / `groups:read` / `users:read` 뿐이다. 새 스코프도 재설치도 필요 없다.
봇이 채널에 초대돼 있어야 한다.

- 제외: `USLACKBOT`(조회 없이 제외), `is_bot`, `is_app_user`, `deleted`. 제외 인원 수는 출력에 나온다.
- 이름: `profile.real_name` → 비었으면 `user.real_name` → 그것도 비면 `user.name`. 표시 이름은 `profile.display_name`.
  앞뒤 공백을 떼고 그대로 저장한다. 이름은 **검색·대조·표시 값이지 정체성이 아니다.** 동명이인은 사용자 ID로 갈린다.

## 실패하면 DB는 그대로다

`SyncMembersApp`의 순서가 안전성의 핵심이다.

1. 설정 검증 → DB 파일 확인 (여기까지는 네트워크도 DB 접근도 없다)
2. **슬랙 수집 전체** (`auth.test` → `conversations.info` → 모든 페이지 → 모든 `users.info`)
3. 성공했을 때만 DB를 열고 마이그레이션한 뒤 **한 트랜잭션**으로 반영

2단계에서 인증·권한·한도 초과·네트워크·프로필 조회 중 무엇이 실패해도 **전체를 포기**한다.
일부만 모은 명단으로 반영하면 빠진 사람이 퇴장으로 오해되기 때문이다. 이 경우 DB는 열리지도, 만들어지지도,
마이그레이션되지도 않는다. 재시도는 하지 않는다 — `429`는 `Retry-After`를 알리고 끝낸다.
구성원 목록이 비어 있는 응답(봇조차 없다)도 믿지 않고 중단한다.

3단계의 반영 규칙(`ChannelRosterStore.ApplyChannelRosterAsync`):

- 이번 명단의 사람: 없으면 추가, 있으면 이름 갱신(비활성이었으면 다시 활성화), `LastSyncedAt` 갱신.
- 이번 명단에 **없는** 사람: **같은 워크스페이스·같은 채널**의 활성 행만 `IsActive=false`로 바꾼다.
  행은 지우지 않는다(이력 보존). 이미 비활성인 행은 시각도 건드리지 않는다.
- 다른 채널·다른 워크스페이스의 행은 읽지도 않는다. 키는 `(WorkspaceId, ChannelId, SlackUserId)`다.
- 같은 명단으로 다시 돌려도 행도 값도 늘거나 바뀌지 않는다(확인 시각 `LastSyncedAt`만 올라간다).
- 저장 도중 실패하거나 취소(Ctrl+C)되면 전부 롤백된다.

## 출력과 비밀

- 동기화 출력은 워크스페이스·채널·DB 경로·인원 수 집계뿐이다. **구성원 이름은 출력하지 않는다.**
- 오류 메시지는 고정 문구와 슬랙이 준 짧은 오류 코드(`invalid_auth` 같은 `[a-z0-9_]` 형식일 때만)만 담는다.
  토큰, 응답 본문, 내부 예외 메시지는 출력하지 않는다. 예상 못 한 예외는 형식 이름만 낸다.
- 종료 코드: `0` 성공, `1` 실패, `2` 인수 오류, `130` 취소.

## `-List` 출력과 다른 도구에서 쓰기

`-List`는 **활성 구성원만** 돌려주고, 필터(`-WorkspaceId`, `-ChannelId`)를 안 주면 DB의 전체 활성 행이다.
정렬은 워크스페이스 → 채널 → 실명 → 표시 이름 → 사용자 ID의 서수 비교라 결정적이다.

`-AsJson`: 필드 `WorkspaceId`, `ChannelId`, `SlackUserId`, `RealName`, `DisplayName`, `LastSyncedAt`(ISO 8601)의
배열. 결과가 없으면 `[]` 이다. 실패하면 표준 출력은 비고 종료 코드가 0이 아니다.

```powershell
# 저장된 실명을 다른 스크립트의 입력으로 (같은 사람이 두 채널에 있으면 중복되니 필요하면 Sort-Object -Unique)
./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db -AsJson |
    ConvertFrom-Json | ForEach-Object RealName
```

저장된 명단을 이름 검색에 사용하려면 같은 PowerShell 세션에서 다음처럼 연결한다.
여러 회사·채널을 한 DB에 저장했다면 반드시 `-WorkspaceId`와 `-ChannelId`로 범위를 지정한다.

```powershell
$json = & ./tools/sync-members/Sync-ChannelMembers.ps1 -List -Database data/lunch.db -AsJson
if ($LASTEXITCODE -ne 0) { throw '명단 조회 실패' }
$members = $json | ConvertFrom-Json
$names = @($members | ForEach-Object RealName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
if ($names.Count -eq 0) { throw '검색할 구성원 이름이 없습니다.' }
& ./tools/mail-search/Search-OutlookMail.ps1 -Name $names -ReceivedAfter 2026-10-01
```

명단 조회 실패나 빈 명단을 전체 메일 검색으로 바꾸지 않는다. 이름은 후보 검색용이며 동명이인을 확정하지 않는다.
메일 결과와 휴가 정보는 변수의 일시 메모리와 출력에만 존재한다.

.NET 코드에서는 `LunchDbContext.GetActiveChannelMembersAsync(workspaceId?, channelId?, ct)`
(`SelectLunch.Shared.Members.ChannelRosterStore`)를 쓰면 같은 결과를 EF로 얻는다. 조회 전용이다.

## 구조

| 파일 | 역할 |
|---|---|
| `Sync-ChannelMembers.ps1` | 공개 진입점. 인수 검증, 경로 해석, 빌드·실행. `-Help`는 여기서 끝난다 |
| `Program.cs` | UTF-8 출력, Ctrl+C → 취소 연결 |
| `SyncMembersApp.cs` | `sync`/`list` 실행과 종료 코드 |
| `CliOptions.cs`, `UsageText.cs` | 인수 해석, 도움말 |
| `SyncSettings.cs` | 설정 파일 읽기(세 키)와 DB 경로 해석 |
| `SlackRosterClient.cs` | Slack Web API 수집, 안전한 오류 메시지 |
| `src/SelectLunch.Shared/Entities/ChannelMember.cs` | 엔티티 |
| `src/SelectLunch.Shared/Members/ChannelRosterStore.cs` | 반영·조회 (Slack을 모르는 순수 DB 로직) |

도우미는 솔루션(`.slnx`)에 포함된다. `dotnet test`로 구성원 저장·조회·실패 경계를 검증한다.
테스트는 `tests/SelectLunch.Shared.Tests`의 `ChannelRosterStoreTests`, `SlackRosterClientTests`,
`SyncSettingsTests`, `SyncMembersAppTests`이며, 이 도구 프로젝트를 참조한다.
