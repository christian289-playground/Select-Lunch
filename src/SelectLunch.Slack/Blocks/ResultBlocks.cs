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

        sb.AppendLine($"*{w.CategoryName}{IgaParticle(w.CategoryName)} 선정된 이유 — 점수 {FormatScore(w.Score)}점 (1위)*");
        sb.AppendLine($"• 마지막 방문 {Format(w.LastEatenOn)} → {w.DaysSince}일 경과  `D = {w.DaysSince}`");

        // Derive penalty from the score itself to ensure arithmetic is always self-consistent
        var penalty = w.DaysSince - w.Score;

        // Only show per-term breakdown if weights match what produced the score
        var term7d = options.Weight7d * w.Count7d;
        var term30d = options.Weight30d * w.Count30d;

        if (term7d + term30d == penalty)
        {
            // Weights match; safe to show detailed terms
            sb.AppendLine($"• 최근 7일 {w.Count7d}회  `−{options.Weight7d} × {w.Count7d} = {FormatPenalty(term7d)}`");
            sb.AppendLine($"• 최근 30일 {w.Count30d}회  `−{options.Weight30d} × {w.Count30d} = {FormatPenalty(term30d)}`");
            sb.AppendLine($"• `{w.DaysSince} − {term7d} − {term30d} = {FormatScore(w.Score)}점`");
        }
        else
        {
            // Weights don't match; show aggregate penalty only
            sb.AppendLine($"• 최근 식사 차감  `{FormatPenalty(penalty)}`");
            sb.AppendLine($"• `{w.DaysSince} − {penalty} = {FormatScore(w.Score)}점`");
        }

        if (recommendation.Others.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("*경쟁 카테고리*");
            foreach (var other in recommendation.Others)
            {
                sb.AppendLine(
                    $"• {other.CategoryName} {FormatScore(other.Score)}점 " +
                    $"({other.DaysSince}일 전, 7일내 {other.Count7d}회, 30일내 {other.Count30d}회)");
            }
        }

        sb.AppendLine();
        var pickLastEaten = Format(recommendation.Pick.LastEatenOn);
        sb.Append(
            $"{w.CategoryName} 중 *{recommendation.Pick.Name}* — " +
            $"마지막 방문 {pickLastEaten}{Particle(pickLastEaten)} 가장 오래됨");

        return sb.ToString();
    }

    /// <summary>0이 아니면 마이너스 기호와 함께, 0이면 "0"만 출력.</summary>
    static string FormatPenalty(int amount) => amount == 0 ? "0" : $"−{amount}";

    /// <summary>음수 점수를 Unicode 마이너스로 포맷. 음수면 −x 형태, 양수/0이면 그대로.</summary>
    static string FormatScore(int score) => score < 0 ? $"−{-score}" : score.ToString();

    /// <summary>"이" 또는 "가"를 선택. 최종 글자의 받침 여부로 판단.</summary>
    static string IgaParticle(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "이";

        var last = text[^1];

        // 한글 문자 (U+AC00~U+D7A3)
        if (last >= 0xAC00 && last <= 0xD7A3)
        {
            var code = last - 0xAC00;
            var jongseong = code % 28; // 0=받침 없음
            return jongseong != 0 ? "이" : "가";
        }

        return "이";
    }

    /// <summary>"으로" 또는 "로"를 선택. 마지막 글자의 읽음에 따라 결정.</summary>
    static string Particle(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "으로";

        var last = text[^1];

        // 마지막이 숫자면 그 숫자의 읽음에 따라 결정
        if (char.IsDigit(last))
        {
            return last switch
            {
                // 1(일), 7(칠), 8(팔) ㄹ로 끝남 → 로
                '1' or '7' or '8' => "로",
                // 2(이), 4(사), 5(오), 9(구) 모음 → 로
                '2' or '4' or '5' or '9' => "로",
                // 0(영), 3(삼), 6(육) 자음 → 으로
                '0' or '3' or '6' => "으로",
                _ => "으로"
            };
        }

        // 한글 문자의 받침으로 판단 (모음 또는 ㄹ → 로, 기타 자음 → 으로)
        if (last >= 0xAC00 && last <= 0xD7A3)
        {
            var code = last - 0xAC00;
            var jongseong = code % 28; // 0=받침 없음, 8=ㄹ, 기타=다른 자음

            if (jongseong == 0 || jongseong == 8)
                return "로"; // 받침 없음 (모음) 또는 ㄹ → 로
            else
                return "으로"; // 기타 받침 → 으로
        }

        return "으로";
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
