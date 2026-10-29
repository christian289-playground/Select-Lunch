using Microsoft.EntityFrameworkCore;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>선택창을 기존 식당의 수정폼으로 바꾼다. 이 단계에서는 DB를 변경하지 않는다.</summary>
public sealed class RestaurantEditActionHandler(LunchDbContext db, ISlackApiClient slack) : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        if (request.Actions?.FirstOrDefault() is not ExternalSelectAction { ActionId: ActionIds.RestaurantEditSelect } action
            || !SelectedRestaurant.TryRead(action, out var restaurantId) || restaurantId <= 0
            || request.View is not { CallbackId: RestaurantEditPicker.CallbackId } view
            || string.IsNullOrEmpty(view.Id) || string.IsNullOrEmpty(view.Hash)
            || string.IsNullOrEmpty(request.User?.Id) || view.PrivateMetadata != request.User.Id)
        {
            return;
        }

        var ct = CancellationToken.None;
        var restaurant = await db.Restaurants.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == restaurantId && r.Status != RestaurantStatus.Archived, ct);
        ModalViewDefinition next;
        if (restaurant is null)
        {
            var hasRestaurants = await db.Restaurants.AnyAsync(r => r.Status != RestaurantStatus.Archived, ct);
            next = RestaurantEditPicker.Build(hasRestaurants, request.User.Id, selectionUnavailable: true);
        }
        else
        {
            var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
            var draft = new RestaurantDraft(
                restaurant.Id, restaurant.Name, restaurant.CategoryId,
                restaurant.WalkMinutes, restaurant.PriceLevel, restaurant.Note,
                restaurant.Address, restaurant.WaitLevel, DepartureTime: restaurant.DepartureTime,
                EarlyDepartureSeconds: restaurant.EarlyDepartureSeconds);
            next = RestaurantModal.Build(categories, draft, ModalContext.ForEdit(restaurant.Id));
        }

        try
        {
            await slack.Views.UpdateByViewId(next, view.Id, view.Hash, ct);
        }
        catch (SlackException ex) when (ex.ErrorCode == "hash_conflict")
        {
            // 먼저 처리된 선택을 늦게 도착한 중복 이벤트로 덮어쓰지 않는다.
        }
    }
}
