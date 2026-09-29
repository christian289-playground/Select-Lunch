# Select-Lunch

슬랙 점심 투표 봇. `SelectLunch.Shared`(플랫폼 무관: 추천·스케줄·DB) +
`SelectLunch.Slack`(SlackNet Socket Mode Worker). 솔루션은 `.slnx`.

## 빌드가 깨지는 함정

- `global.json`의 `test.runner = Microsoft.Testing.Platform` 없으면 `dotnet test`가
  "VSTest target is no longer supported"로 실패한다 (xunit.v3 + .NET 10 SDK).
- `TreatWarningsAsErrors=true`: 미사용 생성자 매개변수(CS9113)가 빌드를 깬다.
  async 테스트는 `TestContext.Current.CancellationToken`을 넘겨야 한다 (xUnit1051).
- `DebugType=embedded` — 별도 `.pdb`가 나오지 않는다.

## 런타임에만 터지는 것 (컴파일은 통과)

- **`DateTimeOffset` 컬럼으로 서버측 정렬 금지.** EF Core SQLite가 번역 못 해
  `NotSupportedException`. `ToListAsync()` 후 메모리에서 정렬할 것. `DateOnly`는 무관.
- **`OptionGroup.Options`는 자동 초기화 안 됨** — 만들면 `Options = []` 필수.
- 기본 카테고리 7종(한식·중식·일식·양식·분식·아시안·기타, Id 1~7)이 `HasData`로
  시드되고 `TestDb`가 이를 적용한다. **테스트에서 같은 이름을 다시 삽입하면 UNIQUE 위반.**
  단 **닫힌 집합이 아니다** — 자유 입력으로 추가되며 그건 `IsBuiltIn=false`로 들어간다.
- **`NormalizedName`을 손으로 만들지 말 것.** `Restaurant.Normalize`/`Category.Normalize`가
  NFC → 공백 제거 → 소문자 순서로 만들고 UNIQUE가 걸린다. 규칙이 어긋난 행은 중복을
  막지 못하거나 조회에서 새는데, **넣는 시점엔 아무 오류도 나지 않는다.**
- **`Status == Active` ⟺ `CategoryId != null`.** 코드 여러 곳이 이 동치에 기댄다.
  한쪽만 맞춘 행은 투표 후보·추천에서 조용히 빠진다.

## SlackNet 0.18.0 실측 사실

- `ModalViewDefinition`·`ViewState`·`ViewInfo`는 루트 `SlackNet`에 있다.
  `Option`이 `SlackNet`/`SlackNet.Blocks` 양쪽에 있어 둘 다 열면
  `using Option = SlackNet.Blocks.Option;` 필요. `OptionGroup`·`Button`도 동일.
- `SlashCommandResponse.Null`과 `ViewSubmissionResponse.Errors(...)`는 **없다.**
  입력 오류는 `ViewErrorsResponse`, 정상 종료는 `ViewSubmissionResponse.Null`(정적 프로퍼티).
- 발송은 `Message.Channel`, 갱신은 `MessageUpdate.ChannelId` + `Ts`. 이름이 다르다.
- SlackNet 로거 기본값은 `NullLogger` — `UseLogger`로 연결하지 않으면 재연결 실패가
  앱 로그에 전혀 안 남는다.

## 설계상 지켜야 할 것

- **추천에 랜덤 금지** (사용자 요구). `Random`/`Guid.NewGuid` 사용 불가, 모든 정렬은
  유일 키까지 tie-break할 것. 시각은 주입하고 `DateTime.Now`를 직접 부르지 않는다.
- `SelectLunch.Shared`는 Slack/Teams 패키지를 참조하지 않는다.
- 타임존 변환은 `LunchClock` 하나만 쓴다.
- **운영 설정에서 EF Core SQL 로그를 켜지 말 것.** 스케줄러가 `PollIntervalSeconds`
  (기본 30초)마다 상태를 조회해서, `Microsoft.EntityFrameworkCore.Database.Command`를
  `Information`으로 두면 같은 SELECT가 끝없이 쌓인다. `appsettings.json`은 `Warning`을
  유지하고 필요하면 `appsettings.Development.json`에서만 올린다.
- **DB에 직접 SQL을 쓰지 말 것.** 데이터 투입은 `tools/seed-restaurants`가 실제 엔티티와
  `Normalize`를 태워서 넣는다. 위 두 규약이 코드와 어긋날 수 없게 하려는 장치다.
- `.gitignore`에 앵커 없는 `data/` 금지 — Windows에서 소스 폴더 `Data/`까지 가린다.

## 명령

```bash
dotnet test
dotnet ef migrations add <이름> --project src/SelectLunch.Shared --output-dir Data/Migrations
dotnet publish src/SelectLunch.Slack/SelectLunch.Slack.csproj -c Release -r linux-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:PublishTrimmed=false
```

**Native AOT·트리밍은 불가** — EF Core가 IL2026/IL3050, SlackNet이 Newtonsoft.Json 의존.
재조사 불필요.
