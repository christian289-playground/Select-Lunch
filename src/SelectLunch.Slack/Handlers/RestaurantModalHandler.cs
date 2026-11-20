using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 식당 등록 창의 제출. 일반 등록·수정(<see cref="RestaurantModal.CallbackId"/>)은 식당만 저장한다.
/// 식사 기록용 등록(<see cref="RestaurantModal.MealCallbackId"/>)은 새 식당과 선택한 기존 식당의 확정을
/// <see cref="MealConfirmationService.SaveAsync"/> 한 트랜잭션으로 처리한다 — 날짜·채널·사용자·한도·선택이 거절되면
/// 식당·카테고리도 기록도 남지 않고, 후보 갱신·출발 안내 종료·공개 알림도 없다.
/// </summary>
public sealed class RestaurantModalHandler(
    LunchService service,
    MealConfirmationService meals,
    LunchAnnouncer announcer,
    MealConfirmationPublisher publisher)
    : IViewSubmissionHandler
{
    public async Task<ViewSubmissionResponse> Handle(ViewSubmission viewSubmission)
    {
        var ct = CancellationToken.None;
        var draft = RestaurantModal.Parse(viewSubmission);
        var metadata = viewSubmission.View.PrivateMetadata;
        var userId = viewSubmission.User.Id;

        // 식사 기록용 창은 callback으로 가른다. 메타데이터가 틀린 제출은 일반 등록으로 넘기지 않고 거절한다.
        var isMealCallback = viewSubmission.View.CallbackId == RestaurantModal.MealCallbackId;
        var mealDate = default(DateOnly);
        if (isMealCallback)
        {
            if (!MealConfirmationModal.TryDate(metadata, meals.ChannelId, userId, out mealDate))
            {
                return Rejected(
                    "이 창의 기록 대상을 확인할 수 없습니다. " +
                    "창을 닫고 '뭐 드셨어요?' 메시지에서 새 식당 등록을 다시 열어 주세요.");
            }
        }
        else if (!string.IsNullOrEmpty(metadata) && ModalContext.Parse(metadata) is not ModalContext.Edit)
        {
            // 일반 등록의 메타데이터는 비어 있고 수정은 식당 ID뿐이다. 그 밖의 값 — 배포 전에 열린 날짜 전용 기록 창,
            // 일반 callback에 실린 식사 기록용 값, 깨진 값 — 은 채널·사용자·날짜에 묶여 있지 않거나 종류를 알 수 없으므로
            // 날짜와 관계없이 일반 등록으로 넘기지 않고 창을 다시 열게 한다.
            return Rejected(
                "이 창은 더 이상 사용할 수 없습니다. 창을 닫고 다시 열어 주세요. " +
                "식사 기록이면 '뭐 드셨어요?' 메시지에서 새 식당 등록을 선택해 주세요.");
        }

        if (string.IsNullOrWhiteSpace(draft.Name))
            return Rejected("이름을 입력해 주세요.");

        if (RestaurantModal.ValidateCategory(draft, requireCategory: !isMealCallback) is { } categoryError)
            return Rejected(categoryError.Message, categoryError.BlockId);

        if (RestaurantModal.ValidateDepartureTime(viewSubmission) is { } departureError)
            return Rejected(departureError.Message, departureError.BlockId);

        if (RestaurantModal.ValidateEarlyDeparture(viewSubmission) is { } earlyDepartureError)
            return Rejected(earlyDepartureError.Message, earlyDepartureError.BlockId);

        if (!isMealCallback)
            return await RegisterAsync(draft, userId, viewSubmission.User.Name, ct);

        if (!MealConfirmationModal.TryReadPlaces(viewSubmission, out var extras))
        {
            return Rejected(
                "선택한 식당을 읽을 수 없습니다. 창을 닫고 새 식당 등록을 다시 열어 주세요.",
                MealConfirmationModal.PlacesBlockId);
        }

        return await RegisterAndRecordAsync(draft, mealDate, extras, userId, viewSubmission.User.Name, ct);
    }

    /// <summary>일반 등록·수정. 식사 기록과 무관하게 식당만 저장한다.</summary>
    async Task<ViewSubmissionResponse> RegisterAsync(
        RestaurantDraft draft, string userId, string? displayName, CancellationToken ct)
    {
        var restaurant = await service.SaveRestaurantAsync(draft, userId, ct, createdByDisplayName: displayName);

        // 이미 올라온 투표 메시지에도 바로 보이게 한다(드롭다운은 풀의 후보 스냅샷만 검색).
        if (await service.AddCandidateToOpenPollAsync(restaurant.Id, ct) is { } openPollId)
            await announcer.RefreshPollAsync(openPollId, ct);

        var status = restaurant.Status == RestaurantStatus.Active
            ? "추천 대상에 포함됩니다"
            : "카테고리가 비어 있어 추천에서 제외됩니다";
        var by = string.IsNullOrEmpty(restaurant.CreatedByDisplayName)
            ? ""
            : $" (등록자 {restaurant.CreatedByDisplayName})";
        await announcer.PostTextAsync($"🏪 *{restaurant.Name}* 등록 완료{by} — {status}.", ct);

        return ViewSubmissionResponse.Null;
    }

    /// <summary>
    /// 식사 기록용 등록. 새 식당과 <paramref name="extras"/>의 확정을 한 번에 저장하고, 저장에 성공한 뒤에만
    /// 후보 갱신·출발 안내 종료·공개 안내를 한다. 지난 날짜의 최초 기록은 오늘의 투표·안내에 손대지 않는다.
    /// </summary>
    async Task<ViewSubmissionResponse> RegisterAndRecordAsync(
        RestaurantDraft draft, DateOnly date, long[] extras, string userId, string? displayName, CancellationToken ct)
    {
        var saved = await meals.SaveAsync(date, extras, userId, ct, draft, displayName);
        if (saved.Error is { } error)
            return Rejected(error);

        publisher.AfterSave(date, userId, saved);

        return ViewSubmissionResponse.Null;
    }

    static ViewErrorsResponse Rejected(string message, string blockId = RestaurantModal.BlockIds.Name) =>
        new() { Errors = { [blockId] = message } };

    public Task HandleClose(ViewClosed viewClosed) => Task.CompletedTask;
}
