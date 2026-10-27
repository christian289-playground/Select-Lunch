using SelectLunch.Slack.Leave;

namespace SelectLunch.Slack.Tests;

internal sealed class TestPollLeaveRoster : IPollLeaveRoster
{
    readonly LeaveRosterSnapshot snapshot;
    readonly Task<LeaveRosterSnapshot> completed;

    public static TestPollLeaveRoster Disabled { get; } =
        new(new LeaveRosterSnapshot(LeaveRosterStatus.Disabled, []));

    public TestPollLeaveRoster(LeaveRosterSnapshot snapshot)
    {
        this.snapshot = snapshot;
        completed = Task.FromResult(snapshot);
    }

    public Task<LeaveRosterSnapshot> GetAsync(long pollId, DateOnly date, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return completed;
    }

    public LeaveRosterSnapshot? GetCached(DateOnly date) => snapshot;
}
