using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

public sealed record VoteTally(long RestaurantId, string Name, int Count);

/// <summary>투표 메시지. 후보 수에 따라 버튼과 드롭다운을 갈아 끼운다.</summary>
public static class PollBlocks
{
    /// <summary>이 수를 넘으면 버튼 대신 드롭다운을 쓴다. 가독성과 25개 제한 때문이다.</summary>
    public const int ButtonThreshold = 10;

    /// <summary>드롭다운에서 허용하는 최대 옵션 수. Slack 제한.</summary>
    public const int MaxSelectOptions = 100;

    public static IList<Block> Build(
        long pollId,
        IReadOnlyList<RestaurantInfo> candidates,
        IReadOnlyList<VoteTally> tallies,
        IReadOnlyList<string> abstainers,
        DateTimeOffset closesAt,
        bool closed = false)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍚 오늘 점심 뭐 먹지?") },
        };

        if (candidates.Count == 0)
        {
            blocks.Add(Section("등록된 식당이 없습니다. `/lunch add` 로 먼저 등록해 주세요."));
            // 마감된 뒤에는 눌러도 반영되지 않는 버튼을 남겨두지 않는다(IMPORTANT 3).
            if (!closed)
                blocks.Add(AbstainActions(pollId));
            AddAbstainRoster(blocks, abstainers);
            return blocks;
        }

        if (candidates.Count > MaxSelectOptions)
        {
            blocks.Add(Section($"식당이 {candidates.Count}곳이라 한 메시지에 담을 수 없습니다. 팀에서 더 이상 이용하지 않는 식당을 정리해 주세요. `/lunch list` 로 확인할 수 있습니다."));
            return blocks;
        }

        blocks.Add(Section(TallyText(candidates, tallies)));
        // 기권자 명단도 득표 현황과 마찬가지로 "현재 상태" 정보라 득표 집계 바로 뒤에 둔다.
        AddAbstainRoster(blocks, abstainers);
        // 마감된 뒤에는 후보 버튼/드롭다운과 기권 버튼을 모두 뺀다 — 눌러도 집계에
        // 반영되지 않는데 눌러지는 것처럼 보이면 안 된다(IMPORTANT 3).
        if (!closed)
        {
            blocks.Add(candidates.Count <= ButtonThreshold
                ? ButtonActions(pollId, candidates)
                : SelectActions(pollId, candidates));
            // 후보 버튼/드롭다운과 별도 블록에 둔다 — 후보 수와 무관하게 항상 보여야 한다.
            blocks.Add(AbstainActions(pollId));
        }
        blocks.Add(new ContextBlock
        {
            Elements = { new Markdown(closed
                ? "마감되었습니다"
                : $"{closesAt:HH:mm}에 마감됩니다 · 한 사람당 한 표, 변경 가능") },
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

    /// <summary>"나 오늘 따로 먹어요" 버튼. 후보 버튼/드롭다운과 섞이지 않게 별도 블록으로 둔다.</summary>
    static ActionsBlock AbstainActions(long pollId) => new()
    {
        Elements =
        {
            new Button
            {
                ActionId = ActionIds.Abstain(pollId),
                Text = new PlainText("나 오늘 따로 먹어요"),
            },
        },
    };

    static void AddAbstainRoster(List<Block> blocks, IReadOnlyList<string> abstainers)
    {
        if (abstainers.Count > 0)
            blocks.Add(Section(AbstainText(abstainers)));
    }

    /// <summary>기권자 명단. 알고리즘에는 쓰이지 않는 사회적 정보용 표시다.</summary>
    static string AbstainText(IReadOnlyList<string> abstainers) =>
        $"따로 먹어요 ({abstainers.Count}) — {string.Join(" ", abstainers.Select(id => $"<@{id}>"))}";

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
