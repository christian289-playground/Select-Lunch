// 실제 Chromium + 합성 Outlook 화면으로 검색 흐름 전체(계정 게이트·검색식·결과 확정·가상 스크롤·본문·로그인)를 검증한다.
// 외부 통신은 없다. 모든 주소·이름은 가짜다.

import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { DEFAULT_QUERY } from '../lib/search-expression.mjs';
import { ACCOUNT, createHarness, mailRow } from './support/synthetic-outlook.mjs';

let harness;
before(async () => {
  harness = await createHarness();
});
after(async () => {
  await harness?.close();
});

const M1 = mailRow('M1', '홍길동', '휴가 신청합니다', '10월 연차 사용 예정', '2026-10-20');
const M2 = mailRow('M2', '김철수', '연차 사용 안내', '다음 주 연차', '2026-10-21');
const M3 = mailRow('M3', '홍길동', '점심 약속', '내일 점심 어때요', '2026-10-20');
const M4 = mailRow('M4', '박민수', '반차 신청', '오후 반차', '2026-11-02');
const M5 = mailRow('M5', '이영희', '회의록', '주간 회의', '2026-10-20');
const M6 = mailRow('M6', '최지우', '휴가 복귀', '복귀 인사', '2026-09-30');

const texts = (output) => output.messages.map((m) => m.text);
const failure = (expectedCode, exitCode) => (error) => {
  assert.equal(error.code, expectedCode);
  assert.equal(error.exitCode, exitCode);
  return true;
};

function virtualCorpus(count) {
  return Array.from({ length: count }, (_, i) => {
    const n = String(i + 1).padStart(3, '0');
    return mailRow(`V${n}`, `발신자 ${n}`, `휴가 일정 ${n}`, `일정 안내 ${n}`, '2026-10-15');
  });
}

// ───────────────────────────── 검색식과 결과 ─────────────────────────────

test('검색어 묶음 OR · 이름 묶음 AND: 화면의 검색 서버가 해석한 결과가 의도와 같다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1, M2, M3, M4, M5] });
  const output = await harness.run({ names: ['홍길동', '김철수'] });

  assert.equal(output.mode, 'search');
  assert.equal(output.account, ACCOUNT);
  assert.equal(output.searchExpression, `(${DEFAULT_QUERY.join(' OR ')}) AND (홍길동 OR 김철수)`);
  assert.equal(harness.state.searches.length, 1);
  assert.equal(harness.state.searches[0].expression, output.searchExpression);
  assert.equal(harness.state.searches[0].scope, '모든 폴더', '검색 전에 범위를 모든 폴더로 바꿔야 한다');
  assert.equal(harness.state.serverError, null);

  // 키워드만 맞는 M4, 이름만 맞는 M3 는 빠져야 한다.
  assert.equal(output.messages.length, 2);
  assert.ok(texts(output)[0].includes('휴가 신청합니다'));
  assert.ok(texts(output)[1].includes('연차 사용 안내'));
  assert.deepEqual(output.result, { returned: 2, truncated: false, incompleteReason: null });
  assert.deepEqual(Object.keys(output.messages[0]), ['text']);
});
test('설명형 검색창 이름에서도 로그인 계정 확인과 검색을 진행한다', { timeout: 60_000 }, async () => {
  for (const searchLabel of ['전자 메일, 모임, 파일 등을 검색합니다.', 'Search mail and people']) {
    harness.use({ corpus: [M1], searchLabel });
    const login = await harness.run({ login: true, loginTimeoutSeconds: 2 });
    assert.equal(login.status, 'ready');
    const result = await harness.run({ timeoutSeconds: 3 });
    assert.equal(result.result.returned, 1);
    assert.ok(result.messages[0].text.includes('휴가 신청합니다'));
  }
});
test('검색창이 보인 뒤 초기화 과정에서 입력이 지워져도 준비가 끝난 뒤 검색한다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], searchReadyDelayMs: 1000 });
  const output = await harness.run({ timeoutSeconds: 3 });
  assert.equal(output.result.returned, 1);
  assert.ok(output.messages[0].text.includes('휴가 신청합니다'));
  assert.equal(harness.state.searches.length, 1);
});



test('받은 날짜 범위가 검색식에 문서 형식으로 실린다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1, M2, M4, M6] });
  const output = await harness.run({ receivedAfter: '2026-10-20', receivedBefore: '2026-10-22' });
  assert.match(output.searchExpression, / AND received:10\/20\/2026\.\.10\/21\/2026$/);
  assert.deepEqual(
    texts(output).map((t) => t.split(' ')[0]),
    ['홍길동', '김철수'],
  );
  assert.deepEqual(output.result, { returned: 2, truncated: false, incompleteReason: null });
});

