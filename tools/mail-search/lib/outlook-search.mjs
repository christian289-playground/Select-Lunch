// 검색창에 검색식을 입력하고, 결과가 확정될 때까지 기다리고, 보이는 행을 모은다.
// 화면에 보이는 DOM만 읽는다(네트워크 응답·내부 API·쿠키/토큰은 보지 않는다).
//
// 정직성 규칙
// - "결과 없음"은 화면의 명시적 문구가 보일 때만 인정한다. 행이 없다고 해서 비었다고 추정하지 않는다.
// - 목록이 가상 스크롤이므로 맨 아래에서 새 행이 연속으로 나타나지 않는 것을 확인했을 때만 "끝"으로 본다.
//   확인하지 못하면 incompleteReason 에 endNotConfirmed 를 싣는다.
// - 본문(includeBody)은 읽기 창을 실제로 읽거나 실패로 끝낸다. 조용히 빼지 않는다.

import { runtimeError } from './errors.mjs';
import { assertPageOpen, safeVisible, sleep, throwIfAborted, waitFor } from './async.mjs';
import { sanitizeBody, sanitizeLine, normalizeForMatch } from './text.mjs';
import { isDateInRange, parseVisibleDate, pickRowDate } from './visible-date.mjs';
import { UI, messageList, readingPane, resultsHeading, scopeControl, searchBox } from './outlook-ui.mjs';
import { defaultMaxScrollRounds } from './timing.mjs';

const ROW_SELECTOR = '[role="listitem"], [role="option"], li';

// ───────────────────────────── 페이지 안에서 실행되는 함수들 ─────────────────────────────
// Playwright 가 직렬화해 페이지로 보내므로 바깥 변수를 참조하면 안 된다.

/** 목록 안의 최상위 행(중첩된 항목 제외)을 모아 보이는 텍스트·줄·날짜 후보를 돌려준다. */
function snapshotInPage(list, selector) {
  const norm = (s) => String(s ?? '').replace(/\s+/g, ' ').trim();
  const candidates = Array.from(list.querySelectorAll(selector));
  const shown = candidates.filter((el) => {
    const style = window.getComputedStyle(el);
    return style.display !== 'none' && style.visibility !== 'hidden' && el.getAttribute('aria-hidden') !== 'true';
  });
  const top = shown.filter((el) => !shown.some((other) => other !== el && other.contains(el)));
  const rows = [];
  const infos = [];
  for (const el of top) {
    const lines = String(el.innerText || '')
      .split(/\n+/)
      .map(norm)
      .filter(Boolean);
    let text = norm(lines.join(' '));
    if (!text) text = norm(el.getAttribute('aria-label'));
    if (!text) continue;
    const dateTexts = [];
    for (const t of el.querySelectorAll('time[datetime]')) {
      const v = norm(t.getAttribute('datetime'));
      if (/^\d{4}-\d{2}-\d{2}$/.test(v)) dateTexts.push(v);
    }
    for (const leaf of el.querySelectorAll('*')) {
      if (leaf.childElementCount !== 0) continue;
      const v = norm(leaf.textContent);
      if (v && v.length <= 32 && /\d/.test(v)) dateTexts.push(v);
    }
    rows.push(el);
    infos.push({ text, lines, dateTexts });
  }
  return { rows, infos };
}

/** 목록의 스크롤 컨테이너를 찾아 한 번 내린다. atBottom 은 내리기 "전" 상태. */
function scrollInPage(list, fraction) {
  const scrollable = (el) => {
    const style = window.getComputedStyle(el);
    return /(auto|scroll|overlay)/.test(style.overflowY) && el.scrollHeight > el.clientHeight + 1;
  };
  const candidates = [list, ...Array.from(list.querySelectorAll(':scope > *, :scope > * > *'))];
  for (let p = list.parentElement; p && p !== document.documentElement; p = p.parentElement) candidates.push(p);
  const scroller = candidates.find(scrollable);
  if (!scroller) return { scrollable: false, atBottom: true, moved: false };
  const before = scroller.scrollTop;
  const max = scroller.scrollHeight - scroller.clientHeight;
  const atBottom = before >= max - 2;
  if (!atBottom) {
    scroller.scrollTop = Math.min(max, before + Math.max(40, Math.floor(scroller.clientHeight * fraction)));
  }
  return { scrollable: true, atBottom, moved: scroller.scrollTop !== before };
}

