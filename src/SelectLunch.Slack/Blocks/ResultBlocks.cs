using System.Text;
using SelectLunch.Shared.Options;
using SelectLunch.Shared.Recommendation;
using SlackNet.Blocks;

namespace SelectLunch.Slack.Blocks;

public sealed record PollOutcome(
    VoteTally? Winner,
    IReadOnlyList<VoteTally> Tallies,
    Recommendation? Recommendation);

/// <summary>마감 결과. 추천은 답만이 아니라 계산 과정을 함께 낸다.</summary>
public static class ResultBlocks
{
    public static IList<Block> Build(PollOutcome outcome, RecommendationOptions options)
    {
        var blocks = new List<Block>
        {
            new HeaderBlock { Text = new PlainText("🍽️ 오늘 점심 투표 결과") },
        };

        var sameChoice = outcome.Winner is not null
            && outcome.Recommendation is not null
            && outcome.Winner.RestaurantId == outcome.Recommendation.Pick.RestaurantId;

        if (sameChoice)
        {
            blocks.Add(Section(
                $"🎯 *투표와 추천이 일치했습니다* — *{outcome.Winner!.Name}* ({outcome.Winner.Count}표)"));
        }
        else
        {
            blocks.Add(Section(outcome.Winner is null
                ? "🗳️ *투표 1위* — 투표가 없었습니다."
                : $"🗳️ *투표 1위* — *{outcome.Winner.Name}* ({outcome.Winner.Count}표)"));

            if (outcome.Recommendation is { } recommendation)
            {
                blocks.Add(Section(
                    $"🤖 *앱 추천* — *{recommendation.Pick.Name}* ({recommendation.Winner.CategoryName})"));
            }
        }

        if (outcome.Tallies.Count > 1)
            blocks.Add(Section(TallyDetail(outcome.Tallies)));

        if (outcome.Recommendation is { } rec)
        {
            blocks.Add(new DividerBlock());
            blocks.Add(Section(Rationale(rec, options)));
        }

        return blocks;
    }

    /// <summary>사람이 읽는 선정 근거. 알고리즘이 블랙박스가 되지 않게 한다.</summary>
    public static string Rationale(Recommendation recommendation, RecommendationOptions options)
    {
        var w = recommendation.Winner;
        var sb = new StringBuilder();

        sb.AppendLine($"*{w.CategoryName}이(가) 선정된 이유 — 점수 {w.Score}점 (1위)*");
        sb.AppendLine($"• 마지막 방문 {Format(w.LastEatenOn)} → {w.DaysSince}일 경과  `D = {w.DaysSince}`");
        sb.AppendLine($"• 최근 7일 {w.Count7d}회  `−{options.Weight7d} × {w.Count7d} = −{options.Weight7d * w.Count7d}`");
        sb.AppendLine($"• 최근 30일 {w.Count30d}회  `−{options.Weight30d} × {w.Count30d} = −{options.Weight30d * w.Count30d}`");
        sb.AppendLine($"• `{w.DaysSince} − {options.Weight7d * w.Count7d} − {options.Weight30d * w.Count30d} = {w.Score}점`");

        if (recommendation.Others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("*경쟁 카테고리*");
            foreach (var other in recommendation.Others)
            {
                sb.AppendLine(
                    $"• {other.CategoryName} {other.Score}점 " +
                    $"({other.DaysSince}일 전, 7일내 {other.Count7d}회, 30일내 {other.Count30d}회)");
            }
        }

        sb.AppendLine();
        sb.Append(
            $"{w.CategoryName} 중 *{recommendation.Pick.Name}* — " +
            $"마지막 방문 {Format(recommendation.Pick.LastEatenOn)}으로 가장 오래됨");

        return sb.ToString();
    }

    static string TallyDetail(IReadOnlyList<VoteTally> tallies)
    {
        var lines = tallies
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"• {t.Name} — {t.Count}표");

        return $"*전체 집계*\n{string.Join("\n", lines)}";
    }

    static string Format(DateOnly? date) => date?.ToString("yyyy-MM-dd") ?? "기록 없음";

    static SectionBlock Section(string markdown) => new() { Text = new Markdown(markdown) };
}
