// 테스트 전용: 실제 Chromium 에서 도는 "합성 Outlook 화면".
// - 실제 주소(https://outlook.cloud.microsoft/mail/)를 context.route 로 가로채 합성 HTML 을 돌려준다(외부 통신 없음).
// - 역할·이름은 실제 화면 관찰과 같게 만든다: 검색 콤보박스 '검색', 폴더 트리의 계정 treeitem, 목록 '메시지 목록 …',
//   결과 제목 '결과', 행 listitem, 읽기 창 '읽기 창'.
// - 서버 쪽은 Node 에서 흉내 낸다(검색식을 작은 AQS 해석기로 평가). 가상 스크롤·지연 로딩·지연된 읽기 창을 재현한다.
// - 모든 주소·이름은 가짜다(example.invalid).

import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { openSession } from '../../lib/browser-session.mjs';
import { parseRequest } from '../../lib/request.mjs';
import { runMailSearch } from '../../mail-search.mjs';

export const ACCOUNT = 'alice@example.invalid';

export const FAST_TIMING = Object.freeze({
  pollMs: 40,
  settleMs: 250,
  scrollSettleMs: 250,
  endIdleRounds: 3,
  paneSettleMs: 120,
  loginGraceMs: 600,
  loginPollMs: 150,
  treeWaitMs: 600,
});

const MAIL_HOST = 'outlook.cloud.microsoft';
const LOGIN_HOST = 'login.microsoftonline.com';

export function mailRow(id, from, subject, preview, date, body) {
  return { id, from, subject, preview, date, body: body ?? `${subject} 본문 ${id}` };
}

export const BASE_SCENARIO = Object.freeze({
  kind: 'mailbox', // 'mailbox' | 'login'(항상 로그인으로 이동) | 'login-then-mailbox'(로그인 페이지가 잠시 뒤 메일함으로 이동)
  accounts: [ACCOUNT],
  accountSource: 'tree', // 'tree' | 'menu' | 'none'
  inbox: [
    mailRow('INBOX-1', '받은편지 발신자 가', '받은편지 제목 하나', '미리보기 하나', '2026-10-01'),
    mailRow('INBOX-2', '받은편지 발신자 나', '받은편지 제목 둘', '미리보기 둘', '2026-10-02'),
  ],
  corpus: [],
  matchExpression: true, // false: 서버가 검색식을 무시하고 corpus 전부를 돌려준다
  scope: 'current', // 'current' | 'all' | 'none'(범위 선택 UI 없음)
  loadingMs: 0, // 결과 제목은 보이지만 행·없음 문구가 아직 없는 시간
  busyWhileLoading: true,
  keepInboxWhileLoading: false,
  pageSize: 20,
  fetchDelayMs: 40,
  endless: false,
  readingPane: true,
  paneDelayMs: 0,
  emptyText: '검색 결과가 없습니다',
  loginRedirectMs: null, // 'login-then-mailbox' 에서 로그인 페이지가 메일함으로 이동하기까지
  treeDelayMs: 0, // 폴더 트리가 검색창보다 늦게 나타나는 시간
});

// ───────────────────────────── 검색식 해석기(서버 흉내) ─────────────────────────────

function toIso(mdy) {
  const [m, d, y] = mdy.split('/');
  return `${y}-${m}-${d}`;
}

