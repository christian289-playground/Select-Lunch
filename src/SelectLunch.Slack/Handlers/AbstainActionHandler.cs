using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Services;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// "나 오늘 따로 먹어요" 버튼. 투표가 아니라 기권이다 — CastAbstentionAsync가
/// PollVote.RestaurantId를 null로 기록할 뿐, 집계·동점·추천·기록 어디에도
/// 영향을 주지 않는다. 채널에 명단이 보이는 것이 이 기능의 유일한 효과다.
/// </summary>
public sealed class AbstainActionHandler(LunchService service, LunchAnnouncer announcer)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        var ct = CancellationToken.None;

        if (!ActionIds.TryParseAbstain(action.ActionId, out var pollId))
            return;

        await service.CastAbstentionAsync(pollId, request.User.Id, ct);
        await announcer.RefreshPollAsync(pollId, ct);
    }
}
