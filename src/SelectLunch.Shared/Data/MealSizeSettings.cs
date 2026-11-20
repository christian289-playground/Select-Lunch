using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Entities;

namespace SelectLunch.Shared.Data;

public static class MealSizeSettings
{
    // 설정 행이 없어도 값 하나를 반환한다. 투표 인원 집계와 조합하면 한 SELECT에서 둘 다 읽을 수 있다.
    public static IQueryable<int> EffectiveMealSize(this LunchDbContext db, string channelId, int serverDefault) =>
        db.ChannelMealSizes.Where(s => s.ChannelId == channelId)
            .Select(s => (int?)s.ParticipantsPerRestaurant).DefaultIfEmpty().Select(size => size ?? serverDefault);

    public static async Task SetMealSizeOverrideAsync(this LunchDbContext db, string channelId, int? size, CancellationToken ct)
    {
        if (size is { } positive)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(positive);

        // 설정·초기화를 동일한 SQLite 쓰기 잠금 아래서 직렬화하고 응답 기한이 지난 변경은 커밋하지 않는다.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var channel = db.ChannelMealSizes.Where(s => s.ChannelId == channelId);
        if (size is not { } value)
        {
            await channel.ExecuteDeleteAsync(ct);
        }
        else if (await channel.ExecuteUpdateAsync(s => s.SetProperty(row => row.ParticipantsPerRestaurant, value), ct) == 0)
        {
            var entry = db.ChannelMealSizes.Add(new ChannelMealSize { ChannelId = channelId, ParticipantsPerRestaurant = value });
            try { await db.SaveChangesAsync(ct); }
            finally { entry.State = EntityState.Detached; }
        }
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(ct);
    }
}
