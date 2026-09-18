# Select-Lunch Slack 봇 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL — `superpowers:subagent-driven-development`(권장)
> 또는 `superpowers:executing-plans`를 사용해 task 단위로 구현하세요.
> 각 단계는 체크박스(`- [ ]`)로 추적합니다.

**Goal:** 슬랙 잠금 채널에서 매일 정시에 점심 투표를 열고, 30분 뒤 마감해
투표 1위와 알고리즘 추천을 함께 제시하며, 오후에 실제 식사를 기록받는 봇을 만든다.

**Architecture:** `SelectLunch.Shared`는 플랫폼을 모르는 순수 코어(엔티티 · 추천
알고리즘 · 스케줄 판정 · EF Core)이고, `SelectLunch.Slack`은 SlackNet Socket Mode로
동작하는 독립 실행 Worker다. 판단은 Shared의 순수 함수가, 실행은 head가 맡는다.

**Tech Stack:** .NET 10 / C# 14, SlackNet 0.18.0, EF Core 10.0.12 + SQLite, xunit.v3 4.0.1

**Spec:** `docs/superpowers/specs/2026-09-18-select-lunch-design.md`
(이 계획은 스펙을 근거로 하므로 두 문서를 함께 읽으세요)

---

## Global Constraints

- **TFM** `net10.0`. `LangVersion` 기본(C# 14). `Nullable` · `ImplicitUsings` 활성.
- **`SelectLunch.Shared`는 Slack/Teams 패키지를 절대 참조하지 않는다.** 순수 코어를
  유지하는 것이 이 설계의 핵심이다.
- **`global.json`에 `test.runner = Microsoft.Testing.Platform` 필수.** .NET 10 SDK에서
  xunit.v3는 이 설정 없이는 `dotnet test`가 동작하지 않는다 (검증 완료).
- **테스트 프로젝트 csproj에 `<OutputType>Exe</OutputType>` 필수**,
  `UseMicrosoftTestingPlatformRunner`는 **넣지 말 것** (VSTest 브리지가 .NET 10에서 실패).
- **추천에 랜덤 금지.** `Random`·`Guid.NewGuid()`·해시 순서 의존을 쓰지 않는다.
  모든 정렬은 완전한 tie-break까지 명시해 결정적이어야 한다.
- **시간은 주입한다.** 순수 함수는 `DateTimeOffset now` / `DateOnly today`를 인자로 받는다.
  `DateTime.Now`를 직접 호출하지 않는다.
- **커밋 메시지 말미**에 다음 줄을 넣는다:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **비밀값을 커밋하지 않는다.** 토큰·채널 ID는 `appsettings.Local.json`(gitignore)에만 둔다.
- 각 task는 `dotnet test`가 **전체 통과**한 상태로 끝난다. 계획은 누적 테스트
  개수를 세지 않는다 — 구현 중 케이스가 늘 수 있으므로 통과 여부만 본다.

---

## File Structure

```
global.json                                  test runner 지정 + SDK 고정
Directory.Build.props                        공통 TFM/Nullable 설정
SelectLunch.slnx
.gitignore                                   (수정) data/, *.db, appsettings.Local.json

src/SelectLunch.Shared/
  SelectLunch.Shared.csproj
  Options/RecommendationOptions.cs           가중치·상한
  Options/PendingReminderOptions.cs          보완 리마인더 설정
  Options/LunchOptions.cs                    시각·영업일·위 둘을 포함
  Entities/Enums.cs                          RestaurantStatus, PollStatus, MealSource
  Entities/Category.cs
  Entities/Restaurant.cs
  Entities/LunchPoll.cs
  Entities/PollCandidate.cs
  Entities/PollVote.cs
  Entities/MealRecord.cs
  Entities/ChannelDay.cs                     하루치 발송 이력 (중복 발송 차단)
  Recommendation/RecommendationModels.cs     CategoryStat, CategoryScore, RestaurantInfo,
                                             RestaurantPick, Recommendation
  Recommendation/RecommendationEngine.cs     순수 함수 — 점수·선정
  Scheduling/ScheduleModels.cs               PollSnapshot, TodayState, DueAction(Kind)
  Scheduling/LunchSchedule.cs                순수 함수 — 영업일·due 판정
  Data/LunchDbContext.cs                     DbSet + 제약 구성 + 시드
  Data/DesignTimeDbContextFactory.cs         dotnet ef 용
  Data/LunchQueries.cs                       집계 조회 (DbContext 확장 메서드)
  Data/Migrations/                           EF 생성물

src/SelectLunch.Slack/
  SelectLunch.Slack.csproj
  Program.cs                                 호스트 구성 + DI + SlackNet 등록
  appsettings.json                           커밋됨 (토큰 없음)
  Options/SlackOptions.cs                    BotToken, AppToken, ChannelId
  SingleInstanceGuard.cs                     이름 있는 뮤텍스
  Blocks/ActionIds.cs                        action_id 문자열 + 파싱
  Blocks/PollBlocks.cs                       투표 메시지 (버튼/드롭다운 분기)
  Blocks/ResultBlocks.cs                     마감 결과 + 추천 근거
  Blocks/MealPromptBlocks.cs                 기록 요청 메시지
  Blocks/RestaurantModal.cs                  등록/수정 모달 view
  Services/LunchService.cs                   투표·기록·등록 도메인 조작
  Services/LunchAnnouncer.cs                 메시지 발송/갱신
  Handlers/LunchSlashCommandHandler.cs       /lunch
  Handlers/VoteActionHandler.cs              투표 버튼/드롭다운
  Handlers/MealActionHandler.cs              기록 버튼
  Handlers/RestaurantModalHandler.cs         모달 제출
  Workers/SocketModeWorker.cs                Socket Mode 연결 유지
  Workers/SchedulerWorker.cs                 GetDueActions 실행 루프

tests/SelectLunch.Shared.Tests/
  SelectLunch.Shared.Tests.csproj
  RecommendationEngineTests.cs
  LunchScheduleTests.cs
  LunchDbContextTests.cs
  LunchQueriesTests.cs
  TestDb.cs                                  in-memory SQLite 헬퍼

tests/SelectLunch.Slack.Tests/
  SelectLunch.Slack.Tests.csproj
  ActionIdsTests.cs
  PollBlocksTests.cs
  ResultBlocksTests.cs
  SingleInstanceGuardTests.cs
```

---

### Task 1: 솔루션 스캐폴딩

빌드·테스트 파이프라인이 도는 빈 골격을 만든다. 이 task가 끝나면 `dotnet test`가
초록으로 통과한다.

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `SelectLunch.slnx` (`dotnet new sln`의 .NET 10 기본 형식)
- Create: `src/SelectLunch.Shared/SelectLunch.Shared.csproj`
- Create: `tests/SelectLunch.Shared.Tests/SelectLunch.Shared.Tests.csproj`
- Create: `tests/SelectLunch.Shared.Tests/ScaffoldingTests.cs`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: 없음 (최초 task)
- Produces: `SelectLunch.Shared` 어셈블리(빈 상태), `SelectLunch.Shared.Tests` 테스트
  프로젝트. 이후 모든 task가 이 두 프로젝트에 파일을 추가한다.

- [ ] **Step 1: `global.json` 작성**

.NET 10 SDK에서 xunit.v3를 `dotnet test`로 돌리려면 test runner 지정이 필수다.
이 설정이 없으면 "Testing with VSTest target is no longer supported" 오류가 난다.

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- [ ] **Step 2: `Directory.Build.props` 작성**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>false</InvariantGlobalization>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

`InvariantGlobalization`을 `false`로 두는 이유: `TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul")`
같은 IANA 타임존 조회에 ICU가 필요하다.

- [ ] **Step 3: 프로젝트 생성 및 솔루션 구성**

```bash
dotnet new sln -n SelectLunch        # .NET 10 기본 형식은 .slnx (XML)
dotnet new classlib -o src/SelectLunch.Shared -n SelectLunch.Shared
rm src/SelectLunch.Shared/Class1.cs
dotnet sln add src/SelectLunch.Shared/SelectLunch.Shared.csproj
```

- [ ] **Step 4: 테스트 프로젝트 csproj 작성**

`dotnet new xunit` 템플릿은 xunit v2를 생성하므로 쓰지 않는다. 직접 작성한다.
`OutputType`이 `Exe`여야 하며, `UseMicrosoftTestingPlatformRunner`는 넣지 않는다
(VSTest 브리지 경로가 .NET 10 SDK에서 실패한다).

`tests/SelectLunch.Shared.Tests/SelectLunch.Shared.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SelectLunch.Shared/SelectLunch.Shared.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln add tests/SelectLunch.Shared.Tests/SelectLunch.Shared.Tests.csproj
```

- [ ] **Step 5: 스캐폴딩 확인 테스트 작성**

`tests/SelectLunch.Shared.Tests/ScaffoldingTests.cs`:

```csharp
namespace SelectLunch.Shared.Tests;

public class ScaffoldingTests
{
    [Fact]
    public void 한국_타임존을_조회할_수_있다()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

        Assert.Equal(TimeSpan.FromHours(9), tz.BaseUtcOffset);
    }
}
```

IANA ID 조회는 `InvariantGlobalization=false`가 실제로 먹혔는지 검증한다.
스펙 §10이 `"TimeZone": "Asia/Seoul"`을 쓰므로 여기서 미리 확인해 둔다.

- [ ] **Step 6: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

실패 시 `global.json`의 `test.runner` 누락 또는 csproj의 `OutputType` 누락을 의심한다.

- [ ] **Step 7: `.gitignore`에 런타임 산출물 추가**

파일 끝에 다음을 덧붙인다. 스펙 §11 요구사항이며, DB 파일이 저장소에 올라가는 것을 막는다.

```gitignore

# Select-Lunch 런타임 산출물
# 주의: `data/`처럼 앵커 없는 디렉터리 규칙은 쓰지 않는다. Windows의
# core.ignorecase=true 환경에서 소스 폴더 `src/SelectLunch.Shared/Data/`까지
# 가려버려 DbContext가 조용히 커밋에서 빠진다.
# DB 파일 패턴만으로 충분하다 — git은 빈 디렉터리를 추적하지 않는다.
*.db
*.db-shm
*.db-wal
appsettings.Local.json
```

- [ ] **Step 8: 커밋**

```bash
git add -A
git commit -m "chore: 솔루션 스캐폴딩 및 테스트 파이프라인 구성"
```

---

### Task 2: 옵션 모델

스펙 §10의 설정 스키마를 타입으로 옮긴다. 이후 추천·스케줄 task가 모두 이 타입을 인자로 받는다.

**Files:**
- Create: `src/SelectLunch.Shared/Options/RecommendationOptions.cs`
- Create: `src/SelectLunch.Shared/Options/PendingReminderOptions.cs`
- Create: `src/SelectLunch.Shared/Options/LunchOptions.cs`
- Test: `tests/SelectLunch.Shared.Tests/LunchOptionsTests.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `RecommendationOptions { int DaysSinceCap; int Weight7d; int Weight30d; }`
  - `PendingReminderOptions { bool Enabled; DayOfWeek DayOfWeek; TimeOnly At; }`
  - `LunchOptions { string TimeZone; TimeOnly VoteOpenAt; int VoteDurationMinutes;
    TimeOnly MealRecordAt; bool WeekdaysOnly; int CatchUpGraceMinutes;
    int PollIntervalSeconds; HashSet<DateOnly> Holidays;
    PendingReminderOptions PendingReminder; RecommendationOptions Recommendation;
    TimeOnly VoteCloseAt }`
  - 상수 `LunchOptions.SectionName` = `"Lunch"`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Shared.Tests/LunchOptionsTests.cs`:

```csharp
using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Tests;

public class LunchOptionsTests
{
    [Fact]
    public void 기본값은_스펙과_일치한다()
    {
        var options = new LunchOptions();

        Assert.Equal("Asia/Seoul", options.TimeZone);
        Assert.Equal(new TimeOnly(10, 30), options.VoteOpenAt);
        Assert.Equal(30, options.VoteDurationMinutes);
        Assert.Equal(new TimeOnly(13, 30), options.MealRecordAt);
        Assert.True(options.WeekdaysOnly);
        Assert.Equal(180, options.CatchUpGraceMinutes);
        Assert.Equal(30, options.PollIntervalSeconds);
    }

    [Fact]
    public void 추천_가중치_기본값은_스펙과_일치한다()
    {
        var options = new RecommendationOptions();

        Assert.Equal(30, options.DaysSinceCap);
        Assert.Equal(3, options.Weight7d);
        Assert.Equal(1, options.Weight30d);
    }

    [Fact]
    public void 투표_마감_시각은_개시_시각에_진행시간을_더한_값이다()
    {
        var options = new LunchOptions
        {
            VoteOpenAt = new TimeOnly(10, 30),
            VoteDurationMinutes = 30,
        };

        Assert.Equal(new TimeOnly(11, 0), options.VoteCloseAt);
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `SelectLunch.Shared.Options` 네임스페이스를 찾을 수 없음 (CS0246)

- [ ] **Step 3: 옵션 타입 작성**

`src/SelectLunch.Shared/Options/RecommendationOptions.cs`:

```csharp
namespace SelectLunch.Shared.Options;

/// <summary>스펙 §7 추천 점수 수식의 조정 값.</summary>
public sealed class RecommendationOptions
{
    /// <summary>경과일 상한. 한 번도 먹지 않은 카테고리도 이 값을 쓴다.</summary>
    public int DaysSinceCap { get; set; } = 30;

    /// <summary>최근 7일 식사 1회당 차감치.</summary>
    public int Weight7d { get; set; } = 3;

    /// <summary>최근 30일 식사 1회당 차감치.</summary>
    public int Weight30d { get; set; } = 1;
}
```

`src/SelectLunch.Shared/Options/PendingReminderOptions.cs`:

```csharp
namespace SelectLunch.Shared.Options;

/// <summary>정보가 덜 찬 식당(Pending) 보완 요청 설정.</summary>
public sealed class PendingReminderOptions
{
    public bool Enabled { get; set; } = true;

    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Friday;

    public TimeOnly At { get; set; } = new(16, 0);
}
```

`src/SelectLunch.Shared/Options/LunchOptions.cs`:

```csharp
namespace SelectLunch.Shared.Options;

public sealed class LunchOptions
{
    public const string SectionName = "Lunch";

    /// <summary>IANA 타임존 ID. 모든 시각 판정의 기준이 된다.</summary>
    public string TimeZone { get; set; } = "Asia/Seoul";

    public TimeOnly VoteOpenAt { get; set; } = new(10, 30);

    public int VoteDurationMinutes { get; set; } = 30;

    public TimeOnly MealRecordAt { get; set; } = new(13, 30);

    public bool WeekdaysOnly { get; set; } = true;

    /// <summary>
    /// 예정 시각을 이만큼 넘겨 지난 작업은 건너뛴다. 앱이 오래 꺼져 있다가
    /// 늦게 올라왔을 때 철 지난 메시지를 쏟아내지 않기 위함이다.
    /// </summary>
    public int CatchUpGraceMinutes { get; set; } = 180;

    public int PollIntervalSeconds { get; set; } = 30;

    public HashSet<DateOnly> Holidays { get; set; } = [];

    public PendingReminderOptions PendingReminder { get; set; } = new();

    public RecommendationOptions Recommendation { get; set; } = new();

    public TimeOnly VoteCloseAt => VoteOpenAt.AddMinutes(VoteDurationMinutes);
}
```

- [ ] **Step 4: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "feat: 점심 봇 설정 옵션 모델 추가"
```

---

### Task 3: 추천 모델 + 카테고리 점수 계산

스펙 §7 수식 `score = D − (W7 × N7 + W30 × N30)` 을 구현한다.
이 task는 점수 **계산**까지만 하고, 순위 결정과 식당 선정은 Task 4가 맡는다.

**Files:**
- Create: `src/SelectLunch.Shared/Recommendation/RecommendationModels.cs`
- Create: `src/SelectLunch.Shared/Recommendation/RecommendationEngine.cs`
- Test: `tests/SelectLunch.Shared.Tests/RecommendationEngineTests.cs`

**Interfaces:**
- Consumes: `RecommendationOptions` (Task 2)
- Produces:
  - `record CategoryStat(long CategoryId, string CategoryName, DateOnly? LastEatenOn, int Count7d, int Count30d)`
  - `record CategoryScore(long CategoryId, string CategoryName, DateOnly? LastEatenOn, int DaysSince, int Count7d, int Count30d, int Score)`
  - `record RestaurantInfo(long RestaurantId, string Name, long CategoryId, string CategoryName, DateOnly? LastEatenOn, DateTimeOffset CreatedAt)`
  - `record RestaurantPick(long RestaurantId, string Name, DateOnly? LastEatenOn)`
  - `record Recommendation(RestaurantPick Pick, CategoryScore Winner, IReadOnlyList<CategoryScore> Others)`
  - `static IReadOnlyList<CategoryScore> RecommendationEngine.ScoreCategories(DateOnly today, IReadOnlyList<CategoryStat> stats, RecommendationOptions options)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Shared.Tests/RecommendationEngineTests.cs`:

```csharp
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;

namespace SelectLunch.Shared.Tests;

public class RecommendationEngineTests
{
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly RecommendationOptions Options = new();

    [Fact]
    public void 경과일에서_최근_빈도를_차감해_점수를_낸다()
    {
        // 일식: 9/4에 먹음 → D=14, 최근 7일 0회, 최근 30일 1회
        //       14 − (3×0 + 1×1) = 13
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 4), 0, 1)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        var 일식 = Assert.Single(scores);
        Assert.Equal(14, 일식.DaysSince);
        Assert.Equal(13, 일식.Score);
    }

    [Fact]
    public void 최근에_자주_먹은_카테고리는_점수가_낮다()
    {
        // 한식: 9/15에 먹음 → D=3, 최근 7일 2회, 최근 30일 5회
        //       3 − (3×2 + 1×5) = −8
        CategoryStat[] stats = [new(1, "한식", new DateOnly(2026, 9, 15), 2, 5)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(-8, scores[0].Score);
    }

    [Fact]
    public void 한_번도_먹지_않은_카테고리는_경과일_상한을_쓴다()
    {
        CategoryStat[] stats = [new(9, "태국식", null, 0, 0)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(Options.DaysSinceCap, scores[0].DaysSince);
        Assert.Equal(30, scores[0].Score);
    }

    [Fact]
    public void 아주_오래_전에_먹었어도_경과일은_상한을_넘지_않는다()
    {
        CategoryStat[] stats = [new(5, "분식", new DateOnly(2025, 1, 1), 0, 0)];

        var scores = RecommendationEngine.ScoreCategories(Today, stats, Options);

        Assert.Equal(30, scores[0].DaysSince);
    }

    [Fact]
    public void 가중치를_바꾸면_점수가_따라_바뀐다()
    {
        CategoryStat[] stats = [new(1, "한식", new DateOnly(2026, 9, 15), 2, 5)];
        var options = new RecommendationOptions { Weight7d = 1, Weight30d = 0 };

        var scores = RecommendationEngine.ScoreCategories(Today, stats, options);

        Assert.Equal(1, scores[0].Score);   // 3 − (1×2 + 0×5)
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `RecommendationEngine`을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 모델 작성**

`src/SelectLunch.Shared/Recommendation/RecommendationModels.cs`:

```csharp
namespace SelectLunch.Shared.Recommendation;

/// <summary>한 카테고리의 식사 이력 집계. 조회 계층이 만들어 넘긴다.</summary>
public sealed record CategoryStat(
    long CategoryId,
    string CategoryName,
    DateOnly? LastEatenOn,
    int Count7d,
    int Count30d);

/// <summary>점수가 매겨진 카테고리. <see cref="LastEatenOn"/>은 동점 처리에 쓴다.</summary>
public sealed record CategoryScore(
    long CategoryId,
    string CategoryName,
    DateOnly? LastEatenOn,
    int DaysSince,
    int Count7d,
    int Count30d,
    int Score);

/// <summary>추천 후보가 될 수 있는 식당(Status가 Active인 것만).</summary>
public sealed record RestaurantInfo(
    long RestaurantId,
    string Name,
    long CategoryId,
    string CategoryName,
    DateOnly? LastEatenOn,
    DateTimeOffset CreatedAt);

public sealed record RestaurantPick(
    long RestaurantId,
    string Name,
    DateOnly? LastEatenOn);

/// <summary>추천 결과. <see cref="Others"/>는 점수 내림차순 경쟁 카테고리다.</summary>
public sealed record Recommendation(
    RestaurantPick Pick,
    CategoryScore Winner,
    IReadOnlyList<CategoryScore> Others);
```

- [ ] **Step 4: 점수 계산 구현**

`src/SelectLunch.Shared/Recommendation/RecommendationEngine.cs`:

```csharp
using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Recommendation;

/// <summary>
/// 스펙 §7 추천 알고리즘. 전 구간이 결정적이며 랜덤을 쓰지 않는다.
/// DB도 Slack도 호출하지 않는 순수 함수의 모음이다.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>score = D − (W7 × N7 + W30 × N30)</summary>
    public static IReadOnlyList<CategoryScore> ScoreCategories(
        DateOnly today,
        IReadOnlyList<CategoryStat> stats,
        RecommendationOptions options)
    {
        var scores = new List<CategoryScore>(stats.Count);

        foreach (var stat in stats)
        {
            var daysSince = DaysSince(today, stat.LastEatenOn, options.DaysSinceCap);
            var penalty = options.Weight7d * stat.Count7d + options.Weight30d * stat.Count30d;

            scores.Add(new CategoryScore(
                stat.CategoryId,
                stat.CategoryName,
                stat.LastEatenOn,
                daysSince,
                stat.Count7d,
                stat.Count30d,
                daysSince - penalty));
        }

        return scores;
    }

    /// <summary>미방문은 상한값으로 본다. 미래 날짜가 섞여도 음수가 되지 않게 0에서 자른다.</summary>
    static int DaysSince(DateOnly today, DateOnly? lastEatenOn, int cap) =>
        lastEatenOn is { } last
            ? Math.Clamp(today.DayNumber - last.DayNumber, 0, cap)
            : cap;
}
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "feat: 카테고리 추천 점수 계산 구현"
```

---

### Task 4: 카테고리 순위 + 식당 선정

Task 3의 점수를 받아 최종 추천을 낸다. **동점 처리가 이 task의 핵심**이다.
스펙 §7의 tie-break 사슬을 끝까지 구현해 랜덤 없이 결과가 하나로 정해져야 한다.

**Files:**
- Modify: `src/SelectLunch.Shared/Recommendation/RecommendationEngine.cs`
- Modify: `tests/SelectLunch.Shared.Tests/RecommendationEngineTests.cs`

**Interfaces:**
- Consumes: `ScoreCategories`, `CategoryStat`, `RestaurantInfo` (Task 3)
- Produces:
  `static Recommendation? RecommendationEngine.Recommend(DateOnly today, IReadOnlyList<CategoryStat> stats, IReadOnlyList<RestaurantInfo> restaurants, RecommendationOptions options)`
  — 추천 가능한 식당이 없으면 `null`

- [ ] **Step 1: 실패하는 테스트 추가**

`RecommendationEngineTests.cs`의 클래스 안에 이어 붙인다.

```csharp
    static RestaurantInfo R(
        long id, string name, long categoryId, string categoryName,
        DateOnly? lastEaten, int createdDay = 1) =>
        new(id, name, categoryId, categoryName, lastEaten,
            new DateTimeOffset(2026, 1, createdDay, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void 점수가_가장_높은_카테고리의_식당을_추천한다()
    {
        CategoryStat[] stats =
        [
            new(1, "한식", new DateOnly(2026, 9, 15), 2, 5),   // −8
            new(2, "일식", new DateOnly(2026, 9, 4), 0, 1),    // 13
        ];
        RestaurantInfo[] restaurants =
        [
            R(10, "김밥천국", 1, "한식", new DateOnly(2026, 9, 15)),
            R(20, "스시로", 2, "일식", new DateOnly(2026, 8, 21)),
            R(21, "스시노야", 2, "일식", new DateOnly(2026, 9, 4)),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.NotNull(result);
        Assert.Equal("일식", result.Winner.CategoryName);
        Assert.Equal(13, result.Winner.Score);
        Assert.Equal("스시로", result.Pick.Name);          // 일식 중 가장 오래됨
        Assert.Equal("한식", Assert.Single(result.Others).CategoryName);
    }

    [Fact]
    public void 카테고리_동점이면_더_오래_전에_먹은_쪽이_이긴다()
    {
        // 둘 다 D=10, 빈도 0 → 점수 10으로 동점
        CategoryStat[] stats =
        [
            new(1, "한식", new DateOnly(2026, 9, 8), 0, 0),
            new(2, "일식", new DateOnly(2026, 9, 8), 0, 0),
        ];
        // 마지막 식사일까지 같으면 이름 사전순 → "일식" < "한식" (ordinal)
        RestaurantInfo[] restaurants = [R(10, "김밥천국", 1, "한식", null), R(20, "스시로", 2, "일식", null)];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("일식", result!.Winner.CategoryName);
    }

    [Fact]
    public void 카테고리_안에서는_마지막_방문이_가장_오래된_식당을_고른다()
    {
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 4), 0, 1)];
        RestaurantInfo[] restaurants =
        [
            R(20, "스시노야", 2, "일식", new DateOnly(2026, 9, 4)),
            R(21, "스시로", 2, "일식", new DateOnly(2026, 8, 21)),
            R(22, "오마카세김", 2, "일식", new DateOnly(2026, 8, 30)),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("스시로", result!.Pick.Name);
    }

    [Fact]
    public void 한_번도_안_간_식당이_방문한_식당보다_우선한다()
    {
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 4), 0, 1)];
        RestaurantInfo[] restaurants =
        [
            R(20, "스시로", 2, "일식", new DateOnly(2026, 8, 21)),
            R(21, "신규스시", 2, "일식", null),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("신규스시", result!.Pick.Name);
    }

    [Fact]
    public void 식당_동점이면_먼저_등록된_쪽을_고른다()
    {
        CategoryStat[] stats = [new(2, "일식", null, 0, 0)];
        RestaurantInfo[] restaurants =
        [
            R(21, "나중등록", 2, "일식", null, createdDay: 5),
            R(20, "먼저등록", 2, "일식", null, createdDay: 2),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("먼저등록", result!.Pick.Name);
    }

    [Fact]
    public void Active_식당이_없는_카테고리는_후보에서_빠진다()
    {
        // 중식 점수가 가장 높지만 Active 식당이 하나도 없다
        CategoryStat[] stats =
        [
            new(3, "중식", null, 0, 0),                       // 30점
            new(2, "일식", new DateOnly(2026, 9, 4), 0, 1),   // 13점
        ];
        RestaurantInfo[] restaurants = [R(20, "스시로", 2, "일식", new DateOnly(2026, 8, 21))];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("일식", result!.Winner.CategoryName);
        Assert.Empty(result.Others);
    }

    [Fact]
    public void 이력이_없는_카테고리의_식당도_추천_대상이_된다()
    {
        // stats에 없지만 Active 식당은 있는 카테고리 — 미방문으로 간주해야 한다
        CategoryStat[] stats = [new(2, "일식", new DateOnly(2026, 9, 17), 1, 1)];
        RestaurantInfo[] restaurants =
        [
            R(20, "스시로", 2, "일식", new DateOnly(2026, 9, 17)),
            R(30, "왕서방", 3, "중식", null),
        ];

        var result = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        Assert.Equal("왕서방", result!.Pick.Name);
        Assert.Equal(30, result.Winner.Score);
    }

    [Fact]
    public void 추천할_식당이_없으면_null을_돌려준다()
    {
        var result = RecommendationEngine.Recommend(Today, [], [], Options);

        Assert.Null(result);
    }

    [Fact]
    public void 같은_입력이면_항상_같은_결과가_나온다()
    {
        CategoryStat[] stats =
        [
            new(1, "한식", new DateOnly(2026, 9, 8), 0, 0),
            new(2, "일식", new DateOnly(2026, 9, 8), 0, 0),
            new(3, "중식", new DateOnly(2026, 9, 8), 0, 0),
        ];
        RestaurantInfo[] restaurants =
        [
            R(10, "가게A", 1, "한식", null), R(20, "가게B", 2, "일식", null), R(30, "가게C", 3, "중식", null),
        ];

        var first = RecommendationEngine.Recommend(Today, stats, restaurants, Options);

        for (var i = 0; i < 20; i++)
        {
            var again = RecommendationEngine.Recommend(Today, stats, restaurants, Options);
            Assert.Equal(first!.Pick.RestaurantId, again!.Pick.RestaurantId);
        }
    }
```

마지막 테스트는 "완전 랜덤 금지"라는 요구사항을 직접 지키는 회귀 테스트다.
구현이 나중에 `Random`이나 순서 불안정한 컬렉션에 의존하게 되면 여기서 깨진다.

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `Recommend` 메서드가 없음 (CS0117)

- [ ] **Step 3: 순위 결정과 식당 선정 구현**

`RecommendationEngine.cs`의 `ScoreCategories` 아래에 추가한다.

```csharp
    /// <summary>
    /// 최종 추천을 낸다. 추천 가능한 식당이 하나도 없으면 null.
    /// </summary>
    /// <param name="restaurants">Status가 Active인 식당만 넘긴다.</param>
    public static Recommendation? Recommend(
        DateOnly today,
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        RecommendationOptions options)
    {
        if (restaurants.Count == 0)
            return null;

        var ranked = RankCategories(today, stats, restaurants, options);
        if (ranked.Count == 0)
            return null;

        var winner = ranked[0];
        var pick = restaurants
            .Where(r => r.CategoryId == winner.CategoryId)
            .OrderBy(r => r.LastEatenOn ?? DateOnly.MinValue)   // 미방문 최우선
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.RestaurantId)
            .First();

        return new Recommendation(
            new RestaurantPick(pick.RestaurantId, pick.Name, pick.LastEatenOn),
            winner,
            [.. ranked.Skip(1)]);
    }

    /// <summary>
    /// Active 식당을 가진 카테고리만 점수 내림차순으로 정렬한다.
    /// 이력이 없는 카테고리는 미방문 통계를 만들어 채운다.
    /// </summary>
    static List<CategoryScore> RankCategories(
        DateOnly today,
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        RecommendationOptions options)
    {
        var byId = stats.ToDictionary(s => s.CategoryId);

        var eligible = restaurants
            .Select(r => r.CategoryId)
            .Distinct()
            .Select(id => byId.TryGetValue(id, out var stat)
                ? stat
                : new CategoryStat(id, CategoryNameOf(restaurants, id), null, 0, 0))
            .ToList();

        return [.. RecommendationEngine
            .ScoreCategories(today, eligible, options)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.LastEatenOn ?? DateOnly.MinValue)
            .ThenBy(s => s.CategoryName, StringComparer.Ordinal)];
    }

    /// <summary>이력이 아직 없는 카테고리의 이름은 소속 식당에서 가져온다.</summary>
    static string CategoryNameOf(IReadOnlyList<RestaurantInfo> restaurants, long categoryId) =>
        restaurants.First(r => r.CategoryId == categoryId).CategoryName;
