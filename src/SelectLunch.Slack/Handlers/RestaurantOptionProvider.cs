using Microsoft.Extensions.Options;
using System.Globalization;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Search;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet.Interaction;
using Option = SlackNet.Blocks.Option;
using OptionGroup = SlackNet.Blocks.OptionGroup;
using PlainText = SlackNet.Blocks.PlainText;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 외부 선택 드롭다운의 제안(<c>block_suggestion</c>) 응답. 사용자가 글자를 칠 때마다
/// 불린다 — 슬랙은 **3초 안에** 응답을 요구하므로 조회를 무겁게 만들지 않는다.
///
/// 어느 드롭다운인지는 <c>action_id</c>로 구분한다. 모집단이 서로 다르기 때문이다:
/// 투표는 그 풀의 후보 스냅샷, 식사 기록(기존 선택·확정 창의 다중 선택)은 Active 전체, 수정은 분류 미완성까지 포함한다.
/// 모르는 action_id에는 빈 목록을 돌려준다(예외를 던지면 슬랙에 오류가 보인다).
/// </summary>
public sealed class RestaurantOptionProvider(
    LunchDbContext db, AdvanceVoteService advanceVotes, IOptions<SlackOptions> slackOptions) : IBlockOptionProvider
{
    public async Task<BlockOptionsResponse> GetOptions(BlockOptionsRequest request)
    {
        // SlackNet의 제안 경로에는 취소 토큰이 없다. 조회가 가벼워 문제되지 않는다.
        var ct = CancellationToken.None;

        var pool = ActionIds.TryParseAdvanceSelect(request.ActionId ?? "", out var date, out var userId, out var expectedChangeCount)
            ? await AdvancePoolAsync(request, date, userId, expectedChangeCount, ct)
            : await PoolForAsync(request.ActionId, ct);
        if (pool is null)
            return new BlockOptionsResponse { Options = [] };

        var matches = RestaurantSearch.Filter(pool, request.Value);

        // 확정 창의 다중 선택은 초기 선택(MealPlace의 이름뿐)과 라벨이 정확히 같아야 한다 —
        // 출발 시각 같은 덧붙임 없이 이름만 쓴다. 그 밖의 드롭다운은 기존 라벨을 그대로 쓴다.
        var plainLabels = request.ActionId == MealConfirmationModal.PlacesActionId;

        // 식당이 많아지면 한 줄 목록에서 고르기 어렵다 — 카테고리 제목 아래로 묶는다.
        // 슬랙은 options와 option_groups를 동시에 받지 않으므로 항상 묶음으로만 응답하고,
        // 일치하는 식당이 없으면 기존처럼 빈 options를 돌려준다.
        if (matches.Count == 0)
            return new BlockOptionsResponse { Options = [] };

        return new BlockOptionsResponse
        {
            OptionGroups =
            [
                .. RestaurantSearch.GroupByCategory(matches).Select(group => new OptionGroup
                {
                    Label = new PlainText(RestaurantSearch.Truncate(group.CategoryName, RestaurantSearch.MaxLabelLength)),
                    Options =
                    [
                        .. group.Options.Select(m => new Option
                        {
                            Text = new PlainText(plainLabels
                                ? MealConfirmationModal.PlaceLabel(m.Name)
                                : RestaurantSearch.GroupedLabel(m)),
                            Value = m.RestaurantId.ToString(CultureInfo.InvariantCulture),
                        }),
                    ],
                }),
            ],
        };
    }

    async Task<List<RestaurantOption>?> AdvancePoolAsync(
        BlockOptionsRequest request, DateOnly date, string userId, int expectedChangeCount, CancellationToken ct)
    {
        if (request.User?.Id != userId
            || (request.Container?.ChannelId ?? request.Channel?.Id) != slackOptions.Value.ChannelId
            || await advanceVotes.GetStatusAsync(date, ct) != AdvanceVoteStatus.Ready)
            return null;

        var vote = await advanceVotes.FindAsync(date, userId, ct);
        var changes = vote?.ChangeCount ?? 0;
        return changes == expectedChangeCount && changes < AdvanceVote.MaxChanges
            ? await db.GetActiveRestaurantOptionsAsync(ct)
            : null;
    }

    /// <summary>
    /// 제안 대상 모집단. 모르는 action_id면 <c>null</c>.
    ///
    /// 투표 쪽에서 Active 전체를 쓰면 안 된다 — 투표가 열린 뒤 등록된 식당이
    /// 제안에 끼어들고, 그걸 고르면 후보에 없는 식당에 표가 들어간다.
    /// </summary>
    async Task<List<RestaurantOption>?> PoolForAsync(string? actionId, CancellationToken ct)
    {
        if (actionId is null)
            return null;

        if (actionId == ActionIds.RestaurantEditSelect)
            return await db.GetEditableRestaurantOptionsAsync(ct);

        // 식사 확정 창(기존 식당 선택·새 식당 등록 창의 추가 식당)의 검색. 기록 대상이라 Active 전체다.
        if (actionId == MealConfirmationModal.PlacesActionId)
            return await db.GetActiveRestaurantOptionsAsync(ct);

        // 테스트 모드(`/lunchtest`) 메시지는 실제 풀이 없다 — 지금 올라갈 후보인 Active 전체를 보여준다.
        if (ActionIds.TryUnwrapTest(actionId, out var testInner))
            return ActionIds.TryParseVoteSelect(testInner, out _)
                ? await db.GetActiveRestaurantOptionsAsync(ct)
                : null;

        if (ActionIds.TryParseVoteSelect(actionId, out var pollId))
            return await db.GetPollCandidateOptionsAsync(pollId, ct);

        // 기록 드롭다운의 action_id는 meal:{날짜}:0 — 식당 자리는 자리표시자다.
        if (ActionIds.TryParseMeal(actionId, out _, out _))
            return await db.GetActiveRestaurantOptionsAsync(ct);

        return null;
    }
}
