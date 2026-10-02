using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

public sealed record VoteTally(long RestaurantId, string Name, int Count);

/// <summary>
/// 투표 메시지. 후보 수에 따라 버튼과 드롭다운을 갈아 끼운다.
///
/// 메뉴 이미지는 여기에 넣지 않는다 — 외부 URL을 <c>image_url</c>로 실으면 슬랙이
/// 그 URL을 직접 가져가는데, 카카오 CDN 주소는 슬랙 서버에서 받아지지 않아
/// <c>invalid_blocks</c>로 메시지 전체가 거절된다(실측). 메뉴는 서버가 직접 내려받아
/// 스레드에 파일로 올린다(<c>MenuThreadPoster</c>).
/// </summary>
public static class PollBlocks
{
    /// <summary>이 수를 넘으면 버튼 대신 드롭다운을 쓴다. 가독성과 25개 제한 때문이다.</summary>
    public const int ButtonThreshold = 10;

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

        // 예전에는 후보가 100곳을 넘으면 드롭다운을 포기하고 안내문만 보여줬다.
        // 외부 선택(external_select)은 옵션을 메시지에 싣지 않고 block_suggestion으로
        // 그때그때 받아오므로 그 상한이 사라졌다 — 한 번에 돌려주는 옵션 수만
        // RestaurantSearch.MaxOptions로 지키면 된다.
        blocks.Add(Section(TallyText(candidates, tallies)));
        // 기권자 명단도 득표 현황과 마찬가지로 "현재 상태" 정보라 득표 집계 바로 뒤에 둔다.
        AddAbstainRoster(blocks, abstainers);
        // 마감된 뒤에는 후보 버튼/드롭다운과 기권 버튼을 모두 뺀다 — 눌러도 집계에
        // 반영되지 않는데 눌러지는 것처럼 보이면 안 된다(IMPORTANT 3).
        if (!closed)
        {
            blocks.Add(candidates.Count <= ButtonThreshold
                ? ButtonActions(pollId, candidates)
                : SelectActions(pollId));
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

    /// <summary>
    /// 후보 드롭다운. 옵션을 미리 싣지 않는 외부 선택이라, 사용자가 친 글자가
    /// <c>block_suggestion</c>으로 <see cref="Handlers.RestaurantOptionProvider"/>에
    /// 오고 거기서 부분 일치로 거른다. 기본 <c>static_select</c>는 슬랙 클라이언트가
    /// **단어 앞부분만** 매칭해 "옛날경성순대국"을 "순대국"으로 찾을 수 없었다.
    ///
    /// 제안 대상은 이 풀의 <c>PollCandidate</c> 스냅샷이다 — 어느 풀인지는
    /// <c>action_id</c>에 실린 pollId로 구분한다.
    /// </summary>
    static ActionsBlock SelectActions(long pollId)
    {
        var menu = new ExternalSelectMenu
        {
            ActionId = ActionIds.VoteSelect(pollId),
            Placeholder = new PlainText("식당 이름·음식 종류로 검색"),
            // 기본값은 3글자다. 한글은 "순대"처럼 두 글자 검색이 흔하고, 0이면
            // 열자마자 전체 목록이 떠서 기존 static_select 동작과 그대로 이어진다.
            MinQueryLength = 0,
        };

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