```

정렬 사슬이 `Score → LastEatenOn → CategoryName(ordinal)`로 끝까지 내려가고,
식당은 `LastEatenOn → CreatedAt → RestaurantId`로 유일하게 결정된다.
`RestaurantId`가 유일하므로 이 사슬은 항상 하나의 답에 도달한다.

- [ ] **Step 4: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

`이력이_없는_카테고리의_식당도_추천_대상이_된다`가 실패하면 `RankCategories`의
보강 로직을, `같은_입력이면_항상_같은_결과가_나온다`가 실패하면 tie-break 누락을 의심한다.

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "feat: 결정적 추천 선정 로직 구현"
```

---

### Task 5: 스케줄 모델 + 영업일 판정

스펙 §9의 판정 로직 중 영업일 부분을 먼저 만든다.

**Files:**
- Create: `src/SelectLunch.Shared/Entities/Enums.cs`
- Create: `src/SelectLunch.Shared/Scheduling/ScheduleModels.cs`
- Create: `src/SelectLunch.Shared/Scheduling/LunchSchedule.cs`
- Test: `tests/SelectLunch.Shared.Tests/LunchScheduleTests.cs`

**Interfaces:**
- Consumes: `LunchOptions` (Task 2)
- Produces:
  - `enum RestaurantStatus { Active, Pending, Archived }`
  - `enum PollStatus { Open, Closed, Cancelled }`
  - `enum MealSource { Prompt, NewRegistration, Manual }`
  - `record PollSnapshot(long PollId, PollStatus Status, DateTimeOffset ClosesAt)`
  - `record TodayState(DateOnly Date, PollSnapshot? Poll, bool MealPromptPosted, DateOnly? LastPendingReminderOn, bool HasPendingRestaurants)`
  - `enum DueActionKind { OpenPoll, ClosePoll, PostMealPrompt, RemindPending }`
  - `record DueAction(DueActionKind Kind, DateTimeOffset ScheduledFor)`
  - `static bool LunchSchedule.IsBusinessDay(DateOnly date, LunchOptions options)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Shared.Tests/LunchScheduleTests.cs`:

```csharp
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Shared.Tests;

public class LunchScheduleTests
{
    // 2026-09-18은 금요일, 09-19 토, 09-20 일, 09-21 월
    static readonly DateOnly Friday = new(2026, 9, 18);
    static readonly DateOnly Saturday = new(2026, 9, 19);
    static readonly DateOnly Sunday = new(2026, 9, 20);
    static readonly DateOnly Monday = new(2026, 9, 21);

    [Fact]
    public void 평일은_영업일이다()
    {
        Assert.True(LunchSchedule.IsBusinessDay(Friday, new LunchOptions()));
        Assert.True(LunchSchedule.IsBusinessDay(Monday, new LunchOptions()));
    }

    [Fact]
    public void 주말은_영업일이_아니다()
    {
        var options = new LunchOptions { WeekdaysOnly = true };

        Assert.False(LunchSchedule.IsBusinessDay(Saturday, options));
        Assert.False(LunchSchedule.IsBusinessDay(Sunday, options));
    }

    [Fact]
    public void WeekdaysOnly가_꺼지면_주말도_영업일이다()
    {
        var options = new LunchOptions { WeekdaysOnly = false };

        Assert.True(LunchSchedule.IsBusinessDay(Saturday, options));
    }

    [Fact]
    public void 설정된_공휴일은_평일이어도_영업일이_아니다()
    {
        var options = new LunchOptions { Holidays = [Friday] };

        Assert.False(LunchSchedule.IsBusinessDay(Friday, options));
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `LunchSchedule`을 찾을 수 없음 (CS0246)

- [ ] **Step 3: 열거형과 스케줄 모델 작성**

`src/SelectLunch.Shared/Entities/Enums.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

public enum RestaurantStatus
{
    /// <summary>이름 + 카테고리가 갖춰져 투표·추천에 참여한다.</summary>
    Active = 0,

    /// <summary>이름만 들어온 상태. 이력엔 남지만 투표·추천에서 제외된다.</summary>
    Pending = 1,

    /// <summary>더 이상 쓰지 않지만 과거 기록 해석을 위해 남겨둔다.</summary>
    Archived = 2,
}

public enum PollStatus
{
    Open = 0,
    Closed = 1,
    Cancelled = 2,
}

/// <summary>식사 기록이 어떤 경로로 들어왔는지.</summary>
public enum MealSource
{
    /// <summary>기록 요청 메시지에서 목록 선택.</summary>
    Prompt = 0,

    /// <summary>기록 도중 신규 식당을 등록하며 함께 기록.</summary>
    NewRegistration = 1,

    /// <summary>슬래시 커맨드로 수동 입력·정정.</summary>
    Manual = 2,
}
```

`src/SelectLunch.Shared/Scheduling/ScheduleModels.cs`:

```csharp
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Scheduling;

public sealed record PollSnapshot(
    long PollId,
    PollStatus Status,
    DateTimeOffset ClosesAt);

/// <summary>스케줄 판정에 필요한 오늘치 상태. 조회 계층이 DB에서 읽어 만든다.</summary>
public sealed record TodayState(
    DateOnly Date,
    PollSnapshot? Poll,
    bool MealPromptPosted,
    DateOnly? LastPendingReminderOn,
    bool HasPendingRestaurants);

public enum DueActionKind
{
    OpenPoll,
    ClosePoll,
    PostMealPrompt,
    RemindPending,
}

/// <param name="ScheduledFor">원래 예정 시각. 지각 판정과 로그에 쓴다.</param>
public sealed record DueAction(
    DueActionKind Kind,
    DateTimeOffset ScheduledFor);
```

- [ ] **Step 4: 영업일 판정 구현**

`src/SelectLunch.Shared/Scheduling/LunchSchedule.cs`:

```csharp
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;

namespace SelectLunch.Shared.Scheduling;

/// <summary>
/// 스펙 §9 스케줄 판정. 시계를 인자로 받는 순수 함수이며 DB도 Slack도 모른다.
/// </summary>
public static class LunchSchedule
{
    public static bool IsBusinessDay(DateOnly date, LunchOptions options)
    {
        if (options.WeekdaysOnly && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        return !options.Holidays.Contains(date);
    }
}
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "feat: 영업일 판정과 스케줄 모델 추가"
```

---

### Task 6: GetDueActions — 따라잡기와 지각 방어

스케줄러의 두뇌다. "지금 무엇을 해야 하는가"를 DB 상태와 시각만으로 결정한다.
타이머가 아니라 상태 기반이므로, 앱이 꺼져 있던 구간을 재기동 시 따라잡고
중복 발송도 누락도 생기지 않는다.

**Files:**
- Modify: `src/SelectLunch.Shared/Scheduling/LunchSchedule.cs`
- Modify: `tests/SelectLunch.Shared.Tests/LunchScheduleTests.cs`

**Interfaces:**
- Consumes: `TodayState`, `DueAction`, `LunchOptions`, `PollSnapshot` (Task 5)
- Produces:
  `static IReadOnlyList<DueAction> LunchSchedule.GetDueActions(DateTimeOffset now, TodayState state, LunchOptions options)`

- [ ] **Step 1: 실패하는 테스트 추가**

`LunchScheduleTests.cs`의 클래스 안에 이어 붙인다.

```csharp
    static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    static readonly LunchOptions Default = new();

    static DateTimeOffset At(DateOnly date, int hour, int minute) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, Kst);

    static TodayState State(
        DateOnly date,
        PollSnapshot? poll = null,
        bool mealPromptPosted = false,
        DateOnly? lastReminder = null,
        bool hasPending = false) =>
        new(date, poll, mealPromptPosted, lastReminder, hasPending);

    [Fact]
    public void 개시_시각_전에는_아무것도_하지_않는다()
    {
        var actions = LunchSchedule.GetDueActions(At(Friday, 9, 0), State(Friday), Default);

        Assert.Empty(actions);
    }

    [Fact]
    public void 개시_시각이_되고_투표가_없으면_연다()
    {
        var actions = LunchSchedule.GetDueActions(At(Friday, 10, 30), State(Friday), Default);

        var action = Assert.Single(actions);
        Assert.Equal(DueActionKind.OpenPoll, action.Kind);
        Assert.Equal(At(Friday, 10, 30), action.ScheduledFor);
    }

    [Fact]
    public void 이미_투표가_열려_있으면_다시_열지_않는다()
    {
        var poll = new PollSnapshot(1, PollStatus.Open, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 10, 45), State(Friday, poll), Default);

        Assert.Empty(actions);
    }

    [Fact]
    public void 마감_시각이_지난_열린_투표는_닫는다()
    {
        var poll = new PollSnapshot(1, PollStatus.Open, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 11, 0), State(Friday, poll), Default);

        Assert.Equal(DueActionKind.ClosePoll, Assert.Single(actions).Kind);
    }

    [Fact]
    public void 열린_투표는_아무리_늦어도_반드시_닫는다()
    {
        // 지각 유예(180분)를 한참 넘겼어도 닫기는 건너뛰지 않는다.
        // 투표가 열린 채로 영원히 남으면 다음 날 판정까지 오염된다.
        var poll = new PollSnapshot(1, PollStatus.Open, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 23, 30), State(Friday, poll), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.ClosePoll);
    }
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `GetDueActions` 메서드가 없음 (CS0117)

- [ ] **Step 3: 지각 방어와 기록·리마인더 테스트 추가**

같은 클래스에 이어 붙인다.

```csharp
    [Fact]
    public void 유예_시간을_넘겨_지난_투표_개시는_건너뛴다()
    {
        // 10:30 예정 + 180분 유예 → 13:30 이후엔 열지 않는다
        var actions = LunchSchedule.GetDueActions(At(Friday, 14, 0), State(Friday), Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.OpenPoll);
    }

    [Fact]
    public void 유예_시간_안이면_지난_투표_개시를_따라잡는다()
    {
        var actions = LunchSchedule.GetDueActions(At(Friday, 12, 0), State(Friday), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.OpenPoll);
    }

    [Fact]
    public void 기록_시각이_되면_기록을_요청한다()
    {
        var poll = new PollSnapshot(1, PollStatus.Closed, At(Friday, 11, 0));

        var actions = LunchSchedule.GetDueActions(At(Friday, 13, 30), State(Friday, poll), Default);

        Assert.Equal(DueActionKind.PostMealPrompt, Assert.Single(actions).Kind);
    }

    [Fact]
    public void 투표가_열리지_않았던_날에도_기록은_요청한다()
    {
        // 앱이 오전 내내 꺼져 있어 투표를 놓쳤어도 기록은 받아야 한다.
        // 기록이 비면 추천 알고리즘의 입력이 그만큼 낡는다.
        var actions = LunchSchedule.GetDueActions(At(Friday, 13, 30), State(Friday), Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.PostMealPrompt);
    }

    [Fact]
    public void 이미_기록을_요청했으면_다시_요청하지_않는다()
    {
        var state = State(Friday, mealPromptPosted: true);

        var actions = LunchSchedule.GetDueActions(At(Friday, 15, 0), state, Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.PostMealPrompt);
    }

    [Fact]
    public void 주말에는_아무것도_하지_않는다()
    {
        var actions = LunchSchedule.GetDueActions(At(Saturday, 10, 30), State(Saturday), Default);

        Assert.Empty(actions);
    }

    [Fact]
    public void 공휴일에는_아무것도_하지_않는다()
    {
        var options = new LunchOptions { Holidays = [Friday] };

        var actions = LunchSchedule.GetDueActions(At(Friday, 10, 30), State(Friday), options);

        Assert.Empty(actions);
    }

    [Fact]
    public void 지정_요일에_Pending_식당이_있으면_보완을_요청한다()
    {
        // 기본 설정은 금요일 16:00
        var state = State(Friday, mealPromptPosted: true, hasPending: true);

        var actions = LunchSchedule.GetDueActions(At(Friday, 16, 0), state, Default);

        Assert.Contains(actions, a => a.Kind == DueActionKind.RemindPending);
    }

    [Fact]
    public void Pending_식당이_없으면_보완을_요청하지_않는다()
    {
        var state = State(Friday, mealPromptPosted: true, hasPending: false);

        var actions = LunchSchedule.GetDueActions(At(Friday, 16, 0), state, Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.RemindPending);
    }

    [Fact]
    public void 같은_날_보완_요청을_두_번_보내지_않는다()
    {
        var state = State(Friday, mealPromptPosted: true, lastReminder: Friday, hasPending: true);

        var actions = LunchSchedule.GetDueActions(At(Friday, 16, 30), state, Default);

        Assert.DoesNotContain(actions, a => a.Kind == DueActionKind.RemindPending);
    }

    [Fact]
    public void 오전에_한꺼번에_올라오면_개시와_마감이_함께_나온다()
    {
        // 11:30에 기동 — 아직 투표가 없고 개시 유예 안이며, 개시 직후 마감 시각도 지났다.
        // 개시만 먼저 나오고, 다음 루프에서 마감이 잡힌다.
        var actions = LunchSchedule.GetDueActions(At(Friday, 11, 30), State(Friday), Default);

        Assert.Equal(DueActionKind.OpenPoll, Assert.Single(actions).Kind);
    }
```

