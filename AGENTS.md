# Select-Lunch

슬랙 점심 투표 봇. `SelectLunch.Shared`(플랫폼 무관: 추천·스케줄·DB) +
`SelectLunch.Slack`(SlackNet Socket Mode Worker). 솔루션은 `.slnx`.

## 기능 개발 작업 방식

- **신규 기능은 항상 별도 기능 브랜치에서 개발하고, 검증 후 `main`에 병합한다.**
  `main`에서 기능 코드를 직접 수정하지 않는다.
- **기능 개발은 사용자가 Slack에서 직접 검증할 수 있도록 원격 푸시·운영 배포까지 완료한다.**
  별도 배포 요청을 기다리지 않는다. 검증 통과 → `main` 병합·푸시 → 운영 백업 → 배포·기동 확인 순서다.
- 배포 전 실행 파일과 SQLite 백업 API로 만든 일관된 DB 사본을 반드시 보관한다.
  백업 실패 시 배포하지 않는다. 운영 설정·토큰은 보존하고, 배포 후 실행 파일 해시·DB 마이그레이션·
  단일 서비스 인스턴스·Slack Socket Mode 연결을 확인한다. 상세 절차는 `docs/deployment.md`를 따른다.
- 작업 시작 전에 기존 변경을 확인하고, 다른 작업의 수정·미추적 파일은 커밋에 섞지 않는다.

## 비밀 랜덤 투표

- `PollVote.IsSecret`인 표도 `RestaurantId`를 즉시 저장한다. 마감 전에는 식당별 득표에서
  제외하고 같은 득표 목록에 `비밀이에요 — N표`로 표시한다.
  `🤫 비밀 투표` 버튼으로 저장한 뒤, 본인 전용 접수 안내의 `👀 내가 뽑은 식당 보기`를 누를 때만
  식당을 본인에게 공개한다. 확인은 읽기 전용이며 재추첨·표 추가·공개 집계 변경을 하지 않는다.
  접수 안내는 `VotedAt.UtcTicks` revision에 묶고, 본인 ID·비밀 상태·revision이 모두 맞아야 공개한다.
  일반 랜덤·카테고리 랜덤·비밀 랜덤 UI는 별도 랜덤 섹션으로 묶고 기권과 분리한다.
- 마감할 때 실제 식당별 득표에 포함한다. 재시작·재발표·재마감 때 재추첨하지 않는다.
- 일반 투표·일반 랜덤·기권으로 변경하면 `IsSecret=false`. 기존 1인 1표 행을 갱신한다.

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
- **외부 URL을 `ImageBlock.ImageUrl`로 싣지 말 것.** 슬랙 서버가 그 URL을 직접 가져오는데,
  우리 쪽에서 200으로 열리는 주소도 슬랙이 못 받으면 `invalid_blocks`로 **메시지 전체가
  거절된다**(카카오 CDN에서 실측). 이미지는 받아서 파일로 올린다.
- 파일 업로드는 `slack.Files.Upload(FileUpload, channelId, threadTs, initialComment, ct)`
  하나면 된다 — `files.getUploadUrlExternal`/`completeUploadExternal` 3단계를 직접 부를
  필요 없다. `FileUpload(fileName, byte[])` + `Title`/`AltText`, `threadTs`에는 **부모
  메시지의 ts**(답글 ts 아님). 스코프는 `files:write`.
- **외부 선택(`ExternalSelectMenu`)의 제안 응답은 `ReplaceBlockOptionProviding<T>()`로 받는다.**
  `RegisterBlockOptionProvider<T>(actionId)`는 action_id가 **정확히** 일치해야 해서,
  `vote_select:{pollId}`처럼 값이 박힌 id에는 쓸 수 없다. 검색어는
  `BlockOptionsRequest.Value`(선언은 `OptionsRequestBase`에 있다), 응답의
  `BlockOptionsResponse.Options`는 `SlackNet.Blocks.Option`이다.
  선택 payload 타입은 `StaticSelectAction`이 아니라 **`ExternalSelectAction`** 으로 바뀐다 —
  둘 다 받지 않으면 배포 전에 올라간 메시지의 선택이 조용히 무시된다.
  소켓 모드라 Options Load URL은 필요 없고 추가 스코프도 없다.
- 슬랙 옵션 `text`는 **75자** 상한이다. 넘기면 제안 응답 전체가 거부돼 드롭다운이
  비어 보인다(조용한 실패). `RestaurantSearch.Label`이 잘라서 넘긴다.

## 설계상 지켜야 할 것

- **추천에 랜덤 금지** (사용자 요구). `Random`/`Guid.NewGuid` 사용 불가, 모든 정렬은
  유일 키까지 tie-break할 것. 시각은 주입하고 `DateTime.Now`를 직접 부르지 않는다.
  단 **랜덤 투표**(`LunchService.CastRandomVoteAsync`, 카테고리 드롭다운·🎲 버튼)는 추천이
  아니라 사용자가 요청한 "아무거나" 기능이라 `Random`을 쓴다. 추천 쪽으로 새지 않게
  `Random`은 `LunchService`(주입, 테스트에서 시드 고정)에만 둘 것.
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
