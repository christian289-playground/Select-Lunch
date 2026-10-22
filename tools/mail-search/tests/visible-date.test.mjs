import assert from 'node:assert/strict';
import test from 'node:test';
import { isDateInRange, parseVisibleDate, pickRowDate } from '../lib/visible-date.mjs';

const today = '2026-10-21';
const parse = (text, t = today) => parseVisibleDate(text, { today: t });

test('연도가 있는 표시는 그대로 해석', () => {
  assert.equal(parse('2026-10-20'), '2026-10-20');
  assert.equal(parse('2026. 10. 20.'), '2026-10-20');
  assert.equal(parse('2026/10/20'), '2026-10-20');
  assert.equal(parse('2026년 10월 20일'), '2026-10-20');
  assert.equal(parse('10/20/2026'), '2026-10-20');
});

test('월/일 순서가 모호하면 알 수 없음, 한쪽이 12를 넘으면 해석', () => {
  assert.equal(parse('03/04/2026'), null);
  assert.equal(parse('20/10/2026'), '2026-10-20');
  assert.equal(parse('10/20/2026'), '2026-10-20');
  assert.equal(parse('05/05/2026'), '2026-05-05');
  assert.equal(parse('3/4'), null);
});

test('연도가 없는 표시는 오늘 기준으로 추정하고, 미래가 되면 전년도', () => {
  assert.equal(parse('10/20'), '2026-10-20');
  assert.equal(parse('10월 20일'), '2026-10-20');
  assert.equal(parse('화 10/20'), '2026-10-20');
  assert.equal(parse('12/30', '2026-01-05'), '2025-12-30');
  assert.equal(parse('10/22'), '2026-10-22'); // 하루 여유(시간대 차이)
  assert.equal(parse('10/23'), '2025-10-23');
});

test('시간만·요일만·실존하지 않는 날짜·문장 속 날짜는 받은 날짜로 보지 않는다', () => {
  assert.equal(parse('오후 3:12'), null);
  assert.equal(parse('월 9:12'), null);
  assert.equal(parse('Tue 9:12 AM'), null);
  assert.equal(parse('2026-02-30'), null);
  assert.equal(parse('휴가 2026-10-20'), null);
  assert.equal(parse('10월 20일부터 연차'), null);
  assert.equal(parse(''), null);
});

test('행의 날짜 후보에서 하나로 정해질 때만 받은 날짜로 인정', () => {
  assert.equal(pickRowDate(['홍길동', '10/20', '2026-10-20'], { today }), '2026-10-20');
  assert.equal(pickRowDate(['2026-10-20', '2026-11-02'], { today }), null);
  assert.equal(pickRowDate(['오후 3:12'], { today }), null);
  assert.equal(pickRowDate([], { today }), null);
});

test('범위 판정은 after 포함, before 미포함, 열린 쪽은 제한 없음', () => {
  const range = { after: '2026-10-20', before: '2026-10-22' };
  assert.equal(isDateInRange('2026-10-19', range), false);
  assert.equal(isDateInRange('2026-10-20', range), true);
  assert.equal(isDateInRange('2026-10-21', range), true);
  assert.equal(isDateInRange('2026-10-22', range), false);
  assert.equal(isDateInRange('1999-01-01', { after: null, before: '2026-10-22' }), true);
  assert.equal(isDateInRange('2099-01-01', { after: '2026-10-20', before: null }), true);
});
