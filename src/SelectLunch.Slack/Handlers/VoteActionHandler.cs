using System.Globalization;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 버튼·드롭다운·카테고리 드롭다운·랜덤 버튼·비밀 버튼의 투표를 모두 받는다.
/// 테스트 모드(<c>test:</c> 접두 action_id)는 아무것도 기록하지 않고 결과만 본인에게 보여준다.
/// </summary>
public sealed class VoteActionHandler(LunchService service, LunchAnnouncer announcer)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var userId = request.User.Id;
        var ct = CancellationToken.None;

        if (ActionIds.TryUnwrapTest(action.ActionId, out var innerActionId))
        {
            await HandleTestAsync(request, action, innerActionId, ct);
            return;
        }

        if (ActionIds.TryParseVote(action.ActionId, out var pollId, out var restaurantId))
        {
            await service.CastVoteAsync(pollId, userId, restaurantId, ct);
            await announcer.RefreshPollAsync(pollId, ct);
            return;
        }

        if (ActionIds.TryParseVoteSelect(action.ActionId, out var selectPollId)
            && SelectedRestaurant.TryRead(action, out var selectedRestaurantId))
        {
            await service.CastVoteAsync(selectPollId, userId, selectedRestaurantId, ct);
            await announcer.RefreshPollAsync(selectPollId, ct);
            return;
        }

        if (ActionIds.TryParseVoteCategory(action.ActionId, out var categoryPollId)
            && TryReadCategory(action, out var categoryId))
        {
            await CastRandomAsync(categoryPollId, userId, categoryId, ct);
            return;
        }

        if (ActionIds.TryParseVoteSecret(action.ActionId, out var secretPollId))
        {
            await CastSecretAsync(secretPollId, userId, ct);
            return;
        }

        if (ActionIds.TryParseVoteRandom(action.ActionId, out var randomPollId))
            await CastRandomAsync(randomPollId, userId, categoryId: null, ct);
    }

    async Task CastSecretAsync(long pollId, string userId, CancellationToken ct)
    {
        // 마감됐거나 고를 후보가 없으면 false — 버튼이 살아 있는 옛 메시지를 눌러도 집계는 그대로다.
        if (!await service.CastSecretRandomVoteAsync(pollId, userId, ct))
            return;

        // 서비스는 성공 여부만 돌려준다 — 뽑힌 식당은 본인에게도 알리지 않으므로 안내문에 식당 정보를 싣지 않는다.
        await announcer.RefreshPollAsync(pollId, ct);
        await announcer.PostEphemeralAsync(userId,
            "🤫 비밀 투표를 했어요. 어느 식당인지는 본인에게도 알려주지 않아요. 다른 식당을 고르면 바꿀 수 있어요.", ct);
    }

    async Task CastRandomAsync(long pollId, string userId, long? categoryId, CancellationToken ct)
    {
        // 마감됐거나 고를 후보가 없으면 null — 버튼이 살아 있는 옛 메시지를 눌러도 집계는 그대로다.
        if (await service.CastRandomVoteAsync(pollId, userId, categoryId, ct) is not { } picked)
            return;

        await announcer.RefreshPollAsync(pollId, ct);
        await announcer.PostEphemeralAsync(userId,
            $"🎲 {Scope(categoryId, picked)} *{picked.Name}* 에 투표했어요. 다른 식당을 고르면 바꿀 수 있어요.", ct);
    }

    async Task HandleTestAsync(BlockActionRequest request, BlockAction action, string innerActionId, CancellationToken ct)
    {
        if (request.Container?.ChannelId is not { } channelId)
            return;

        var text = await DescribeTestAsync(action, innerActionId, ct);
        if (text is null)
            return;

        await announcer.PostEphemeralAsync(request.User.Id,
            $"🧪 [테스트] {text}\n_실제 투표·집계에는 반영되지 않았습니다._", ct, channelId);
    }

    async Task<string?> DescribeTestAsync(BlockAction action, string innerActionId, CancellationToken ct)
    {
        if (ActionIds.TryParseVote(innerActionId, out _, out var restaurantId))
            return await DescribeChosenAsync(restaurantId, ct);

        if (ActionIds.TryParseVoteSelect(innerActionId, out _))
            return SelectedRestaurant.TryRead(action, out var selectedId)
                ? await DescribeChosenAsync(selectedId, ct)
                : null;

        if (ActionIds.TryParseVoteCategory(innerActionId, out _))
            return TryReadCategory(action, out var categoryId)
                ? await DescribeRandomAsync(categoryId, ct)
                : null;

        if (ActionIds.TryParseVoteRandom(innerActionId, out _))
            return await DescribeRandomAsync(categoryId: null, ct);

        if (ActionIds.TryParseVoteSecret(innerActionId, out _))
            return "🤫 비밀 투표 시뮬레이션입니다. 실제 투표라면 후보 중 한 곳에 투표하되, 어느 식당인지는 본인에게도 알려주지 않아요.";

        return ActionIds.TryParseAbstain(innerActionId, out _) ? "‘나 오늘 따로 먹어요’ 를 눌렀습니다." : null;
    }

    async Task<string> DescribeChosenAsync(long restaurantId, CancellationToken ct) =>
        await service.FindRestaurantAsync(restaurantId, ct) is { } chosen
            ? $"*{chosen.Name}* ({chosen.CategoryName}) 에 투표합니다."
            : "선택한 식당을 찾을 수 없습니다.";

    async Task<string> DescribeRandomAsync(long? categoryId, CancellationToken ct) =>
        await service.PickRandomActiveAsync(categoryId, ct) is { } picked
            ? $"🎲 {Scope(categoryId, picked)} *{picked.Name}* 에 투표합니다."
            : "고를 수 있는 식당이 없습니다.";

    /// <summary>"전체 중" / "한식 중" — 카테고리를 골랐을 때만 카테고리 이름을 보여준다.</summary>
    static string Scope(long? categoryId, PickedRestaurant picked) =>
        categoryId is null ? "전체 중 랜덤으로" : $"*{picked.CategoryName}* 중 랜덤으로";

    static bool TryReadCategory(BlockAction action, out long categoryId)
    {
        categoryId = 0;
        return action is StaticSelectAction { SelectedOption.Value: { } value }
            && long.TryParse(value, CultureInfo.InvariantCulture, out categoryId);
    }
}