- [ ] **Step 4: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `GetDueActions` 미구현

- [ ] **Step 5: GetDueActions 구현**

`LunchSchedule.cs`의 `IsBusinessDay` 아래에 추가한다.

```csharp
    /// <summary>
    /// 지금 실행해야 할 작업을 돌려준다. 호출자는 <paramref name="now"/>를
    /// 설정된 타임존으로 변환해 넘겨야 한다.
    /// </summary>
    public static IReadOnlyList<DueAction> GetDueActions(
        DateTimeOffset now,
        TodayState state,
        LunchOptions options)
    {
        if (!IsBusinessDay(state.Date, options))
            return [];

        var actions = new List<DueAction>();
        var grace = TimeSpan.FromMinutes(options.CatchUpGraceMinutes);

        var voteOpenAt = At(state.Date, options.VoteOpenAt, now.Offset);
        if (state.Poll is null && IsDue(now, voteOpenAt, grace))
            actions.Add(new DueAction(DueActionKind.OpenPoll, voteOpenAt));

        // 마감에는 유예를 두지 않는다. 열린 채 남은 투표는 언제든 닫아야 한다.
        if (state.Poll is { Status: PollStatus.Open } poll && now >= poll.ClosesAt)
            actions.Add(new DueAction(DueActionKind.ClosePoll, poll.ClosesAt));

        var mealPromptAt = At(state.Date, options.MealRecordAt, now.Offset);
        if (!state.MealPromptPosted && IsDue(now, mealPromptAt, grace))
            actions.Add(new DueAction(DueActionKind.PostMealPrompt, mealPromptAt));

        if (ShouldRemindPending(state, options))
        {
            var remindAt = At(state.Date, options.PendingReminder.At, now.Offset);
            if (IsDue(now, remindAt, grace))
                actions.Add(new DueAction(DueActionKind.RemindPending, remindAt));
        }

        return actions;
    }

    static bool ShouldRemindPending(TodayState state, LunchOptions options) =>
        options.PendingReminder.Enabled
        && state.HasPendingRestaurants
        && state.Date.DayOfWeek == options.PendingReminder.DayOfWeek
        && state.LastPendingReminderOn != state.Date;

    /// <summary>예정 시각을 지났고, 유예 시간 안에 있으면 실행 대상이다.</summary>
    static bool IsDue(DateTimeOffset now, DateTimeOffset scheduledFor, TimeSpan grace) =>
        now >= scheduledFor && now - scheduledFor <= grace;

    static DateTimeOffset At(DateOnly date, TimeOnly time, TimeSpan offset) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, offset);
```

`ClosePoll`만 유예 검사를 받지 않는 점이 중요하다. 다른 작업은 늦으면 건너뛰는 게
맞지만, 열린 투표를 닫지 않고 넘기면 그 상태가 다음 날 판정까지 오염시킨다.

- [ ] **Step 6: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 7: 커밋**

```bash
git add -A
git commit -m "feat: 스케줄 due 판정과 재기동 따라잡기 구현"
```

---

### Task 7: 엔티티와 DbContext

스펙 §5의 데이터 모델을 EF Core로 구현한다. 제약 조건(UNIQUE)이 이 task의 핵심이며,
in-memory SQLite로 실제 제약이 걸리는지 검증한다.

**Files:**
- Create: `src/SelectLunch.Shared/Entities/Category.cs`
- Create: `src/SelectLunch.Shared/Entities/Restaurant.cs`
- Create: `src/SelectLunch.Shared/Entities/LunchPoll.cs`
- Create: `src/SelectLunch.Shared/Entities/PollCandidate.cs`
- Create: `src/SelectLunch.Shared/Entities/PollVote.cs`
- Create: `src/SelectLunch.Shared/Entities/MealRecord.cs`
- Create: `src/SelectLunch.Shared/Entities/ChannelDay.cs`
- Create: `src/SelectLunch.Shared/Data/LunchDbContext.cs`
- Create: `tests/SelectLunch.Shared.Tests/TestDb.cs`
- Test: `tests/SelectLunch.Shared.Tests/LunchDbContextTests.cs`
- Modify: `src/SelectLunch.Shared/SelectLunch.Shared.csproj` (EF Core 패키지)
- Modify: `tests/SelectLunch.Shared.Tests/SelectLunch.Shared.Tests.csproj`

**Interfaces:**
- Consumes: `RestaurantStatus`, `PollStatus`, `MealSource` (Task 5)
- Produces:
  - 엔티티 클래스 6종 (아래 Step 2 참조)
  - `LunchDbContext` — `DbSet<Category> Categories`, `DbSet<Restaurant> Restaurants`,
    `DbSet<LunchPoll> Polls`, `DbSet<PollCandidate> PollCandidates`,
    `DbSet<PollVote> PollVotes`, `DbSet<MealRecord> MealRecords`,
    `DbSet<ChannelDay> ChannelDays`
  - `Restaurant.Normalize(string name)` — 중복 판정용 정규화(공백 제거 + 소문자)
  - 테스트 헬퍼 `TestDb.CreateAsync()` → `LunchDbContext` (in-memory SQLite, 연결 유지)

- [ ] **Step 1: 패키지 추가**

```bash
dotnet add src/SelectLunch.Shared package Microsoft.EntityFrameworkCore.Sqlite --version 10.0.12
dotnet add src/SelectLunch.Shared package Microsoft.EntityFrameworkCore.Design --version 10.0.12
```

- [ ] **Step 2: 엔티티 작성**

`src/SelectLunch.Shared/Entities/Category.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

public sealed class Category
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>시드로 들어간 기본 카테고리인지. 사용자 추가분과 구분한다.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<Restaurant> Restaurants { get; set; } = [];
}
```

`src/SelectLunch.Shared/Entities/Restaurant.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

public sealed class Restaurant
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>중복 등록을 막기 위한 정규화 이름. UNIQUE 제약이 걸린다.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>null이면 반드시 <see cref="RestaurantStatus.Pending"/>이다.</summary>
    public long? CategoryId { get; set; }

    public Category? Category { get; set; }

    public RestaurantStatus Status { get; set; } = RestaurantStatus.Pending;

    public int? WalkMinutes { get; set; }

    /// <summary>가격대 1~4단계.</summary>
    public int? PriceLevel { get; set; }

    public string? Note { get; set; }

    public required string CreatedBySlackUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>공백을 지우고 소문자로 바꿔 "서브 웨이"와 "서브웨이"를 같게 본다.</summary>
    public static string Normalize(string name) =>
        string.Concat(name.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
}
```

`src/SelectLunch.Shared/Entities/LunchPoll.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

public sealed class LunchPoll
{
    public long Id { get; set; }

    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>발송된 슬랙 메시지의 ts. 집계 갱신에 쓴다.</summary>
    public string? MessageTs { get; set; }

    public DateTimeOffset OpensAt { get; set; }

    public DateTimeOffset ClosesAt { get; set; }

    public PollStatus Status { get; set; } = PollStatus.Open;

    public long? WinnerRestaurantId { get; set; }

    public long? RecommendedRestaurantId { get; set; }

    /// <summary>추천 근거(카테고리별 점수 스냅샷)의 JSON. 나중에 재현할 수 있게 남긴다.</summary>
    public string? RationaleJson { get; set; }

    public ICollection<PollCandidate> Candidates { get; set; } = [];

    public ICollection<PollVote> Votes { get; set; } = [];
}
```

`src/SelectLunch.Shared/Entities/PollCandidate.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

/// <summary>
/// 투표 시점의 후보 스냅샷. 식당이 나중에 수정·보관되어도
/// 과거 투표의 해석이 깨지지 않게 한다.
/// </summary>
public sealed class PollCandidate
{
    public long PollId { get; set; }

    public LunchPoll? Poll { get; set; }

    public long RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public int DisplayOrder { get; set; }
}
```

`src/SelectLunch.Shared/Entities/PollVote.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

public sealed class PollVote
{
    public long Id { get; set; }

    public long PollId { get; set; }

    public LunchPoll? Poll { get; set; }

    /// <summary>1인 1표. (PollId, SlackUserId)에 UNIQUE가 걸려 변경만 가능하다.</summary>
    public required string SlackUserId { get; set; }

    public long RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public DateTimeOffset VotedAt { get; set; }
}
```

`src/SelectLunch.Shared/Entities/MealRecord.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

/// <summary>채널 단위 하루 1건. 나중에 기록한 사람이 덮어쓴다.</summary>
public sealed class MealRecord
{
    public long Id { get; set; }

    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    public long RestaurantId { get; set; }

    public Restaurant? Restaurant { get; set; }

    public required string RecordedBySlackUserId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public MealSource Source { get; set; }
}
```

`src/SelectLunch.Shared/Entities/ChannelDay.cs`:

```csharp
namespace SelectLunch.Shared.Entities;

/// <summary>
/// 채널의 하루치 발송 이력. "이미 보냈는가"를 여기서 판정한다.
/// 투표가 열리지 않은 날에도 기록 요청은 나가야 하므로,
/// 이 상태를 <see cref="LunchPoll"/>에 둘 수 없다 — 그날 Poll이 없을 수 있다.
/// </summary>
public sealed class ChannelDay
{
    public required string ChannelId { get; set; }

    public DateOnly Date { get; set; }

    public DateTimeOffset? MealPromptPostedAt { get; set; }

    public DateTimeOffset? PendingReminderSentAt { get; set; }
}
```

- [ ] **Step 3: DbContext 작성**

`src/SelectLunch.Shared/Data/LunchDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Data;

public sealed class LunchDbContext(DbContextOptions<LunchDbContext> options)
    : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<LunchPoll> Polls => Set<LunchPoll>();
    public DbSet<PollCandidate> PollCandidates => Set<PollCandidate>();
    public DbSet<PollVote> PollVotes => Set<PollVote>();
    public DbSet<MealRecord> MealRecords => Set<MealRecord>();
    public DbSet<ChannelDay> ChannelDays => Set<ChannelDay>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(50);
        });

        b.Entity<Restaurant>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.NormalizedName).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne(x => x.Category)
                .WithMany(c => c.Restaurants)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LunchPoll>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
        });

        b.Entity<PollCandidate>(e =>
        {
            e.HasKey(x => new { x.PollId, x.RestaurantId });
            e.HasOne(x => x.Poll).WithMany(p => p.Candidates).HasForeignKey(x => x.PollId);
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PollVote>(e =>
        {
            // 1인 1표. 다시 누르면 기존 행을 갱신한다.
            e.HasIndex(x => new { x.PollId, x.SlackUserId }).IsUnique();
            e.HasOne(x => x.Poll).WithMany(p => p.Votes).HasForeignKey(x => x.PollId);
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MealRecord>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Date }).IsUnique();
            e.HasIndex(x => x.Date);   // 추천 알고리즘의 기간 집계용
            e.HasOne(x => x.Restaurant).WithMany().HasForeignKey(x => x.RestaurantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChannelDay>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.Date });
        });
    }
}
```

**`Restaurant`를 가리키는 세 FK에 `Restrict`가 반드시 필요하다.** `RestaurantId`가
non-nullable이라 EF Core 규약상 필수 관계가 되고, 필수 관계의 기본 삭제 동작은
**`Cascade`** 다. 그대로 두면 식당 한 곳을 지울 때 그 식당의 투표와 식사 기록이
함께 사라진다. 식사 이력은 추천 알고리즘의 유일한 입력이고, `PollCandidate`는
과거 투표를 해석 가능하게 유지하려고 존재한다 — 연쇄 삭제는 둘 다 무너뜨린다.

- [ ] **Step 4: 테스트 헬퍼 작성**

`tests/SelectLunch.Shared.Tests/TestDb.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;

namespace SelectLunch.Shared.Tests;

/// <summary>
/// in-memory SQLite. 연결을 열어둔 채로 유지해야 DB가 살아 있다 — 연결이 닫히면 사라진다.
/// 실제 SQLite 엔진을 쓰므로 UNIQUE 제약 같은 것이 진짜로 검증된다.
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    readonly SqliteConnection _connection;

    public LunchDbContext Db { get; }

    TestDb(SqliteConnection connection, LunchDbContext db)
    {
        _connection = connection;
        Db = db;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new LunchDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return new TestDb(connection, db);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
```

- [ ] **Step 5: 제약 조건 테스트 작성**

> **주의:** Task 8이 기본 카테고리 7종(한식·중식·일식·양식·분식·아시안·기타, Id 1~7)을
> `HasData`로 시드한다. `TestDb`는 `EnsureCreatedAsync`를 쓰므로 그 시드가 테스트 DB에도
> 들어간다. **테스트에서 같은 이름의 카테고리를 새로 삽입하면 UNIQUE 제약에 걸린다** —
> 시드된 것을 조회해 쓰고, 중복 제약을 시험할 때는 시드에 없는 이름을 쓴다.

`tests/SelectLunch.Shared.Tests/LunchDbContextTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Tests;

public class LunchDbContextTests
{
    static Restaurant NewRestaurant(string name, long? categoryId = null) => new()
    {
        Name = name,
        NormalizedName = Restaurant.Normalize(name),
        CategoryId = categoryId,
        Status = categoryId is null ? RestaurantStatus.Pending : RestaurantStatus.Active,
        CreatedBySlackUserId = "U1",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task 같은_이름의_식당을_두_번_등록할_수_없다()
    {
        await using var fixture = await TestDb.CreateAsync();
        fixture.Db.Restaurants.Add(NewRestaurant("서브웨이"));
        await fixture.Db.SaveChangesAsync();

        fixture.Db.Restaurants.Add(NewRestaurant("서브웨이"));

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 공백만_다른_이름은_같은_식당으로_본다()
    {
        Assert.Equal(Restaurant.Normalize("서브웨이"), Restaurant.Normalize("서브 웨이"));
    }

    [Fact]
    public async Task 한_사람은_한_투표에_한_표만_가진다()
    {
        await using var fixture = await TestDb.CreateAsync();
        // 카테고리는 마이그레이션 시드로 이미 존재한다(Task 8). 새로 넣지 않고 가져다 쓴다.
        var category = await fixture.Db.Categories.SingleAsync(c => c.Name == "일식");
        var restaurant = NewRestaurant("스시로");
        restaurant.CategoryId = category.Id;
        fixture.Db.Restaurants.Add(restaurant);
        var poll = new LunchPoll
        {
            ChannelId = "C1",
            Date = new DateOnly(2026, 9, 18),
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
        };
        fixture.Db.Polls.Add(poll);
        await fixture.Db.SaveChangesAsync();

        fixture.Db.PollVotes.Add(new PollVote
        {
            PollId = poll.Id, SlackUserId = "U1",
            RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Db.PollVotes.Add(new PollVote
        {
            PollId = poll.Id, SlackUserId = "U1",
            RestaurantId = restaurant.Id, VotedAt = DateTimeOffset.UnixEpoch,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 한_채널의_하루_식사_기록은_하나뿐이다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var restaurant = NewRestaurant("스시로");
        fixture.Db.Restaurants.Add(restaurant);
        await fixture.Db.SaveChangesAsync();

        var date = new DateOnly(2026, 9, 18);
        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = date, RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Db.MealRecords.Add(new MealRecord
        {
            ChannelId = "C1", Date = date, RestaurantId = restaurant.Id,
            RecordedBySlackUserId = "U2", RecordedAt = DateTimeOffset.UnixEpoch,
            Source = MealSource.Prompt,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 채널_하루_상태는_채널과_날짜로_유일하다()
    {
        await using var fixture = await TestDb.CreateAsync();
        var date = new DateOnly(2026, 9, 18);
        fixture.Db.ChannelDays.Add(new ChannelDay { ChannelId = "C1", Date = date });
        await fixture.Db.SaveChangesAsync();

        var found = await fixture.Db.ChannelDays.FindAsync("C1", date);

        Assert.NotNull(found);
    }
}
```

- [ ] **Step 6: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 7: 커밋**

```bash
git add -A
git commit -m "feat: 엔티티와 DbContext 제약 조건 구성"
```

---

### Task 8: 마이그레이션과 기본 카테고리 시드

기동 시 `Migrate()` 한 번으로 스키마가 생기고 기본 카테고리가 채워지게 한다.

**Files:**
- Create: `src/SelectLunch.Shared/Data/DesignTimeDbContextFactory.cs`
- Create: `src/SelectLunch.Shared/Data/Migrations/` (EF 생성물)
- Modify: `src/SelectLunch.Shared/Data/LunchDbContext.cs` (시드 추가)
- Test: `tests/SelectLunch.Shared.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: `LunchDbContext` (Task 7)
- Produces: `dotnet ef`로 생성된 `InitialCreate` 마이그레이션.
  기본 카테고리 7종(한식·중식·일식·양식·분식·아시안·기타)이 `Id` 1~7로 고정 시드된다.

- [ ] **Step 1: 시드 추가**

`LunchDbContext.OnModelCreating`의 `Category` 구성 블록 안에 이어 붙인다.
`HasData`는 고정 `Id`와 고정 `CreatedAt`을 요구한다 — `DateTimeOffset.Now`를 쓰면
마이그레이션이 매번 달라진다.

```csharp
            e.HasData(BuiltInCategories);
```

그리고 클래스 안에 상수를 추가한다.

```csharp
    static readonly DateTimeOffset SeedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>기본 카테고리. Id를 고정해야 마이그레이션이 안정적이다.</summary>
    static readonly Category[] BuiltInCategories =
    [
        new() { Id = 1, Name = "한식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 2, Name = "중식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 3, Name = "일식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 4, Name = "양식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 5, Name = "분식",   IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 6, Name = "아시안", IsBuiltIn = true, CreatedAt = SeedAt },
        new() { Id = 7, Name = "기타",   IsBuiltIn = true, CreatedAt = SeedAt },
    ];
```

- [ ] **Step 2: 디자인 타임 팩토리 작성**

`dotnet ef`는 `SelectLunch.Shared`가 실행 프로젝트가 아니어서 DbContext를 만들 방법을
모른다. 팩토리를 제공해 Shared 단독으로 마이그레이션을 생성할 수 있게 한다.

`src/SelectLunch.Shared/Data/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SelectLunch.Shared.Data;

/// <summary>`dotnet ef` 전용. 런타임에는 쓰이지 않는다.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LunchDbContext>
{
    public LunchDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LunchDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new LunchDbContext(options);
    }
}
```

- [ ] **Step 3: 마이그레이션 생성**

```bash
dotnet ef migrations add InitialCreate \
  --project src/SelectLunch.Shared \
  --output-dir Data/Migrations
```

Expected: `src/SelectLunch.Shared/Data/Migrations/`에 3개 파일 생성
(`*_InitialCreate.cs`, `*_InitialCreate.Designer.cs`, `LunchDbContextModelSnapshot.cs`)

- [ ] **Step 4: 마이그레이션 검증 테스트 작성**

`tests/SelectLunch.Shared.Tests/MigrationTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;

namespace SelectLunch.Shared.Tests;