/** (a OR b) AND (c) AND received:MM/DD/YYYY..MM/DD/YYYY 를 평가하는 함수로 바꾼다. 문법이 틀리면 예외. */
export function compileExpression(expression) {
  const tokens = expression.match(/\(|\)|received:\d{2}\/\d{2}\/\d{4}\.\.\d{2}\/\d{2}\/\d{4}|[^\s()]+/g) ?? [];
  let pos = 0;
  const peek = () => tokens[pos];

  function parseOr() {
    let left = parseAnd();
    while (peek() === 'OR') {
      pos += 1;
      const right = parseAnd();
      const l = left;
      left = (row) => l(row) || right(row);
    }
    return left;
  }
  function parseAnd() {
    let left = parseAtom();
    while (peek() !== undefined && peek() !== ')' && peek() !== 'OR') {
      if (peek() === 'AND') pos += 1;
      const right = parseAtom();
      const l = left;
      left = (row) => l(row) && right(row);
    }
    return left;
  }
  function parseAtom() {
    const token = tokens[pos];
    pos += 1;
    if (token === undefined) throw new Error('unexpected end of expression');
    if (token === '(') {
      const inner = parseOr();
      if (tokens[pos] !== ')') throw new Error('unbalanced parenthesis');
      pos += 1;
      return inner;
    }
    if (token === ')' || token === 'OR' || token === 'AND') throw new Error(`unexpected token ${token}`);
    if (token.startsWith('received:')) {
      const [from, to] = token.slice('received:'.length).split('..').map(toIso);
      return (row) => row.date >= from && row.date <= to;
    }
    if (/[:"+\-<>=]/.test(token)) throw new Error(`operator characters in token ${token}`);
    const needle = token.replace(/\*$/, '').toLowerCase();
    return (row) => `${row.from} ${row.subject} ${row.preview ?? ''}`.toLowerCase().includes(needle);
  }

  const evaluate = parseOr();
  if (pos !== tokens.length) throw new Error('trailing tokens');
  return evaluate;
}

// ───────────────────────────── 합성 페이지 ─────────────────────────────

const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

// 브라우저에서 실행된다(toString 으로 페이지에 심는다). 바깥 변수를 참조하지 말 것.
function pageMain(config) {
  const ROW_H = 64;
  const $ = (id) => document.getElementById(id);
  const list = $('list');
  const inner = $('inner');
  const state = { items: config.inbox.slice(), searchId: null, nextOffset: 0, hasMore: false, fetching: false };

  function render() {
    inner.style.height = `${state.items.length * ROW_H}px`;
    const first = Math.max(0, Math.floor(list.scrollTop / ROW_H) - 2);
    const last = Math.min(state.items.length, Math.ceil((list.scrollTop + list.clientHeight) / ROW_H) + 2);
    const fragment = document.createDocumentFragment();
    for (let i = first; i < last; i += 1) {
      const item = state.items[i];
      const row = document.createElement('div');
      row.setAttribute('role', 'listitem');
      row.className = 'row';
      row.style.top = `${i * ROW_H}px`;
      row.tabIndex = 0;
      for (const key of ['from', 'subject', 'preview', 'date']) {
        if (item[key] === undefined || item[key] === null) continue;
        const span = document.createElement('span');
        span.className = key;
        span.textContent = item[key];
        row.appendChild(span);
      }
      row.addEventListener('click', () => openItem(item));
      fragment.appendChild(row);
    }
    inner.replaceChildren(fragment);
  }

  async function maybeLoadMore() {
    if (!state.hasMore || state.fetching) return;
    if (list.scrollTop + list.clientHeight < inner.offsetHeight - ROW_H * 3) return;
    state.fetching = true;
    list.setAttribute('aria-busy', 'true');
    const page = await window.__synthetic('fetch', { searchId: state.searchId, offset: state.nextOffset, limit: config.pageSize });
    state.items = state.items.concat(page.rows);
    state.nextOffset += page.rows.length;
    state.hasMore = page.hasMore;
    state.fetching = false;
    list.removeAttribute('aria-busy');
    render();
  }

  async function openItem(item) {
    const opened = await window.__synthetic('open', { id: item.id });
    const pane = $('pane');
    if (!pane) return;
    await new Promise((resolve) => setTimeout(resolve, opened.delayMs));
    pane.textContent = `${item.from}\n${item.subject}\n\n${opened.body}`;
  }

  list.addEventListener('scroll', () => {
    render();
    maybeLoadMore();
  });

  let searchReady = !config.searchReadyDelayMs;
  if (!searchReady) setTimeout(() => { searchReady = true; }, config.searchReadyDelayMs);
  $('q').addEventListener('input', () => {
    if (!searchReady) $('q').value = '';
  });

  $('q').addEventListener('keydown', async (event) => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    const scopeButton = $('scope');
    const started = await window.__synthetic('search', {
      expression: $('q').value,
      scope: scopeButton ? scopeButton.textContent : null,
    });
    $('heading').hidden = false;
    $('empty').hidden = true;
    state.searchId = started.searchId;
    state.nextOffset = 0;
    state.hasMore = false;
    if (!config.keepInboxWhileLoading) state.items = [];
    list.scrollTop = 0;
    if (config.busyWhileLoading) list.setAttribute('aria-busy', 'true');
    render();
    await new Promise((resolve) => setTimeout(resolve, config.loadingMs));
    const first = await window.__synthetic('fetch', { searchId: state.searchId, offset: 0, limit: config.pageSize });
    list.removeAttribute('aria-busy');
    state.items = first.rows;
    state.nextOffset = first.rows.length;
    state.hasMore = first.hasMore;
    if (first.rows.length === 0) {
      $('empty').textContent = config.emptyText;
      $('empty').hidden = false;
    }
    render();
    maybeLoadMore();
  });

  const scope = $('scope');
  if (scope) {
    scope.addEventListener('click', () => {
      $('scopeMenu').hidden = !$('scopeMenu').hidden;
    });
    for (const item of document.querySelectorAll('#scopeMenu [role="menuitemradio"]')) {
      item.addEventListener('click', () => {
        scope.textContent = item.textContent;
        $('scopeMenu').hidden = true;
      });
    }
  }

  const acct = $('acct');
  if (acct) {
    acct.addEventListener('click', () => {
      $('acctDialog').hidden = !$('acctDialog').hidden;
    });
    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') $('acctDialog').hidden = true;
    });
  }

  if (config.treeDelayMs > 0) {
    $('tree').hidden = true;
    setTimeout(() => {
      $('tree').hidden = false;
    }, config.treeDelayMs);
  }
  render();
}

