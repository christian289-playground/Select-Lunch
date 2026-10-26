using SelectLunch.Slack.Options;

namespace SelectLunch.Slack.Leave;

/// <summary>
/// 메뉴 게시일의 휴가자 명단을 가장 최근 (날짜, 투표) 하나에 대해서만 조회·보관하는 싱글톤.
/// 메뉴 메시지는 투표·갱신·마감 때마다 통째로 다시 만들어지므로, 매번 브라우저 도구를 다시 띄우면 느리고
/// 같은 브라우저 프로필을 쓰는 실행끼리 충돌한다. 그래서 같은 키는 한 번만 조회한다.
/// <list type="bullet">
/// <item>같은 (날짜, 투표)는 하나의 <see cref="Task"/>를 공유한다 — 동시 호출은 합쳐지고, 끝난 결과(실패 포함)는 그대로 재사용한다.
/// 새 투표·새 날짜 또는 앱 재시작이 새 조회다.</item>
/// <item>더 최근 키가 있으면 오래된 키는 조회하지 않고 알 수 없음이다 — 오래된 메시지의 갱신이 최신 조회를 밀어내지 않는다.</item>
/// <item>호출자 취소는 그 호출의 대기만 끝낸다. 공유 조회는 앱 종료 때만 취소돼 다른 호출자에게 영향이 없다.</item>
/// <item>조회마다 새 스코프에서 <see cref="ILeaveRosterLoader"/>를 얻고 끝나면 스코프(DbContext)를 해제한다. 싱글톤이 붙들지 않는다.</item>
/// <item>RAM에는 최신 하나의 결과(표시 이름 목록)만 있다. 메일 원문·파싱 결과는 보관하지 않는다.</item>
/// <item>실패 로그는 고정 문구와 원인 분류(또는 예외 형식 이름)만 남긴다. 사람·날짜·본문·건수·예외 메시지는 남기지 않는다.</item>
/// </list>
/// </summary>
public sealed class PollLeaveRoster : IPollLeaveRoster
{
    readonly IServiceScopeFactory _scopes;
    readonly LeaveDisplayOptions _options;
    readonly IHostApplicationLifetime _lifetime;
    readonly ILogger<PollLeaveRoster> _logger;

    readonly object _gate = new();
    Entry? _entry;

    public PollLeaveRoster(
        IServiceScopeFactory scopes,
        LeaveDisplayOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<PollLeaveRoster> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task<LeaveRosterSnapshot> GetAsync(long pollId, DateOnly date, CancellationToken ct)
    {
        if (!_options.Enabled)
            return Task.FromResult(LeaveRosterSnapshot.Disabled);

        Task<LeaveRosterSnapshot> load;
        lock (_gate)
        {
            var order = _entry is { } current ? Compare(date, pollId, current.Date, current.PollId) : 1;
            if (order < 0)
                return Task.FromResult(LeaveRosterSnapshot.Unavailable);

            if (order == 0)
            {
                load = _entry!.Load;
            }
            else
            {
                // Task.Run이라 잠금 안에서 조회가 시작되지 않는다.
                _entry = new Entry(pollId, date, Task.Run(() => LoadAsync(date)));
                load = _entry.Load;
            }
        }

        return load.WaitAsync(ct);
    }

    public LeaveRosterSnapshot? GetCached(DateOnly date)
    {
        if (!_options.Enabled)
            return LeaveRosterSnapshot.Disabled;

        lock (_gate)
        {
            return _entry is { } entry && entry.Date == date && entry.Load.IsCompletedSuccessfully
                ? entry.Load.Result
                : null;
        }
    }

    /// <summary>날짜가 먼저, 같으면 투표 ID. 클수록 최근이다.</summary>
    static int Compare(DateOnly date, long pollId, DateOnly otherDate, long otherPollId)
    {
        var byDate = date.CompareTo(otherDate);
        return byDate != 0 ? byDate : pollId.CompareTo(otherPollId);
    }

    /// <summary>실패는 던지지 않고 알 수 없음으로 바꾼다. 이 Task는 예외로 끝나지 않는다.</summary>
    async Task<LeaveRosterSnapshot> LoadAsync(DateOnly date)
    {
        var stopping = _lifetime.ApplicationStopping;

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var loader = scope.ServiceProvider.GetRequiredService<ILeaveRosterLoader>();
            return await loader.LoadAsync(date, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // 앱 종료로 멈춘 것이다. 알릴 문제가 아니다.
        }
        catch (LeaveLookupException ex)
        {
            _logger.LogWarning("휴가 정보를 확인하지 못했습니다. 원인: {Reason}", ex.Reason);
        }
        catch (Exception ex)
        {
            // 예외 메시지·스택에는 이름·날짜·메일 내용이 섞일 수 있다. 형식 이름만 남긴다.
            _logger.LogWarning("휴가 정보를 확인하지 못했습니다. 오류 형식: {ErrorType}", ex.GetType().Name);
        }

        return LeaveRosterSnapshot.Unavailable;
    }

    sealed record Entry(long PollId, DateOnly Date, Task<LeaveRosterSnapshot> Load);
}
