// 도구 전용 브라우저 프로필 위치와 잠금 확인.
// 프로필에는 로그인 상태와 브라우저 캐시가 남는다. 기존 개인 Chrome/Edge/Aside 프로필은 쓰지 않는다.

import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { usageError } from './errors.mjs';

/** 선택할 수 있는 브라우저 채널. chromium = Playwright 가 관리하는 Chromium, chrome = 시스템에 설치된 Google Chrome. */
export const BROWSER_CHANNELS = Object.freeze(['chromium', 'chrome']);
export const DEFAULT_BROWSER_CHANNEL = 'chromium';

/**
 * 채널 값을 검증한다. 없으면(undefined/null) chromium. 허용 목록 밖의 값은 입력값을 되풀이하지 않고 종료 코드 2 로 거부한다.
 * @param {unknown} value
 * @returns {'chromium'|'chrome'}
 */
export function normalizeBrowserChannel(value) {
  if (value === undefined || value === null) return DEFAULT_BROWSER_CHANNEL;
  if (typeof value !== 'string' || !BROWSER_CHANNELS.includes(value)) {
    throw usageError('badInput', "입력 오류: 'browserChannel' 는 chromium 또는 chrome 중 하나여야 합니다.");
  }
  return value;
}

/** 운영체제별 사용자 상태 폴더. */
export function stateRoot({ platform = process.platform, env = process.env, homedir = os.homedir() } = {}) {
  const p = platform === 'win32' ? path.win32 : path.posix;
  if (platform === 'win32') return env.LOCALAPPDATA || p.join(homedir, 'AppData', 'Local');
  if (platform === 'darwin') return p.join(homedir, 'Library', 'Application Support');
  if (env.XDG_STATE_HOME && p.isAbsolute(env.XDG_STATE_HOME)) return env.XDG_STATE_HOME;
  return p.join(homedir, '.local', 'state');
}

/**
 * <상태 폴더>/SelectLunch/mail-search/<browserChannel>-<sha256(소문자 계정) 앞 16자>. 저장소 밖이다.
 * browserChannel 기본값 chromium 의 경로는 채널을 구분하기 전과 같다(기존 로그인 프로필 유지).
 */
export function defaultProfileDirectory(account, opts = {}) {
  const platform = opts.platform ?? process.platform;
  const channel = normalizeBrowserChannel(opts.browserChannel);
  const p = platform === 'win32' ? path.win32 : path.posix;
  const hash = createHash('sha256').update(String(account).trim().toLowerCase(), 'utf8').digest('hex').slice(0, 16);
  return p.join(stateRoot({ ...opts, platform }), 'SelectLunch', 'mail-search', `${channel}-${hash}`);
}

// 개인 브라우저 프로필로 보이는 경로 조각(폴더 이름 단위 비교).
const PERSONAL_PROFILE_PATTERNS = [
  /(^|\/)google\/chrome[^/]*(\/|$)/,
  /(^|\/)google-chrome[^/]*(\/|$)/,
  /(^|\/)microsoft\/edge[^/]*(\/|$)/,
  /(^|\/)microsoft-edge[^/]*(\/|$)/,
  /(^|\/)user data(\/|$)/,
  /(^|\/)(aside|chromium|vivaldi|bravesoftware|brave-browser)(\/|$)/,
];

function isInside(parent, child) {
  const rel = path.relative(parent, child);
  return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

/**
 * 프로필 폴더를 정한다. 명시 경로는 절대 경로여야 하고, 저장소 안이나 개인 브라우저 프로필이면 거부한다.
 * 명시 경로가 없으면 계정·채널별 기본 경로. 채널은 명시 경로에도 검증한다(잘못된 채널은 파일을 만들기 전에 거부).
 * @param {{account: string, profileDirectory?: string|null, browserChannel?: 'chromium'|'chrome', forbiddenRoots?: string[],
 *          platformOptions?: object}} input
 */
export function resolveProfileDirectory({
  account,
  profileDirectory = null,
  browserChannel = DEFAULT_BROWSER_CHANNEL,
  forbiddenRoots = [],
  platformOptions = {},
}) {
  const channel = normalizeBrowserChannel(browserChannel);
  if (profileDirectory === null || profileDirectory === undefined) {
    return defaultProfileDirectory(account, { ...platformOptions, browserChannel: channel });
  }
  if (!path.isAbsolute(profileDirectory)) {
    throw usageError('profilePath', '입력 오류: profileDirectory 는 절대 경로여야 합니다.');
  }
  const resolved = path.resolve(profileDirectory);
  for (const root of forbiddenRoots) {
    if (isInside(path.resolve(root), resolved)) {
      throw usageError('profilePath', '입력 오류: 프로필 폴더는 저장소 밖이어야 합니다(로그인 상태·캐시가 저장됩니다).');
    }
  }
  const normalized = resolved.replace(/\\/g, '/').toLowerCase();
  if (PERSONAL_PROFILE_PATTERNS.some((re) => re.test(normalized))) {
    throw usageError('profilePath', '입력 오류: 기존 Chrome/Edge/Aside 등 개인 브라우저 프로필은 쓸 수 없습니다. 도구 전용 폴더를 지정하세요.');
  }
  return resolved;
}

/**
 * 다른 브라우저(Chromium 또는 Chrome)가 이 프로필을 쓰고 있는지 확인한다.
 * - Windows: 브라우저가 `lockfile` 을 독점으로 열어 두므로 읽기/쓰기로 열어 본다.
 * - Linux/macOS: `SingletonLock` 심볼릭 링크의 대상 `<호스트>-<pid>` 의 프로세스가 살아 있는지 본다.
 * @returns {Promise<{locked: boolean}>}
 */
export async function checkProfileLock(dir, { platform = process.platform } = {}) {
  if (platform === 'win32') {
    try {
      const handle = await fs.open(path.join(dir, 'lockfile'), 'r+');
      await handle.close();
      return { locked: false };
    } catch (error) {
      if (['EBUSY', 'EPERM', 'EACCES'].includes(error?.code)) return { locked: true };
      return { locked: false };
    }
  }
  let target;
  try {
    target = await fs.readlink(path.join(dir, 'SingletonLock'));
  } catch {
    return { locked: false };
  }
  const match = /^(.*)-(\d+)$/.exec(target);
  if (!match) return { locked: true };
  if (match[1] !== os.hostname()) return { locked: true };
  try {
    process.kill(Number(match[2]), 0);
    return { locked: true };
  } catch (error) {
    return { locked: error?.code === 'EPERM' };
  }
}