test('날짜 불일치: 서버가 날짜를 무시해 범위 밖 행이 오면 빼고 불완전로 표시한다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1, M4, M2, M6], matchExpression: false });
  const output = await harness.run({ receivedAfter: '2026-10-20', receivedBefore: '2026-10-22' });
  assert.deepEqual(
    texts(output).map((t) => t.split(' ')[0]),
    ['홍길동', '김철수'],
    '범위 밖(11-02, 09-30) 행은 결과에 없어야 한다',
  );
  assert.deepEqual(output.result, { returned: 2, truncated: true, incompleteReason: 'dateBoundaryMismatch' });
});

test('결과 없음은 화면의 "없음" 문구가 확인될 때 returned 0 / 불완전 아님', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M5] });
  const output = await harness.run({});
  assert.deepEqual(output.messages, []);
  assert.deepEqual(output.result, { returned: 0, truncated: false, incompleteReason: null });
});
test('실제 Outlook처럼 결과 표식이 제목이 아닌 그룹이어도 검색 결과를 확인한다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], resultGroup: true });
  const output = await harness.run({ timeoutSeconds: 2 });
  assert.equal(output.result.returned, 1);
  assert.ok(output.messages[0].text.includes('휴가 신청합니다'));
});


test('행이 아직 없다고 비었다고 추정하지 않는다: 로딩이 길어도 기다렸다가 결과를 낸다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1, M2], loadingMs: 1200, busyWhileLoading: false });
  const output = await harness.run({});
  assert.equal(output.result.returned, 2);
  assert.equal(output.messages.length, 2);
});

test('검색 전 목록이 남아 있어도(진행 표시 없음) 그것을 결과로 오인하지 않는다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1, M2], keepInboxWhileLoading: true, busyWhileLoading: false, loadingMs: 900 });
  const output = await harness.run({});
  assert.equal(output.messages.length, 2);
  assert.ok(texts(output).every((t) => !t.includes('받은편지')));
});

test('진행 중 표시가 있는 동안에는 비어 있어도 결과로 확정하지 않는다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], loadingMs: 800, busyWhileLoading: true });
  const output = await harness.run({});
  assert.equal(output.messages.length, 1);
});

test('메일 글자의 제어·방향 제어 문자는 결과에서 지운다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [mailRow('X1', '이영희', '반차\u202e안내\u0007', '미리보기', '2026-10-20')] });
  const output = await harness.run({ names: ['이영희'] });
  assert.equal(output.messages.length, 1);
  assert.doesNotMatch(output.messages[0].text, /[\u202e\u0007]/);
  assert.match(output.messages[0].text, /반차/);
});

// ───────────────────────────── 범위 ─────────────────────────────

test('검색 범위가 이미 모든 폴더면 그대로 검색한다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], scope: 'all' });
  const output = await harness.run({});
  assert.equal(harness.state.searches[0].scope, '모든 폴더');
  assert.equal(output.result.incompleteReason, null);
});

test('범위 선택 UI 가 없으면 결과는 돌려주되 scopeUnverified 로 불완전 표시', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], scope: 'none' });
  const output = await harness.run({});
  assert.equal(output.messages.length, 1);
  assert.deepEqual(output.result, { returned: 1, truncated: true, incompleteReason: 'scopeUnverified' });
});

// ───────────────────────────── 가상 스크롤 ─────────────────────────────

test('가상 스크롤·지연 로딩: 모든 행을 중복 없이 모으고 끝을 확인하면 불완전이 아니다', { timeout: 90_000 }, async () => {
  harness.use({ corpus: virtualCorpus(70) });
  const output = await harness.run({ maxResults: 200 });
  assert.equal(output.messages.length, 70);
  assert.equal(new Set(texts(output)).size, 70);
  assert.ok(texts(output)[0].includes('발신자 001'));
  assert.ok(texts(output)[69].includes('발신자 070'));
  assert.deepEqual(output.result, { returned: 70, truncated: false, incompleteReason: null });
  assert.ok(harness.state.fetches >= 4, '지연 로딩 페이지를 따라가야 한다');
});

test('maxResults 를 넘는 결과가 있으면 상한까지만 담고 maxResults 로 표시', { timeout: 90_000 }, async () => {
  harness.use({ corpus: virtualCorpus(70) });
  const output = await harness.run({ maxResults: 30 });
  assert.equal(output.messages.length, 30);
  assert.ok(texts(output)[29].includes('발신자 030'));
  assert.deepEqual(output.result, { returned: 30, truncated: true, incompleteReason: 'maxResults' });
});

