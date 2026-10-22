import { cancelledError, runtimeError } from './errors.mjs';

export function throwIfAborted(signal) {
  if (signal?.aborted) throw cancelledError();
}

/** 중단 신호로 즉시 깨어나는 sleep. */
export function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(cancelledError());
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      reject(cancelledError());
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/** promise를 기다리되 ms가 지나면 기다림을 포기한다(포기 시 undefined). 타이머는 항상 정리한다. */
export async function settleWithin(promise, ms) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((resolve) => {
        timer = setTimeout(() => resolve(undefined), ms);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

/**
 * predicate가 참이 될 때까지 기다린다. 시간 안에 참이 되지 않으면 false(예외 아님).
 * @param {() => Promise<boolean>|boolean} predicate
 */
export async function waitFor(predicate, { timeoutMs, pollMs, signal }) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    throwIfAborted(signal);
    if (await predicate()) return true;
    if (Date.now() >= deadline) return false;
    await sleep(pollMs, signal);
  }
}

/** locator 상태 조회가 화면 전환 중 던져도 "보이지 않음"으로 본다. */
export async function safeVisible(locator) {
  try {
    return await locator.isVisible();
  } catch {
    return false;
  }
}

/** 사용자가 창을 닫았거나 브라우저가 죽었으면 대기 루프를 끝낸다. */
export function assertPageOpen(page) {
  if (page.isClosed()) {
    throw runtimeError('browserClosed', '브라우저 창이 닫혔습니다.');
  }
}