/** 목록(또는 그 부모)에 진행 중 표시가 있는가. */
function busyInPage(list) {
  if (list.closest('[aria-busy="true"]') || list.querySelector('[aria-busy="true"]')) return true;
  const scope = list.parentElement || list;
  return Array.from(scope.querySelectorAll('[role="progressbar"]')).some((p) => {
    const style = window.getComputedStyle(p);
    return style.display !== 'none' && style.visibility !== 'hidden' && p.getClientRects().length > 0;
  });
}

/** 결과 제목 주변(조상 3단계)에서 메일 행 바깥의 보이는 글자가 패턴과 맞는가. */
function emptyTextInPage(heading, { source, flags }) {
  const re = new RegExp(source, flags);
  const rowSelector = '[role="listitem"], [role="option"], li';
  let scope = heading.parentElement;
  for (let depth = 0; scope && depth < 3; depth += 1, scope = scope.parentElement) {
    const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const parent = node.parentElement;
      if (!parent || parent.closest(rowSelector)) continue;
      const style = window.getComputedStyle(parent);
      if (style.display === 'none' || style.visibility === 'hidden') continue;
      if (parent.getClientRects().length === 0) continue;
      if (re.test(node.textContent || '')) return true;
    }
  }
  return false;
}

// ───────────────────────────── 목록 읽기 ─────────────────────────────

async function takeSnapshot(list, timeoutMs) {
  const handle = await list.evaluateHandle(snapshotInPage, ROW_SELECTOR, { timeout: timeoutMs });
  const infosHandle = await handle.getProperty('infos');
  const infos = await infosHandle.jsonValue();
  await infosHandle.dispose();
  return {
    infos,
    /** 스냅샷 시점의 행 요소. 이미 사라졌으면 null. 호출한 쪽이 dispose 한다. */
    async rowElement(index) {
      const rows = await handle.getProperty('rows');
      try {
        const item = await rows.getProperty(String(index));
        const element = item.asElement();
        if (!element) await item.dispose();
        return element;
      } finally {
        await rows.dispose();
      }
    },
    dispose: () => handle.dispose().catch(() => {}),
  };
}

function signatureOf(infos) {
  if (infos.length === 0) return '0';
  return `${infos.length}|${infos[0].text}|${infos[infos.length - 1].text}`;
}

async function listSignature(page, timeoutMs) {
  const list = messageList(page);
  if (!(await safeVisible(list))) return '';
  try {
    const snapshot = await takeSnapshot(list, Math.min(timeoutMs, 5000));
    const signature = signatureOf(snapshot.infos);
    await snapshot.dispose();
    return signature;
  } catch {
    return '';
  }
}

async function readResultState(page, timeoutMs) {
  const heading = await safeVisible(resultsHeading(page));
  const list = messageList(page);
  const listVisible = await safeVisible(list);
  let rowCount = 0;
  let signature = '';
  let busy = false;
  if (listVisible) {
    try {
      const snapshot = await takeSnapshot(list, Math.min(timeoutMs, 5000));
      rowCount = snapshot.infos.length;
      signature = signatureOf(snapshot.infos);
      await snapshot.dispose();
      busy = await list.evaluate(busyInPage, undefined, { timeout: 5000 });
    } catch {
      // 화면 전환 중이면 다음 확인에서 다시 읽는다.
    }
  }
  let empty = false;
  if (heading) {
    try {
      empty = await resultsHeading(page).evaluate(
        emptyTextInPage,
        { source: UI.emptyState.source, flags: UI.emptyState.flags },
        { timeout: 5000 },
      );
    } catch {
      empty = false;
    }
  }
  let error = false;
  try {
    error = await page.getByRole('alert').filter({ hasText: UI.errorAlert }).first().isVisible();
  } catch {
    error = false;
  }
  return { heading, listVisible, rowCount, signature, busy, empty, error };
}

