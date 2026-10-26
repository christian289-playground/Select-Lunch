import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { Readable } from 'node:stream';
import test from 'node:test';
import { EXIT, MailSearchError } from '../lib/errors.mjs';
import { toAsciiJson } from '../lib/text.mjs';
import { runCli } from '../mail-search.mjs';

function sink() {
  const chunks = [];
  return {
    write(text, callback) {
      chunks.push(String(text));
      callback?.();
      return true;
    },
    get text() {
      return chunks.join('');
    },
  };
}

const input = (value) => Readable.from([Buffer.from(typeof value === 'string' ? value : JSON.stringify(value), 'utf8')]);

test('성공: stdout 은 ASCII JSON 한 줄(한글·이모지는 이스케이프), 종료 코드 0', async () => {
  const output = {
    mode: 'search',
    account: 'alice@example.invalid',
    searchExpression: '(휴가)',
    result: { returned: 1, truncated: false, incompleteReason: null },
    messages: [{ text: '홍길동 휴가 😀' }],
  };
  let received;
  const stdout = sink();
  const stderr = sink();
  const code = await runCli({
    stdin: input({ account: 'Alice@Example.Invalid', query: ['휴가'] }),
    stdout,
    stderr,
    runner: async (request) => {
      received = request;
      return output;
    },
  });
  assert.equal(code, EXIT.OK);
  assert.equal(received.account, 'alice@example.invalid');
  assert.match(stdout.text, /^[\x20-\x7e]*\n$/);
  assert.equal(stdout.text.trim().split('\n').length, 1);
  assert.deepEqual(JSON.parse(stdout.text), output);
});

test('MailSearchError 는 종료 코드와 한국어 사유를 stderr 로, stdout 은 비운다', async () => {
  const stdout = sink();
  const stderr = sink();
  const code = await runCli({
    stdin: input({}),
    stdout,
    stderr,
    runner: async () => {
      throw new MailSearchError(EXIT.LOGIN_REQUIRED, 'loginRequired', '로그인이 필요합니다.');
    },
  });
  assert.equal(code, EXIT.LOGIN_REQUIRED);
  assert.equal(stdout.text, '');
  assert.match(stderr.text, /로그인이 필요합니다/);
});

test('알 수 없는 예외의 원문은 stderr 에 새지 않는다', async () => {
  for (const error of [
    new Error('메일 제목 secret-subject-xyz / vincent@example.invalid'),
    Object.assign(new Error('locator.click: Timeout 60000ms exceeded. <div>secret-dom-abc</div>'), { name: 'TimeoutError' }),
  ]) {
    const stdout = sink();
    const stderr = sink();
    const code = await runCli({
      stdin: input({}),
      stdout,
      stderr,
      runner: async () => {
        throw error;
      },
    });
    assert.equal(code, EXIT.RUNTIME);
    assert.equal(stdout.text, '');
    assert.doesNotMatch(stderr.text, /secret|example\.invalid|<div>/);
    assert.match(stderr.text, /^오류: /);
  }
});

test('잘못된 입력은 종료 코드 2 이고 실행기를 부르지 않는다', async () => {
  for (const stdin of [
    input('not json'),
    input(''),
    input({ login: true, headless: true }),
    input({ unknown: 1 }),
    input({ browserChannel: 'edge' }),
  ]) {
    let called = false;
    const stdout = sink();
    const code = await runCli({
      stdin,
      stdout,
      stderr: sink(),
      runner: async () => {
        called = true;
        return {};
      },
    });
    assert.equal(code, EXIT.USAGE);
    assert.equal(stdout.text, '');
    assert.equal(called, false);
  }
});

test('stdin 이 터미널이면 종료 코드 2', async () => {
  const tty = Object.assign(Readable.from([]), { isTTY: true });
  assert.equal(await runCli({ stdin: tty, stdout: sink(), stderr: sink(), runner: async () => ({}) }), EXIT.USAGE);
});

test('검색식 연산자 주입은 브라우저를 띄우기 전에 종료 코드 2 (실제 실행기)', async () => {
  const stdout = sink();
  const stderr = sink();
  const code = await runCli({ stdin: input({ query: ['휴가 OR 1'] }), stdout, stderr });
  assert.equal(code, EXIT.USAGE);
  assert.equal(stdout.text, '');
  assert.match(stderr.text, /연산자/);
});

test('SIGINT 는 실행을 중단 신호로 알리고 종료 코드 1, 핸들러는 정리된다', async () => {
  const target = new EventEmitter();
  const stdout = sink();
  const stderr = sink();
  const pending = runCli({
    stdin: input({}),
    stdout,
    stderr,
    signalTarget: target,
    runner: (_request, { signal }) =>
      new Promise((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(new Error('Target closed')), { once: true });
      }),
  });
  await new Promise((resolve) => setTimeout(resolve, 80));
  assert.ok(target.listenerCount('SIGINT') > 0);
  target.emit('SIGINT');
  assert.equal(await pending, EXIT.RUNTIME);
  assert.equal(stdout.text, '');
  assert.match(stderr.text, /중단/);
  assert.equal(target.listenerCount('SIGINT'), 0);
  assert.equal(target.listenerCount('SIGTERM'), 0);
});

test('toAsciiJson: 서로게이트 쌍과 제어 문자도 왕복된다', () => {
  const value = { text: '😀 한글\u0007\n', n: 1 };
  const text = toAsciiJson(value);
  assert.match(text, /^[\x20-\x7e]*$/);
  assert.deepEqual(JSON.parse(text), value);
});
