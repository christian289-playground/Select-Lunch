# Select-Lunch 설계 문서

- 작성일: 2026-09-18
- 상태: 검토 대기
- 1차 대상: **Slack 봇**
- 후순위: Teams 봇 (경계만 확정, 이번 구현 범위 밖)

---

## 1. 목적

팀의 점심 결정을 슬랙 채널 안에서 끝낸다. 매일 정해진 시각에 봇이 투표를 열고,
30분 뒤 마감해 **투표 1위**와 **알고리즘 추천** 두 갈래를 제시한다.
오후에는 실제로 무엇을 먹었는지 기록받아, 다음 날의 추천 정확도를 높인다.

추천은 **완전 랜덤을 배제한다.** 카테고리별 최근 식사 이력에 기반한 결정적
알고리즘으로 선정하며, 선정 근거(점수 계산 과정)를 결과와 함께 노출한다.

## 2. 범위

### 범위 내

1. **식당 등록** — 이름 + 카테고리 필수, 도보시간·가격대·메모 선택
2. **정시 자동 알림 + 투표** — 설정된 시각에 투표 개시, 30분 후 자동 마감
3. **투표** — 1인 1표, 변경 가능, 실시간 집계 표시
4. **알고리즘 추천** — 카테고리 점수 기반, 근거 전문 공개
5. **식사 기록** — 오후 정시에 기록 요청, 신규 식당 즉시 등록 가능
6. **설정 핫리로드** — 앱 재기동 없이 시각·가중치 변경 반영

### 범위 밖 (Non-goals)

- 다채널 동시 운영 (채널 1개 고정)
- 개인별 맞춤 추천 (기록은 채널 단위 하루 1건)
- 완전 랜덤 추첨 — **명시적으로 금지**
- 외부 지도/식당 정보 API 연동
- 공휴일 자동 조회 API (설정 파일의 날짜 목록으로 관리)
- 다중 인스턴스 동시 가동 (단일 인스턴스 전제)

## 3. 기술 스택

| 항목 | 선택 | 버전 | 근거 |
|---|---|---|---|
| 런타임 | .NET / C# | 10 / C# 14 | 최신 안정 버전 |
| Slack | SlackNet | 0.18.0 | Socket Mode 지원 |
| DI | SlackNet.Extensions.DependencyInjection | 0.18.0 | 코어만으로 Socket Mode 구동 |
| 호스트 | Worker Service | .NET 10 | Socket Mode는 HTTP 수신 불필요 → ASP.NET Core 의존성 제거 |
| DB | SQLite + EF Core | 10.0.12 | 파일 1개, 무설정, 프로세스 내 동작 |
| 테스트 | xunit.v3 | 4.0.1 | `global.json`에 `test.runner = Microsoft.Testing.Platform` 필수 (.NET 10 SDK) |

### DB 선택 근거

LiteDB도 검토했다. 순수 .NET이라 네이티브 의존성이 없다는 장점이 있으나,
EF Core 공식 프로바이더가 없어 쿼리를 직접 작성해야 한다. `Microsoft.Data.Sqlite`가
네이티브 바이너리를 자동 동봉하므로 실사용상 배포 부담은 동일하다. **SQLite 채택.**

### 연결 방식 근거

Socket Mode를 쓴다. 공인 IP·도메인·인증서·포트포워딩이 모두 불필요해,
사내 PC에서 앱 토큰(`xapp-`)만으로 동작한다. SlackNet 코어의 `ISlackSocketModeClient`를
사용하므로 ASP.NET Core 호스트가 필요 없다.

## 4. 프로젝트 구조

```
SelectLunch.slnx
├ src/
│  ├ SelectLunch.Shared/        플랫폼 의존성 0 — Slack도 Teams도 모름
│  │    Entities/                 Category, Restaurant, LunchPoll, PollCandidate,
│  │                              PollVote, MealRecord, ChannelDay
│  │    Recommendation/           RecommendationEngine        ← 순수 함수
│  │    Scheduling/               LunchSchedule               ← 순수 함수
│  │    Data/                     LunchDbContext, Migrations,
│  │                              DesignTimeDbContextFactory
│  │    Options/                  LunchOptions, RecommendationOptions
│  │
│  ├ SelectLunch.Slack/         독립 실행 — Worker + SlackNet + Block Kit
│  └ SelectLunch.Teams/         (후순위 — 이번 범위 밖)
│
└ tests/
   ├ SelectLunch.Shared.Tests/   알고리즘·스케줄 판정 — 테스트 무게중심
   └ SelectLunch.Slack.Tests/    Block Kit 빌더, 핸들러
```

