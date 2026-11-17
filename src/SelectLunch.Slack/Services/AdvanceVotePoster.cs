using SelectLunch.Slack.Blocks;
using SlackNet;
using SlackNet.WebApi;

namespace SelectLunch.Slack.Services;

/// <summary>커밋된 사전투표 변경의 익명 스냅샷을 채널에 한 번 전송한다.</summary>
public sealed class AdvanceVotePoster(ISlackApiClient slack, string channelId, ILogger<AdvanceVotePoster> logger)
{
    public async Task<bool> PostAsync(AdvanceVoteChange change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await slack.Chat.PostMessage(new Message
            {
                Channel = channelId,
                Text = "점심 사전투표 집계가 변경되었어요.",
                Blocks = AdvanceVoteNoticeBlocks.Build(change),
                Parse = ParseMode.None,
                LinkNames = false,
                UnfurlLinks = false,
                UnfurlMedia = false,
            }, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("사전투표 공개 전송 실패. 오류 형식: {ErrorType}", ex.GetType().FullName);
            return false;
        }
    }
}