/**
 * 검색이 "확정"될 때까지 기다린다. 결과 제목이 보이고, 진행 표시가 없고, (행이 있거나) 없음 문구가 보이는
 * 상태가 settleMs 동안 변하지 않아야 한다. 행도 없음 문구도 없으면 계속 기다린다(빈 결과로 추정하지 않는다).
 * baselineSignature 는 검색 전 목록의 지문으로, 검색 전 화면이 그대로 남아 있을 가능성에 대비해 더 오래 기다린다.
 * @returns {Promise<{kind: 'rows'|'empty'}>}
 */
export async function waitForResults(page, { timeoutMs, timing, signal, baselineSignature = '' }) {
  const deadline = Date.now() + timeoutMs;
  let stableSig = null;
  let stableSince = 0;
  for (;;) {
    throwIfAborted(signal);
    assertPageOpen(page);
    const state = await readResultState(page, timeoutMs);
    if (state.error && state.rowCount === 0) {
      throw runtimeError('outlookError', 'Outlook 이 검색 오류를 표시했습니다. 잠시 후 다시 실행하세요.');
    }
    let kind = null;
    if (state.heading && !state.busy) {
      if (state.rowCount > 0) kind = 'rows';
      else if (state.empty) kind = 'empty';
    }
    const now = Date.now();
    if (kind) {
      const sig = `${kind}|${state.signature}`;
      if (sig === stableSig) {
        const stale = kind === 'rows' && baselineSignature !== '' && state.signature === baselineSignature;
        const needed = stale ? timing.settleMs * 5 : timing.settleMs;
        if (now - stableSince >= needed) return { kind };
      } else {
        stableSig = sig;
        stableSince = now;
      }
    } else {
      stableSig = null;
    }
    if (now >= deadline) {
      throw runtimeError(
        'searchTimeout',
        `검색 결과가 확정되지 않았습니다(결과 제목=${state.heading ? '보임' : '없음'}, 목록=${state.listVisible ? '보임' : '없음'}, 행=${state.rowCount}, 없음 문구=${state.empty ? '보임' : '없음'}, 진행 중=${state.busy ? '예' : '아니오'}).`,
      );
    }
    await sleep(timing.pollMs, signal);
  }
}

// ───────────────────────────── 검색 범위 ─────────────────────────────

async function controlLabel(control) {
  return control.evaluate((el) => `${el.getAttribute('aria-label') || ''} ${el.innerText || el.value || ''}`, undefined, {
    timeout: 5000,
  });
}

/**
 * 검색 범위를 "모든 폴더"로 맞춘다.
 * @returns {Promise<'all'|'changed'|'unavailable'|'unverified'>}
 *  all: 이미 모든 폴더. changed: 바꿨고 확인함. unavailable: 범위 선택 UI가 없음. unverified: 바꿨지만 확인하지 못함.
 */
export async function ensureAllFoldersScope(page, { timeoutMs, timing, signal }) {
  const control = scopeControl(page);
  if (!(await safeVisible(control))) return 'unavailable';
  if (UI.scopeAll.test(await controlLabel(control))) return 'all';

  await control.click({ timeout: timeoutMs });
  const option = page
    .getByRole('menuitemradio', { name: UI.scopeAll })
    .or(page.getByRole('menuitem', { name: UI.scopeAll }))
    .or(page.getByRole('option', { name: UI.scopeAll }))
    .or(page.getByRole('radio', { name: UI.scopeAll }))
    .first();
  const shown = await waitFor(() => safeVisible(option), {
    timeoutMs: Math.min(timeoutMs, 5000),
    pollMs: timing.pollMs,
    signal,
  });
  if (!shown) {
    await page.keyboard.press('Escape').catch(() => {});
    return 'unverified';
  }
  await option.click({ timeout: timeoutMs });
  const confirmed = await waitFor(
    async () => {
      const current = scopeControl(page);
      return (await safeVisible(current)) && UI.scopeAll.test(await controlLabel(current));
    },
    { timeoutMs: Math.min(timeoutMs, 10_000), pollMs: timing.pollMs, signal },
  );
  return confirmed ? 'changed' : 'unverified';
}

// ───────────────────────────── 본문(읽기 창) ─────────────────────────────

const TIME_ONLY = /^(?:(?:오전|오후|am|pm)\s*)?\d{1,2}:\d{2}(?::\d{2})?(?:\s*(?:am|pm))?$/i;