### 공유 경계 원칙

Shared에 들어가는 기준은 **"플랫폼과 무관하게 정답이 하나뿐인 것"** 이다.
엔티티, 추천 점수 계산, 스케줄 판정, 스키마가 여기 해당한다.
메시지 렌더링·상호작용 수신·호스팅은 전부 head 소관이며 공유하지 않는다.

플랫폼 간 추상화 인터페이스(`IChatGateway` 류)는 **두지 않는다.** head가 각각
독립 실행 파일이므로, 자기 SDK를 직접 호출하는 편이 간접층보다 읽기 쉽다.

각 head는 서로를 참조하지 않고, 각자의 DB 파일을 쓴다. 데이터가 섞이지 않으므로
`Platform` 구분 컬럼이 필요 없다.

## 5. 데이터 모델

```csharp
public enum RestaurantStatus { Active = 0, Pending = 1, Archived = 2 }
public enum PollStatus       { Open = 0, Closed = 1, Cancelled = 2 }

// 기록이 어떤 경로로 들어왔는지
public enum MealSource
{
    Prompt          = 0,   // 기록 요청 메시지에서 목록 선택
    NewRegistration = 1,   // 기록 도중 신규 식당 등록
    Manual          = 2,   // 슬래시 커맨드로 수동 입력/정정
}
```

**불변 조건:** `Status == Active`  ⟺  `CategoryId != null`.
카테고리가 없는 식당은 점수 계산에 참여할 수 없으므로 반드시 `Pending`이다.

| 엔티티 | 필드 | 제약 |
|---|---|---|
| `Category` | Id, Name, IsBuiltIn, CreatedAt | UNIQUE(Name) |
| `Restaurant` | Id, Name, CategoryId?, Status, WalkMinutes?, PriceLevel?, Note?, CreatedBySlackUserId, CreatedAt, UpdatedAt | UNIQUE(정규화된 Name) |
| `LunchPoll` | Id, ChannelId, Date, MessageTs?, OpensAt, ClosesAt, Status, WinnerRestaurantId?, RecommendedRestaurantId?, RationaleJson? | UNIQUE(ChannelId, Date) |
| `PollCandidate` | PollId, RestaurantId, DisplayOrder | PK(PollId, RestaurantId) |
| `PollVote` | Id, PollId, SlackUserId, RestaurantId, VotedAt | UNIQUE(PollId, SlackUserId) |
| `MealRecord` | Id, ChannelId, Date, RestaurantId, RecordedBySlackUserId, RecordedAt, Source | UNIQUE(ChannelId, Date), INDEX(Date) |
| `ChannelDay` | ChannelId, Date, MealPromptPostedAt?, PendingReminderSentAt? | PK(ChannelId, Date) |

### 설계 의도

- **`Restaurant.Status`** — `Pending`은 기록 과정에서 이름만 들어온 상태다.
  이력에는 남지만 **투표 후보와 추천 풀에서 제외**된다. 카테고리가 없으면
  점수 계산에 참여할 수 없기 때문이다. 봇이 주기적으로 정보 보완을 요청한다.
- **`Archived` 식당의 식사 이력은 카테고리 점수에 계속 반영된다.** 투표 후보와 추천
  풀에서는 빠지지만(`Active`만 후보), 과거에 그 가게에서 먹었다는 사실은 남는다.
  이력까지 제외하면 가게 하나를 보관 처리하는 순간 그 카테고리가 "최근 안 먹은 것"으로
  보여 과다 추천된다 — 팀이 그날 그 음식을 먹은 것은 가게 폐업과 무관한 사실이다.
- **`PollCandidate`** — 투표 시점의 후보 스냅샷. 식당이 나중에 수정·보관되어도
  과거 투표 기록의 해석이 깨지지 않는다.
- **`LunchPoll.RationaleJson`** — 추천 계산 근거(카테고리별 점수 스냅샷)를 저장한다.
  나중에도 "그날 왜 그게 나왔는지" 재현할 수 있다.