public class MigrationTests
{
    [Fact]
    public async Task 마이그레이션으로_스키마와_기본_카테고리가_생성된다()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);

        await db.Database.MigrateAsync();

        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(7, categories.Count);
        Assert.Equal("한식", categories[0].Name);
        Assert.All(categories, c => Assert.True(c.IsBuiltIn));
    }

    [Fact]
    public async Task 적용되지_않은_모델_변경이_남아_있지_않다()
    {
        // 엔티티를 고치고 마이그레이션 생성을 잊으면 여기서 잡힌다.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LunchDbContext>().UseSqlite(connection).Options;
        await using var db = new LunchDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "feat: 초기 마이그레이션과 기본 카테고리 시드 추가"
```

---

### Task 9: 조회 계층

순수 함수(`RecommendationEngine`, `LunchSchedule`)가 먹을 입력을 DB에서 만들어 준다.
Shared의 마지막 조각이며, 이 task가 끝나면 Slack head로 넘어간다.

**Files:**
- Create: `src/SelectLunch.Shared/Data/LunchQueries.cs`
- Test: `tests/SelectLunch.Shared.Tests/LunchQueriesTests.cs`

**Interfaces:**
- Consumes: `LunchDbContext` (Task 7), `CategoryStat`/`RestaurantInfo` (Task 3),
  `TodayState`/`PollSnapshot` (Task 5)
- Produces — `LunchDbContext` 확장 메서드:
  - `Task<List<CategoryStat>> GetCategoryStatsAsync(DateOnly today, CancellationToken ct)`
  - `Task<List<RestaurantInfo>> GetActiveRestaurantsAsync(CancellationToken ct)`
  - `Task<TodayState> GetTodayStateAsync(string channelId, DateOnly today, CancellationToken ct)`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Shared.Tests/LunchQueriesTests.cs`:

```csharp
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Tests;

public class LunchQueriesTests
{
    static readonly DateOnly Today = new(2026, 9, 18);
    const string Channel = "C1";

    static async Task<TestDb> SeedAsync()
    {
        var fixture = await TestDb.CreateAsync();
        var db = fixture.Db;

        // 카테고리(1 한식 · 3 일식)는 마이그레이션 시드로 이미 들어 있다(Task 8).
        // 다시 넣으면 Categories.Name UNIQUE 제약에 걸린다 — 시드된 Id를 그대로 참조한다.
        db.Restaurants.AddRange(
            Restaurant(10, "김밥천국", 1, RestaurantStatus.Active),
            Restaurant(20, "스시로", 3, RestaurantStatus.Active),
            Restaurant(30, "이름만아는집", null, RestaurantStatus.Pending));

        await db.SaveChangesAsync();
        return fixture;
    }

    static Restaurant Restaurant(long id, string name, long? categoryId, RestaurantStatus status) => new()
    {
        Id = id, Name = name, NormalizedName = Entities.Restaurant.Normalize(name),
        CategoryId = categoryId, Status = status, CreatedBySlackUserId = "U1",
        CreatedAt = new DateTimeOffset(2026, 1, (int)(id / 10), 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static MealRecord Meal(DateOnly date, long restaurantId) => new()
    {
        ChannelId = Channel, Date = date, RestaurantId = restaurantId,
        RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch,
        Source = MealSource.Prompt,
    };

    [Fact]
    public async Task Active_식당만_추천_후보로_조회된다()
    {
        await using var fixture = await SeedAsync();

        var restaurants = await fixture.Db.GetActiveRestaurantsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, restaurants.Count);
        Assert.DoesNotContain(restaurants, r => r.Name == "이름만아는집");
        Assert.Contains(restaurants, r => r is { Name: "스시로", CategoryName: "일식" });
    }

    [Fact]
    public async Task 식당별_마지막_방문일이_채워진다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.Add(Meal(new DateOnly(2026, 8, 21), 20));
        await fixture.Db.SaveChangesAsync();

        var restaurants = await fixture.Db.GetActiveRestaurantsAsync(TestContext.Current.CancellationToken);

        var 스시로 = restaurants.Single(r => r.Name == "스시로");
        Assert.Equal(new DateOnly(2026, 8, 21), 스시로.LastEatenOn);
        Assert.Null(restaurants.Single(r => r.Name == "김밥천국").LastEatenOn);
    }
```

```csharp
    [Fact]
    public async Task 카테고리_통계는_7일과_30일_횟수를_센다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.AddRange(
            Meal(Today.AddDays(-2), 10),    // 한식, 7일 안
            Meal(Today.AddDays(-5), 10),    // 한식, 7일 안
            Meal(Today.AddDays(-20), 10),   // 한식, 30일 안
            Meal(Today.AddDays(-40), 10));  // 한식, 범위 밖
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        var 한식 = stats.Single(s => s.CategoryName == "한식");
        Assert.Equal(2, 한식.Count7d);
        Assert.Equal(3, 한식.Count30d);
        Assert.Equal(Today.AddDays(-2), 한식.LastEatenOn);
    }

    [Fact]
    public async Task Active_식당이_없는_카테고리는_통계에서_빠진다()
    {
        await using var fixture = await SeedAsync();

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.Count);   // 한식, 일식만
    }

    [Fact]
    public async Task Pending_식당의_기록은_카테고리_통계에_들어가지_않는다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.MealRecords.Add(Meal(Today.AddDays(-1), 30));   // Pending 식당
        await fixture.Db.SaveChangesAsync();

        var stats = await fixture.Db.GetCategoryStatsAsync(Today, TestContext.Current.CancellationToken);

        Assert.All(stats, s => Assert.Equal(0, s.Count7d));
    }

    [Fact]
    public async Task 오늘_상태는_투표와_발송_이력을_함께_읽는다()
    {
        await using var fixture = await SeedAsync();
        fixture.Db.Polls.Add(new LunchPoll
        {
            Id = 1, ChannelId = Channel, Date = Today,
            OpensAt = DateTimeOffset.UnixEpoch,
            ClosesAt = DateTimeOffset.UnixEpoch.AddMinutes(30),
            Status = PollStatus.Open,
        });
        fixture.Db.ChannelDays.Add(new ChannelDay
        {
            ChannelId = Channel, Date = Today,
            MealPromptPostedAt = DateTimeOffset.UnixEpoch,
        });
        await fixture.Db.SaveChangesAsync();

        var state = await fixture.Db.GetTodayStateAsync(Channel, Today, TestContext.Current.CancellationToken);

        Assert.Equal(PollStatus.Open, state.Poll!.Status);
        Assert.True(state.MealPromptPosted);
        Assert.True(state.HasPendingRestaurants);   // "이름만아는집"
        Assert.Null(state.LastPendingReminderOn);
    }

    [Fact]
    public async Task 투표가_없는_날의_오늘_상태는_Poll이_null이다()
    {
        await using var fixture = await SeedAsync();

        var state = await fixture.Db.GetTodayStateAsync(Channel, Today, TestContext.Current.CancellationToken);

        Assert.Null(state.Poll);
        Assert.False(state.MealPromptPosted);
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `GetActiveRestaurantsAsync` 등이 없음 (CS1061)

- [ ] **Step 3: 조회 계층 구현**

`src/SelectLunch.Shared/Data/LunchQueries.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Shared.Scheduling;

namespace SelectLunch.Shared.Data;

/// <summary>순수 함수들이 먹을 입력을 DB에서 만들어 주는 조회 모음.</summary>
public static class LunchQueries
{
    /// <summary>추천 후보가 되는 Active 식당과 각각의 마지막 방문일.</summary>
    public static async Task<List<RestaurantInfo>> GetActiveRestaurantsAsync(
        this LunchDbContext db,
        CancellationToken ct)
    {
        var lastEaten = await db.MealRecords
            .GroupBy(m => m.RestaurantId)
            .Select(g => new { RestaurantId = g.Key, Last = g.Max(m => m.Date) })
            .ToDictionaryAsync(x => x.RestaurantId, x => x.Last, ct);

        var restaurants = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Active && r.CategoryId != null)
            .Select(r => new
            {
                r.Id, r.Name, CategoryId = r.CategoryId!.Value,
                CategoryName = r.Category!.Name, r.CreatedAt,
            })
            .ToListAsync(ct);

        return [.. restaurants.Select(r => new RestaurantInfo(
            r.Id, r.Name, r.CategoryId, r.CategoryName,
            lastEaten.TryGetValue(r.Id, out var last) ? last : null,
            r.CreatedAt))];
    }

    /// <summary>
    /// Active 식당을 가진 카테고리별 식사 이력 집계.
    /// Pending 식당의 기록은 카테고리가 없으므로 자연히 빠진다.
    /// </summary>
    public static async Task<List<CategoryStat>> GetCategoryStatsAsync(
        this LunchDbContext db,
        DateOnly today,
        CancellationToken ct)
    {
        var from7 = today.AddDays(-6);     // 오늘 포함 7일
        var from30 = today.AddDays(-29);   // 오늘 포함 30일

        var categories = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Active && r.CategoryId != null)
            .Select(r => new { Id = r.CategoryId!.Value, Name = r.Category!.Name })
            .Distinct()
            .ToListAsync(ct);

        var history = await db.MealRecords
            .Where(m => m.Restaurant!.CategoryId != null)
            .Select(m => new { CategoryId = m.Restaurant!.CategoryId!.Value, m.Date })
            .ToListAsync(ct);

        return [.. categories.Select(c =>
        {
            var rows = history.Where(h => h.CategoryId == c.Id).ToList();
            return new CategoryStat(
                c.Id,
                c.Name,
                rows.Count == 0 ? null : rows.Max(h => h.Date),
                rows.Count(h => h.Date >= from7 && h.Date <= today),
                rows.Count(h => h.Date >= from30 && h.Date <= today));
        })];
    }

    /// <summary>스케줄 판정에 필요한 오늘치 상태.</summary>
    public static async Task<TodayState> GetTodayStateAsync(
        this LunchDbContext db,
        string channelId,
        DateOnly today,
        CancellationToken ct)
    {
        var poll = await db.Polls
            .Where(p => p.ChannelId == channelId && p.Date == today)
            .Select(p => new PollSnapshot(p.Id, p.Status, p.ClosesAt))
            .SingleOrDefaultAsync(ct);

        var day = await db.ChannelDays
            .SingleOrDefaultAsync(d => d.ChannelId == channelId && d.Date == today, ct);

        var hasPending = await db.Restaurants
            .AnyAsync(r => r.Status == RestaurantStatus.Pending, ct);

        return new TodayState(
            today,
            poll,
            day?.MealPromptPostedAt is not null,
            day?.PendingReminderSentAt is null ? null : today,
            hasPending);
    }
}
```

집계를 메모리에서 도는 이유: 식사 기록은 하루 1건이라 1년치가 250행 남짓이다.
SQL로 기간별 조건부 집계를 짜는 것보다 읽기 쉽고, 이 규모에서는 비용 차이가 없다.

- [ ] **Step 4: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "feat: 추천·스케줄 입력을 만드는 조회 계층 추가"
```

---

### Task 10: Slack 프로젝트 골격

Shared를 참조하는 Worker Service를 세우고, 설정 로딩과 단일 인스턴스 보장을 붙인다.
아직 슬랙에 붙지는 않는다 — 다음 task들이 블록과 핸들러를 채운 뒤 Task 16에서 연결한다.

**Files:**
- Create: `src/SelectLunch.Slack/SelectLunch.Slack.csproj`
- Create: `src/SelectLunch.Slack/Options/SlackOptions.cs`
- Create: `src/SelectLunch.Slack/SingleInstanceGuard.cs`
- Create: `src/SelectLunch.Slack/appsettings.json`
- Create: `src/SelectLunch.Slack/Program.cs`
- Create: `tests/SelectLunch.Slack.Tests/SelectLunch.Slack.Tests.csproj`
- Test: `tests/SelectLunch.Slack.Tests/SlackOptionsTests.cs`

**Interfaces:**
- Consumes: `LunchOptions` (Task 2), `LunchDbContext` (Task 7)
- Produces:
  - `SlackOptions { string BotToken; string AppToken; string ChannelId; }`,
    상수 `SlackOptions.SectionName` = `"Slack"`, `Validate()` → 누락 시 예외
  - `SingleInstanceGuard.TryAcquire(out SingleInstanceGuard? guard)` — 이름 있는 뮤텍스

- [ ] **Step 1: 프로젝트 생성**

```bash
dotnet new worker -o src/SelectLunch.Slack -n SelectLunch.Slack
rm src/SelectLunch.Slack/Worker.cs
dotnet sln add src/SelectLunch.Slack/SelectLunch.Slack.csproj
dotnet add src/SelectLunch.Slack reference src/SelectLunch.Shared
dotnet add src/SelectLunch.Slack package SlackNet --version 0.18.0
dotnet add src/SelectLunch.Slack package SlackNet.Extensions.DependencyInjection --version 0.18.0
```

`SelectLunch.Slack.csproj`의 `TargetFramework`/`Nullable` 줄은 `Directory.Build.props`와
중복되므로 지운다.

- [ ] **Step 2: 테스트 프로젝트 생성**

`tests/SelectLunch.Slack.Tests/SelectLunch.Slack.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SelectLunch.Slack/SelectLunch.Slack.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln add tests/SelectLunch.Slack.Tests/SelectLunch.Slack.Tests.csproj
```

- [ ] **Step 3: 실패하는 테스트 작성**

`tests/SelectLunch.Slack.Tests/SlackOptionsTests.cs`:

```csharp
using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Tests;

public class SlackOptionsTests
{
    static SlackOptions Valid() => new()
    {
        BotToken = "xoxb-test", AppToken = "xapp-test", ChannelId = "C0TEST",
    };

    [Fact]
    public void 모든_값이_채워지면_검증을_통과한다()
    {
        Valid().Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 봇_토큰이_비면_예외를_던진다(string token)
    {
        var options = Valid();
        options.BotToken = token;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("BotToken", ex.Message);
    }

    [Fact]
    public void 채널_아이디가_비면_예외를_던진다()
    {
        var options = Valid();
        options.ChannelId = "";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
```

- [ ] **Step 4: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `SelectLunch.Slack.Options`를 찾을 수 없음 (CS0246)

- [ ] **Step 5: 설정과 단일 인스턴스 가드 구현**

`src/SelectLunch.Slack/Options/SlackOptions.cs`:

```csharp
namespace SelectLunch.Slack.Options;

/// <summary>
/// 토큰과 채널 ID. appsettings.Local.json 또는 환경변수로만 주입하며 커밋하지 않는다.
/// 이 값들은 핫리로드 대상이 아니다 — 바뀌면 웹소켓을 다시 맺어야 한다.
/// </summary>
public sealed class SlackOptions
{
    public const string SectionName = "Slack";

    public string BotToken { get; set; } = "";

    public string AppToken { get; set; } = "";

    /// <summary>봇이 동작할 잠금 채널의 ID (`C`로 시작).</summary>
    public string ChannelId { get; set; } = "";

    public void Validate()
    {
        Require(BotToken, nameof(BotToken), "xoxb-");
        Require(AppToken, nameof(AppToken), "xapp-");
        Require(ChannelId, nameof(ChannelId), null);
    }

    static void Require(string value, string name, string? expectedPrefix)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Slack:{name} 설정이 비어 있습니다. appsettings.Local.json 또는 환경변수로 지정하세요.");

        if (expectedPrefix is not null && !value.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Slack:{name} 값이 '{expectedPrefix}'로 시작하지 않습니다.");
    }
}
```

`src/SelectLunch.Slack/SingleInstanceGuard.cs`:

```csharp
namespace SelectLunch.Slack;

/// <summary>
/// 같은 PC에서 두 번 뜨는 것을 막는다. 중복 기동하면 자동 메시지가 두 번 나가고
/// 투표 집계가 갈라진다.
///
/// <para>크래시로 프로세스가 죽어도 다음 기동은 깨끗이 획득한다 — 마지막 핸들이
/// 닫히면 OS가 named 커널 객체를 파기하기 때문이다. <c>WaitOne</c>으로 대기하지
/// 않으므로 <see cref="AbandonedMutexException"/>이 발생할 지점 자체가 없다.
/// 블로킹 대기를 도입하면 이 성질이 깨진다.</para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    readonly Mutex _mutex;

    SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        // "Global\\" 접두사로 세션 경계를 넘어 배제한다. 백슬래시는 반드시 이스케이프할 것.
        var mutex = new Mutex(initiallyOwned: true, $"Global\\SelectLunch-{name}", out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
```

- [ ] **Step 6: 가드 테스트 추가**

`tests/SelectLunch.Slack.Tests/SingleInstanceGuardTests.cs`:

```csharp
namespace SelectLunch.Slack.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void 두_번째_획득은_실패한다()
    {
        var name = $"test-{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(name, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void 해제_후에는_다시_획득할_수_있다()
    {
        var name = $"test-{Guid.NewGuid():N}";

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        first!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var second));
        second!.Dispose();
    }
}
```

- [ ] **Step 7: appsettings.json 작성**

`src/SelectLunch.Slack/appsettings.json` — 스펙 §10 그대로. 토큰은 넣지 않는다.

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.Hosting.Lifetime": "Information" }
  },
  "Lunch": {
    "TimeZone": "Asia/Seoul",
    "VoteOpenAt": "10:30",
    "VoteDurationMinutes": 30,
    "MealRecordAt": "13:30",
    "WeekdaysOnly": true,
    "CatchUpGraceMinutes": 180,
    "PollIntervalSeconds": 30,
    "Holidays": [],
    "PendingReminder": { "Enabled": true, "DayOfWeek": "Friday", "At": "16:00" },
    "Recommendation": { "DaysSinceCap": 30, "Weight7d": 3, "Weight30d": 1 }
  },
  "Database": { "Path": "data/lunch.db" }
}
```

- [ ] **Step 8: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 9: 커밋**

```bash
git add -A
git commit -m "feat: Slack head 골격과 설정 검증 추가"
```

---

### Task 11: action_id 규약과 투표 메시지 블록

스펙 §8의 `action_id` 문자열을 타입 안전하게 다루고, 투표 메시지를 그린다.
식당 수에 따라 버튼과 드롭다운을 갈아 끼우는 것이 핵심이다.

**Files:**
- Create: `src/SelectLunch.Slack/Blocks/ActionIds.cs`
- Create: `src/SelectLunch.Slack/Blocks/PollBlocks.cs`
- Test: `tests/SelectLunch.Slack.Tests/ActionIdsTests.cs`
- Test: `tests/SelectLunch.Slack.Tests/PollBlocksTests.cs`

**Interfaces:**
- Consumes: `RestaurantInfo` (Task 3)
- Produces:
  - `ActionIds.Vote(long pollId, long restaurantId)` / `ActionIds.TryParseVote(string, out long pollId, out long restaurantId)`
  - `ActionIds.VoteSelect(long pollId)` / `TryParseVoteSelect`
  - `ActionIds.Meal(DateOnly date, long restaurantId)` / `TryParseMeal`
  - `ActionIds.MealNew(DateOnly date)` / `TryParseMealNew`
  - `ActionIds.RestaurantFill(long restaurantId)` / `TryParseRestaurantFill`
  - `record VoteTally(long RestaurantId, string Name, int Count)`
  - `PollBlocks.Build(long pollId, IReadOnlyList<RestaurantInfo> candidates, IReadOnlyList<VoteTally> tallies, DateTimeOffset closesAt)` → `IList<Block>`
  - 상수 `PollBlocks.ButtonThreshold` = 10

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Slack.Tests/ActionIdsTests.cs`:

```csharp
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Tests;

public class ActionIdsTests
{
    [Fact]
    public void 투표_action_id를_왕복_변환한다()
    {
        var id = ActionIds.Vote(pollId: 12, restaurantId: 34);

        Assert.True(ActionIds.TryParseVote(id, out var pollId, out var restaurantId));
        Assert.Equal(12, pollId);
        Assert.Equal(34, restaurantId);
    }

    [Fact]
    public void 기록_action_id를_왕복_변환한다()
    {
        var date = new DateOnly(2026, 9, 18);

        var id = ActionIds.Meal(date, restaurantId: 7);

        Assert.True(ActionIds.TryParseMeal(id, out var parsedDate, out var restaurantId));
        Assert.Equal(date, parsedDate);
        Assert.Equal(7, restaurantId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vote")]
    [InlineData("vote:abc:34")]
    [InlineData("meal:2026-09-18:7")]
    public void 형식이_다르면_투표_파싱에_실패한다(string id)
    {
        Assert.False(ActionIds.TryParseVote(id, out _, out _));
    }

    [Fact]
    public void 서로_다른_종류의_action_id는_섞이지_않는다()
    {
        var mealNew = ActionIds.MealNew(new DateOnly(2026, 9, 18));

        Assert.False(ActionIds.TryParseMeal(mealNew, out _, out _));
        Assert.True(ActionIds.TryParseMealNew(mealNew, out _));
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `ActionIds`를 찾을 수 없음 (CS0246)

- [ ] **Step 3: ActionIds 구현**

`src/SelectLunch.Slack/Blocks/ActionIds.cs`:

```csharp
using System.Globalization;

namespace SelectLunch.Slack.Blocks;

/// <summary>
/// 스펙 §8의 action_id 규약. 슬랙은 문자열만 돌려주므로
/// 생성과 해석을 한곳에 모아 오탈자를 막는다.
/// </summary>
public static class ActionIds
{
    const string DateFormat = "yyyyMMdd";

    public static string Vote(long pollId, long restaurantId) => $"vote:{pollId}:{restaurantId}";

    public static string VoteSelect(long pollId) => $"vote_select:{pollId}";

    public static string Meal(DateOnly date, long restaurantId) =>
        $"meal:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}:{restaurantId}";

    public static string MealNew(DateOnly date) =>
        $"meal_new:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}";

    public static string RestaurantFill(long restaurantId) => $"restaurant_fill:{restaurantId}";

    public static bool TryParseVote(string actionId, out long pollId, out long restaurantId) =>
        TryParseTwoLongs(actionId, "vote", out pollId, out restaurantId);

