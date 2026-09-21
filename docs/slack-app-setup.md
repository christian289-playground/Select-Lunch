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
| `chat:write` | 메시지 발송(`chat.postMessage`)·갱신(`chat.update`) |

코드가 실제로 호출하는 Slack Web API는 이 세 가지뿐이다: `chat.postMessage`(투표·결과·식사
기록 안내 발송), `chat.update`(투표 집계 갱신), `views.open`(등록/수정 모달 열기). `chat:write`는
채널 종류와 무관한 단일 스코프이므로 — `channels:write`나 `groups:write` 같은 채널별 변종은
없다 — 대상이 비공개(잠금) 채널이어도 이 스코프 하나로 충분하다. 대신 **봇이 그 채널의
멤버여야** 발송이 되므로, [7단계](#7-채널에-초대)의 초대를 빠뜨리면 안 된다. `views.open`은
아예 스코프가 필요 없다 — 슬래시 커맨드나 버튼 클릭에서 받은 `trigger_id`만 있으면 된다.

봇은 채널 정보나 과거 메시지를 조회하지 않고(`conversations.info`/`.history` 호출 없음),
사용자 이름도 `<@사용자ID>` 멘션 문법으로만 표시해 Slack 클라이언트가 알아서 풀어준다(별도
조회 없음). 그래서 `groups:read`·`groups:history`·`users:read`는 이 봇에는 필요 없다 — 최소
권한 원칙에 따라 추가하지 않는다.

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
