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
