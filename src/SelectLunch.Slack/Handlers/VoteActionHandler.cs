using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>버튼과 드롭다운 두 경로의 투표를 모두 받는다.</summary>
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
            && action is StaticSelectAction { SelectedOption.Value: { } value }
            && long.TryParse(value, out var selectedRestaurantId))
        {
            await service.CastVoteAsync(selectPollId, userId, selectedRestaurantId, ct);
            await announcer.RefreshPollAsync(selectPollId, ct);
        }
    }
}
