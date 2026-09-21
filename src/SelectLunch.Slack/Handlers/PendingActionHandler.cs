using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Slack.Blocks;
using SlackNet;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>보완 요청 메시지의 식당 버튼 — 기존 값을 채운 수정 모달을 연다.</summary>
public sealed class PendingActionHandler(LunchDbContext db, ISlackApiClient slack)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        if (!ActionIds.TryParseRestaurantFill(request.Action.ActionId, out var restaurantId))
            return;

        var ct = CancellationToken.None;
        var restaurant = await db.Restaurants.SingleOrDefaultAsync(r => r.Id == restaurantId, ct);
        if (restaurant is null)
            return;

        var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
        var draft = new RestaurantDraft(
            restaurant.Id, restaurant.Name, restaurant.CategoryId,
            restaurant.WalkMinutes, restaurant.PriceLevel, restaurant.Note);

        await slack.Views.Open(request.TriggerId, RestaurantModal.Build(categories, draft, ModalContext.ForEdit(restaurant.Id)), ct);
    }
}
