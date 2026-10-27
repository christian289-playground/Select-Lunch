using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SelectLunch.Shared.Data;
using SelectLunch.Shared.Entities;
using SelectLunch.Slack.Blocks;
using SelectLunch.Slack.Options;
using SelectLunch.Slack.Services;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;

namespace SelectLunch.Slack.Handlers;

/// <summary>
/// 마감 결과의 "점수 자세히 보기" 버튼. 누른 사람에게만 읽기 전용 모달을 열고, 모달 안의 쪽 이동 버튼은
/// 같은 모달을 갱신한다. 저장된 점수 내역만 읽는다 — 다시 계산하지 않고 투표·이력·결과 메시지도 바꾸지 않는다.
/// 구성된 채널의 마감된 투표만 보여주며, 그 밖의 요청은 아무것도 알려주지 않는다.
/// </summary>
public sealed class ResultDetailsActionHandler(
    LunchDbContext db,
    ISlackApiClient slack,
    IOptions<SlackOptions> options)
    : IBlockActionHandler
{
    public async Task Handle(BlockActionRequest request)
    {
        if (request.Actions?.FirstOrDefault() is not ButtonAction { ActionId: { } actionId }
            || !ActionIds.TryParseResultDetails(actionId, out var pollId, out var page))
        {
            return;
        }

        var channelId = options.Value.ChannelId;
        var clickedChannel = request.Container?.ChannelId;
        var requestChannel = request.Channel?.Id;
        if (string.IsNullOrEmpty(channelId)
            || !IsNoneOrConfigured(clickedChannel, channelId)
            || !IsNoneOrConfigured(requestChannel, channelId))
        {
            return;
        }

        var ct = CancellationToken.None;

        if (request.View is { } view)
        {
            await ShowPageAsync(view, pollId, page, channelId, ct);
            return;
        }

        // 채널 메시지의 버튼은 항상 0쪽이고, 어느 채널에서 눌렸는지 슬랙이 알려준 요청만 연다.
        if (page == 0
            && !string.IsNullOrEmpty(request.TriggerId)
            && (!string.IsNullOrEmpty(clickedChannel) || !string.IsNullOrEmpty(requestChannel)))
        {
            await OpenAsync(request.TriggerId, pollId, channelId, ct);
        }
    }

    async Task OpenAsync(string triggerId, long pollId, string channelId, CancellationToken ct)
    {
        var modal = await LoadAsync(pollId, channelId, ct) is { } details
            ? ResultDetailsModal.Build(pollId, details.Date, details.Text)
            : ResultDetailsModal.Unavailable();

        await slack.Views.Open(triggerId, modal, ct);
    }

    /// <summary>
    /// 모달 안의 쪽 이동. 우리 모달이고, 이 투표용으로 만든 것이며, 존재하는 쪽일 때만 같은 창을 갱신한다.
    /// </summary>
    async Task ShowPageAsync(ViewInfo view, long pollId, int page, string channelId, CancellationToken ct)
    {
        if (view.CallbackId != ResultDetailsModal.CallbackId
            || string.IsNullOrEmpty(view.Id)
            || !ResultDetailsModal.IsFor(view.PrivateMetadata, pollId))
        {
            return;
        }

        ViewDefinition next;
        if (await LoadAsync(pollId, channelId, ct) is not { } details)
            next = ResultDetailsModal.Unavailable();
        else if (!ResultDetailsModal.TryBuild(pollId, details.Date, details.Text, page, out var pageView))
            return;
        else
            next = pageView;

        try
        {
            await slack.Views.UpdateByViewId(next, view.Id, view.Hash, ct);
        }
        catch (SlackException ex) when (ex.ErrorCode == "hash_conflict")
        {
            // 같은 보기의 중복 클릭이다. 먼저 성공한 갱신을 덮어쓰거나 다시 시도하지 않는다.
        }
    }

    /// <summary>
    /// 구성된 채널에서 마감된 투표의 저장된 점수 내역. 투표가 없거나 열려 있거나 내역이 없거나
    /// 손상됐으면 null — 어떤 경우에도 점수를 새로 계산하지 않는다.
    /// </summary>
    async Task<(DateOnly Date, string Text)?> LoadAsync(long pollId, string channelId, CancellationToken ct)
    {
        var poll = await db.Polls
            .AsNoTracking()
            .Where(p => p.Id == pollId && p.ChannelId == channelId && p.Status == PollStatus.Closed)
            .Select(p => new { p.Date, p.RationaleJson })
            .SingleOrDefaultAsync(ct);
        if (poll is null)
            return null;

        var text = ResultScoreSnapshot.GetDetailsText(poll.RationaleJson);
        return string.IsNullOrWhiteSpace(text) ? null : (poll.Date, text);
    }

    static bool IsNoneOrConfigured(string? candidate, string configuredChannelId) =>
        string.IsNullOrEmpty(candidate) || candidate == configuredChannelId;
}
