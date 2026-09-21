# 알려진 한계와 미처리 관찰

구현 중 리뷰에서 나왔으나 **의도적으로 고치지 않은** 항목들이다. 전부 실사용을 막지
않는다고 판단한 것들이고, 각각 왜 미뤘는지와 고칠 때 어디를 보면 되는지를 남긴다.

최종 전체 리뷰 판정: **실제 Slack 워크스페이스에서 사용 가능.** 실패 모드가 전부
메시지 수준(하나 빠지거나 하나 더 나감)이고 데이터 손상·유실 경로는 없다.

---

## 실제로 겪을 수 있는 것

**결과 발표 후 기록 실패 시 중복 발표**
`SchedulerWorker`가 `PostResultAsync` 성공 뒤 `MarkResultAnnouncedAsync`에서 실패하면
`ResultAnnouncedAt`이 null로 남아 다음 tick에 결과를 한 번 더 보낸다. 발표 실패 시
재시도를 넣으면서 생긴 반대편 창이다. 창이 매우 좁고 피해는 메시지 중복 하나.

**설정 핫리로드 중 개시·발표가 같은 tick에 겹칠 가능성**
`VoteOpenAt`/`VoteDurationMinutes`를 투표 진행 중에 바꾸면, 새 옵션으로 계산한
마감 시각이 이미 저장된 `LunchPoll.ClosesAt`과 어긋나 두 액션이 동시에 나올 수 있다.
설정을 점심 시간대에 바꾸지 않으면 발생하지 않는다.

**동시 더블클릭**
같은 사람이 투표 버튼을 빠르게 두 번 누르면 `(PollId, SlackUserId)` 유니크 제약
위반이 날 수 있다. 예외는 삼켜지고 화면만 갱신되지 않는다. 다시 누르면 정상 동작.

**다른 PC에서 중복 기동**
같은 PC는 뮤텍스로 막지만, 다른 PC에서 같은 채널에 붙이면 막을 방법이 없다.
정시 메시지가 두 번 나가고 집계가 갈라진다. 운영 규칙으로 관리해야 한다.

---

## 설계상 남겨둔 것

**`LastEatenOn`에 미래 날짜 상한이 없다**
`GetCategoryStatsAsync`가 `Max(Date)`를 상한 없이 취한다. 미래 날짜 기록이 들어오면
그 카테고리가 날짜가 지날 때까지 추천 순위 바닥으로 밀린다. 다만 UI 경로로는
미래 날짜가 생길 수 없고(기록 날짜는 프롬프트 발송 시점의 오늘), `DaysSince`의
`[0, cap]` 클램프가 피해를 가둔다. 방어가 필요하면 읽기 쿼리가 아니라
`RecordMealAsync`(쓰기 경로)에 `Date <= today` 검증을 넣는 것이 옳은 위치다.

**동점 종결 키가 이름에 의존**
카테고리 순위의 마지막 tie-break가 `CategoryName`이라 유일성을 DB의 `UNIQUE(Name)`
제약에 기댄다. `.ThenBy(s => s.CategoryId)` 한 줄이면 순수 함수가 자기완결적으로
끝난다. 같은 패턴이 `RecommendationEngine`, `LunchService`, `ResultBlocks`,
`PollBlocks`에 있다.

**`At()`이 `now.Offset`을 빌려 쓴다**
`LunchSchedule.At`이 예정 시각을 만들 때 현재 시각의 오프셋을 그대로 쓴다.
한국은 서머타임이 없어 안전하지만, DST가 있는 지역으로 옮기면 전환일 인근에
한 시간 어긋난다. 그때는 `TimeZoneInfo`로 해당 날짜의 오프셋을 계산하도록 바꿔야 한다.

**`/lunch today`가 상태 한 줄만 출력**
설계 문서 §8은 "오늘 투표·결과·추천 다시 보기"라고 적었으나 구현은 투표 상태와
마감 시각만 보여준다.

**마감 결과 메시지의 `*투표 1위*` 라벨**
1위가 없는 경우에도 이 라벨이 붙는다. 세 분기가 공통 라벨을 쓰고 있어 고치려면
범위가 조금 커진다.

---

## 테스트 커버리지 공백

실제 동작은 확인됐으나 회귀를 잡아줄 테스트가 없는 것들.

- `OpenPollAsync`의 트랜잭션 — 성공 경로만 검증. 트랜잭션을 걷어내도 통과한다.
  실패 주입이 공개 API로 불가능해 테스트 훅이 필요하다.
- 캐스케이드 삭제 차단 — `MealRecord` 경로만 테스트. `PollCandidate`/`PollVote`의
  `Restrict`는 설정 대칭성에만 의존한다.
- `LunchClockTests` — 핸들러를 호출하지 않아 핸들러가 `LunchClock`을 무시해도 통과한다.
  실제 자정~09:00 경계는 시계 주입 없이 재현 불가.
- `SlackNetLoggerAdapter` — 테스트 없음. 가장 값진 테스트는
  `ToLogLevel(LogCategory.Error) == LogLevel.Error`와 `Exception` 전달 여부.
- 상한 경계값 — `DaysSinceCap`이 정확히 30인 경우, 드롭다운 후보가 정확히 100곳인
  경우가 코드 판독으로만 확인됨.

---

## 사소한 것

- `RationaleJson`에 `Winner`/`Others`/`Pick`은 있으나 계산에 쓴 가중치는 없다.
  나중에 가중치를 바꾸면 과거 근거를 그 시점 설정으로 재현할 수 없다.
- `LunchQueries.GetCategoryStatsAsync`가 매번 전체 `MealRecords`를 메모리로 읽는다.
  하루 1건이라 수년간 무해하지만 무경계 쿼리인 것은 사실이다.
- `RestaurantModalHandler`가 view_submission 응답 3초 예산 안에서 `chat.postMessage`
  왕복을 수행한다. 슬랙이 느릴 때 타임아웃 여지가 있다.
- `AllowVoteAbstention` 마이그레이션의 `Down()`이 `defaultValue: 0L`을 쓴다.
  기권 행이 있는 상태로 다운그레이드하면 FK 위반이 난다. 운영에서 실행할 일은 없다.
- 기권자 명단 위치가 후보 0곳 경로와 1곳 이상 경로에서 다르다.
- 한글 정규화는 NFC만 적용한다. 식당명에 전각/반각 숫자가 섞이면 별개 행이 된다.

---

## 사람이 해야 하는 일

코드로는 대신할 수 없다. [docs/slack-app-setup.md](slack-app-setup.md) 참고.

1. Slack 앱 생성, Socket Mode 활성화, 토큰 2종 발급
2. `appsettings.Local.json` 작성
3. **봇을 대상 채널에 초대** — 빠뜨리면 발송이 전부 실패한다
4. 첫 며칠 로그 관찰 — 특히 금요일 16시 보완 요청과 재기동 직후 동작