- **`ChannelDay`** — 그날 어떤 메시지를 이미 보냈는지 기록한다. 투표가 열리지 않은
  날에도 기록 요청은 나가야 하므로 이 상태를 `LunchPoll`에 둘 수 없다 — 그날 Poll이
  아예 없을 수 있고, 그러면 기록 요청이 무한 반복된다.
- **`MealRecord` UNIQUE(ChannelId, Date)** — 채널 단위 하루 1건.
  나중에 누른 사람이 덮어쓴다(오입력 정정 가능). 누가 기록했는지 함께 표시한다.
- **기본 카테고리** — 한식 / 중식 / 일식 / 양식 / 분식 / 아시안 / 기타.
  `IsBuiltIn = true`로 시드하며, 사용자 추가가 가능하다.

## 6. 하루 흐름

```
10:30  투표 개시
         후보 = Status가 Active인 등록 식당 전체 (추첨하지 않음)
         식당 <= 10개 → Block Kit 버튼
         식당 >  10개 → 카테고리별 option_group 드롭다운
                        (actions 블록당 25개 제한 회피 + 가독성)

11:00  마감 (개시 + VoteDurationMinutes) → 두 갈래 제시
         ├ 투표 1위   동점이면 추천 점수가 높은 쪽 (랜덤 아님)
         └ 앱 추천    점수 계산 전 과정 노출
         두 값이 같으면 "투표와 추천이 일치" 한 줄로 합쳐 표시

13:30  기록 요청 "오늘 뭐 드셨어요?"
         ├ 목록에서 선택         → MealRecord 확정
         └ [새 식당 등록] 버튼   → 모달(이름 + 카테고리 필수)
                                 → Active로 즉시 등록 후 기록
```

아무도 기록하지 않으면 그날은 **기록 없음**으로 둔다. 투표 1위를 자동 기록하지
않는다 — 실제로 먹지 않았을 수 있고, 거짓 데이터는 추천 품질을 직접 훼손한다.

## 7. 추천 알고리즘

### 수식

```
각 카테고리 c (Active 식당을 1곳 이상 보유) 에 대해:

    D   = today − 마지막으로 먹은 날
          한 번도 안 먹었으면 DaysSinceCap
          DaysSinceCap(기본 30)으로 상한

    N7  = 최근 7일  내 해당 카테고리 식사 횟수
    N30 = 최근 30일 내 해당 카테고리 식사 횟수

    score(c) = D − (W7 × N7 + W30 × N30)        기본 W7 = 3, W30 = 1
```

### 선정 순서

```
1. score 최고 카테고리
     동점 → 마지막 식사가 더 오래된 쪽
     동점 → 카테고리 이름 사전순(ordinal)

2. 해당 카테고리 안에서 마지막 방문이 가장 오래된 Active 식당
     (미방문 최우선)
     동점 → 등록일 오래된 순 → Id 순
```

전 구간이 **결정적(deterministic)** 이다. 같은 이력이면 언제 돌려도 같은 답이
나오며, 랜덤이 개입하는 지점이 하나도 없다.

`Pending` 식당의 식사 기록은 카테고리가 없으므로 점수 계산에서 **제외**하되,
이력 자체는 보존한다.

### 인터페이스 (순수 함수)

```csharp
public sealed record CategoryStat(
    long CategoryId, string CategoryName,
    DateOnly? LastEatenOn, int Count7d, int Count30d);

public sealed record CategoryScore(
    long CategoryId, string CategoryName,
    DateOnly? LastEatenOn,                       // 동점 처리에 필요
    int DaysSince, int Count7d, int Count30d, int Score);

public sealed record RestaurantInfo(
    long RestaurantId, string Name,
    long CategoryId, string CategoryName,
    DateOnly? LastEatenOn, DateTimeOffset CreatedAt);

public sealed record RestaurantPick(
    long RestaurantId, string Name, DateOnly? LastEatenOn);

public sealed record Recommendation(
    RestaurantPick Pick,
    CategoryScore Winner,
    IReadOnlyList<CategoryScore> Others);

// 추천 가능한 식당이 하나도 없으면 null
public static Recommendation? Recommend(
    DateOnly today,
    IReadOnlyList<CategoryStat> stats,
    IReadOnlyList<RestaurantInfo> restaurants,   // Active 식당만
    RecommendationOptions options);
```

