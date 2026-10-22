// 메일 내용은 신뢰할 수 없는 입력이다. 터미널을 조작하는 제어 문자와 글자 방향을 뒤집는 문자를 지운다.
// ZWJ/ZWNJ(U+200C, U+200D)는 이모지·일부 문자 결합에 쓰이므로 남긴다.
const UNSAFE =
  /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f\u200b\u200e\u200f\u202a-\u202e\u2060-\u2064\u2066-\u2069\ufeff]/g;

/** 한 줄 텍스트: 공백을 하나로 접고 제어 문자를 지운다. */
export function sanitizeLine(text) {
  return String(text ?? '')
    .replace(/\s+/g, ' ')
    .replace(UNSAFE, '')
    .trim();
}

/** 본문: 줄바꿈·탭은 유지하고 제어 문자와 방향 제어 문자만 지운다. */
export function sanitizeBody(text) {
  return String(text ?? '')
    .replace(/\r\n?/g, '\n')
    .replace(UNSAFE, '')
    .replace(/\u2028|\u2029/g, '\n')
    .trim();
}

/** 비교용: 공백을 접고 소문자로. */
export function normalizeForMatch(text) {
  return String(text ?? '')
    .normalize('NFC')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

/**
 * stdout은 코드 페이지와 무관하게 읽히도록 ASCII만 쓴다(JSON \uXXXX 이스케이프).
 * PowerShell이 자식 프로세스 출력을 콘솔 코드 페이지(cp949 등)로 해석해도 한글이 깨지지 않는다.
 */
export function toAsciiJson(value) {
  return JSON.stringify(value).replace(
    /[\u007f-\uffff]/g,
    (ch) => `\\u${ch.charCodeAt(0).toString(16).padStart(4, '0')}`,
  );
}