function renderMailbox(sc) {
  const treeItems =
    sc.accountSource === 'tree'
      ? sc.accounts
          .map((a) => `<div role="treeitem" aria-level="1">${esc(sc.accountPrefix ?? '')}${esc(a)}</div><div role="treeitem" aria-level="2">받은 편지함</div>`)
          .join('')
      : '<div role="treeitem" aria-level="1">받은 편지함</div>';
  const accountMenu =
    sc.accountSource === 'menu'
      ? `<button type="button" id="acct" aria-label="계정 관리자">AB</button><div role="dialog" id="acctDialog" aria-label="계정 관리자" hidden>Alice<br>${esc(sc.accounts[0])}</div>`
      : '';
  const scopeControl =
    sc.scope === 'none'
      ? ''
      : `<button type="button" id="scope">${sc.scope === 'all' ? '모든 폴더' : '현재 폴더'}</button><div role="menu" id="scopeMenu" hidden><div role="menuitemradio" tabindex="0">현재 폴더</div><div role="menuitemradio" tabindex="0">모든 폴더</div></div>`;
  const pane = sc.readingPane ? '<div id="pane" role="region" aria-label="읽기 창">메일을 선택하세요</div>' : '';
  const config = {
    inbox: sc.inbox,
    pageSize: sc.pageSize,
    loadingMs: sc.loadingMs,
    busyWhileLoading: sc.busyWhileLoading,
    keepInboxWhileLoading: sc.keepInboxWhileLoading,
    emptyText: sc.emptyText,
    treeDelayMs: sc.treeDelayMs,
    searchReadyDelayMs: sc.searchReadyDelayMs ?? 0,
  };
  return `<!doctype html><html lang="ko"><head><meta charset="utf-8"><title>synthetic mailbox</title><style>
body{margin:0;font:12px/14px sans-serif}
#layout{display:flex;height:100vh}
#folders{width:240px}
#middle{width:560px}
#list{height:480px;overflow:auto;position:relative}
#inner{position:relative}
.row{position:absolute;left:0;right:0;height:64px;box-sizing:border-box;border-bottom:1px solid #ccc;overflow:hidden}
.row span{display:block}
#pane{flex:1;min-height:100px;white-space:pre-wrap}
[hidden]{display:none !important}
</style></head><body>
<div id="layout">
<div id="folders"><div id="tree" role="tree" aria-label="폴더 창">${treeItems}</div></div>
<div id="middle">
${accountMenu}
<input id="q" type="text" role="combobox" aria-label="${esc(sc.searchLabel ?? '검색')}" style="width:100%">
${scopeControl}
${sc.resultGroup ? '<div id="heading" role="group" aria-label="결과" hidden>결과</div>' : '<h2 id="heading" hidden>결과</h2>'}
<div id="empty" hidden></div>
<div id="list" role="list" aria-label="메시지 목록 선택한 대화가 없습니다."><div id="inner" role="presentation"></div></div>
</div>
${pane}
</div>
<script>(${pageMain.toString()})(${JSON.stringify(config)});</script>
</body></html>`;
}

function renderLogin(sc) {
  const redirect =
    sc.loginRedirectMs === null
      ? ''
      : `<script>setTimeout(function(){location.href='https://${MAIL_HOST}/mail/';},${sc.loginRedirectMs});</script>`;
  return `<!doctype html><html lang="ko"><head><meta charset="utf-8"><title>sign in</title></head><body><h1>로그인</h1>${redirect}</body></html>`;
}

// ───────────────────────────── 하니스 ─────────────────────────────

/**
 * 실제 headless Chromium 하나를 임시 프로필로 띄우고, 합성 Outlook 을 서빙하는 테스트 하니스.
 * 제품 코드의 launch 를 대신하는 launch 를 제공해 runMailSearch 를 그대로 돌릴 수 있다.
 */