test('끝없이 이어지는 목록은 끝을 확인하지 못하므로 endNotConfirmed', { timeout: 90_000 }, async () => {
  harness.use({ endless: true });
  const output = await harness.run({ maxResults: 1000 }, { limits: { maxScrollRounds: 6 } });
  assert.ok(output.messages.length > 20);
  assert.equal(output.result.truncated, true);
  assert.equal(output.result.incompleteReason, 'endNotConfirmed');
});

// ───────────────────────────── 계정 게이트 ─────────────────────────────

test('폴더 트리 계정이 다르면 메일을 읽지 않고 종료 코드 3', { timeout: 60_000 }, async () => {
  harness.use({ accounts: ['other@example.invalid'], corpus: [M1] });
  await assert.rejects(harness.run({}), (error) => {
    failure('accountMismatch', 3)(error);
    assert.match(error.message, /other@example\.invalid/);
    return true;
  });
  assert.equal(harness.state.searches.length, 0);
  assert.equal(harness.state.fetches, 0);
  assert.deepEqual(harness.state.opened, []);
});
test('계정 앞의 장식용 아이콘을 주소에 포함하지 않고 실제 계정을 검증한다', { timeout: 60_000 }, async () => {
  harness.use({ accountPrefix: '\uE488', corpus: [M1] });
  const output = await harness.run({});
  assert.equal(output.account, ACCOUNT);
  assert.equal(output.result.returned, 1);

  harness.use({ accountPrefix: '\uE488', accounts: ['other@example.invalid'], corpus: [M1] });
  await assert.rejects(harness.run({}), failure('accountMismatch', 3));
  assert.equal(harness.state.searches.length, 0);
});


test('메일 목록 글자에 계정이 적혀 있어도 화면의 계정 UI 가 아니면 확인으로 치지 않는다', { timeout: 60_000 }, async () => {
  harness.use({
    accountSource: 'none',
    inbox: [mailRow('D1', ACCOUNT, `${ACCOUNT} 로그인 알림`, `${ACCOUNT} 님`, '2026-10-01')],
    corpus: [M1],
  });
  await assert.rejects(harness.run({}, {}), failure('accountUnverified', 1));
  assert.equal(harness.state.searches.length, 0);
  assert.equal(harness.state.fetches, 0);
});

test('첫(기본) 계정만 인정한다: 공유 사서함이 뒤에 있어도 요청 계정이 거기 있으면 불일치', { timeout: 60_000 }, async () => {
  harness.use({ accounts: [ACCOUNT, 'shared@example.invalid'], corpus: [M1] });
  const ok = await harness.run({});
  assert.equal(ok.messages.length, 1);

  harness.use({ accounts: [ACCOUNT, 'shared@example.invalid'], corpus: [M1] });
  await assert.rejects(harness.run({ account: 'shared@example.invalid' }), failure('accountMismatch', 3));
  assert.equal(harness.state.searches.length, 0);
});

test('트리에 계정이 없으면 계정 관리자 메뉴에서 확인한다', { timeout: 60_000 }, async () => {
  harness.use({ accountSource: 'menu', corpus: [M1] });
  const output = await harness.run({});
  assert.equal(output.messages.length, 1);

  harness.use({ accountSource: 'menu', accounts: ['other@example.invalid'], corpus: [M1] });
  await assert.rejects(harness.run({}), failure('accountMismatch', 3));
  assert.equal(harness.state.searches.length, 0);
});

// ───────────────────────────── 로그아웃 ─────────────────────────────

test('로그인 페이지로 이동하면 검색 없이 종료 코드 3', { timeout: 60_000 }, async () => {
  harness.use({ kind: 'login', corpus: [M1] });
  await assert.rejects(harness.run({}), (error) => {
    failure('loginRequired', 3)(error);
    assert.match(error.message, /-Login/);
    return true;
  });
  assert.equal(harness.state.searches.length, 0);
  assert.ok(harness.state.sessions[0].closed, '브라우저(페이지)는 항상 닫는다');
});

// ───────────────────────────── 본문 ─────────────────────────────