DB도 Slack도 호출하지 않는다. 전부 표 기반 테스트로 검증한다.
호출자(head)가 `Active` 식당과 집계된 `CategoryStat`을 DB에서 읽어 넘긴다.

### 출력 — 결과가 아니라 계산 과정

```
🍣 오늘의 추천 — 스시로 (일식)

  일식이 선정된 이유 — 점수 13점 (1위)
    · 마지막 방문 2026-09-04 → 14일 경과          D  = 14
    · 최근 7일  0회                               −3 × 0 =  0
    · 최근 30일 1회                               −1 × 1 = −1
    · 14 − 0 − 1 = 13점

  경쟁 카테고리
    분식  11점 (11일 전, 7일내 0회, 30일내 0회)
    중식   9점 (12일 전, 7일내 1회, 30일내 0회)
    한식   4점 ( 3일 전, 7일내 2회, 30일내 5회)  ← 최근 과다

  일식 3곳 중 스시로 — 마지막 방문 2026-08-21로 가장 오래됨
     (스시노야 09-04, 오마카세김 08-30)
```

알고리즘이 블랙박스가 되지 않게 하는 것이 요구사항이다. 결과만 보여주지 않는다.

## 8. Slack 인터페이스

### 슬래시 커맨드

`/lunch` 하나에 서브커맨드를 둔다.

| 커맨드 | 동작 |
|---|---|
| `/lunch` · `/lunch help` | 도움말 |
| `/lunch add [이름]` | 등록 모달 (이름 프리필) |
| `/lunch list` | 등록된 식당 목록 (카테고리별) |
| `/lunch edit` | 수정 모달 (이름이 같으면 기존 식당을 갱신) |
| `/lunch pending` | 정보 미완성 식당 + 보완 버튼 |
| `/lunch stats` | 카테고리별 현재 점수 현황 (7일·30일 횟수 동시 표시) |
| `/lunch today` | 오늘 투표/결과/추천 다시 보기 |

### 인터랙션 (`action_id` 규약)

| action_id | 처리 |
|---|---|
| `vote:{pollId}:{restaurantId}` | 버튼 투표 |
| `vote_select:{pollId}` | 드롭다운 투표 |
| `meal:{date}:{restaurantId}` | 식사 기록 |
| `meal_new:{date}` | 신규 식당 등록 모달 |
| `restaurant_fill:{restaurantId}` | Pending 보완 모달 (`PendingActionHandler`) |

### SlackNet 핸들러 매핑

- `ISlashCommandHandler` — `/lunch`
- `IBlockActionHandler` — 위 action_id 전체
- `IViewSubmissionHandler` — 등록/수정 모달 제출

### 필요 스코프

대상이 **잠금(private) 채널**이므로 `channels:*`가 아니라 `groups:*`를 쓴다.

- Bot Token (`xoxb-`): `commands`, `chat:write` — 이 둘뿐이다.
  코드가 호출하는 Web API는 `chat.postMessage` · `chat.update` · `views.open` 세 가지이고,
  `chat:write`는 채널 종류와 무관한 단일 스코프라 `groups:*` 변종이 없다. 채널 내용을
  읽지 않으므로(`conversations.*` 미사용) `groups:read`/`groups:history`도 불필요하고,
  멘션은 `<@사용자ID>` 문법이라 `users:read`도 필요 없다. `views.open`은 무스코프.
- App-Level Token (`xapp-`): `connections:write`
- **봇을 해당 채널에 초대해야 동작한다.**

## 9. 스케줄러

타이머가 아니라 **DB 상태를 기준으로 판단**한다. 앱이 꺼져 있던 구간을 재기동 시
따라잡으며, 중복 발송도 누락도 발생하지 않는다.

```csharp
public sealed record PollSnapshot(
    long PollId, PollStatus Status, DateTimeOffset ClosesAt);

public sealed record TodayState(
    DateOnly Date,
    PollSnapshot? Poll,                  // 오늘자 투표가 없으면 null
    bool MealPromptPosted,
    DateOnly? LastPendingReminderOn,
    bool HasPendingRestaurants);

public enum DueActionKind { OpenPoll, ClosePoll, PostMealPrompt, RemindPending }

public sealed record DueAction(
    DueActionKind Kind,
    DateTimeOffset ScheduledFor);        // 원래 예정 시각 (지각 판정 근거)

public static IReadOnlyList<DueAction> GetDueActions(
    DateTimeOffset now, TodayState state, LunchOptions options);
```

