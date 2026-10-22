// 실제 Chromium 을 임시 프로필로 띄워 수명·잠금을 확인한다(외부 통신 없음).

import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { classifyLaunchError, openSession } from '../lib/browser-session.mjs';
import { checkProfileLock } from '../lib/profile.mjs';

test('같은 프로필을 쓰는 Chromium 이 떠 있으면 잠김이고 두 번째 실행은 안전하게 실패한다', { timeout: 120_000 }, async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'mail-search-session-'));
  try {
    const first = await openSession({ profileDir: dir, headless: true, timeoutMs: 60_000 });
    try {
      assert.ok(first.page, '지속 컨텍스트의 페이지를 돌려준다');
      assert.deepEqual(await checkProfileLock(dir), { locked: true });
      await assert.rejects(openSession({ profileDir: dir, headless: true, timeoutMs: 60_000 }), (error) => {
        assert.equal(error.exitCode, 1);
        assert.equal(error.code, 'profileInUse');
        return true;
      });
    } finally {
      await first.close();
    }
    assert.deepEqual(await checkProfileLock(dir), { locked: false }, '닫은 뒤에는 잠금이 풀린다');

    const again = await openSession({ profileDir: dir, headless: true, timeoutMs: 60_000 });
    await again.close();
    await again.close(); // 두 번 닫아도 안전
  } finally {
    await fs.rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {});
  }
});

test('중단 신호가 오면 브라우저가 닫혀 진행 중인 호출이 끝난다', { timeout: 120_000 }, async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'mail-search-session-'));
  const controller = new AbortController();
  try {
    const session = await openSession({ profileDir: dir, headless: true, signal: controller.signal, timeoutMs: 60_000 });
    const waiting = session.page.waitForFunction(() => false, undefined, { timeout: 60_000 });
    waiting.catch(() => {});
    setTimeout(() => controller.abort(), 300);
    await assert.rejects(waiting);
    await session.close();
  } finally {
    await fs.rm(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => {});
  }
});

test('시작 실패는 원인별 종료 코드로 분류하고 예외 원문은 싣지 않는다', () => {
  const cases = [
    ["browserType.launchPersistentContext: Executable doesn't exist at /secret/path/chrome", 2, 'browserMissing'],
    ['error while loading shared libraries: libnspr4.so: cannot open shared object file', 2, 'browserDeps'],
    ['Host system is missing dependencies to run browsers.', 2, 'browserDeps'],
    ['Missing X server or $DISPLAY', 2, 'noDisplay'],
    ['No usable sandbox!', 1, 'sandbox'],
    ['something else entirely /secret/path', 1, 'launchFailed'],
  ];
  for (const [text, exitCode, code] of cases) {
    const error = classifyLaunchError(new Error(text));
    assert.equal(error.exitCode, exitCode, text);
    assert.equal(error.code, code, text);
    assert.doesNotMatch(error.message, /secret/);
  }
});
