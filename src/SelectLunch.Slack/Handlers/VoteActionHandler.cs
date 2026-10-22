using System.Globalization;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>버튼·드롭다운·카테고리 드롭다운·랜덤 버튼의 투표를 모두 받는다.</summary>
public sealed class VoteActionHandler(LunchService service, LunchAnnouncer announcer)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var userId = request.User.Id;
        var ct = CancellationToken.None;

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

        if (ActionIds.TryParseVoteRandom(action.ActionId, out var randomPollId))
            await CastRandomAsync(randomPollId, userId, categoryId: null, ct);
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
