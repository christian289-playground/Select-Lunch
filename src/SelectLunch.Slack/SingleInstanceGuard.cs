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
        var mutex = new Mutex(initiallyOwned: true, $"Global\\SelectLunch-{name}", out var createdNew);

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
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
