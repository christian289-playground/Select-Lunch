using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

public sealed class RestaurantModalHandler(LunchService service, LunchAnnouncer announcer)
    : IViewSubmissionHandler
{
    public async Task<ViewSubmissionResponse> Handle(ViewSubmission viewSubmission)
    {
        var ct = CancellationToken.None;
        var draft = RestaurantModal.Parse(viewSubmission);
        var context = ModalContext.Parse(viewSubmission.View.PrivateMetadata);

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            return new ViewErrorsResponse
            {
                Errors = { [RestaurantModal.BlockIds.Name] = "이름을 입력해 주세요." },
            };
        }

        var userId = viewSubmission.User.Id;
        var restaurant = await service.SaveRestaurantAsync(draft, userId, ct);

        // 기록 흐름에서 열린 모달이면 등록 직후 그날 식사로 기록한다
        if (context.RecordFor is { } date)
        {
            await service.RecordMealAsync(date, restaurant.Id, userId, MealSource.NewRegistration, ct);
            await announcer.PostTextAsync(
                $"✅ <@{userId}> 님이 *{restaurant.Name}* 을(를) 등록하고 오늘 점심으로 기록했습니다.", ct);
        }
        else
        {
            var status = restaurant.Status == RestaurantStatus.Active
                ? "추천 대상에 포함됩니다"
                : "카테고리가 비어 있어 추천에서 제외됩니다";
            await announcer.PostTextAsync($"🏪 *{restaurant.Name}* 등록 완료 — {status}.", ct);
        }

        return ViewSubmissionResponse.Null;
    }

    public Task HandleClose(ViewClosed viewClosed) => Task.CompletedTask;
}