test('includeBody: 선택한 메일의 읽기 창 글을 읽는다(같은 보낸이의 이전 메일 내용으로 착각하지 않는다)', { timeout: 90_000 }, async () => {
  const rows = ['B1', 'B2', 'B3'].map((id, i) =>
    mailRow(id, '홍길동', `휴가 신청 ${i + 1}번`, `미리보기 ${id}`, '2026-10-20', `BODYTOKEN-${id} 본문 내용`),
  );
  harness.use({ corpus: rows, paneDelayMs: 250 });
  const output = await harness.run({ includeBody: true });
  assert.equal(output.messages.length, 3);
  for (const [i, message] of output.messages.entries()) {
    const id = `B${i + 1}`;
    assert.ok(message.body.includes(`BODYTOKEN-${id}`), `${id}: ${message.body}`);
    assert.ok(message.body.includes(`휴가 신청 ${i + 1}번`));
    for (const other of ['B1', 'B2', 'B3'].filter((x) => x !== id)) {
      assert.ok(!message.body.includes(`BODYTOKEN-${other}`));
    }
  }
  assert.deepEqual(harness.state.opened, ['B1', 'B2', 'B3'], '각 메일을 열었다(읽음 표시 부작용)');
  assert.deepEqual(output.result, { returned: 3, truncated: false, incompleteReason: null });
});

test('includeBody: 읽기 창이 없으면 조용히 빼지 않고 실패한다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], readingPane: false });
  await assert.rejects(harness.run({ includeBody: true, timeoutSeconds: 2 }), failure('readingPaneMissing', 1));
});

test('includeBody 를 끄면 어떤 메일도 열지 않는다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1, M2] });
  const output = await harness.run({});
  assert.equal(output.messages.length, 2);
  assert.deepEqual(harness.state.opened, []);
  assert.ok(output.messages.every((m) => !('body' in m)));
});

// ───────────────────────────── 로그인 모드 ─────────────────────────────

test('로그인 모드: 화면이 보이는 브라우저로 열고, 사용자가 로그인해 메일함·계정이 확인되면 ready', { timeout: 60_000 }, async () => {
  harness.use({ kind: 'login-then-mailbox', loginRedirectMs: 700 });
  const logs = [];
  const output = await harness.run({ login: true, loginTimeoutSeconds: 20 }, { log: (line) => logs.push(line) });
  assert.deepEqual(output, { mode: 'login', account: ACCOUNT, status: 'ready' });
  assert.equal(harness.state.launchCalls[0].headless, false);
  assert.equal(harness.state.launchCalls[0].viewport, null);
  assert.equal(harness.state.searches.length, 0, '로그인 모드는 검색하지 않는다');
  assert.ok(logs.some((line) => line.includes('직접 로그인')));
  assert.ok(harness.state.sessions[0].closed);
});

test('로그인 모드: 시간 안에 로그인되지 않으면 종료 코드 4', { timeout: 60_000 }, async () => {
  harness.use({ kind: 'login' });
  await assert.rejects(harness.run({ login: true, loginTimeoutSeconds: 1.5 }), failure('loginTimeout', 4));
  assert.ok(harness.state.sessions[0].closed);
});

test('로그인 모드: 다른 계정으로 로그인돼 있으면 안내하고, 끝내 못 바꾸면 종료 코드 3', { timeout: 60_000 }, async () => {
  harness.use({ accounts: ['other@example.invalid'] });
  const logs = [];
  await assert.rejects(
    harness.run({ login: true, loginTimeoutSeconds: 1.5 }, { log: (line) => logs.push(line) }),
    failure('accountMismatch', 3),
  );
  assert.ok(logs.some((line) => line.includes('other@example.invalid')));
});

test('중단 신호: 로그인 대기를 즉시 끝내고 브라우저를 닫는다', { timeout: 60_000 }, async () => {
  harness.use({ kind: 'login' });
  const controller = new AbortController();
  setTimeout(() => controller.abort(), 500);
  const started = Date.now();
  await assert.rejects(harness.run({ login: true, loginTimeoutSeconds: 120 }, { signal: controller.signal }), failure('cancelled', 1));
  assert.ok(Date.now() - started < 15_000, '중단 뒤 오래 매달리지 않는다');
  assert.ok(harness.state.sessions[0].closed);
});

test('폴더 트리가 검색창보다 늦게 그려져도 기다려 계정을 확인한 뒤 검색한다', { timeout: 60_000 }, async () => {
  harness.use({ corpus: [M1], treeDelayMs: 350 });
  const output = await harness.run({});
  assert.equal(output.messages.length, 1);

  harness.use({ corpus: [M1], treeDelayMs: 350, accounts: ['other@example.invalid'] });
  await assert.rejects(harness.run({}), failure('accountMismatch', 3));
  assert.equal(harness.state.searches.length, 0, '늦게 나타난 트리의 다른 계정도 검색 전에 잡는다');
});
