using System.Globalization;
using System.Text;
using SelectLunch.Slack.Services;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

/// <summary>식당명 등 외부 입력을 마크업 없이 Slack의 블록·섹션 길이 제한 안에서 표시한다.</summary>
internal readonly struct PlainTextSections(IList<Block> blocks, int maxBlocks)
{
    const int MaxSectionLength = 3000;
    readonly StringBuilder section = new(256);

    public void AddTallies(AdvanceVoteSnapshot snapshot)
    {
        foreach (var tally in snapshot.Tallies)
            AddLine(string.Create(CultureInfo.InvariantCulture, $"{tally.Name} — {tally.Count}표"));
        AddLine(string.Create(CultureInfo.InvariantCulture, $"기권 — {snapshot.Abstentions}표"));
    }

    public void AddLine(string line)
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

    public void Flush()
    {
        if (section.Length == 0) return;
        if (blocks.Count == maxBlocks)
            throw new InvalidOperationException("표시할 내용이 Slack 메시지의 블록 제한을 초과했습니다.");
        blocks.Add(new SectionBlock { Text = new PlainText(section.ToString()) });
        section.Clear();
    }
}
