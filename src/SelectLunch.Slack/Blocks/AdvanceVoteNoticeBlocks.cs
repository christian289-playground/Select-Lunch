using System.Globalization;
using System.Text;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>개인 식별자 없이 변경 직후의 사전투표 집계를 공개한다.</summary>
public static class AdvanceVoteNoticeBlocks
{
    const int MaxSectionLength = 3000;
    const int MaxBlocks = 50;

    public static IList<Block> Build(AdvanceVoteChange change)
    {
        var blocks = new List<Block>();
        var section = new StringBuilder(256);

        void Flush()
        {
            if (section.Length == 0) return;
            if (blocks.Count == MaxBlocks)
                throw new InvalidOperationException("사전투표 집계가 Slack 메시지의 블록 제한을 초과했습니다.");
            blocks.Add(new SectionBlock { Text = new PlainText(section.ToString()) });
            section.Clear();
        }

        void AddLine(string line)
        {
            // 보통 한 줄을 한 섹션에 유지한다. 비정상적으로 긴 이름만 여러 섹션으로 나눈다.
            if (section.Length > 0 && line.Length + 1 > MaxSectionLength - section.Length)
                Flush();
            if (section.Length > 0) section.Append('\n');
            var offset = 0;
            while (offset < line.Length)
            {
                var length = Math.Min(MaxSectionLength - section.Length, line.Length - offset);
                if (offset + length < line.Length && char.IsHighSurrogate(line[offset + length - 1]))
                    length--;
                if (length == 0) { Flush(); continue; }
                section.Append(line, offset, length);
                offset += length;
                if (offset < line.Length) Flush();
            }
        }

        AddLine(string.Create(CultureInfo.InvariantCulture, $"{change.Snapshot.Date:yyyy-MM-dd} 점심 사전투표"));
        AddLine(change.Kind switch
        {
            AdvanceVoteChangeKind.Selected => $"익명으로 {change.RestaurantName}에 사전투표했어요.",
            AdvanceVoteChangeKind.Abstained => "익명으로 기권했어요.",
            AdvanceVoteChangeKind.Cancelled => "익명으로 사전투표를 취소했어요.",
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        });
        AddLine(string.Create(CultureInfo.InvariantCulture, $"이 변경 직후 집계 ({change.ChangedAt:yyyy-MM-dd HH:mm zzz})"));
        foreach (var tally in change.Snapshot.Tallies)
            AddLine(string.Create(CultureInfo.InvariantCulture, $"{tally.Name} — {tally.Count}표"));
        AddLine(string.Create(CultureInfo.InvariantCulture, $"기권 — {change.Snapshot.Abstentions}표"));
        Flush();
        return blocks;
    }
}
