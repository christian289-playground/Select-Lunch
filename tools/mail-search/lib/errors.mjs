// 종료 코드와 안전한 오류 모델.
// 오류 메시지는 항상 이 파일의 한국어 문장이다. 브라우저·Playwright 예외의 원문, DOM, 메일 내용은
// 메시지에 싣지 않는다(분류에만 쓰고 버린다).

export const EXIT = Object.freeze({
  OK: 0,
  RUNTIME: 1,
  USAGE: 2,
  LOGIN_REQUIRED: 3,
  LOGIN_TIMEOUT: 4,
});

export class MailSearchError extends Error {
  /**
   * @param {number} exitCode EXIT 값
   * @param {string} code 기계가 읽는 짧은 식별자
   * @param {string} message 사용자에게 보여 줄 한국어 문장(원문 예외·메일 내용 금지)
   */
  constructor(exitCode, code, message) {
    super(message);
    this.name = 'MailSearchError';
    this.exitCode = exitCode;
    this.code = code;
  }
}

export const usageError = (code, message) => new MailSearchError(EXIT.USAGE, code, message);
export const runtimeError = (code, message) => new MailSearchError(EXIT.RUNTIME, code, message);
export const cancelledError = () => runtimeError('cancelled', '사용자 요청으로 중단했습니다.');

const UNEXPECTED = '예기치 않은 오류로 중단했습니다. 메일·화면 내용 보호를 위해 오류 상세는 출력하지 않습니다.';

/**
 * 알 수 없는 예외를 안전한 MailSearchError로 바꾼다. 예외 메시지는 분류에만 쓰고 출력하지 않는다.
 * @param {unknown} error
 * @param {AbortSignal} [signal]
 */
export function toMailSearchError(error, signal) {
  if (error instanceof MailSearchError) return error;
  if (signal?.aborted) return cancelledError();
  const name = typeof error?.name === 'string' ? error.name : '';
  const text = typeof error?.message === 'string' ? error.message : '';
  if (name === 'TimeoutError') {
    return runtimeError('timeout', 'Outlook 화면 동작을 기다리다 시간이 초과되었습니다.');
  }
  if (/net::ERR_/.test(text)) {
    return runtimeError('network', 'Outlook에 접속하지 못했습니다(네트워크 오류).');
  }
  if (
    /Target (?:page, context or browser )?has been closed|Browser has been closed|browser has disconnected|Target closed|Execution context was destroyed|Frame was detached/i.test(
      text,
    )
  ) {
    return runtimeError('browserClosed', '브라우저가 닫혔거나 연결이 끊겼습니다.');
  }
  return runtimeError('unexpected', UNEXPECTED);
}