    public static bool TryParseVoteSelect(string actionId, out long pollId) =>
        TryParseOneLong(actionId, "vote_select", out pollId);

    public static bool TryParseRestaurantFill(string actionId, out long restaurantId) =>
        TryParseOneLong(actionId, "restaurant_fill", out restaurantId);

    public static bool TryParseMeal(string actionId, out DateOnly date, out long restaurantId)
    {
        date = default;
        restaurantId = 0;

        var parts = Split(actionId, "meal", 3);
        return parts is not null
            && TryDate(parts[1], out date)
            && long.TryParse(parts[2], CultureInfo.InvariantCulture, out restaurantId);
    }

    public static bool TryParseMealNew(string actionId, out DateOnly date)
    {
        date = default;

        var parts = Split(actionId, "meal_new", 2);
        return parts is not null && TryDate(parts[1], out date);
    }

    static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    static string[]? Split(string actionId, string prefix, int expectedParts)
    {
        var parts = actionId.Split(':');
        return parts.Length == expectedParts && parts[0] == prefix ? parts : null;
    }

    static bool TryParseOneLong(string actionId, string prefix, out long value)
    {
        value = 0;
        var parts = Split(actionId, prefix, 2);
        return parts is not null && long.TryParse(parts[1], CultureInfo.InvariantCulture, out value);
    }

    static bool TryParseTwoLongs(string actionId, string prefix, out long first, out long second)
    {
        first = 0;
        second = 0;
        var parts = Split(actionId, prefix, 3);
        return parts is not null
            && long.TryParse(parts[1], CultureInfo.InvariantCulture, out first)
            && long.TryParse(parts[2], CultureInfo.InvariantCulture, out second);
    }
}
```

`meal`의 날짜를 `yyyyMMdd`로 쓰는 이유: `yyyy-MM-dd`는 하이픈이 있어 콜론 분리와
섞이지는 않지만, 자릿수가 고정된 형식이 파싱 실패를 더 분명하게 만든다.
테스트 `형식이_다르면_투표_파싱에_실패한다`의 `"meal:2026-09-18:7"` 케이스가 이를 지킨다.

- [ ] **Step 4: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 5: 투표 블록 테스트 작성**

`tests/SelectLunch.Slack.Tests/PollBlocksTests.cs`:

```csharp
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class PollBlocksTests
{
    static readonly DateTimeOffset ClosesAt =
        new(2026, 9, 18, 11, 0, 0, TimeSpan.FromHours(9));

    static IReadOnlyList<RestaurantInfo> Candidates(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new RestaurantInfo(
            i, $"식당{i}", 1 + i % 3, $"카테고리{1 + i % 3}", null,
            DateTimeOffset.UnixEpoch))];

    [Fact]
    public void 식당이_적으면_버튼으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], ClosesAt);

        var actions = blocks.OfType<ActionsBlock>().Single();
        Assert.Equal(3, actions.Elements.OfType<Button>().Count());
    }

    [Fact]
    public void 식당이_많으면_드롭다운으로_그린다()
    {
        var blocks = PollBlocks.Build(1, Candidates(PollBlocks.ButtonThreshold + 1), [], ClosesAt);

        var actions = blocks.OfType<ActionsBlock>().Single();
        Assert.Empty(actions.Elements.OfType<Button>());
        Assert.Single(actions.Elements.OfType<StaticSelectMenu>());
    }

    [Fact]
    public void 드롭다운은_카테고리별로_묶인다()
    {
        var blocks = PollBlocks.Build(1, Candidates(12), [], ClosesAt);

        var menu = blocks.OfType<ActionsBlock>().Single().Elements.OfType<StaticSelectMenu>().Single();
        Assert.Equal(3, menu.OptionGroups.Count);
    }

    [Fact]
    public void 버튼의_action_id는_투표_규약을_따른다()
    {
        var blocks = PollBlocks.Build(pollId: 42, Candidates(1), [], ClosesAt);

        var button = blocks.OfType<ActionsBlock>().Single().Elements.OfType<Button>().Single();
        Assert.True(ActionIds.TryParseVote(button.ActionId, out var pollId, out var restaurantId));
        Assert.Equal(42, pollId);
        Assert.Equal(1, restaurantId);
    }

    [Fact]
    public void 집계가_있으면_득표수를_표시한다()
    {
        VoteTally[] tallies = [new(1, "식당1", 3), new(2, "식당2", 1)];

        var blocks = PollBlocks.Build(1, Candidates(3), tallies, ClosesAt);

        var text = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? ""));
        Assert.Contains("식당1", text);
        Assert.Contains("3표", text);
    }

    [Fact]
    public void 아무도_투표하지_않으면_안내_문구를_보여준다()
    {
        var blocks = PollBlocks.Build(1, Candidates(3), [], ClosesAt);

        var text = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? ""));
        Assert.Contains("아직 투표가 없습니다", text);
    }

    [Fact]
    public void 후보가_없으면_등록을_안내한다()
    {
        var blocks = PollBlocks.Build(1, [], [], ClosesAt);

        Assert.Empty(blocks.OfType<ActionsBlock>());
        var text = string.Join("\n", blocks.OfType<SectionBlock>()
            .Select(s => (s.Text as Markdown)?.Text ?? ""));
        Assert.Contains("/lunch add", text);
    }
}
```

- [ ] **Step 6: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `PollBlocks`를 찾을 수 없음 (CS0246)

- [ ] **Step 7: 투표 블록 구현**

`src/SelectLunch.Slack/Blocks/PollBlocks.cs`:

```csharp
using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

public sealed record VoteTally(long RestaurantId, string Name, int Count);

/// <summary>투표 메시지. 후보 수에 따라 버튼과 드롭다운을 갈아 끼운다.</summary>
public static class PollBlocks
{
    /// <summary>이 수를 넘으면 버튼 대신 드롭다운을 쓴다. 가독성과 25개 제한 때문이다.</summary>
    public const int ButtonThreshold = 10;

    public static IList<Block> Build(
        long pollId,
        IReadOnlyList<RestaurantInfo> candidates,
        IReadOnlyList<VoteTally> tallies,
        DateTimeOffset closesAt)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍚 오늘 점심 뭐 먹지?") },
        };

        if (candidates.Count == 0)
        {
            blocks.Add(Section("등록된 식당이 없습니다. `/lunch add` 로 먼저 등록해 주세요."));
            return blocks;
        }

        blocks.Add(Section(TallyText(candidates, tallies)));
        blocks.Add(candidates.Count <= ButtonThreshold
            ? ButtonActions(pollId, candidates)
            : SelectActions(pollId, candidates));
        blocks.Add(new ContextBlock
        {
            Elements = { new Markdown($"{closesAt:HH:mm}에 마감됩니다 · 한 사람당 한 표, 변경 가능") },
        });

        return blocks;
    }

    static ActionsBlock ButtonActions(long pollId, IReadOnlyList<RestaurantInfo> candidates)
    {
        var actions = new ActionsBlock();

        foreach (var candidate in candidates)
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.Vote(pollId, candidate.RestaurantId),
                Text = new PlainText(candidate.Name),
                Value = candidate.RestaurantId.ToString(),
            });
        }

        return actions;
    }

    static ActionsBlock SelectActions(long pollId, IReadOnlyList<RestaurantInfo> candidates)
    {
        var menu = new StaticSelectMenu
        {
            ActionId = ActionIds.VoteSelect(pollId),
            Placeholder = new PlainText("식당을 고르세요"),
        };

        foreach (var group in candidates.GroupBy(c => c.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var optionGroup = new OptionGroup
            {
                Label = new PlainText(group.Key),
                Options = [],   // SlackNet은 이 컬렉션을 자동 초기화하지 않는다
            };

            foreach (var candidate in group.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                optionGroup.Options.Add(new Option
                {
                    Text = new PlainText(candidate.Name),
                    Value = candidate.RestaurantId.ToString(),
                });
            }

            menu.OptionGroups.Add(optionGroup);
        }

        return new ActionsBlock { Elements = { menu } };
    }

    static string TallyText(IReadOnlyList<RestaurantInfo> candidates, IReadOnlyList<VoteTally> tallies)
    {
        if (tallies.Count == 0)
            return $"후보 {candidates.Count}곳 · 아직 투표가 없습니다.";

        var lines = tallies
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"• *{t.Name}* — {t.Count}표");

        return $"후보 {candidates.Count}곳 · 현재 집계\n{string.Join("\n", lines)}";
    }

    static SectionBlock Section(string markdown) => new() { Text = new Markdown(markdown) };
}
```

- [ ] **Step 8: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

`드롭다운은_카테고리별로_묶인다`가 실패하면 `Candidates(12)`가 만드는 카테고리 수
(`1 + i % 3` → 3종)를 확인한다.

- [ ] **Step 9: 커밋**

```bash
git add -A
git commit -m "feat: action_id 규약과 투표 메시지 블록 추가"
```

---

### Task 12: 마감 결과 블록 — 추천 근거 공개

스펙 §7의 "결과가 아니라 계산 과정"을 구현한다. 사용자가 명시적으로 요구한 기능이므로
점수 계산의 전 과정이 메시지에 드러나야 한다.

**Files:**
- Create: `src/SelectLunch.Slack/Blocks/ResultBlocks.cs`
- Test: `tests/SelectLunch.Slack.Tests/ResultBlocksTests.cs`

**Interfaces:**
- Consumes: `Recommendation`, `CategoryScore`, `RestaurantPick` (Task 3~4),
  `RecommendationOptions` (Task 2), `VoteTally` (Task 11)
- Produces:
  - `record PollOutcome(VoteTally? Winner, IReadOnlyList<VoteTally> Tallies, Recommendation? Recommendation)`
  - `ResultBlocks.Build(PollOutcome outcome, RecommendationOptions options)` → `IList<Block>`
  - `ResultBlocks.Rationale(Recommendation recommendation, RecommendationOptions options)` → `string`
    (`LunchPoll.RationaleJson`과는 별개인 사람이 읽는 설명)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Slack.Tests/ResultBlocksTests.cs`:

```csharp
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class ResultBlocksTests
{
    static readonly RecommendationOptions Options = new();

    static Recommendation Sample() => new(
        new RestaurantPick(20, "스시로", new DateOnly(2026, 8, 21)),
        new CategoryScore(3, "일식", new DateOnly(2026, 9, 4), 14, 0, 1, 13),
        [
            new CategoryScore(5, "분식", new DateOnly(2026, 9, 7), 11, 0, 0, 11),
            new CategoryScore(1, "한식", new DateOnly(2026, 9, 15), 3, 2, 5, -8),
        ]);

    static string TextOf(IList<Block> blocks) =>
        string.Join("\n", blocks.OfType<SectionBlock>().Select(s => (s.Text as Markdown)?.Text ?? "")
            .Concat(blocks.OfType<ContextBlock>().SelectMany(c => c.Elements.OfType<Markdown>().Select(m => m.Text))));

    [Fact]
    public void 추천_근거에_점수_계산_과정이_모두_드러난다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("13점", text);          // 최종 점수
        Assert.Contains("14일", text);          // 경과일 D
        Assert.Contains("최근 7일", text);       // N7
        Assert.Contains("최근 30일", text);      // N30
        Assert.Contains("14 −", text);          // 실제 뺄셈 식
    }

    [Fact]
    public void 경쟁_카테고리의_점수도_함께_보여준다()
    {
        var text = ResultBlocks.Rationale(Sample(), Options);

        Assert.Contains("분식", text);
        Assert.Contains("11점", text);
        Assert.Contains("한식", text);
    }

    [Fact]
    public void 투표_결과와_추천을_두_갈래로_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(10, "김밥천국", 4),
            [new VoteTally(10, "김밥천국", 4), new VoteTally(20, "스시로", 1)],
            Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("김밥천국", text);
        Assert.Contains("스시로", text);
        Assert.Contains("투표 1위", text);
        Assert.Contains("앱 추천", text);
    }

    [Fact]
    public void 투표_1위와_추천이_같으면_하나로_합쳐_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(20, "스시로", 3),
            [new VoteTally(20, "스시로", 3)],
            Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("투표와 추천이 일치", text);
        Assert.DoesNotContain("투표 1위", text);
    }

    [Fact]
    public void 아무도_투표하지_않으면_추천만_보여준다()
    {
        var outcome = new PollOutcome(null, [], Sample());

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("투표가 없었습니다", text);
        Assert.Contains("스시로", text);
    }

    [Fact]
    public void 추천할_식당이_없어도_투표_결과는_보여준다()
    {
        var outcome = new PollOutcome(
            new VoteTally(10, "김밥천국", 2),
            [new VoteTally(10, "김밥천국", 2)],
            Recommendation: null);

        var text = TextOf(ResultBlocks.Build(outcome, Options));

        Assert.Contains("김밥천국", text);
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `ResultBlocks`를 찾을 수 없음 (CS0246)

- [ ] **Step 3: 결과 블록 구현**

`src/SelectLunch.Slack/Blocks/ResultBlocks.cs`:

```csharp
using System.Text;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

public sealed record PollOutcome(
    VoteTally? Winner,
    IReadOnlyList<VoteTally> Tallies,
    Recommendation? Recommendation);

/// <summary>마감 결과. 추천은 답만이 아니라 계산 과정을 함께 낸다.</summary>
public static class ResultBlocks
{
    public static IList<Block> Build(PollOutcome outcome, RecommendationOptions options)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍽️ 오늘 점심 투표 결과") },
        };

        var sameChoice = outcome.Winner is not null
            && outcome.Recommendation is not null
            && outcome.Winner.RestaurantId == outcome.Recommendation.Pick.RestaurantId;

        if (sameChoice)
        {
            blocks.Add(Section(
                $"🎯 *투표와 추천이 일치했습니다* — *{outcome.Winner!.Name}* ({outcome.Winner.Count}표)"));
        }
        else
        {
            blocks.Add(Section(outcome.Winner is null
                ? "🗳️ *투표 1위* — 투표가 없었습니다."
                : $"🗳️ *투표 1위* — *{outcome.Winner.Name}* ({outcome.Winner.Count}표)"));

            if (outcome.Recommendation is { } recommendation)
            {
                blocks.Add(Section(
                    $"🤖 *앱 추천* — *{recommendation.Pick.Name}* ({recommendation.Winner.CategoryName})"));
            }
        }

        if (outcome.Tallies.Count > 1)
            blocks.Add(Section(TallyDetail(outcome.Tallies)));

        if (outcome.Recommendation is { } rec)
        {
            blocks.Add(new DividerBlock());
            blocks.Add(Section(Rationale(rec, options)));
        }

        return blocks;
    }

    /// <summary>사람이 읽는 선정 근거. 알고리즘이 블랙박스가 되지 않게 한다.</summary>
    public static string Rationale(Recommendation recommendation, RecommendationOptions options)
    {
        var w = recommendation.Winner;
        var sb = new StringBuilder();

        sb.AppendLine($"*{w.CategoryName}이(가) 선정된 이유 — 점수 {w.Score}점 (1위)*");
        sb.AppendLine($"• 마지막 방문 {Format(w.LastEatenOn)} → {w.DaysSince}일 경과  `D = {w.DaysSince}`");
        sb.AppendLine($"• 최근 7일 {w.Count7d}회  `−{options.Weight7d} × {w.Count7d} = −{options.Weight7d * w.Count7d}`");
        sb.AppendLine($"• 최근 30일 {w.Count30d}회  `−{options.Weight30d} × {w.Count30d} = −{options.Weight30d * w.Count30d}`");
        sb.AppendLine($"• `{w.DaysSince} − {options.Weight7d * w.Count7d} − {options.Weight30d * w.Count30d} = {w.Score}점`");

        if (recommendation.Others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("*경쟁 카테고리*");
            foreach (var other in recommendation.Others)
            {
                sb.AppendLine(
                    $"• {other.CategoryName} {other.Score}점 " +
                    $"({other.DaysSince}일 전, 7일내 {other.Count7d}회, 30일내 {other.Count30d}회)");
            }
        }

        sb.AppendLine();
        sb.Append(
            $"{w.CategoryName} 중 *{recommendation.Pick.Name}* — " +
            $"마지막 방문 {Format(recommendation.Pick.LastEatenOn)}으로 가장 오래됨");

        return sb.ToString();
    }

    static string TallyDetail(IReadOnlyList<VoteTally> tallies)
    {
        var lines = tallies
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"• {t.Name} — {t.Count}표");

        return $"*전체 집계*\n{string.Join("\n", lines)}";
    }

    static string Format(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "기록 없음";

    static SectionBlock Section(string markdown) => new() { Text = new Markdown(markdown) };
}
```

- [ ] **Step 4: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "feat: 마감 결과와 추천 근거 렌더링 추가"
```

---

### Task 13: 기록 요청 블록과 등록 모달

스펙 §6의 13:30 기록 흐름을 그린다. "목록에서 선택" 또는 "신규 식당 등록"
두 갈래가 한 메시지에 있어야 한다.

**Files:**
- Create: `src/SelectLunch.Slack/Blocks/MealPromptBlocks.cs`
- Create: `src/SelectLunch.Slack/Blocks/RestaurantModal.cs`
- Test: `tests/SelectLunch.Slack.Tests/MealPromptBlocksTests.cs`
- Test: `tests/SelectLunch.Slack.Tests/RestaurantModalTests.cs`

