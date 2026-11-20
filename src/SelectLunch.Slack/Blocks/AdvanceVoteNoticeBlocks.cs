using System.Globalization;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>개인 식별자 없이 변경 직후의 사전투표 집계를 공개한다.</summary>
public static class AdvanceVoteNoticeBlocks
{
    public static IList<Block> Build(AdvanceVoteChange change)
    {
        var blocks = new List<Block>();
        var sections = new PlainTextSections(blocks, 50);

        sections.AddLine(string.Create(CultureInfo.InvariantCulture, $"{change.Snapshot.Date:yyyy-MM-dd} 점심 사전투표"));
        sections.AddLine(change.Kind switch
        {
            AdvanceVoteChangeKind.Selected => $"익명으로 {change.RestaurantName}에 사전투표했어요.",
            AdvanceVoteChangeKind.Abstained => "익명으로 기권했어요.",
            AdvanceVoteChangeKind.Cancelled => "익명으로 사전투표를 취소했어요.",
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        });
        sections.AddLine(string.Create(CultureInfo.InvariantCulture, $"이 변경 직후 집계 ({change.ChangedAt:yyyy-MM-dd HH:mm zzz})"));
        sections.AddTallies(change.Snapshot);
        sections.Flush();
        return blocks;
    }
}
