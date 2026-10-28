namespace SelectLunch.Slack.Services;

/// <summary>하루 동안 사용자·식당별 알림 시도를 메모리에만 보관한다.</summary>
public sealed class EarlyDepartureNoticeTracker
{
    readonly Lock gate = new();
    readonly HashSet<(long RestaurantId, string UserId)> attempts = [];
    DateOnly? currentDate;
    bool completed;

    /// <summary>새 날짜에만 기록을 비우며 같은 날 완료 상태는 유지한다.</summary>
    public void StartDay(DateOnly date)
    {
        lock (gate)
        {
            if (currentDate is null || date > currentDate.Value)
            {
                currentDate = date;
                completed = false;
                attempts.Clear();
            }
        }
    }

    /// <summary>기록을 바꾸지 않고 이미 처리했거나 더 이상 처리할 수 없는 날짜인지 확인한다.</summary>
    public bool HasAttempted(DateOnly date, long restaurantId, string userId)
    {
        lock (gate)
        {
            if (currentDate is not { } current || date > current)
                return false;
            return date < current || completed || attempts.Contains((restaurantId, userId));
        }
    }

    public bool TryClaim(DateOnly date, long restaurantId, string userId)
    {
        lock (gate)
        {
            if (currentDate is { } current && date < current)
                return false;
            if (currentDate is null || date > currentDate.Value)
            {
                currentDate = date;
                completed = false;
                attempts.Clear();
            }
            return !completed && attempts.Add((restaurantId, userId));
        }
    }

    public void CompleteDay(DateOnly date)
    {
        lock (gate)
        {
            if (currentDate is { } current && date < current)
                return;
            currentDate = date;
            attempts.Clear();
            completed = true;
        }
    }
}
