// stdin 으로 받는 JSON 한 개를 검증해 정규화한다. 잘못된 입력은 브라우저를 띄우기 전에 종료 코드 2로 끝난다.
// 오류 문장에는 키 이름만 쓰고 입력값을 되풀이하지 않는다.

import { usageError } from './errors.mjs';
import { parseIsoDate } from './search-expression.mjs';

export const DEFAULT_ACCOUNT = 'vincent@snuailab.ai';
export const DEFAULT_MAX_RESULTS = 200;
export const DEFAULT_TIMEOUT_SECONDS = 60;
export const DEFAULT_LOGIN_TIMEOUT_SECONDS = 600;
export const MAX_STDIN_BYTES = 1024 * 1024;

const ALLOWED_KEYS = new Set([
  'account',
  'query',
  'names',
  'receivedAfter',
  'receivedBefore',
  'maxResults',
  'includeBody',
  'login',
  'headless',
  'profileDirectory',
  'timeoutSeconds',
  'loginTimeoutSeconds',
]);

const EMAIL = /^[^\s@<>()[\]\\,;:"']+@[^\s@<>()[\]\\,;:"']+\.[^\s@<>()[\]\\,;:"']+$/;
const MIN_YEAR = 1990;
const MAX_YEAR = 2099;

function bad(key, what) {
  return usageError('badInput', `입력 오류: '${key}' ${what}`);
}

function readStringList(raw, key) {
  if (raw === undefined || raw === null) return [];
  if (!Array.isArray(raw)) throw bad(key, '는 문자열 배열이어야 합니다.');
  for (const item of raw) {
    if (typeof item !== 'string') throw bad(key, '의 모든 항목은 문자열이어야 합니다.');
  }
  return [...raw];
}

function readBoolean(raw, key) {
  if (raw === undefined || raw === null) return false;
  if (typeof raw !== 'boolean') throw bad(key, '는 true/false 여야 합니다.');
  return raw;
}

function readDate(raw, key) {
  if (raw === undefined || raw === null) return null;
  const p = typeof raw === 'string' ? parseIsoDate(raw) : null;
  if (!p) throw bad(key, '는 YYYY-MM-DD 형식의 실제 날짜이거나 null 이어야 합니다.');
  if (p.y < MIN_YEAR || p.y > MAX_YEAR) throw bad(key, `의 연도는 ${MIN_YEAR}~${MAX_YEAR} 이어야 합니다.`);
  return raw;
}

function readNumber(raw, key, { fallback, min, max, integer }) {
  if (raw === undefined || raw === null) return fallback;
  if (typeof raw !== 'number' || !Number.isFinite(raw) || (integer && !Number.isInteger(raw)) || raw < min || raw > max) {
    throw bad(key, `는 ${min}~${max} 범위의 ${integer ? '정수' : '숫자'}여야 합니다.`);
  }
  return raw;
}

/**
 * @param {unknown} raw JSON.parse 결과
 */
export function parseRequest(raw) {
  if (raw === null || typeof raw !== 'object' || Array.isArray(raw)) {
    throw usageError('badInput', '입력 오류: stdin 은 JSON 객체 하나여야 합니다.');
  }
  for (const key of Object.keys(raw)) {
    if (!ALLOWED_KEYS.has(key)) throw usageError('badInput', `입력 오류: 알 수 없는 키 '${key}' 입니다.`);
  }

  let account = DEFAULT_ACCOUNT;
  if (raw.account !== undefined && raw.account !== null) {
    if (typeof raw.account !== 'string' || !EMAIL.test(raw.account.trim())) {
      throw bad('account', '는 이메일 주소 형식이어야 합니다.');
    }
    account = raw.account.trim();
  }
  account = account.toLowerCase();

  const login = readBoolean(raw.login, 'login');
  const headless = readBoolean(raw.headless, 'headless');
  if (login && headless) {
    throw usageError('loginHeadless', '입력 오류: 로그인 모드(login)는 화면이 보여야 하므로 headless 와 함께 쓸 수 없습니다.');
  }

  const receivedAfter = readDate(raw.receivedAfter, 'receivedAfter');
  const receivedBefore = readDate(raw.receivedBefore, 'receivedBefore');
  if (receivedAfter && receivedBefore && receivedAfter >= receivedBefore) {
    throw usageError('dateRange', '입력 오류: receivedAfter 는 receivedBefore 보다 앞서야 합니다(receivedBefore 는 미포함).');
  }

  let profileDirectory = null;
  if (raw.profileDirectory !== undefined && raw.profileDirectory !== null) {
    if (typeof raw.profileDirectory !== 'string' || raw.profileDirectory.trim() === '' || raw.profileDirectory.includes('\0')) {
      throw bad('profileDirectory', '는 비어 있지 않은 경로 문자열이거나 null 이어야 합니다.');
    }
    profileDirectory = raw.profileDirectory;
  }

  return {
    account,
    query: readStringList(raw.query, 'query'),
    names: readStringList(raw.names, 'names'),
    receivedAfter,
    receivedBefore,
    maxResults: readNumber(raw.maxResults, 'maxResults', { fallback: DEFAULT_MAX_RESULTS, min: 1, max: 1000, integer: true }),
    includeBody: readBoolean(raw.includeBody, 'includeBody'),
    login,
    headless,
    profileDirectory,
    timeoutSeconds: readNumber(raw.timeoutSeconds, 'timeoutSeconds', {
      fallback: DEFAULT_TIMEOUT_SECONDS,
      min: 1,
      max: 3600,
      integer: false,
    }),
    loginTimeoutSeconds: readNumber(raw.loginTimeoutSeconds, 'loginTimeoutSeconds', {
      fallback: DEFAULT_LOGIN_TIMEOUT_SECONDS,
      min: 1,
      max: 3600,
      integer: false,
    }),
  };
}

/**
 * stdin 전체를 읽어 JSON 객체 하나로 파싱한다. 크기 상한을 둔다.
 * @param {AsyncIterable<Buffer|string>} stream
 */
export async function readJsonObject(stream, maxBytes = MAX_STDIN_BYTES) {
  const chunks = [];
  let size = 0;
  for await (const chunk of stream) {
    const buf = typeof chunk === 'string' ? Buffer.from(chunk, 'utf8') : chunk;
    size += buf.length;
    if (size > maxBytes) throw usageError('inputTooLarge', '입력 오류: stdin 이 너무 큽니다.');
    chunks.push(buf);
  }
  let text = Buffer.concat(chunks).toString('utf8');
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  if (text.trim() === '') {
    throw usageError('noInput', '입력 오류: stdin 으로 JSON 객체 하나를 전달해야 합니다.');
  }
  try {
    return JSON.parse(text);
  } catch {
    throw usageError('badJson', '입력 오류: stdin 이 올바른 JSON 이 아닙니다.');
  }
}
