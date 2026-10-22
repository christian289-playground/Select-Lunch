// Playwright 지속 Chromium 컨텍스트 수명 관리.
// - 도구 전용 프로필(저장소 밖)로 같은 Chromium(channel:'chromium')을 headed/headless 양쪽에 쓴다.
// - chromiumSandbox:true 고정. 보안을 끄는 인수나 스텔스 설정은 넣지 않는다.
// - HAR·trace·video·screenshot·다운로드는 켜지 않는다.
// - SIGINT 등은 우리가 처리한다(handleSIGINT:false). 중단 신호가 오면 컨텍스트를 닫아 대기 중인 호출을 깨운다.

import fs from 'node:fs/promises';
import { EXIT, MailSearchError, runtimeError } from './errors.mjs';
import { settleWithin } from './async.mjs';
import { checkProfileLock } from './profile.mjs';

export const VIEWPORT = Object.freeze({ width: 1440, height: 900 });
const CLOSE_GRACE_MS = 15_000;

const PROFILE_IN_USE = '이 프로필을 다른 Chromium 이 쓰고 있습니다. 해당 브라우저 창을 닫은 뒤 다시 실행하세요.';

/** 시작 실패 원인을 분류한다. 예외 원문은 출력하지 않는다. */
export function classifyLaunchError(error) {
  const text = typeof error?.message === 'string' ? error.message : '';
  if (/error while loading shared libraries|Host system is missing dependencies|install-deps/i.test(text)) {
    return new MailSearchError(EXIT.USAGE, 'browserDeps', 'Chromium 실행에 필요한 시스템 라이브러리가 없습니다(Linux: `npx playwright install-deps chromium`).');
  }
  if (/Executable doesn't exist/i.test(text)) {
    return new MailSearchError(EXIT.USAGE, 'browserMissing', 'Chromium 이 설치되어 있지 않습니다. tools/mail-search 에서 `npx playwright install chromium` 을 실행하세요.');
  }
  if (/Missing X server|\$DISPLAY|without having a XServer|no display/i.test(text)) {
    return new MailSearchError(EXIT.USAGE, 'noDisplay', '화면 표시 장치가 없어 창을 열 수 없습니다. 검색은 headless 로, 로그인은 원격 데스크톱/Xvfb 같은 화면이 있는 환경에서 실행하세요.');
  }
  if (/sandbox|user namespace/i.test(text)) {
    return runtimeError('sandbox', 'Chromium 샌드박스를 시작하지 못했습니다. 보안 설정을 끄지 않으므로 컨테이너/권한 환경을 확인하세요.');
  }
  return runtimeError('launchFailed', 'Chromium 을 시작하지 못했습니다.');
}

/**
 * @param {{profileDir: string, headless: boolean, viewport?: {width:number,height:number}|null,
 *          signal?: AbortSignal, timeoutMs?: number}} options
 * @returns {Promise<{context: import('playwright').BrowserContext, page: import('playwright').Page, close: () => Promise<void>}>}
 */
export async function openSession({ profileDir, headless, viewport = VIEWPORT, signal, timeoutMs = 60_000 }) {
  let chromium;
  try {
    ({ chromium } = await import('playwright'));
  } catch {
    throw new MailSearchError(EXIT.USAGE, 'playwrightMissing', 'playwright 패키지를 불러오지 못했습니다. tools/mail-search 에서 `npm ci` 를 실행하세요.');
  }

  await fs.mkdir(profileDir, { recursive: true, mode: 0o700 });
  if ((await checkProfileLock(profileDir)).locked) {
    throw runtimeError('profileInUse', PROFILE_IN_USE);
  }

  let context;
  try {
    context = await chromium.launchPersistentContext(profileDir, {
      channel: 'chromium',
      headless,
      chromiumSandbox: true,
      viewport,
      acceptDownloads: false,
      handleSIGINT: false,
      handleSIGTERM: false,
      handleSIGHUP: false,
      timeout: timeoutMs,
    });
  } catch (error) {
    if ((await checkProfileLock(profileDir)).locked) throw runtimeError('profileInUse', PROFILE_IN_USE);
    throw classifyLaunchError(error);
  }

  context.setDefaultTimeout(timeoutMs);
  context.setDefaultNavigationTimeout(timeoutMs);

  const onAbort = () => {
    context.close().catch(() => {});
  };
  signal?.addEventListener('abort', onAbort, { once: true });
  let closed = false;

  const close = async () => {
    if (closed) return;
    closed = true;
    signal?.removeEventListener('abort', onAbort);
    await settleWithin(context.close().catch(() => {}), CLOSE_GRACE_MS);
  };

  try {
    const page = context.pages()[0] ?? (await context.newPage());
    return { context, page, close };
  } catch (error) {
    await close();
    throw classifyLaunchError(error);
  }
}