### 판정 규칙

```
영업일이 아니면(주말 또는 설정된 공휴일) 아무 것도 하지 않는다.

OpenPoll        now >= VoteOpenAt  이고 오늘자 Poll이 없음
ClosePoll       Poll.Status == Open  이고 now >= Poll.ClosesAt
PostMealPrompt  now >= MealRecordAt  이고 오늘 기록 요청을 아직 안 보냄
                (투표가 열리지 않았던 날에도 기록은 받는다)
RemindPending   설정된 요일·시각이고 Pending 식당이 존재하며 이번 주 미발송
```

### 지각 방어

`CatchUpGraceMinutes`(기본 180분)를 넘겨 지난 작업은 **건너뛴다.**
자정 직전에 기동했다고 그날 아침 투표를 뒤늦게 여는 일을 막는다.

head는 `PollIntervalSeconds`(기본 30초)마다 `TodayState`를 읽어 이 함수에 넘기고,
돌려받은 지시를 자기 SDK로 실행할 뿐이다. 시계를 주입하므로 전 규칙을 단위 테스트할 수 있다.

## 10. 설정

```jsonc
// appsettings.json — 커밋됨. 토큰 없음
{
  "Lunch": {
    "TimeZone": "Asia/Seoul",
    "VoteOpenAt": "10:30",
    "VoteDurationMinutes": 30,
    "MealRecordAt": "13:30",
    "WeekdaysOnly": true,
    "CatchUpGraceMinutes": 180,
    "PollIntervalSeconds": 30,
    "Holidays": [ "2026-10-03", "2026-10-09" ],
    "PendingReminder": { "Enabled": true, "DayOfWeek": "Friday", "At": "16:00" },
    "Recommendation": { "DaysSinceCap": 30, "Weight7d": 3, "Weight30d": 1 }
  },
  "Database": { "Path": "data/lunch.db" }
}
```

```jsonc
// appsettings.Local.json — gitignore. 토큰·채널 ID
{
  "Slack": {
    "BotToken":  "xoxb-...",
    "AppToken":  "xapp-...",
    "ChannelId": "C0..."        // 잠금 채널 ID
  }
}
```

### 핫리로드

`IOptionsMonitor<LunchOptions>` + `reloadOnChange: true`로 **재기동 없이 반영**된다.
스케줄러는 변경 콜백에서 다음 실행 시각을 재계산하고, 변경 사실을 채널에 한 줄 알린다.

> ⚙️ 투표 시각이 10:30 → 11:00으로 변경되었습니다

**토큰과 채널 ID는 핫리로드 대상에서 제외한다.** 웹소켓 연결을 다시 맺어야 하는
값이라, 조용히 바뀌면 오히려 장애 추적이 어려워진다. 변경 시 재기동하도록 로그로 안내한다.

`TimeZone`은 IANA ID를 쓴다 (.NET 6 이후 Windows에서도 동작).

## 11. 운영

- **DB 위치** — 실행 디렉터리 하위 `data/lunch.db`. 앱이 도는 PC에 남으며 커밋하지 않는다.
- **`.gitignore` 추가** — `data/`, `*.db`, `*.db-shm`, `*.db-wal`, `appsettings.Local.json`
- **기동 시** `Database.Migrate()`로 스키마 자동 생성 — 첫 실행에 별도 작업이 필요 없다.
- **마이그레이션** — Shared에 `IDesignTimeDbContextFactory`를 두어 `dotnet ef`가 Shared
  단독으로 동작하게 한다.
- **단일 인스턴스 보장** — 이름 있는 뮤텍스로 같은 PC에서 중복 기동을 차단한다.
  (중복 실행 시 자동 메시지가 두 번 나가고 집계가 갈라진다)
- **세션 만료** — SlackNet이 Socket Mode 재연결을 처리한다. 로그로 관측한다.

## 12. 테스트 전략

TDD로 진행한다. 무게중심은 `SelectLunch.Shared.Tests`다.

