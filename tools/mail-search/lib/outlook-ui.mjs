// Outlook 웹 화면의 "의미 기반" 로케이터(역할·접근 가능한 이름, 한국어/영어)와 로그인·계정 확인.
// 네트워크 요청/응답이나 내부 API는 쓰지 않는다. 화면에 보이는 DOM만 읽는다.
//
// 아래 UI 문자열은 실제 Outlook 화면 관찰(검색 콤보박스 '검색'/'Search', 폴더 트리의 계정 treeitem,
// 목록 '메시지 목록 …'/'Message list …', 결과 제목 '결과'/'Results')과 공개 문서에 기반한다.
// 화면 문구가 바뀌면 이 상수만 고친다.

import { EXIT, MailSearchError, runtimeError } from './errors.mjs';
import { assertPageOpen, safeVisible, sleep, throwIfAborted, waitFor } from './async.mjs';

export const OUTLOOK_MAIL_URL = 'https://outlook.cloud.microsoft/mail/';
const OUTLOOK_HOSTS = ['outlook.cloud.microsoft', 'outlook.office.com', 'outlook.office365.com', 'outlook.live.com'];

export const UI = Object.freeze({
  searchBox: /^\s*(?:검색|전자\s*메일,\s*모임,\s*파일\s*등을\s*검색합니다\.?|Search(?:\s+(?:mail|for)\b.*)?)\s*$/i,
  messageList: /^\s*(?:메시지\s*목록|Message\s*list)/i,
  resultsHeading: /^\s*(?:검색\s*결과|결과|search\s+results|results)(?:[\s(:：]|$)/i,
  // 검색 결과 없음. 메일 행 안의 글자는 제외하고 본다(outlook-search.mjs).
  emptyState:
    /(?:검색\s*결과(?:가|를|는)?\s*(?:없|찾지)|결과(?:가|를|는)?\s*(?:없|찾지\s*못|찾을\s*수\s*없)|일치하는\s*.{0,12}(?:없|찾지)|항목이\s*없|표시할\s*.{0,8}없|no\s+results|no\s+items|no\s+messages|(?:didn['’]t|couldn['’]t|can['’]t)\s+find|nothing\s+(?:was\s+)?found|nothing\s+to\s+show)/i,
  // role=alert 안의 오류 문구. 이게 보이면 "결과 없음"이 아니라 실패로 본다.
  errorAlert: /(?:문제가\s*발생|오류가\s*발생|다시\s*시도|something\s+went\s+wrong|try\s+again|couldn['’]t\s+(?:complete|connect|search)|unable\s+to)/i,
  scopeAny: /(?:현재\s*폴더|모든\s*폴더|하위\s*폴더|current\s+folder|all\s+folders|subfolders)/i,
  scopeAll: /(?:모든\s*폴더|all\s+folders)/i,
  readingPane: /^\s*(?:읽기\s*창|reading\s*pane)/i,
  accountMenu: /(?:계정\s*관리자|account\s+manager|내\s*계정|my\s+account)/i,
});

const EMAIL_IN_TEXT = /[\p{L}\p{N}.!#$%&'*+/=?^_`{|}~-]+@[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+/gu;

export function isOutlookUrl(url) {
  try {
    const u = new URL(url);
    return u.protocol === 'https:' && OUTLOOK_HOSTS.includes(u.hostname.toLowerCase());
  } catch {
    return false;
  }
}

// ───────────────────────────── 로케이터 ─────────────────────────────

export const searchBox = (page) => page.getByRole('combobox', { name: UI.searchBox }).first();

export const resultsHeading = (page) =>
  page.getByRole('heading', { name: UI.resultsHeading }).or(page.getByRole('group', { name: UI.resultsHeading })).first();

export const messageList = (page) =>
  page
    .getByRole('list', { name: UI.messageList })
    .or(page.getByRole('listbox', { name: UI.messageList }))
    .or(page.getByRole('grid', { name: UI.messageList }))
    .or(page.getByRole('region', { name: UI.messageList }))
    .first();

export const readingPane = (page) => page.getByLabel(UI.readingPane).first();

export const scopeControl = (page) =>
  page.getByRole('button', { name: UI.scopeAny }).or(page.getByRole('combobox', { name: UI.scopeAny })).first();

// ───────────────────────────── 접속 상태 ─────────────────────────────

/** Outlook 주소로 이동한다. 네트워크 실패는 안전한 문장으로 바꾼다. */
export async function gotoMail(page, url, timeoutMs) {
  try {
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: timeoutMs });
  } catch (error) {
    if (/net::ERR_/.test(String(error?.message ?? ''))) {
      throw runtimeError('network', 'Outlook에 접속하지 못했습니다(네트워크 오류).');
    }
    throw error;
  }
}

/**
 * 메일함 화면이 뜰 때까지 기다린다.
 * - 'mailbox': Outlook 주소이고 검색창이 보인다.
 * - 'login': Outlook 주소가 아닌 곳(로그인/인증 페이지)에 loginGraceMs 이상 머문다(조용한 SSO 리디렉션은 기다려 준다).
 * - 'unrecognized': Outlook 주소인데 제한 시간 안에 검색창이 나타나지 않았다.
 */
export async function waitForMailboxState(page, { timeoutMs, timing, signal }) {
  const deadline = Date.now() + timeoutMs;
  let offHostSince = null;
  for (;;) {
    throwIfAborted(signal);
    assertPageOpen(page);
    const url = page.url();
    if (url.startsWith('chrome-error:')) {
      throw runtimeError('network', 'Outlook에 접속하지 못했습니다(네트워크 오류).');
    }
    const onOutlook = isOutlookUrl(url);
    if (onOutlook) {
      offHostSince = null;
      if (await safeVisible(searchBox(page))) return 'mailbox';
    } else {
      offHostSince ??= Date.now();
      if (Date.now() - offHostSince >= timing.loginGraceMs) return 'login';
    }
    if (Date.now() >= deadline) return onOutlook ? 'unrecognized' : 'login';
    await sleep(timing.pollMs, signal);
  }
}

export const LOGIN_REQUIRED_MESSAGE =
  'Outlook 로그인이 필요합니다(로그아웃됨 또는 추가 인증 필요). 검색을 중단했습니다. 로그인 모드(-Login)로 실행해 브라우저에서 직접 로그인·다중 인증을 완료하세요.';

export function loginRequiredError() {
  return new MailSearchError(EXIT.LOGIN_REQUIRED, 'loginRequired', LOGIN_REQUIRED_MESSAGE);
}

export function unrecognizedUiError() {
  return runtimeError('uiUnrecognized', 'Outlook 메일함 화면(검색창)을 인식하지 못했습니다. 화면이 바뀌었을 수 있습니다.');
}

// ───────────────────────────── 계정 확인 ─────────────────────────────

function extractEmails(text) {
  return (String(text ?? '').match(EMAIL_IN_TEXT) ?? []).map((e) => e.toLowerCase());
}

/** 폴더 트리의 계정 항목(이메일이 이름인 treeitem). 화면 순서대로 각 항목의 첫 주소. */
async function readTreeAccounts(page) {
  const items = page.getByRole('tree').getByRole('treeitem', { name: /@/ });
  const labels = await items.evaluateAll((els) =>
    els.map((el) => (el.getAttribute('aria-label') || el.textContent || '').replace(/\s+/g, ' ').trim()),
  );
  const accounts = [];
  for (const label of labels) {
    const first = extractEmails(label)[0];
    if (first) accounts.push(first);
  }
  return accounts;
}

/** 계정 관리자 메뉴를 열어 그 창 안의 첫 주소를 읽고 닫는다. 메뉴를 못 찾으면 null. */
async function readMenuAccount(page, { timeoutMs, timing, signal }) {
  const button = page.getByRole('button', { name: UI.accountMenu }).first();
  if (!(await safeVisible(button))) return null;
  await button.click({ timeout: timeoutMs });
  try {
    const panel = page
      .getByRole('dialog')
      .or(page.getByRole('menu'))
      .or(page.getByRole('region', { name: UI.accountMenu }))
      .first();
    const shown = await waitFor(() => safeVisible(panel), {
      timeoutMs: Math.min(timeoutMs, 5000),
      pollMs: timing.pollMs,
      signal,
    });
    if (!shown) return null;
    const text = await panel.innerText({ timeout: timeoutMs });
    return extractEmails(text)[0] ?? null;
  } finally {
    await page.keyboard.press('Escape').catch(() => {});
  }
}

/**
 * 로그인된 계정이 기대한 계정인지 화면에서 확인한다(메일 본문·목록 글자는 보지 않는다).
 * 1) 폴더 트리의 첫 계정 항목(기본 사서함). 2) 트리에 계정이 없으면 계정 관리자 메뉴.
 * @returns {Promise<{status: 'verified'|'mismatch'|'unknown', source: 'tree'|'menu'|null, detected: string|null}>}
 */
export async function verifyAccount(page, account, { timeoutMs, timing, signal, useMenu = true }) {
  const expected = account.trim().toLowerCase();
  const tree = await readTreeAccounts(page);
  if (tree.length > 0) {
    const detected = tree[0];
    return { status: detected === expected ? 'verified' : 'mismatch', source: 'tree', detected };
  }
  if (useMenu) {
    const detected = await readMenuAccount(page, { timeoutMs, timing, signal });
    if (detected) return { status: detected === expected ? 'verified' : 'mismatch', source: 'menu', detected };
  }
  return { status: 'unknown', source: null, detected: null };
}

/**
 * 폴더 트리는 검색창보다 늦게 그려질 수 있다. 트리에서 계정이 확인될 때까지 timing.treeWaitMs 동안 기다린 뒤,
 * 그래도 없으면 계정 관리자 메뉴를 한 번 확인한다. 계정을 확인하기 전에는 메일 목록을 읽지 않는다.
 */
export async function verifyAccountWhenReady(page, account, { timeoutMs, timing, signal }) {
  const treeDeadline = Date.now() + Math.min(timeoutMs, timing.treeWaitMs);
  for (;;) {
    throwIfAborted(signal);
    assertPageOpen(page);
    const result = await verifyAccount(page, account, { timeoutMs, timing, signal, useMenu: false });
    if (result.status !== 'unknown') return result;
    if (Date.now() >= treeDeadline) break;
    await sleep(timing.pollMs, signal);
  }
  return verifyAccount(page, account, { timeoutMs, timing, signal, useMenu: true });
}

export function accountMismatchError(detected) {
  return new MailSearchError(
    EXIT.LOGIN_REQUIRED,
    'accountMismatch',
    `로그인된 계정(${detected})이 요청한 계정과 다릅니다. 메일을 읽지 않고 중단했습니다. 브라우저 프로필에서 로그아웃한 뒤 로그인 모드(-Login)로 올바른 계정에 로그인하세요.`,
  );
}

export function accountUnverifiedError() {
  return runtimeError('accountUnverified', '화면에서 로그인 계정을 확인하지 못했습니다(폴더 트리·계정 메뉴). 계정을 확인할 수 없어 메일을 읽지 않고 중단했습니다.');
}

// ───────────────────────────── 로그인 모드 ─────────────────────────────

/**
 * 사용자가 직접 로그인하는 동안 기다린다. 입력란을 자동으로 채우지 않는다.
 * 메일함 화면이 뜨고 계정이 확인되면 반환한다. 다른 계정이 보이면 사용자가 계정을 바꿀 수 있게 계속 기다리되,
 * 시간이 끝나면 종료 코드 3(계정 불일치)으로 끝낸다.
 */
export async function waitForLoggedInMailbox(page, account, { loginTimeoutMs, timeoutMs, timing, signal, log = () => {} }) {
  const deadline = Date.now() + loginTimeoutMs;
  let lastMismatch = null;
  let announcedMismatch = false;
  let polls = 0;
  for (;;) {
    throwIfAborted(signal);
    assertPageOpen(page);
    if (isOutlookUrl(page.url()) && (await safeVisible(searchBox(page)))) {
      // 계정 메뉴는 화면을 건드리므로 폴링마다가 아니라 가끔만 시도한다.
      const result = await verifyAccount(page, account, { timeoutMs, timing, signal, useMenu: polls % 5 === 0 });
      if (result.status === 'verified') return result;
      if (result.status === 'mismatch') {
        lastMismatch = result.detected;
        if (!announcedMismatch) {
          announcedMismatch = true;
          log(`다른 계정(${result.detected})으로 로그인되어 있습니다. 브라우저에서 ${account} 계정으로 바꿔 로그인하세요.`);
        }
      }
    }
    polls++;
    if (Date.now() >= deadline) break;
    await sleep(timing.loginPollMs, signal);
  }
  if (lastMismatch) throw accountMismatchError(lastMismatch);
  throw new MailSearchError(
    EXIT.LOGIN_TIMEOUT,
    'loginTimeout',
    '제한 시간 안에 로그인이 확인되지 않았습니다. 브라우저에서 로그인(다중 인증·동의 포함)을 마치고 다시 실행하세요.',
  );
}
