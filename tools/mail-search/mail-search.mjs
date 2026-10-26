#!/usr/bin/env node
// Outlook 웹 메일 검색(읽기 전용) — Chromium 또는 설치된 Google Chrome + Playwright 화면 제어 엔진. pwsh 진입점(Search-OutlookMail.ps1)이 호출한다.
//
// ── 계약 ─────────────────────────────────────────────────────────────────────
// stdin : JSON 객체 하나(UTF-8). 키는 아래뿐이고 모르는 키·잘못된 값은 종료 코드 2.
//   account(기본 vincent@snuailab.ai) query[] names[] receivedAfter receivedBefore(YYYY-MM-DD|null, before 는 미포함)
//   maxResults(1..1000, 기본 200) includeBody login headless profileDirectory(절대 경로|null)
//   browserChannel(chromium|chrome, 기본 chromium; 그 밖의 값은 브라우저·파일을 만들기 전에 종료 코드 2)
//   timeoutSeconds(기본 60, 화면 대기 한 단계의 상한) loginTimeoutSeconds(기본 600). login 과 headless 는 함께 쓸 수 없다.
//   창이 보이는 것은 login 뿐이다. 검색은 항상 headless 로 실행하며, headless 키는 받아 주지만 검색의 창 표시 여부를 바꾸지 못한다.
//   chromium = Playwright 가 관리하는 Chromium. chrome = 시스템에 설치된 Google Chrome(stable). chrome 이 없으면 내려받거나
//   chromium 으로 바꿔 실행하지 않고 종료 코드 2 로 끝난다. 두 채널 모두 브라우저 샌드박스를 끄지 않는다.
//   query 가 없거나 비면 기본 검색어(휴가,연차,반차,반반차,시간차,대체휴가,경조휴가)를 쓴다.
//   검색어는 서로 OR, 이름은 서로 OR, 검색어 묶음과 이름 묶음은 AND. 검색식 연산자(AND/OR/NOT/NEAR)는 거부한다.
// stdout: 성공일 때만 JSON 객체 한 줄(ASCII; 한글은 \uXXXX 이스케이프라 콘솔 코드 페이지와 무관). 실패 땐 비어 있다.
//   검색: {mode:'search', account, searchExpression, result:{returned, truncated, incompleteReason}, messages:[{text, body?}]}
//   로그인: {mode:'login', account, status:'ready'}
// stderr: 한국어 안전 진단(진행 안내·오류 사유). 화면/DOM/예외 원문·메일 내용은 싣지 않는다.
// 종료 코드: 0 성공 / 1 실행·화면 오류(중단 포함) / 2 인자·의존성 / 3 로그인 필요·계정 불일치 / 4 수동 로그인 시간 초과
//
// ── 프로필 ───────────────────────────────────────────────────────────────────
// 계정·채널마다 전용 브라우저 프로필(저장소 밖). 기존 개인 Chrome/Edge/Aside 프로필은 쓰지 않는다.
//   기본 경로 = <상태 폴더>/SelectLunch/mail-search/<browserChannel>-<sha256(소문자 계정) 앞 16자>
//   chromium 의 경로(chromium-<해시>)는 채널 구분이 생기기 전과 같아 기존 로그인 프로필을 그대로 쓴다. chrome 은 chrome-<해시> 로
//   따로라서 채널마다 한 번씩 -Login 이 필요하다.
//   상태 폴더: Windows %LOCALAPPDATA% / macOS ~/Library/Application Support / Linux $XDG_STATE_HOME 또는 ~/.local/state
//   profileDirectory 를 주면 그 절대 경로를 쓴다(저장소 안·개인 브라우저 프로필 경로는 거부). 이때는 채널마다 다른 폴더를 줘야 한다:
//   Chromium 과 Chrome 은 버전이 달라 같은 프로필 폴더를 공유하면 안 된다(직접 지정하면 실행 때마다 경고를 낸다).
//   프로필에는 로그인 상태와 브라우저 캐시가 남는다. 결과·본문은 어디에도 쓰지 않는다(메모리+stdout만).
//   같은 프로필을 다른 브라우저가 쓰고 있으면 종료 코드 1.
//
// ── result.incompleteReason (truncated === true 일 때만 값이 있고, 여러 개면 쉼표로 이어 붙인다) ─────────────
//   maxResults        maxResults 를 넘는 결과가 더 있다(상한에서 멈춤).
//   serverLimit       Outlook 웹 검색이 처음 1,000건만 돌려주므로 더 있을 수 있다.
//   endNotConfirmed   목록 맨 아래에서 새 행이 없음을 확인하지 못했다(스크롤 횟수 상한 등). 더 있을 수 있다.
//   scopeUnverified   검색 범위가 "모든 폴더"임을 화면에서 확인·설정하지 못했다. 다른 폴더의 메일이 빠졌을 수 있다.
//   dateBoundaryMismatch  목록에 보이는 받은 날짜가 요청 범위 밖인 행이 나와 그 행을 뺐다. 서버의 날짜 경계 해석이
//                     요청과 달라 경계 쪽 메일이 빠졌을 수 있다.
//   결과 없음은 화면에 "없음" 문구가 확인될 때만 returned 0 / truncated false 로 낸다.
//
// ── includeBody ──────────────────────────────────────────────────────────────
// 각 행을 눌러 읽기 창의 보이는 글(머리글+본문)을 body 로 담는다. 메일이 "읽음"으로 바뀌는 부작용이 있다.
// 읽기 창을 못 찾거나 선택한 메일의 내용으로 바뀌지 않으면 조용히 건너뛰지 않고 종료 코드 1로 끝낸다.
// body 가 빈 문자열이면 읽기 창은 읽었지만 글이 없었다는 뜻이다.

import { realpathSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { openSession, VIEWPORT } from './lib/browser-session.mjs';
import { throwIfAborted } from './lib/async.mjs';
import { EXIT, toMailSearchError, usageError } from './lib/errors.mjs';
import {
  OUTLOOK_MAIL_URL,
  accountMismatchError,
  accountUnverifiedError,
  gotoMail,
  loginRequiredError,
  unrecognizedUiError,
  verifyAccountWhenReady,
  waitForLoggedInMailbox,
  waitForMailboxState,
} from './lib/outlook-ui.mjs';
import { searchMailbox } from './lib/outlook-search.mjs';
import { resolveProfileDirectory } from './lib/profile.mjs';
import { parseRequest, readJsonObject } from './lib/request.mjs';
import { buildSearchExpression } from './lib/search-expression.mjs';
import { toAsciiJson } from './lib/text.mjs';
import { DEFAULT_LIMITS, DEFAULT_TIMING } from './lib/timing.mjs';
import { localTodayIso } from './lib/visible-date.mjs';

const MODULE_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(MODULE_DIR, '..', '..');

async function runLogin(session, request, ctx) {
  const { page } = session;
  const { log, mailUrl, timeoutMs, loginTimeoutMs, timing, signal } = ctx;
  log(`브라우저가 열렸습니다. 열린 창에서 ${request.account} 계정으로 직접 로그인(다중 인증·동의 포함)하세요. 최대 ${Math.round(loginTimeoutMs / 1000)}초 기다립니다.`);
  log('"로그인 유지" 질문이 나오면 "예"를 선택해야 다음 실행에도 로그인이 유지됩니다(인증 상태는 전용 브라우저 프로필에 저장됩니다).');
  await page.bringToFront().catch(() => {});
  await gotoMail(page, mailUrl, timeoutMs);
  await waitForLoggedInMailbox(page, request.account, { loginTimeoutMs, timeoutMs, timing, signal, log });
  log('로그인과 계정 확인이 끝났습니다.');
  return { mode: 'login', account: request.account, status: 'ready' };
}

async function runSearch(session, request, ctx) {
  const { page } = session;
  const { log, mailUrl, timeoutMs, timing, limits, signal, expression, today } = ctx;
  await page.bringToFront().catch(() => {});
  await gotoMail(page, mailUrl, timeoutMs);

  const state = await waitForMailboxState(page, { timeoutMs, timing, signal });
  if (state === 'login') throw loginRequiredError();
  if (state === 'unrecognized') throw unrecognizedUiError();

  // 계정 게이트: 화면(폴더 트리·계정 메뉴)에서 계정을 확인하기 전에는 메일 목록을 읽지 않는다.
  const account = await verifyAccountWhenReady(page, request.account, { timeoutMs, timing, signal });
  if (account.status === 'mismatch') throw accountMismatchError(account.detected);
  if (account.status !== 'verified') throw accountUnverifiedError();
  log('로그인 계정을 확인했습니다. 검색합니다.');

  const found = await searchMailbox(page, {
    expression,
    maxResults: request.maxResults,
    includeBody: request.includeBody,
    receivedAfter: request.receivedAfter,
    receivedBefore: request.receivedBefore,
    today,
    timeoutMs,
    timing,
    limits,
    signal,
    log,
  });
  return {
    mode: 'search',
    account: request.account,
    searchExpression: expression,
    result: { returned: found.returned, truncated: found.truncated, incompleteReason: found.incompleteReason },
    messages: found.messages,
  };
}

/**
 * 요청 하나를 실행한다. 브라우저는 항상 finally 에서 닫는다.
 * @param {ReturnType<typeof parseRequest>} request
 * @param {{signal?: AbortSignal, log?: (line: string) => void, launch?: Function, mailUrl?: string,
 *          now?: () => Date, timing?: object, limits?: object}} [deps] 테스트에서 launch·timing·mailUrl 을 바꾼다.
 */
export async function runMailSearch(request, deps = {}) {
  const { signal, log = () => {}, launch = openSession, mailUrl = OUTLOOK_MAIL_URL, now = () => new Date() } = deps;
  const timing = { ...DEFAULT_TIMING, ...deps.timing };
  const limits = { ...DEFAULT_LIMITS, ...deps.limits };
  const timeoutMs = Math.round(request.timeoutSeconds * 1000);
  const loginTimeoutMs = Math.round(request.loginTimeoutSeconds * 1000);

  let session = null;
  try {
    throwIfAborted(signal);
    // 입력 검증은 브라우저를 띄우기 전에 끝낸다.
    const expression = request.login ? null : buildSearchExpression(request);
    const profileDir = resolveProfileDirectory({
      account: request.account,
      profileDirectory: request.profileDirectory,
      browserChannel: request.browserChannel,
      forbiddenRoots: [REPO_ROOT],
    });

    log(request.login ? '로그인용 브라우저를 시작합니다.' : `브라우저(${request.browserChannel})를 headless 로 시작합니다.`);
    if (request.profileDirectory != null) {
      log('경고: 직접 지정한 프로필 폴더는 browserChannel(chromium/chrome)마다 따로 써야 합니다. 다른 채널과 같은 폴더를 공유하지 마세요.');
    }
    session = await launch({
      profileDir,
      headless: !request.login,
      browserChannel: request.browserChannel,
      viewport: request.login ? null : VIEWPORT,
      signal,
      timeoutMs,
    });

    const ctx = { log, mailUrl, timeoutMs, loginTimeoutMs, timing, limits, signal, expression, today: localTodayIso(now()) };
    return request.login ? await runLogin(session, request, ctx) : await runSearch(session, request, ctx);
  } catch (error) {
    throw toMailSearchError(error, signal);
  } finally {
    if (session) await session.close();
  }
}

function writeAll(stream, text) {
  return new Promise((resolve) => {
    stream.write(text, () => resolve());
  });
}

/**
 * stdin → 실행 → stdout/stderr/종료 코드. 종료 코드를 돌려줄 뿐 프로세스를 끝내지 않는다(테스트 가능).
 * 실패해도 stdout 에는 아무것도 쓰지 않는다.
 */
export async function runCli({
  stdin = process.stdin,
  stdout = process.stdout,
  stderr = process.stderr,
  runner = runMailSearch,
  signalTarget = process,
} = {}) {
  const controller = new AbortController();
  const onSignal = () => controller.abort();
  const signals = ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK'];
  for (const name of signals) signalTarget.on(name, onSignal);
  try {
    if (stdin.isTTY) {
      throw usageError('noInput', '입력 오류: stdin 으로 JSON 객체 하나를 전달해야 합니다(파이프 또는 리디렉션).');
    }
    const request = parseRequest(await readJsonObject(stdin));
    const output = await runner(request, {
      signal: controller.signal,
      log: (line) => {
        stderr.write(`${line}\n`);
      },
    });
    await writeAll(stdout, `${toAsciiJson(output)}\n`);
    return EXIT.OK;
  } catch (error) {
    const failure = toMailSearchError(error, controller.signal);
    await writeAll(stderr, `오류: ${failure.message}\n`);
    return failure.exitCode;
  } finally {
    for (const name of signals) signalTarget.off(name, onSignal);
  }
}

function isEntryPoint() {
  if (!process.argv[1]) return false;
  try {
    return realpathSync.native(fileURLToPath(import.meta.url)) === realpathSync.native(path.resolve(process.argv[1]));
  } catch {
    return false;
  }
}

if (isEntryPoint()) {
  // 처리하지 못한 예외의 원문(스택·화면 조각)이 stderr 로 새지 않게 한다.
  const crash = () => {
    process.stderr.write('오류: 예기치 않은 오류로 중단했습니다. 오류 상세는 안전을 위해 출력하지 않습니다.\n', () => process.exit(EXIT.RUNTIME));
  };
  process.on('unhandledRejection', crash);
  process.on('uncaughtException', crash);
  process.exit(await runCli());
}
