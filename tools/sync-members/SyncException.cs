namespace SyncMembers;

/// <summary>
/// 사용자에게 그대로 보여 줘도 안전한 메시지만 담는 실패. 토큰·Slack 응답 원문·내부 예외 메시지를
/// 넣지 않는다 — 호출하는 쪽이 메시지를 가공 없이 stderr에 출력한다.
/// </summary>
public sealed class SyncException(string message) : Exception(message);

/// <summary>인수를 잘못 줬다. 종료 코드 2.</summary>
public sealed class UsageException(string message) : Exception(message);