**Interfaces:**
- Consumes: `ActionIds` (Task 11), `RestaurantInfo` (Task 3), `Category` (Task 7)
- Produces:
  - `MealPromptBlocks.Build(DateOnly date, IReadOnlyList<RestaurantInfo> restaurants, string? recordedName)` → `IList<Block>`
  - `RestaurantModal.CallbackId` = `"restaurant_form"`
  - `record ModalContext(DateOnly? RecordFor, long? RestaurantId)` — `None` / `ForRecord(date)` / `ForEdit(id)` / `Serialize()` / `Parse(string?)`
  - `RestaurantModal.Build(IReadOnlyList<Category> categories, RestaurantDraft? existing, ModalContext context)` → `ModalViewDefinition`
  - `record RestaurantDraft(long? RestaurantId, string Name, long? CategoryId, int? WalkMinutes, int? PriceLevel, string? Note)`
  - `RestaurantModal.Parse(ViewSubmission submission)` → `RestaurantDraft` (컨텍스트에서 RestaurantId를 읽음)
  - 블록 ID 상수: `BlockIds.Name`, `BlockIds.Category`, `BlockIds.WalkMinutes`, `BlockIds.PriceLevel`, `BlockIds.Note`

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Slack.Tests/MealPromptBlocksTests.cs`:

```csharp
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class MealPromptBlocksTests
{
    static readonly DateOnly Date = new(2026, 9, 18);

    static IReadOnlyList<RestaurantInfo> Restaurants(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new RestaurantInfo(
            i, $"식당{i}", 1, "한식", null, DateTimeOffset.UnixEpoch))];

    static string TextOf(IList<Block> blocks) =>
        string.Join("\n", blocks.OfType<SectionBlock>().Select(s => (s.Text as Markdown)?.Text ?? ""));

    [Fact]
    public void 신규_등록_버튼이_항상_있다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(3), recordedName: null);

        var buttons = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>());
        Assert.Contains(buttons, b => ActionIds.TryParseMealNew(b.ActionId, out _));
    }

    [Fact]
    public void 식당이_하나도_없어도_신규_등록은_할_수_있다()
    {
        var blocks = MealPromptBlocks.Build(Date, [], recordedName: null);

        var buttons = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<Button>());
        Assert.Contains(buttons, b => ActionIds.TryParseMealNew(b.ActionId, out _));
    }

    [Fact]
    public void 식당이_많으면_드롭다운으로_고른다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(20), recordedName: null);

        var menus = blocks.OfType<ActionsBlock>().SelectMany(a => a.Elements.OfType<StaticSelectMenu>());
        Assert.Single(menus);
    }

    [Fact]
    public void 이미_기록되면_기록된_식당을_보여준다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(3), recordedName: "스시로");

        Assert.Contains("스시로", TextOf(blocks));
    }

    [Fact]
    public void 기록_후에도_정정할_수_있게_선택지를_남긴다()
    {
        var blocks = MealPromptBlocks.Build(Date, Restaurants(3), recordedName: "스시로");

        Assert.NotEmpty(blocks.OfType<ActionsBlock>());
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `MealPromptBlocks`를 찾을 수 없음 (CS0246)

- [ ] **Step 3: 기록 요청 블록 구현**

`src/SelectLunch.Slack/Blocks/MealPromptBlocks.cs`:

```csharp
using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>오후 기록 요청. 목록 선택과 신규 등록 두 갈래를 함께 제공한다.</summary>
public static class MealPromptBlocks
{
    public const int ButtonThreshold = 10;

    public static IList<Block> Build(
        DateOnly date,
        IReadOnlyList<RestaurantInfo> restaurants,
        string? recordedName)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍜 오늘 뭐 드셨어요?") },
            new SectionBlock
            {
                Text = new Markdown(recordedName is null
                    ? "오늘 먹은 곳을 알려주시면 내일 추천이 정확해집니다."
                    : $"오늘은 *{recordedName}* 으로 기록되어 있습니다. 다르면 아래에서 고쳐 주세요."),
            },
        };

        if (restaurants.Count > 0)
        {
            blocks.Add(restaurants.Count <= ButtonThreshold
                ? ButtonActions(date, restaurants)
                : SelectActions(date, restaurants));
        }

        blocks.Add(new ActionsBlock
        {
            Elements =
            {
                new Button
                {
                    ActionId = ActionIds.MealNew(date),
                    Text = new PlainText("➕ 새 식당 등록"),
                    Style = ButtonStyle.Primary,
                },
            },
        });

        blocks.Add(new ContextBlock
        {
            Elements = { new Markdown("등록 시 카테고리를 함께 지정해야 추천에 반영됩니다.") },
        });

        return blocks;
    }

    static ActionsBlock ButtonActions(DateOnly date, IReadOnlyList<RestaurantInfo> restaurants)
    {
        var actions = new ActionsBlock();

        foreach (var restaurant in restaurants)
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.Meal(date, restaurant.RestaurantId),
                Text = new PlainText(restaurant.Name),
            });
        }

        return actions;
    }

    static ActionsBlock SelectActions(DateOnly date, IReadOnlyList<RestaurantInfo> restaurants)
    {
        // 드롭다운은 action_id 하나로 받고 선택 값에서 식당을 읽는다.
        var menu = new StaticSelectMenu
        {
            ActionId = ActionIds.Meal(date, restaurantId: 0),
            Placeholder = new PlainText("먹은 곳을 고르세요"),
        };

        foreach (var group in restaurants.GroupBy(r => r.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var optionGroup = new OptionGroup
            {
                Label = new PlainText(group.Key),
                Options = [],   // SlackNet은 이 컬렉션을 자동 초기화하지 않는다
            };

            foreach (var restaurant in group.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                optionGroup.Options.Add(new Option
                {
                    Text = new PlainText(restaurant.Name),
                    Value = restaurant.RestaurantId.ToString(),
                });
            }

            menu.OptionGroups.Add(optionGroup);
        }

        return new ActionsBlock { Elements = { menu } };
    }
}
```

드롭다운의 `action_id`에 `restaurantId: 0`을 넣는 이유: 선택 값은 페이로드의
`SelectedOption.Value`로 오므로 action_id에는 날짜만 실으면 된다. 핸들러가 두 경로를
구분할 수 있도록 형식은 `meal:` 규약을 그대로 쓴다.

- [ ] **Step 4: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 5: 등록 모달 테스트 작성**

`tests/SelectLunch.Slack.Tests/RestaurantModalTests.cs`:

```csharp
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Tests;

public class RestaurantModalTests
{
    static IReadOnlyList<Category> Categories() =>
    [
        new() { Id = 1, Name = "한식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
        new() { Id = 3, Name = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
    ];

    [Fact]
    public void 이름과_카테고리_입력이_있다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var blockIds = view.Blocks.OfType<InputBlock>().Select(b => b.BlockId).ToList();
        Assert.Contains(RestaurantModal.BlockIds.Name, blockIds);
        Assert.Contains(RestaurantModal.BlockIds.Category, blockIds);
    }

    [Fact]
    public void 이름과_카테고리는_필수이고_나머지는_선택이다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var inputs = view.Blocks.OfType<InputBlock>().ToDictionary(b => b.BlockId);
        Assert.False(inputs[RestaurantModal.BlockIds.Name].Optional);
        Assert.False(inputs[RestaurantModal.BlockIds.Category].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.WalkMinutes].Optional);
        Assert.True(inputs[RestaurantModal.BlockIds.Note].Optional);
    }

    [Fact]
    public void 카테고리_선택지는_전달된_목록에서_나온다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        var menu = view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.Category)
            .Element as StaticSelectMenu;
        Assert.Equal(2, menu!.Options.Count);
    }

    [Fact]
    public void 기존_값이_있으면_이름이_채워진다()
    {
        var draft = new RestaurantDraft(7, "스시로", 3, 5, 2, "회전초밥");

        var view = RestaurantModal.Build(Categories(), draft, ModalContext.None);

        var input = view.Blocks.OfType<InputBlock>()
            .Single(b => b.BlockId == RestaurantModal.BlockIds.Name)
            .Element as PlainTextInput;
        Assert.Equal("스시로", input!.InitialValue);
    }

    [Fact]
    public void 콜백_아이디가_고정되어_있다()
    {
        var view = RestaurantModal.Build(Categories(), existing: null, ModalContext.None);

        Assert.Equal(RestaurantModal.CallbackId, view.CallbackId);
    }

    [Fact]
    public void 기록_흐름의_컨텍스트가_private_metadata에_실린다()
    {
        var view = RestaurantModal.Build(
            Categories(), existing: null, ModalContext.ForRecord(new DateOnly(2026, 9, 18)));

        Assert.Equal("date:20260918", view.PrivateMetadata);
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void 모달_컨텍스트를_왕복_변환한다(ModalContext context)
    {
        Assert.Equal(context, ModalContext.Parse(context.Serialize()));
    }

    public static TheoryData<ModalContext> Contexts() =>
    [
        ModalContext.None,
        ModalContext.ForRecord(new DateOnly(2026, 9, 18)),
        ModalContext.ForEdit(7),
    ];

    [Theory]
    [InlineData("garbage")]
    [InlineData("date:nope")]
    [InlineData("restaurant:abc")]
    public void 형식이_깨진_컨텍스트는_None이_된다(string metadata)
    {
        Assert.Equal(ModalContext.None, ModalContext.Parse(metadata));
    }
}
```

`PrivateMetadata`에 날짜를 실어 두면, 모달 제출 시 "기록 흐름에서 열린 등록인지"를
구분할 수 있다. 기록 중 등록이면 등록 직후 그날 식사로 바로 기록해야 한다.

- [ ] **Step 6: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `RestaurantModal`을 찾을 수 없음 (CS0246)

- [ ] **Step 7: 등록 모달 구현**

`src/SelectLunch.Slack/Blocks/RestaurantModal.cs`:

```csharp
using System.Globalization;
using SelectLunch.Shared.Entities;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Blocks;

public sealed record RestaurantDraft(
    long? RestaurantId,
    string Name,
    long? CategoryId,
    int? WalkMinutes,
    int? PriceLevel,
    string? Note);

/// <summary>
/// 모달이 어떤 흐름에서 열렸는지. private_metadata로 왕복한다.
/// 날짜(기록 흐름)와 식당 ID(수정 흐름)가 같은 자리를 쓰므로 접두사로 구분한다.
/// </summary>
public sealed record ModalContext(DateOnly? RecordFor, long? RestaurantId)
{
    public static readonly ModalContext None = new(null, null);

    public static ModalContext ForRecord(DateOnly date) => new(date, null);

    public static ModalContext ForEdit(long restaurantId) => new(null, restaurantId);

    public string Serialize() =>
        RecordFor is { } date ? $"date:{date:yyyyMMdd}"
        : RestaurantId is { } id ? $"restaurant:{id}"
        : "";

    public static ModalContext Parse(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return None;

        var parts = metadata.Split(':', 2);
        if (parts.Length != 2)
            return None;

        return parts[0] switch
        {
            "date" when DateOnly.TryParseExact(
                parts[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) => ForRecord(date),
            "restaurant" when long.TryParse(
                parts[1], CultureInfo.InvariantCulture, out var id) => ForEdit(id),
            _ => None,
        };
    }
}

/// <summary>식당 등록·수정 모달. 이름과 카테고리는 필수다.</summary>
public static class RestaurantModal
{
    public const string CallbackId = "restaurant_form";

    public static class BlockIds
    {
        public const string Name = "name";
        public const string Category = "category";
        public const string WalkMinutes = "walk_minutes";
        public const string PriceLevel = "price_level";
        public const string Note = "note";
    }

    const string ActionSuffix = "_input";

    public static ModalViewDefinition Build(
        IReadOnlyList<Category> categories,
        RestaurantDraft? existing,
        ModalContext context)
    {
        var categoryMenu = new StaticSelectMenu
        {
            ActionId = BlockIds.Category + ActionSuffix,
            Placeholder = new PlainText("카테고리를 고르세요"),
        };

        foreach (var category in categories)
        {
            var option = new Option
            {
                Text = new PlainText(category.Name),
                Value = category.Id.ToString(CultureInfo.InvariantCulture),
            };
            categoryMenu.Options.Add(option);

            if (existing?.CategoryId == category.Id)
                categoryMenu.InitialOption = option;
        }

        return new ModalViewDefinition
        {
            CallbackId = CallbackId,
            Title = new PlainText(existing?.RestaurantId is null ? "식당 등록" : "식당 수정"),
            Submit = new PlainText("저장"),
            Close = new PlainText("취소"),
            PrivateMetadata = context.Serialize(),
            Blocks =
            {
                Text(BlockIds.Name, "이름", existing?.Name, optional: false, "예) 스시로"),
                new InputBlock
                {
                    BlockId = BlockIds.Category,
                    Label = new PlainText("카테고리"),
                    Optional = false,
                    Element = categoryMenu,
                },
                Text(BlockIds.WalkMinutes, "도보 시간(분)", existing?.WalkMinutes?.ToString(), optional: true, "예) 5"),
                Text(BlockIds.PriceLevel, "가격대(1~4)", existing?.PriceLevel?.ToString(), optional: true, "예) 2"),
                Text(BlockIds.Note, "메모", existing?.Note, optional: true, "예) 점심 특선 있음"),
            },
        };
    }

    /// <summary>제출된 모달에서 초안을 읽는다. 숫자 필드는 형식이 틀리면 null로 둔다.</summary>
    public static RestaurantDraft Parse(ViewSubmission submission)
    {
        var state = submission.View.State;
        var context = ModalContext.Parse(submission.View.PrivateMetadata);

        return new RestaurantDraft(
            context.RestaurantId,
            Value(state, BlockIds.Name) ?? "",
            long.TryParse(Selected(state, BlockIds.Category), CultureInfo.InvariantCulture, out var categoryId)
                ? categoryId
                : null,
            int.TryParse(Value(state, BlockIds.WalkMinutes), CultureInfo.InvariantCulture, out var walk)
                ? walk
                : null,
            int.TryParse(Value(state, BlockIds.PriceLevel), CultureInfo.InvariantCulture, out var price)
                ? Math.Clamp(price, 1, 4)
                : null,
            Value(state, BlockIds.Note));
    }

    static InputBlock Text(string blockId, string label, string? initial, bool optional, string placeholder) =>
        new()
        {
            BlockId = blockId,
            Label = new PlainText(label),
            Optional = optional,
            Element = new PlainTextInput
            {
                ActionId = blockId + ActionSuffix,
                InitialValue = initial ?? "",
                Placeholder = new PlainText(placeholder),
            },
        };

    static string? Value(ViewState state, string blockId) =>
        state.GetValue<PlainTextInputValue>(blockId, blockId + ActionSuffix)?.Value is { Length: > 0 } text
            ? text
            : null;

    static string? Selected(ViewState state, string blockId) =>
        state.GetValue<StaticSelectValue>(blockId, blockId + ActionSuffix)?.SelectedOption?.Value;
}
```

`ViewState.GetValue<T>(blockId, actionId)`는 SlackNet이 제공하는 조회 헬퍼로,
값이 없거나 타입이 다르면 null을 돌려준다 (0.18.0 어셈블리에서 확인).

- [ ] **Step 8: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 9: 커밋**

```bash
git add -A
git commit -m "feat: 기록 요청 블록과 식당 등록 모달 추가"
```

---

### Task 14: LunchService — 도메인 조작

DB를 바꾸는 모든 동작을 한곳에 모은다. 핸들러는 이 서비스만 호출하고,
서비스는 슬랙을 모른다. 덕분에 슬랙 없이 테스트할 수 있다.

**Files:**
- Create: `src/SelectLunch.Slack/Services/LunchService.cs`
- Test: `tests/SelectLunch.Slack.Tests/LunchServiceTests.cs`
- Modify: `tests/SelectLunch.Slack.Tests/SelectLunch.Slack.Tests.csproj` (Shared.Tests의
  `TestDb`를 재사용하기 위해 `TestDb.cs`를 링크로 추가)

**Interfaces:**
- Consumes: `LunchDbContext`·`LunchQueries` (Task 7·9), `RecommendationEngine` (Task 4),
  `VoteTally`·`PollOutcome` (Task 11·12), `RestaurantDraft` (Task 13)
- Produces (`LunchService`, 생성자 `(LunchDbContext db, string channelId)`):
  - `Task<LunchPoll> OpenPollAsync(DateOnly date, DateTimeOffset opensAt, DateTimeOffset closesAt, CancellationToken ct)`
  - `Task CastVoteAsync(long pollId, string slackUserId, long restaurantId, CancellationToken ct)`
  - `Task<IReadOnlyList<VoteTally>> GetTalliesAsync(long pollId, CancellationToken ct)`
  - `Task<PollOutcome> ClosePollAsync(long pollId, DateOnly today, RecommendationOptions options, CancellationToken ct)`
  - `Task RecordMealAsync(DateOnly date, long restaurantId, string slackUserId, MealSource source, CancellationToken ct)`
  - `Task<Restaurant> SaveRestaurantAsync(RestaurantDraft draft, string slackUserId, CancellationToken ct)`
  - `Task MarkMealPromptPostedAsync(DateOnly date, DateTimeOffset at, CancellationToken ct)`
  - `Task MarkPendingReminderSentAsync(DateOnly date, DateTimeOffset at, CancellationToken ct)`

- [ ] **Step 1: 테스트 프로젝트에 TestDb 링크 추가**

`tests/SelectLunch.Slack.Tests/SelectLunch.Slack.Tests.csproj`의 `ItemGroup`에 추가한다.

```xml
  <ItemGroup>
    <Compile Include="../SelectLunch.Shared.Tests/TestDb.cs" Link="TestDb.cs" />
  </ItemGroup>
```

`TestDb`의 네임스페이스가 `SelectLunch.Shared.Tests`이므로 테스트에서 `using`이 필요하다.

- [ ] **Step 2: 실패하는 테스트 작성**

`tests/SelectLunch.Slack.Tests/LunchServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Tests;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Tests;

public class LunchServiceTests
{
    const string Channel = "C1";
    static readonly DateOnly Today = new(2026, 9, 18);
    static readonly DateTimeOffset OpensAt = new(2026, 9, 18, 10, 30, 0, TimeSpan.FromHours(9));
    static readonly DateTimeOffset ClosesAt = OpensAt.AddMinutes(30);

    static async Task<(TestDb Fixture, LunchService Service)> SetupAsync()
    {
        var fixture = await TestDb.CreateAsync();
        fixture.Db.Categories.AddRange(
            new Category { Id = 1, Name = "한식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch },
            new Category { Id = 3, Name = "일식", IsBuiltIn = true, CreatedAt = DateTimeOffset.UnixEpoch });
        await fixture.Db.SaveChangesAsync();
        return (fixture, new LunchService(fixture.Db, Channel));
    }

    static RestaurantDraft Draft(string name, long categoryId) =>
        new(null, name, categoryId, null, null, null);

    [Fact]
    public async Task 투표를_열면_Active_식당이_후보로_들어간다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        var candidates = await fixture.Db.PollCandidates.CountAsync(c => c.PollId == poll.Id, ct);
        Assert.Equal(2, candidates);
        Assert.Equal(PollStatus.Open, poll.Status);
    }

    [Fact]
    public async Task 같은_사람이_다시_투표하면_표가_바뀐다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var b = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.CastVoteAsync(poll.Id, "U1", a.Id, ct);
        await service.CastVoteAsync(poll.Id, "U1", b.Id, ct);

        var votes = await fixture.Db.PollVotes.Where(v => v.PollId == poll.Id).ToListAsync(ct);
        Assert.Equal(b.Id, Assert.Single(votes).RestaurantId);
    }
```

```csharp
    [Fact]
    public async Task 투표_동점이면_추천_점수가_높은_쪽이_1위가_된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var 한식집 = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var 일식집 = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        // 한식을 최근에 많이 먹었으므로 추천 점수는 일식이 높다
        fixture.Db.MealRecords.AddRange(
            new MealRecord { ChannelId = Channel, Date = Today.AddDays(-1), RestaurantId = 한식집.Id,
                RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch, Source = MealSource.Prompt },
            new MealRecord { ChannelId = Channel, Date = Today.AddDays(-3), RestaurantId = 한식집.Id,
                RecordedBySlackUserId = "U1", RecordedAt = DateTimeOffset.UnixEpoch, Source = MealSource.Prompt });
        await fixture.Db.SaveChangesAsync(ct);

        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);
        await service.CastVoteAsync(poll.Id, "U1", 한식집.Id, ct);
        await service.CastVoteAsync(poll.Id, "U2", 일식집.Id, ct);   // 1:1 동점

        var outcome = await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        Assert.Equal(일식집.Id, outcome.Winner!.RestaurantId);
    }

    [Fact]
    public async Task 마감하면_상태와_추천_근거가_저장된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);
        var poll = await service.OpenPollAsync(Today, OpensAt, ClosesAt, ct);

        await service.ClosePollAsync(poll.Id, Today, new RecommendationOptions(), ct);

        var saved = await fixture.Db.Polls.SingleAsync(p => p.Id == poll.Id, ct);
        Assert.Equal(PollStatus.Closed, saved.Status);
        Assert.NotNull(saved.RecommendedRestaurantId);
        Assert.NotNull(saved.RationaleJson);
    }

    [Fact]
    public async Task 하루에_두_번_기록하면_나중_것이_덮어쓴다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var a = await service.SaveRestaurantAsync(Draft("김밥천국", 1), "U1", ct);
        var b = await service.SaveRestaurantAsync(Draft("스시로", 3), "U1", ct);

        await service.RecordMealAsync(Today, a.Id, "U1", MealSource.Prompt, ct);
        await service.RecordMealAsync(Today, b.Id, "U2", MealSource.Prompt, ct);

        var record = await fixture.Db.MealRecords.SingleAsync(m => m.Date == Today, ct);
        Assert.Equal(b.Id, record.RestaurantId);
        Assert.Equal("U2", record.RecordedBySlackUserId);
    }

    [Fact]
    public async Task 카테고리가_있으면_Active로_등록된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;

        var restaurant = await service.SaveRestaurantAsync(
            Draft("스시로", 3), "U1", TestContext.Current.CancellationToken);

        Assert.Equal(RestaurantStatus.Active, restaurant.Status);
    }

    [Fact]
    public async Task 카테고리가_없으면_Pending으로_등록된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;

        var restaurant = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "이름만아는집", null, null, null, null),
            "U1", TestContext.Current.CancellationToken);

        Assert.Equal(RestaurantStatus.Pending, restaurant.Status);
    }

    [Fact]
    public async Task 같은_이름을_다시_등록하면_기존_식당을_갱신한다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;
        var pending = await service.SaveRestaurantAsync(
            new RestaurantDraft(null, "스시로", null, null, null, null), "U1", ct);

        var filled = await service.SaveRestaurantAsync(Draft("스시 로", 3), "U2", ct);

        Assert.Equal(pending.Id, filled.Id);
        Assert.Equal(RestaurantStatus.Active, filled.Status);
        Assert.Equal(1, await fixture.Db.Restaurants.CountAsync(ct));
    }

    [Fact]
    public async Task 발송_이력을_남기면_오늘_상태에_반영된다()
    {
        var (fixture, service) = await SetupAsync();
        await using var _ = fixture;
        var ct = TestContext.Current.CancellationToken;

        await service.MarkMealPromptPostedAsync(Today, ClosesAt, ct);

        var day = await fixture.Db.ChannelDays.SingleAsync(ct);
        Assert.NotNull(day.MealPromptPostedAt);
    }
}
```

`같은_이름을_다시_등록하면_기존_식당을_갱신한다`가 중요하다. 기록 흐름에서 이름만
들어와 `Pending`이 된 식당을, 나중에 `/lunch add`로 카테고리까지 채우면
중복 행이 생기는 대신 그 행이 `Active`로 승격되어야 한다.

- [ ] **Step 3: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `LunchService`를 찾을 수 없음 (CS0246)

- [ ] **Step 4: LunchService 구현**

`src/SelectLunch.Slack/Services/LunchService.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Services;

/// <summary>DB를 바꾸는 동작을 모은다. 슬랙 타입을 전혀 모른다.</summary>
public sealed class LunchService(LunchDbContext db, string channelId)
{
    public async Task<LunchPoll> OpenPollAsync(
        DateOnly date, DateTimeOffset opensAt, DateTimeOffset closesAt, CancellationToken ct)
    {
        var poll = new LunchPoll
        {
            ChannelId = channelId, Date = date,
            OpensAt = opensAt, ClosesAt = closesAt, Status = PollStatus.Open,
        };
        db.Polls.Add(poll);
        await db.SaveChangesAsync(ct);

        var candidates = await db.GetActiveRestaurantsAsync(ct);
        var order = 0;
        foreach (var candidate in candidates.OrderBy(c => c.CategoryName, StringComparer.Ordinal)
                                            .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            db.PollCandidates.Add(new PollCandidate
            {
                PollId = poll.Id, RestaurantId = candidate.RestaurantId, DisplayOrder = order++,
            });
        }
        await db.SaveChangesAsync(ct);

        return poll;
    }

    /// <summary>1인 1표. 이미 투표했으면 대상만 바꾼다.</summary>
    public async Task CastVoteAsync(long pollId, string slackUserId, long restaurantId, CancellationToken ct)
    {
        var existing = await db.PollVotes
            .SingleOrDefaultAsync(v => v.PollId == pollId && v.SlackUserId == slackUserId, ct);

        if (existing is null)
        {
            db.PollVotes.Add(new PollVote
            {
                PollId = pollId, SlackUserId = slackUserId,
                RestaurantId = restaurantId, VotedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.RestaurantId = restaurantId;
            existing.VotedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<VoteTally>> GetTalliesAsync(long pollId, CancellationToken ct) =>
        await db.PollVotes
            .Where(v => v.PollId == pollId)
            .GroupBy(v => new { v.RestaurantId, v.Restaurant!.Name })
            .Select(g => new VoteTally(g.Key.RestaurantId, g.Key.Name, g.Count()))
            .ToListAsync(ct);

    /// <summary>
    /// 마감하고 결과를 낸다. 투표 동점은 추천 점수가 높은 쪽으로 푼다 —
    /// 랜덤을 쓰지 않으면서 결정적으로 해소하는 방법이다.
    /// </summary>
    public async Task<PollOutcome> ClosePollAsync(
        long pollId, DateOnly today, RecommendationOptions options, CancellationToken ct)
    {
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        var tallies = await GetTalliesAsync(pollId, ct);

        var stats = await db.GetCategoryStatsAsync(today, ct);
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        var recommendation = RecommendationEngine.Recommend(today, stats, restaurants, options);

        var scoreByRestaurant = ScoreLookup(stats, restaurants, today, options);
        var winner = tallies
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => scoreByRestaurant.GetValueOrDefault(t.RestaurantId, int.MinValue))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        poll.Status = PollStatus.Closed;
        poll.WinnerRestaurantId = winner?.RestaurantId;
        poll.RecommendedRestaurantId = recommendation?.Pick.RestaurantId;
        poll.RationaleJson = recommendation is null
            ? null
            : JsonSerializer.Serialize(new { recommendation.Winner, recommendation.Others });
        await db.SaveChangesAsync(ct);

        return new PollOutcome(winner, tallies, recommendation);
    }

    /// <summary>식당 → 소속 카테고리 점수. 투표 동점 처리에 쓴다.</summary>
    static Dictionary<long, int> ScoreLookup(
        IReadOnlyList<CategoryStat> stats,
        IReadOnlyList<RestaurantInfo> restaurants,
        DateOnly today,
        RecommendationOptions options)
    {
        var byCategory = RecommendationEngine.ScoreCategories(today, stats, options)
            .ToDictionary(s => s.CategoryId, s => s.Score);

        return restaurants.ToDictionary(
            r => r.RestaurantId,
            r => byCategory.GetValueOrDefault(r.CategoryId, 0));
    }
}
```

같은 클래스에 이어 붙인다.

```csharp
    /// <summary>채널 단위 하루 1건. 나중에 기록한 사람이 덮어쓴다.</summary>
    public async Task RecordMealAsync(
        DateOnly date, long restaurantId, string slackUserId, MealSource source, CancellationToken ct)
    {
        var existing = await db.MealRecords
            .SingleOrDefaultAsync(m => m.ChannelId == channelId && m.Date == date, ct);

        if (existing is null)
        {
            db.MealRecords.Add(new MealRecord
            {
                ChannelId = channelId, Date = date, RestaurantId = restaurantId,
                RecordedBySlackUserId = slackUserId, RecordedAt = DateTimeOffset.UtcNow,
                Source = source,
            });
        }
        else
        {
            existing.RestaurantId = restaurantId;
            existing.RecordedBySlackUserId = slackUserId;
            existing.RecordedAt = DateTimeOffset.UtcNow;
            existing.Source = source;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 등록 또는 수정. 정규화된 이름이 같으면 기존 행을 갱신한다 —
    /// 기록 중 이름만 들어와 Pending이 된 식당을 나중에 Active로 승격시키는 경로다.
    /// </summary>
    public async Task<Restaurant> SaveRestaurantAsync(
        RestaurantDraft draft, string slackUserId, CancellationToken ct)
    {
        var normalized = Restaurant.Normalize(draft.Name);
        var now = DateTimeOffset.UtcNow;

        var restaurant = draft.RestaurantId is { } id
            ? await db.Restaurants.SingleAsync(r => r.Id == id, ct)
            : await db.Restaurants.SingleOrDefaultAsync(r => r.NormalizedName == normalized, ct);

        if (restaurant is null)
        {
            restaurant = new Restaurant
            {
                Name = draft.Name, NormalizedName = normalized,
                CreatedBySlackUserId = slackUserId, CreatedAt = now, UpdatedAt = now,
            };
            db.Restaurants.Add(restaurant);
        }

        restaurant.Name = draft.Name;
        restaurant.NormalizedName = normalized;
        restaurant.CategoryId = draft.CategoryId ?? restaurant.CategoryId;
        restaurant.WalkMinutes = draft.WalkMinutes ?? restaurant.WalkMinutes;
        restaurant.PriceLevel = draft.PriceLevel ?? restaurant.PriceLevel;
        restaurant.Note = draft.Note ?? restaurant.Note;
        restaurant.UpdatedAt = now;

        // 불변 조건: Active ⟺ CategoryId != null
        restaurant.Status = restaurant.CategoryId is null
            ? RestaurantStatus.Pending
            : RestaurantStatus.Active;

        await db.SaveChangesAsync(ct);
        return restaurant;
    }

    public Task MarkMealPromptPostedAsync(DateOnly date, DateTimeOffset at, CancellationToken ct) =>
        MarkDayAsync(date, day => day.MealPromptPostedAt = at, ct);

    public Task MarkPendingReminderSentAsync(DateOnly date, DateTimeOffset at, CancellationToken ct) =>
        MarkDayAsync(date, day => day.PendingReminderSentAt = at, ct);

    async Task MarkDayAsync(DateOnly date, Action<ChannelDay> update, CancellationToken ct)
    {
        var day = await db.ChannelDays
            .SingleOrDefaultAsync(d => d.ChannelId == channelId && d.Date == date, ct);

        if (day is null)
        {
            day = new ChannelDay { ChannelId = channelId, Date = date };
            db.ChannelDays.Add(day);
        }

        update(day);
        await db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 5: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "feat: 투표·기록·등록 도메인 서비스 구현"
```

---

### Task 15: 메시지 발송기 (LunchAnnouncer)

블록을 실제로 슬랙에 보내고 갱신한다. `ISlackApiClient`를 주입받으므로
페이크로 테스트할 수 있다.

**Files:**
- Create: `src/SelectLunch.Slack/Services/LunchAnnouncer.cs`
- Test: `tests/SelectLunch.Slack.Tests/LunchAnnouncerTests.cs`

**Interfaces:**
- Consumes: `ISlackApiClient` (SlackNet), `LunchService` (Task 14),
  `PollBlocks`·`ResultBlocks`·`MealPromptBlocks` (Task 11~13)
- Produces (`LunchAnnouncer`, 생성자 `(ISlackApiClient slack, LunchDbContext db, LunchService service, string channelId)`):
  - `Task<string> PostPollAsync(long pollId, DateTimeOffset closesAt, CancellationToken ct)` → 메시지 ts
  - `Task RefreshPollAsync(long pollId, CancellationToken ct)`
  - `Task PostResultAsync(PollOutcome outcome, RecommendationOptions options, CancellationToken ct)`
  - `Task PostMealPromptAsync(DateOnly date, CancellationToken ct)`
  - `Task PostPendingReminderAsync(CancellationToken ct)`
  - `Task PostTextAsync(string markdown, CancellationToken ct)`

- [ ] **Step 1: 구현**

`src/SelectLunch.Slack/Services/LunchAnnouncer.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;
using SelectLunch.Slack.Blocks;

namespace SelectLunch.Slack.Services;

/// <summary>블록을 슬랙에 실제로 보낸다. 도메인 판단은 하지 않는다.</summary>
public sealed class LunchAnnouncer(
    ISlackApiClient slack,
    LunchDbContext db,
    LunchService service,
    string channelId)
{
    public async Task<string> PostPollAsync(long pollId, DateTimeOffset closesAt, CancellationToken ct)
    {
        var candidates = await db.GetActiveRestaurantsAsync(ct);
        var blocks = PollBlocks.Build(pollId, candidates, [], closesAt);

        var ts = await PostAsync(blocks, "오늘 점심 뭐 먹지?", ct);

        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        poll.MessageTs = ts;
        await db.SaveChangesAsync(ct);

        return ts;
    }

    /// <summary>투표 후 집계를 메시지에 되비춘다.</summary>
    public async Task RefreshPollAsync(long pollId, CancellationToken ct)
    {
        var poll = await db.Polls.SingleAsync(p => p.Id == pollId, ct);
        if (poll.MessageTs is null)
            return;

        var candidates = await db.GetActiveRestaurantsAsync(ct);
        var tallies = await service.GetTalliesAsync(pollId, ct);

        await slack.Chat.Update(new MessageUpdate
        {
            ChannelId = channelId,
            Ts = poll.MessageTs,
            Text = "오늘 점심 뭐 먹지?",
            Blocks = PollBlocks.Build(pollId, candidates, tallies, poll.ClosesAt),
        }, ct);
    }

    public Task PostResultAsync(PollOutcome outcome, RecommendationOptions options, CancellationToken ct) =>
        PostAsync(ResultBlocks.Build(outcome, options), "오늘 점심 투표 결과", ct);

    public async Task PostMealPromptAsync(DateOnly date, CancellationToken ct)
    {
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        var recorded = await db.MealRecords
            .Where(m => m.ChannelId == channelId && m.Date == date)
            .Select(m => m.Restaurant!.Name)
            .SingleOrDefaultAsync(ct);

        await PostAsync(MealPromptBlocks.Build(date, restaurants, recorded), "오늘 뭐 드셨어요?", ct);
    }

    public async Task PostPendingReminderAsync(CancellationToken ct)
    {
        var pending = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        var blocks = new List<Block>
        {
            new SectionBlock
            {
                Text = new Markdown(
                    $"ℹ️ 정보가 덜 찬 식당이 {pending.Count}곳 있습니다. " +
                    "카테고리를 채워 주시면 추천에 반영됩니다."),
            },
        };

        var actions = new ActionsBlock();
        foreach (var restaurant in pending.Take(5))
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.RestaurantFill(restaurant.Id),
                Text = new PlainText(restaurant.Name),
            });
        }
        blocks.Add(actions);

        await PostAsync(blocks, "정보가 덜 찬 식당이 있습니다", ct);
    }

    public Task PostTextAsync(string markdown, CancellationToken ct) =>
        PostAsync([new SectionBlock { Text = new Markdown(markdown) }], markdown, ct);

    async Task<string> PostAsync(IList<Block> blocks, string fallbackText, CancellationToken ct)
    {
        var response = await slack.Chat.PostMessage(new Message
        {
            Channel = channelId,
            Text = fallbackText,   // 알림·접근성용 대체 텍스트
            Blocks = blocks,
        }, ct);

        return response.Ts;
    }
}
```

> `Text`를 항상 채우는 이유: 블록만 보내면 모바일 알림과 스크린 리더에 내용이
> 비어 보인다. 슬랙이 대체 텍스트로 쓴다.

- [ ] **Step 2: 빌드 확인**

```bash
dotnet build
```

Expected: 성공.

SlackNet 0.18.0 어셈블리에서 확인한 프로퍼티명이다 — 발송은 `Message.Channel`,
갱신은 `MessageUpdate.ChannelId` + `MessageUpdate.Ts`로 **이름이 다르다.**
응답의 메시지 ts는 `PostMessageResponse.Ts`다.

- [ ] **Step 3: 커밋**

```bash
git add -A
git commit -m "feat: 슬랙 메시지 발송기 추가"
```

---

### Task 16: 슬랙 핸들러 4종

사용자 상호작용을 받는다. 핸들러는 얇게 유지하고 판단은 `LunchService`에 맡긴다.

**Files:**
- Create: `src/SelectLunch.Slack/Handlers/LunchSlashCommandHandler.cs`
- Create: `src/SelectLunch.Slack/Handlers/VoteActionHandler.cs`
- Create: `src/SelectLunch.Slack/Handlers/MealActionHandler.cs`
- Create: `src/SelectLunch.Slack/Handlers/RestaurantModalHandler.cs`
- Create: `src/SelectLunch.Slack/Handlers/PendingActionHandler.cs`
- Test: `tests/SelectLunch.Slack.Tests/SlashCommandParsingTests.cs`

**Interfaces:**
- Consumes: `LunchService`·`LunchAnnouncer` (Task 14·15), `ActionIds` (Task 11),
  `RestaurantModal` (Task 13)
- Produces:
  - `record LunchCommand(string SubCommand, string Argument)`,
    `LunchCommand.Parse(string text)` — 서브커맨드 분해
  - `ISlashCommandHandler` 구현 `LunchSlashCommandHandler`
  - `IBlockActionHandler` 구현 3종 (`VoteActionHandler`, `MealActionHandler`, `PendingActionHandler`)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/SelectLunch.Slack.Tests/SlashCommandParsingTests.cs`:

```csharp
using SelectLunch.Slack.Handlers;

namespace SelectLunch.Slack.Tests;

public class SlashCommandParsingTests
{
    [Theory]
    [InlineData("", "help", "")]
    [InlineData("   ", "help", "")]
    [InlineData("help", "help", "")]
    [InlineData("list", "list", "")]
    [InlineData("add", "add", "")]
    [InlineData("add 스시로", "add", "스시로")]
    [InlineData("add  스시 로  ", "add", "스시 로")]
    [InlineData("stats week", "stats", "week")]   // 인자는 무시되지만 파싱은 되어야 한다
    public void 서브커맨드와_인자를_분해한다(string text, string expectedSub, string expectedArg)
    {
        var command = LunchCommand.Parse(text);

        Assert.Equal(expectedSub, command.SubCommand);
        Assert.Equal(expectedArg, command.Argument);
    }

    [Fact]
    public void 대소문자를_가리지_않는다()
    {
        Assert.Equal("list", LunchCommand.Parse("LIST").SubCommand);
    }
}
```

- [ ] **Step 2: 테스트 실행 — 실패 확인**

```bash
dotnet test
```

Expected: FAIL — `LunchCommand`를 찾을 수 없음 (CS0246)

- [ ] **Step 3: 슬래시 커맨드 핸들러 구현**

`src/SelectLunch.Slack/Handlers/LunchSlashCommandHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

public sealed record LunchCommand(string SubCommand, string Argument)
{
    public static LunchCommand Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return new LunchCommand("help", "");

        var space = trimmed.IndexOf(' ');
        return space < 0
            ? new LunchCommand(trimmed.ToLowerInvariant(), "")
            : new LunchCommand(
                trimmed[..space].ToLowerInvariant(),
                trimmed[(space + 1)..].Trim());
    }
}

