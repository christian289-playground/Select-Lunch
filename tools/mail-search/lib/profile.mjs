// 도구 전용 Chromium 프로필 위치와 잠금 확인.
// 프로필에는 로그인 상태와 브라우저 캐시가 남는다. 기존 개인 Chrome/Edge/Aside 프로필은 쓰지 않는다.

import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { usageError } from './errors.mjs';

/** 운영체제별 사용자 상태 폴더. */
export function stateRoot({ platform = process.platform, env = process.env, homedir = os.homedir() } = {}) {
  const p = platform === 'win32' ? path.win32 : path.posix;
  if (platform === 'win32') return env.LOCALAPPDATA || p.join(homedir, 'AppData', 'Local');
  if (platform === 'darwin') return p.join(homedir, 'Library', 'Application Support');
  if (env.XDG_STATE_HOME && p.isAbsolute(env.XDG_STATE_HOME)) return env.XDG_STATE_HOME;
  return p.join(homedir, '.local', 'state');
}

/** <상태 폴더>/SelectLunch/mail-search/chromium-<sha256(소문자 계정) 앞 16자>. 저장소 밖이다. */
export function defaultProfileDirectory(account, opts = {}) {
  const platform = opts.platform ?? process.platform;
  const p = platform === 'win32' ? path.win32 : path.posix;
  const hash = createHash('sha256').update(String(account).trim().toLowerCase(), 'utf8').digest('hex').slice(0, 16);
  return p.join(stateRoot({ ...opts, platform }), 'SelectLunch', 'mail-search', `chromium-${hash}`);
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
 * @param {{account: string, profileDirectory?: string|null, forbiddenRoots?: string[]}} input
 */
export function resolveProfileDirectory({ account, profileDirectory = null, forbiddenRoots = [], platformOptions = {} }) {
  if (profileDirectory === null || profileDirectory === undefined) {
    return defaultProfileDirectory(account, platformOptions);
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
 * 다른 Chromium 이 이 프로필을 쓰고 있는지 확인한다.
 * - Windows: Chromium 이 `lockfile` 을 독점으로 열어 두므로 읽기/쓰기로 열어 본다.
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
