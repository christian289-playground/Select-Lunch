// Outlook 웹 검색창에 입력할 AQS 검색식을 만든다.
//
// 사용자 입력은 검색식이 아니라 평문 단어다. 글자·숫자·접미 와일드카드(*)만 남기고 나머지는 구분자로 바꿔
// 따옴표·괄호·콜론(속성 지정)·+/- 로 검색식 구조를 바꿀 수 없게 한다. AND/OR/NOT/NEAR 단어는 거부한다.
//
// 문법 근거(Microsoft Support, "Search Mail and People in Outlook on the web"):
//   - AND / OR(대문자), 괄호 그룹, 접미 와일드카드만 지원(앞·중간 * 불가).
//   - Received:MM/DD/YYYY 와 두 점(..) 범위. 날짜는 반드시 MM/DD/YYYY. 범위 양끝은 포함.
//   - 검색 결과는 처음 1,000건만 돌려준다.
// 문서에 시간대 해석이 없다. 그래서 검색식은 날짜 범위를 그대로 보내고, 목록에 보이는 받은 날짜가
// 범위 밖이면 그 행을 빼고 "경계 불일치"로 불완전 표시한다(outlook-search.mjs).

import { usageError } from './errors.mjs';

export const DEFAULT_QUERY = Object.freeze(['휴가', '연차', '반차', '반반차', '시간차', '대체휴가', '경조휴가']);

// 한쪽만 주어진 날짜 범위를 채우는 값. 문서에 최소·최대가 없어 "메일이 존재할 수 없는" 먼 날짜를 쓴다.
// 검색식에 그대로 드러나므로 출력의 searchExpression 에서 확인할 수 있다.
export const EARLIEST_DATE = '1990-01-01';
export const LATEST_DATE = '2099-12-31';

export const LIMITS = Object.freeze({
  maxQueryTerms: 50,
  maxNameTerms: 200,
  maxTermLength: 100,
  maxExpressionLength: 2000,
});

const OPERATOR_WORDS = new Set(['and', 'or', 'not', 'near']);

// ───────────────────────────── 날짜 ─────────────────────────────

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** 'YYYY-MM-DD' 가 실존하는 날짜면 {y,m,d}, 아니면 null. */
export function parseIsoDate(value) {
  if (typeof value !== 'string') return null;
  const match = ISO_DATE.exec(value);
  if (!match) return null;
  const y = Number(match[1]);
  const m = Number(match[2]);
  const d = Number(match[3]);
  const t = new Date(Date.UTC(y, m - 1, d));
  if (t.getUTCFullYear() !== y || t.getUTCMonth() !== m - 1 || t.getUTCDate() !== d) return null;
  return { y, m, d };
}

