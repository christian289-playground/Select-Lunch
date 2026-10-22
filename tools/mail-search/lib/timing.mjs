// 화면 폴링·안정화 시간(ms). 테스트는 더 짧은 값으로 덮어쓴다.
export const DEFAULT_TIMING = Object.freeze({
  pollMs: 250, // 화면 상태 확인 간격
  settleMs: 1200, // 결과(행 또는 없음 표시)가 이 시간 동안 바뀌지 않아야 "검색 끝"으로 본다
  scrollSettleMs: 700, // 스크롤 뒤 새 행이 붙기를 기다리는 시간
  endIdleRounds: 3, // 맨 아래에서 새 행이 연속 이만큼 없어야 "끝"으로 본다
  paneSettleMs: 500, // 읽기 창 내용이 이 시간 동안 같아야 본문을 읽은 것으로 본다
  loginGraceMs: 5000, // 로그인 주소에 이 시간 이상 머물러야 "로그인 필요"로 본다(조용한 SSO 이동 허용)
  loginPollMs: 1000, // 로그인 모드에서 사용자가 로그인했는지 확인하는 간격
  treeWaitMs: 8000, // 검색창이 뜬 뒤 폴더 트리(계정 항목)가 나타나기를 기다리는 시간
});

export const DEFAULT_LIMITS = Object.freeze({
  maxScrollRounds: null, // null 이면 maxResults 로 계산
  serverResultLimit: 1000, // Outlook 웹 검색은 처음 1,000건만 돌려준다(공식 문서)
});

export function defaultMaxScrollRounds(maxResults) {
  return Math.min(800, Math.ceil(maxResults / 2) + 40);
}
