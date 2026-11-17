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

/// <summary>조작 화면은 본인 전용이며 실제 변경만 익명으로 공개한다. 본투표에는 영향을 주지 않는다.</summary>
public sealed class AdvanceVoteActionHandler(
    LunchDbContext db,
    AdvanceVoteService service,
    ISlackApiClient slack,
    IOptionsMonitor<LunchOptions> options,
    IOptions<SlackOptions> slackOptions,
    AdvanceVotePoster poster) : IBlockActionHandler
{
    public async Task<SlashCommandResponse> OpenAsync(SlashCommand command, CancellationToken ct)
    {
        if (command.ChannelId != slackOptions.Value.ChannelId)
            return Ephemeral(new Message { Text = $"사전투표는 <#{slackOptions.Value.ChannelId}> 채널에서 `/lunchpre`로 진행해 주세요." });

        var date = await service.GetTargetDateAsync(ct);
        var status = await service.GetStatusAsync(date, ct);
        var message = status is AdvanceVoteStatus.Ready or AdvanceVoteStatus.VotingStarted
            ? await SelectionAsync(date, command.UserId, status == AdvanceVoteStatus.VotingStarted, ct)
            : await UnavailableAsync(status, date, ct);
        return Ephemeral(message);
    }

    public async Task Handle(BlockActionRequest request)
    {
        var channel = request.Container?.ChannelId ?? request.Channel?.Id;
        var userId = request.User?.Id;
        if (channel != slackOptions.Value.ChannelId || string.IsNullOrEmpty(userId))
            return;

        var action = request.Action;
        var actionId = action.ActionId ?? "";
        var ct = CancellationToken.None;
        DateOnly date;
        string owner;
        int expectedChangeCount;
        long? restaurantId = null;
        var cancel = false;

        if (ActionIds.TryParseAdvanceSelect(actionId, out date, out owner, out expectedChangeCount))
        {
            if (!SelectedRestaurant.TryRead(action, out var selected))
                return;
            restaurantId = selected;
        }
        else if (action is ButtonAction && ActionIds.TryParseAdvanceAbstain(actionId, out date, out owner, out expectedChangeCount))
        {
            // null 식당은 기권이며 취소 상태와 구별한다.
        }
        else if (action is ButtonAction && ActionIds.TryParseAdvanceCancel(actionId, out date, out owner, out expectedChangeCount))
        {
            cancel = true;
        }
        else
        {
            if (actionId.StartsWith("pre_", StringComparison.Ordinal))
                await slack.Chat.PostEphemeral(userId, new Message
                {
                    Channel = channel,
                    Text = "이 사전투표 화면은 오래되었거나 유효하지 않습니다. `/lunchpre`로 다시 열어 주세요.",
                }, ct);
            return;
        }

        if (userId != owner)
            return;

        var result = cancel
            ? await service.CancelAsync(date, owner, expectedChangeCount, ct)
            : await service.CastAsync(date, owner, restaurantId, expectedChangeCount, ct);

        // 커밋된 실제 변경만 한 번 공개한다. 실패 시 환급하면 응답을 잃은 성공 전송이
        // 한도를 넘어 재발송될 수 있으므로 표·횟수는 유지하고 개인 화면에 실패를 알린다.
        var publishFailed = result.Change is { } change && !await poster.PostAsync(change, ct);
        Message message;
        if (result.Status is AdvanceVoteStatus.Saved or AdvanceVoteStatus.Cancelled
            or AdvanceVoteStatus.Unchanged or AdvanceVoteStatus.LimitReached or AdvanceVoteStatus.Conflict)
        {
            var gate = await service.GetStatusAsync(date, ct);
            message = gate is AdvanceVoteStatus.Ready or AdvanceVoteStatus.VotingStarted
                ? await SelectionAsync(date, owner, gate == AdvanceVoteStatus.VotingStarted, ct,
                    MutationNotice(result.Status))
                : await UnavailableAsync(gate, date, ct);
        }
        else
        {
            message = await UnavailableAsync(result.Status, date, ct);
        }
        if (publishFailed)
        {
            const string failure = "선택은 저장했지만 공개 안내 전송에 실패했습니다. 중복 방지를 위해 재전송하지 않습니다.";
            message.Text = failure + "\n" + message.Text;
            if (message.Blocks is { Count: > 0 })
                message.Blocks.Insert(0, new SectionBlock { Text = new PlainText(failure) });
        }
        message.Channel = channel;
        await slack.Chat.PostEphemeral(owner, message, ct);
    }

    async Task<Message> SelectionAsync(
        DateOnly date, string userId, bool votingStarted, CancellationToken ct, string? notice = null)
    {
        var blocks = AdvanceVoteBlocks.Build(date, options.CurrentValue.VoteOpenAt, userId,
            await service.FindAsync(date, userId, ct), votingStarted);
        if (notice is not null)
            blocks.Insert(1, new SectionBlock { Text = new PlainText(notice) });
        return new Message { Text = $"{date:yyyy-MM-dd} 점심 사전투표", Blocks = blocks };
    }

    static string? MutationNotice(AdvanceVoteStatus status) => status switch
    {
        AdvanceVoteStatus.Unchanged => "이미 같은 선택입니다. 횟수를 차감하거나 공개 메시지를 보내지 않았습니다.",
        AdvanceVoteStatus.LimitReached => "이번 점심 사전투표 3회를 모두 사용했습니다. 점심 확정 후 다음 회차에 다시 참여할 수 있습니다.",
        AdvanceVoteStatus.Conflict => "다른 요청이 먼저 반영됐습니다. 아래 최신 선택을 확인한 뒤 다시 골라 주세요.",
        _ => null,
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
            : "본투표 진행 중에는 사전투표를 조회만 할 수 있습니다. 점심이 확정되면 다음 회차의 사전투표가 열립니다.";
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
