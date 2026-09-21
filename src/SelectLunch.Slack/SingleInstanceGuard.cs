namespace SelectLunch.Slack;

/// <summary>
/// 같은 PC에서 두 번 뜨는 것을 막는다. 중복 기동하면 자동 메시지가 두 번 나가고
/// 투표 집계가 갈라진다.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    readonly Mutex _mutex;

    SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        // initiallyOwned: false — 이 가드가 실제로 기대는 성질은 "소유권"이 아니라
        // "핸들이 열려 있는 동안 이름 있는 뮤텍스의 createdNew가 false로 유지된다"는
        // 것뿐이다. true로 만들면 이 스레드가 뮤텍스 소유자가 되는데, await
        // host.RunAsync()의 연속(continuation)은 스레드풀의 다른 스레드에서 재개될 수
        // 있어 Dispose()에서 ReleaseMutex()를 부르면 "소유하지 않은 스레드의 해제"로
        // ApplicationException이 나고 정상 종료마저 비정상 종료로 보이게 만든다.
        var mutex = new Mutex(initiallyOwned: false, $"Global\\SelectLunch-{name}", out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        // ReleaseMutex()를 부르지 않는다 — 애초에 소유한 적이 없다(initiallyOwned: false).
        // 핸들을 닫는 것만으로 이 프로세스가 가드를 놓았다는 뜻이 되어 다음 인스턴스가
        // 다시 획득할 수 있다.
        _mutex.Dispose();
    }
}
