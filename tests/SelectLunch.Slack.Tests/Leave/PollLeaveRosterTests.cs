using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelectLunch.Slack.Leave;
using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Tests;

/// <summary>
/// 싱글톤 캐시 경계. 목적은 세 가지다 — 메뉴 메시지를 다시 만들 때마다 브라우저 도구를 다시 띄우지 않는다(같은 키는 한 번),
/// 한 호출자의 취소가 다른 호출자의 조회를 깨지 않는다, 실패가 "휴가자 없음"으로 둔갑하거나 로그로 새지 않는다.
/// 조회 자체(Slack·DB·메일)는 <see cref="ILeaveRosterLoader"/> 뒤에 있고 여기서는 스코프 경계에서만 대신한다.
/// </summary>
public class PollLeaveRosterTests
{
    static readonly DateOnly Day = new(2031, 3, 12);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static LeaveRosterSnapshot Available(params string[] names) => new(LeaveRosterStatus.Available, names);

    static Task<T> Within<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

    sealed class LoaderScript
    {
        int _calls;
        int _disposed;

        public ConcurrentQueue<DateOnly> Dates { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public int Disposed => Volatile.Read(ref _disposed);

        public Func<DateOnly, CancellationToken, Task<LeaveRosterSnapshot>> Handler { get; set; } =
            (_, _) => Task.FromResult(Available("default"));

        public Task<LeaveRosterSnapshot> Run(DateOnly date, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Dates.Enqueue(date);
            return Handler(date, ct);
        }

        public void MarkDisposed() => Interlocked.Increment(ref _disposed);
    }

    sealed class ScriptedLoader(LoaderScript script) : ILeaveRosterLoader, IDisposable
    {
        public Task<LeaveRosterSnapshot> LoadAsync(DateOnly date, CancellationToken ct) => script.Run(date, ct);

        public void Dispose() => script.MarkDisposed();
    }

    sealed class TestLifetime : IHostApplicationLifetime
    {
        readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    sealed class CapturingLogger<T> : ILogger<T>
    {
        readonly object _gate = new();

        public List<string> Messages { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Messages.Add(formatter(state, exception));
                if (exception is not null)
                    Exceptions.Add(exception);
            }
        }
    }

    sealed class Harness : IDisposable
    {
        readonly ServiceProvider _provider;

        public LoaderScript Script { get; } = new();

        public TestLifetime Lifetime { get; } = new();

        public CapturingLogger<PollLeaveRoster> Logger { get; } = new();

        public PollLeaveRoster Roster { get; }

        public Harness(bool enabled = true)
        {
            var services = new ServiceCollection();
            services.AddScoped<ILeaveRosterLoader>(_ => new ScriptedLoader(Script));
            _provider = services.BuildServiceProvider();

            Roster = new PollLeaveRoster(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new LeaveDisplayOptions { Enabled = enabled },
                Lifetime,
                Logger);
        }

        public void Dispose() => _provider.Dispose();
    }