| 대상 | 방식 |
|---|---|
| `RecommendationEngine` | 표 기반 케이스 — 동점 처리, 상한 적용, 미방문 카테고리, Pending 제외 |
| `LunchSchedule` | 시각 주입 — 정상 흐름, 재기동 따라잡기, 지각 방어, 주말/공휴일 |
| `LunchDbContext` | in-memory SQLite (`Data Source=:memory:`, 연결 유지) — 제약 조건 검증 |
| Block Kit 빌더 | 스냅샷 — 버튼/드롭다운 분기, 25개 제한 |
| 핸들러 | `ISlackApiClient` 페이크 |

## 13. Teams head — 경계만 (이번 범위 밖)

Azure 구독이 확보되면 `SelectLunch.Teams`를 추가한다. Shared는 플랫폼을 모르므로
나중에 붙여도 손해가 없다.

### 선결 조건 (코드로 우회 불가)

- Teams에는 **Socket Mode 등가물이 없다.** Azure Bot Service가 엔드포인트로 요청을
  밀어넣는 구조라 **공개 HTTPS 주소가 필수**다.
- Microsoft Entra ID 앱 등록 / Azure Bot 리소스(F0 무료 티어 존재)
- Teams 앱 매니페스트 패키징(`manifest.json` + 아이콘 2종 → zip) 후 조직 업로드
- 조직 정책에 따라 IT 관리자 승인 필요

로컬 파일 DB를 유지하면서 푸는 현실적인 방법은 **Cloudflare Tunnel** 이다.
PC에서 아웃바운드로만 연결해 고정 HTTPS 도메인을 받으므로, 포트포워딩·인증서·공인 IP
없이 DB는 그대로 그 PC에 남는다.

### SDK

- **Microsoft 365 Agents SDK** `Microsoft.Agents.Hosting.AspNetCore` 1.8.77
  — Bot Framework SDK v4(4.23.1)의 공식 후속. .NET 10 TFM 직접 지원
- Adaptive Card는 **JSON을 System.Text.Json으로 직접 구성**한다.
  `AdaptiveCards` 3.1.0 패키지는 2023년 이후 정체 + Newtonsoft 의존이라 채택하지 않는다.

### 기능 대응

| 기능 | Slack | Teams |
|---|---|---|
| 호스트 | Worker Service | ASP.NET Core |
| 명령 | `/lunch ...` 슬래시 커맨드 | 슬래시 커맨드 없음 → @멘션 + 명령어, 카드 버튼 |
| 폼 | Modal `views.open` | Dialog(Task Module) + Adaptive Card |
| 투표 | Block Kit 버튼/드롭다운 | Adaptive Card `Action.Execute` |
| 집계 갱신 | `chat.update` | `UpdateActivityAsync` |
| 정시 발송 | 소켓 연결 위에서 발송 | Proactive — conversation reference를 DB에 저장 |
| 사용자 식별 | Slack User ID (`U...`) | Entra Object ID (GUID) |

## 14. 결정 기록

| # | 결정 | 근거 |
|---|---|---|
| 1 | Socket Mode | 사내 PC에서 네트워크 설정 없이 구동 |
| 2 | SQLite + EF Core | 파일 1개 + 공식 ORM 프로바이더 |
| 3 | 식당 중심 등록 (메뉴 아님) | 거리·가격대 등 속성 필요 |
| 4 | 투표 후보는 전체 (추첨 안 함) | "완전 랜덤 금지" 요구 |
| 5 | 기록은 별도 시각의 전용 메시지 | 마감 시점 버튼보다 실제 식사와 시점이 맞음 |
| 6 | `Pending` 상태 도입 | 구체화되지 않은 등록은 추천에 참여 불가 |
| 7 | 하이브리드 점수 (경과일 − 빈도 페널티) | 경과일만·빈도만은 각각 편향이 있음 |
| 8 | 투표 동점을 추천 점수로 해결 | 랜덤 금지를 지키며 결정적으로 해소 |
| 9 | 기록은 하루 1건, 덮어쓰기 허용 | 오입력 정정 가능 |
| 10 | 토큰·채널 ID 핫리로드 제외 | 연결 재수립 필요 — 조용한 변경은 추적을 해침 |
| 11 | 플랫폼 추상화 인터페이스 없음 | head가 독립 실행 파일 — 간접층이 이득 없음 |
| 12 | 스케줄 판정만 Shared, 실행은 head | 가장 버그가 숨기 쉬운 로직의 중복 제거 |
