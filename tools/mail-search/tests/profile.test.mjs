import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { checkProfileLock, defaultProfileDirectory, resolveProfileDirectory } from '../lib/profile.mjs';

const hash = (account) => createHash('sha256').update(account, 'utf8').digest('hex').slice(0, 16);

test('기본 프로필: 운영체제별 상태 폴더 + SelectLunch/mail-search/chromium-<sha256 앞 16자>', () => {
  const account = 'alice@example.invalid';
  assert.equal(
    defaultProfileDirectory(account, { platform: 'win32', env: { LOCALAPPDATA: 'C:\\Users\\u\\AppData\\Local' }, homedir: 'C:\\Users\\u' }),
    `C:\\Users\\u\\AppData\\Local\\SelectLunch\\mail-search\\chromium-${hash(account)}`,
  );
  assert.equal(
    defaultProfileDirectory(account, { platform: 'win32', env: {}, homedir: 'C:\\Users\\u' }),
    `C:\\Users\\u\\AppData\\Local\\SelectLunch\\mail-search\\chromium-${hash(account)}`,
  );
  assert.equal(
    defaultProfileDirectory(account, { platform: 'linux', env: { XDG_STATE_HOME: '/srv/state' }, homedir: '/home/u' }),
    `/srv/state/SelectLunch/mail-search/chromium-${hash(account)}`,
  );
  assert.equal(
    defaultProfileDirectory(account, { platform: 'linux', env: {}, homedir: '/home/u' }),
    `/home/u/.local/state/SelectLunch/mail-search/chromium-${hash(account)}`,
  );
  assert.equal(
    defaultProfileDirectory(account, { platform: 'linux', env: { XDG_STATE_HOME: 'relative/state' }, homedir: '/home/u' }),
    `/home/u/.local/state/SelectLunch/mail-search/chromium-${hash(account)}`,
  );
  assert.equal(
    defaultProfileDirectory(account, { platform: 'darwin', env: {}, homedir: '/Users/u' }),
    `/Users/u/Library/Application Support/SelectLunch/mail-search/chromium-${hash(account)}`,
  );
});

test('기본 프로필은 계정마다 다르고 대소문자·공백과 무관하다', () => {
  const opts = { platform: 'linux', env: {}, homedir: '/home/u' };
  assert.equal(
    defaultProfileDirectory(' Alice@Example.INVALID ', opts),
    defaultProfileDirectory('alice@example.invalid', opts),
  );
  assert.notEqual(defaultProfileDirectory('a@example.invalid', opts), defaultProfileDirectory('b@example.invalid', opts));
});

test('명시한 프로필은 절대 경로만, 저장소 안·개인 브라우저 프로필은 거부', () => {
  const account = 'alice@example.invalid';
  const ok = path.join(os.tmpdir(), 'select-lunch-profile-test');
  assert.equal(resolveProfileDirectory({ account, profileDirectory: ok }), path.resolve(ok));

  const usage = (error) => {
    assert.equal(error.exitCode, 2);
    assert.equal(error.code, 'profilePath');
    return true;
  };
  assert.throws(() => resolveProfileDirectory({ account, profileDirectory: 'relative/profile' }), usage);

  const repo = path.join(os.tmpdir(), 'fake-repo');
  assert.throws(
    () => resolveProfileDirectory({ account, profileDirectory: path.join(repo, 'tools', 'profile'), forbiddenRoots: [repo] }),
    usage,
  );
  assert.throws(() => resolveProfileDirectory({ account, profileDirectory: repo, forbiddenRoots: [repo] }), usage);

  for (const personal of [
    path.join(os.tmpdir(), 'Google', 'Chrome', 'User Data'),
    path.join(os.tmpdir(), 'Microsoft', 'Edge', 'User Data', 'Default'),
    path.join(os.tmpdir(), '.config', 'google-chrome'),
    path.join(os.tmpdir(), '.config', 'chromium'),
    path.join(os.tmpdir(), 'AppData', 'Local', 'Aside'),
  ]) {
    assert.throws(() => resolveProfileDirectory({ account, profileDirectory: personal }), usage, personal);
  }
  // 도구 전용 이름(chromium-<hash>)은 개인 프로필로 오인하지 않는다.
  const dedicated = path.join(os.tmpdir(), 'SelectLunch', 'mail-search', 'chromium-vincent-test');
  assert.equal(resolveProfileDirectory({ account, profileDirectory: dedicated }), path.resolve(dedicated));
});

test('프로필 지정이 없으면 기본 경로', () => {
  const platformOptions = { platform: 'linux', env: {}, homedir: '/home/u' };
  assert.equal(
    resolveProfileDirectory({ account: 'alice@example.invalid', profileDirectory: null, platformOptions }),
    defaultProfileDirectory('alice@example.invalid', platformOptions),
  );
});

test('잠금: 폴더가 없거나 비어 있으면 잠겨 있지 않다', async () => {
  assert.deepEqual(await checkProfileLock(path.join(os.tmpdir(), 'mail-search-no-such-profile-dir')), { locked: false });
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'mail-search-lock-'));
  try {
    assert.deepEqual(await checkProfileLock(dir), { locked: false });
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
});

test('잠금(POSIX): SingletonLock 이 살아 있는 프로세스를 가리키면 잠김, 죽은 프로세스·낡은 링크는 아님', { skip: process.platform === 'win32' }, async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'mail-search-lock-'));
  const link = path.join(dir, 'SingletonLock');
  try {
    await fs.symlink(`${os.hostname()}-${process.pid}`, link);
    assert.deepEqual(await checkProfileLock(dir), { locked: true });

    await fs.rm(link);
    await fs.symlink(`${os.hostname()}-2147483646`, link);
    assert.deepEqual(await checkProfileLock(dir), { locked: false });

    await fs.rm(link);
    await fs.symlink(`other-host-${process.pid}`, link);
    assert.deepEqual(await checkProfileLock(dir), { locked: true });
  } finally {
    await fs.rm(dir, { recursive: true, force: true });
  }
});