/** 읽기 창 글이 이 행의 것인지: 행의 의미 있는 줄 중 절반 이상(최소 2줄, 줄이 적으면 전부)이 창 안에 있어야 한다. */
export function paneMatchesRow(paneText, rowLines, today) {
  const haystack = normalizeForMatch(paneText);
  if (!haystack) return false;
  const needles = (rowLines ?? [])
    .map(normalizeForMatch)
    .filter((line) => line.length >= 3 && !TIME_ONLY.test(line) && parseVisibleDate(line, { today }) === null);
  if (needles.length === 0) return true;
  const required = Math.min(needles.length, Math.max(2, Math.ceil(needles.length / 2)));
  return needles.filter((needle) => haystack.includes(needle)).length >= required;
}

function isDetachedError(error) {
  return /not attached|detached/i.test(String(error?.message ?? ''));
}

/**
 * 행을 눌러 읽기 창을 연 뒤 창의 보이는 글을 읽는다. 읽음 표시가 바뀌는 부작용이 있다.
 * @returns {Promise<{stale: true}|{body: string}>}
 */
async function readBodyForRow(page, snapshot, index, info, ctx) {
  const element = await snapshot.rowElement(index);
  if (!element) return { stale: true };
  try {
    try {
      await element.click({ timeout: ctx.timeoutMs });
    } catch (error) {
      if (isDetachedError(error)) return { stale: true };
      throw error;
    }
    const pane = readingPane(page);
    const deadline = Date.now() + ctx.timeoutMs;
    let paneEverVisible = false;
    let previous = null;
    let since = 0;
    for (;;) {
      throwIfAborted(ctx.signal);
      assertPageOpen(page);
      let text = null;
      if (await safeVisible(pane)) {
        paneEverVisible = true;
        text = await pane.innerText({ timeout: 5000 }).catch(() => null);
      }
      if (text && paneMatchesRow(text, info.lines, ctx.today)) {
        if (text === previous) {
          if (Date.now() - since >= ctx.timing.paneSettleMs) return { body: sanitizeBody(text) };
        } else {
          previous = text;
          since = Date.now();
        }
      } else {
        previous = null;
      }
      if (Date.now() >= deadline) {
        throw paneEverVisible
          ? runtimeError('bodyTimeout', '본문 읽기에 실패했습니다: 읽기 창이 선택한 메일의 내용으로 바뀌지 않았습니다.')
          : runtimeError('readingPaneMissing', '본문 읽기에 실패했습니다: 읽기 창(읽기 창/Reading pane)을 찾지 못했습니다.');
      }
      await sleep(ctx.timing.pollMs, ctx.signal);
    }
  } finally {
    await element.dispose().catch(() => {});
  }
}

// ───────────────────────────── 행 수집 ─────────────────────────────

/**
 * 보이는 행을 모으며 목록을 내려간다. 행 동일성은 보이는 텍스트로 본다(같은 글자의 행은 같은 행으로 접힌다).
 * @returns {Promise<{messages: object[], overflow: boolean, endConfirmed: boolean, outOfRange: number, seen: number}>}
 */
