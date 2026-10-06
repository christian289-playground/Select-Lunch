# Slack 앱 설정

봇을 돌리려면 먼저 Slack 앱을 만들어야 한다. 코드로 대신할 수 없는 단계다.

## 1. 앱 생성

<https://api.slack.com/apps> → **Create New App** → **From scratch**
이름을 정하고 워크스페이스를 고른다.

## 2. Socket Mode 활성화

**Settings → Socket Mode** 에서 켠다.
App-Level Token이 생성되며 `connections:write` 스코프가 필요하다.
발급된 `xapp-...` 토큰을 `appsettings.Local.json`의 `Slack:AppToken`에 넣는다.

## 3. 봇 스코프 지정

**Features → OAuth & Permissions → Bot Token Scopes** 에 다음을 추가한다.

| 스코프 | 용도 |
|---|---|
| `commands` | `/lunch` 슬래시 커맨드 등록 |
| `chat:write` | 메시지 발송·갱신 및 본인만 보이는 투표 알림(`chat.postEphemeral`) |
| `files:write` | 오늘의 메뉴 사진을 투표 스레드에 업로드(`files.*`) |
| `channels:read` | 공개 채널 구성원 조회(`conversations.members`) |
| `groups:read` | 비공개 채널 구성원 조회(`conversations.members`) |
| `users:read` | 구성원 중 봇·앱·비활성 계정 제외(`users.info`) |

투표·결과·식사 기록은 `chat.postMessage`, 집계 갱신은 `chat.update`, 등록/수정 모달은
`views.open`을 쓴다. 파일 업로드는 SlackNet의 `Files.Upload`가 내부적으로
`files.getUploadUrlExternal`과 `files.completeUploadExternal`을 호출한다.
`files:write`가 없으면 투표는 정상 동작하고 메뉴 사진만 올라가지 않는다.

마감 전 알림은 `conversations.members`로 채널 구성원을 읽고 `users.info`로 사람 계정을
구분한 뒤, 투표·기권하지 않은 구성원 각각에게 `chat.postEphemeral`을 한 번만 호출한다.
투표 링크는 `chat.getPermalink`로 얻는다(추가 스코프 없음). 공개 채널만 사용한다면
`groups:read`, 비공개 채널만 사용한다면 `channels:read`는 생략할 수 있다.

`chat:write`는 채널 종류와 무관한 단일 스코프다. 비공개 채널이어도 메시지 발송에는
이 스코프 하나로 충분하지만, 구성원 조회에는 `groups:read`가 따로 필요하다.
**봇이 해당 채널의 멤버여야** 조회·발송이 되므로 [7단계](#7-채널에-초대)의 초대를
빠뜨리지 않는다. `views.open`은 스코프 없이 `trigger_id`만 있으면 된다.

과거 메시지 조회나 이메일 주소는 쓰지 않으므로 `channels:history`·`groups:history`·
`users:read.email`은 필요 없다.

**이미 설치한 앱에 권한을 추가했다면 같은 페이지 위쪽의 Reinstall to Workspace로
반드시 재설치한다.** 스코프만 저장하면 기존 봇 토큰에는 적용되지 않는다.
재설치 후 봇 토큰이 바뀌었다면 `appsettings.Local.json`의 `Slack:BotToken`도 갱신하고
재기동한다. 토큰을 채팅이나 git에 남기지 않는다.

## 4. 슬래시 커맨드 등록

**Features → Slash Commands → Create New Command**

- Command: `/lunch`
- Description: `점심 투표와 식당 관리`
- Usage Hint: `add | list | edit | pending | stats | today | help`

Socket Mode를 쓰므로 Request URL은 비워 둔다.

## 5. 상호작용 활성화

**Features → Interactivity & Shortcuts** 를 켠다.
Socket Mode에서는 Request URL이 필요 없다. 투표 버튼, "나 오늘 따로 먹어요" 기권 버튼,
식사 기록 버튼, 등록/수정 모달 제출이 모두 이 경로로 들어온다.

식당 드롭다운은 **external_select**(검색어를 우리가 받아 거르는 방식)라 같은 화면의
**Select Menus → Options Load URL**이 눈에 띄는데, **비워 두면 된다.** Socket Mode에서는
`block_suggestion` 요청도 소켓으로 들어오므로 공개 URL이 필요 없고, 추가 스코프도 없다.

## 6. 워크스페이스에 설치

**Settings → Install App** → 설치하면 `xoxb-...` 봇 토큰이 나온다.
`appsettings.Local.json`의 `Slack:BotToken`에 넣는다.

## 7. 채널에 초대

봇이 동작할 잠금 채널에서 `/invite @봇이름` 을 실행한다.
**이 단계를 빠뜨리면 메시지 발송이 실패한다.**

채널 ID는 채널 이름 클릭 → 하단의 Channel ID(`C`로 시작)를 복사해
`Slack:ChannelId`에 넣는다.

## 8. 토큰 확인

여기까지 마치면 `xoxb-...`(봇), `xapp-...`(앱 레벨), 채널 ID(`C...`) 세 가지가 모두
있어야 한다. 앱을 실행하면 이 세 값을 기동 시점에 검사한다 — 하나라도 비어 있거나
형식이 틀리면(예: `BotToken`이 `xoxb-`로 시작하지 않음) 스택 트레이스 대신
"설정이 올바르지 않습니다: ..." 한 줄과 함께 즉시 종료한다. 어느 값이 문제인지
그 메시지로 알 수 있다.
