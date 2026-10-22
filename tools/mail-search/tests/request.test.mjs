import assert from 'node:assert/strict';
import { Readable } from 'node:stream';
import test from 'node:test';
import { DEFAULT_ACCOUNT, parseRequest, readJsonObject } from '../lib/request.mjs';

const usage = (expectedCode) => (error) => {
  assert.equal(error.exitCode, 2);
  if (expectedCode) assert.equal(error.code, expectedCode);
  return true;
};

test('빈 객체는 기본값으로 정규화된다', () => {
  const request = parseRequest({});
  assert.equal(request.account, DEFAULT_ACCOUNT);
  assert.deepEqual(request.query, []);
  assert.deepEqual(request.names, []);
  assert.equal(request.receivedAfter, null);
  assert.equal(request.receivedBefore, null);
  assert.equal(request.maxResults, 200);
  assert.equal(request.includeBody, false);
  assert.equal(request.login, false);
  assert.equal(request.headless, false);
  assert.equal(request.profileDirectory, null);
  assert.equal(request.timeoutSeconds, 60);
  assert.equal(request.loginTimeoutSeconds, 600);
});

test('계정은 소문자로 정규화하고 이메일 형식만 받는다', () => {
  assert.equal(parseRequest({ account: '  Alice@Example.INVALID ' }).account, 'alice@example.invalid');
  for (const bad of ['alice', 'alice@', '@example.invalid', 'a b@example.invalid', 'a@b', 'a@b.c,d@e.f', 7, '']) {
    assert.throws(() => parseRequest({ account: bad }), usage('badInput'), String(bad));
  }
});

test('Shell 이 보내는 전체 키 묶음(로그인 모드 포함)을 받는다', () => {
  const request = parseRequest({
    account: 'alice@example.invalid',
    query: ['휴가', '연차'],
    names: [],
    receivedAfter: null,
    receivedBefore: null,
    maxResults: 200,
    includeBody: false,
    login: true,
    headless: false,
    profileDirectory: null,
    timeoutSeconds: 60,
    loginTimeoutSeconds: 600,
  });
  assert.equal(request.login, true);
});

test('login 과 headless 는 함께 쓸 수 없다', () => {
  assert.throws(() => parseRequest({ login: true, headless: true }), usage('loginHeadless'));
});

test('알 수 없는 키·배열이 아닌 입력·잘못된 타입은 거부', () => {
  assert.throws(() => parseRequest({ nope: 1 }), usage('badInput'));
  assert.throws(() => parseRequest([]), usage('badInput'));
  assert.throws(() => parseRequest(null), usage('badInput'));
  assert.throws(() => parseRequest('x'), usage('badInput'));
  assert.throws(() => parseRequest({ query: '휴가' }), usage('badInput'));
  assert.throws(() => parseRequest({ query: ['휴가', 3] }), usage('badInput'));
  assert.throws(() => parseRequest({ names: {} }), usage('badInput'));
  assert.throws(() => parseRequest({ includeBody: 'yes' }), usage('badInput'));
  assert.throws(() => parseRequest({ headless: 1 }), usage('badInput'));
});

test('날짜는 실존하는 YYYY-MM-DD, 앞날이 뒷날보다 먼저여야 한다', () => {
  assert.equal(parseRequest({ receivedAfter: '2026-10-01' }).receivedAfter, '2026-10-01');
  for (const bad of ['2026-02-30', '2026-13-01', '20261001', '2026-1-1', '2026-10-01T00:00:00Z', 20261001, '1989-12-31', '2100-01-01']) {
    assert.throws(() => parseRequest({ receivedAfter: bad }), usage('badInput'), String(bad));
    assert.throws(() => parseRequest({ receivedBefore: bad }), usage('badInput'), String(bad));
  }
  assert.throws(() => parseRequest({ receivedAfter: '2026-10-02', receivedBefore: '2026-10-01' }), usage('dateRange'));
  assert.throws(() => parseRequest({ receivedAfter: '2026-10-01', receivedBefore: '2026-10-01' }), usage('dateRange'));
});

test('maxResults 1..1000 정수, 시간 제한은 양수 범위', () => {
  assert.equal(parseRequest({ maxResults: 1 }).maxResults, 1);
  assert.equal(parseRequest({ maxResults: 1000 }).maxResults, 1000);
  for (const bad of [0, 1001, 1.5, '10', Number.NaN]) {
    assert.throws(() => parseRequest({ maxResults: bad }), usage('badInput'), String(bad));
  }
  assert.equal(parseRequest({ timeoutSeconds: 1.5 }).timeoutSeconds, 1.5);
  for (const bad of [0, -1, 3601, '60']) {
    assert.throws(() => parseRequest({ timeoutSeconds: bad }), usage('badInput'), String(bad));
    assert.throws(() => parseRequest({ loginTimeoutSeconds: bad }), usage('badInput'), String(bad));
  }
});

test('profileDirectory 는 비어 있지 않은 문자열이거나 null', () => {
  assert.equal(parseRequest({ profileDirectory: null }).profileDirectory, null);
  assert.equal(parseRequest({ profileDirectory: '/x/y' }).profileDirectory, '/x/y');
  assert.throws(() => parseRequest({ profileDirectory: '' }), usage('badInput'));
  assert.throws(() => parseRequest({ profileDirectory: '  ' }), usage('badInput'));
  assert.throws(() => parseRequest({ profileDirectory: 3 }), usage('badInput'));
});

test('입력 오류 문장은 입력값을 되풀이하지 않는다', () => {
  assert.throws(
    () => parseRequest({ account: 'secret-person-name' }),
    (error) => {
      assert.doesNotMatch(error.message, /secret-person-name/);
      return true;
    },
  );
});

test('stdin: BOM 을 벗기고 JSON 객체 하나를 읽는다', async () => {
  const stream = Readable.from([Buffer.from('\ufeff{"account":"a@b.cd"}', 'utf8')]);
  assert.deepEqual(await readJsonObject(stream), { account: 'a@b.cd' });
});

test('stdin: 비었거나 JSON 이 아니거나 너무 크면 종료 코드 2', async () => {
  await assert.rejects(readJsonObject(Readable.from([Buffer.from('  ')])), usage('noInput'));
  await assert.rejects(readJsonObject(Readable.from([Buffer.from('{"a":')])), usage('badJson'));
  await assert.rejects(readJsonObject(Readable.from([Buffer.from('{} {}')])), usage('badJson'));
  await assert.rejects(readJsonObject(Readable.from([Buffer.alloc(40)]), 16), usage('inputTooLarge'));
});
