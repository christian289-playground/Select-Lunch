using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 오후 "뭐 드셨어요?" 메시지의 버튼·드롭다운·신규 등록. 이미 올라간 메시지의 action_id를 그대로 받지만,
/// 누르는 순간에는 **아무것도 기록하지 않고** 본인용 확정 창만 연다 — 기록은 창을 제출할 때
/// <see cref="MealConfirmationModalHandler"/>가 한 번에 한다.
/// </summary>
public sealed class MealActionHandler(
    LunchDbContext db,
    MealConfirmationService meals,
    ISlackApiClient slack,
    IOptionsMonitor<LunchOptions> options,
    TimeProvider clock)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var ct = CancellationToken.None;

        // 신규 등록 — 확정 창의 날짜·채널·사용자를 등록 창에 실어 등록 직후 같은 기록으로 잇는다
        if (ActionIds.TryParseMealNew(action.ActionId, out var newDate))
        {
            if (await AllowedStateAsync(request, newDate, ct) is not { } newState)
                return;

            var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
            await slack.Views.Open(
                request.TriggerId,
                RestaurantModal.BuildForMeal(categories, newDate, meals.ChannelId, request.User.Id, newState),
                ct);
            return;
        }

        if (!ActionIds.TryParseMeal(action.ActionId, out var date, out var restaurantId))
            return;

        // 드롭다운이면 선택 값이 실제 식당이다 (action_id의 0은 자리표시자)
        if (SelectedRestaurant.TryRead(action, out var selected))
            restaurantId = selected;

        if (restaurantId <= 0)
            return;

        if (await AllowedStateAsync(request, date, ct) is not { } state)
            return;

        // 당일 정정은 이미 기록된 전체 목록에서 시작하고, 새 기록은 방금 고른 식당 하나에서 시작한다.
        IReadOnlyList<MealPlace> initial = state.Recorded;
        if (initial.Count == 0)
        {
            // 기록 직전에 서버가 다시 검사하지만, 없는 식당으로 창을 열지 않도록 여기서도 거른다.
            var name = await db.Restaurants.AsNoTracking()
                .Where(r => r.Id == restaurantId && r.Status == RestaurantStatus.Active && r.CategoryId != null)
                .Select(r => r.Name)
                .SingleOrDefaultAsync(ct);
            if (name is null)
            {
                await slack.Chat.PostEphemeral(request.User.Id, new SlackNet.WebApi.Message
                {
                    Channel = request.Container.ChannelId,
                    Text = "선택한 식당을 찾을 수 없습니다. 식당 목록에서 다시 골라 주세요. 기록은 변경하지 않았습니다.",
                }, ct);
                return;
            }

            initial = [new MealPlace(restaurantId, name)];
        }

        await slack.Views.Open(
            request.TriggerId,
            MealConfirmationModal.Build(date, meals.ChannelId, request.User.Id, state, initial),
            ct);
    }

    /// <summary>
    /// 구성 채널의 메시지에서 온 요청이고 그 날짜가 지금 기록 가능할 때만 상태를 돌려준다.
    /// 다른 채널·사용자 없는 요청은 조용히 무시하고, 기록할 수 없는 날짜는 본인에게만 알린다.
    /// </summary>
    async Task<MealSelectionState?> AllowedStateAsync(BlockActionRequest request, DateOnly date, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.User?.Id) || request.Container?.ChannelId != meals.ChannelId)
            return null;

        var state = await meals.GetStateAsync(date, ct);
        if (state.Allowed)
            return state;

        var today = LunchClock.ToLocalDate(clock.GetUtcNow(), options.CurrentValue.TimeZone);
        await slack.Chat.PostEphemeral(request.User.Id, new SlackNet.WebApi.Message
        {
            Channel = request.Container.ChannelId,
            Text = $"이 메시지는 {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 식사 기록용입니다. " +
                $"{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 날짜의 식사 기록 메시지를 사용해 주세요. " +
                "다른 날짜의 기록은 변경하지 않았습니다.",
        }, ct);
        return null;
    }
}