    [Fact]
    public async Task Disabled_feature_never_looks_anything_up()
    {
        using var h = new Harness(enabled: false);

        var snapshot = await h.Roster.GetAsync(1, Day, Ct);
        var cached = h.Roster.GetCached(Day);

        Assert.Equal(LeaveRosterStatus.Disabled, snapshot.Status);
        Assert.NotNull(cached);
        Assert.Equal(LeaveRosterStatus.Disabled, cached.Status);
        Assert.Equal(0, h.Script.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_for_the_same_poll_share_one_lookup()
    {
        using var h = new Harness();
        var gate = new TaskCompletionSource<LeaveRosterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script.Handler = (_, _) => gate.Task;

        var calls = Enumerable.Range(0, 5).Select(_ => h.Roster.GetAsync(7, Day, Ct)).ToArray();
        Assert.Null(h.Roster.GetCached(Day));   // 진행 중이면 기다리지 않고 없음

        gate.SetResult(Available("alpha"));
        var results = await Within(Task.WhenAll(calls));

        Assert.All(results, r => Assert.Equal(["alpha"], r.DisplayNames));
        Assert.Equal(1, h.Script.Calls);
    }

    [Fact]
    public async Task Finished_result_is_reused_and_visible_to_get_cached_for_that_date_only()
    {
        using var h = new Harness();

        var first = await h.Roster.GetAsync(7, Day, Ct);
        var again = await h.Roster.GetAsync(7, Day, Ct);

        Assert.Same(first, again);
        Assert.Equal(1, h.Script.Calls);

        var cached = h.Roster.GetCached(Day);
        Assert.Same(first, cached);
        Assert.Null(h.Roster.GetCached(Day.AddDays(1)));
    }

    [Fact]
    public async Task Newer_poll_replaces_the_cache_and_older_poll_neither_loads_nor_evicts()
    {
        using var h = new Harness();
        h.Script.Handler = (_, _) => Task.FromResult(Available("load-" + h.Script.Calls));

        Assert.Equal(["load-1"], (await h.Roster.GetAsync(5, Day, Ct)).DisplayNames);
        Assert.Equal(["load-2"], (await h.Roster.GetAsync(6, Day, Ct)).DisplayNames);

        // 오래된 투표의 갱신은 조회하지 않고 "알 수 없음"이다 — 최신 캐시를 밀어내지 않는다.
        var stale = await h.Roster.GetAsync(5, Day, Ct);
        Assert.Equal(LeaveRosterStatus.Unavailable, stale.Status);
        Assert.Equal(2, h.Script.Calls);

        Assert.Equal(["load-2"], (await h.Roster.GetAsync(6, Day, Ct)).DisplayNames);
        Assert.Equal(2, h.Script.Calls);
    }

    [Fact]
    public async Task Older_request_does_not_disturb_a_newer_lookup_still_in_flight()
    {
        using var h = new Harness();
        var gate = new TaskCompletionSource<LeaveRosterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script.Handler = (_, _) => gate.Task;

        var newest = h.Roster.GetAsync(6, Day, Ct);
        var stale = await h.Roster.GetAsync(5, Day, Ct);
        gate.SetResult(Available("alpha"));

        Assert.Equal(LeaveRosterStatus.Unavailable, stale.Status);
        Assert.Equal(["alpha"], (await Within(newest)).DisplayNames);
        Assert.Equal(1, h.Script.Calls);
    }

    [Fact]
    public async Task Date_rollover_looks_up_the_new_day_and_keeps_only_the_newest()
    {
        using var h = new Harness();
        var next = Day.AddDays(1);

        await h.Roster.GetAsync(1, Day, Ct);
        await h.Roster.GetAsync(2, next, Ct);
        await h.Roster.GetAsync(2, next, Ct);

        Assert.Equal([Day, next], h.Script.Dates.ToArray());

        // 어제 키는 더 이상 보관하지 않는다.
        var yesterday = await h.Roster.GetAsync(1, Day, Ct);
        Assert.Equal(LeaveRosterStatus.Unavailable, yesterday.Status);
        Assert.Equal(2, h.Script.Calls);
        Assert.Null(h.Roster.GetCached(Day));
        Assert.NotNull(h.Roster.GetCached(next));
    }

    [Fact]
    public async Task Caller_cancellation_ends_only_that_wait_and_does_not_cancel_the_shared_lookup()
    {
        using var h = new Harness();
        var gate = new TaskCompletionSource<LeaveRosterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = CancellationToken.None;
        h.Script.Handler = (_, token) =>
        {
            seen = token;
            return gate.Task;
        };

        using var cts = new CancellationTokenSource();
        var waiting = h.Roster.GetAsync(1, Day, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var other = h.Roster.GetAsync(1, Day, Ct);
        gate.SetResult(Available("alpha"));

        Assert.Equal(["alpha"], (await Within(other)).DisplayNames);
        Assert.Equal(1, h.Script.Calls);
        Assert.False(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task Application_stopping_cancels_the_lookup_quietly()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script.Handler = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Available("never");
        };

        var pending = h.Roster.GetAsync(1, Day, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        h.Lifetime.StopApplication();

        var snapshot = await Within(pending);

        Assert.Equal(LeaveRosterStatus.Unavailable, snapshot.Status);
        Assert.Empty(h.Logger.Messages);
    }

    [Fact]
    public async Task Failed_lookup_is_unknown_stays_cached_for_that_poll_and_does_not_taint_the_next()
    {
        using var h = new Harness();
        h.Script.Handler = (_, _) => Task.FromException<LeaveRosterSnapshot>(new LeaveLookupException(LeaveLookupFailure.MailTimeout));

        var failed = await h.Roster.GetAsync(1, Day, Ct);
        var again = await h.Roster.GetAsync(1, Day, Ct);

        Assert.Equal(LeaveRosterStatus.Unavailable, failed.Status);
        Assert.Empty(failed.DisplayNames);
        Assert.Same(failed, again);
        Assert.Equal(1, h.Script.Calls);   // 같은 투표는 실패도 다시 조회하지 않는다.
        Assert.Equal(LeaveRosterStatus.Unavailable, h.Roster.GetCached(Day)!.Status);

        var warning = Assert.Single(h.Logger.Messages);
        Assert.Contains(nameof(LeaveLookupFailure.MailTimeout), warning);

        h.Script.Handler = (_, _) => Task.FromResult(Available("alpha"));
        var next = await h.Roster.GetAsync(2, Day, Ct);

        Assert.Equal(["alpha"], next.DisplayNames);
        Assert.Equal(2, h.Script.Calls);
    }

    [Fact]
    public async Task Unexpected_error_is_unknown_and_its_message_never_reaches_the_log()
    {
        using var h = new Harness();
        h.Script.Handler = (_, _) => throw new InvalidOperationException("SECRET-NAME 2031-03-12 body");

        var snapshot = await h.Roster.GetAsync(1, Day, Ct);

        Assert.Equal(LeaveRosterStatus.Unavailable, snapshot.Status);

        var warning = Assert.Single(h.Logger.Messages);
        Assert.Contains(nameof(InvalidOperationException), warning);
        Assert.DoesNotContain("SECRET", warning);
        Assert.Empty(h.Logger.Exceptions);   // 예외 객체(메시지·스택)는 로거에 넘기지 않는다.
    }

    [Fact]
    public async Task Each_lookup_gets_its_own_scope_that_is_released_even_when_it_fails()
    {
        using var h = new Harness();

        await h.Roster.GetAsync(1, Day, Ct);
        Assert.Equal(1, h.Script.Disposed);

        h.Script.Handler = (_, _) => throw new InvalidOperationException("boom");
        await h.Roster.GetAsync(2, Day, Ct);
        Assert.Equal(2, h.Script.Disposed);
    }
}
