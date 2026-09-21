# Select-Lunch

점심 고르자. 슬랙 채널에서 점심 투표를 열고, 알고리즘으로 식당을 추천하는 봇.

## 하루 흐름

    10:30  투표 개시 — 등록된 식당 전체가 후보
    11:00  마감 → 투표 1위 + 알고리즘 추천 (선정 근거 포함)
    13:30  기록 요청 — 실제로 먹은 곳을 남기면 다음 추천이 정확해진다

시각은 모두 설정으로 바꿀 수 있고, **앱 재기동 없이 반영**된다(단, 토큰·채널 ID는
예외 — [알아둘 점](#알아둘-점) 참고). 투표 메시지에는 후보 버튼과 함께
**"나 오늘 따로 먹어요"** 버튼이 있다. 이건 투표가 아니라 기권 표시로, 집계·동점
해소·추천 점수·식사 기록 어디에도 영향을 주지 않는다 — 누가 따로 먹는지 채널에
보이게 하는 것이 유일한 목적이다. 집계 아래에 `따로 먹어요 (N) — @이름 @이름` 형태로
명단이 뜬다.

카테고리 정보가 비어 추천에서 빠진("Pending") 식당이 있으면 매주 금요일
`Lunch:PendingReminder:At`(기본 16:00)에 보완 요청 메시지가 따로 올라온다.

## 추천 알고리즘

랜덤을 쓰지 않는다. 카테고리별로 점수를 매겨 결정적으로 고른다.

    score = D − (W7 × N7 + W30 × N30)

      D   = 마지막으로 먹은 날로부터 경과일 (상한 30, 미방문도 30)
      N7  = 최근 7일 식사 횟수     (기본 가중치 3)
      N30 = 최근 30일 식사 횟수    (기본 가중치 1)

점수가 가장 높은 카테고리에서, 마지막 방문이 가장 오래된 식당을 고른다.
결과만이 아니라 **계산 과정 전체를 메시지에 노출**한다. 기권은 이 계산에 전혀
관여하지 않는다 — 투표하지 않은 것과 동일하게 취급된다.

## 시작하기

1. [Slack 앱 설정](docs/slack-app-setup.md)을 따라 토큰을 발급한다
2. `src/SelectLunch.Slack/appsettings.Local.example.json`을
   `appsettings.Local.json`으로 복사하고 토큰·채널 ID를 채운다
3. 실행한다

```bash
dotnet run --project src/SelectLunch.Slack
```

DB(`data/lunch.db`)는 기동 시 자동 생성·마이그레이션된다. 별도 준비가 필요 없다.
토큰이 비어 있거나 형식이 틀리면(예: `xoxb-`로 시작하지 않음), 또는 `Lunch:TimeZone`이
잘못된 타임존 ID면 스택 트레이스 없이 한 줄 메시지와 함께 즉시 종료한다(종료 코드 1).

## 명령어

`/lunch <서브커맨드>` 형태로 쓴다. 인자 없이 `/lunch`만 치면 도움말이 뜬다.

| 명령 | 동작 |
|---|---|
| `/lunch add [이름]` | 식당 등록 (모달이 열린다) |
| `/lunch list` | 등록된 식당 목록 |
| `/lunch edit` | 식당 수정 (모달이 열린다) |
| `/lunch pending` | 카테고리가 비어 추천에서 빠진 식당 |
| `/lunch stats` | 카테고리별 현재 추천 점수 |
| `/lunch today` | 오늘 투표 상태 다시 보기 |
| `/lunch help` (또는 그 외 인자) | 위 목록을 요약한 도움말 |

## 구조

    src/SelectLunch.Shared    추천 알고리즘·스케줄 판정·DB (플랫폼 의존성 0)
    src/SelectLunch.Slack     SlackNet Socket Mode Worker

`Shared`는 Slack을 모른다. Teams head를 나중에 붙일 수 있게 한 경계다.

## 알아둘 점

- **한 번에 하나만 띄운다.** 같은 PC에서의 중복 기동은 뮤텍스로 막히지만, 다른 PC에서
  같은 채널에 붙이면 막을 방법이 없어 메시지가 두 번 나간다.
- **토큰과 채널 ID는 핫리로드되지 않는다.** `appsettings.Local.json`의 `Slack:*` 값을
  바꾸면 재기동해야 한다. 반면 `Lunch:*`(시각·가중치·공휴일 등)는 파일을 저장하는
  즉시 반영된다.
- 공휴일은 `appsettings.json`의 `Lunch:Holidays`에 날짜를 적어 관리한다.
- Slack과의 연결이 끊기거나 재연결에 실패하면 앱 로그(`SlackNet.*` 카테고리)에
  남는다 — 프로세스는 살아있어도 아무 메시지가 안 나가는 상태를 로그로 구분할 수 있다.

## 개발

```bash
dotnet test                                  # 전체 테스트
dotnet ef migrations add <이름> --project src/SelectLunch.Shared --output-dir Data/Migrations
```

설계 문서: [docs/superpowers/specs/2026-09-18-select-lunch-design.md](docs/superpowers/specs/2026-09-18-select-lunch-design.md)
