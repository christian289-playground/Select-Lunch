// 목록 행에 "날짜만" 적힌 칸(리프 요소의 전체 텍스트가 날짜)을 읽어 받은 날짜를 얻는다.
// 미리보기·제목 속 날짜(예: 휴가 기간)를 받은 날짜로 오해하지 않도록, 전체 텍스트가 날짜 형식과
// 정확히 일치하는 칸만 인정한다. 시간만 있거나 요일만 있는 표시, 월/일이 모호한 표시는 null(알 수 없음).

import { addDays, formatIsoDate, parseIsoDate } from './search-expression.mjs';

const WEEKDAY_PREFIX =
  /^(?:(?:월|화|수|목|금|토|일)(?:요일)?|(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*\.?)[\s,]+/i;

function validIso(y, m, d) {
  const iso = formatIsoDate(y, m, d);
  return parseIsoDate(iso) ? iso : null;
}

/** 연도가 없는 표시의 연도를 추정한다: 오늘보다 미래(하루 여유)가 되면 전년도. */
function inferYear(m, d, today) {
  const t = parseIsoDate(today);
  if (!t) return null;
  const candidate = validIso(t.y, m, d);
  if (!candidate) return null;
  return candidate > addDays(today, 1) ? validIso(t.y - 1, m, d) : candidate;
}

/** 월/일 순서가 모호한 두 숫자를 (month, day)로. 모호하면 null. */
function resolveMonthDay(a, b) {
  if (a === b) return { m: a, d: b };
  if (a > 12 && b <= 12) return { m: b, d: a };
  if (b > 12 && a <= 12) return { m: a, d: b };
  return null;
}

/**
 * @param {string} text 요소 하나의 전체 텍스트
 * @param {{today: string}} ctx today = 'YYYY-MM-DD'(연도 없는 표시의 연도 추정용)
 * @returns {string|null} 'YYYY-MM-DD'
 */
export function parseVisibleDate(text, { today }) {
  let value = String(text ?? '').replace(/\s+/g, ' ').trim();
  if (!value) return null;
  value = value.replace(WEEKDAY_PREFIX, '').replace(/\.$/, '').trim();

  let m = /^(\d{4}) ?[-./] ?(\d{1,2}) ?[-./] ?(\d{1,2})$/.exec(value);
  if (m) return validIso(Number(m[1]), Number(m[2]), Number(m[3]));

  m = /^(\d{4}) ?년 ?(\d{1,2}) ?월 ?(\d{1,2}) ?일$/.exec(value);
  if (m) return validIso(Number(m[1]), Number(m[2]), Number(m[3]));

  m = /^(\d{1,2}) ?월 ?(\d{1,2}) ?일$/.exec(value);
  if (m) return inferYear(Number(m[1]), Number(m[2]), today);

  m = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value);
  if (m) {
    const md = resolveMonthDay(Number(m[1]), Number(m[2]));
    return md ? validIso(Number(m[3]), md.m, md.d) : null;
  }

  m = /^(\d{1,2})\/(\d{1,2})$/.exec(value);
  if (m) {
    const md = resolveMonthDay(Number(m[1]), Number(m[2]));
    return md ? inferYear(md.m, md.d, today) : null;
  }

  return null;
}

/** 한 행의 날짜 후보들에서 받은 날짜를 고른다. 해석되는 날짜가 정확히 하나일 때만 인정. */
export function pickRowDate(candidates, { today }) {
  const found = new Set();
  for (const candidate of candidates ?? []) {
    const iso = parseVisibleDate(candidate, { today });
    if (iso) found.add(iso);
  }
  return found.size === 1 ? [...found][0] : null;
}

/** [after, before) 판정. after/before 가 null 이면 그쪽은 열려 있다. */
export function isDateInRange(iso, { after = null, before = null }) {
  if (after && iso < after) return false;
  if (before && iso >= before) return false;
  return true;
}

/** 로컬 달력의 오늘 'YYYY-MM-DD'. */
export function localTodayIso(now = new Date()) {
  return formatIsoDate(now.getFullYear(), now.getMonth() + 1, now.getDate());
}