public sealed class LunchSlashCommandHandler(
    LunchDbContext db,
    LunchService service,
    ISlackApiClient slack,
    IOptionsMonitor<LunchOptions> options)
    : ISlashCommandHandler
{
    public async Task<SlashCommandResponse> Handle(SlashCommand command)
    {
        var parsed = LunchCommand.Parse(command.Text);
        var ct = CancellationToken.None;

        return parsed.SubCommand switch
        {
            "add" => await OpenAddModalAsync(command, parsed.Argument, ct),
            "edit" => await OpenEditModalAsync(command, ct),
            "list" => Ephemeral(await ListAsync(ct)),
            "pending" => Ephemeral(await PendingAsync(ct)),
            "stats" => Ephemeral(await StatsAsync(ct)),
            "today" => Ephemeral(await TodayAsync(ct)),
            _ => Ephemeral(HelpText),
        };
    }

    const string HelpText = """
        *점심 봇 사용법*
        • `/lunch add [이름]` — 식당 등록 (모달이 열립니다)
        • `/lunch list` — 등록된 식당 목록
        • `/lunch edit` — 식당 수정
        • `/lunch pending` — 정보가 덜 찬 식당 보완
        • `/lunch stats` — 카테고리별 현재 추천 점수
        • `/lunch today` — 오늘 투표·결과 다시 보기
        """;

    async Task<SlashCommandResponse> OpenAddModalAsync(SlashCommand command, string name, CancellationToken ct)
    {
        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
        var draft = name.Length == 0 ? null : new RestaurantDraft(null, name, null, null, null, null);

        await slack.Views.Open(command.TriggerId, RestaurantModal.Build(categories, draft, ModalContext.None), ct);
        return Ephemeral("등록 창을 열었습니다.");
    }

    async Task<SlashCommandResponse> OpenEditModalAsync(SlashCommand command, CancellationToken ct)
    {
        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
        await slack.Views.Open(command.TriggerId, RestaurantModal.Build(categories, null, ModalContext.None), ct);
        return Ephemeral("수정 창을 열었습니다.");
    }

    async Task<string> ListAsync(CancellationToken ct)
    {
        var restaurants = await db.GetActiveRestaurantsAsync(ct);
        if (restaurants.Count == 0)
            return "등록된 식당이 없습니다. `/lunch add 이름` 으로 등록해 보세요.";

        var groups = restaurants
            .GroupBy(r => r.CategoryName)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"*{g.Key}* — {string.Join(", ", g.Select(r => r.Name).Order(StringComparer.Ordinal))}");

        return $"등록된 식당 {restaurants.Count}곳\n{string.Join("\n", groups)}";
    }

    async Task<string> PendingAsync(CancellationToken ct)
    {
        var pending = await db.Restaurants
            .Where(r => r.Status == RestaurantStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.Name)
            .ToListAsync(ct);

        return pending.Count == 0
            ? "정보가 덜 찬 식당이 없습니다."
            : $"카테고리가 비어 추천에서 빠진 식당 {pending.Count}곳\n• {string.Join("\n• ", pending)}\n" +
              "`/lunch add 이름` 으로 카테고리를 채워 주세요.";
    }

    async Task<string> StatsAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var stats = await db.GetCategoryStatsAsync(today, ct);
        if (stats.Count == 0)
            return "집계할 카테고리가 없습니다.";

        var scores = RecommendationEngine
            .ScoreCategories(today, stats, options.CurrentValue.Recommendation)
            .OrderByDescending(s => s.Score)
            .Select(s => $"• {s.CategoryName} *{s.Score}점* ({s.DaysSince}일 전, 7일내 {s.Count7d}회, 30일내 {s.Count30d}회)");

        return $"*카테고리 점수 현황*\n{string.Join("\n", scores)}";
    }

    async Task<string> TodayAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var poll = await db.Polls.OrderByDescending(p => p.Date).FirstOrDefaultAsync(p => p.Date == today, ct);

        return poll is null
            ? "오늘 투표가 아직 열리지 않았습니다."
            : $"오늘 투표 상태: {poll.Status} (마감 {poll.ClosesAt:HH:mm})";
    }

    static SlashCommandResponse Ephemeral(string text) =>
        new() { Message = new SlackNet.WebApi.Message { Text = text }, ResponseType = ResponseType.Ephemeral };
}
```

- [ ] **Step 4: 투표·기록·모달 핸들러 구현**

`src/SelectLunch.Slack/Handlers/VoteActionHandler.cs`:

```csharp
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>버튼과 드롭다운 두 경로의 투표를 모두 받는다.</summary>
public sealed class VoteActionHandler(LunchService service, LunchAnnouncer announcer)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var userId = request.User.Id;
        var ct = CancellationToken.None;

        if (ActionIds.TryParseVote(action.ActionId, out var pollId, out var restaurantId))
        {
            await service.CastVoteAsync(pollId, userId, restaurantId, ct);
            await announcer.RefreshPollAsync(pollId, ct);
            return;
        }

        if (ActionIds.TryParseVoteSelect(action.ActionId, out var selectPollId)
            && action is StaticSelectAction { SelectedOption.Value: { } value }
            && long.TryParse(value, out var selectedRestaurantId))
        {
            await service.CastVoteAsync(selectPollId, userId, selectedRestaurantId, ct);
            await announcer.RefreshPollAsync(selectPollId, ct);
        }
    }
}
```

`src/SelectLunch.Slack/Handlers/MealActionHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

public sealed class MealActionHandler(
    LunchDbContext db,
    LunchService service,
    LunchAnnouncer announcer,
    ISlackApiClient slack)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var userId = request.User.Id;
        var ct = CancellationToken.None;

        // 신규 등록 — 모달의 private_metadata에 날짜를 실어 등록 직후 기록으로 잇는다
        if (ActionIds.TryParseMealNew(action.ActionId, out var newDate))
        {
            var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
            await slack.Views.Open(
                request.TriggerId,
                RestaurantModal.Build(categories, null, ModalContext.ForRecord(newDate)),
                ct);
            return;
        }

        if (!ActionIds.TryParseMeal(action.ActionId, out var date, out var restaurantId))
            return;

        // 드롭다운이면 선택 값이 실제 식당이다 (action_id의 0은 자리표시자)
        if (action is StaticSelectAction { SelectedOption.Value: { } value }
            && long.TryParse(value, out var selected))
        {
            restaurantId = selected;
        }

        if (restaurantId == 0)
            return;

        await service.RecordMealAsync(date, restaurantId, userId, MealSource.Prompt, ct);

        var name = await db.Restaurants.Where(r => r.Id == restaurantId).Select(r => r.Name).SingleAsync(ct);
        await announcer.PostTextAsync($"✅ <@{userId}> 님이 오늘 점심을 *{name}* 으로 기록했습니다.", ct);
    }
}
```

`src/SelectLunch.Slack/Handlers/RestaurantModalHandler.cs`:

```csharp
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

