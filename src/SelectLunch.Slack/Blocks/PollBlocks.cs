using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

public sealed record VoteTally(long RestaurantId, string Name, int Count);

/// <summary>투표 메시지. 후보 수에 따라 버튼과 드롭다운을 갈아 끼운다.</summary>
public static class PollBlocks
{
    /// <summary>이 수를 넘으면 버튼 대신 드롭다운을 쓴다. 가독성과 25개 제한 때문이다.</summary>
    public const int ButtonThreshold = 10;

    public static IList<Block> Build(
        long pollId,
        IReadOnlyList<RestaurantInfo> candidates,
        IReadOnlyList<VoteTally> tallies,
        DateTimeOffset closesAt)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍚 오늘 점심 뭐 먹지?") },
        };

        if (candidates.Count == 0)
        {
            blocks.Add(Section("등록된 식당이 없습니다. `/lunch add` 로 먼저 등록해 주세요."));
            return blocks;
        }

        blocks.Add(Section(TallyText(candidates, tallies)));
        blocks.Add(candidates.Count <= ButtonThreshold
            ? ButtonActions(pollId, candidates)
            : SelectActions(pollId, candidates));
        blocks.Add(new ContextBlock
        {
            Elements = { new Markdown($"{closesAt:HH:mm}에 마감됩니다 · 한 사람당 한 표, 변경 가능") },
        });

        return blocks;
    }

    static ActionsBlock ButtonActions(long pollId, IReadOnlyList<RestaurantInfo> candidates)
    {
        var actions = new ActionsBlock();

        foreach (var candidate in candidates)
        {
            actions.Elements.Add(new Button
            {
                ActionId = ActionIds.Vote(pollId, candidate.RestaurantId),
                Text = new PlainText(candidate.Name),
                Value = candidate.RestaurantId.ToString(),
            });
        }

        return actions;
    }

    static ActionsBlock SelectActions(long pollId, IReadOnlyList<RestaurantInfo> candidates)
    {
        var menu = new StaticSelectMenu
        {
            ActionId = ActionIds.VoteSelect(pollId),
            Placeholder = new PlainText("식당을 고르세요"),
        };

        foreach (var group in candidates.GroupBy(c => c.CategoryName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var optionGroup = new OptionGroup
            {
                Label = new PlainText(group.Key),
                Options = []
            };

            foreach (var candidate in group.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                optionGroup.Options.Add(new Option
                {
                    Text = new PlainText(candidate.Name),
                    Value = candidate.RestaurantId.ToString(),
                });
            }

            menu.OptionGroups.Add(optionGroup);
        }

        return new ActionsBlock { Elements = { menu } };
    }

    static string TallyText(IReadOnlyList<RestaurantInfo> candidates, IReadOnlyList<VoteTally> tallies)
    {
        if (tallies.Count == 0)
            return $"후보 {candidates.Count}곳 · 아직 투표가 없습니다.";

        var lines = tallies
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"• *{t.Name}* — {t.Count}표");

        return $"후보 {candidates.Count}곳 · 현재 집계\n{string.Join("\n", lines)}";
    }

    static SectionBlock Section(string markdown) => new() { Text = new Markdown(markdown) };
}
