namespace SelectLunch.Slack.Workers;

/// <summary>
/// 서드파티를 30초/60초 tick마다 두드리지 않기 위한 인메모리 시도 스로틀.
/// 재기동하면 곧바로 다시 시도할 뿐이라 영속화하지 않는다.
///
/// 메뉴 수집과 메뉴 스레드 업로드가 같은 것을 쓴다 — 스로틀이 두 벌이면 한쪽만
/// 고쳐 놓고 다른 쪽은 그대로인 상태가 되기 쉽다.
/// </summary>
public sealed class AttemptThrottle(TimeSpan interval)
{
    DateTimeOffset? _lastAttemptAt;

    /// <summary>
    /// 지금 시도해도 되면 true를 돌려주고 시도 시각을 기록한다. 간격 안이면 false.
    /// 실패한 시도도 시도로 친다 — 스로틀의 목적이 "실패를 빠르게 반복하지 않는 것"이다.
    /// </summary>
    public bool TryAttempt(DateTimeOffset now)
    {
        if (_lastAttemptAt is { } last && now - last < interval)
            return false;

        _lastAttemptAt = now;
        return true;
    }
}
