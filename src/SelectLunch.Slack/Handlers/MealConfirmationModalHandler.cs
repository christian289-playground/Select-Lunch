using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 식사 확정 창(<see cref="MealConfirmationModal.CallbackId"/>)에서 기존 식당 목록을 제출한다.
/// 창을 연 날짜·채널·사용자를 메타데이터와 대조하고, 창에 담긴 선택만 <see cref="MealConfirmationService.SaveAsync"/>에
/// 넘긴다. 거절은 제출한 사람의 창에만 오류로 보이고, 성공일 때만 채널에 저장된 전체 식당을 알린다.
/// 창을 닫는 것은 아무것도 하지 않는다.
/// </summary>
public sealed class MealConfirmationModalHandler(
    MealConfirmationService meals,
    MealConfirmationPublisher publisher)
    : IViewSubmissionHandler
{
    public async Task<ViewSubmissionResponse> Handle(ViewSubmission viewSubmission)
    {
        var ct = CancellationToken.None;
        var userId = viewSubmission.User?.Id;

        if (string.IsNullOrEmpty(userId) || viewSubmission.View?.CallbackId != MealConfirmationModal.CallbackId
            || !MealConfirmationModal.TryDate(viewSubmission.View.PrivateMetadata, meals.ChannelId, userId, out var date))
        {
            return Rejected("이 확정 창은 사용할 수 없습니다. 창을 닫고 식사 기록 메시지에서 다시 열어 주세요.");
        }

        if (!MealConfirmationModal.TryReadPlaces(viewSubmission, out var ids))
            return Rejected("선택한 식당을 읽지 못했습니다. 창을 닫고 식사 기록 메시지에서 다시 열어 주세요.");

        if (ids.Length == 0)
            return Rejected("식당을 한 곳 이상 선택해 주세요.");

        var saved = await meals.SaveAsync(date, ids, userId, ct);
        if (saved.Error is { } error)
            return Rejected(error);

        publisher.AfterSave(date, userId, saved);
        return ViewSubmissionResponse.Null;
    }

    public Task HandleClose(ViewClosed viewClosed) => Task.CompletedTask;

    static ViewErrorsResponse Rejected(string message) =>
        new() { Errors = { [MealConfirmationModal.PlacesBlockId] = message } };
}