export async function collectRows(page, ctx) {
  const { maxResults, includeBody, receivedAfter, receivedBefore, today, timeoutMs, timing, limits, signal } = ctx;
  const maxRounds = limits.maxScrollRounds ?? defaultMaxScrollRounds(maxResults);
  const messages = [];
  const seen = new Set();
  const staleAttempts = new Map();
  let outOfRange = 0;
  let overflow = false;
  let endConfirmed = false;
  let idle = 0;

  for (let round = 0; ; round += 1) {
    throwIfAborted(signal);
    assertPageOpen(page);
    const list = messageList(page);
    if (!(await safeVisible(list))) {
      throw runtimeError('listGone', '메시지 목록이 화면에서 사라졌습니다(화면 상태가 바뀜).');
    }
    const snapshot = await takeSnapshot(list, timeoutMs);
    let added = 0;
    let restart = false;
    try {
      for (let index = 0; index < snapshot.infos.length; index += 1) {
        const info = snapshot.infos[index];
        if (seen.has(info.text)) continue;
        const rowDate = pickRowDate(info.dateTexts, { today });
        if (rowDate && !isDateInRange(rowDate, { after: receivedAfter, before: receivedBefore })) {
          seen.add(info.text);
          outOfRange += 1;
          added += 1;
          continue;
        }
        if (messages.length >= maxResults) {
          overflow = true;
          break;
        }
        const message = { text: sanitizeLine(info.text) };
        if (includeBody) {
          const outcome = await readBodyForRow(page, snapshot, index, info, ctx);
          if ('stale' in outcome) {
            const attempts = (staleAttempts.get(info.text) ?? 0) + 1;
            staleAttempts.set(info.text, attempts);
            if (attempts > 3) {
              throw runtimeError('bodyTimeout', '본문 읽기에 실패했습니다: 목록이 계속 다시 그려져 메일을 선택하지 못했습니다.');
            }
            restart = true;
            break;
          }
          message.body = outcome.body;
        }
        messages.push(message);
        seen.add(info.text);
        added += 1;
      }
    } finally {
      await snapshot.dispose();
    }
    if (overflow) break;
    if (restart) continue;

    const scroll = await list.evaluate(scrollInPage, 0.85, { timeout: timeoutMs });
    const busy = await list.evaluate(busyInPage, undefined, { timeout: timeoutMs }).catch(() => true);
    if (added === 0 && scroll.atBottom && !busy) idle += 1;
    else idle = 0;
    if (idle >= timing.endIdleRounds) {
      endConfirmed = true;
      break;
    }
    if (round + 1 >= maxRounds) break;
    await sleep(timing.scrollSettleMs, signal);
  }
  return { messages, overflow, endConfirmed, outOfRange, seen: seen.size };
}

// ───────────────────────────── 검색 실행 ─────────────────────────────

/**
 * 메일함에서 검색한다. 계정 확인은 호출한 쪽에서 끝낸 뒤여야 한다(이 함수는 메일 목록을 읽는다).
 * @returns {Promise<{messages: object[], returned: number, truncated: boolean, incompleteReason: string|null}>}
 */
export async function searchMailbox(page, ctx) {
  const { expression, timeoutMs, timing, signal, limits } = ctx;
  const box = searchBox(page);
  await box.click({ timeout: timeoutMs });
  await box.fill(expression, { timeout: timeoutMs });

  let scope = await ensureAllFoldersScope(page, { timeoutMs, timing, signal });
  if ((await box.inputValue({ timeout: timeoutMs })) !== expression) {
    throw runtimeError('searchBoxMismatch', '검색창이 입력한 검색식을 그대로 받지 않았습니다(길이 제한 등). 검색을 중단했습니다.');
  }

  const baselineSignature = await listSignature(page, timeoutMs);
  await box.press('Enter', { timeout: timeoutMs });
  let settled = await waitForResults(page, { timeoutMs, timing, signal, baselineSignature });

  if (scope === 'unavailable') {
    // 범위 선택이 결과 화면에서야 나타나는 화면 구성일 수 있다.
    scope = await ensureAllFoldersScope(page, { timeoutMs, timing, signal });
    if (scope === 'changed') {
      settled = await waitForResults(page, { timeoutMs, timing, signal, baselineSignature: '' });
    }
  }
  const scopeVerified = scope === 'all' || scope === 'changed';

  const reasons = [];
  let messages = [];
  if (settled.kind === 'rows') {
    const collected = await collectRows(page, ctx);
    messages = collected.messages;
    if (collected.overflow) reasons.push('maxResults');
    if (collected.seen >= limits.serverResultLimit) reasons.push('serverLimit');
    if (!collected.overflow && !collected.endConfirmed) reasons.push('endNotConfirmed');
    if (collected.outOfRange > 0) reasons.push('dateBoundaryMismatch');
    ctx.log?.(
      `수집 완료: ${messages.length}건` + (collected.outOfRange > 0 ? ` (받은 날짜가 범위 밖이라 제외한 행 ${collected.outOfRange}건)` : ''),
    );
  }
  if (!scopeVerified) reasons.push('scopeUnverified');

  return {
    messages,
    returned: messages.length,
    truncated: reasons.length > 0,
    incompleteReason: reasons.length > 0 ? reasons.join(',') : null,
  };
}
