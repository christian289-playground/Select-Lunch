import assert from 'node:assert/strict';
import test from 'node:test';
import {
  DEFAULT_QUERY,
  addDays,
  buildDateClause,
  buildSearchExpression,
  formatAqsDate,
  tokenizeTerm,
} from '../lib/search-expression.mjs';

const code = (expected) => (error) => {
  assert.equal(error.exitCode, 2);
  assert.equal(error.code, expected);
  return true;
};

test('기본 검색어는 서로 OR 로 한 묶음', () => {
  assert.equal(buildSearchExpression({}), `(${DEFAULT_QUERY.join(' OR ')})`);
  assert.equal(buildSearchExpression({ query: [], names: [] }), `(${DEFAULT_QUERY.join(' OR ')})`);
});

test('검색어 묶음 AND 이름 묶음, 이름끼리는 OR, 여러 단어 이름은 단어 모두 포함', () => {
  assert.equal(
    buildSearchExpression({ query: ['휴가', '연차'], names: ['홍길동', '김 철수'] }),
    '(휴가 OR 연차) AND (홍길동 OR (김 철수))',
  );
});

test('쉼표로 이어 붙은 항목은 항목으로 풀고 중복은 합친다', () => {
  assert.equal(buildSearchExpression({ query: ['연차,휴가', '연차'] }), '(연차 OR 휴가)');
});

test('접미 와일드카드만 남기고 앞·중간 * 는 구분자', () => {
  assert.deepEqual(tokenizeTerm('연차*'), ['연차*']);
  assert.deepEqual(tokenizeTerm('*연차'), ['연차']);
  assert.deepEqual(tokenizeTerm('연*차'), ['연', '차']);
  assert.deepEqual(tokenizeTerm('연차**'), ['연차*']);
  assert.deepEqual(tokenizeTerm('***'), []);
});

test('검색식 연산자 단어는 대소문자와 무관하게 거부', () => {
  for (const hostile of ['휴가 OR 1', 'x and y', 'NOT 휴가', '연차 NEAR 휴가', 'or', '휴가*  Or  연차']) {
    assert.throws(() => buildSearchExpression({ query: [hostile] }), code('operatorWord'), hostile);
    assert.throws(() => buildSearchExpression({ query: ['휴가'], names: [hostile] }), code('operatorWord'), hostile);
  }
});

test('따옴표·괄호·콜론·+/- 로 검색식 구조를 바꿀 수 없다', () => {
  const hostile = ['휴가") (from:ceo', 'subject:휴가', '-from:boss 휴가', '(휴가', '휴가)', '+연차 -반차', 'received:01/01/2020..12/31/2020'];
  for (const term of hostile) {
    const expression = buildSearchExpression({ query: [term], names: [term] });
    assert.doesNotMatch(expression, /[":+\-<>=\/\\]/, `${term} → ${expression}`);
    const opens = [...expression].filter((c) => c === '(').length;
    const closes = [...expression].filter((c) => c === ')').length;
    assert.equal(opens, closes, expression);
  }
  assert.equal(buildSearchExpression({ query: ['subject:휴가'] }), '((subject 휴가))');
});

test('검색할 글자가 없는 항목은 조용히 버리지 않고 거부', () => {
  assert.throws(() => buildSearchExpression({ query: [''] }), code('emptyTerm'));
  assert.throws(() => buildSearchExpression({ query: ['!!!'] }), code('emptyTerm'));
  assert.throws(() => buildSearchExpression({ query: ['휴가'], names: [''] }), code('emptyTerm'));
});

test('받은 날짜: [after, before) 를 문서의 포함 범위 MM/DD/YYYY..MM/DD/YYYY 로', () => {
  assert.equal(buildDateClause('2026-10-01', '2026-11-01'), 'received:10/01/2026..10/31/2026');
  assert.equal(buildDateClause('2026-10-20', '2026-10-21'), 'received:10/20/2026..10/20/2026');
  assert.equal(buildDateClause('2026-01-05', '2026-01-06'), 'received:01/05/2026..01/05/2026');
});

test('받은 날짜: before 의 하루 전 계산은 해·윤일·월말 경계를 넘는다', () => {
  assert.equal(buildDateClause('2025-12-01', '2026-01-01'), 'received:12/01/2025..12/31/2025');
  assert.equal(buildDateClause('2024-02-01', '2024-03-01'), 'received:02/01/2024..02/29/2024');
  assert.equal(buildDateClause('2025-02-01', '2025-03-01'), 'received:02/01/2025..02/28/2025');
  assert.equal(addDays('2024-12-31', 1), '2025-01-01');
  assert.equal(addDays('2026-03-01', -1), '2026-02-28');
});

test('한쪽만 주어진 날짜 범위는 문서에 드러나는 먼 날짜로 채운다', () => {
  assert.equal(buildDateClause('2026-10-01', null), 'received:10/01/2026..12/31/2099');
  assert.equal(buildDateClause(null, '2026-01-01'), 'received:01/01/1990..12/31/2025');
  assert.equal(buildDateClause(null, null), null);
});

test('빈 날짜 범위는 거부', () => {
  assert.throws(() => buildDateClause('2026-10-02', '2026-10-01'), code('dateRange'));
  assert.throws(() => buildDateClause('2026-10-01', '2026-10-01'), code('dateRange'));
  assert.throws(() => buildDateClause(null, '1990-01-01'), code('dateRange'));
});

test('날짜 절은 검색어·이름 묶음과 AND 로 붙고, 형식은 MM/DD/YYYY', () => {
  assert.equal(formatAqsDate('2026-03-05'), '03/05/2026');
  assert.equal(
    buildSearchExpression({ query: ['휴가'], names: ['홍길동'], receivedAfter: '2026-10-01', receivedBefore: '2026-11-01' }),
    '(휴가) AND (홍길동) AND received:10/01/2026..10/31/2026',
  );
});

test('항목 수·길이·검색식 길이 상한', () => {
  const many = Array.from({ length: 51 }, (_, i) => `단어${i}`);
  assert.throws(() => buildSearchExpression({ query: many }), code('tooManyTerms'));
  assert.throws(() => buildSearchExpression({ query: ['가'.repeat(101)] }), code('termTooLong'));
  const names = Array.from({ length: 200 }, (_, i) => `이름${String(i).padStart(6, '0')}`);
  assert.throws(() => buildSearchExpression({ query: ['휴가'], names }), code('expressionTooLong'));
});
