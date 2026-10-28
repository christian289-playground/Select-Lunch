using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

public sealed class MealActionHandler(
    LunchDbContext db,
    LunchService service,
    LunchAnnouncer announcer,
    ISlackApiClient slack,
    IOptionsMonitor<LunchOptions> options,
    TimeProvider clock,
    EarlyDepartureNoticeTracker earlyDepartureNotices)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var userId = request.User.Id;
        var ct = CancellationToken.None;

        // 신규 등록 — 모달의 private_metadata에 날짜를 실어 등록 직후 기록으로 잇는다
        if (ActionIds.TryParseMealNew(action.ActionId, out var newDate))
        {
            var newToday = LunchClock.ToLocalDate(clock.GetUtcNow(), options.CurrentValue.TimeZone);
            if (newDate != newToday)
            {
                await RejectDateAsync(request, newDate, newToday, ct);
                return;
            }

            var categories = await db.Categories.OrderBy(c => c.Id).ToListAsync(ct);
            await slack.Views.Open(
                request.TriggerId,
                RestaurantModal.Build(categories, null, ModalContext.ForRecord(newDate)),
                ct);
            return;
        }

        if (!ActionIds.TryParseMeal(action.ActionId, out var date, out var restaurantId))
            return;

        // 드롭다운이면 선택 값이 실제 식당이다 (action_id의 0은 자리표시자)
        if (SelectedRestaurant.TryRead(action, out var selected))
            restaurantId = selected;

        if (restaurantId == 0)
            return;

        var today = LunchClock.ToLocalDate(clock.GetUtcNow(), options.CurrentValue.TimeZone);
        if (date != today)
        {
            await RejectDateAsync(request, date, today, ct);
            return;
        }

        await service.RecordMealAsync(date, restaurantId, userId, MealSource.Prompt, ct);
        earlyDepartureNotices.CompleteDay(date);

        // 기록은 이미 커밋됐다 — 오래됐거나 위조된 restaurantId라 이름을 못 찾아도
        // 예외를 던지면 안 되고, 그냥 완료 메시지를 생략한다.
        var name = await db.Restaurants.Where(r => r.Id == restaurantId).Select(r => r.Name).SingleOrDefaultAsync(ct);
        if (name is null)
            return;

        await announcer.PostTextAsync(
            $"✅ <@{userId}> 님이 *{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}* 점심을 *{name}* 으로 기록했습니다.", ct);
    }

    Task RejectDateAsync(BlockActionRequest request, DateOnly requested, DateOnly today, CancellationToken ct) =>
        slack.Chat.PostEphemeral(request.User.Id, new SlackNet.WebApi.Message
        {
            Channel = request.Container.ChannelId,
            Text = $"이 메시지는 {requested.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 식사 기록용입니다. " +
                $"{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} 날짜의 식사 기록 메시지를 사용해 주세요. " +
                "다른 날짜의 기록은 변경하지 않았습니다.",
        }, ct);
}