export async function createHarness() {
  const profileDir = await fs.mkdtemp(path.join(os.tmpdir(), 'mail-search-test-'));
  const root = await openSession({ profileDir, headless: true, timeoutMs: 60_000 });
  const { context } = root;

  const state = {
    scenario: { ...BASE_SCENARIO },
    searches: [],
    opened: [],
    fetches: 0,
    results: new Map(),
    launchCalls: [],
    sessions: [],
    loggedIn: false,
    serverError: null,
  };

  function findRow(id) {
    const sc = state.scenario;
    return [...sc.inbox, ...sc.corpus].find((r) => r.id === id) ?? { id, from: id, subject: id, preview: '', date: '2026-10-01', body: id };
  }

  await context.exposeFunction('__synthetic', async (op, payload) => {
    const sc = state.scenario;
    try {
      if (op === 'search') {
        state.searches.push({ expression: payload.expression, scope: payload.scope });
        const id = state.searches.length;
        const matcher = sc.matchExpression ? compileExpression(payload.expression) : () => true;
        state.results.set(id, sc.corpus.filter(matcher));
        return { searchId: id };
      }
      if (op === 'fetch') {
        state.fetches += 1;
        await new Promise((resolve) => setTimeout(resolve, sc.fetchDelayMs));
        if (sc.endless) {
          const rows = Array.from({ length: payload.limit }, (_, i) => {
            const n = payload.offset + i;
            return mailRow(`E${n}`, `끝없는 발신자 ${n}`, `휴가 끝없음 ${n}`, '미리보기', '2026-10-15');
          });
          return { rows, hasMore: true };
        }
        const all = state.results.get(payload.searchId) ?? [];
        const rows = all.slice(payload.offset, payload.offset + payload.limit);
        return { rows, hasMore: payload.offset + payload.limit < all.length };
      }
      if (op === 'open') {
        state.opened.push(payload.id);
        return { delayMs: sc.paneDelayMs, body: findRow(payload.id).body };
      }
    } catch (error) {
      state.serverError = String(error?.message ?? error);
      throw error;
    }
    throw new Error(`unknown op ${op}`);
  });

  await context.route(/.*/, async (route) => {
    const url = new URL(route.request().url());
    const sc = state.scenario;
    const html = (body) => route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body });
    if (url.pathname === '/favicon.ico') return route.fulfill({ status: 204, body: '' });
    if (url.hostname === MAIL_HOST) {
      if (sc.kind === 'mailbox' || (sc.kind === 'login-then-mailbox' && state.loggedIn)) return html(renderMailbox(sc));
      // Playwright의 route.fulfill(302) 뒤 리디렉션 요청은 이 핸들러를 다시 거치지 않을 수 있다.
      // 합성 로그인 페이지도 반드시 가로채도록 별도 문서 탐색으로 전환한다(외부 통신 없음).
      return html(`<script>location.replace('https://${LOGIN_HOST}/common/oauth2/authorize');</script>`);
    }
    if (url.hostname === LOGIN_HOST) {
      if (sc.kind === 'login-then-mailbox') state.loggedIn = true;
      return html(renderLogin(sc));
    }
    return route.abort();
  });

  const launch = async (options) => {
    state.launchCalls.push({ headless: options.headless, viewport: options.viewport });
    const page = await context.newPage();
    const session = {
      context,
      page,
      closed: false,
      async close() {
        session.closed = true;
        await page.close().catch(() => {});
      },
    };
    state.sessions.push(session);
    return session;
  };

  return {
    state,
    profileDir,
    launch,
    /** 시나리오를 바꾸고 기록을 비운다. */
    use(overrides = {}) {
      state.scenario = { ...BASE_SCENARIO, ...overrides };
      state.searches = [];
      state.opened = [];
      state.fetches = 0;
      state.results = new Map();
      state.launchCalls = [];
      state.sessions = [];
      state.loggedIn = false;
      state.serverError = null;
    },
    /** runMailSearch 를 합성 화면에 대해 실행한다. */
    run(requestOverrides = {}, deps = {}) {
      const request = parseRequest({ account: ACCOUNT, profileDirectory: profileDir, timeoutSeconds: 8, ...requestOverrides });
      return runMailSearch(request, { launch, timing: FAST_TIMING, now: () => new Date(2026, 9, 21), ...deps });
    },
    async close() {
      await root.close();
      await fs.rm(profileDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {});
    },
  };
}