public sealed class RestaurantModalHandler(LunchService service, LunchAnnouncer announcer)
    : IViewSubmissionHandler
{
    public async Task<ViewSubmissionResponse> Handle(ViewSubmission viewSubmission)
    {
        var ct = CancellationToken.None;
        var draft = RestaurantModal.Parse(viewSubmission);
        var context = ModalContext.Parse(viewSubmission.View.PrivateMetadata);

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            return new ViewErrorsResponse
            {
                Errors = { [RestaurantModal.BlockIds.Name] = "이름을 입력해 주세요." },
            };
        }

        var userId = viewSubmission.User.Id;
        var restaurant = await service.SaveRestaurantAsync(draft, userId, ct);

        // 기록 흐름에서 열린 모달이면 등록 직후 그날 식사로 기록한다
        if (context.RecordFor is { } date)
        {
            await service.RecordMealAsync(date, restaurant.Id, userId, MealSource.NewRegistration, ct);
            await announcer.PostTextAsync(
                $"✅ <@{userId}> 님이 *{restaurant.Name}* 을(를) 등록하고 오늘 점심으로 기록했습니다.", ct);
        }
        else
        {
            var status = restaurant.Status == RestaurantStatus.Active
                ? "추천 대상에 포함됩니다"
                : "카테고리가 비어 있어 추천에서 제외됩니다";
            await announcer.PostTextAsync($"🏪 *{restaurant.Name}* 등록 완료 — {status}.", ct);
        }

        return ViewSubmissionResponse.Null;
    }

    public Task HandleClose(ViewClosed viewClosed) => Task.CompletedTask;
}
```

`src/SelectLunch.Slack/Handlers/PendingActionHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Slack.Blocks;
using SlackNet;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>보완 요청 메시지의 식당 버튼 — 기존 값을 채운 수정 모달을 연다.</summary>
public sealed class PendingActionHandler(LunchDbContext db, ISlackApiClient slack)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        if (!ActionIds.TryParseRestaurantFill(request.Action.ActionId, out var restaurantId))
            return;

        var ct = CancellationToken.None;
        var restaurant = await db.Restaurants.SingleOrDefaultAsync(r => r.Id == restaurantId, ct);
        if (restaurant is null)
            return;

        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
        var draft = new RestaurantDraft(
            restaurant.Id, restaurant.Name, restaurant.CategoryId,
            restaurant.WalkMinutes, restaurant.PriceLevel, restaurant.Note);

        await slack.Views.Open(request.TriggerId, RestaurantModal.Build(categories, draft, ModalContext.ForEdit(restaurant.Id)), ct);
    }
}
```

이 핸들러가 없으면 `LunchAnnouncer.PostPendingReminderAsync`가 만든 버튼이
눌러도 아무 반응이 없다. `RestaurantModal.Parse`에 `restaurantId`를 넘겨야
새 행이 생기지 않고 기존 행이 갱신된다 — 모달 제출 시 `PrivateMetadata` 대신
`RestaurantDraft.RestaurantId`가 그 경로를 만든다.

- [ ] **Step 5: 테스트 실행 — 통과 확인**

```bash
dotnet test
```

Expected: PASS — 전체 통과

SlackNet 0.18.0 어셈블리에서 확인한 사항이다:
- `SlashCommandResponse`에는 `Null` 정적 멤버가 **없다.** 모달을 연 뒤에도
  ephemeral 응답을 돌려준다 — 사용자에게도 "창이 열렸다"는 피드백이 남는다.
- 입력 오류는 `ViewSubmissionResponse.Errors(...)`가 아니라
  `ViewErrorsResponse { Errors = ... }`로 돌려준다.
- 정상 종료는 `ViewSubmissionResponse.Null`(정적 프로퍼티)이 맞다.

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "feat: 슬래시 커맨드·투표·기록·모달 핸들러 구현"
```

---

### Task 17: 워커와 호스트 구성

Socket Mode 연결과 스케줄러 루프를 붙여 앱을 실제로 동작시킨다.
설정 핫리로드도 여기서 마무리한다.

**Files:**
- Create: `src/SelectLunch.Slack/Workers/SocketModeWorker.cs`
- Create: `src/SelectLunch.Slack/Workers/SchedulerWorker.cs`
- Create: `src/SelectLunch.Slack/Program.cs`
- Test: `tests/SelectLunch.Slack.Tests/TimeZoneResolutionTests.cs`

**Interfaces:**
- Consumes: 앞선 모든 task
- Produces: 실행 가능한 `SelectLunch.Slack` 애플리케이션

- [ ] **Step 1: Socket Mode 워커 작성**

`src/SelectLunch.Slack/Workers/SocketModeWorker.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Slack.Options;
using SlackNet;
using SlackNet.Extensions.DependencyInjection;

namespace SelectLunch.Slack.Workers;

/// <summary>
/// Socket Mode 연결을 유지한다. 아웃바운드 웹소켓이라 공인 IP·인증서가 필요 없다.
/// 재연결은 SlackNet이 처리하므로 여기서는 연결 수립과 수명만 관리한다.
/// </summary>
public sealed class SocketModeWorker(
    IServiceProvider services,
    IOptions<SlackOptions> slackOptions,
    ILogger<SocketModeWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = services.SlackServices().GetSocketModeClient();

        logger.LogInformation("Socket Mode 연결을 시작합니다.");
        await client.Connect(null, stoppingToken);
        logger.LogInformation("Socket Mode 연결됨. 채널 {ChannelId} 를 담당합니다.",
            slackOptions.Value.ChannelId);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
```

- [ ] **Step 2: 스케줄러 워커 작성**

`src/SelectLunch.Slack/Workers/SchedulerWorker.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Scheduling;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;

namespace SelectLunch.Slack.Workers;

/// <summary>
/// DB 상태를 기준으로 할 일을 찾아 실행한다. 타이머가 아니라 상태 기반이라
/// 앱이 꺼져 있던 구간을 재기동 시 따라잡고, 중복 발송도 누락도 생기지 않는다.
/// </summary>
public sealed class SchedulerWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<LunchOptions> lunchOptions,
    IOptions<SlackOptions> slackOptions,
    ILogger<SchedulerWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NotifyOnOptionsChange();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 한 번의 실패로 루프가 죽으면 그날 점심이 통째로 날아간다
                logger.LogError(ex, "스케줄 처리 중 오류. 다음 주기에 다시 시도합니다.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(lunchOptions.CurrentValue.PollIntervalSeconds),
                stoppingToken);
        }
    }

    async Task TickAsync(CancellationToken ct)
    {
        var options = lunchOptions.CurrentValue;
        var channelId = slackOptions.Value.ChannelId;
        var now = NowIn(options.TimeZone);
        var today = DateOnly.FromDateTime(now.DateTime);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LunchDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<LunchService>();
        var announcer = scope.ServiceProvider.GetRequiredService<LunchAnnouncer>();

        var state = await db.GetTodayStateAsync(channelId, today, ct);

        foreach (var action in LunchSchedule.GetDueActions(now, state, options))
        {
            logger.LogInformation("{Kind} 실행 (예정 {ScheduledFor:HH:mm})", action.Kind, action.ScheduledFor);

            switch (action.Kind)
            {
                case DueActionKind.OpenPoll:
                    var poll = await service.OpenPollAsync(
                        today, action.ScheduledFor,
                        action.ScheduledFor.AddMinutes(options.VoteDurationMinutes), ct);
                    await announcer.PostPollAsync(poll.Id, poll.ClosesAt, ct);
                    break;

                case DueActionKind.ClosePoll:
                    var outcome = await service.ClosePollAsync(
                        state.Poll!.PollId, today, options.Recommendation, ct);
                    await announcer.PostResultAsync(outcome, options.Recommendation, ct);
                    break;

                case DueActionKind.PostMealPrompt:
                    await announcer.PostMealPromptAsync(today, ct);
                    await service.MarkMealPromptPostedAsync(today, now, ct);
                    break;

                case DueActionKind.RemindPending:
                    await announcer.PostPendingReminderAsync(ct);
                    await service.MarkPendingReminderSentAsync(today, now, ct);
                    break;
            }
        }
    }

    /// <summary>설정이 바뀌면 채널에 한 줄 남긴다. 조용한 변경은 추적을 어렵게 한다.</summary>
    void NotifyOnOptionsChange()
    {
        var previous = Snapshot(lunchOptions.CurrentValue);

        lunchOptions.OnChange(options =>
        {
            var current = Snapshot(options);
            if (current == previous)
                return;

            logger.LogInformation("설정 변경 감지: {Previous} → {Current}", previous, current);
            previous = current;
        });
    }

    static string Snapshot(LunchOptions o) =>
        $"투표 {o.VoteOpenAt:HH\:mm}(+{o.VoteDurationMinutes}분) · 기록 {o.MealRecordAt:HH\:mm} · " +
        $"가중치 {o.Recommendation.Weight7d}/{o.Recommendation.Weight30d}";

    /// <summary>설정된 타임존의 현재 시각. 순수 함수에 넘길 기준이 된다.</summary>
    internal static DateTimeOffset NowIn(string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
    }
}
```

- [ ] **Step 3: 타임존 변환 테스트 작성**

`tests/SelectLunch.Slack.Tests/TimeZoneResolutionTests.cs`:

```csharp
using SelectLunch.Slack.Workers;

namespace SelectLunch.Slack.Tests;

public class TimeZoneResolutionTests
{
    [Fact]
    public void 설정된_타임존의_오프셋이_적용된다()
    {
        var now = SchedulerWorker.NowIn("Asia/Seoul");

        Assert.Equal(TimeSpan.FromHours(9), now.Offset);
    }

    [Fact]
    public void 알_수_없는_타임존은_예외를_던진다()
    {
        Assert.ThrowsAny<Exception>(() => SchedulerWorker.NowIn("Not/AZone"));
    }
}
```

`InternalsVisibleTo`가 필요하다. `src/SelectLunch.Slack/SelectLunch.Slack.csproj`에 추가한다.

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="SelectLunch.Slack.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Program.cs 작성**

`src/SelectLunch.Slack/Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Options;
using SelectLunch.Slack;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Handlers;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SelectLunch.Slack.Workers;
using SlackNet;
using SlackNet.Extensions.DependencyInjection;

// 중복 기동을 막는다. 두 인스턴스가 뜨면 자동 메시지가 두 번 나가고 집계가 갈라진다.
if (!SingleInstanceGuard.TryAcquire("slack", out var guard))
{
    Console.Error.WriteLine("이미 실행 중인 인스턴스가 있습니다. 종료합니다.");
    return 1;
}

using (guard)
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    builder.Services.Configure<LunchOptions>(
        builder.Configuration.GetSection(LunchOptions.SectionName));
    builder.Services.Configure<SlackOptions>(
        builder.Configuration.GetSection(SlackOptions.SectionName));

    var slackOptions = builder.Configuration.GetSection(SlackOptions.SectionName).Get<SlackOptions>()
        ?? new SlackOptions();
    slackOptions.Validate();   // 토큰이 없으면 여기서 즉시 실패시킨다

    var dbPath = builder.Configuration["Database:Path"] ?? "data/lunch.db";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

    builder.Services.AddDbContext<LunchDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

    builder.Services.AddScoped(sp => new LunchService(
        sp.GetRequiredService<LunchDbContext>(), slackOptions.ChannelId));
    builder.Services.AddScoped(sp => new LunchAnnouncer(
        sp.GetRequiredService<ISlackApiClient>(),
        sp.GetRequiredService<LunchDbContext>(),
        sp.GetRequiredService<LunchService>(),
        slackOptions.ChannelId));

    builder.Services.AddScoped<LunchSlashCommandHandler>();
    builder.Services.AddScoped<VoteActionHandler>();
    builder.Services.AddScoped<MealActionHandler>();
    builder.Services.AddScoped<RestaurantModalHandler>();
    builder.Services.AddScoped<PendingActionHandler>();

    builder.Services.AddSlackNet(c => c
        .UseApiToken(slackOptions.BotToken)
        .UseAppLevelToken(slackOptions.AppToken)
        .RegisterSlashCommandHandler<LunchSlashCommandHandler>("/lunch")
        .RegisterBlockActionHandler<VoteActionHandler>()
        .RegisterBlockActionHandler<MealActionHandler>()
        .RegisterBlockActionHandler<PendingActionHandler>()
        .RegisterViewSubmissionHandler<RestaurantModalHandler>(RestaurantModal.CallbackId));

    builder.Services.AddHostedService<SocketModeWorker>();
    builder.Services.AddHostedService<SchedulerWorker>();

    var host = builder.Build();

    // 기동 시 스키마 생성/갱신 — 첫 실행에 별도 작업이 필요 없다
    using (var scope = host.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<LunchDbContext>().Database.MigrateAsync();
    }

    await host.RunAsync();
    return 0;
}
```

`appsettings.Local.json`을 `reloadOnChange: true`로 읽지만, `SlackOptions`는
기동 시 한 번만 읽어 쓴다 — 토큰과 채널 ID가 바뀌면 웹소켓을 다시 맺어야 하므로
재기동이 필요하다. `LunchOptions`만 `IOptionsMonitor`로 무중단 반영된다.

- [ ] **Step 5: 빌드와 테스트**

```bash
dotnet build
dotnet test
```

Expected: 빌드 성공, PASS (109 tests)

`AddSlackNet`의 등록 메서드 시그니처가 맞지 않으면 HandMirror로 확인한다:
`SlackNet.dll`의 `SlackServiceConfigurationBase`에 `RegisterSlashCommandHandler`,
`RegisterBlockActionHandler`, `RegisterViewSubmissionHandler`가 있다 (검증 완료).

- [ ] **Step 6: 커밋**

```bash
git add -A
git commit -m "feat: Socket Mode 연결과 스케줄러 워커 구성"
```

---

### Task 18: 운영 문서와 최종 점검

사람이 Slack 앱을 만들고 토큰을 발급해야만 실제로 돌아간다.
그 절차를 문서로 남기고 전체를 점검한다.

**Files:**
- Modify: `README.md`
- Create: `docs/slack-app-setup.md`
- Create: `src/SelectLunch.Slack/appsettings.Local.example.json`

**Interfaces:**
- Consumes: 앞선 모든 task
- Produces: 없음 (문서)

- [ ] **Step 1: 설정 예시 파일 작성**

`src/SelectLunch.Slack/appsettings.Local.example.json` — 실제 파일은 gitignore되므로
예시를 커밋해 둔다.

```json
{
  "Slack": {
    "BotToken": "xoxb-여기에-봇-토큰",
    "AppToken": "xapp-여기에-앱-레벨-토큰",
    "ChannelId": "C0여기에-잠금채널-ID"
  }
}
```

- [ ] **Step 2: Slack 앱 설정 문서 작성**

`docs/slack-app-setup.md`:

```markdown
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
대상이 **잠금(private) 채널**이므로 `channels:*`가 아니라 `groups:*`다.

| 스코프 | 용도 |
|---|---|
| `commands` | `/lunch` 슬래시 커맨드 |
| `chat:write` | 메시지 발송·갱신 |
| `groups:read` | 잠금 채널 정보 조회 |
| `groups:history` | 잠금 채널 메시지 조회 |
| `users:read` | 사용자 표시 이름 |

## 4. 슬래시 커맨드 등록

**Features → Slash Commands → Create New Command**

- Command: `/lunch`
- Description: `점심 투표와 식당 관리`
- Usage Hint: `add | list | edit | pending | stats | today | help`

Socket Mode를 쓰므로 Request URL은 비워 둔다.

## 5. 상호작용 활성화

**Features → Interactivity & Shortcuts** 를 켠다.
Socket Mode에서는 Request URL이 필요 없다.

## 6. 워크스페이스에 설치

**Settings → Install App** → 설치하면 `xoxb-...` 봇 토큰이 나온다.
`appsettings.Local.json`의 `Slack:BotToken`에 넣는다.

## 7. 채널에 초대

봇이 동작할 잠금 채널에서 `/invite @봇이름` 을 실행한다.
**이 단계를 빠뜨리면 메시지 발송이 실패한다.**

채널 ID는 채널 이름 클릭 → 하단의 Channel ID(`C`로 시작)를 복사해
`Slack:ChannelId`에 넣는다.
```

- [ ] **Step 3: README 작성**

`README.md`를 다음으로 교체한다.

```markdown
# Select-Lunch

점심 고르자. 슬랙 채널에서 점심 투표를 열고, 알고리즘으로 식당을 추천하는 봇.

## 하루 흐름

    10:30  투표 개시 — 등록된 식당 전체가 후보
    11:00  마감 → 투표 1위 + 알고리즘 추천 (선정 근거 포함)
    13:30  기록 요청 — 실제로 먹은 곳을 남기면 다음 추천이 정확해진다

시각은 모두 설정으로 바꿀 수 있고, **앱 재기동 없이 반영**된다.

## 추천 알고리즘

랜덤을 쓰지 않는다. 카테고리별로 점수를 매겨 결정적으로 고른다.

    score = D − (W7 × N7 + W30 × N30)

      D   = 마지막으로 먹은 날로부터 경과일 (상한 30, 미방문도 30)
      N7  = 최근 7일 식사 횟수     (기본 가중치 3)
      N30 = 최근 30일 식사 횟수    (기본 가중치 1)

점수가 가장 높은 카테고리에서, 마지막 방문이 가장 오래된 식당을 고른다.
결과만이 아니라 **계산 과정 전체를 메시지에 노출**한다.

## 시작하기

1. [Slack 앱 설정](docs/slack-app-setup.md)을 따라 토큰을 발급한다
2. `src/SelectLunch.Slack/appsettings.Local.example.json`을
   `appsettings.Local.json`으로 복사하고 토큰·채널 ID를 채운다
3. 실행한다

```bash
dotnet run --project src/SelectLunch.Slack
```

DB(`data/lunch.db`)는 기동 시 자동 생성된다. 별도 준비가 필요 없다.

## 명령어

| 명령 | 동작 |
|---|---|
| `/lunch add [이름]` | 식당 등록 |
| `/lunch list` | 등록된 식당 목록 |
| `/lunch edit` | 식당 수정 |
| `/lunch pending` | 카테고리가 비어 추천에서 빠진 식당 |
| `/lunch stats` | 카테고리별 현재 점수 |
| `/lunch today` | 오늘 투표 상태 |

## 구조

    src/SelectLunch.Shared    추천 알고리즘·스케줄 판정·DB (플랫폼 의존성 0)
    src/SelectLunch.Slack     SlackNet Socket Mode Worker

`Shared`는 Slack을 모른다. Teams head를 나중에 붙일 수 있게 한 경계다.

## 알아둘 점

- **한 번에 하나만 띄운다.** 중복 기동은 뮤텍스로 막히지만, 다른 PC에서
  같은 채널에 붙이면 메시지가 두 번 나간다.
- **토큰과 채널 ID는 핫리로드되지 않는다.** 바꾸면 재기동해야 한다.
- 공휴일은 `appsettings.json`의 `Lunch:Holidays`에 날짜를 적어 관리한다.

## 개발

```bash
dotnet test                                  # 전체 테스트
dotnet ef migrations add <이름> --project src/SelectLunch.Shared --output-dir Data/Migrations
```

설계 문서: [docs/superpowers/specs/2026-09-18-select-lunch-design.md](docs/superpowers/specs/2026-09-18-select-lunch-design.md)
```

- [ ] **Step 4: 전체 점검**

```bash
dotnet build
dotnet test
git status --short
```

Expected: 빌드 성공, 전체 테스트 통과, `data/`와 `appsettings.Local.json`이
`git status`에 **나타나지 않음** (gitignore 확인).

- [ ] **Step 5: 커밋**

```bash
git add -A
git commit -m "docs: README와 Slack 앱 설정 가이드 추가"
```

---

## 완료 기준

- [ ] `dotnet test` 전체 통과
- [ ] `git status`에 DB 파일·로컬 설정이 올라오지 않음
- [ ] `RecommendationEngine`이 같은 입력에 항상 같은 결과를 냄 (랜덤 금지)
- [ ] 앱을 껐다 켜도 투표가 중복 발송되지 않음 (`LunchSchedule` 테스트로 보장)
- [ ] 추천 메시지에 점수 계산 과정이 드러남
- [ ] `appsettings.json`의 시각을 바꾸면 재기동 없이 반영됨

## 실제 동작 확인 (사람이 해야 하는 단계)

코드만으로는 끝나지 않는다. Slack 앱 생성·토큰 발급·채널 초대는 대신할 수 없으므로,
구현 완료 후 사용자에게 다음을 요청한다.

1. `docs/slack-app-setup.md`대로 앱을 만들고 토큰을 발급
2. `appsettings.Local.json` 작성
3. `dotnet run --project src/SelectLunch.Slack` 실행
4. 채널에서 `/lunch help` → 도움말이 뜨는지 확인
5. `/lunch add 테스트식당` → 모달이 열리고 저장되는지 확인
6. `appsettings.json`의 `VoteOpenAt`을 몇 분 뒤로 바꿔 투표가 자동으로 열리는지 확인
