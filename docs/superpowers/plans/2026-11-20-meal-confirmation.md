# 실제 방문 식당 복수 확정 구현 계획

> 실행: 독립된 저장·슬랙 입력 작업을 분리한다. 체크 항목은 테스트와 배포 결과로 닫는다.

**목표:** 1인 1표를 유지하면서 기존 식사 기록 요청에서 기준 인원에 따라 하나 이상의 식당을 확정한다.

**구조:** 투표·마감 경로는 건드리지 않는다. 쓰기 정책은 한 서비스와 SQLite 트랜잭션에 두고, 기존 식당 선택과 신규 등록은 각각 같은 서비스를 호출한다.

**기술:** 기존 닷넷, EF Core SQLite, SlackNet 0.18.0, xUnit. 새 패키지 없음.

**설계:** [식사 확정 설계](../specs/2026-11-20-meal-confirmation-design.md)

## 공통 조건

기준 `Lunch:MealParticipantsPerRestaurant` 기본 5, 양수. 과거 날짜 예외 `Lunch:CarryoverMealPromptDate` 기본 미지정. 운영 예외는 지정 채널의 11월 19일 미기록 요청 1회뿐이다. 메시지 재게시·운영 시험 표·시험 식사 기록 금지. 과거 행과 투표 결과 보존.

### 작업 1: 기록 정책과 저장

- [ ] `src/SelectLunch.Shared/Options/LunchOptions.cs`, `Data/LunchDbContext.cs`, `Entities/MealRecord.cs`에 설정과 식당 단위 고유키를 반영한다. `Services/LunchService.cs`의 단건 쓰기는 제거한다. `src/SelectLunch.Slack/Services/MealConfirmationService.cs`에서 다음 계약을 제공한다.

```csharp
// 생성자: LunchDbContext db, LunchService restaurants, string channelId,
//         IOptionsMonitor<LunchOptions> options, TimeProvider clock
public sealed record MealPlace(long Id, string Name);
public sealed record MealSelectionState(bool Allowed, bool IsToday, int Limit, IReadOnlyList<MealPlace> Recorded);
public sealed record MealSaveResult(string? Error, IReadOnlyList<MealPlace> Places, Restaurant? Registered, bool IsToday);
// MealConfirmationService.ChannelId; GetStateAsync(DateOnly date, CancellationToken ct)
// SaveAsync(DateOnly date, IReadOnlyList<long> restaurantIds, string userId, CancellationToken ct,
//           RestaurantDraft? registration = null, string? displayName = null) -> Task<MealSaveResult>
```

- [ ] 한도는 `count == 0 ? 1 : 1 + (count - 1) / threshold`로 계산한다. 마감 본투표의 null이 아닌 식당표만 센다. 지난 미기록·게시 이력·지정일, 선택 수, 중복, 실제 식당을 쓰기 트랜잭션 안에서 검사한다. 신규 등록은 같은 트랜잭션에 묶는다. 날짜·식당별 방문 행을 교체하되 중간 실패 시 전체를 보존한다.
- [ ] `tests/SelectLunch.Slack.Tests/Services/MealConfirmationServiceTests.cs`에서 0·5·6·10·11명 및 기준 4, 기권·비밀표, 다른 채널, 두 곳→한 곳 정정, 지난 요청의 첫 저장 뒤 재제출, 거절된 등록의 원자성을 검사한다. `tests/SelectLunch.Shared.Tests/Data/MigrationTests.cs`에서는 기존 행을 보존한 DB에 두 번째 식당을 추가하고 추천 집계를 읽는다.

### 작업 2: 기존 선택 메시지와 확정 창

- [ ] `Handlers/MealActionHandler.cs`, `Blocks/MealConfirmationModal.cs`, `Handlers/MealConfirmationModalHandler.cs`, `Handlers/RestaurantOptionProvider.cs`가 메시지에서 한 곳을 고른 직후에는 쓰지 않고 모달을 연다. `meal_new`는 작업 3의 `RestaurantModal.BuildForMeal(categories, date, channelId, userId, state)`를 사용한다. 검색은 기존 Active 식당 검색을 쓴다.
- [ ] 확정 창의 공용 계약: `PlacesInput(int limit, IReadOnlyList<MealPlace> initial, bool optional)`, `TryReadPlaces(ViewSubmission submission, out long[] ids)`, `Metadata(DateOnly date, string channel, string user)`, `TryDate(string? metadata, string channel, string user, out DateOnly date)`, `PlacesBlockId`, `PlacesActionId`, `CallbackId`. 빈 선택과 잘못된 ID를 구분한다. 날짜·사용자·원본 채널을 대조하고 모달의 저장만 `SaveAsync`에 전달한다. 거절은 개인 오류, 성공은 전체 식당 안내다.
- [ ] 기존 버튼·정적/외부 검색 테스트를 새 계약으로 바꾼다. 클릭 직후 `Assert.False(await fixture.Db.MealRecords.AnyAsync(ct))`, 모달에 고른 식당이 포함됐는지 확인한다. 보통 날짜·기준 변경·과거 요청도 같은 진입 경로로 다룬다. 식사 없는 취소로 미리 출발 안내를 종료하지 않는다.

### 작업 3: 신규 식당을 포함한 확정

- [ ] `Blocks/RestaurantModal.cs`, `Handlers/RestaurantModalHandler.cs`가 식사 기록용 등록과 일반 등록·수정을 구분한다. 기록용 창에서 새 식당 한 곳을 예약하고 남은 한도에 한해 작업 2의 선택 입력을 추가한다. 저장할 때 `SaveAsync(date, extras, userId, ct, draft, displayName)`를 호출한다. 거절되면 후보 갱신·공개 알림·식당만 따로 등록되는 일이 없다. 지난 날짜의 최초 식사 기록은 오늘 투표에 식당을 추가하지 않는다.
- [ ] 기존 등록 시험과 기록 전용 제출 시험에서 부가 식당, 중복, 과거 요청 제한, 카테고리 없는 신규 식당, 정상 일반 등록을 확인한다.

### 작업 4: 조립, 마이그레이션, 운영 적용

- [ ] `Program.cs`에서 기준 양수 검사·서비스·제출 핸들러를 등록한다. `LunchAnnouncer.PostMealPromptAsync`의 단건 조회를 전체 이름 조회로 바꾼다. `MealPromptBlocks`는 기록 전체와 선택 후 확정 동작을 표시한다. 기존 테스트용 단건 쓰기 호출도 새 계약 또는 명시적 데이터 준비로 치환한다.
- [ ] `dotnet ef migrations add AllowMultipleMealRestaurants --project src/SelectLunch.Shared --output-dir Data/Migrations`로 모델과 마이그레이션을 맞춘다. `dotnet test`의 관련 테스트 필터로 마이그레이션·서비스·모달·출발 안내 경로를 실행한다. 런타임 파일은 `docs/deployment.md`의 리눅스 배포 명령으로 만든다.
- [ ] 사용법·운영 인계 기록을 갱신하고 검증한 브랜치를 병합·푸시한다. 실행 파일·설정·SQLite 백업 사본과 무결성을 확인한 후에만 배포한다. 설정·토큰 보존, 지정된 미확정 날짜, 새 인덱스·기존 데이터, 실행 중 해시·단일 서비스·소켓 연결을 확인한다. 슬랙에서는 기존 확정 창만 열어 보고 취소한다.