export function formatIsoDate(y, m, d) {
  return `${String(y).padStart(4, '0')}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
}

/** ISO 날짜에 일수를 더한다. 시간대·DST와 무관하게 UTC 달력으로만 계산한다. */
export function addDays(iso, days) {
  const p = parseIsoDate(iso);
  if (!p) throw new RangeError('invalid iso date');
  const t = new Date(Date.UTC(p.y, p.m - 1, p.d + days));
  return formatIsoDate(t.getUTCFullYear(), t.getUTCMonth() + 1, t.getUTCDate());
}

/** Outlook 문서 형식 MM/DD/YYYY. */
export function formatAqsDate(iso) {
  const p = parseIsoDate(iso);
  if (!p) throw new RangeError('invalid iso date');
  return `${String(p.m).padStart(2, '0')}/${String(p.d).padStart(2, '0')}/${String(p.y).padStart(4, '0')}`;
}

/**
 * 받은 날짜 절. [after, before) 를 문서의 포함 범위로 바꾸려고 before 에서 하루를 뺀다.
 * 둘 다 없으면 null.
 */
export function buildDateClause(receivedAfter, receivedBefore) {
  if (!receivedAfter && !receivedBefore) return null;
  if (receivedAfter && receivedBefore && receivedAfter >= receivedBefore) {
    throw usageError('dateRange', '받은 날짜 범위가 비어 있습니다. receivedAfter 는 receivedBefore 보다 앞서야 합니다.');
  }
  const start = receivedAfter ?? EARLIEST_DATE;
  const end = receivedBefore ? addDays(receivedBefore, -1) : LATEST_DATE;
  if (end < start) {
    throw usageError('dateRange', '받은 날짜 범위가 비어 있습니다.');
  }
  return `received:${formatAqsDate(start)}..${formatAqsDate(end)}`;
}

// ───────────────────────────── 단어 ─────────────────────────────

/**
 * 한 항목을 검색 단어들로 쪼갠다. 글자·숫자 외에는 구분자. 단어 끝의 * 하나만 접두 와일드카드로 남긴다.
 * @returns {string[]}
 */
export function tokenizeTerm(text) {
  const normalized = String(text ?? '').normalize('NFC');
  let cleaned = normalized.replace(/[^\p{L}\p{M}\p{N}*]+/gu, ' ');
  cleaned = cleaned.replace(/\*+(?=[\p{L}\p{M}\p{N}])/gu, ' ');
  cleaned = cleaned.replace(/\*{2,}/g, '*');
  const tokens = [];
  for (const token of cleaned.split(' ')) {
    if (!token) continue;
    const core = token.replace(/\*+$/, '');
    if (!core) continue;
    if (OPERATOR_WORDS.has(core.toLowerCase())) {
      throw usageError(
        'operatorWord',
        `검색어에 연산자 단어 '${core}'가 있습니다. AND/OR/NOT/NEAR는 검색식 연산자로 해석되어 쓸 수 없습니다. 여러 검색어는 항목으로 나눠 주세요.`,
      );
    }
    tokens.push(token);
  }
  return tokens;
}

/** 쉼표(, ，)로 이어 붙여 들어온 항목을 푼다. 쉼표 없는 항목(빈 문자열 포함)은 그대로 둬 이후 검증이 거부하게 한다. */
export function expandTerms(terms) {
  const out = [];
  for (const term of terms) {
    if (/[,，]/.test(term)) {
      for (const piece of term.split(/[,，]/)) {
        if (piece.trim() !== '') out.push(piece.trim());
      }
    } else {
      out.push(term);
    }
  }
  return out;
}

function buildGroup(terms, label, maxTerms) {
  const expanded = expandTerms(terms);
  if (expanded.length === 0) {
    throw usageError('emptyTerms', `${label} 에는 항목이 하나 이상 필요합니다.`);
  }
  if (expanded.length > maxTerms) {
    throw usageError('tooManyTerms', `${label} 항목이 너무 많습니다(${expanded.length}개 > ${maxTerms}개).`);
  }
  const parts = [];
  for (const term of expanded) {
    if (term.length > LIMITS.maxTermLength) {
      throw usageError('termTooLong', `${label} 항목이 너무 깁니다(${LIMITS.maxTermLength}자 초과).`);
    }
    const tokens = tokenizeTerm(term);
    if (tokens.length === 0) {
      throw usageError('emptyTerm', `${label} 항목에 검색할 수 있는 글자(문자·숫자)가 없는 값이 있습니다.`);
    }
    const part = tokens.length === 1 ? tokens[0] : `(${tokens.join(' ')})`;
    if (!parts.includes(part)) parts.push(part);
  }
  return `(${parts.join(' OR ')})`;
}

/**
 * 검색식을 만든다: (검색어 OR …) AND (이름 OR …) AND received:MM/DD/YYYY..MM/DD/YYYY
 * - query 가 없거나 비어 있으면 기본 검색어를 쓴다.
 * - 이름은 서로 OR, 검색어 묶음과는 AND.
 * @param {{query?: string[]|null, names?: string[]|null, receivedAfter?: string|null, receivedBefore?: string|null}} input
 */
export function buildSearchExpression({ query, names, receivedAfter = null, receivedBefore = null }) {
  const keywordTerms = query && query.length > 0 ? query : DEFAULT_QUERY;
  const clauses = [buildGroup(keywordTerms, '검색어', LIMITS.maxQueryTerms)];
  if (names && names.length > 0) {
    clauses.push(buildGroup(names, '이름', LIMITS.maxNameTerms));
  }
  const date = buildDateClause(receivedAfter, receivedBefore);
  if (date) clauses.push(date);
  const expression = clauses.join(' AND ');
  if (expression.length > LIMITS.maxExpressionLength) {
    throw usageError(
      'expressionTooLong',
      `검색식이 너무 깁니다(${expression.length}자 > ${LIMITS.maxExpressionLength}자). 이름·검색어를 줄이거나 나눠서 실행하세요.`,
    );
  }
  return expression;
}
