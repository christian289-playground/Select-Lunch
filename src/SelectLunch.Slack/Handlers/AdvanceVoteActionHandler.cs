using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Shared.Options;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using Message = SlackNet.WebApi.Message;

namespace SelectLunch.Slack.Handlers;

/// <summary>명령어와 사전투표 조작에는 본인 전용 응답만 보낸다. 정규 투표를 만들거나 게시하지 않는다.</summary>
public sealed class AdvanceVoteActionHandler(
    LunchDbContext db,
    AdvanceVoteService service,
    ISlackApiClient slack,
    IOptionsMonitor<LunchOptions> options,
    IOptions<SlackOptions> slackOptions) : IBlockActionHandler
{
    public async Task<SlashCommandResponse> OpenAsync(SlashCommand command, CancellationToken ct)
    {
        if (command.ChannelId != slackOptions.Value.ChannelId)
            return Ephemeral(new Message { Text = $"사전투표는 <#{slackOptions.Value.ChannelId}> 채널에서 `/lunchpre`로 진행해 주세요." });

        var currentOptions = options.CurrentValue;
        var date = await service.GetTargetDateAsync(ct);
        var status = await service.GetStatusAsync(date, ct);
        var message = status == AdvanceVoteStatus.Ready
            ? await SelectionAsync(date, command.UserId, currentOptions.VoteOpenAt, ct)
            : await UnavailableAsync(status, date, ct);
        return Ephemeral(message);
    }

    public async Task Handle(BlockActionRequest request)
    {
        var action = request.Action;
        DateOnly date;
        string owner;
        long? restaurantId = null;
        var cancel = false;

        if (ActionIds.TryParseAdvanceSelect(action.ActionId, out date, out owner))
        {
            if (!SelectedRestaurant.TryRead(action, out var selected))
                return;
            restaurantId = selected;
        }
        else if (action is ButtonAction && ActionIds.TryParseAdvanceAbstain(action.ActionId, out date, out owner))
        {
            // null 식당은 기권이며 취소(행 제거)와 다르다.
        }
        else if (action is ButtonAction && ActionIds.TryParseAdvanceCancel(action.ActionId, out date, out owner))
        {
            cancel = true;
        }
        else
        {
            return;
        }

        var channel = request.Container?.ChannelId ?? request.Channel?.Id;
        if (channel != slackOptions.Value.ChannelId || request.User?.Id != owner)
            return;

        var ct = CancellationToken.None;
        var status = cancel
            ? await service.CancelAsync(date, owner, ct)
            : await service.CastAsync(date, owner, restaurantId, ct);
        var message = status is AdvanceVoteStatus.Saved or AdvanceVoteStatus.Cancelled
            ? await SelectionAsync(date, owner, options.CurrentValue.VoteOpenAt, ct)
            : await UnavailableAsync(status, date, ct);
        message.Channel = channel;
        await slack.Chat.PostEphemeral(owner, message, ct);
    }

    async Task<Message> SelectionAsync(DateOnly date, string userId, TimeOnly opensAt, CancellationToken ct) => new()
    {
        Text = $"{date:yyyy-MM-dd} 점심 사전투표",
        Blocks = AdvanceVoteBlocks.Build(date, opensAt, userId, await service.FindAsync(date, userId, ct)),
    };

    async Task<Message> UnavailableAsync(AdvanceVoteStatus status, DateOnly date, CancellationToken ct)
    {
        if (status != AdvanceVoteStatus.VotingStarted)
        {
            return new Message
            {
                Text = status switch
                {
                    AdvanceVoteStatus.NotTargetDate => "현재 사전투표 대상 날짜가 아닙니다. `/lunchpre`로 다음 투표의 사전투표 화면을 다시 열어 주세요.",
                    AdvanceVoteStatus.NonBusinessDay => "이 날짜에는 점심 투표가 없습니다. `/lunchpre`로 다음 투표일을 확인해 주세요.",
                    AdvanceVoteStatus.InvalidRestaurant => "현재 투표할 수 없는 식당입니다. `/lunchpre`로 다시 열어 다른 식당을 선택해 주세요.",
                    _ => throw new ArgumentOutOfRangeException(nameof(status)),
                },
            };
        }

        var poll = await db.Polls.AsNoTracking()
            .SingleOrDefaultAsync(p => p.ChannelId == slackOptions.Value.ChannelId && p.Date == date, ct);
        var text = poll?.Status == PollStatus.Closed
            ? "이 날짜의 정규 투표는 마감되었습니다. `/lunchpre`로 다음 투표에 미리 참여할 수 있습니다."
            : "이 날짜의 사전투표 접수가 끝났습니다. 선택·변경은 채널의 정규 투표에서 진행하고, 다음 투표의 사전투표는 `/lunchpre`로 열어 주세요.";
        if (poll?.MessageTs is { } ts)
        {
            var link = await slack.Chat.GetPermalink(slackOptions.Value.ChannelId, ts, ct);
            text += $"\n<{link.Permalink}|해당 날짜 투표 보기>";
        }
        return new Message { Text = text };
    }

    static SlashCommandResponse Ephemeral(Message message) => new()
    {
        Message = message, ResponseType = ResponseType.Ephemeral,
    };
}
