// 읽기 창 글이 "방금 누른 행의 메일"의 것인지 가르는 순수 함수 검증. 브라우저 없이 돈다.
// 행 모양은 실제 Outlook 화면 관찰과 같게 만들었다(아이콘 버튼 줄·연도 없는 날짜 칸·말줄임 미리보기·폴더 버튼 줄).
// 모든 이름·주소는 가짜다.

import assert from 'node:assert/strict';
import test from 'node:test';
import { describeRowIdentity, paneMatchesRow } from '../lib/outlook-search.mjs';

const TODAY = '2026-10-21';
const STAMP = '화 2026-10-20 오전 9:00';
const OTHER_STAMP = '월 2026-10-19 오전 9:00';
const LONG_PREVIEW = '...결재 문서: https://example.invalid/doc/1';

/** 행 하나의 스냅샷(outlook-search.mjs 의 snapshotInPage 가 돌려주는 모양). */
function outlookRow(overrides = {}) {
  return {
    lines: ['\ue490', '홍길동', '\ue354', '휴가-홍길동', '화 10-20', LONG_PREVIEW, '받은 편지함'],
    controlLines: ['\ue490', '받은 편지함'],
    titled: [
      { title: '고정', text: '' },
      { title: 'a1@example.invalid', text: '홍길동' },
      { title: STAMP, text: '화 10-20' },
    ],
    ...overrides,
  };
}

const pane = (...lines) => lines.join('\n');
const PANE = pane('휴가-홍길동', '\uf32e', '메일 요약', '홍길동', STAMP, '________', '결재 문서: https://example.invalid/doc/1', '신청자: 홍길동');

test('실제 구조의 행: 아이콘·버튼 줄·날짜 칸·말줄임 미리보기는 비교 대상이 아니고, 받은 시각 tooltip 이 신원 증거가 된다', () => {
  const identity = describeRowIdentity(outlookRow(), TODAY);
  assert.deepEqual(identity.needles, ['홍길동', '휴가-홍길동']);
  assert.deepEqual(identity.stamps, [STAMP]);
});

test('실제 구조의 행은 자기 읽기 창과 연결된다', () => {
  assert.equal(paneMatchesRow(PANE, outlookRow(), TODAY), true);
});

test('보낸이·제목이 같아도 받은 시각이 다른 이전 메일의 읽기 창은 거부한다', () => {
  const stale = pane('휴가-홍길동', '\uf32e', '메일 요약', '홍길동', OTHER_STAMP, '________', '이전 메일 본문');
  assert.equal(paneMatchesRow(stale, outlookRow(), TODAY), false);
});

test('받은 시각이 같아도 보낸이·제목이 다른 메일의 읽기 창은 거부한다', () => {
  const other = pane('연차-김철수', '\uf32e', '메일 요약', '김철수', STAMP, '________', '다른 메일 본문');
  assert.equal(paneMatchesRow(other, outlookRow(), TODAY), false);
});

test('받은 시각이 읽기 창에 없으면 보낸이·제목이 맞아도 거부한다(확인하지 못한 본문은 돌려주지 않는다)', () => {
  const noStamp = pane('휴가-홍길동', '홍길동', '________', '본문');
  assert.equal(paneMatchesRow(noStamp, outlookRow(), TODAY), false);
});

test('받은 시각만 맞고 보낸이·제목 줄이 맞지 않으면 거부한다', () => {
  assert.equal(paneMatchesRow(pane('다른 제목', STAMP, '본문'), outlookRow(), TODAY), false);
  assert.equal(paneMatchesRow(pane('연차-김철수', '김철수', STAMP, '본문'), outlookRow(), TODAY), false);
});

test('미리보기만 읽기 창에 있는 경우는 신원 증거가 아니다', () => {
  const row = { lines: ['홍길동', '휴가 신청합니다', '10월 연차 사용 예정'], controlLines: [], titled: [] };
  assert.equal(paneMatchesRow('10월 연차 사용 예정', row, TODAY), false);
});

test('받은 시각 tooltip 이 없는 단순한 행: 의미 있는 줄의 절반 이상(최소 2줄)이 읽기 창에 있어야 한다', () => {
  const row = { lines: ['홍길동', '휴가 신청합니다', '10월 연차 사용 예정', '2026-10-20'], controlLines: [], titled: [] };
  assert.equal(paneMatchesRow('홍길동\n휴가 신청합니다\n\n본문', row, TODAY), true);
  assert.equal(paneMatchesRow('홍길동\n다른 제목\n\n본문', row, TODAY), false);
  assert.equal(paneMatchesRow('메일을 선택하세요', row, TODAY), false);
});

test('줄만 있는 스냅샷(controlLines·titled 없음)도 같은 규칙으로 판정한다', () => {
  assert.equal(paneMatchesRow('홍길동\n휴가 신청합니다\n본문', { lines: ['홍길동', '휴가 신청합니다', '미리보기'] }, TODAY), true);
});

test('비교할 줄이 없는 행은 받은 시각이 맞아도 확인할 수 없으므로 거부한다', () => {
  const row = { lines: ['\ue490', '오전 9:00', '받은 편지함'], controlLines: ['\ue490', '받은 편지함'], titled: [] };
  assert.deepEqual(describeRowIdentity(row, TODAY), { needles: [], stamps: [] });
  assert.equal(paneMatchesRow(PANE, row, TODAY), false);
  const stampOnly = { ...row, titled: [{ title: STAMP, text: '화 10-20' }] };
  assert.deepEqual(describeRowIdentity(stampOnly, TODAY), { needles: [], stamps: [STAMP] });
  assert.equal(paneMatchesRow(PANE, stampOnly, TODAY), false);
});

test('읽기 창이 비었으면 거부한다', () => {
  assert.equal(paneMatchesRow('', outlookRow(), TODAY), false);
  assert.equal(paneMatchesRow('   \n ', outlookRow(), TODAY), false);
});

test('말줄임 미리보기는 앞뒤 어느 쪽 말줄임 표시든 비교 대상에서 뺀다', () => {
  for (const preview of ['...결재 문서 안내', '결재 문서 안내...', '…결재 문서 안내', '결재 문서 안내…']) {
    const identity = describeRowIdentity({ lines: ['홍길동', '휴가 신청', preview], controlLines: [], titled: [] }, TODAY);
    assert.deepEqual(identity.needles, ['홍길동', '휴가 신청'], preview);
  }
});

test('연도 없는 날짜 칸 글자는 받은 시각 tooltip 이 붙은 칸이면 비교 대상에서 뺀다', () => {
  const row = outlookRow({ lines: ['홍길동', '휴가-홍길동', '월 10-19'], titled: [{ title: OTHER_STAMP, text: '월 10-19' }] });
  assert.deepEqual(describeRowIdentity(row, TODAY).needles, ['홍길동', '휴가-홍길동']);
});
