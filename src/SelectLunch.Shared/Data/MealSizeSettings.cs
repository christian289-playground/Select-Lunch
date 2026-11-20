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
        var channel = db.ChannelMealSizes.Where(s => s.ChannelId == channelId);
        if (size is not { } value)
        {
            await channel.ExecuteDeleteAsync(ct);
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        // SQLite 쓰기 잠금 아래 갱신·최초 삽입을 직렬화한다. 두 사람이 처음 설정해도 중복키가 나지 않는다.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await channel.ExecuteUpdateAsync(s => s.SetProperty(row => row.ParticipantsPerRestaurant, value), ct) == 0)
        {
            var entry = db.ChannelMealSizes.Add(new ChannelMealSize { ChannelId = channelId, ParticipantsPerRestaurant = value });
            try { await db.SaveChangesAsync(ct); }
            finally { entry.State = EntityState.Detached; }
        }
        await transaction.CommitAsync(ct);
    }
}
